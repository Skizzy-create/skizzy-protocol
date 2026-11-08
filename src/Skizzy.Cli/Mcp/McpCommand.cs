using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Skizzy.Cli.Mcp;
using Skizzy.Protocol;
using Skizzy.Protocol.Json;

namespace Skizzy.Cli
{
    /// <summary>
    /// <c>skizzy mcp</c>: stdio MCP server (03 §11). stdout carries only MCP frames; diagnostics go to stderr.
    /// The tool list is static, so Claude Code, Codex and any other client see the same tools with the game closed.
    /// </summary>
    public static class McpCommand
    {
        public static readonly CommandInfo Info = new CommandInfo
        {
            Name = "mcp",
            Summary = "run the MCP bridge on stdio (claude mcp add skizzy -- skizzy mcp)",
            Usage = "skizzy mcp [--toolset core] [--actor <id>] [--role commander|advisor|observer|delegate] [--side <side>] [--actor-token <t>]\n" +
                    "           [--client-name <name>] [--max-text <bytes>] [--ksp <KSP folder>|--endpoint <endpoint.json>] [--token-file <file>]\n" +
                    "           [--print-config]\n" +
                    "  --print-config prints the commands that register this bridge with Claude Code and Codex CLI, then exits.",
            Flags = new[] { "print-config" },
            Run = Run,
        };

        public const string Instructions =
            "skizzy-protocol connects you to Kerbal Space Program (with BDArmory). Call skizzy_status first: it works with the game closed " +
            "and tells you the scene, time and whether the game is paused. Read with ksp_vessels_list and ksp_read (vessel by name or handle " +
            "'v:<id>'), ksp_query for fields across many vessels, ksp_camera_screenshot to see the game. Lists return 10 rows by default " +
            "(max 50) with a cursor for the next page. Errors read 'KIND: reason - hint'; follow the hint. Some actions need the player's " +
            "approval in the game and wait for it. catalog_search finds every other capability; call those with cap_call.";

        private static int Run(CliArgs a)
        {
            if (a.Flag("print-config"))
            {
                PrintConfig();
                return Program.ExitOk;
            }
            var conn = ConnectionOptions.From(a, "bridge", "llm");
            if (conn.Role == null) conn.Role = "commander";
            var opts = new BridgeOptions
            {
                Connection = conn,
                Toolsets = ParseToolsets(a.Opt("toolset", "core")),
                MaxText = Math.Max(1024, a.Int("max-text", 8192)),
            };
            using (var cts = new CancellationTokenSource())
            {
                Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };
                try { RunAsync(opts, Console.OpenStandardInput(), Console.OpenStandardOutput(), cts.Token).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { }
            }
            return Program.ExitOk;
        }

