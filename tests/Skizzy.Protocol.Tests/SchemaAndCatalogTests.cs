using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Skizzy.Protocol.Catalog;
using Skizzy.Protocol.Errors;
using Skizzy.Protocol.Json;
using Skizzy.Protocol.Schema;
using Xunit;

namespace Skizzy.Protocol.Tests
{
    public class SchemaAndCatalogTests
    {
        [Fact]
        public void Validator_reports_pointers()
        {
            var schema = S.Obj()
                .Prop("vessel", S.Str().MinLen(1), true)
                .Prop("depth", S.Int().Min(0).Max(2))
                .Prop("format", S.Enum(null, "json", "compact"))
                .Prop("fields", S.Arr(S.Str()).MinItems(1))
                .Closed().Build();
            Assert.Empty(SchemaValidator.Validate(JsonValue.Parse("{\"vessel\":\"active\",\"depth\":1,\"format\":\"json\",\"fields\":[\"a\"]}"), schema));
            var errs = SchemaValidator.Validate(JsonValue.Parse("{\"depth\":3,\"format\":\"xml\",\"fields\":[1],\"bogus\":true}"), schema);
            var ptrs = errs.Select(e => e.Pointer).ToList();
            Assert.Contains("/vessel", ptrs);
            Assert.Contains("/depth", ptrs);
            Assert.Contains("/format", ptrs);
            Assert.Contains("/fields/0", ptrs);
            Assert.Contains("/bogus", ptrs);
        }

        [Fact]
        public void Integer_type_rejects_fractions()
        {
            var s = S.Obj().Prop("n", S.Int()).Build();
            Assert.Single(SchemaValidator.Validate(JsonValue.Parse("{\"n\":1.5}"), s));
            Assert.Empty(SchemaValidator.Validate(JsonValue.Parse("{\"n\":2}"), s));
            Assert.Empty(SchemaValidator.Validate(JsonValue.Parse("{\"n\":2.0}"), s));
        }

        [Fact]
        public void StrOrList_and_oneOf()
        {
            var s = S.Obj().Prop("t", S.StrOrList()).Prop("o", S.OneOf(null, S.Str(), S.Int())).Build();
            Assert.Empty(SchemaValidator.Validate(JsonValue.Parse("{\"t\":\"a\",\"o\":1}"), s));
            Assert.Empty(SchemaValidator.Validate(JsonValue.Parse("{\"t\":[\"a\",\"b\"],\"o\":\"x\"}"), s));
            Assert.NotEmpty(SchemaValidator.Validate(JsonValue.Parse("{\"t\":[1]}"), s));
            Assert.NotEmpty(SchemaValidator.Validate(JsonValue.Parse("{\"o\":true}"), s));
        }

        [Fact]
        public void ApplyDefaults_fills_missing_members()
        {
            var s = S.Obj().Prop("depth", S.Int().Default(0)).Prop("format", S.Str().Default("json"))
                .Prop("inner", S.Obj().Prop("x", S.Num().Default(2.5))).Build();
            var v = SchemaValidator.ApplyDefaults(JsonValue.Parse("{\"format\":\"compact\",\"inner\":{}}"), s);
            Assert.Equal(0, v.GetInt("depth", -1));
            Assert.Equal("compact", v.GetString("format"));
            Assert.Equal(2.5, v.GetObj("inner").GetDouble("x"));
        }

