using System;
using System.Collections.Generic;
using System.Threading;
using Skizzy.Host.Core.Net;
using Skizzy.Protocol;
using Skizzy.Protocol.Catalog;
using Skizzy.Protocol.Errors;
using Skizzy.Protocol.Json;
using Skizzy.Protocol.Permissions;
using Skizzy.Protocol.Schema;
using Skizzy.Protocol.Util;

namespace Skizzy.Host.Core
{
    public sealed partial class HostCore
    {
        public static readonly string[] HostFeatures = { "ops", "streams", "events", "confirm.game" };

        // ------------------------------------------------------------------ session.hello (network thread)

        private void Hello(Session s, Connection c, JsonValue id, JsonValue p)
        {
            try
            {
                if (!s.Authenticated)
                {
                    string tok = p.GetString("token");
                    if (tok == null || !WsHandshake.TokenEquals(tok, _token))
                    {
                        SendError(c, id, new SkizzyError(ErrorKind.Unauthenticated, tok == null ? "token required" : "invalid token", null,
                            "Send 'Authorization: Bearer <token>' (or params.token for allow-listed browser origins); the token is in PluginData/auth.token."));
                        c.Close(WsClose.ForErrorCode(-32001), "UNAUTHENTICATED");
                        return;
                    }
                    s.Authenticated = true;
                }
                string protocol = p.GetString("protocol", ProtocolInfo.Name);
                if (protocol != ProtocolInfo.Name) throw SkizzyError.InvalidParam("/protocol", "protocol must be \"" + ProtocolInfo.Name + "\"");
                var versions = p.GetStringList("versions");
                if (versions != null && versions.Count > 0 && !versions.Contains(ProtocolInfo.Version))
                    throw new SkizzyError(ErrorKind.VersionUnsupported, "no common protocol version", null, "Upgrade the client.",
                        JsonValue.Obj().Set("supported", JsonValue.ArrOf(ProtocolInfo.SupportedVersions)));

                var client = p.GetObj("client") ?? JsonValue.Obj();
                var actor = p.GetObj("actor") ?? JsonValue.Obj();
                string clientKind = client.GetString("kind", "sdk");
                if (!Actors.IsClientKind(clientKind)) throw SkizzyError.InvalidParam("/client/kind", "client.kind must be one of map, bridge, sdk, cli");
                string actorKind = actor.GetString("kind", clientKind == "bridge" ? "llm" : "script");
                if (actorKind == "orchestrator") throw SkizzyError.InvalidParam("/actor/kind", "actor.kind 'orchestrator' was removed (03 §2); use llm");
                if (!Actors.IsKind(actorKind)) throw SkizzyError.InvalidParam("/actor/kind", "actor.kind must be one of human, llm, script, behaviour, harness");
                string role = actor.GetString("role") ?? Actors.DefaultRole(actorKind);
                if (!Actors.IsRole(role)) throw SkizzyError.InvalidParam("/actor/role", "unknown role '" + CompactText.SafeName(role) + "'");
                if (!Actors.IsRoleAllowed(actorKind, role))
                    throw new SkizzyError(ErrorKind.PermissionDenied, "actor kind '" + actorKind + "' cannot take role '" + role + "'", "role_clamp",
                        "Pick a role allowed for this actor kind (15 §2).", JsonValue.Obj().Set("role", role));

                var perm = _perm;
                var view = p.GetObj("view");
                bool god = perm.MayUseGodView(role);
                bool fog = !god;
                if (view != null && view.Has("fog"))
                {
                    fog = view.GetBool("fog", true);
                    if (!fog && !god)
                        throw new SkizzyError(ErrorKind.PermissionDenied, "role '" + role + "' may not use god view", "god_view",
                            "Connect with view.fog=true, or ask the human to allow commander god view in the permissions window.",
                            JsonValue.Obj().Set("role", role).Set("required", "tac.god"));
                }

                int? requested = null;
                if (p.Has("precedence")) requested = p.GetInt("precedence");
                else if (actor.Has("precedence")) requested = actor.GetInt("precedence");

                // Resume (03 §2): same session id and actor within the window.
                var resume = p.GetObj("resume");
                Session target = s;
                bool resumed = false;
                string actorId = actor.GetString("id");
                if (resume != null && resume.GetString("session") != null)
                {
                    var old = FindSession(resume.GetString("session"));
                    if (old != null && old != s && !old.IsAttached && (actorId == null || actorId == old.ActorId))
                    {
                        lock (_sessions)
                        {
                            _sessions.Remove(s);
                            old.Conn = c;
                            old.DetachedAtMs = 0;
                            old.Authenticated = true;
                        }
                        c.SessionTag = old;
                        target = old;
                        resumed = true;
                        if (resume.Has("fromEventCursor")) old.EventCursor = resume.GetLong("fromEventCursor");
                    }
                }

                target.ClientName = CompactText.SafeName(client.GetString("name", "unknown"), 64);
                target.ClientVersion = CompactText.SafeName(client.GetString("version", ""), 32);
                target.ClientKind = clientKind;
                target.McpClient = client.GetString("mcpClient") != null ? CompactText.SafeName(client.GetString("mcpClient"), 32) : null;
                target.ActorKind = actorKind;
                target.RequestedRole = role;
                target.ActorId = actorId != null ? CompactText.SafeName(actorId, 64) : "a:" + target.ClientName + "-" + target.Id.Substring(2, 4);
                target.Side = actor.GetString("side") != null ? CompactText.SafeName(actor.GetString("side"), 32) : null;
                target.Display = actor.GetString("display") != null ? CompactText.SafeName(actor.GetString("display"), 48) : target.ActorId;
                target.Team = view != null && view.GetString("team") != null ? CompactText.SafeName(view.GetString("team"), 32) : target.Side;
                target.Fog = fog;
                target.Features.Clear();
                var feats = p.GetStringList("features");
                if (feats != null) foreach (var f in feats) target.Features.Add(f);

                // Seat (M0: informational, 09 §1.1): the first llm commander per side holds it.
                target.Role = role;
                if (role == "commander")
                {
                    bool held = false;
                    lock (_sessions)
                        foreach (var o in _sessions)
                            if (o != target && o.IsAttached && o.SeatHolder && o.Side == target.Side) held = true;
                    if (held) { target.Role = "advisor"; target.SeatHolder = false; }
                    else target.SeatHolder = true;
                }
                target.Precedence = perm.EffectivePrecedence(target.Role, requested);
                target.HelloDone = true;

                var result = JsonValue.Obj()
                    .Set("protocol", ProtocolInfo.Name)
                    .Set("version", ProtocolInfo.Version)
                    .Set("hostVersion", ProtocolInfo.HostVersion)
                    .Set("session", target.Id)
                    .Set("resumed", resumed)
                    .Set("actor", target.ActorId)
                    .Set("role", target.Role)
                    .Set("features", FeatureList(feats))
                    .Set("limits", LimitsJson())
                    .Set("catalogRevision", CatalogRevision)
                    .Set("areas", AreasJson(perm))
                    .Set("grants", GrantsJson(perm, target))
                    .Set("precedence", target.Precedence)
                    .Set("team", target.Team)
                    .Set("fog", target.Fog)
                    .Set("seat", JsonValue.Obj().Set("side", target.Side).Set("holder", SeatHolderOf(target.Side)))
                    .Set("eventCursor", Events.LastCursor)
                    .Set("killSwitch", Kill.ToJson());
                if (target.Role != role) result.Set("roleNote", "the commander seat for this side is held; connected as advisor (precedence 0)");
                SendResult(target, id, result);
                Events.Emit("host.session_opened", "system",
                    target.ActorId + " connected via " + target.ClientName + " as " + target.Role + (resumed ? " (resumed)" : ""),
                    JsonValue.Obj().Set("session", target.Id).Set("actor", target.ActorId).Set("role", target.Role).Set("client", target.ClientName));
                AuditCall(target, "session.hello", null, null, "allow", "hello", "n/a", null, 0, 0);
            }
            catch (SkizzyError e)
            {
                SendError(c, id, e);
            }
        }

