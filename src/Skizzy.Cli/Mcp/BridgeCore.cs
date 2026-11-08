using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Skizzy.Client;
using Skizzy.Protocol;
using Skizzy.Protocol.Catalog;
using Skizzy.Protocol.Errors;
using Skizzy.Protocol.Json;
using Skizzy.Protocol.Permissions;
using Skizzy.Protocol.Util;

namespace Skizzy.Cli.Mcp
{
    /// <summary>A tool the bridge lists. The list is static: it never depends on the game running (03 §11).</summary>
    public sealed class ToolSpec
    {
        public string Name;
        public string Title;
        public string Description;
        public JsonValue InputSchema;
        public bool ReadOnly, Destructive, Idempotent;
        /// <summary>Host capability behind a projected tool; null for bridge-native tools.</summary>
        public CapabilityDef Def;
    }

    public sealed class ToolBlock
    {
        public string Text;
        public byte[] Image;
        public string MimeType;
    }

    public sealed class ToolOutput
    {
        public readonly List<ToolBlock> Blocks = new List<ToolBlock>();
        public bool IsError;

        public static ToolOutput OfText(string text, bool error = false)
        {
            var o = new ToolOutput { IsError = error };
            o.Blocks.Add(new ToolBlock { Text = text });
            return o;
        }

        public string AllText
        {
            get
            {
                var sb = new StringBuilder();
                foreach (var b in Blocks) if (b.Text != null) { if (sb.Length > 0) sb.Append('\n'); sb.Append(b.Text); }
                return sb.ToString();
            }
        }
    }

    public sealed class BridgeOptions
    {
        public ConnectionOptions Connection = new ConnectionOptions { ClientKind = "bridge", ActorKind = "llm", ClientName = "skizzy-bridge" };
        public string[] Toolsets = { Builtins.Core };
        public int MaxText = 8192;
        public int DefaultLimit = 10;
        public int MaxLimit = 50;
        public double ConnectTimeoutS = 5;
        /// <summary>Name of the MCP client (claude-code, codex, ...) once known; sent in hello as client.mcpClient.</summary>
        public Func<string> McpClientName;
    }

    /// <summary>
    /// The MCP bridge without the MCP SDK: projects catalog capabilities into a static tool list, applies the
    /// output caps (default limit 10, max 50 rows, 8 KB text cut at a line boundary), renders errors as
    /// "KIND: reason - hint" and images as image blocks, and connects lazily with automatic reconnect.
    /// </summary>
    public sealed class BridgeCore : IDisposable
    {
        public const string ToolStatus = "skizzy_status", ToolSearch = "catalog_search", ToolDescribe = "catalog_describe",
            ToolCapCall = "cap_call", ToolWait = "skizzy_wait", ToolCancel = "skizzy_cancel";

        private readonly BridgeOptions _o;
        private readonly DefRegistry _registry;
        private readonly List<ToolSpec> _tools = new List<ToolSpec>();
        private readonly Dictionary<string, ToolSpec> _byName = new Dictionary<string, ToolSpec>(StringComparer.Ordinal);
        private readonly SemaphoreSlim _connectLock = new SemaphoreSlim(1, 1);
        private SkizzyClient _client;
        private string _lastConnectError;
        private volatile bool _disposed;

        public BridgeCore(BridgeOptions options, DefRegistry registry = null)
        {
            _o = options ?? new BridgeOptions();
            _registry = registry ?? Builtins.Registry;
            BuildTools();
        }

        public IReadOnlyList<ToolSpec> Tools => _tools;
        public SkizzyClient Client => _client;
        public bool Connected => _client != null && _client.IsConnected;

        // ------------------------------------------------------------------ tool list

