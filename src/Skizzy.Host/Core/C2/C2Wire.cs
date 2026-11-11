using System;
using System.Collections.Generic;
using System.Globalization;
using Skizzy.C2;
using Skizzy.Host.Core.World;
using Skizzy.Protocol.Errors;
using Skizzy.Protocol.Json;

namespace Skizzy.Host.Core
{
    /// <summary>
    /// The c2.* wire contract (params in, results and typed refusals out), shared by the host's capabilities
    /// (<see cref="C2Host"/>) and by in-process executors that run the same engine without a host (the map app's demo
    /// battle). One parser and one serializer, so every client sees the same JSON whichever executor answers.
    /// </summary>
    public static class C2Wire
    {
        /// <summary>Resolves a unit reference ("v:&lt;id&gt;", a numeric id or a unit name); <paramref name="pointer"/> names the param for errors.</summary>
        public delegate uint UnitResolver(string reference, string pointer);

        public static string Handle(uint id) { return HandleRegistry.VesselHandle(id); }

        public static GeoPoint Pt(JsonValue o) { return new GeoPoint(o.GetDouble("lat"), o.GetDouble("lon")); }

        private static bool Has(JsonValue p, string key)
        {
            var v = p.Get(key);
            return v != null && !v.IsNull;
        }

        /// <summary>"v:&lt;id&gt;", a numeric id, or a unit name the world reports (exact, else one case-insensitive match).</summary>
        public static uint ResolveInWorld(IC2World world, List<UnitState> scratch, string reference, string pointer)
        {
            if (string.IsNullOrEmpty(reference)) throw SkizzyError.InvalidParam(pointer, "empty unit reference");
            uint id;
            if (HandleRegistry.TryParseVessel(reference, out id)) return id;
            if (uint.TryParse(reference, NumberStyles.None, CultureInfo.InvariantCulture, out id)) return id;
            scratch.Clear();
            if (world != null) world.GetUnits(scratch);
            UnitState ci = null;
            int ciCount = 0;
            foreach (var u in scratch)
            {
                if (u.Name == null) continue;
                if (string.Equals(u.Name, reference, StringComparison.Ordinal)) return u.Id;
                if (string.Equals(u.Name, reference, StringComparison.OrdinalIgnoreCase)) { ci = u; ciCount++; }
            }
            if (ciCount == 1) return ci.Id;
            if (ciCount > 1) throw SkizzyError.InvalidParam(pointer, "unit name '" + reference + "' is ambiguous", "Use the handle v:<id>.");
            throw SkizzyError.NotFound(reference, "unknown", "No C2 unit with that name; units are vessels with a BDArmory AI or weapon manager. Use ksp.vessels.list for handles.");
        }

        public static ErrorKind KindOf(string wire)
        {
            switch (wire)
            {
                case "INVALID_PARAMS": return ErrorKind.InvalidParams;
                case "NOT_FOUND": return ErrorKind.NotFound;
                case "PERMISSION_DENIED": return ErrorKind.PermissionDenied;
                case "NOT_IN_VIEW": return ErrorKind.NotInView;
                case "EXECUTOR_UNAVAILABLE": return ErrorKind.ExecutorUnavailable;
                default: return ErrorKind.PreconditionFailed;
            }
        }

        public static JsonValue OutcomesJson(IssueResult r)
        {
            var arr = JsonValue.Arr();
            foreach (var u in r.Units)
            {
                var o = JsonValue.Obj().Set("unit", Handle(u.Unit)).Set("name", u.Name).Set("accepted", u.Accepted);
                if (!u.Accepted) o.Set("code", Orders.Wire(u.Code)).Set("reason", u.Reason);
                if (u.SubstitutedBy.HasValue) o.Set("substitutedBy", Orders.Wire(u.SubstitutedBy.Value));
                arr.Add(o);
            }
            return arr;
        }