        private string SeatHolderOf(string side)
        {
            lock (_sessions)
                foreach (var o in _sessions)
                    if (o.IsAttached && o.SeatHolder && o.Side == side) return o.ActorId;
            return null;
        }

        private JsonValue FeatureList(List<string> requested)
        {
            var a = JsonValue.Arr();
            foreach (var f in HostFeatures)
                if (requested == null || requested.Count == 0 || requested.Contains(f) || f == "confirm.game") a.Add(f);
            return a;
        }

        public JsonValue LimitsJson()
        {
            var l = Settings.Limits;
            return JsonValue.Obj()
                .Set("maxMessageBytes", l.MaxMessageBytes)
                .Set("maxJsonDepth", l.MaxJsonDepth)
                .Set("maxInflight", l.MaxInflight)
                .Set("maxSubscriptions", l.MaxSubscriptions)
                .Set("maxConnections", l.MaxConnections)
                .Set("outboundQueueBytes", l.OutboundQueueBytes)
                .Set("idleTimeoutS", l.IdleTimeoutS)
                .Set("pingIntervalS", l.PingIntervalS)
                .Set("callsPerMinute", l.CallsPerMinute);
        }

        private static JsonValue AreasJson(PermissionEngine perm)
        {
            var o = JsonValue.Obj();
            foreach (var a in Skizzy.Protocol.Permissions.PermissionConfig.KnownAreas) o.Set(a, perm.Config.IsAreaEnabled(a));
            return o;
        }

