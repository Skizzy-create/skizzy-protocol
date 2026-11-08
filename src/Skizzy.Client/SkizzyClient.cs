using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Skizzy.Protocol;
using Skizzy.Protocol.Errors;
using Skizzy.Protocol.Json;

namespace Skizzy.Client
{
    /// <summary>Who this connection speaks for (03 §2 <c>session.hello</c>).</summary>
    public sealed class HelloOptions
    {
        public string ClientName = "skizzy-sdk";
        public string ClientVersion = ProtocolInfo.HostVersion;
        public string ClientKind = "sdk";       // map | bridge | sdk | cli
        public string McpClient;                // claude-code, codex, ... (bridge only)
        public string ActorId;
        public string ActorKind = "script";     // human | llm | script | behaviour | harness
        public string Role;                     // null = default for the kind
        public string Side;
        public string Display;
        public int? Precedence;
        public bool? Fog;
        public string Team;
        /// <summary>Delegation token for a delegate actor (09 §1.2, R2; M0 hosts ignore it).</summary>
        public string DelegationToken;
        public string[] Features = { "ops", "streams", "events" };

        public JsonValue ToParams(string resumeSession, long resumeCursor)
        {
            var actor = JsonValue.Obj().Set("kind", ActorKind);
            if (ActorId != null) actor.Set("id", ActorId);
            if (Role != null) actor.Set("role", Role);
            if (Side != null) actor.Set("side", Side);
            if (Display != null) actor.Set("display", Display);
            if (DelegationToken != null) actor.Set("delegationToken", DelegationToken);
            var p = JsonValue.Obj()
                .Set("protocol", ProtocolInfo.Name)
                .Set("versions", JsonValue.ArrOf(ProtocolInfo.SupportedVersions))
                .Set("client", JsonValue.Obj().Set("name", ClientName).Set("version", ClientVersion).Set("kind", ClientKind).Set("mcpClient", McpClient))
                .Set("actor", actor)
                .Set("features", JsonValue.ArrOf(Features));
            if (Precedence.HasValue) p.Set("precedence", Precedence.Value);
            if (Fog.HasValue || Team != null)
            {
                var view = JsonValue.Obj();
                if (Fog.HasValue) view.Set("fog", Fog.Value);
                if (Team != null) view.Set("team", Team);
                p.Set("view", view);
            }
            if (resumeSession != null) p.Set("resume", JsonValue.Obj().Set("session", resumeSession).Set("fromEventCursor", resumeCursor));
            return p;
        }
    }

    /// <summary>Where the host is: URL plus token (from endpoint.json and auth.token, 02 §5).</summary>
    public sealed class Endpoint
    {
        public string Url;
        public string Token;
        public string PluginData;

        public static string PluginDataFor(string kspRoot)
        {
            return Path.Combine(Path.Combine(Path.Combine(kspRoot, "GameData"), "SkizzyProtocol"), "PluginData");
        }

        /// <summary>Reads PluginData/endpoint.json and auth.token. Throws <see cref="SkizzyClientException"/> with a hint.</summary>
        public static Endpoint FromPluginData(string pluginData)
        {
            string ep = Path.Combine(pluginData, "endpoint.json");
            string tok = Path.Combine(pluginData, "auth.token");
            if (!File.Exists(tok))
                throw new SkizzyClientException("NO_TOKEN", "no auth.token in " + pluginData, "Start KSP once with Skizzy installed (it creates the token), or pass --token-file.");
            var e = new Endpoint { PluginData = pluginData, Token = File.ReadAllText(tok).Trim() };
            if (File.Exists(ep))
            {
                var j = JsonValue.Parse(File.ReadAllText(ep));
                e.Url = j.GetString("url") ?? ("ws://127.0.0.1:" + j.GetInt("port", ProtocolInfo.DefaultPort) + ProtocolInfo.Path);
            }
            else e.Url = "ws://127.0.0.1:" + ProtocolInfo.DefaultPort + ProtocolInfo.Path;
            return e;
        }

        public static Endpoint FromKsp(string kspRoot) { return FromPluginData(PluginDataFor(kspRoot)); }

        /// <summary>Per-user pointer the running host writes (%LOCALAPPDATA%/skizzy-protocol/endpoint.json).</summary>
        public static string UserPointerPath()
        {
            string dir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrEmpty(dir) ? null : Path.Combine(Path.Combine(dir, "skizzy-protocol"), "endpoint.json");
        }

