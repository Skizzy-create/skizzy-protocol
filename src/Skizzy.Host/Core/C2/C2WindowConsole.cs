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
    /// The host's console for the in-game C2 window (<see cref="C2Runtime.SetConsole"/>): every command the window
    /// sends is audited (audit JSONL, method <c>c2.window.*</c>, actor = the in-game human session) and logged to
    /// host.log, refused while the kill switch is engaged (as the in-game capability console is), and hands ticking to
    /// the host's 5 Hz slot (<see cref="C2Host.Engage"/>) so the pump, its timing and the kill switch drive the engine.
    /// Reads go straight to the inner console. Main thread only.
    /// </summary>
    public sealed class C2WindowConsole : IC2Console
    {
        private readonly HostCore _h;
        private readonly IC2Console _inner;

        public C2WindowConsole(HostCore h, IC2Console inner)
        {
            _h = h ?? throw new ArgumentNullException(nameof(h));
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public IC2Console Inner => _inner;

        // ------------------------------------------------------------------ reads

        public bool Ready => _inner.Ready;
        public string Status => _h.Kill.Engaged ? "Kill switch engaged: C2 commands are refused until it is released. " + _inner.Status : _inner.Status;
        public HookSet Hooks => _inner.Hooks;
        public string Owner => _inner.Owner;
        public int ReportSerial => _inner.ReportSerial;
        public double Now => _inner.Now;
        public void ListSides(List<string> into) { _inner.ListSides(into); }
        public void ListUnits(List<UnitBrief> into, string side) { _inner.ListUnits(into, side); }
        public UnitView GetUnitView(uint unit) { return _inner.GetUnitView(unit); }
        public DoctrineValues SideDoctrine(string side) { return _inner.SideDoctrine(side); }
        public DoctrineValues EffectiveDoctrine(uint unit) { return _inner.EffectiveDoctrine(unit); }
        public bool IsSideManaged(string side) { return _inner.IsSideManaged(side); }
        public void CopyReports(List<C2Event> into, int max) { _inner.CopyReports(into, max); }

        // ------------------------------------------------------------------ commands

        private const string KillText = "kill switch engaged: C2 commands are refused";

        public IssueResult Issue(OrderRequest request)
        {
            long t0 = Clock.Ticks;
            var args = JsonValue.Obj().Set("type", request != null ? Orders.Wire(request.Type) : null).Set("side", request != null ? request.Side : null)
                .Set("units", request != null ? request.Units.Count : 0).Set("scope", request != null && request.SideScope ? "side" : "units");
            if (_h.Kill.Engaged)
            {
                var res = new IssueResult { Code = RefusalCode.NotPermitted, Message = KillText };
                res.Summary = (request != null ? Orders.Wire(request.Type) : "order") + " refused: " + KillText + ".";
                Audit("c2.window.order.issue", args, Killed(), t0);
                return res;
            }
            var r = _inner.Issue(request);
            if (r.Accepted)
            {
                args.Set("order", r.Order.Handle).Set("accepted", r.AcceptedCount);
                _h.C2.Engage();
                Audit("c2.window.order.issue", args, null, t0);
            }
            else
            {
                string code = Orders.Wire(r.Code);
                Audit("c2.window.order.issue", args, new SkizzyError(ErrorKind.PreconditionFailed, r.Message ?? code, code.ToLowerInvariant()), t0);
            }
            return r;
        }

        public bool Cancel(int orderId, string by)
        {
            long t0 = Clock.Ticks;
            var args = JsonValue.Obj().Set("order", "o:" + orderId.ToString(CultureInfo.InvariantCulture));
            if (_h.Kill.Engaged) { Audit("c2.window.order.cancel", args, Killed(), t0); return false; }
            bool ok = _inner.Cancel(orderId, by);
            if (ok) _h.C2.Engage();
            Audit("c2.window.order.cancel", args.Set("cancelled", ok), null, t0);
            return ok;
        }

        public bool Release(uint unit, string by)
        {
            // Releasing hands a unit back to BDArmory: allowed even with the kill switch engaged (it only removes control).
            long t0 = Clock.Ticks;
            bool ok = _inner.Release(unit, by);
            if (ok && !_h.Kill.Engaged) _h.C2.Engage();
            Audit("c2.window.unit.release", JsonValue.Obj().Set("unit", HandleRegistry.VesselHandle(unit)).Set("released", ok), null, t0);
            return ok;
        }

        public List<string> SetDoctrine(DoctrineLevel level, string target, IDictionary<string, object> values)
        {
            long t0 = Clock.Ticks;
            var args = JsonValue.Obj().Set("level", Level(level)).Set("target", target).Set("values", Values(values));
            if (_h.Kill.Engaged)
            {
                Audit("c2.window.doctrine.set", args, Killed(), t0);
                return new List<string> { KillText };
            }
            var errors = _inner.SetDoctrine(level, target, values);
            bool ok = errors == null || errors.Count == 0;
            if (ok) _h.C2.Engage();
            Audit("c2.window.doctrine.set", args, ok ? null : new SkizzyError(ErrorKind.InvalidParams, errors[0], "doctrine"), t0);
            return errors;
        }

        public void UnsetDoctrine(DoctrineLevel level, string target, IList<string> keys)
        {
            // Unsetting only moves back toward the defaults: allowed with the kill switch engaged.
            long t0 = Clock.Ticks;
            _inner.UnsetDoctrine(level, target, keys);
            if (!_h.Kill.Engaged) _h.C2.Engage();
            var k = JsonValue.Arr();
            if (keys != null) foreach (var s in keys) k.Add(s);
            Audit("c2.window.doctrine.unset", JsonValue.Obj().Set("level", Level(level)).Set("target", target).Set("keys", k), null, t0);
        }

        public void SetSideManaged(string side, bool managed)
        {
            long t0 = Clock.Ticks;
            var args = JsonValue.Obj().Set("side", side).Set("managed", managed);
            if (managed && _h.Kill.Engaged) { Audit("c2.window.side.managed", args, Killed(), t0); return; }
            _inner.SetSideManaged(side, managed);
            if (!_h.Kill.Engaged) _h.C2.Engage();
            Audit("c2.window.side.managed", args, null, t0);
        }

        // ------------------------------------------------------------------ helpers

        private static SkizzyError Killed()
        {
            return new SkizzyError(ErrorKind.KillSwitchActive, KillText, "kill_switch");
        }

        private static string Level(DoctrineLevel l) { return l == DoctrineLevel.Unit ? "unit" : l == DoctrineLevel.Group ? "group" : "side"; }

        private static JsonValue Values(IDictionary<string, object> values)
        {
            var o = JsonValue.Obj();
            if (values == null) return o;
            foreach (var kv in values)
            {
                if (kv.Value is bool b) o.Set(kv.Key, b);
                else if (kv.Value is string s) o.Set(kv.Key, s);
                else if (kv.Value is IConvertible c) o.Set(kv.Key, c.ToDouble(CultureInfo.InvariantCulture));
                else o.Set(kv.Key, kv.Value != null ? kv.Value.ToString() : null);
            }
            return o;
        }

        private void Audit(string method, JsonValue args, SkizzyError err, long t0)
        {
            double ms = Clock.SinceMs(t0);
            string decision = err != null && err.Kind == ErrorKind.KillSwitchActive ? "deny:kill_switch" : "allow";
            try { _h.AuditCall(_h.LocalSession, method, null, args, decision, "c2_window", "n/a", err, ms, ms); }
            catch (Exception e) { _h.Log.Warn("c2: window audit failed: " + e.Message); }
            _h.Log.Info("c2: window " + method.Substring("c2.window.".Length) + " " + args + (err != null ? " -> " + err.Kind + ": " + err.Message : ""));
        }
    }
}