        private void BuildTools()
        {
            var sets = new HashSet<string>(_o.Toolsets, StringComparer.OrdinalIgnoreCase);
            foreach (var d in _registry.All)
            {
                bool include = false;
                foreach (var t in d.Toolsets) if (sets.Contains(t)) { include = true; break; }
                if (!include) continue;
                Add(new ToolSpec
                {
                    Name = d.ToolName,
                    Title = d.Title ?? d.Name,
                    Description = Describe(d),
                    InputSchema = d.InputSchema ?? JsonValue.Obj().Set("type", "object"),
                    ReadOnly = d.Safety == Safety.Read,
                    Destructive = d.Safety == Safety.Destructive,
                    Idempotent = d.Idempotent || d.Safety == Safety.Read,
                    Def = d,
                });
            }
            Add(Native(ToolStatus, "Skizzy status",
                "Connection and game state in a few lines: whether KSP is reachable, scene, UT, pause, kill switch, session and role. " +
                "Works with the game closed. Call this first.",
                JsonValue.Obj().Set("type", "object").Set("properties", JsonValue.Obj()).Set("additionalProperties", false), true));
            Add(Native(ToolSearch, "Search capabilities",
                "Search every capability the host offers (more than the listed tools) by words or a glob such as 'ksp.camera.*'. " +
                "Returns name, safety and a one-line summary; call one with cap_call.",
                JsonValue.Obj().Set("type", "object").Set("properties", JsonValue.Obj()
                    .Set("query", JsonValue.Obj().Set("type", "string").Set("description", "words or a glob, e.g. 'log', 'vessel position', 'ksp.camera.*'"))
                    .Set("limit", JsonValue.Obj().Set("type", "integer").Set("minimum", 1).Set("maximum", _o.MaxLimit).Set("default", _o.DefaultLimit)))
                    .Set("additionalProperties", false), true));
            Add(Native(ToolDescribe, "Describe a capability",
                "Full descriptor of one capability: input schema, safety, scenes, and whether this session may call it.",
                JsonValue.Obj().Set("type", "object").Set("properties", JsonValue.Obj()
                    .Set("name", JsonValue.Obj().Set("type", "string").Set("description", "capability name, e.g. ksp.camera.look_at")))
                    .Set("required", JsonValue.ArrOf("name")).Set("additionalProperties", false), true));
            Add(Native(ToolCapCall, "Call a capability",
                "Call any capability by name with its params (see catalog_search / catalog_describe). Permission rules still apply; " +
                "a capability that needs approval waits for the user in the game.",
                JsonValue.Obj().Set("type", "object").Set("properties", JsonValue.Obj()
                    .Set("capability", JsonValue.Obj().Set("type", "string").Set("description", "capability name, e.g. ksp.camera.orbit"))
                    .Set("params", JsonValue.Obj().Set("type", "object").Set("description", "parameters per the capability's input schema")))
                    .Set("required", JsonValue.ArrOf("capability")).Set("additionalProperties", false), false));
            Add(Native(ToolWait, "Wait for an operation",
                "Wait up to timeoutS (max 60) for a long-running operation ('op:N' from a result) and return its state and result.",
                JsonValue.Obj().Set("type", "object").Set("properties", JsonValue.Obj()
                    .Set("op", JsonValue.Obj().Set("type", "string").Set("description", "operation id, e.g. op:3"))
                    .Set("timeoutS", JsonValue.Obj().Set("type", "number").Set("minimum", 0).Set("maximum", 60).Set("default", 20)))
                    .Set("required", JsonValue.ArrOf("op")).Set("additionalProperties", false), true));
            Add(Native(ToolCancel, "Cancel an operation",
                "Request cancellation of a long-running operation ('op:N').",
                JsonValue.Obj().Set("type", "object").Set("properties", JsonValue.Obj()
                    .Set("op", JsonValue.Obj().Set("type", "string").Set("description", "operation id, e.g. op:3")))
                    .Set("required", JsonValue.ArrOf("op")).Set("additionalProperties", false), false));
        }

        private static string Describe(CapabilityDef d)
        {
            string desc = d.Description ?? d.Name;
            string scenes = d.Scenes != null && d.Scenes.Length > 0 && d.Scenes[0] != "ANY" ? " Scenes: " + string.Join(", ", d.Scenes) + "." : "";
            return desc + scenes + " [" + d.Name + ", " + SafetyNames.Name(d.Safety) + "]";
        }

        private static ToolSpec Native(string name, string title, string desc, JsonValue schema, bool readOnly)
        {
            return new ToolSpec { Name = name, Title = title, Description = desc, InputSchema = schema, ReadOnly = readOnly, Idempotent = readOnly };
        }

        private void Add(ToolSpec t)
        {
            if (_byName.ContainsKey(t.Name)) return;
            _byName[t.Name] = t;
            _tools.Add(t);
        }

        // ------------------------------------------------------------------ connection