        private static JsonValue GrantsJson(PermissionEngine perm, Session s)
        {
            var o = JsonValue.Obj();
            foreach (var lvl in SafetyNames.All)
            {
                var d = perm.Evaluate("*", "Skizzy", lvl, s.Role, s.ClientName, s.Grants);
                o.Set(SafetyNames.Name(lvl), SafetyNames.PolicyName(d.Policy));
            }
            return o;
        }

        // ------------------------------------------------------------------ protocol methods

        private bool TryProtocolMethod(Session s, JsonValue id, string method, JsonValue p, bool notification)
        {
            switch (method)
            {
                case "session.hello":
                    throw new SkizzyError(ErrorKind.InvalidRequest, "session.hello was already called on this connection", "duplicate_hello",
                        "Open a new connection to change actor.");
                case "session.heartbeat":
                    if (!notification) SendResult(s, id, JsonValue.Obj().Set("ok", true).Set("session", s.Id));
                    return true;
                case "session.bye":
                    if (!notification) SendResult(s, id, JsonValue.Obj().Set("ok", true));
                    s.Conn?.Close(WsClose.Normal, "bye");
                    lock (_sessions) _sessions.Remove(s);
                    return true;
                case "catalog.get":
                    SendResult(s, id, CatalogGet(p));
                    return true;
                case "catalog.search":
                    SendResult(s, id, CatalogSearch(p));
                    return true;
                case "catalog.describe":
                    {
                        string name = p.GetString("name");
                        var def = name != null ? Catalog.Find(name) : null;
                        if (def == null) throw new SkizzyError(ErrorKind.NotFound, "no capability '" + CompactText.SafeName(name ?? "") + "'", "unknown",
                            "Search with catalog.search.", JsonValue.Obj().Set("handle", name));
                        var d = def.ToDescriptor();
                        var dec = _perm.Evaluate(def.Name, def.Mod, def.Safety, s.Role, s.ClientName, s.Grants);
                        d.Set("policy", SafetyNames.PolicyName(dec.Policy)).Set("policySource", dec.Source).Set("available", HasHandler(def.Name))
                         .Set("inScene", def.InScene(Meta.Scene));
                        SendResult(s, id, JsonValue.Obj().Set("capability", d));
                        return true;
                    }
                case "op.get":
                    {
                        var op = FindOp(p);
                        SendResult(s, id, op.ToJson(true));
                        return true;
                    }
                case "op.wait":
                    {
                        var op = FindOp(p);
                        int timeoutMs = (int)(Clamp(p.GetDouble("timeoutS", 30), 0, 300) * 1000);
                        Ops.Wait(op, timeoutMs, o => SendResult(s, id, o.ToJson(true)));
                        return true;
                    }
                case "op.cancel":
                    {
                        var op = FindOp(p);
                        op.CancelRequested = true;
                        Dispatcher.Wake();
                        SendResult(s, id, JsonValue.Obj().Set("op", op.Id).Set("cancelRequested", true).Set("state", op.State));
                        return true;
                    }
                case "op.list":
                    {
                        var list = new List<Operation>();
                        Ops.List(list);
                        var arr = JsonValue.Arr();
                        foreach (var o in list) arr.Add(o.ToJson(false));
                        SendResult(s, id, JsonValue.Obj().Set("ops", arr).Set("total", list.Count));
                        return true;
                    }
                case "stream.subscribe":
                case "stream.modify":
                case "stream.unsubscribe":
                case "stream.list":
                    Streams.HandleProtocol(s, id, method, p);
                    return true;
                case "events.subscribe":
                    {
                        var names = p.GetStringList("names");
                        s.EventGlobs = names != null && names.Count > 0 ? names.ToArray() : new[] { "*" };
                        SendResult(s, id, JsonValue.Obj().Set("subscribed", JsonValue.ArrOf(s.EventGlobs)).Set("cursor", Events.LastCursor));
                        return true;
                    }
                case "events.unsubscribe":
                    s.EventGlobs = null;
                    SendResult(s, id, JsonValue.Obj().Set("ok", true));
                    return true;
            }
            return false;
        }

