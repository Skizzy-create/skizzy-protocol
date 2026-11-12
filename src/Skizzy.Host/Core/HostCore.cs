using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Skizzy.Host.Core.Caps;
using Skizzy.Host.Core.Net;
using Skizzy.Host.Core.World;
using Skizzy.Protocol;
using Skizzy.Protocol.Catalog;
using Skizzy.Protocol.Config;
using Skizzy.Protocol.Errors;
using Skizzy.Protocol.Json;
using Skizzy.Protocol.Permissions;

namespace Skizzy.Host.Core
{
    /// <summary>
    /// The Unity-free host (02 §1.1 Core). The KSP pump and the FakeKsp runner drive it through
    /// <see cref="OnUpdate"/>/<see cref="OnFixedUpdate"/> on their main thread, and only while
    /// <see cref="WantsUpdates"/> is true: with nothing connected and nothing running the pump is disabled
    /// and the host costs nothing per frame (acceptance 1).
    /// </summary>
    public sealed partial class HostCore : IConnectionHandler
    {
        public readonly HostSettings Settings;
        public readonly HostPaths Paths;
        public readonly IGameWorld World;
        public readonly IHostLog Log;
        public readonly HandleRegistry Handles;
        public readonly EventRing Events = new EventRing(8192);
        public readonly OpTable Ops;
        public readonly AuditWriter Audit;
        public readonly KillSwitch Kill = new KillSwitch();
        public readonly FaultTracker Faults = new FaultTracker();
        public readonly PromptQueue Prompts = new PromptQueue();
        public readonly Dispatcher Dispatcher;
        public readonly DefRegistry Catalog;
        public readonly StreamManager Streams;
        public readonly LogManager Logs;
        /// <summary>The C2 orders engine slot (src/Skizzy.C2); inert until the BDArmory binding attaches an engine.</summary>
        public readonly C2Host C2;
        /// <summary>M2: per-team track picture engine (tac.*, source.tactical); attached with the C2 engine.</summary>
        public readonly Tactical.TacticalHost Tactical;
        /// <summary>M2: BDArmory operations (bda.*); the BDArmory plugin attaches them in flight.</summary>
        public readonly Bda.BdaHost Bda;
        public readonly HostStats Stats = new HostStats();
        public readonly WorldEvents WorldEvents;
#if !SKIZZY_KSP
        public readonly AllocCounters Alloc = new AllocCounters();
#endif
        /// <summary>Set between onGameSceneLoadRequested and the new scene's GUI-ready (UNAVAILABLE_IN_SCENE/transition).</summary>
        public volatile bool SceneTransition;
        public int CatalogRevision = 1;

        private readonly Dictionary<string, CapHandler> _handlers = new Dictionary<string, CapHandler>();
        private readonly Dictionary<string, Func<CallContext, Operation, IEnumerator<object>>> _opHandlers =
            new Dictionary<string, Func<CallContext, Operation, IEnumerator<object>>>();
        private readonly List<Session> _sessions = new List<Session>();
        private readonly object _metaLock = new object();
        private MetaState _meta;
        private volatile PermissionEngine _perm;
        private byte[] _token;
        private WsServer _server;
        private int _mainThreadId = -1;
        private long _lastActiveMs;
        private long _frames;
        private readonly IMainThreadWaker _waker;
        public string StartError;
        public bool Started;
        /// <summary>KSP layer: extra self-check lines (GameEvents counts, OnFlyByWire, Harmony, spikes, textures).</summary>
        public Action<CallContext, List<string[]>> SelfCheckExtras;
        /// <summary>KSP layer: Unity object counts for the screenshot leak gate (null in FakeKsp).</summary>
        public Func<JsonValue> CountUnityObjects;

        /// <summary>Keeps the pump awake this long after the last activity (lets deferred work settle).</summary>
        public double GraceS = 5;