        /// <summary>
        /// The c2.order.issue params as an <see cref="OrderRequest"/>: everything except <c>Issuer</c>, <c>Role</c> and
        /// <c>Side</c>, which the caller decides (session identity and the acting-side rules).
        /// </summary>
        public static OrderRequest ParseIssue(JsonValue p, UnitResolver resolve)
        {
            OrderType type;
            string ts = p.GetString("type");
            if (!Orders.TryParse(ts, out type)) throw SkizzyError.InvalidParam("/type", "unknown order type " + ts);
            var req = new OrderRequest
            {
                Type = type,
                Group = p.GetString("group"),
                SideScope = p.GetString("scope", "units") == "side" || type == OrderType.AllAttack,
                Label = p.GetString("label"),
            };
            var units = p.Get("units");
            if (units != null && units.IsArray)
                for (int i = 0; i < units.Count; i++) req.Units.Add(resolve(units[i].AsString(), "/units/" + i));
            switch (p.GetString("domain"))
            {
                case "air": req.DomainFilter = Domain.Air; break;
                case "surface": req.DomainFilter = Domain.Surface; break;
                case "subsurface": req.DomainFilter = Domain.Subsurface; break;
                case "land": req.DomainFilter = Domain.Land; break;
            }
            var point = p.GetObj("point");
            if (point != null) req.Point = Pt(point);
            if (Has(p, "altitudeM")) req.AltitudeM = p.GetDouble("altitudeM");
            var route = p.Get("route");
            if (route != null && route.IsArray)
            {
                req.Route = new List<GeoPoint>();
                for (int i = 0; i < route.Count; i++) req.Route.Add(Pt(route[i]));
            }
            if (Has(p, "target")) req.Target = resolve(p.GetString("target"), "/target");
            var targets = p.Get("targets");
            if (targets != null && targets.IsArray)
            {
                req.Targets = new List<uint>();
                for (int i = 0; i < targets.Count; i++) req.Targets.Add(resolve(targets[i].AsString(), "/targets/" + i));
            }
            var pa = p.GetObj("patternA");
            if (pa != null) req.PatternA = Pt(pa);
            var pb = p.GetObj("patternB");
            if (pb != null) req.PatternB = Pt(pb);
            if (Has(p, "widthKm")) req.WidthKm = p.GetDouble("widthKm");
            req.TurnRight = p.GetBool("turnRight", true);
            if (Has(p, "altStackM")) req.AltStackM = p.GetDouble("altStackM");
            if (Has(p, "base")) req.Base = resolve(p.GetString("base"), "/base");
            switch (p.GetString("weapons"))
            {
                case "free": req.Weapons = WeaponsControl.Free; break;
                case "tight": req.Weapons = WeaponsControl.Tight; break;
                case "hold": req.Weapons = WeaponsControl.Hold; break;
            }
            if (Has(p, "protect")) req.Protect = resolve(p.GetString("protect"), "/protect");
            if (Has(p, "radiusKm")) req.RadiusKm = p.GetDouble("radiusKm");
            if (Has(p, "escortOf")) req.EscortOf = resolve(p.GetString("escortOf"), "/escortOf");
            switch (p.GetString("slot", "ahead"))
            {
                case "abreast": req.Slot = EscortSlot.Abreast; break;
                case "astern": req.Slot = EscortSlot.Astern; break;
                default: req.Slot = EscortSlot.Ahead; break;
            }
            if (Has(p, "distanceKm")) req.DistanceKm = p.GetDouble("distanceKm");
            req.ExtendRange = p.GetBool("extendRange") || type == OrderType.AllAttack;
            return req;
        }

        /// <summary>A whole refusal: the typed error (code, summary and per-unit outcomes in its data).</summary>
        public static SkizzyError Refusal(IssueResult r)
        {
            string code = Orders.Wire(r.Code);
            return new SkizzyError(KindOf(Orders.ErrorKind(r.Code)), r.Message ?? ("order refused: " + code), code.ToLowerInvariant(),
                r.Summary, JsonValue.Obj().Set("code", code).Set("summary", r.Summary).Set("units", OutcomesJson(r)));
        }