        private Operation FindOp(JsonValue p)
        {
            string opId = p.GetString("op");
            var op = opId != null ? Ops.Find(opId) : null;
            if (op == null) throw SkizzyError.NotFound(opId ?? "", "unknown", "Unknown operation id; list them with op.list.");
            return op;
        }

        private static double Clamp(double v, double lo, double hi) { return v < lo ? lo : v > hi ? hi : v; }

        private JsonValue CatalogGet(JsonValue p)
        {
            string toolset = p.GetString("toolset");
            var arr = JsonValue.Arr();
            foreach (var d in Catalog.All)
            {
                if (toolset != null && Array.IndexOf(d.Toolsets, toolset) < 0) continue;
                var desc = d.ToDescriptor();
                desc.Set("available", HasHandler(d.Name));
                arr.Add(desc);
            }
            var methods = JsonValue.ArrOf(ProtocolMethods);
            return JsonValue.Obj().Set("catalogRevision", CatalogRevision).Set("capabilities", arr).Set("methods", methods).Set("total", arr.Count);
        }

        private JsonValue CatalogSearch(JsonValue p)
        {
            string q = (p.GetString("query") ?? p.GetString("q") ?? "").Trim();
            int limit = Math.Max(1, Math.Min(50, p.GetInt("limit", 10)));
            var scored = new List<KeyValuePair<int, CapabilityDef>>();
            string[] words = q.ToLowerInvariant().Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var d in Catalog.All)
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
            return JsonValue.Obj().Set("results", arr).Set("total", scored.Count).Set("truncated", scored.Count > limit);
        }

        // ------------------------------------------------------------------ capability pipeline (spec 0.2 §6.3)