        public HostCore(HostSettings settings, HostPaths paths, IGameWorld world, IMainThreadWaker waker, IHostLog log)
        {
            Settings = settings;
            Paths = paths;
            World = world;
            Log = log ?? NullLog.Instance;
            _waker = waker;
            Handles = new HandleRegistry(world);
            Ops = new OpTable(Log);
            Audit = new AuditWriter(paths.AuditDir, Log);
            Dispatcher = new Dispatcher(waker, settings.BudgetMs, settings.BudgetMinMs, settings.BudgetMaxMs) { Execute = ExecuteJob };
            Catalog = Builtins.Registry;
            Streams = new StreamManager(this);
            Logs = new LogManager(this);
            Tactical = new Tactical.TacticalHost(this);
            Bda = new Bda.BdaHost(this);
            C2 = new C2Host(this);
            WorldEvents = new WorldEvents(this);
            _perm = new PermissionEngine(PermissionConfig.BattleLab());
            Events.UtSource = () => { lock (_metaLock) return _meta.Ut; };
            Events.FrameSource = () => { lock (_metaLock) return _meta.Frame; };
            Events.Listeners = OnEventEmitted;
            _meta.Scene = "LOADING";
            _meta.TimeScale = 1;
            _meta.WarpMode = "none";
            _meta.WarpRate = 1;
            BuiltinCaps.Register(this);
        }

        // ------------------------------------------------------------------ lifecycle

        public PermissionEngine Permissions => _perm;
        public string WakerMode => _waker.Mode;
        public int Port => _server != null ? _server.Port : 0;
        public WsServer Server => _server;

        /// <summary>Loads token and permissions, binds the socket, writes endpoint.json. Never throws.</summary>
        public void Start()
        {
            try
            {
                Directory.CreateDirectory(Paths.PluginData);
                LoadPermissions();
                if (!Settings.Listen)
                {
                    Log.Info("listen=false in settings.cfg: socket not opened (baseline mode)");
                    Started = true;
                    return;
                }
                _token = TokenStore.LoadOrCreate(Paths.TokenFile, Log);
                var policy = new HandshakePolicy { TokenBytes = _token, AllowedOrigins = Settings.AllowedOrigins };
                _server = new WsServer(this, policy, Settings.Limits, Log);
                int port = _server.Start(Settings.PortFirst, Settings.PortLast);
                EndpointFile.Write(Paths.EndpointFile, port, Log);
                if (Settings.WriteUserPointer) EndpointFile.WritePointer(Paths.PluginData, port, Log);
                Started = true;
                Log.Info("listening on ws://127.0.0.1:" + port + ProtocolInfo.Path);
            }
            catch (Exception e)
            {
                StartError = e.Message;
                Log.Error("host start failed: " + e);
            }
        }

        public void Stop()
        {
            try
            {
                Logs.StopAll("host stopping");
                Streams.Clear();
                Ops.CancelAll("scene_change");
                Prompts.CancelAll();
                Events.ReleaseAll();
                _server?.Stop();
                EndpointFile.Delete(Paths.EndpointFile);
                if (Settings.WriteUserPointer) EndpointFile.DeletePointer();
                Audit.Stop();
            }
            catch (Exception e) { Log.Error("host stop failed: " + e.Message); }
        }

        public void LoadPermissions()
        {
            var warnings = new List<string>();
            PermissionConfig cfg;
            try
            {
                if (File.Exists(Paths.PermissionsFile))
                    cfg = PermissionConfig.Parse(File.ReadAllText(Paths.PermissionsFile), warnings);
                else
                {
                    cfg = PermissionConfig.BattleLab();
                    File.WriteAllText(Paths.PermissionsFile, cfg.Serialize());
                }
            }
            catch (Exception e)
            {
                Log.Warn("permissions.cfg unreadable (" + e.Message + "); using Battle Lab");
                cfg = PermissionConfig.BattleLab();
            }
            foreach (var w in warnings) Log.Warn("permissions.cfg: " + w);
            _perm = new PermissionEngine(cfg);
        }

        /// <summary>Saves a modified permission config (in-game window only; nothing over the wire).</summary>
        public void SavePermissions(PermissionConfig cfg)
        {
            File.WriteAllText(Paths.PermissionsFile, cfg.Serialize());
            _perm = new PermissionEngine(cfg);
            CatalogRevision++;
        }