        /// <summary>Connects on first use; with auto-reconnect a dropped game comes back without restarting the bridge.</summary>
        public async Task<SkizzyClient> EnsureConnectedAsync(CancellationToken ct)
        {
            var c = _client;
            if (c != null && c.IsConnected) return c;
            await _connectLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                c = _client;
                if (c != null && c.IsConnected) return c;
                if (c != null)
                {
                    // Auto-reconnect may be in flight (KSP restarting): give it a moment before starting over.
                    if (await c.WaitConnectedAsync(TimeSpan.FromSeconds(1.5)).ConfigureAwait(false)) return c;
                    c.Dispose();
                    _client = null;
                }
                var conn = _o.Connection;
                if (_o.McpClientName != null) conn.McpClient = _o.McpClientName();
                var fresh = new SkizzyClient { AutoReconnect = true };
                var connect = fresh.ConnectAsync(() => conn.Discover(), conn.Hello(), ct);
                var done = await Task.WhenAny(connect, Task.Delay(TimeSpan.FromSeconds(_o.ConnectTimeoutS), ct)).ConfigureAwait(false);
                if (done != connect)
                {
                    fresh.Dispose();
                    _lastConnectError = "no answer within " + _o.ConnectTimeoutS.ToString(CultureInfo.InvariantCulture) + " s";
                    throw new SkizzyClientException("CONNECT_TIMEOUT", "the game did not answer within " + _o.ConnectTimeoutS.ToString(CultureInfo.InvariantCulture) + " s",
                        "KSP may still be loading. Retry in a few seconds; skizzy_status shows the state.");
                }
                try { await connect.ConfigureAwait(false); }
                catch (Exception e)
                {
                    fresh.Dispose();
                    _lastConnectError = e.Message;
                    throw;
                }
                _lastConnectError = null;
                _client = fresh;
                return fresh;
            }
            finally { _connectLock.Release(); }
        }

        // ------------------------------------------------------------------ calls