        /// <summary>
        /// resolve → validate → scene → kill switch → permission → rate limit → (preconditions in handler) → dispatch →
        /// execute → audit. Also used by the in-game console with a local human session.
        /// </summary>
        public void Invoke(Session s, JsonValue id, string method, CapabilityDef def, JsonValue args, bool notification)
        {
            var ctx = new CallContext
            {
                Host = this,
                Session = s,
                Def = def,
                RequestId = id,
                Method = method,
                StartTicks = Clock.Ticks,
            };
            ctx.OnDone = (cx, result, err) => OnCallDone(cx, result, err, notification);

            // validate (schema + defaults)
            var errors = SchemaValidator.Validate(args, def.InputSchema);
            if (errors.Count > 0)
            {
                ctx.Decision = "n/a";
                ctx.Fail(new SkizzyError(ErrorKind.InvalidParams, "invalid arguments for " + def.Name + ": " + errors[0].Pointer + " " + errors[0].Message, null,
                    "Fix the listed fields; the schema is in catalog.describe.", JsonValue.Obj().Set("errors", SchemaValidator.ErrorsToJson(errors))));
                return;
            }
            ctx.Params = SchemaValidator.ApplyDefaults(args.Clone(), def.InputSchema);

            if (Faults.IsDisabled(def.Name))
            {
                ctx.Fail(SkizzyError.Precondition("auto_disabled", def.Name + " was disabled after repeated faults",
                    "Re-enable it in the in-game Capabilities window after checking KSP.log."));
                return;
            }
            if (!HasHandler(def.Name))
            {
                ctx.Fail(new SkizzyError(ErrorKind.PreconditionFailed, def.Name + " is not available in this host build", "api_missing",
                    "This capability needs the game (or a later milestone)."));
                return;
            }

            // scene
            var meta = Meta;
            if (!def.InScene(meta.Scene))
            {
                ctx.Fail(SkizzyError.Unavailable(meta.Scene, def.Scenes, meta.SceneTransition ? "transition" : "wrong_scene"));
                return;
            }

            // kill switch
            if (Kill.Engaged && def.Safety != Safety.Read && !def.KillSwitchExempt)
            {
                ctx.Fail(new SkizzyError(ErrorKind.KillSwitchActive, "the kill switch is engaged", null,
                    "Only reads work until the human releases the kill switch in game.",
                    JsonValue.Obj().Set("since", Kill.Since).Set("by", Kill.By)));
                return;
            }

            // inflight + session rate
            if (Interlocked.Increment(ref s.Inflight) > Settings.Limits.MaxInflight)
            {
                Interlocked.Decrement(ref s.Inflight);
                ctx.Fail(SkizzyError.Busy(200, "more than " + Settings.Limits.MaxInflight + " calls in flight"));
                return;
            }
            ctx.InflightCounted = true;
            long wait = s.Calls.TryTake(Clock.NowMs);
            if (wait > 0)
            {
                ctx.Fail(SkizzyError.RateLimited(wait, s.Calls.Description));
                return;
            }

            // permission
            var perm = _perm;
            if (!perm.Config.IsAreaEnabled(def.Area))
            {
                ctx.Decision = "deny";
                ctx.DecisionSource = "AREAS " + def.Area;
                ctx.Fail(Denied(def, s, "policy", "AREAS " + def.Area + " = off", "The '" + def.Area + "' area is switched off in the in-game permissions window."));
                return;
            }
            var dec = perm.Evaluate(def.Name, def.Mod, def.Safety, s.Role, s.ClientName, s.Grants);
            ctx.DecisionSource = dec.Source;
            if (dec.Policy == Policy.Deny)
            {
                ctx.Decision = "deny";
                ctx.Fail(Denied(def, s, dec.DenyReason ?? "policy", dec.Source, null));
                return;
            }

            // per-capability rate limit (permission rules)
            string rate = dec.RateLimit ?? perm.FindRate(def.Name, def.Mod);
            if (rate != null && rate != "none")
            {
                RateLimiter rl;
                lock (s.CapLimits)
                {
                    if (!s.CapLimits.TryGetValue(def.Name, out rl) && RateLimiter.TryParseSpec(rate, out int cnt, out double per))
                        s.CapLimits[def.Name] = rl = new RateLimiter(cnt, per, rate);
                }
                if (rl != null)
                {
                    long w = rl.TryTake(Clock.NowMs);
                    if (w > 0) { ctx.Fail(SkizzyError.RateLimited(w, def.Name + " " + rate)); return; }
                }
            }

            if (dec.Policy == Policy.Ask)
            {
                ctx.Decision = "ask";
                AskThenDispatch(ctx, null);
                return;
            }
            Dispatch(ctx);
        }