        public void RegenerateToken()
        {
            _token = TokenStore.Create(Paths.TokenFile);
            if (_server != null) _server.SetToken(_token);
            List<Session> all;
            lock (_sessions) all = new List<Session>(_sessions);
            foreach (var s in all) s.Conn?.Close(WsClose.ForErrorCode(-32001), "token regenerated");
        }

        // ------------------------------------------------------------------ registration

        public void Register(string capability, CapHandler handler)
        {
            if (Catalog.Find(capability) == null) throw new ArgumentException("no CapabilityDef for " + capability);
            _handlers[capability] = handler;
        }

        public void RegisterOp(string capability, Func<CallContext, Operation, IEnumerator<object>> run)
        {
            if (Catalog.Find(capability) == null) throw new ArgumentException("no CapabilityDef for " + capability);
            _opHandlers[capability] = run;
        }

        public bool HasHandler(string capability) { return _handlers.ContainsKey(capability) || _opHandlers.ContainsKey(capability); }

        // ------------------------------------------------------------------ main-thread pump

        /// <summary>True while the pump must run: sessions, queued jobs, ops, logs, streams, prompts, or the grace period.</summary>
        public bool WantsUpdates
        {
            get
            {
                if (Dispatcher.HasWork || Ops.RunningCount > 0 || Logs.ActiveCount > 0 || Streams.Count > 0 || Prompts.Count > 0 || C2.WantsTicks || Tactical.SubscriptionCount > 0) return true;
                if (AttachedSessionCount > 0) return true;
                return Clock.NowMs - Volatile.Read(ref _lastActiveMs) < GraceS * 1000;
            }
        }

        public bool IsMainThread => Thread.CurrentThread.ManagedThreadId == _mainThreadId;

        /// <summary>Main thread, every frame while the pump is enabled.</summary>
        public void OnUpdate(double frameMs)
        {
            long t0 = Clock.Ticks;
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            _frames++;
            PublishMeta();
            Dispatcher.NoteFrameMs(frameMs);
            Dispatcher.DrainMain();
            Ops.Step();
            C2.OnUpdate();
            Stats.FrameMs.Add(frameMs);
            Stats.UpdateMs.Add(Clock.SinceMs(t0));
        }

        /// <summary>Main thread, every physics step while the pump is enabled.</summary>
        public void OnFixedUpdate()
        {
            long t0 = Clock.Ticks;
            Dispatcher.DrainFixed();
            if (Tactical.WantsTicks) Tactical.OnFixedUpdate(); // M2: one TPE slice per physics step, a full picture every 10
            if (C2.WantsTicks) C2.OnFixedUpdate(); // C2 slot: 5 Hz engine tick while a c2.* command is live
#if SKIZZY_KSP
            if (Logs.ActiveCount > 0) Logs.Sample();
            if (Streams.Count > 0) Streams.Sample();
#else
            // CI allocation probe (02 §8.1): exact bytes per sampling pass on .NET 10; Unity's mscorlib lacks the API.
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            if (Logs.ActiveCount > 0) Logs.Sample();
            long a1 = GC.GetAllocatedBytesForCurrentThread();
            if (Streams.Count > 0) Streams.Sample();
            long a2 = GC.GetAllocatedBytesForCurrentThread();
            Alloc.Record(a1 - a0, a2 - a1);
#endif
            Stats.FixedMs.Add(Clock.SinceMs(t0));
        }

        /// <summary>Main thread: decide whether the pump can sleep. Returns true if it should stay enabled.</summary>
        public bool AfterFrame()
        {
            if (WantsUpdates) { Dispatcher.MarkAwake(); return true; }
            return !Dispatcher.TrySleep(() => WantsUpdates);
        }

        /// <summary>Marks activity so the pump keeps running for the grace period.</summary>
        public void Touch() { Volatile.Write(ref _lastActiveMs, Clock.NowMs); }

        private void PublishMeta()
        {
            var m = default(MetaState);
            try { World.ReadMeta(ref m); }
            catch (Exception e) { Log.Warn("ReadMeta failed: " + e.Message); return; }
            if (SceneTransition) m.SceneTransition = true;
            lock (_metaLock) _meta = m;
        }

