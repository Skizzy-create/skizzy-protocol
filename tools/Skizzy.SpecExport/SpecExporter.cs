using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Skizzy.Protocol;
using Skizzy.Protocol.Catalog;
using Skizzy.Protocol.Errors;
using Skizzy.Protocol.Json;
using Skizzy.Protocol.Permissions;

namespace Skizzy.SpecExport
{
    /// <summary>Generates the spec/schemas files from the code-defined catalog. Output is deterministic.</summary>
    public static class SpecExporter
    {
        public const string SchemaUri = "https://json-schema.org/draft/2020-12/schema";

        /// <summary>Relative path (forward slashes) to file text (LF, trailing newline).</summary>
        public static IDictionary<string, string> Generate()
        {
            var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var defs = new List<CapabilityDef>(Builtins.Registry.All);
            defs.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

            var caps = JsonValue.Arr();
            foreach (var d in defs)
            {
                caps.Add(d.ToDescriptor());
                files["capabilities/" + d.Name + ".json"] = Text(CapabilityFile(d));
            }
            files["catalog.json"] = Text(JsonValue.Obj()
                .Set("protocol", ProtocolInfo.Name)
                .Set("version", ProtocolInfo.Version)
                .Set("capabilities", caps));
            files["errors.json"] = Text(Errors());
            files["endpoint.schema.json"] = Text(EndpointSchema());
            return files;
        }

        public static void Write(string specRoot)
        {
            var files = Generate();
            string capDir = Path.Combine(specRoot, "capabilities");
            if (Directory.Exists(capDir))
            {
                foreach (var f in Directory.GetFiles(capDir, "*.json"))
                    if (!files.ContainsKey("capabilities/" + Path.GetFileName(f))) File.Delete(f);
            }
            foreach (var kv in files)
            {
                string full = Path.Combine(specRoot, kv.Key.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full));
                File.WriteAllBytes(full, new UTF8Encoding(false).GetBytes(kv.Value));
            }
        }

        /// <summary>Relative paths whose committed content differs from the generated content (including stale files).</summary>
        public static List<string> Diff(string specRoot)
        {
            var files = Generate();
            var diffs = new List<string>();
            foreach (var kv in files)
            {
                string full = Path.Combine(specRoot, kv.Key.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full)) { diffs.Add(kv.Key + " (missing)"); continue; }
                string have = File.ReadAllText(full).Replace("\r\n", "\n");
                if (have != kv.Value) diffs.Add(kv.Key);
            }
            string capDir = Path.Combine(specRoot, "capabilities");
            if (Directory.Exists(capDir))
            {
                foreach (var f in Directory.GetFiles(capDir, "*.json"))
                    if (!files.ContainsKey("capabilities/" + Path.GetFileName(f))) diffs.Add("capabilities/" + Path.GetFileName(f) + " (stale)");
            }
            diffs.Sort(StringComparer.Ordinal);
            return diffs;
        }

        private static string Text(JsonValue v)
        {
            return v.ToPrettyString().Replace("\r\n", "\n").TrimEnd('\n') + "\n";
        }

        private static JsonValue CapabilityFile(CapabilityDef d)
        {
            var o = JsonValue.Obj()
                .Set("$schema", SchemaUri)
                .Set("$id", "urn:skizzy:capability:" + d.Name + "@" + d.Version)
                .Set("title", d.Title ?? d.Name)
                .Set("description", d.Description)
                .Set("x-skz-safety", SafetyNames.Name(d.Safety))
                .Set("x-skz-scenes", JsonValue.ArrOf(d.Scenes))
                .Set("x-skz-affinity", CapabilityDef.AffinityName(d.Affinity))
                .Set("x-skz-mcpTool", d.ToolName)
                .Set("input", d.InputSchema ?? JsonValue.Obj().Set("type", "object"));
            if (d.OutputSchema != null) o.Set("output", d.OutputSchema);
            return o;
        }

        private static JsonValue Errors()
        {
            var arr = JsonValue.Arr();
            foreach (ErrorKind k in Enum.GetValues(typeof(ErrorKind)))
            {
                arr.Add(JsonValue.Obj()
                    .Set("name", ErrorKinds.Name(k))
                    .Set("code", ErrorKinds.Code(k))
                    .Set("retryable", ErrorKinds.Retryable(k))
                    .Set("defaultHint", ErrorKinds.DefaultHint(k)));
            }
            return JsonValue.Obj()
                .Set("protocol", ProtocolInfo.Name)
                .Set("version", ProtocolInfo.Version)
                .Set("errors", arr)
                .Set("webSocketCloseCode", JsonValue.Obj()
                    .Set("rule", "4000 + (-code - 32000)")
                    .Set("description", "WebSocket close code for a fatal protocol error: 4000 plus the distance of the JSON-RPC error code from -32000, e.g. -32001 closes with 4001."));
        }

        private static JsonValue Prop(string type, string description)
        {
            return JsonValue.Obj().Set("type", type).Set("description", description);
        }

        private static JsonValue EndpointSchema()
        {
            var props = JsonValue.Obj()
                .Set("protocol", JsonValue.Obj().Set("const", ProtocolInfo.Name))
                .Set("version", Prop("string", "Protocol version the host speaks."))
                .Set("hostVersion", Prop("string", "Version of the Skizzy host plugin."))
                .Set("url", Prop("string", "Full WebSocket URL, e.g. ws://127.0.0.1:47549/skizzy."))
                .Set("host", Prop("string", "Loopback address the host listens on."))
                .Set("port", JsonValue.Obj().Set("type", "integer").Set("minimum", 1).Set("maximum", 65535).Set("description", "TCP port the host listens on."))
                .Set("path", Prop("string", "WebSocket path."))
                .Set("subprotocol", JsonValue.Obj().Set("const", ProtocolInfo.SubProtocol))
                .Set("pid", JsonValue.Obj().Set("type", "integer").Set("description", "Process id of the KSP process (0 if unknown)."))
                .Set("startedUtc", Prop("string", "UTC ISO 8601 time the host started."))
                .Set("tokenFile", Prop("string", "Name of the token file next to endpoint.json; send its content as a Bearer token."));
            return JsonValue.Obj()
                .Set("$schema", SchemaUri)
                .Set("$id", "urn:skizzy:endpoint")
                .Set("title", "endpoint.json")
                .Set("description", "PluginData/endpoint.json, written by the host while it runs so clients can find the port and token.")
                .Set("type", "object")
                .Set("required", JsonValue.ArrOf("protocol", "version", "hostVersion", "url", "host", "port", "path", "subprotocol", "pid", "startedUtc", "tokenFile"))
                .Set("properties", props)
                .Set("additionalProperties", true);
        }
    }
}