        private SkizzyError Denied(CapabilityDef def, Session s, string reason, string source, string hint)
        {
            if (hint == null)
            {
                switch (reason)
                {
                    case "role_clamp": hint = "Role '" + s.Role + "' does not allow " + SafetyNames.Name(def.Safety) + " capabilities."; break;
                    case "client_cap": hint = "A CLIENT rule caps what '" + s.ClientName + "' may do."; break;
                    default: hint = "Denied by the permission file; ask the human with permissions.request if you need it."; break;
                }
            }
            return new SkizzyError(ErrorKind.PermissionDenied, def.Name + " denied (" + reason + ")", reason, hint,
                JsonValue.Obj().Set("capability", def.Name).Set("mod", def.Mod).Set("required", SafetyNames.Name(def.Safety))
                    .Set("policy", "deny").Set("source", source).Set("role", s.Role));
        }

        /// <summary>Shows the in-game prompt; continues on the answer (PLAN §6.6).</summary>
        private void AskThenDispatch(CallContext ctx, string aiReason)
        {
            var s = ctx.Session;
            var req = new PromptRequest
            {
                SessionId = s.Id,
                ClientName = s.ClientName,
                ActorId = s.ActorId,
                Role = s.Role,
                Def = ctx.Def,
                Capability = ctx.Def.Name,
                Summary = SummarizeArgs(ctx.Params),
                AiReason = aiReason ?? ctx.Params.GetString("note") ?? ctx.Params.GetString("reason"),
                TimeoutS = Settings.ConfirmTimeoutS,
            };
            Dispatcher.Wake();
            Prompts.Show(req, a =>
            {
                switch (a)
                {
                    case PromptAnswer.AllowSession:
                        lock (s.Grants) s.Grants.Add(ctx.Def.Name);
                        goto case PromptAnswer.AllowOnce;
                    case PromptAnswer.AllowAlways:
                        PersistGrant(ctx.Def.Name);
                        goto case PromptAnswer.AllowOnce;
                    case PromptAnswer.AllowOnce:
                        ctx.ConfirmedBy = "game";
                        ctx.Decision = "ask:granted";
                        Dispatch(ctx);
                        break;
                    default:
                        ctx.Decision = "ask:" + (a == PromptAnswer.Timeout ? "timeout" : "denied");
                        if (a == PromptAnswer.DenyAndPause) EngageKill("human", "denied a prompt and paused AI");
                        ctx.Fail(new SkizzyError(ErrorKind.PermissionDenied,
                            ctx.Def.Name + (a == PromptAnswer.Timeout ? ": confirmation timed out" : a == PromptAnswer.Cancelled ? ": confirmation cancelled" : ": the human denied it"),
                            "policy", a == PromptAnswer.Timeout ? "Nobody answered the in-game prompt within " + Settings.ConfirmTimeoutS + " s." : "The human said no; do not retry without asking.",
                            JsonValue.Obj().Set("capability", ctx.Def.Name).Set("policy", "ask").Set("source", ctx.DecisionSource).Set("answer", a.ToString())));
                        break;
                }
            });
        }

        public void PersistGrantPublic(string capability) { PersistGrant(capability); }

        private void PersistGrant(string capability)
        {
            try
            {
                var cfg = Skizzy.Protocol.Permissions.PermissionConfig.Parse(_perm.Config.Serialize(), new List<string>());
                cfg.Caps.Add(new CapRule { NamePattern = capability, HasPolicy = true, Policy = Policy.Allow });
                SavePermissions(cfg);
            }
            catch (Exception e) { Log.Warn("could not persist grant: " + e.Message); }
        }

        private static string SummarizeArgs(JsonValue p)
        {
            if (p == null || p.Count == 0) return "(no arguments)";
            string s = p.ToString();
            return CompactText.SafeName(s, 300);
        }

        private void Dispatch(CallContext ctx)
        {
            var def = ctx.Def;
            if (def.Affinity == Affinity.Main || def.LongRunning)
            {
                Dispatcher.EnqueueMain(new Job { Call = ctx });
                return;
            }
            if (def.Affinity == Affinity.Fixed)
            {
                Dispatcher.EnqueueFixed(new Job { Call = ctx });
                return;
            }
            Execute(ctx);
        }

        private void ExecuteJob(Job j)
        {
            if (j.Action != null)
            {
                try { j.Action(); }
                catch (Exception e) { Log.Error("main-thread action failed: " + e); }
                return;
            }
            long t0 = Clock.Ticks;
            Execute(j.Call);
            j.Call.MainThreadMs += Clock.SinceMs(t0);
        }

