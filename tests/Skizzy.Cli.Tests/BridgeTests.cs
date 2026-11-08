using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Skizzy.Cli;
using Skizzy.Cli.Mcp;
using Skizzy.Host.Core;
using Skizzy.Protocol.Json;
using Skizzy.Sim;
using Xunit;

namespace Skizzy.Cli.Tests
{
    /// <summary>A FakeKsp host in a temp KSP root (endpoint.json + auth.token like the real plugin).</summary>
    public sealed class FakeGame : IDisposable
    {
        public readonly string Root;
        public SimHost Sim;

        public FakeGame(int fleet = 0, string root = null)
        {
            Root = root ?? Path.Combine(Path.GetTempPath(), "skizzy-cli-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Root);
            var world = FakeWorld.CreateDefault();
            if (fleet > 0) world.AddFleet(fleet);
            Sim = new SimHost(Root, world, new HostSettings { PortFirst = 0, PortLast = 0 });
            Sim.Host.GraceS = 0.2;
            Sim.Start();
        }

        public void Dispose()
        {
            Sim?.Dispose();
            try { Directory.Delete(Root, true); } catch (Exception) { }
        }
    }

    public class BridgeTests
    {
        private static BridgeCore Bridge(string kspRoot)
        {
            var o = new BridgeOptions();
            o.Connection.Ksp = kspRoot;
            o.Connection.Role = "commander";
            o.ConnectTimeoutS = 3;
            return new BridgeCore(o);
        }

        private static ToolOutput Call(BridgeCore b, string tool, JsonValue args = null)
        {
            return b.CallAsync(tool, args ?? JsonValue.Obj()).GetAwaiter().GetResult();
        }

        private static readonly string[] RequiredCoreTools =
        {
            // 03 §11: core includes these, so the M0 tool list already reads, logs and screenshots.
            "ksp_read", "ksp_query", "ksp_camera_screenshot", "rec_log_start", "rec_log_stop", "host_kill_switch",
            "catalog_search", "cap_call", "ksp_vessels_list", "skizzy_status", "catalog_describe", "skizzy_wait", "skizzy_cancel",
        };

        [Fact]
        public void Tool_list_is_static_and_identical_with_the_game_closed()
        {
            string nowhere = Path.Combine(Path.GetTempPath(), "skizzy-nogame-" + Guid.NewGuid().ToString("N"));
            List<string> closed;
            using (var b = Bridge(nowhere)) closed = b.Tools.Select(t => t.Name).ToList();
            using (var game = new FakeGame())
            using (var b = Bridge(game.Root))
            {
                Assert.False(Call(b, "skizzy_status").IsError);
                Assert.True(b.Connected);
                Assert.Equal(closed, b.Tools.Select(t => t.Name).ToList());
            }
            foreach (var n in RequiredCoreTools) Assert.Contains(n, closed);
            Assert.Equal(closed.Count, closed.Distinct().Count());
            Assert.DoesNotContain(closed, n => n.StartsWith("host_debug", StringComparison.Ordinal));
        }

        [Fact]
        public void Tool_schemas_are_objects_and_convert_to_mcp_tools()
        {
            using (var b = Bridge(Path.GetTempPath()))
            {
                foreach (var t in b.Tools)
                {
                    Assert.Equal("object", t.InputSchema.GetString("type"));
                    var mcp = McpCommand.ToMcpTool(t);
                    Assert.Equal(t.Name, mcp.Name);
                    Assert.False(string.IsNullOrWhiteSpace(mcp.Description));
                    Assert.True(mcp.Name.Length <= 64 && mcp.Name.All(c => char.IsLetterOrDigit(c) || c == '_'), mcp.Name);
                }
                Assert.True(b.Tools.First(t => t.Name == "ksp_read").ReadOnly);
                Assert.False(b.Tools.First(t => t.Name == "host_kill_switch").ReadOnly);
            }
        }