        /// <summary>The reply to an accepted c2.order.issue.</summary>
        public static JsonValue IssueReply(IssueResult r)
        {
            var o = r.Order;
            return JsonValue.Obj()
                .Set("order", o.Handle)
                .Set("type", Orders.Wire(o.Type))
                .Set("side", o.Side)
                .Set("status", Orders.Wire(o.Status))
                .Set("accepted", r.AcceptedCount)
                .Set("refused", r.Units.Count - r.AcceptedCount)
                .Set("summary", r.Summary)
                .Set("units", OutcomesJson(r))
                .Set("text", r.Summary ?? (o.Handle + " " + Orders.Wire(o.Type)));
        }

        /// <summary>"o:12" or "12" as an order id.</summary>
        public static int OrderId(string s)
        {
            if (s != null && s.StartsWith("o:", StringComparison.Ordinal)) s = s.Substring(2);
            int id;
            if (!int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out id)) throw SkizzyError.InvalidParam("/order", "not an order handle");
            return id;
        }

        /// <summary>
        /// c2.order.cancel on an engine: refuses an order that already ended, cancels the rest. The caller has checked
        /// the side. Returns the reply.
        /// </summary>
        public static JsonValue Cancel(C2Engine e, Order o, string by)
        {
            // The order's own status rolls up from its unit orders at the next tick; until then all-terminal unit orders count.
            string ended = o.IsTerminal ? Orders.Wire(o.Status) : null;
            if (ended == null && o.Units.Count > 0)
            {
                bool all = true;
                for (int i = 0; i < o.Units.Count && all; i++) all = o.Units[i].IsTerminal;
                if (all) ended = Orders.Wire(o.Units[0].Status);
            }
            if (ended != null)
                throw SkizzyError.Precondition("already_" + ended, "order " + o.Handle + " is already " + ended);
            bool ok = e.Cancel(o.Id, by); // unit orders end now; the order status rolls up at the next tick
            return JsonValue.Obj().Set("order", o.Handle).Set("cancelled", ok).Set("status", ok ? "cancelled" : Orders.Wire(o.Status)).Set("text", o.Handle + (ok ? " cancelled" : " not cancelled"));
        }

        public static DoctrineLevel Level(string s)
        {
            return s == "unit" ? DoctrineLevel.Unit : s == "group" ? DoctrineLevel.Group : DoctrineLevel.Side;
        }

        public static List<string> Strings(JsonValue arr)
        {
            var l = new List<string>();
            if (arr != null && arr.IsArray) for (int i = 0; i < arr.Count; i++) { var s = arr[i].AsString(); if (s != null) l.Add(s); }
            return l;
        }

        /// <summary>c2.doctrine.set <c>values</c> as engine values (strings, numbers, booleans).</summary>
        public static Dictionary<string, object> DoctrineValues(JsonValue vals)
        {
            var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (vals == null) throw SkizzyError.InvalidParam("/values", "values is required");
            for (int i = 0; i < vals.Count; i++)
            {
                var v = vals.ValueAt(i);
                object o;
                switch (v.Kind)
                {
                    case JsonKind.String: o = v.AsString(); break;
                    case JsonKind.Bool: o = v.AsBool(); break;
                    case JsonKind.Number: o = v.AsDouble(); break;
                    default: throw SkizzyError.InvalidParam("/values/" + vals.KeyAt(i), "doctrine values are strings, numbers or booleans");
                }
                dict[vals.KeyAt(i)] = o;
            }
            return dict;
        }

        /// <summary>The typed error for <see cref="C2Engine.SetDoctrine"/> errors (a lock wins over a bad value).</summary>
        public static SkizzyError DoctrineError(List<string> errors)
        {
            var arr = JsonValue.ArrOf(errors.ToArray());
            foreach (var err in errors)
                if (err.StartsWith("locked_by:", StringComparison.Ordinal) || err.Contains(" locked_by:"))
                {
                    int k = err.IndexOf("locked_by:", StringComparison.Ordinal);
                    string reason = err.Substring(k).Split(' ')[0];
                    return new SkizzyError(ErrorKind.PreconditionFailed, err, reason, "A higher level locked this key; change it there or unset the lock.", JsonValue.Obj().Set("errors", arr));
                }
            return new SkizzyError(ErrorKind.InvalidParams, errors[0], "doctrine", null, JsonValue.Obj().Set("errors", arr));
        }