        /// <summary>Runs on main thread (main/fixed/long-running) or the network thread (any/snapshot).</summary>
        private void Execute(CallContext ctx)
        {
            var def = ctx.Def;
            try
            {
                if (_opHandlers.TryGetValue(def.Name, out var run))
                {
                    var op = Ops.Start(def.Name, ctx.Session, null);
                    op.Steps = run(ctx, op);
                    ctx.Complete(JsonValue.Obj().Set("op", op.Id).Set("state", op.State).Set("capability", def.Name)
                        .Set("hint", "Long-running: call op.wait {op:\"" + op.Id + "\"} for the result."));
                    return;
                }
                var result = _handlers[def.Name](ctx);
                if (ctx.Deferred) return;
                ctx.Complete(result ?? JsonValue.Obj());
            }
            catch (SkizzyError e) { ctx.Fail(e); }
            catch (Exception e)
            {
                string trace = NewTraceId();
                Log.Error(def.Name + " failed [" + trace + "]: " + e);
                if (Faults.Record(def.Name))
                    Events.Emit("host.auto_disabled", "system", def.Name + " disabled after " + Faults.Threshold + " faults in 60 s",
                        JsonValue.Obj().Set("capability", def.Name));
                ctx.Fail(new SkizzyError(ErrorKind.ProviderError, def.Name + " threw " + e.GetType().Name + ": " + e.Message, null,
                    "This is a host or game error; try again later or check KSP.log (trace " + trace + ").",
                    JsonValue.Obj().Set("provider", def.Provider).Set("exceptionType", e.GetType().FullName).Set("traceId", trace)));
            }
        }

        private void OnCallDone(CallContext ctx, JsonValue result, SkizzyError err, bool notification)
        {
            var s = ctx.Session;
            if (ctx.InflightCounted) Interlocked.Decrement(ref s.Inflight);
            double dur = Clock.SinceMs(ctx.StartTicks);
            Stats.CallMs.Add(dur);
            if (err != null) { s.ErrorCount++; Interlocked.Increment(ref Stats.Errors); }
            if (!notification && s.Conn != null)
            {
                if (IsMainThread && result != null && ctx.Def.Affinity != Affinity.Any)
                {
                    // Serialise off the main thread: results can be large (vessel lists, images).
                    ThreadPool.QueueUserWorkItem(_ => SendResult(s, ctx.RequestId, result));
                }
                else if (err != null) SendError(s.Conn, ctx.RequestId, err);
                else SendResult(s, ctx.RequestId, result);
            }
            AuditCall(s, ctx.Method, ctx.Def, ctx.Params, ctx.Decision, ctx.DecisionSource, ctx.ConfirmedBy, err, dur, ctx.MainThreadMs);
        }

        public void AuditCall(Session s, string method, CapabilityDef def, JsonValue args, string decision, string source, string confirmedBy,
            SkizzyError err, double durMs, double mainMs)
        {
            var e = JsonValue.Obj()
                .Set("tsUtc", Clock.WallUtcIso())
                .Set("ut", Math.Round(Meta.Ut, 3))
                .Set("session", s?.Id)
                .Set("actor", s?.ActorId)
                .Set("role", s?.Role)
                .Set("client", s?.ClientName)
                .Set("method", method)
                .Set("capability", def?.Name)
                .Set("safety", def != null ? SafetyNames.Name(def.Safety) : null);
            if (args != null && args.Count > 0)
            {
                string a = args.ToString();
                e.Set("args", a.Length > 1024 ? a.Substring(0, 1024) + "..." : a);
            }
            e.Set("decision", decision).Set("policySource", source).Set("confirmedBy", confirmedBy)
             .Set("status", err == null ? "ok" : "error");
            if (err != null) e.Set("errorKind", ErrorKinds.Name(err.Kind)).Set("errorReason", err.Reason);
            e.Set("durationMs", Math.Round(durMs, 3)).Set("mainThreadMs", Math.Round(mainMs, 3));
            Audit.Record(e);
        }

        // ------------------------------------------------------------------ kill switch