        [Fact]
        public void Offline_status_explains_and_game_tools_say_unavailable()
        {
            string nowhere = Path.Combine(Path.GetTempPath(), "skizzy-nogame-" + Guid.NewGuid().ToString("N"));
            using (var b = Bridge(nowhere))
            {
                var st = Call(b, "skizzy_status");
                Assert.False(st.IsError);
                Assert.Contains("not connected", st.AllText);
                var r = Call(b, "ksp_read", JsonValue.Obj().Set("vessel", "DDG-1"));
                Assert.True(r.IsError);
                Assert.StartsWith("UNAVAILABLE: game_not_running - ", r.AllText);
                var s = Call(b, "catalog_search", JsonValue.Obj().Set("query", "screenshot"));
                Assert.False(s.IsError);
                Assert.Contains("ksp.camera.screenshot", s.AllText);
                var d = Call(b, "catalog_describe", JsonValue.Obj().Set("name", "ksp.camera.orbit"));
                Assert.Contains("inputSchema", d.AllText);
            }
        }

        [Fact]
        public void Lists_default_to_ten_rows_refuse_more_than_fifty_and_page_with_a_cursor()
        {
            using (var game = new FakeGame(fleet: 20))
            using (var b = Bridge(game.Root))
            {
                var r = Call(b, "ksp_vessels_list");
                Assert.False(r.IsError, r.AllText);
                var lines = r.AllText.Split('\n');
                Assert.Equal(10, lines.Count(l => l.StartsWith("v:", StringComparison.Ordinal) || l.StartsWith("*v:", StringComparison.Ordinal)));
                Assert.Contains("next page: cursor=\"o:10\"", r.AllText);
                Assert.Contains("[FLIGHT ut ", r.AllText);

                var page2 = Call(b, "ksp_vessels_list", JsonValue.Obj().Set("cursor", "o:10"));
                Assert.False(page2.IsError, page2.AllText);
                Assert.DoesNotContain("DDG-1 Kirov", page2.AllText);

                var big = Call(b, "ksp_vessels_list", JsonValue.Obj().Set("limit", 51));
                Assert.True(big.IsError);
                Assert.StartsWith("INVALID_PARAMS: limit_too_large - ", big.AllText);
            }
        }

        [Fact]
        public void Errors_render_as_kind_reason_hint()
        {
            using (var game = new FakeGame())
            using (var b = Bridge(game.Root))
            {
                var r = Call(b, "ksp_read", JsonValue.Obj().Set("vessel", "no-such-ship"));
                Assert.True(r.IsError);
                Assert.Matches(@"^NOT_FOUND: [a-z_]+ - .+", r.AllText);
                var bad = Call(b, "ksp_read", JsonValue.Obj().Set("vessel", "DDG-1 Kirov").Set("bogus", 1));
                Assert.True(bad.IsError);
                Assert.StartsWith("INVALID_PARAMS: schema - ", bad.AllText);
                var unknown = Call(b, "no_such_tool");
                Assert.True(unknown.IsError);
                Assert.StartsWith("INVALID_PARAMS: unknown_tool", unknown.AllText);
            }
        }

        [Fact]
        public void Read_defaults_to_compact_text_and_cap_call_reaches_any_capability()
        {
            using (var game = new FakeGame())
            using (var b = Bridge(game.Root))
            {
                var r = Call(b, "ksp_read", JsonValue.Obj().Set("vessel", "ddg-1 kirov"));
                Assert.False(r.IsError, r.AllText);
                Assert.Contains("DDG-1 Kirov", r.AllText);
                Assert.DoesNotContain("\"meta\"", r.AllText);
                var c = Call(b, "cap_call", JsonValue.Obj().Set("capability", "host.state"));
                Assert.False(c.IsError, c.AllText);
                Assert.Contains("\"scene\":\"FLIGHT\"", c.AllText);
            }
        }

        [Fact]
        public void Screenshots_come_back_as_image_blocks()
        {
            using (var game = new FakeGame())
            using (var b = Bridge(game.Root))
            {
                var r = Call(b, "ksp_camera_screenshot", JsonValue.Obj().Set("width", 320).Set("format", "png"));
                Assert.False(r.IsError, r.AllText);
                var img = r.Blocks.FirstOrDefault(x => x.Image != null);
                Assert.NotNull(img);
                Assert.Equal(0x89, img.Image[0]);
                Assert.Equal((byte)'P', img.Image[1]);
                Assert.Equal("image/png", img.MimeType);
                Assert.DoesNotContain("\"data\"", r.AllText);
                var mcp = McpCommand.ToMcpResult(r);
                Assert.Contains(mcp.Content, cb => cb is ImageContentBlock);
            }
        }