        public static SkizzyError UnknownUnit(uint id)
        {
            return SkizzyError.NotFound(Handle(id), "unknown", "The C2 engine does not know this unit yet (it appears after the next tick if it has a BDArmory AI or weapon manager).");
        }

        public static JsonValue DoctrineExplained(uint id, List<DoctrineExplain> lines)
        {
            var arr = JsonValue.Arr();
            var sb = new System.Text.StringBuilder();
            foreach (var l in lines)
            {
                arr.Add(JsonValue.Obj().Set("key", l.Key).Set("value", l.Value).Set("from", l.From).Set("locked", l.Locked));
                sb.Append(l.Key).Append('=').Append(l.Value).Append(" (").Append(l.From).Append(l.Locked ? ", locked" : "").Append(")\n");
            }
            return JsonValue.Obj().Set("unit", Handle(id)).Set("doctrine", arr).Set("text", sb.ToString());
        }

        private static JsonValue UnitOrderJson(UnitOrder u)
        {
            if (u == null) return JsonValue.Null;
            return JsonValue.Obj()
                .Set("order", u.Parent != null ? u.Parent.Handle : null)
                .Set("type", Orders.Wire(u.Type))
                .Set("status", Orders.Wire(u.Status))
                .Set("reason", u.Reason)
                .Set("phase", u.Phase);
        }

        private static string Lower(object o) { return o.ToString().ToLowerInvariant(); }

        /// <summary>The c2.unit.status reply for an engine unit view.</summary>
        public static JsonValue UnitStatus(UnitView v)
        {
            var route = JsonValue.Arr();
            foreach (var pt in v.RoutePoints) route.Add(JsonValue.Obj().Set("lat", Math.Round(pt.Lat, 5)).Set("lon", Math.Round(pt.Lon, 5)));
            var sec = JsonValue.Arr();
            foreach (var s in v.Secondary) sec.Add(Handle(s));
            var allows = JsonValue.Arr();
            foreach (var s in v.FilterAllows) allows.Add(Handle(s));
            var o = JsonValue.Obj()
                .Set("unit", Handle(v.Id)).Set("name", v.Name).Set("side", v.Side).Set("alive", v.Alive)
                .Set("task", v.Task)
                .Set("movement", UnitOrderJson(v.Movement)).Set("movementSuspended", v.MovementSuspended)
                .Set("engagement", UnitOrderJson(v.Engagement))
                .Set("hold", v.Hold).Set("marshalSlot", v.MarshalSlot).SetFinite("holdAltM", v.HoldAltM)
                .Set("fireAuthority", Lower(v.FireAuthority)).Set("filterInstalled", v.FilterInstalled).Set("filterAllows", allows)
                .Set("overrideTarget", v.OverrideTarget != 0 ? Handle(v.OverrideTarget) : null).Set("overrideSticky", v.OverrideSticky)
                .Set("secondary", sec)
                .Set("route", v.Route).Set("routeId", v.RouteId).Set("routePoints", route)
                .Set("yieldToCombat", v.YieldToCombat).Set("leashM", v.LeashM)
                .Set("divert", v.Divert != 0 ? Handle(v.Divert) : null)
                .Set("selfDefence", v.SelfDefence != 0 ? Handle(v.SelfDefence) : null)
                .Set("advance", v.Advance != 0 ? Handle(v.Advance) : null)
                .Set("vipThreat", v.VipThreat != 0 ? Handle(v.VipThreat) : null)
                .Set("vip", v.Vip).Set("rangeExtended", v.RangeExtended)
                .Set("weaponsControl", Lower(v.WeaponsControl)).Set("onContact", Lower(v.OnContact));
            o.Set("text", v.Name + ": " + (v.Task ?? "idle") + (v.Movement != null ? ", " + Orders.Wire(v.Movement.Type) + " " + Orders.Wire(v.Movement.Status) : "") +
                          (v.Engagement != null ? ", " + Orders.Wire(v.Engagement.Type) + " " + Orders.Wire(v.Engagement.Status) : "") +
                          ", weapons " + Lower(v.WeaponsControl));
            return o;
        }
    }
}