        public bool EngageKill(string by, string reason)
        {
            if (!Kill.Engage(by, reason, Meta.Ut)) return false;
            Ops.CancelAll("kill_switch");
            Prompts.CancelAll();
            Events.Emit("host.kill_switch", "system", "Kill switch engaged by " + by + (reason != null ? ": " + reason : ""),
                JsonValue.Obj().Set("by", by).Set("reason", reason));
            Dispatcher.Wake();
            return true;
        }

        public bool ReleaseKill(string by)
        {
            if (!Kill.Release()) return false;
            Events.Emit("host.kill_switch_released", "system", "Kill switch released by " + by, JsonValue.Obj().Set("by", by));
            return true;
        }

        // ------------------------------------------------------------------ local (in-game console) session

        private Session _local;

        /// <summary>The in-game console's own session: a human actor, no socket (21 §3).</summary>
        public Session LocalSession
        {
            get
            {
                if (_local == null)
                {
                    _local = new Session(100000)
                    {
                        Authenticated = true,
                        HelloDone = true,
                        ClientName = "in-game console",
                        ClientKind = "map",
                        ActorKind = "human",
                        Role = "human",
                        ActorId = "a:human",
                        Display = "You",
                        Fog = false,
                    };
                    _local.Precedence = _perm.EffectivePrecedence("human", null);
                }
                return _local;
            }
        }

        /// <summary>Runs a capability for the in-game console; <paramref name="done"/> gets the result or error.</summary>
        public void InvokeLocal(string capability, JsonValue args, Action<JsonValue, SkizzyError> done)
        {
            var def = Catalog.Find(capability);
            if (def == null) { done(null, new SkizzyError(ErrorKind.MethodNotFound, "no capability " + capability)); return; }
            var s = LocalSession;
            var ctx = new CallContext { Host = this, Session = s, Def = def, Method = def.Name, StartTicks = Clock.Ticks };
            ctx.OnDone = (cx, r, e) =>
            {
                if (cx.InflightCounted) Interlocked.Decrement(ref s.Inflight);
                AuditCall(s, cx.Method, def, cx.Params, cx.Decision, cx.DecisionSource, cx.ConfirmedBy, e, Clock.SinceMs(cx.StartTicks), cx.MainThreadMs);
                done(r, e);
            };
            InvokeWith(ctx, args ?? JsonValue.Obj());
        }

        private void InvokeWith(CallContext ctx, JsonValue args)
        {
            // Same pipeline minus the network: reuse Invoke's checks by routing through a shim session id.
            var errors = SchemaValidator.Validate(args, ctx.Def.InputSchema);
            if (errors.Count > 0)
            {
                ctx.Fail(new SkizzyError(ErrorKind.InvalidParams, errors[0].Pointer + " " + errors[0].Message, null, null,
                    JsonValue.Obj().Set("errors", SchemaValidator.ErrorsToJson(errors))));
                return;
            }
            ctx.Params = SchemaValidator.ApplyDefaults(args.Clone(), ctx.Def.InputSchema);
            var meta = Meta;
            if (!ctx.Def.InScene(meta.Scene)) { ctx.Fail(SkizzyError.Unavailable(meta.Scene, ctx.Def.Scenes, "wrong_scene")); return; }
            if (!HasHandler(ctx.Def.Name)) { ctx.Fail(SkizzyError.Precondition("api_missing", ctx.Def.Name + " is not available in this build")); return; }
            var dec = _perm.Evaluate(ctx.Def.Name, ctx.Def.Mod, ctx.Def.Safety, "human", "in-game console", null);
            ctx.DecisionSource = dec.Source;
            if (dec.Policy == Policy.Deny) { ctx.Decision = "deny"; ctx.Fail(Denied(ctx.Def, ctx.Session, dec.DenyReason ?? "policy", dec.Source, null)); return; }
            ctx.Decision = dec.Policy == Policy.Ask ? "ask:local" : "allow";
            ctx.ConfirmedBy = dec.Policy == Policy.Ask ? "game" : "n/a"; // the human pressed the button
            Dispatch(ctx);
        }
    }
}
