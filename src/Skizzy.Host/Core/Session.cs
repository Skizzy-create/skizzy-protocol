using System;
using System.Collections.Generic;
using System.Threading;
using Skizzy.Host.Core.Net;
using Skizzy.Protocol.Catalog;
using Skizzy.Protocol.Errors;
using Skizzy.Protocol.Json;
using Skizzy.Protocol.Util;

namespace Skizzy.Host.Core
{
    /// <summary>
    /// A client session (03 §2): the authenticated connection plus the actor it speaks for. Survives a dropped socket
    /// for the resume window so a reconnecting client keeps its id, grants and event cursor.
    /// </summary>
    public sealed class Session
    {
        private static int _next;

        public readonly string Id;
        public Connection Conn;
        public bool Authenticated;
        public bool HelloDone;
        public long CreatedMs = Clock.NowMs;
        public long DetachedAtMs;

        // client
        public string ClientName = "unknown";
        public string ClientVersion = "";
        public string ClientKind = "sdk";
        public string McpClient;
        // actor
        public string ActorId;
        public string ActorKind = "script";
        public string Role = "script";
        public string RequestedRole;
        public string Side;
        public string Display;
        public int Precedence;
        public string Team;
        public bool Fog = true;
        public bool SeatHolder;
        public readonly HashSet<string> Features = new HashSet<string>();

        public readonly HashSet<string> Grants = new HashSet<string>();
        public readonly RateLimiter Calls;
        public readonly Dictionary<string, RateLimiter> CapLimits = new Dictionary<string, RateLimiter>();
        public int Inflight;
        public long CallCount;
        public long ErrorCount;
        public long LastCallMs;
        public long EventCursor;
        public readonly List<int> Subscriptions = new List<int>();
        /// <summary>events.subscribe globs (push as events.data); null = not subscribed.</summary>
        public volatile string[] EventGlobs;
        internal System.Threading.Timer HelloTimer;

        public Session(int callsPerMinute)
        {
            int n = Interlocked.Increment(ref _next);
            Id = "s-" + n.ToString("x4") + "-" + (Clock.NowMs & 0xFFFF).ToString("x4");
            Calls = new RateLimiter(callsPerMinute, 60000, callsPerMinute + "/min");
        }

        public bool IsAttached => Conn != null && Conn.IsOpen;

        public JsonValue Describe()
        {
            var o = JsonValue.Obj()
                .Set("session", Id)
                .Set("attached", IsAttached)
                .Set("actor", JsonValue.Obj().Set("id", ActorId).Set("kind", ActorKind).Set("role", Role)
                    .Set("side", Side).Set("display", Display).Set("precedence", Precedence))
                .Set("client", JsonValue.Obj().Set("name", ClientName).Set("version", ClientVersion).Set("kind", ClientKind).Set("mcpClient", McpClient))
                .Set("ageS", (Clock.NowMs - CreatedMs) / 1000)
                .Set("calls", CallCount)
                .Set("errors", ErrorCount)
                .Set("seat", SeatHolder);
            if (Conn != null)
                o.Set("bytesIn", Interlocked.Read(ref Conn.BytesIn)).Set("bytesOut", Interlocked.Read(ref Conn.BytesOut));
            return o;
        }
    }

    /// <summary>
    /// One capability invocation. Handlers return a result synchronously or call <see cref="Defer"/> and later
    /// <see cref="Complete"/>/<see cref="Fail"/> exactly once (screenshots, confirmations, long polls).
    /// </summary>
    public sealed class CallContext
    {
        public HostCore Host;
        public Session Session;
        public CapabilityDef Def;
        public JsonValue Params;
        public JsonValue RequestId;
        public string Method;
        public long StartTicks;
        public double MainThreadMs;
        public string Decision = "allow";
        public string DecisionSource;
        public string ConfirmedBy = "n/a";
        internal bool Deferred;
        internal bool InflightCounted;
        private int _done;
        internal Action<CallContext, JsonValue, SkizzyError> OnDone;

        public void Defer() { Deferred = true; }

        public bool IsDone => Volatile.Read(ref _done) != 0;

        public void Complete(JsonValue result) { Finish(result, null); }

        public void Fail(SkizzyError error) { Finish(null, error); }

        internal void Finish(JsonValue result, SkizzyError error)
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            OnDone?.Invoke(this, result, error);
        }

        // ---- parameter helpers (params already validated and defaulted against the schema) ----
        public string Str(string key, string def = null) { return Params.GetString(key, def); }
        public int Int(string key, int def = 0) { return Params.GetInt(key, def); }
        public double Num(string key, double def = 0) { return Params.GetDouble(key, def); }
        public bool Bool(string key, bool def = false) { return Params.GetBool(key, def); }
        public bool Has(string key) { var v = Params.Get(key); return v != null && !v.IsNull; }
    }

    public delegate JsonValue CapHandler(CallContext ctx);
}