        [Fact]
        public void Builtins_are_consistent()
        {
            var all = Builtins.Registry.All;
            Assert.True(all.Count >= 30, "expected the M0 capability set");
            var nameRx = new Regex("^[a-z][a-z0-9_]*(\\.[a-z0-9_]+)+$");
            var toolRx = new Regex("^[a-zA-Z0-9_-]{1,64}$");
            var tools = new HashSet<string>();
            foreach (var d in all)
            {
                Assert.Matches(nameRx, d.Name);
                Assert.False(string.IsNullOrWhiteSpace(d.Description), d.Name);
                Assert.True(d.Description.Length >= 40, d.Name + " description too short for an LLM");
                Assert.NotNull(d.InputSchema);
                Assert.Equal("object", d.InputSchema.GetString("type"));
                Assert.False(string.IsNullOrEmpty(d.Area), d.Name);
                Assert.Matches(toolRx, d.ToolName);
                Assert.True(tools.Add(d.ToolName), "duplicate tool name " + d.ToolName);
                // Every schema default must validate against its own schema.
                var withDefaults = SchemaValidator.ApplyDefaults(JsonValue.Obj(), d.InputSchema);
                var req = d.InputSchema.GetArr("required");
                if (req == null || req.Count == 0)
                    Assert.Empty(SchemaValidator.Validate(withDefaults, d.InputSchema));
                // MCP defaults must be valid arguments too.
                if (d.McpDefaults != null)
                    for (int i = 0; i < d.McpDefaults.Count; i++)
                        Assert.NotNull(d.InputSchema.GetObj("properties")?.Get(d.McpDefaults.KeyAt(i)));
            }
            Assert.Same(Builtins.Registry.Find("ksp.vessel.get"), Builtins.Registry.Find("ksp.read"));
            Assert.Equal("ksp_read", Builtins.Registry.Find("ksp.read").ToolName);
        }

        [Fact]
        public void Core_toolset_covers_the_M0_tool_list_of_03_section_11()
        {
            var core = Builtins.Registry.All.Where(d => d.Toolsets.Contains("core")).Select(d => d.ToolName).ToList();
            foreach (var t in new[] { "ksp_read", "ksp_query", "ksp_camera_screenshot", "rec_log_start", "rec_log_stop", "host_kill_switch" })
                Assert.Contains(t, core);
        }

        [Fact]
        public void Tool_names_are_truncated_with_hash()
        {
            string longName = "x." + new string('a', 80) + ".b";
            var t = ToolNaming.ToToolName(longName);
            Assert.Equal(64, t.Length);
            Assert.Equal(t, ToolNaming.ToToolName(longName));
            Assert.Equal("ksp_vessels_list", ToolNaming.ToToolName("ksp.vessels.list"));
        }

        [Fact]
        public void Error_kinds_have_unique_codes_and_hints()
        {
            var codes = new HashSet<int>();
            for (int i = 0; i < ErrorKinds.Count; i++)
            {
                var k = (ErrorKind)i;
                Assert.True(codes.Add(ErrorKinds.Code(k)), "duplicate code for " + k);
                Assert.False(string.IsNullOrEmpty(ErrorKinds.DefaultHint(k)));
                Assert.Contains(ErrorKinds.Retryable(k), new[] { "no", "yes", "later" });
                Assert.True(ErrorKinds.TryParse(ErrorKinds.Name(k), out var back) && back == k);
            }
            Assert.Equal(-32027, ErrorKinds.Code(ErrorKind.KillSwitchActive));
            Assert.Equal(-32028, ErrorKinds.Code(ErrorKind.DuplicateName));
        }

        [Fact]
        public void Error_object_shape()
        {
            var e = SkizzyError.NotFound("v:1", "stale");
            var o = e.ToErrorObject();
            Assert.Equal(-32005, o.GetInt("code"));
            var d = o.GetObj("data");
            Assert.Equal("NOT_FOUND", d.GetString("kind"));
            Assert.Equal("stale", d.GetString("reason"));
            Assert.Equal("v:1", d.GetString("handle"));
            Assert.Equal("no", d.GetString("retryable"));
            Assert.False(string.IsNullOrEmpty(d.GetString("hint")));
            var back = SkizzyError.FromErrorObject(o);
            Assert.Equal(ErrorKind.NotFound, back.Kind);
            Assert.Equal("stale", back.Reason);
        }

        [Fact]
        public void Vessel_fields_resolve_aliases()
        {
            Assert.Equal("ctrlState.mainThrottle", VesselFields.Canonical("throttle"));
            Assert.Equal("altitude", VesselFields.Canonical("vessel.altitude"));
            Assert.NotNull(VesselFields.Find("srfSpeed"));
            Assert.Null(VesselFields.Find("floodedDisplay"));
        }
    }
}