        /// <summary>Main thread: refresh meta now (scene changes, before answering a call).</summary>
        public void RefreshMeta() { PublishMeta(); }

        public MetaState Meta { get { lock (_metaLock) return _meta; } }

        // skizzy:hot HostCore.WriteMeta
        public void WriteMeta(JsonWriter w)
        {
            MetaState m;
            lock (_metaLock) m = _meta;
            w.Name("meta").BeginObject()
                .Prop("ut", m.Ut).Prop("frame", m.Frame).Prop("scene", m.Scene).Prop("paused", m.Paused)
                .Prop("timeScale", m.TimeScale);
            w.Name("warp").BeginObject().Prop("mode", m.WarpMode ?? "none").Prop("rate", m.WarpRate).EndObject();
            w.Prop("catalogRevision", CatalogRevision);
            if (Kill.Engaged) w.Prop("killSwitch", true);
            w.EndObject();
        }

        // ------------------------------------------------------------------ sessions

        public int AttachedSessionCount
        {
            get
            {
                lock (_sessions)
                {
                    int n = 0;
                    foreach (var s in _sessions) if (s.IsAttached) n++;
                    return n;
                }
            }
        }

        public void SnapshotSessions(List<Session> into) { lock (_sessions) into.AddRange(_sessions); }

        public Session FindSession(string id)
        {
            lock (_sessions)
                foreach (var s in _sessions) if (s.Id == id) return s;
            return null;
        }

        public void Disconnect(string sessionId, string reason)
        {
            var s = FindSession(sessionId);
            s?.Conn?.Close(WsClose.Normal, reason);
        }

        void IConnectionHandler.OnOpen(Connection c)
        {
            var s = new Session(Settings.Limits.CallsPerMinute) { Conn = c, Authenticated = c.Handshake.TokenVerified };
            c.SessionTag = s;
            lock (_sessions) _sessions.Add(s);
            Touch();
            Dispatcher.Wake();
            // Close connections that never say hello (10 s).
            s.HelloTimer = new Timer(_ =>
            {
                if (!s.HelloDone && c.IsOpen) c.Close(WsClose.ForErrorCode(-32001), "no session.hello within " + Settings.Limits.HelloTimeoutS + " s");
                s.HelloTimer?.Dispose();
                s.HelloTimer = null;
            }, null, (int)(Settings.Limits.HelloTimeoutS * 1000), Timeout.Infinite);
        }

        void IConnectionHandler.OnClosed(Connection c, int code, string reason)
        {
            var s = c.SessionTag as Session;
            if (s == null) return;
            Touch();
            Dispatcher.Wake();
            Prompts.CancelAll(s.Id);
            Streams.RemoveSession(s);
            bool keep = s.HelloDone && s.ActorId != null;
            lock (_sessions)
            {
                if (s.Conn == c)
                {
                    s.DetachedAtMs = Clock.NowMs;
                    if (!keep) _sessions.Remove(s);
                }
                PruneDetached();
            }
            if (s.HelloDone)
            {
                if (s.SeatHolder) s.SeatHolder = false;
                Events.Emit("host.session_closed", "system", (s.ActorId ?? s.ClientName) + " disconnected (" + reason + ")",
                    JsonValue.Obj().Set("session", s.Id).Set("code", code).Set("reason", reason));
            }
        }

        private void PruneDetached()
        {
            long now = Clock.NowMs;
            for (int i = _sessions.Count - 1; i >= 0; i--)
            {
                var s = _sessions[i];
                if (!s.IsAttached && s.DetachedAtMs > 0 && now - s.DetachedAtMs > Settings.Limits.ResumeWindowS * 1000) _sessions.RemoveAt(i);
            }
        }

        // ------------------------------------------------------------------ messages