        public async Task<ToolOutput> CallAsync(string tool, JsonValue args, CancellationToken ct = default(CancellationToken))
        {
            if (args == null || args.Kind != JsonKind.Object) args = JsonValue.Obj();
            if (!_byName.TryGetValue(tool ?? "", out var spec))
                return ToolOutput.OfText("INVALID_PARAMS: unknown_tool - list the tools again; unknown tool '" + CompactText.SafeName(tool ?? "", 60) + "'", true);
            try
            {
                switch (spec.Name)
                {
                    case ToolStatus: return await StatusAsync(ct).ConfigureAwait(false);
                    case ToolSearch: return await SearchAsync(args, ct).ConfigureAwait(false);
                    case ToolDescribe: return await DescribeAsync(args, ct).ConfigureAwait(false);
                    case ToolCapCall:
                        {
                            string cap = args.GetString("capability");
                            if (string.IsNullOrEmpty(cap)) return ToolOutput.OfText("INVALID_PARAMS: missing_capability - pass {capability, params}", true);
                            var prms = args.GetObj("params") ?? JsonValue.Obj();
                            var def = _registry.Find(cap);
                            if (def != null)
                            {
                                var err = ApplyCaps(def, prms);
                                if (err != null) return err;
                            }
                            return await InvokeAsync(cap, prms, def, ct).ConfigureAwait(false);
                        }
                    case ToolWait:
                        {
                            double t = Math.Max(0, Math.Min(60, args.GetDouble("timeoutS", 20)));
                            var c = await EnsureConnectedAsync(ct).ConfigureAwait(false);
                            var res = await c.CallAsync("op.wait", JsonValue.Obj().Set("op", args.GetString("op")).Set("timeoutS", t), TimeSpan.FromSeconds(t + 10), ct).ConfigureAwait(false);
                            return RenderResult(res, null);
                        }
                    case ToolCancel:
                        {
                            var c = await EnsureConnectedAsync(ct).ConfigureAwait(false);
                            var res = await c.CallAsync("op.cancel", JsonValue.Obj().Set("op", args.GetString("op")), TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
                            return RenderResult(res, null);
                        }
                }
                var copy = JsonValue.Parse(args.ToString());
                var capsErr = ApplyCaps(spec.Def, copy);
                if (capsErr != null) return capsErr;
                return await InvokeAsync(spec.Def.Name, copy, spec.Def, ct).ConfigureAwait(false);
            }
            catch (SkizzyError e) { return ToolOutput.OfText(Render.Error(e), true); }
            catch (SkizzyClientException e) { return ToolOutput.OfText(Render.ClientError(e), true); }
            catch (OperationCanceledException) { return ToolOutput.OfText("CANCELLED: client_cancelled - the tool call was cancelled", true); }
        }

        /// <summary>McpDefaults and the list output caps (default limit, max rows). Returns an error output or null.</summary>
        internal ToolOutput ApplyCaps(CapabilityDef def, JsonValue args)
        {
            var props = def.InputSchema != null ? def.InputSchema.GetObj("properties") : null;
            if (def.McpDefaults != null)
            {
                for (int i = 0; i < def.McpDefaults.Count; i++)
                {
                    string k = def.McpDefaults.KeyAt(i);
                    if (args.Get(k) == null && props != null && props.Get(k) != null) args.Set(k, def.McpDefaults.ValueAt(i));
                }
            }
            if (def.ListShaped && props != null && props.Get("limit") != null)
            {
                var lim = args.Get("limit");
                if (lim == null || lim.Kind == JsonKind.Null) args.Set("limit", _o.DefaultLimit);
                else if (lim.Kind == JsonKind.Number && lim.AsDouble() > _o.MaxLimit)
                    return ToolOutput.OfText("INVALID_PARAMS: limit_too_large - at most " + _o.MaxLimit + " rows per call; page with the cursor from the previous result", true);
            }
            return null;
        }

        private async Task<ToolOutput> InvokeAsync(string method, JsonValue prms, CapabilityDef def, CancellationToken ct)
        {
            var c = await EnsureConnectedAsync(ct).ConfigureAwait(false);
            double timeoutS = 60;
            if (method == "events.wait") timeoutS = Math.Min(65, prms.GetDouble("timeoutS", 10) + 10);
            if (def != null && def.LongRunning) timeoutS = 30;
            var res = await c.CallAsync(method, prms, TimeSpan.FromSeconds(timeoutS), ct).ConfigureAwait(false);
            return RenderResult(res, def);
        }

        /// <summary>Text for the model: the result's text (compact formats) or JSON without meta, cut to MaxText; images as blocks.</summary>
        internal ToolOutput RenderResult(JsonValue res, CapabilityDef def)
        {
            var o = new ToolOutput();
            if (res == null || res.Kind != JsonKind.Object)
            {
                o.Blocks.Add(new ToolBlock { Text = res == null ? "null" : res.ToString() });
                return o;
            }
            var meta = res.GetObj("meta");
            var body = JsonValue.Parse(res.ToString());
            body.Remove("meta");
            var img = body.GetObj("image");
            if (img != null)
            {
                string b64 = img.GetString("data");
                if (b64 != null)
                {
                    try { o.Blocks.Add(new ToolBlock { Image = Convert.FromBase64String(b64), MimeType = img.GetString("mimeType") ?? "image/png" }); }
                    catch (FormatException) { }
                }
                body.Remove("image");
            }
            string text = body.GetString("text");
            string rendered;
            if (text != null)
            {
                var sb = new StringBuilder(text.TrimEnd('\n'));
                if (body.GetBool("truncated"))
                {
                    long total = body.GetLong("total");
                    sb.Append("\n(").Append(total > 0 ? total.ToString(CultureInfo.InvariantCulture) + " total; " : "")
                      .Append("next page: cursor=\"").Append(body.GetString("cursor")).Append("\")");
                }
                rendered = sb.ToString();
            }
            else
            {
                rendered = body.ToString();
                if (Encoding.UTF8.GetByteCount(rendered) > _o.MaxText) rendered = body.ToPrettyString();
            }
            string metaLine = Render.MetaLine(meta);
            if (metaLine != null) rendered = rendered + "\n" + metaLine;
            o.Blocks.Insert(0, new ToolBlock { Text = Render.CutText(rendered, _o.MaxText) });
            return o;
        }

        private async Task<ToolOutput> StatusAsync(CancellationToken ct)
        {
            SkizzyClient c;
            try { c = await EnsureConnectedAsync(ct).ConfigureAwait(false); }
            catch (Exception e) when (e is SkizzyClientException || e is SkizzyError)
            {
                var sb = new StringBuilder("not connected: ").Append(e.Message);
                if (e is SkizzyClientException ce && ce.Hint != null) sb.Append("\nhint: ").Append(ce.Hint);
                sb.Append("\ntools are listed anyway; game tools answer UNAVAILABLE until KSP is running with skizzy-protocol.");
                return ToolOutput.OfText(sb.ToString());
            }
            var state = await c.CallAsync("host.state", JsonValue.Obj(), TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            var hello = c.HelloResult;
            var sb2 = new StringBuilder();
            sb2.Append("connected to KSP (host ").Append(hello.GetString("hostVersion")).Append(", protocol ").Append(hello.GetString("version")).Append(")\n");
            sb2.Append("session ").Append(c.SessionId).Append(", role ").Append(hello.GetString("role"));
            string side = hello.GetString("team");
            if (side != null) sb2.Append(", side ").Append(side);
            if (c.Reconnects > 0) sb2.Append(", reconnects ").Append(c.Reconnects);
            sb2.Append('\n').Append(Render.MetaLine(state.GetObj("meta")));
            var active = state.GetString("activeVessel");
            if (active != null) sb2.Append("\nactive vessel: ").Append(state.GetString("activeVesselName") ?? "?").Append(" (").Append(active).Append(')');
            string note = hello.GetString("roleNote");
            if (note != null) sb2.Append("\nnote: ").Append(note);
            return ToolOutput.OfText(sb2.ToString());
        }

        private async Task<ToolOutput> SearchAsync(JsonValue args, CancellationToken ct)
        {
            int limit = Math.Max(1, Math.Min(_o.MaxLimit, args.GetInt("limit", _o.DefaultLimit)));
            string q = args.GetString("query") ?? "";
            JsonValue results = null;
            if (Connected)
            {
                try { results = (await _client.CallAsync("catalog.search", JsonValue.Obj().Set("query", q).Set("limit", limit), TimeSpan.FromSeconds(10), ct).ConfigureAwait(false)).GetArr("results"); }
                catch (SkizzyError) { results = null; }
            }
            if (results == null) results = LocalSearch(q, limit);
            var sb = new StringBuilder();
            foreach (var r in results.Items)
            {
                sb.Append(r.GetString("name")).Append(" [").Append(r.GetString("safety")).Append("] ").Append(r.GetString("summary")).Append('\n');
            }
            if (sb.Length == 0) sb.Append("no capability matches '").Append(CompactText.SafeName(q, 60)).Append("'");
            return ToolOutput.OfText(Render.CutText(sb.ToString().TrimEnd('\n'), _o.MaxText));
        }

        /// <summary>The host's catalog.search scoring, over the static catalog (works with the game closed).</summary>
        internal JsonValue LocalSearch(string q, int limit)
        {
            var scored = new List<KeyValuePair<int, CapabilityDef>>();
            string[] words = (q ?? "").ToLowerInvariant().Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var d in _registry.All)
            {
                int score = 0;
                string name = d.Name.ToLowerInvariant();
                string desc = (d.Description ?? "").ToLowerInvariant();
                if (words.Length == 0) score = 1;
                foreach (var w in words)
                {
                    if (name == w) score += 100;
                    else if (name.StartsWith(w, StringComparison.Ordinal)) score += 30;
                    else if (name.IndexOf(w, StringComparison.Ordinal) >= 0) score += 20;
                    if (desc.IndexOf(w, StringComparison.Ordinal) >= 0) score += 5;
                    if (Glob.IsMatch(d.Name, w)) score += 25;
                }
                if (score > 0) scored.Add(new KeyValuePair<int, CapabilityDef>(score, d));
            }
            scored.Sort((a, b) => b.Key != a.Key ? b.Key.CompareTo(a.Key) : string.CompareOrdinal(a.Value.Name, b.Value.Name));
            var arr = JsonValue.Arr();
            for (int i = 0; i < scored.Count && i < limit; i++)
            {
                var d = scored[i].Value;
                string desc = d.Description ?? "";
                int dot = desc.IndexOf(". ", StringComparison.Ordinal);
                arr.Add(JsonValue.Obj().Set("name", d.Name).Set("tool", d.ToolName).Set("safety", SafetyNames.Name(d.Safety))
                    .Set("summary", dot > 0 && dot < 200 ? desc.Substring(0, dot + 1) : (desc.Length > 200 ? desc.Substring(0, 200) + "..." : desc)));
            }
            return arr;
        }

        private async Task<ToolOutput> DescribeAsync(JsonValue args, CancellationToken ct)
        {
            string name = args.GetString("name");
            if (string.IsNullOrEmpty(name)) return ToolOutput.OfText("INVALID_PARAMS: missing_name - pass {name}", true);
            if (Connected)
            {
                var res = await _client.CallAsync("catalog.describe", JsonValue.Obj().Set("name", name), TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                return ToolOutput.OfText(Render.CutText((res.GetObj("capability") ?? res).ToPrettyString(), _o.MaxText));
            }
            var def = _registry.Find(name);
            if (def == null) return ToolOutput.OfText("NOT_FOUND: unknown - search with catalog_search; no capability '" + CompactText.SafeName(name, 60) + "'", true);
            return ToolOutput.OfText(Render.CutText(def.ToDescriptor().Set("note", "game not connected: policy for this session unknown").ToPrettyString(), _o.MaxText));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _client?.Dispose();
        }
    }
}