        internal static string[] ParseToolsets(string spec)
        {
            var list = new List<string>();
            foreach (var raw in (spec ?? "core").Split(new[] { ',', '+', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = raw.Trim().ToLowerInvariant();
                switch (t)
                {
                    // Presets (03 §11). Toolsets other than core arrive in later milestones; listing them is harmless.
                    case "commander": list.AddRange(new[] { "core", "tactical", "c2", "comms", "bda", "rec", "sim", "map" }); break;
                    case "delegate": list.AddRange(new[] { "core", "tactical", "c2", "flight", "comms" }); break;
                    case "builder": list.AddRange(new[] { "core", "craft", "bda", "rec", "sim" }); break;
                    default: list.Add(t); break;
                }
            }
            if (!list.Contains("core")) list.Insert(0, "core");
            return list.ToArray();
        }

        /// <summary>Runs the MCP server over the given streams until the client disconnects or <paramref name="ct"/> fires.</summary>
        public static async Task RunAsync(BridgeOptions opts, Stream input, Stream output, CancellationToken ct)
        {
            McpServer server = null;
            opts.McpClientName = () => server?.ClientInfo?.Name;
            using (var bridge = new BridgeCore(opts))
            {
                var tools = new List<Tool>();
                foreach (var t in bridge.Tools) tools.Add(ToMcpTool(t));
                var options = new McpServerOptions
                {
                    ServerInfo = new Implementation { Name = "skizzy", Version = ProtocolInfo.HostVersion },
                    ServerInstructions = Instructions,
                    Capabilities = new ServerCapabilities { Tools = new ToolsCapability { ListChanged = false } },
                    Handlers = new McpServerHandlers
                    {
                        ListToolsHandler = (req, token) => new ValueTask<ListToolsResult>(new ListToolsResult { Tools = tools }),
                        CallToolHandler = async (req, token) =>
                        {
                            var p = req.Params;
                            var args = ToJsonValue(p?.Arguments);
                            var output2 = await bridge.CallAsync(p?.Name, args, token).ConfigureAwait(false);
                            return ToMcpResult(output2);
                        },
                    },
                };
                var transport = new StreamServerTransport(input, output, "skizzy");
                await using (transport.ConfigureAwait(false))
                {
                    server = McpServer.Create(transport, options);
                    await using (server.ConfigureAwait(false))
                    {
                        await server.RunAsync(ct).ConfigureAwait(false);
                    }
                }
            }
        }

        internal static Tool ToMcpTool(ToolSpec t)
        {
            using (var doc = JsonDocument.Parse(t.InputSchema.ToString()))
            {
                return new Tool
                {
                    Name = t.Name,
                    Title = t.Title,
                    Description = t.Description,
                    InputSchema = doc.RootElement.Clone(),
                    Annotations = new ToolAnnotations
                    {
                        Title = t.Title,
                        ReadOnlyHint = t.ReadOnly,
                        DestructiveHint = t.Destructive,
                        IdempotentHint = t.Idempotent,
                        OpenWorldHint = false,
                    },
                };
            }
        }

        internal static JsonValue ToJsonValue(IDictionary<string, JsonElement> args)
        {
            var o = JsonValue.Obj();
            if (args == null) return o;
            foreach (var kv in args) o.Set(kv.Key, JsonValue.Parse(kv.Value.GetRawText()));
            return o;
        }

        internal static CallToolResult ToMcpResult(ToolOutput o)
        {
            var content = new List<ContentBlock>();
            foreach (var b in o.Blocks)
            {
                if (b.Image != null) content.Add(ImageContentBlock.FromBytes(b.Image, b.MimeType ?? "image/png"));
                else if (b.Text != null) content.Add(new TextContentBlock { Text = b.Text });
            }
            return new CallToolResult { Content = content, IsError = o.IsError };
        }

        private static void PrintConfig()
        {
            string exe = Environment.ProcessPath ?? "skizzy";
            bool isDotnetHost = Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
            string dll = Path.Combine(AppContext.BaseDirectory, "skizzy.dll");
            string cmd = isDotnetHost ? "\"" + exe + "\" \"" + dll + "\" mcp" : "\"" + exe + "\" mcp";
            Console.Out.WriteLine("Claude Code:");
            Console.Out.WriteLine("  claude mcp add skizzy -- " + cmd);
            Console.Out.WriteLine();
            Console.Out.WriteLine("Codex CLI (~/.codex/config.toml):");
            Console.Out.WriteLine("  [mcp_servers.skizzy]");
            if (isDotnetHost)
            {
                Console.Out.WriteLine("  command = " + TomlString(exe));
                Console.Out.WriteLine("  args = [" + TomlString(dll) + ", \"mcp\"]");
            }
            else
            {
                Console.Out.WriteLine("  command = " + TomlString(exe));
                Console.Out.WriteLine("  args = [\"mcp\"]");
            }
            Console.Out.WriteLine();
            Console.Out.WriteLine("Any other MCP client: stdio command " + cmd);
        }

        private static string TomlString(string s) { return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""; }
    }
}