        void IConnectionHandler.OnMessage(Connection c, ByteBuffer message, byte opcode)
        {
            var s = c.SessionTag as Session;
            if (s == null) return;
            if (opcode != WsOpcode.Text)
            {
                SendError(c, JsonValue.Null, new SkizzyError(ErrorKind.InvalidRequest, "binary frames need enc.msgpack, which arrives in M2", "binary_unsupported",
                    "Send JSON-RPC as text frames."));
                return;
            }
            Touch();
            JsonValue req;
            try { req = JsonParser.Parse(message.Data, 0, message.Length, Settings.Limits.MaxJsonDepth); }
            catch (JsonParseException e)
            {
                SendError(c, JsonValue.Null, new SkizzyError(ErrorKind.ParseError, "invalid JSON at byte " + e.Offset + ": " + e.Message));
                return;
            }
            HandleRequest(s, c, req);
        }

        private void HandleRequest(Session s, Connection c, JsonValue req)
        {
            if (req.IsArray)
            {
                SendError(c, JsonValue.Null, new SkizzyError(ErrorKind.InvalidRequest, "batches are not supported", "batch", "Send one JSON-RPC request per frame."));
                return;
            }
            if (!req.IsObject || req.GetString("jsonrpc") != "2.0")
            {
                SendError(c, req.IsObject ? IdOf(req) : JsonValue.Null, new SkizzyError(ErrorKind.InvalidRequest, "not a JSON-RPC 2.0 request object"));
                return;
            }
            var id = IdOf(req);
            string method = req.GetString("method");
            if (string.IsNullOrEmpty(method))
            {
                SendError(c, id, new SkizzyError(ErrorKind.InvalidRequest, "missing method"));
                return;
            }
            var p = req.Get("params");
            if (p == null || p.IsNull) p = JsonValue.Obj();
            if (!p.IsObject)
            {
                SendError(c, id, SkizzyError.InvalidParams("params must be an object (named parameters)"));
                return;
            }
            bool notification = !req.Has("id");

            if (!s.HelloDone)
            {
                if (method != "session.hello")
                {
                    if (!s.Authenticated)
                    {
                        SendError(c, id, new SkizzyError(ErrorKind.Unauthenticated, "authenticate with session.hello first"));
                        c.Close(WsClose.ForErrorCode(-32001), "UNAUTHENTICATED");
                    }
                    else SendError(c, id, new SkizzyError(ErrorKind.InvalidRequest, "call session.hello first", "no_hello", "The first request on a connection must be session.hello."));
                    return;
                }
                Hello(s, c, id, p);
                return;
            }

            s.CallCount++;
            s.LastCallMs = Clock.NowMs;
            Interlocked.Increment(ref Stats.Calls);
            try
            {
                if (TryProtocolMethod(s, id, method, p, notification)) return;
                string capName = method;
                JsonValue args = p;
                if (method == "cap.call")
                {
                    capName = p.GetString("name");
                    if (string.IsNullOrEmpty(capName)) throw SkizzyError.InvalidParam("/name", "cap.call needs the capability name");
                    args = p.GetObj("args") ?? p.GetObj("params") ?? JsonValue.Obj();
                }
                var def = Catalog.Find(capName);
                if (def == null)
                {
                    string guess = Skizzy.Protocol.Util.Levenshtein.Closest(capName, AllMethodNames());
                    var extra = JsonValue.Obj().Set("name", capName);
                    if (guess != null) extra.Set("didYouMean", guess);
                    throw new SkizzyError(ErrorKind.MethodNotFound, "no method or capability '" + Skizzy.Protocol.Util.CompactText.SafeName(capName, 80) + "'", null,
                        guess != null ? "Did you mean " + guess + "? Search with catalog.search." : "Search capabilities with catalog.search.", extra);
                }
                Invoke(s, id, method, def, args, notification);
            }
            catch (SkizzyError e)
            {
                s.ErrorCount++;
                if (!notification) SendError(c, id, e);
            }
            catch (Exception e)
            {
                s.ErrorCount++;
                string trace = NewTraceId();
                Log.Error("request " + method + " failed [" + trace + "]: " + e);
                if (!notification) SendError(c, id, SkizzyError.Internal("host error: " + e.GetType().Name, trace));
            }
        }

        private static JsonValue IdOf(JsonValue req)
        {
            var id = req.Get("id");
            if (id == null) return JsonValue.Null;
            if (id.IsString || id.IsNumber || id.IsNull) return id;
            return JsonValue.Null;
        }