        /// <summary>
        /// Finds the host without configuration. Order: explicit endpoint file, explicit KSP folder, SKIZZY_ENDPOINT /
        /// KSPDIR environment, the PluginData next to this executable (GameData/SkizzyProtocol/Bin), the per-user
        /// pointer. <paramref name="tokenFile"/> overrides where the token is read from. Returns where it looked in
        /// <paramref name="searched"/> so errors can say so.
        /// </summary>
        public static Endpoint Discover(string endpointFile, string kspRoot, string tokenFile, System.Collections.Generic.List<string> searched = null)
        {
            var tried = searched ?? new System.Collections.Generic.List<string>();
            string pluginData = null;
            if (!string.IsNullOrEmpty(endpointFile)) pluginData = Path.GetDirectoryName(Path.GetFullPath(endpointFile));
            else if (!string.IsNullOrEmpty(kspRoot)) pluginData = PluginDataFor(kspRoot);
            else
            {
                string envEp = Environment.GetEnvironmentVariable("SKIZZY_ENDPOINT");
                string envKsp = Environment.GetEnvironmentVariable("KSPDIR");
                if (!string.IsNullOrEmpty(envEp)) pluginData = Path.GetDirectoryName(Path.GetFullPath(envEp));
                else if (!string.IsNullOrEmpty(envKsp)) pluginData = PluginDataFor(envKsp);
                else
                {
                    string exeSibling = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "PluginData"));
                    tried.Add(exeSibling);
                    if (File.Exists(Path.Combine(exeSibling, "auth.token"))) pluginData = exeSibling;
                    else
                    {
                        string ptr = UserPointerPath();
                        if (ptr != null)
                        {
                            tried.Add(ptr);
                            if (File.Exists(ptr))
                            {
                                try { pluginData = JsonValue.Parse(File.ReadAllText(ptr)).GetString("pluginData"); }
                                catch (Exception) { pluginData = null; }
                            }
                        }
                    }
                }
            }
            if (pluginData == null)
                throw new SkizzyClientException("NO_ENDPOINT", "could not find a running skizzy host (looked in: " + string.Join("; ", tried.ToArray()) + ")",
                    "Start KSP with skizzy-protocol installed, or pass --ksp <KSP folder> (or set KSPDIR).");
            if (!tried.Contains(pluginData)) tried.Add(pluginData);
            var e = FromPluginDataOrToken(pluginData, tokenFile);
            return e;
        }

        private static Endpoint FromPluginDataOrToken(string pluginData, string tokenFile)
        {
            if (string.IsNullOrEmpty(tokenFile)) return FromPluginData(pluginData);
            if (!File.Exists(tokenFile))
                throw new SkizzyClientException("NO_TOKEN", "token file not found: " + tokenFile, "Check --token-file.");
            var e = new Endpoint { PluginData = pluginData, Token = File.ReadAllText(tokenFile).Trim() };
            string ep = Path.Combine(pluginData, "endpoint.json");
            if (File.Exists(ep)) e.Url = JsonValue.Parse(File.ReadAllText(ep)).GetString("url");
            if (e.Url == null) e.Url = "ws://127.0.0.1:" + ProtocolInfo.DefaultPort + ProtocolInfo.Path;
            return e;
        }
    }

    public sealed class SkizzyClientException : Exception
    {
        public readonly string Code;
        public readonly string Hint;
        public SkizzyClientException(string code, string message, string hint) : base(message) { Code = code; Hint = hint; }
    }

    /// <summary>
    /// C# client for the host (02 §1 <c>Skizzy.Client</c>): one WebSocket, JSON-RPC calls with ids, notifications,
    /// optional automatic reconnect with back-off (0.5, 1, 2, 4, max 10 s) and <c>resume</c> (02 §5).
    /// </summary>
    public sealed class SkizzyClient : IDisposable
    {
        private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonValue>> _pending = new ConcurrentDictionary<long, TaskCompletionSource<JsonValue>>();
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private ClientWebSocket _ws;
        private long _nextId;
        private CancellationTokenSource _cts;
        private Task _readLoop;
        private Func<Endpoint> _endpointSource;
        private HelloOptions _hello;
        private volatile bool _disposed;
        private int _reconnecting;
        public int MaxMessageBytes = 64 * 1024 * 1024;

        public JsonValue HelloResult { get; private set; }
        public string SessionId { get; private set; }
        public long LastEventCursor;
        public bool AutoReconnect;
        public bool IsConnected => _ws != null && _ws.State == WebSocketState.Open && HelloResult != null;
        public string LastError { get; private set; }
        public int Reconnects;

        /// <summary>Raised on the read thread for every notification (method, params).</summary>
        public event Action<string, JsonValue> Notification;
        public event Action<string> Disconnected;
        public event Action Reconnected;

        public Task ConnectAsync(Endpoint ep, HelloOptions hello, CancellationToken ct = default(CancellationToken))
        {
            return ConnectAsync(() => ep, hello, ct);
        }

        /// <summary>Connects and says hello. <paramref name="endpointSource"/> is re-read on every reconnect (new port after a restart).</summary>
        public async Task ConnectAsync(Func<Endpoint> endpointSource, HelloOptions hello, CancellationToken ct = default(CancellationToken))
        {
            _endpointSource = endpointSource;
            _hello = hello;
            await OpenAsync(null, 0, ct).ConfigureAwait(false);
        }

        private async Task OpenAsync(string resumeSession, long resumeCursor, CancellationToken ct)
        {
            var ep = _endpointSource();
            var ws = new ClientWebSocket();
            ws.Options.SetRequestHeader("Authorization", "Bearer " + ep.Token);
            ws.Options.AddSubProtocol(ProtocolInfo.SubProtocol);
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            try { await ws.ConnectAsync(new Uri(ep.Url), ct).ConfigureAwait(false); }
            catch (WebSocketException e)
            {
                ws.Dispose();
                throw new SkizzyClientException("CONNECT_FAILED", "cannot connect to " + ep.Url + ": " + e.Message,
                    "Is KSP running with Skizzy loaded? Check the in-game Skizzy window for the port, or run `skizzy doctor`.");
            }
            _ws = ws;
            _cts = new CancellationTokenSource();
            _readLoop = Task.Run(() => ReadLoop(ws, _cts.Token));
            HelloResult = null;
            var res = await RawCallAsync("session.hello", _hello.ToParams(resumeSession, resumeCursor), TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            HelloResult = res;
            SessionId = res.GetString("session");
            if (LastEventCursor == 0) LastEventCursor = res.GetLong("eventCursor");
        }

        public Task<JsonValue> CallAsync(string method, JsonValue prms = null, TimeSpan? timeout = null, CancellationToken ct = default(CancellationToken))
        {
            return RawCallAsync(method, prms, timeout ?? TimeSpan.FromSeconds(60), ct);
        }

        /// <summary>Synchronous convenience for scripts and the CLI.</summary>
        public JsonValue Call(string method, JsonValue prms = null, double timeoutS = 60)
        {
            return CallAsync(method, prms, TimeSpan.FromSeconds(timeoutS)).GetAwaiter().GetResult();
        }

        private async Task<JsonValue> RawCallAsync(string method, JsonValue prms, TimeSpan timeout, CancellationToken ct)
        {
            var ws = _ws;
            if (ws == null || ws.State != WebSocketState.Open)
                throw new SkizzyClientException("NOT_CONNECTED", "not connected to the host", LastError ?? "Is KSP running? Run `skizzy doctor`.");
            long id = Interlocked.Increment(ref _nextId);
            var tcs = new TaskCompletionSource<JsonValue>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;
            var req = JsonValue.Obj().Set("jsonrpc", "2.0").Set("id", id).Set("method", method).Set("params", prms ?? JsonValue.Obj());
            var bytes = JsonWriter.SerializeToBytes(req);
            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
            try { await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
            catch (Exception e)
            {
                _pending.TryRemove(id, out _);
                throw new SkizzyClientException("SEND_FAILED", "send failed: " + e.Message, "The connection dropped; retry.");
            }
            finally { _sendLock.Release(); }
            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeoutCts.CancelAfter(timeout);
                using (timeoutCts.Token.Register(() => tcs.TrySetException(new SkizzyError(ErrorKind.Timeout,
                    method + " timed out after " + timeout.TotalSeconds + " s (client side)", null, "The host may be busy or paused; retry.",
                    JsonValue.Obj().Set("timeoutMs", (long)timeout.TotalMilliseconds)))))
                {
                    try { return await tcs.Task.ConfigureAwait(false); }
                    finally { _pending.TryRemove(id, out _); }
                }
            }
        }

        private async Task ReadLoop(ClientWebSocket ws, CancellationToken ct)
        {
            var buf = new byte[64 * 1024];
            var msg = new MemoryStream();
            string reason = "closed";
            try
            {
                while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    var r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        reason = "host closed: " + r.CloseStatus + " " + ws.CloseStatusDescription;
                        try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false); } catch (Exception) { }
                        break;
                    }
                    msg.Write(buf, 0, r.Count);
                    if (msg.Length > MaxMessageBytes) { reason = "message too large"; break; }
                    if (!r.EndOfMessage) continue;
                    var data = msg.ToArray();
                    msg.SetLength(0);
                    Dispatch(data);
                }
            }
            catch (OperationCanceledException) { reason = "cancelled"; }
            catch (WebSocketException e) { reason = e.Message; }
            catch (Exception e) { reason = e.GetType().Name + ": " + e.Message; }
            OnDropped(ws, reason);
        }

        private void Dispatch(byte[] data)
        {
            JsonValue m;
            try { m = JsonParser.Parse(data, 0, data.Length, 128); }
            catch (JsonParseException) { return; }
            var idv = m.Get("id");
            if (m.Has("method") && idv == null)
            {
                string method = m.GetString("method");
                var p = m.GetObj("params") ?? JsonValue.Obj();
                if (method == "events.data") { long nc = p.GetLong("nextCursor"); if (nc > LastEventCursor) LastEventCursor = nc; }
                try { Notification?.Invoke(method, p); } catch (Exception) { }
                return;
            }
            if (idv == null || !idv.IsNumber) return;
            if (!_pending.TryGetValue(idv.AsLong(), out var tcs)) return;
            var err = m.GetObj("error");
            if (err != null) tcs.TrySetException(SkizzyError.FromErrorObject(err));
            else tcs.TrySetResult(m.Get("result") ?? JsonValue.Obj());
        }

        private void OnDropped(ClientWebSocket ws, string reason)
        {
            if (ws != _ws) return;
            LastError = reason;
            foreach (var kv in _pending)
                kv.Value.TrySetException(new SkizzyClientException("DISCONNECTED", "connection lost: " + reason, "Retry; the client reconnects automatically if enabled."));
            _pending.Clear();
            HelloResult = null;
            try { Disconnected?.Invoke(reason); } catch (Exception) { }
            if (AutoReconnect && !_disposed && !reason.Contains("UNAUTHENTICATED")) _ = ReconnectLoop();
        }

        private async Task ReconnectLoop()
        {
            if (Interlocked.Exchange(ref _reconnecting, 1) != 0) return;
            try
            {
                double[] backoff = { 0.5, 1, 2, 4, 10 };
                for (int attempt = 0; !_disposed; attempt++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(backoff[Math.Min(attempt, backoff.Length - 1)])).ConfigureAwait(false);
                    try
                    {
                        await OpenAsync(SessionId, LastEventCursor, CancellationToken.None).ConfigureAwait(false);
                        Reconnects++;
                        try { Reconnected?.Invoke(); } catch (Exception) { }
                        return;
                    }
                    catch (Exception e) { LastError = e.Message; }
                }
            }
            finally { Interlocked.Exchange(ref _reconnecting, 0); }
        }

        /// <summary>Waits until connected (for reconnecting callers), up to the timeout.</summary>
        public async Task<bool> WaitConnectedAsync(TimeSpan timeout)
        {
            var end = DateTime.UtcNow + timeout;
            while (!IsConnected && DateTime.UtcNow < end) await Task.Delay(100).ConfigureAwait(false);
            return IsConnected;
        }

        public void Dispose()
        {
            _disposed = true;
            var ws = _ws;
            if (ws != null)
            {
                try
                {
                    if (ws.State == WebSocketState.Open)
                        ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).Wait(500);
                }
                catch (Exception) { }
            }
            try { _cts?.Cancel(); } catch (Exception) { }
            ws?.Dispose();
        }
    }
}