        [Fact]
        public void Long_text_is_cut_at_a_line_boundary_with_a_trailer()
        {
            var lines = new List<string>();
            for (int i = 0; i < 2000; i++) lines.Add("line " + i + " " + new string('x', 20));
            string text = string.Join("\n", lines);
            string cut = Render.CutText(text, 8192);
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(cut) <= 8192);
            Assert.Matches("\n\u2026[0-9]+ more lines, use cursor$", cut);
            string body = cut.Substring(0, cut.LastIndexOf('\n'));
            Assert.EndsWith(new string('x', 20), body);   // ended on a whole line
            Assert.Equal("short", Render.CutText("short", 8192));
        }

        [Fact]
        public void Bridge_reconnects_after_the_game_restarts_on_another_port()
        {
            var game = new FakeGame();
            string root = game.Root;
            using (var b = Bridge(root))
            {
                Assert.False(Call(b, "ksp_vessels_list").IsError);
                int oldPort = game.Sim.Host.Port;
                game.Sim.Dispose();
                game.Sim = null;
                var r = Call(b, "ksp_vessels_list");
                Assert.True(r.IsError);
                Assert.StartsWith("UNAVAILABLE", r.AllText);
                // "KSP restart": a new host in the same KSP folder (same token, new port, new endpoint.json).
                using (var again = new FakeGame(root: root))
                {
                    Assert.NotEqual(oldPort, again.Sim.Host.Port);
                    ToolOutput ok = null;
                    var end = DateTime.UtcNow.AddSeconds(15);
                    while (DateTime.UtcNow < end)
                    {
                        ok = Call(b, "ksp_vessels_list");
                        if (!ok.IsError) break;
                        Thread.Sleep(250);
                    }
                    Assert.False(ok.IsError, ok.AllText);
                    Assert.Contains("DDG-1 Kirov", ok.AllText);
                }
            }
            try { Directory.Delete(root, true); } catch (Exception) { }
        }

        [Fact]
        public void Toolset_presets_always_include_core()
        {
            Assert.Equal(new[] { "core" }, McpCommand.ParseToolsets("core"));
            Assert.Contains("core", McpCommand.ParseToolsets("tactical"));
            Assert.Contains("c2", McpCommand.ParseToolsets("commander"));
        }
    }

    /// <summary>End to end: an MCP client starts `skizzy mcp` as a stdio process, like Claude Code or Codex would.</summary>
    public class StdioBridgeTests
    {
        private static string SkizzyDll()
        {
            string here = Path.Combine(AppContext.BaseDirectory, "skizzy.dll");
            Assert.True(File.Exists(here), "skizzy.dll not next to the tests: " + here);
            return here;
        }

        [Fact]
        public async Task Mcp_client_lists_tools_and_reads_a_vessel_over_stdio()
        {
            using (var game = new FakeGame())
            {
                var transport = new StdioClientTransport(new StdioClientTransportOptions
                {
                    Name = "skizzy",
                    Command = "dotnet",
                    Arguments = new List<string> { SkizzyDll(), "mcp", "--ksp", game.Root, "--client-name", "stdio-test" },
                });
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
                {
                    var client = await McpClient.CreateAsync(transport,
                        new McpClientOptions { ClientInfo = new Implementation { Name = "stdio-test-client", Version = "1.0" } }, cancellationToken: cts.Token);
                    await using (client)
                    {
                        Assert.Equal("skizzy", client.ServerInfo.Name);
                        Assert.Contains("skizzy_status", client.ServerInstructions ?? "");
                        var tools = await client.ListToolsAsync(cancellationToken: cts.Token);
                        Assert.Contains(tools, t => t.Name == "ksp_read");
                        Assert.Contains(tools, t => t.Name == "cap_call");
                        var res = await client.CallToolAsync("ksp_read", new Dictionary<string, object> { ["vessel"] = "DDG-1 Kirov" }, cancellationToken: cts.Token);
                        Assert.True(res.IsError != true, string.Join("\n", res.Content.OfType<TextContentBlock>().Select(x => x.Text)));
                        var text = string.Join("\n", res.Content.OfType<TextContentBlock>().Select(x => x.Text));
                        Assert.Contains("DDG-1 Kirov", text);
                        var sessions = new List<Session>();
                        game.Sim.Host.SnapshotSessions(sessions);
                        Assert.Contains(sessions, s => s.ClientKind == "bridge" && s.ActorKind == "llm" && s.ClientName == "stdio-test" && s.McpClient == "stdio-test-client");
                    }
                }
            }
        }
    }
}