        private IEnumerable<string> AllMethodNames()
        {
            foreach (var d in Catalog.All) yield return d.Name;
            foreach (var m in ProtocolMethods) yield return m;
        }

        public static readonly string[] ProtocolMethods =
        {
            "session.hello", "session.bye", "session.heartbeat", "catalog.get", "catalog.search", "catalog.describe", "cap.call",
            "op.get", "op.wait", "op.cancel", "op.list", "stream.subscribe", "stream.modify", "stream.unsubscribe", "stream.list",
            "events.subscribe", "events.unsubscribe",
        };

        private static int _trace;
        public static string NewTraceId() { return "t-" + Interlocked.Increment(ref _trace).ToString("x6") + "-" + (Clock.NowMs & 0xFFFFF).ToString("x5"); }

        // ------------------------------------------------------------------ sending

        public void SendResult(Session s, JsonValue id, JsonValue result)
        {
            var c = s.Conn;
            if (c == null || !c.IsOpen) return;
            var b = ByteBufferPool.Rent(1024);
            var w = JsonWriterPool.Rent(b);
            try
            {
                w.BeginObject().Prop("jsonrpc", "2.0").Name("id").Value(id ?? JsonValue.Null).Name("result").BeginObject();
                if (result != null && result.IsObject)
                    for (int i = 0; i < result.Count; i++)
                    {
                        string k = result.KeyAt(i);
                        if (k == "meta") continue;
                        w.Name(k).Value(result.ValueAt(i));
                    }
                WriteMeta(w);
                w.EndObject().EndObject();
            }
            finally { JsonWriterPool.Return(w); }
            c.Send(b);
        }

        public void SendError(Connection c, JsonValue id, SkizzyError e)
        {
            if (c == null || !c.IsOpen) return;
            var b = ByteBufferPool.Rent(512);
            var w = JsonWriterPool.Rent(b);
            try
            {
                w.BeginObject().Prop("jsonrpc", "2.0").Name("id").Value(id ?? JsonValue.Null).Name("error").Value(e.ToErrorObject()).EndObject();
            }
            finally { JsonWriterPool.Return(w); }
            c.Send(b);
        }

        /// <summary>Sends a notification whose params are written by <paramref name="body"/> (meta appended).</summary>
        public void Notify(Session s, string method, JsonValue prms)
        {
            var c = s.Conn;
            if (c == null || !c.IsOpen) return;
            var b = ByteBufferPool.Rent(512);
            var w = JsonWriterPool.Rent(b);
            try
            {
                w.BeginObject().Prop("jsonrpc", "2.0").Prop("method", method).Name("params").BeginObject();
                for (int i = 0; i < prms.Count; i++) w.Name(prms.KeyAt(i)).Value(prms.ValueAt(i));
                WriteMeta(w);
                w.EndObject().EndObject();
            }
            finally { JsonWriterPool.Return(w); }
            c.Send(b);
        }

        // ------------------------------------------------------------------ events push

        private void OnEventEmitted(HostEvent e)
        {
            List<Session> subs = null;
            lock (_sessions)
                foreach (var s in _sessions)
                    if (s.IsAttached && s.EventGlobs != null && EventRing.Matches(e.Name, s.EventGlobs)) (subs ?? (subs = new List<Session>())).Add(s);
            if (subs == null) return;
            var p = JsonValue.Obj().Set("events", JsonValue.Arr().Add(e.ToJson())).Set("nextCursor", e.Cursor);
            foreach (var s in subs) Notify(s, "events.data", p);
        }
    }

#if !SKIZZY_KSP
    /// <summary>Allocation bytes of the FixedUpdate sampling passes (CI only).</summary>
    public sealed class AllocCounters
    {
        public long LogPasses, StreamPasses, LogMax, StreamMax, LogTotal, StreamTotal;
        public void Record(long log, long stream)
        {
            LogPasses++; LogTotal += log; if (log > LogMax) LogMax = log;
            StreamPasses++; StreamTotal += stream; if (stream > StreamMax) StreamMax = stream;
        }
        public void Reset() { LogPasses = StreamPasses = LogMax = StreamMax = LogTotal = StreamTotal = 0; }
    }
#endif

    /// <summary>Pooled <see cref="JsonWriter"/>s so response serialisation does not allocate writers.</summary>
    public static class JsonWriterPool
    {
        private static readonly Stack<JsonWriter> Pool = new Stack<JsonWriter>();

        public static JsonWriter Rent(ByteBuffer b)
        {
            lock (Pool)
            {
                if (Pool.Count > 0)
                {
                    var w = Pool.Pop();
                    w.Reset(b);
                    return w;
                }
            }
            return new JsonWriter(b);
        }

        public static void Return(JsonWriter w)
        {
            lock (Pool) if (Pool.Count < 32) Pool.Push(w);
        }
    }

    /// <summary>auth.token: 256-bit random, base64url, in PluginData (never in the repo, never logged).</summary>
    public static class TokenStore
    {
        public static byte[] LoadOrCreate(string path, IHostLog log)
        {
            try
            {
                if (File.Exists(path))
                {
                    string t = File.ReadAllText(path).Trim();
                    if (t.Length >= 32) return Encoding.ASCII.GetBytes(t);
                    log.Warn("auth.token too short; regenerating");
                }
            }
            catch (Exception e) { log.Warn("auth.token unreadable (" + e.Message + "); regenerating"); }
            return Create(path);
        }

        public static byte[] Create(string path)
        {
            var raw = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(raw);
            string t = Convert.ToBase64String(raw).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, t);
            return Encoding.ASCII.GetBytes(t);
        }
    }

    /// <summary>PluginData/endpoint.json: bound port, pid and start time, read by clients (02 §5).</summary>
    public static class EndpointFile
    {
        public static void Write(string path, int port, IHostLog log)
        {
            try
            {
                int pid = 0;
                try { pid = System.Diagnostics.Process.GetCurrentProcess().Id; } catch (Exception) { }
                var o = JsonValue.Obj()
                    .Set("protocol", ProtocolInfo.Name)
                    .Set("version", ProtocolInfo.Version)
                    .Set("hostVersion", ProtocolInfo.HostVersion)
                    .Set("url", "ws://127.0.0.1:" + port + ProtocolInfo.Path)
                    .Set("host", "127.0.0.1")
                    .Set("port", port)
                    .Set("path", ProtocolInfo.Path)
                    .Set("subprotocol", ProtocolInfo.SubProtocol)
                    .Set("pid", pid)
                    .Set("startedUtc", Clock.WallUtcIso())
                    .Set("tokenFile", "auth.token");
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, o.ToPrettyString());
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch (Exception e) { log.Warn("endpoint.json not written: " + e.Message); }
        }

        public static void Delete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
        }

        /// <summary>
        /// Per-user pointer (%LOCALAPPDATA%/skizzy-protocol/endpoint.json) naming the running host's PluginData, so the
        /// Python SDK and a CLI outside GameData find the token and port with no configuration. Outside the KSP install.
        /// </summary>
        public static string PointerPath()
        {
            string dir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(dir)) return null;
            return Path.Combine(Path.Combine(dir, "skizzy-protocol"), "endpoint.json");
        }

        public static void WritePointer(string pluginData, int port, IHostLog log)
        {
            try
            {
                string path = PointerPath();
                if (path == null) return;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                int pid = 0;
                try { pid = System.Diagnostics.Process.GetCurrentProcess().Id; } catch (Exception) { }
                var o = JsonValue.Obj().Set("pluginData", pluginData).Set("url", "ws://127.0.0.1:" + port + ProtocolInfo.Path).Set("port", port)
                    .Set("pid", pid).Set("startedUtc", Clock.WallUtcIso());
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, o.ToPrettyString());
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch (Exception e) { log.Warn("user endpoint pointer not written: " + e.Message); }
        }

        /// <summary>Deletes the pointer only if this process wrote it (another KSP instance may own it now).</summary>
        public static void DeletePointer()
        {
            try
            {
                string path = PointerPath();
                if (path == null || !File.Exists(path)) return;
                var j = JsonValue.Parse(File.ReadAllText(path));
                int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
                if (j.GetInt("pid") == pid) File.Delete(path);
            }
            catch (Exception) { }
        }
    }
}
