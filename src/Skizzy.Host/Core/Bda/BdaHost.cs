using System;
using System.Collections.Generic;
using System.Globalization;
using Skizzy.C2;
using Skizzy.C2.Tactical;
using Skizzy.Host.Core.World;
using Skizzy.Protocol.Errors;
using Skizzy.Protocol.Json;

namespace Skizzy.Host.Core.Bda
{
    /// <summary>
    /// The bda.* capabilities (M2): weapon manager settings, missile fire through F9, fire authority and EMCON through the
    /// C2 doctrine (the engine applies F3 and radar on/off through the BDArmory binding), F2/F4 for units the C2 engine
    /// does not manage, guns, countermeasures, ECM, AI modules and BDArmory competitions. Every call goes through the
    /// host pipeline (permissions, kill switch); a missile salvo also stops at the kill switch between shots.
    /// </summary>
    public sealed class BdaHost
    {
        private readonly HostCore _h;
        private IBdaOps _ops;
        private readonly List<UnitState> _units = new List<UnitState>();

        public BdaHost(HostCore h)
        {
            _h = h;
            HookTap = new HookEventPublisher(h);
        }

        /// <summary>Set as the C2 engine's <see cref="C2Engine.Tap"/>: every hook event becomes a bda.hook.* host event.</summary>
        public readonly IHookEvents HookTap;

        public IBdaOps Ops => _ops;
        public bool Attached => _ops != null;

        /// <summary>Main thread: the BDArmory plugin attaches its operations (or a test attaches a fake).</summary>
        public void Attach(IBdaOps ops)
        {
            _ops = ops;
            _h.Log.Info("bda: operations attached (" + (ops != null ? ops.Version : "none") + ")");
        }

        public void Detach(string reason)
        {
            if (_ops == null) return;
            _ops = null;
            _h.Log.Info("bda: operations detached (" + reason + ")");
        }

        private IBdaOps Need()
        {
            var o = _ops;
            if (o == null)
                throw new SkizzyError(ErrorKind.ExecutorUnavailable, "the BDArmory operations are not attached", "bda_not_attached",
                    "bda.* needs the Skizzy BDArmory binding in a flight scene (and BDArmory installed).");
            return o;
        }

        private C2Engine Engine()
        {
            var e = _h.C2.Engine;
            if (e == null)
                throw new SkizzyError(ErrorKind.ExecutorUnavailable, "the C2 engine is not attached", "c2_not_attached",
                    "Fire authority, EMCON and target assignment go through the C2 engine (BDArmory binding in flight).");
            return e;
        }

        private static SkizzyError Refuse(string reason, string message, string hint = null)
        {
            return SkizzyError.Precondition(reason, message, hint);
        }

        // ------------------------------------------------------------------ unit resolution

        private UnitState Unit(uint id)
        {
            _units.Clear();
            var w = _h.C2.World;
            if (w != null) w.GetUnits(_units);
            foreach (var u in _units) if (u.Id == id) return u;
            return null;
        }

        /// <summary>The acting side may command the unit: its own units, any for a human, referee or director.</summary>
        private UnitState Own(CallContext c, uint id, string pointer)
        {
            var u = Unit(id);
            if (u == null) throw SkizzyError.NotFound(HandleRegistry.VesselHandle(id), "unknown", "No BDArmory unit with that id is loaded (destroyed or unloaded?).");
            var s = c.Session;
            if (s != null && s.Side != null && u.Side != s.Side && !Tactical.TacticalHost.Privileged(s))
                throw new SkizzyError(ErrorKind.PermissionDenied, u.Name + " is on " + (u.Side ?? "no team") + "; this session commands " + s.Side, "not_own_side",
                    "Command your own side's units.");
            if (!u.Alive) throw Refuse("unit_destroyed", u.Name + " is destroyed.");
            return u;
        }

        private uint One(CallContext c, string key)
        {
            return _h.C2.ResolveUnit(c.Str(key), "/" + key);
        }

        /// <summary>units[] | unit | group | side: the recipients of a per-unit or per-group setting.</summary>
        private List<uint> Recipients(CallContext c, out string level, out string target)
        {
            var ids = new List<uint>();
            level = "unit";
            target = null;
            var arr = c.Params.GetArr("units");
            if (arr != null)
            {
                for (int i = 0; i < arr.Count; i++) ids.Add(_h.C2.ResolveUnit(arr[i].AsString(), "/units/" + i));
                return ids;
            }
            if (c.Has("unit")) { ids.Add(One(c, "unit")); return ids; }
            _units.Clear();
            var w = _h.C2.World;
            if (w != null) w.GetUnits(_units);
            if (c.Has("group"))
            {
                level = "group";
                target = c.Str("group");
                foreach (var u in _units) if (u.Alive && string.Equals(u.Group, target, StringComparison.OrdinalIgnoreCase)) ids.Add(u.Id);
                if (ids.Count == 0) throw SkizzyError.NotFound(target, "unknown_group", "No live unit is in that group.");
                return ids;
            }
            if (c.Has("side"))
            {
                level = "side";
                target = c.Str("side");
                foreach (var u in _units) if (u.Alive && !u.IsMissile && u.Side == target) ids.Add(u.Id);
                return ids;
            }
            throw SkizzyError.InvalidParam("/units", "give units, unit, group or side");
        }

        private string Name(uint id)
        {
            var u = Unit(id);
            return u != null ? u.Name : HandleRegistry.VesselHandle(id);
        }

        /// <summary>True when the C2 engine drives the unit (orders, a managed side, doctrine or hooks applied): direct F2/F4 writes would fight it.</summary>
        private bool C2Manages(uint id)
        {
            var e = _h.C2.Engine;
            if (e == null) return false;
            var v = e.GetUnitView(id);
            if (v == null) return false;
            return v.Movement != null || v.Engagement != null || v.Hold != null || (v.Side != null && e.IsSideManaged(v.Side)) ||
                   v.FireAuthority != FireAuthorityMode.Free || v.FilterInstalled || v.OverrideTarget != 0 || v.Route != "none";
        }

        // ------------------------------------------------------------------ registration

        public static void Register(HostCore h)
        {
            var b = h.Bda;
            h.Register("bda.hooks", c => b.Hooks(c));
            h.Register("bda.get_weapon_manager", c => b.GetWm(c));
            h.Register("bda.configure_weapon_manager", c => b.ConfigureWm(c));
            h.Register("bda.set_fire_authority", c => b.FireAuthority(c));
            h.Register("bda.set_radar", c => b.Radar(c));
            h.RegisterOp("bda.fire_missile", (c, op) => b.FireMissile(c, op));
            h.Register("bda.set_guns", c => b.Guns(c));
            h.Register("bda.countermeasure", c => b.Countermeasure(c));
            h.Register("bda.set_secondary_targets", c => b.Secondary(c));
            h.Register("bda.set_target_filter", c => b.Filter(c));
            h.Register("bda.assign_target", c => b.AssignTarget(c));
            h.Register("bda.radar_contacts", c => b.RadarContacts(c));
            h.Register("bda.competition.start", c => b.CompetitionStart(c));
            h.Register("bda.competition.stop", c => b.CompetitionStop(c));
            h.Register("bda.competition.state", c => b.CompetitionState(c));
            h.Register("bda.competition.scores", c => b.CompetitionScores(c));
            h.Register("bda.ai.list", c => b.AiList(c));
            h.Register("bda.ai.activate", c => b.AiActivate(c));
            h.Register("bda.ai.set", c => b.AiSet(c));
            h.Register("bda.ai.presets", c => b.AiPresets(c));
            h.Register("bda.ai.apply_preset", c => b.AiApplyPreset(c));
            h.Register("bda.hook.state", c => b.HookStateCap(c));
            h.Register("bda.hook.override", c => b.HookOverride(c));
            h.Register("bda.hook.fire_authority", c => b.HookFireAuthority(c));
            h.Register("bda.hook.waypoints", c => b.HookWaypoints(c));
            h.Register("bda.hook.patrol", c => b.HookPatrol(c));
            h.Register("bda.settings.get", c => b.SettingsGet(c));
            h.Register("bda.settings.set", c => b.SettingsSet(c));
        }

        // ------------------------------------------------------------------ hooks and weapon manager

        /// <summary>The per-hook report (M4 checklist 0.1): F1-F12 (F8 and F11 are not implemented in BDArmory yet), OD1 and later ids.</summary>
        private JsonValue Hooks(CallContext c)
        {
            var ops = _ops;
            var w = _h.C2.World;
            var report = new List<HookPresence>();
            if (ops != null) ops.HookReport(report);
            else if (w != null)
            {
                HookSet hs = w.Hooks;
                foreach (HookSet f in new[] { HookSet.F1, HookSet.F2, HookSet.F3, HookSet.F4, HookSet.F5, HookSet.F6, HookSet.F6b, HookSet.F9, HookSet.F10, HookSet.F12 })
                    report.Add(new HookPresence(f.ToString(), (hs & f) != 0, null));
            }
            var have = JsonValue.Arr();
            var missing = JsonValue.Arr();
            var rows = JsonValue.Arr();
            foreach (var r in report)
            {
                (r.Present ? have : missing).Add(r.Id);
                var o = JsonValue.Obj().Set("id", r.Id).Set("present", r.Present);
                if (r.Members != null) o.Set("members", r.Members);
                rows.Add(o);
            }
            bool attached = ops != null || w != null;
            HookSet hooks = ops != null ? ops.Hooks : w != null ? w.Hooks : HookSet.None;
            return JsonValue.Obj().Set("attached", attached).Set("version", ops != null ? ops.Version : null).Set("hooks", rows).Set("present", have).Set("missing", missing)
                .Set("allOverdriveHooks", hooks == HookSet.All).Set("c2Engine", _h.C2.Attached).Set("tactical", _h.Tactical.Attached)
                .Set("text", !attached ? "BDArmory binding not attached (needs BDArmory and a flight scene)." :
                    "BDArmory " + (ops != null ? ops.Version : "") + " hooks present: " + (have.Count == 0 ? "none (stock BDArmory)" : JoinStr(have)) +
                    (missing.Count > 0 ? "; missing: " + JoinStr(missing) : "") + ".");
        }

        private static string JoinStr(JsonValue arr)
        {
            var l = new List<string>();
            for (int i = 0; i < arr.Count; i++) l.Add(arr[i].AsString());
            return string.Join(", ", l.ToArray());
        }

        private JsonValue GetWm(CallContext c)
        {
            var ops = Need();
            uint id = One(c, "unit");
            var info = new WmInfo();
            if (!ops.GetWeaponManager(id, info)) throw Refuse("no_weapon_manager", Name(id) + " has no BDArmory weapon manager (or is not loaded).");
            return WmJson(info);
        }

        internal JsonValue WmJson(WmInfo i)
        {
            var weapons = JsonValue.Arr();
            var wl = new List<string>();
            foreach (var w in i.Weapons)
            {
                var o = JsonValue.Obj().Set("name", w.Name).Set("kind", w.Kind).Set("count", w.Count).Set("enabled", w.Enabled);
                if (w.RangeMaxM > 0) o.Set("rangeMinM", Math.Round(w.RangeMinM)).Set("rangeMaxM", Math.Round(w.RangeMaxM));
                if (w.Guidance != null) o.Set("guidance", w.Guidance);
                if (!double.IsNaN(w.Ammo)) o.Set("ammo", w.Ammo);
                weapons.Add(o);
                wl.Add(w.Name + " " + w.Kind + (w.Count != 1 ? " x" + w.Count : "") + (w.Enabled ? "" : " off"));
            }
            var tpe = _h.Tactical.Tpe;
            string tgt = null;
            if (i.CurrentTarget != 0)
            {
                var pic = tpe != null && i.Team != null ? tpe.Team(i.Team) : null;
                int n = pic != null ? pic.NumberOf(i.CurrentTarget) : 0;
                tgt = n > 0 ? "T" + n.ToString(CultureInfo.InvariantCulture) : HandleRegistry.VesselHandle(i.CurrentTarget);
            }
            var inv = CultureInfo.InvariantCulture;
            string text = "#wm " + TacticalText.Tok(i.Name) + " team " + (i.Team ?? "-") + " guard " + (i.GuardMode ? "on" : "off") + " auth " + i.FireAuthority.ToUpperInvariant() +
                          (tgt != null ? " tgt " + tgt : "") + "\nranges guard " + (i.GuardRangeM / 1000).ToString("0.#", inv) + "km gun " + (i.GunRangeM / 1000).ToString("0.#", inv) +
                          "km angle " + i.GuardAngleDeg.ToString("0", inv) + " scan " + i.TargetScanIntervalS.ToString("0.#", inv) + "s" +
                          "\nmulti targets " + i.MultiTargetNum + " missile targets " + i.MultiMissileTgtNum + " max on target " + i.MaxMissilesOnTarget +
                          " salvo " + (i.ParallelFire ? i.ParallelSalvoSize + " stagger " + i.ParallelSalvoStaggerS.ToString("0.##", inv) + "s" : "off") +
                          "\nwpn " + (wl.Count == 0 ? "none" : string.Join(" | ", wl.ToArray())) +
                          "\ncm flare " + i.Flares + " chaff " + i.Chaff + " smoke " + i.Smoke + " decoy " + i.Decoys + " | ecm " + i.JammersOn + "/" + i.Jammers +
                          " | radar " + i.RadarsOn + "/" + i.Radars + (i.MissileIncoming ? " | MISSILE INCOMING" : "");
            return JsonValue.Obj().Set("unit", HandleRegistry.VesselHandle(i.Unit)).Set("name", i.Name).Set("team", i.Team).Set("guardMode", i.GuardMode)
                .Set("fireAuthority", i.FireAuthority).Set("guardRangeM", i.GuardRangeM).Set("gunRangeM", i.GunRangeM).Set("guardAngleDeg", i.GuardAngleDeg)
                .Set("targetScanIntervalS", i.TargetScanIntervalS).Set("multiTargetNum", i.MultiTargetNum).Set("multiMissileTgtNum", i.MultiMissileTgtNum)
                .Set("maxMissilesOnTarget", i.MaxMissilesOnTarget).Set("parallelFire", i.ParallelFire).Set("parallelSalvoSize", i.ParallelSalvoSize)
                .Set("parallelSalvoStaggerS", i.ParallelSalvoStaggerS).Set("isVip", i.IsVip).Set("targetMissiles", i.TargetMissiles).Set("weaponChannel", i.WeaponChannel)
                .Set("cmOutsideGuard", i.CmOutsideGuard).Set("currentTarget", tgt).Set("missileIncoming", i.MissileIncoming).Set("underFire", i.UnderFire)
                .Set("outOfAmmo", i.OutOfAmmo).Set("weapons", weapons)
                .Set("countermeasures", JsonValue.Obj().Set("flare", i.Flares).Set("chaff", i.Chaff).Set("smoke", i.Smoke).Set("decoy", i.Decoys))
                .Set("ecm", JsonValue.Obj().Set("jammers", i.Jammers).Set("on", i.JammersOn)).Set("radars", JsonValue.Obj().Set("count", i.Radars).Set("on", i.RadarsOn))
                .Set("text", text);
        }

        private JsonValue ConfigureWm(CallContext c)
        {
            var ops = Need();
            uint id = One(c, "unit");
            var u = Own(c, id, "/unit");
            var p = c.Params;
            var ch = new WmChange();
            if (p.Has("guardMode")) ch.GuardMode = c.Bool("guardMode");
            if (p.Has("team")) ch.Team = c.Str("team");
            if (p.Has("guardRangeM")) ch.GuardRangeM = c.Num("guardRangeM");
            if (p.Has("gunRangeM")) ch.GunRangeM = c.Num("gunRangeM");
            if (p.Has("guardAngleDeg")) ch.GuardAngleDeg = c.Num("guardAngleDeg");
            if (p.Has("targetScanIntervalS")) ch.TargetScanIntervalS = c.Num("targetScanIntervalS");
            if (p.Has("multiTargetNum")) ch.MultiTargetNum = c.Int("multiTargetNum");
            if (p.Has("multiMissileTgtNum")) ch.MultiMissileTgtNum = c.Int("multiMissileTgtNum");
            if (p.Has("maxMissilesOnTarget")) ch.MaxMissilesOnTarget = c.Int("maxMissilesOnTarget");
            if (p.Has("parallelFire")) ch.ParallelFire = c.Bool("parallelFire");
            if (p.Has("parallelSalvoSize")) ch.ParallelSalvoSize = c.Int("parallelSalvoSize");
            if (p.Has("parallelSalvoStaggerS")) ch.ParallelSalvoStaggerS = c.Num("parallelSalvoStaggerS");
            if (p.Has("weaponChannel")) ch.WeaponChannel = c.Int("weaponChannel");
            if (p.Has("isVip")) ch.IsVip = c.Bool("isVip");
            if (p.Has("targetMissiles")) ch.TargetMissiles = c.Bool("targetMissiles");
            if (p.Has("cmOutsideGuard")) ch.CmOutsideGuard = c.Bool("cmOutsideGuard");
            var applied = new List<string>();
            var refused = new List<string>();
            if (p.Has("fireAuthority"))
            {
                SetDoctrine(c, DoctrineLevel.Unit, id.ToString(CultureInfo.InvariantCulture), "weaponsControl", c.Str("fireAuthority"));
                applied.Add("fireAuthority (C2 doctrine weaponsControl, F3)");
            }
            if (p.Has("emcon"))
            {
                SetEmcon(c, new List<uint> { id }, "unit", null, c.Str("emcon"), applied);
            }
            string err = ops.Configure(id, ch, applied, refused);
            if (err != null) throw Refuse(err, u.Name + ": " + err.Replace('_', ' ') + ".");
            var info = new WmInfo();
            var res = JsonValue.Obj().Set("unit", HandleRegistry.VesselHandle(id)).Set("applied", JsonValue.ArrOf(applied.ToArray())).Set("refused", JsonValue.ArrOf(refused.ToArray()));
            if (ops.GetWeaponManager(id, info)) res.Set("now", WmJson(info));
            return res.Set("text", u.Name + ": " + (applied.Count > 0 ? "set " + string.Join(", ", applied.ToArray()) : "nothing changed") +
                                   (refused.Count > 0 ? "; refused " + string.Join(", ", refused.ToArray()) : "") + ".");
        }

        private void SetDoctrine(CallContext c, DoctrineLevel level, string target, string key, string value)
        {
            var e = Engine();
            var errors = e.SetDoctrine(level, target, new Dictionary<string, object> { { key, value } });
            if (errors.Count > 0) throw SkizzyError.InvalidParam("/" + key, string.Join("; ", errors.ToArray()));
            _h.C2.Engage();
        }

        // ------------------------------------------------------------------ fire authority and EMCON (C2 doctrine)

        private JsonValue FireAuthority(CallContext c)
        {
            string mode = c.Str("mode");
            string level, target;
            var ids = Recipients(c, out level, out target);
            var names = new List<string>();
            if (level == "group" || level == "side")
            {
                string side = level == "side" ? C2Host.ActingSide(c, target, true) : null;
                if (level == "group")
                    foreach (var id in ids) Own(c, id, "/group");
                SetDoctrine(c, level == "group" ? DoctrineLevel.Group : DoctrineLevel.Side, level == "side" ? side : target, "weaponsControl", mode);
                foreach (var id in ids) names.Add(Name(id));
            }
            else
            {
                foreach (var id in ids) Own(c, id, "/units");
                foreach (var id in ids)
                {
                    SetDoctrine(c, DoctrineLevel.Unit, id.ToString(CultureInfo.InvariantCulture), "weaponsControl", mode);
                    names.Add(Name(id));
                }
            }
            return JsonValue.Obj().Set("mode", mode).Set("level", level).Set("units", ids.Count)
                .Set("text", (level == "unit" ? string.Join(", ", names.ToArray()) : level + " " + target) + ": weapons " + mode.ToUpperInvariant() +
                             " (C2 doctrine weaponsControl; BDArmory fire authority F3 applied at the next C2 tick).");
        }

        private JsonValue Radar(CallContext c)
        {
            string emcon = c.Str("emcon");
            string level, target;
            var ids = Recipients(c, out level, out target);
            foreach (var id in ids) Own(c, id, "/units");
            var applied = new List<string>();
            SetEmcon(c, ids, level, target, emcon, applied);
            return JsonValue.Obj().Set("emcon", emcon).Set("level", level).Set("units", ids.Count).Set("applied", JsonValue.ArrOf(applied.ToArray()))
                .Set("text", (level == "unit" ? NamesOf(ids) : level + " " + target) + ": radar " + emcon + " (" + string.Join("; ", applied.ToArray()) + ").");
        }

        private string NamesOf(List<uint> ids)
        {
            var n = new List<string>();
            foreach (var id in ids) n.Add(Name(id));
            return string.Join(", ", n.ToArray());
        }

        /// <summary>EMCON through the C2 doctrine when the engine controls radars, else straight to the radar modules.</summary>
        private void SetEmcon(CallContext c, List<uint> ids, string level, string target, string emcon, List<string> applied)
        {
            if (emcon != "active" && emcon != "passive") throw SkizzyError.InvalidParam("/emcon", "emcon is active or passive");
            var e = _h.C2.Engine;
            if (e != null && e.HasRadarControl)
            {
                if (level == "group") SetDoctrine(c, DoctrineLevel.Group, target, "emcon", emcon);
                else if (level == "side") SetDoctrine(c, DoctrineLevel.Side, C2Host.ActingSide(c, target, true), "emcon", emcon);
                else foreach (var id in ids) SetDoctrine(c, DoctrineLevel.Unit, id.ToString(CultureInfo.InvariantCulture), "emcon", emcon);
                applied.Add("emcon " + emcon + " (C2 doctrine, radars switched at the next C2 tick)");
                return;
            }
            var ops = Need();
            int changed = 0, none = 0;
            foreach (var id in ids)
            {
                int n = ops.SetRadar(id, emcon == "active");
                if (n < 0) throw SkizzyError.NotFound(HandleRegistry.VesselHandle(id), "unknown", "Unit not loaded.");
                if (n == 0) none++;
                changed += n;
            }
            if (changed == 0 && ids.Count > 0) throw Refuse("no_radar", (ids.Count == 1 ? Name(ids[0]) + " has" : "none of these units has") + " a BDArmory radar.");
            applied.Add(changed + " radar(s) switched " + (emcon == "active" ? "on" : "off") + (none > 0 ? ", " + none + " unit(s) without radar" : ""));
        }

        // ------------------------------------------------------------------ missile fire (F9)

        private static string RefusalCode(FireRefusal r)
        {
            switch (r)
            {
                case FireRefusal.HookMissing: return "HOOK_MISSING";
                case FireRefusal.NoWeaponManager: return "NO_WEAPON_MANAGER";
                case FireRefusal.NoWeapon: return "NO_WEAPON";
                case FireRefusal.OutOfRange: return "OUT_OF_RANGE";
                case FireRefusal.NotLocked: return "NOT_LOCKED";
                case FireRefusal.FireHold: return "FIRE_HOLD";
                case FireRefusal.TargetDestroyed: return "TARGET_DESTROYED";
                case FireRefusal.NotHostile: return "NOT_HOSTILE";
                default: return "LAUNCH_FAILED";
            }
        }

        private static string RefusalHint(FireRefusal r)
        {
            switch (r)
            {
                case FireRefusal.HookMissing: return "This BDArmory lacks hook F9 (FireMissileAt); install the Overdrive build.";
                case FireRefusal.NoWeapon: return "No missile matches (out of ammo, or the weapon filter); see bda.get_weapon_manager.";
                case FireRefusal.OutOfRange: return "Close the range or pick a longer-range missile.";
                case FireRefusal.NotLocked: return "The unit's own radar must hold the target for a radar-guided missile (requireLock:false to fire anyway).";
                case FireRefusal.FireHold: return "Weapons are on Hold: bda.set_fire_authority tight or free first.";
                case FireRefusal.TargetDestroyed: return "Pick another target from tac.threats.";
                default: return null;
            }
        }

        private IEnumerator<object> FireMissile(CallContext c, Operation op)
        {
            var ops = Need();
            uint unit = One(c, "unit");
            var u = Own(c, unit, "/unit");
            uint target = _h.Tactical.ResolveTarget(c, c.Str("target"), u.Side, "/target");
            int count = Math.Max(1, Math.Min(16, c.Int("count", 1)));
            double stagger = Math.Max(0.1, c.Num("staggerS", 1.0));
            var req = new FireRequest
            {
                Unit = unit,
                Target = target,
                Weapon = c.Str("weapon"),
                CheckClearance = c.Bool("checkClearance", true),
                RequireLock = c.Bool("requireLock", true),
            };
            var first = ops.CheckFire(req);
            if (!first.Ok)
                throw new SkizzyError(ErrorKind.PreconditionFailed, u.Name + ": " + (first.Detail ?? RefusalCode(first.Refusal)), RefusalCode(first.Refusal), RefusalHint(first.Refusal),
                    JsonValue.Obj().Set("rangeM", double.IsNaN(first.RangeM) ? null : JsonValue.Num(Math.Round(first.RangeM))).Set("weapon", first.Weapon));
            var fired = JsonValue.Arr();
            string stopped = null;
            for (int i = 0; i < count; i++)
            {
                if (i > 0)
                {
                    long until = Clock.NowMs + (long)(stagger * 1000);
                    while (Clock.NowMs < until)
                    {
                        if (op.CancelRequested || _h.Kill.Engaged) break;
                        yield return null;
                    }
                }
                if (_h.Kill.Engaged) { stopped = "kill_switch"; break; }
                if (op.CancelRequested) { stopped = "cancelled"; break; }
                if (_ops == null) { stopped = "bda_detached"; break; }
                var r = ops.FireMissile(req);
                if (!r.Ok) { stopped = RefusalCode(r.Refusal) + (r.Detail != null ? ": " + r.Detail : ""); break; }
                fired.Add(JsonValue.Obj().Set("weapon", r.Weapon).Set("rangeM", double.IsNaN(r.RangeM) ? null : JsonValue.Num(Math.Round(r.RangeM))));
                op.Progress = (i + 1) / (double)count;
                op.ProgressText = "fired " + (i + 1) + "/" + count;
            }
            string tname = TargetName(u.Side, target, c.Str("target"));
            op.Result = JsonValue.Obj().Set("unit", HandleRegistry.VesselHandle(unit)).Set("target", c.Str("target")).Set("requested", count).Set("fired", fired.Count)
                .Set("shots", fired).Set("stopped", stopped)
                .Set("text", u.Name + ": fired " + fired.Count + "/" + count + " at " + tname + (stopped != null ? "; stopped (" + stopped + ")" : "") + ".");
            if (fired.Count == 0 && stopped != null && stopped != "kill_switch" && stopped != "cancelled")
                op.Error = new SkizzyError(ErrorKind.PreconditionFailed, u.Name + ": no missile fired (" + stopped + ")", stopped.Split(':')[0]);
        }

        private string TargetName(string side, uint target, string asGiven)
        {
            int n;
            if (Tactical.TacticalHost.TryTrackNumber(asGiven, out n)) return "T" + n.ToString(CultureInfo.InvariantCulture);
            return Name(target);
        }

        // ------------------------------------------------------------------ guns, countermeasures

        private JsonValue Guns(CallContext c)
        {
            var ops = Need();
            uint id = One(c, "unit");
            var u = Own(c, id, "/unit");
            string scope = c.Str("scope", "all");
            bool on = c.Bool("enabled");
            int n = ops.SetGuns(id, scope, on);
            if (n < 0) throw Refuse("no_weapon_manager", u.Name + " has no BDArmory weapon manager.");
            if (n == 0) throw Refuse("NO_WEAPON", u.Name + " has no " + (scope == "ciws" ? "CIWS" : scope == "guns" ? "guns" : "guns or CIWS") + " to switch " + (on ? "on" : "off") + ".");
            return JsonValue.Obj().Set("unit", HandleRegistry.VesselHandle(id)).Set("scope", scope).Set("enabled", on).Set("changed", n)
                .Set("text", u.Name + ": " + n + " " + (scope == "ciws" ? "CIWS" : scope == "guns" ? "gun(s)" : "gun/CIWS") + " " + (on ? "enabled" : "disabled") + ".");
        }

        private JsonValue Countermeasure(CallContext c)
        {
            var ops = Need();
            uint id = One(c, "unit");
            var u = Own(c, id, "/unit");
            string kind = c.Str("kind");
            if (kind == "ecm")
            {
                bool on = c.Bool("on", true);
                int j = ops.SetEcm(id, on);
                if (j < 0) throw Refuse("no_weapon_manager", u.Name + " has no BDArmory weapon manager.");
                if (j == 0) throw Refuse("NO_WEAPON", u.Name + " has no ECM jammer.");
                return JsonValue.Obj().Set("unit", HandleRegistry.VesselHandle(id)).Set("kind", "ecm").Set("on", on).Set("changed", j)
                    .Set("text", u.Name + ": ECM " + (on ? "on" : "off") + " (" + j + " jammer(s)).");
            }
            int count = Math.Max(1, Math.Min(10, c.Int("count", 1)));
            int n = ops.Countermeasure(id, kind, count);
            if (n < 0) throw Refuse("no_weapon_manager", u.Name + " has no BDArmory weapon manager.");
            if (n == 0) throw Refuse("NO_WEAPON", u.Name + " has no " + kind + " left.");
            return JsonValue.Obj().Set("unit", HandleRegistry.VesselHandle(id)).Set("kind", kind).Set("dropped", n)
                .Set("text", u.Name + ": " + n + " " + kind + (n == 1 ? "" : "s") + " dropped.");
        }

        // ------------------------------------------------------------------ F2, F4, assignment (C2)

        private void RefuseIfManaged(uint id, string what)
        {
            if (C2Manages(id))
                throw Refuse("c2_managed", Name(id) + " is driven by the C2 engine (orders, a managed side or doctrine); " + what + " would be overwritten.",
                    "Use c2.order.issue (engage with targets sets F1 and the F4 filter), or c2.unit.release first.");
        }

        private IBdaControl Control()
        {
            var ctl = _h.Tactical.Control;
            if (ctl == null)
                throw new SkizzyError(ErrorKind.ExecutorUnavailable, "the BDArmory binding is not attached", "bda_not_attached", "Needs the BDArmory binding in flight.");
            return ctl;
        }

        private JsonValue Secondary(CallContext c)
        {
            var ctl = Control();
            uint id = One(c, "unit");
            var u = Own(c, id, "/unit");
            var w = _h.C2.World;
            if (w != null && (w.Hooks & HookSet.F2) == 0) throw Refuse("HOOK_MISSING", "This BDArmory lacks hook F2 (external secondary targets).");
            RefuseIfManaged(id, "secondary targets");
            var arr = c.Params.GetArr("targets");
            var list = new List<uint>();
            if (arr != null) for (int i = 0; i < arr.Count; i++) list.Add(_h.Tactical.ResolveTarget(c, arr[i].AsString(), u.Side, "/targets/" + i));
            bool only = c.Bool("only");
            ctl.SetSecondaryTargets(id, list, only);
            return JsonValue.Obj().Set("unit", HandleRegistry.VesselHandle(id)).Set("targets", list.Count).Set("only", only)
                .Set("text", u.Name + ": " + (list.Count == 0 ? "secondary targets cleared" : list.Count + " secondary target(s)" + (only ? ", only these" : "")) + " (F2).");
        }

        private JsonValue Filter(CallContext c)
        {
            var ctl = Control();
            uint id = One(c, "unit");
            var u = Own(c, id, "/unit");
            var w = _h.C2.World;
            if (w != null && (w.Hooks & HookSet.F4) == 0) throw Refuse("HOOK_MISSING", "This BDArmory lacks hook F4 (external target filter).");
            RefuseIfManaged(id, "a target filter");
            if (c.Bool("clear"))
            {
                ctl.SetTargetFilter(id, null);
                return JsonValue.Obj().Set("unit", HandleRegistry.VesselHandle(id)).Set("filter", false).Set("text", u.Name + ": target filter removed (F4).");
            }
            var arr = c.Params.GetArr("allow");
            if (arr == null || arr.Count == 0) throw SkizzyError.InvalidParam("/allow", "give allow targets, or clear: true");
            var f = new TargetFilter(id);
            var list = new List<uint>();
            for (int i = 0; i < arr.Count; i++) list.Add(_h.Tactical.ResolveTarget(c, arr[i].AsString(), u.Side, "/allow/" + i));
            f.SetTo(list);
            ctl.SetTargetFilter(id, f);
            return JsonValue.Obj().Set("unit", HandleRegistry.VesselHandle(id)).Set("filter", true).Set("allow", list.Count)
                .Set("text", u.Name + ": BDArmory may only engage " + list.Count + " target(s) (F4 filter; missiles are never filtered).");
        }

        private JsonValue AssignTarget(CallContext c)
        {
            var e = Engine();
            string level, target;
            var ids = Recipients(c, out level, out target);
            if (ids.Count == 0) throw SkizzyError.InvalidParam("/units", "no units");
            var first = Own(c, ids[0], "/units");
            foreach (var id in ids) Own(c, id, "/units");
            string side = C2Host.ActingSide(c, c.Str("side") ?? first.Side, true);
            var req = new OrderRequest { Type = OrderType.Engage, Side = side, Issuer = c.Session != null ? c.Session.ActorId ?? "agent" : "host", Role = IssuerRole.Seat, Label = c.Str("label") };
            req.Targets = new List<uint>();
            foreach (var id in ids) req.Units.Add(id);
            var arr = c.Params.GetArr("targets");
            if (arr != null) for (int i = 0; i < arr.Count; i++) req.Targets.Add(_h.Tactical.ResolveTarget(c, arr[i].AsString(), side, "/targets/" + i));
            else if (c.Has("target")) { req.Target = _h.Tactical.ResolveTarget(c, c.Str("target"), side, "/target"); req.Targets = null; }
            else throw SkizzyError.InvalidParam("/target", "give target or targets");
            var res = e.Issue(req);
            _h.C2.Engage();
            if (!res.Accepted)
                throw new SkizzyError(ErrorKind.PreconditionFailed, res.Message ?? res.Summary ?? "refused", Orders.Wire(res.Code).ToLowerInvariant(),
                    "bda.assign_target issues a C2 engage order; see c2.order.issue for the refusal codes.");
            return JsonValue.Obj().Set("order", res.Order.Handle)
                .Set("units", ids.Count).Set("text", (res.Summary ?? "Engage order issued.") + " (C2 engage order: F1 assignment and F4 filter.)");
        }

        private JsonValue RadarContacts(CallContext c)
        {
            var tpe = _h.Tactical.Tpe;
            if (tpe == null) throw new SkizzyError(ErrorKind.ExecutorUnavailable, "the track picture engine is not attached", "tactical_not_attached", "Needs the BDArmory binding in flight.");
            uint id = One(c, "unit");
            var u = Own(c, id, "/unit");
            var ids = new List<uint>();
            tpe.ContactsOf(id, ids);
            var p = u.Side != null ? tpe.Team(u.Side) : null;
            var rows = JsonValue.Arr();
            var parts = new List<string>();
            foreach (var t in ids)
            {
                var tr = p != null ? p.ByVesselId(t) : null;
                string label = tr != null ? "T" + tr.Number.ToString(CultureInfo.InvariantCulture) : HandleRegistry.VesselHandle(t);
                var o = JsonValue.Obj().Set("track", label);
                var tu = tpe.Unit(t);
                if (tu != null)
                {
                    double d = tpe.Geo.Distance(u.Pos, tu.Pos), b = tpe.Geo.Bearing(u.Pos, tu.Pos);
                    o.Set("bearingDeg", Math.Round(b, 1)).Set("rangeM", Math.Round(d));
                    parts.Add(label + " B" + ((int)Math.Round(b) % 360).ToString("000", CultureInfo.InvariantCulture) + " R" + (d / 1000).ToString("0.0", CultureInfo.InvariantCulture) + "km");
                }
                if (tr != null) o.Set("iff", TrackPictureEngine.IffText(tr.Iff)).Set("class", UnitClasses.Name(tr.Class));
                rows.Add(o);
            }
            return JsonValue.Obj().Set("unit", HandleRegistry.VesselHandle(id)).Set("contacts", rows).Set("total", rows.Count)
                .Set("text", u.Name + " own sensors hold " + rows.Count + (rows.Count > 0 ? ": " + string.Join(", ", parts.ToArray()) : "") + " (F5, as of its last refresh).");
        }

        // ------------------------------------------------------------------ competitions (BDArmory as referee)

        private JsonValue CompetitionStart(CallContext c)
        {
            var ops = Need();
            if (!Tactical.TacticalHost.Privileged(c.Session) && c.Session != null && c.Session.Side != null)
                throw new SkizzyError(ErrorKind.PermissionDenied, "a competition involves every side; a human, referee or director starts it", "not_privileged", "Ask the referee.");
            var s = new CompetitionStart
            {
                DistanceM = c.Num("distanceM", 1000),
                StartDespiteFailures = c.Bool("startDespiteFailures"),
                Tag = c.Str("tag", "skizzy"),
                Type = c.Str("type", "ffa"),
            };
            string err = ops.StartCompetition(s);
            if (err == "competition_running") throw new SkizzyError(ErrorKind.Conflict, "a BDArmory competition is already running", "competition_running", "bda.competition.stop first.");
            if (err != null) throw Refuse(err, err == "no_competitors" ? "Nothing to compete: a competition needs weapon-manager vessels on at least two teams." : "Competition not started: " + err.Replace('_', ' ') + ".");
            var info = new CompetitionInfo();
            ops.Competition(info);
            return CompetitionJson(info, false).Set("text", "BDArmory competition starting (" + s.Type + ", " + info.Scores.Count + " competitors).");
        }

        private JsonValue CompetitionStop(CallContext c)
        {
            var ops = Need();
            if (!Tactical.TacticalHost.Privileged(c.Session) && c.Session != null && c.Session.Side != null)
                throw new SkizzyError(ErrorKind.PermissionDenied, "a human, referee or director stops a competition", "not_privileged", "Ask the referee.");
            string err = ops.StopCompetition();
            if (err != null) throw Refuse(err, err == "no_competition" ? "No BDArmory competition is running." : "Not stopped: " + err.Replace('_', ' ') + ".");
            var info = new CompetitionInfo();
            ops.Competition(info);
            return CompetitionJson(info, true).Set("text", "BDArmory competition stopped. " + Survivors(info));
        }

        private static string Survivors(CompetitionInfo info)
        {
            var alive = new Dictionary<string, int>();
            foreach (var s in info.Scores)
            {
                string t = s.Team ?? "-";
                int n;
                alive.TryGetValue(t, out n);
                alive[t] = n + (s.Alive ? 1 : 0);
            }
            var parts = new List<string>();
            foreach (var kv in alive) parts.Add(kv.Key + " " + kv.Value + " alive");
            return parts.Count > 0 ? string.Join(", ", parts.ToArray()) + "." : "No competitors.";
        }

        private JsonValue CompetitionState(CallContext c)
        {
            var ops = Need();
            var info = new CompetitionInfo();
            ops.Competition(info);
            string st = !info.Available ? "unavailable" : info.Running ? "running" : info.Starting ? "starting" : "idle";
            return CompetitionJson(info, false).Set("text", "Competition " + st + (info.Running || info.Starting ? " (id " + info.Id + ")" : "") + ". " + Survivors(info) +
                                                            (info.DeathOrder.Count > 0 ? " Deaths: " + string.Join(", ", info.DeathOrder.ToArray()) + "." : ""));
        }

        private JsonValue CompetitionScores(CallContext c)
        {
            var ops = Need();
            int limit = c.Int("limit", 10);
            if (limit > 50) throw SkizzyError.InvalidParam("/limit", "at most 50 rows per call");
            int offset = 0;
            string cursor = c.Str("cursor");
            if (cursor != null && !int.TryParse(cursor.TrimStart('o'), NumberStyles.None, CultureInfo.InvariantCulture, out offset)) throw SkizzyError.InvalidParam("/cursor", "bad cursor");
            var info = new CompetitionInfo();
            ops.Competition(info);
            info.Scores.Sort((a, b) => b.Kills != a.Kills ? b.Kills.CompareTo(a.Kills) : b.Hits.CompareTo(a.Hits));
            var rows = JsonValue.Arr();
            var lines = new List<string>();
            int end = Math.Min(info.Scores.Count, offset + limit);
            for (int i = offset; i < end; i++)
            {
                var s = info.Scores[i];
                rows.Add(ScoreJson(s));
                lines.Add(TacticalText.Tok(s.Vessel) + " " + (s.Team ?? "-") + (s.Alive ? " alive" : " dead" + (s.KilledBy != null ? " by " + TacticalText.Tok(s.KilledBy) : "")) +
                          " kills " + s.Kills + " hits " + s.Hits + "/" + s.ShotsFired + " msl " + s.MissileStrikes);
            }
            var res = JsonValue.Obj().Set("scores", rows).Set("total", info.Scores.Count).Set("truncated", end < info.Scores.Count)
                .Set("text", "#scores " + (end - offset) + "/" + info.Scores.Count + (lines.Count > 0 ? "\n" + string.Join("\n", lines.ToArray()) : ""));
            if (end < info.Scores.Count) res.Set("cursor", "o" + end.ToString(CultureInfo.InvariantCulture));
            return res;
        }

        private static JsonValue ScoreJson(ScoreRow s)
        {
            var o = JsonValue.Obj().Set("vessel", s.Vessel).Set("team", s.Team).Set("alive", s.Alive).Set("kills", s.Kills).Set("assists", s.Assists)
                .Set("hits", s.Hits).Set("shotsFired", s.ShotsFired).Set("rocketStrikes", s.RocketStrikes).Set("missileStrikes", s.MissileStrikes)
                .Set("damageDealt", Math.Round(s.DamageDealt, 1));
            if (s.VesselId != 0) o.Set("handle", HandleRegistry.VesselHandle(s.VesselId));
            if (s.KilledBy != null) o.Set("killedBy", s.KilledBy);
            if (s.DeathHow != null) o.Set("deathHow", s.DeathHow);
            if (!double.IsNaN(s.DeathUt)) o.Set("deathUt", s.DeathUt);
            return o;
        }

        private static JsonValue CompetitionJson(CompetitionInfo info, bool withScores)
        {
            var o = JsonValue.Obj().Set("available", info.Available).Set("running", info.Running).Set("starting", info.Starting).Set("id", info.Id)
                .Set("status", info.Status).Set("competitors", info.Scores.Count).Set("deathOrder", JsonValue.ArrOf(info.DeathOrder.ToArray()));
            if (!double.IsNaN(info.StartUt)) o.Set("startUt", info.StartUt);
            if (withScores)
            {
                var arr = JsonValue.Arr();
                foreach (var s in info.Scores) arr.Add(ScoreJson(s));
                o.Set("scores", arr);
            }
            return o;
        }

        // ------------------------------------------------------------------ AI modules

        private JsonValue AiList(CallContext c)
        {
            var ops = Need();
            uint id = One(c, "unit");
            bool fields = c.Bool("fields", true);
            string module = c.Str("module");
            var list = new List<AiInfo>();
            int n = ops.ListAi(id, list, fields);
            if (n < 0) throw SkizzyError.NotFound(HandleRegistry.VesselHandle(id), "unknown", "That vessel is not loaded (destroyed or unloaded).");
            var arr = JsonValue.Arr();
            var lines = new List<string>();
            var tpe = _h.Tactical.Tpe;
            var u = Unit(id);
            var pic = tpe != null && u != null && u.Side != null ? tpe.Team(u.Side) : null;
            foreach (var a in list)
            {
                if (module != null && !Matches(a, module)) continue;
                string tgt = null;
                if (a.Target != 0)
                {
                    int num = pic != null ? pic.NumberOf(a.Target) : 0;
                    tgt = num > 0 ? "T" + num.ToString(CultureInfo.InvariantCulture) : HandleRegistry.VesselHandle(a.Target);
                }
                var o = JsonValue.Obj().Set("module", a.Module).Set("kind", a.Kind).Set("active", a.Active).Set("state", a.State).Set("target", tgt).Set("waypoint", a.Waypoint);
                if (fields)
                {
                    var fa = JsonValue.Arr();
                    foreach (var f in a.Fields)
                    {
                        var fo = JsonValue.Obj().Set("name", f.Name).Set("label", f.Label).Set("type", f.Type).Set("value", f.Value);
                        if (!double.IsNaN(f.Min)) fo.Set("min", f.Min);
                        if (!double.IsNaN(f.Max)) fo.Set("max", f.Max);
                        if (!double.IsNaN(f.Step)) fo.Set("step", f.Step);
                        if (f.Options != null) fo.Set("options", JsonValue.ArrOf(f.Options));
                        if (f.ReadOnly) fo.Set("readOnly", true);
                        fa.Add(fo);
                    }
                    o.Set("fields", fa);
                }
                arr.Add(o);
                lines.Add(a.Module + " (" + a.Kind + ") " + (a.Active ? "ACTIVE" : "off") + (a.State != null ? " \"" + a.State + "\"" : "") + (tgt != null ? " tgt " + tgt : "") +
                          (a.Waypoint != null ? " wp " + a.Waypoint : "") + (fields ? " | " + a.Fields.Count + " settings" : ""));
            }
            if (module != null && arr.Count == 0) throw Refuse("no_ai", "No AI module matching " + module + " on " + Name(id) + ".");
            return JsonValue.Obj().Set("unit", HandleRegistry.VesselHandle(id)).Set("ai", arr).Set("total", arr.Count)
                .Set("text", arr.Count == 0 ? Name(id) + " has no BDArmory AI module." : Name(id) + ": " + string.Join("\n", lines.ToArray()));
        }

        private static bool Matches(AiInfo a, string module)
        {
            return string.Equals(a.Module, module, StringComparison.OrdinalIgnoreCase) || string.Equals(a.Kind, module, StringComparison.OrdinalIgnoreCase);
        }

        private SkizzyError AiError(string reason, uint id, string module, string field)
        {
            switch (reason)
            {
                case "no_ai": return Refuse("no_ai", Name(id) + " has no BDArmory AI module" + (module != null ? " matching " + module : "") + ".");
                case "ambiguous_ai": return Refuse("ambiguous_ai", Name(id) + " has more than one AI module; pass module (pilot, surface, vtol, orbital or the class name).", "List them with bda.ai.list.");
                case "unknown_field": return SkizzyError.InvalidParam("/values/" + field, "the AI has no setting " + field, "List settings with bda.ai.list.");
                case "out_of_range": return SkizzyError.InvalidParam("/values/" + field, field + " is out of range", "bda.ai.list shows min and max.");
                case "read_only": return Refuse("read_only", field + " is not settable.");
                case "bad_value": return SkizzyError.InvalidParam("/values/" + field, "bad value for " + field);
                case "unknown_unit": return SkizzyError.NotFound(HandleRegistry.VesselHandle(id), "unknown", "That vessel is not loaded (destroyed or unloaded).");
                default: return Refuse(reason, reason.Replace('_', ' '));
            }
        }

        private JsonValue AiActivate(CallContext c)
        {
            var ops = Need();
            string level, target;
            var ids = Recipients(c, out level, out target);
            bool active = c.Bool("active", true);
            bool? guard = c.Has("guard") ? (bool?)c.Bool("guard") : null;
            string module = c.Str("module");
            var done = new List<string>();
            var failed = JsonValue.Arr();
            foreach (var id in ids)
            {
                Own(c, id, "/units");
                string err = ops.SetAiActive(id, module, active, guard);
                if (err == null) done.Add(Name(id));
                else
                {
                    if (ids.Count == 1) throw AiError(err, id, module, null);
                    failed.Add(JsonValue.Obj().Set("unit", HandleRegistry.VesselHandle(id)).Set("reason", err));
                }
            }
            return JsonValue.Obj().Set("active", active).Set("changed", done.Count).Set("failed", failed)
                .Set("text", (done.Count > 0 ? string.Join(", ", done.ToArray()) + ": AI " + (active ? "activated" : "deactivated") + (guard.HasValue ? ", guard " + (guard.Value ? "on" : "off") : "") : "No AI changed") +
                             (failed.Count > 0 ? "; " + failed.Count + " refused" : "") + ".");
        }

        private JsonValue AiSet(CallContext c)
        {
            var ops = Need();
            string level, target;
            var ids = Recipients(c, out level, out target);
            string module = c.Str("module");
            var vals = c.Params.GetObj("values");
            if (vals == null || vals.Count == 0) throw SkizzyError.InvalidParam("/values", "give values {setting: value}");
            var results = JsonValue.Arr();
            var applied = new List<string>();
            foreach (var id in ids)
            {
                Own(c, id, "/units");
                for (int i = 0; i < vals.Count; i++)
                {
                    string key = vals.KeyAt(i);
                    var v = vals.ValueAt(i);
                    string sv = v.Kind == JsonKind.String ? v.AsString() : v.Kind == JsonKind.Bool ? (v.AsBool() ? "true" : "false") : v.AsDouble().ToString("R", CultureInfo.InvariantCulture);
                    string now;
                    string err = ops.SetAiField(id, module, key, sv, out now);
                    if (err != null && ids.Count == 1 && vals.Count == 1) throw AiError(err, id, module, key);
                    results.Add(JsonValue.Obj().Set("unit", HandleRegistry.VesselHandle(id)).Set("setting", key).Set("ok", err == null).Set("value", now).Set("reason", err));
                    if (err == null) applied.Add(key + "=" + now);
                }
            }
            return JsonValue.Obj().Set("results", results).Set("applied", applied.Count)
                .Set("text", NamesOf(ids) + ": " + (applied.Count > 0 ? string.Join(", ", applied.ToArray()) : "nothing applied") + (results.Count > applied.Count ? "; " + (results.Count - applied.Count) + " refused (see results)" : "") + ".");
        }

        private JsonValue AiPresets(CallContext c)
        {
            string kind = c.Str("kind");
            var arr = JsonValue.Arr();
            var lines = new List<string>();
            foreach (var p in AiPresetBook.All)
            {
                if (kind != null && p.Kind != kind) continue;
                var vals = JsonValue.Obj();
                foreach (var kv in p.Values) vals.Set(kv.Key, kv.Value);
                arr.Add(JsonValue.Obj().Set("name", p.Name).Set("kind", p.Kind).Set("description", p.Description).Set("values", vals));
                lines.Add(p.Name + " (" + p.Kind + "): " + p.Description);
            }
            return JsonValue.Obj().Set("presets", arr).Set("text", string.Join("\n", lines.ToArray()));
        }

        private JsonValue AiApplyPreset(CallContext c)
        {
            var ops = Need();
            var preset = AiPresetBook.Find(c.Str("preset"));
            if (preset == null) throw SkizzyError.NotFound(c.Str("preset") ?? "", "unknown_preset", "List presets with bda.ai.presets.");
            string level, target;
            var ids = Recipients(c, out level, out target);
            var lines = new List<string>();
            int applied = 0, skipped = 0;
            foreach (var id in ids)
            {
                Own(c, id, "/units");
                var mods = new List<AiInfo>();
                ops.ListAi(id, mods, false);
                AiInfo m = null;
                foreach (var a in mods) if (a.Kind == preset.Kind) { m = a; break; }
                if (m == null) { lines.Add(Name(id) + ": no " + preset.Kind + " AI"); skipped++; continue; }
                int ok = 0, bad = 0;
                foreach (var kv in preset.Values)
                {
                    string now;
                    if (ops.SetAiField(id, m.Module, kv.Key, kv.Value, out now) == null) ok++; else bad++;
                }
                applied++;
                lines.Add(Name(id) + ": " + ok + " set" + (bad > 0 ? ", " + bad + " not on this AI version" : ""));
            }
            if (applied == 0) throw Refuse("no_ai", "None of these units has a " + preset.Kind + " AI for preset " + preset.Name + ".");
            return JsonValue.Obj().Set("preset", preset.Name).Set("units", applied).Set("skipped", skipped)
                .Set("text", "Preset " + preset.Name + ": " + string.Join("; ", lines.ToArray()) + ".");
        }

        // ------------------------------------------------------------------ raw hook calls (testing: HOOKS-CHECKLIST D-H, M4)

        private void NeedHook(HookSet hook)
        {
            var ops = _ops;
            var w = _h.C2.World;
            HookSet have = ops != null ? ops.Hooks : w != null ? w.Hooks : HookSet.None;
            if ((have & hook) == 0) throw Refuse("HOOK_MISSING", "This BDArmory lacks hook " + hook + ".", "bda.hooks lists what this BDArmory has; install the Overdrive build.");
        }

        private static string AuthText(FireAuthorityMode m) { return m == FireAuthorityMode.Hold ? "hold" : m == FireAuthorityMode.Tight ? "tight" : "free"; }

        private string Label(TeamPicture pic, uint v)
        {
            if (v == 0) return null;
            int n = pic != null ? pic.NumberOf(v) : 0;
            return n > 0 ? "T" + n.ToString(CultureInfo.InvariantCulture) : HandleRegistry.VesselHandle(v);
        }

        private JsonValue HookStateCap(CallContext c)
        {
            var ops = Need();
            uint id = One(c, "unit");
            var i = new HookStateInfo();
            if (!ops.HookState(id, i)) throw SkizzyError.NotFound(HandleRegistry.VesselHandle(id), "unknown", "That vessel is not loaded (destroyed or unloaded).");
            var u = Unit(id);
            var tpe = _h.Tactical.Tpe;
            var pic = tpe != null && u != null && u.Side != null ? tpe.Team(u.Side) : null;
            var inv = CultureInfo.InvariantCulture;
            var o = JsonValue.Obj().Set("unit", HandleRegistry.VesselHandle(id)).Set("name", i.Name).Set("weaponManager", i.HasWeaponManager).Set("guardMode", i.GuardMode)
                .Set("guardTarget", Label(pic, i.GuardTarget))
                .Set("override", JsonValue.Obj().Set("active", i.HasOverride).Set("target", Label(pic, i.OverrideTarget)).Set("sticky", i.OverrideSticky).Set("engageable", i.OverrideEngageable)
                    .Set("remainingS", double.IsNaN(i.OverrideRemainingS) ? null : JsonValue.Num(Math.Round(i.OverrideRemainingS, 1)))
                    .Set("lastLostReason", i.LastLostReason).Set("lastLostTarget", Label(pic, i.LastLostTarget))
                    .Set("lastLostUt", double.IsNaN(i.LastLostUt) ? null : JsonValue.Num(i.LastLostUt)).Set("teamAssistBlocked", i.TeamAssistBlocked))
                .Set("secondary", JsonValue.Obj().Set("installed", i.SecondaryInstalled).Set("only", i.SecondaryOnly).Set("count", i.SecondaryCount))
                .Set("fireAuthority", i.FireAuthority).Set("filterInstalled", i.FilterInstalled)
                .Set("ai", JsonValue.Obj().Set("module", i.AiModule).Set("active", i.AiActive).Set("customRoute", i.CustomRoute).Set("runningWaypoints", i.RunningWaypoints)
                    .Set("waypointIndex", i.WaypointIndex).Set("lap", i.WaypointLap).Set("lapLimit", i.LapLimit).Set("yieldToCombat", i.YieldToCombat)
                    .Set("leashM", double.IsNaN(i.LeashM) ? null : JsonValue.Num(i.LeashM)).Set("patrolEngaged", i.PatrolEngaged).Set("patrolTarget", Label(pic, i.PatrolTarget)));
            string text = "#hooks " + TacticalText.Tok(i.Name) + " guard " + (i.GuardMode ? "on" : "off") + (i.GuardTarget != 0 ? " tgt " + Label(pic, i.GuardTarget) : "") +
                          " auth " + (i.FireAuthority ?? "-").ToUpperInvariant() + (i.FilterInstalled ? " filter" : "") +
                          "\nF1 " + (i.HasOverride ? Label(pic, i.OverrideTarget) + (i.OverrideSticky ? " sticky" : "") + (double.IsNaN(i.OverrideRemainingS) ? "" : " " + i.OverrideRemainingS.ToString("0", inv) + "s") : "none") +
                          (i.LastLostReason != null ? " | last lost " + i.LastLostReason + (i.LastLostTarget != 0 ? " " + Label(pic, i.LastLostTarget) : "") : "") +
                          (i.TeamAssistBlocked >= 0 ? " | assist blocked " + i.TeamAssistBlocked : "") +
                          "\nF2 " + (i.SecondaryInstalled ? i.SecondaryCount + (i.SecondaryOnly ? " only" : "") : "none") +
                          "\nAI " + (i.AiModule ?? "none") + (i.AiActive ? " on" : " off") +
                          (i.CustomRoute ? " route wp " + i.WaypointIndex + " lap " + i.WaypointLap + "/" + i.LapLimit : i.RunningWaypoints ? " course wp " + i.WaypointIndex : "") +
                          " yield " + (i.YieldToCombat ? "on" : "off") + (double.IsNaN(i.LeashM) ? "" : " leash " + i.LeashM.ToString("0", inv) + "m") +
                          (i.PatrolEngaged ? " ENGAGED " + Label(pic, i.PatrolTarget) : "");
            return o.Set("text", text);
        }

        private JsonValue HookOverride(CallContext c)
        {
            var ctl = Control();
            uint id = One(c, "unit");
            var u = Own(c, id, "/unit");
            NeedHook(HookSet.F1);
            RefuseIfManaged(id, "a direct target assignment");
            if (c.Bool("clear"))
            {
                ctl.ClearOverrideTarget(id);
                return JsonValue.Obj().Set("unit", HandleRegistry.VesselHandle(id)).Set("cleared", true)
                    .Set("text", u.Name + ": external target assignment cleared (F1 ClearExternalOverride; bda.hook.override_lost reason Cleared follows if one was live).");
            }
            if (!c.Has("target")) throw SkizzyError.InvalidParam("/target", "give target, or clear: true");
            uint target = _h.Tactical.ResolveTarget(c, c.Str("target"), u.Side, "/target");
            var tu = Unit(target);
            if (tu != null && !tu.Alive) throw Refuse("TARGET_DESTROYED", "That target is destroyed.");
            float dur = (float)c.Num("durationS", 60);
            bool sticky = c.Bool("sticky", true);
            ctl.SetOverrideTarget(id, target, dur, sticky);
            return JsonValue.Obj().Set("unit", HandleRegistry.VesselHandle(id)).Set("target", c.Str("target")).Set("durationS", dur).Set("sticky", sticky)
                .Set("text", u.Name + ": assigned " + TargetName(u.Side, target, c.Str("target")) + (sticky ? " sticky" : "") +
                             (dur > 0 ? " for " + dur.ToString("0", CultureInfo.InvariantCulture) + " s" : " until cleared") +
                             " (F1 SetOverrideTarget; a refusal arrives as bda.hook.override_lost reason NotEngageable).");
        }

        private JsonValue HookFireAuthority(CallContext c)
        {
            var ctl = Control();
            uint id = One(c, "unit");
            var u = Own(c, id, "/unit");
            NeedHook(HookSet.F3);
            RefuseIfManaged(id, "a direct fire authority");
            string mode = c.Str("mode");
            var m = mode == "hold" ? FireAuthorityMode.Hold : mode == "tight" ? FireAuthorityMode.Tight : FireAuthorityMode.Free;
            ctl.SetFireAuthority(id, m);
            return JsonValue.Obj().Set("unit", HandleRegistry.VesselHandle(id)).Set("mode", AuthText(m))
                .Set("text", u.Name + ": fireAuthority = " + AuthText(m).ToUpperInvariant() + " (F3, direct; bda.set_fire_authority is the C2 way).");
        }

        private JsonValue HookWaypoints(CallContext c)
        {
            var ctl = Control();
            uint id = One(c, "unit");
            var u = Own(c, id, "/unit");
            NeedHook(HookSet.F6);
            RefuseIfManaged(id, "a direct route");
            if (c.Bool("clear"))
            {
                ctl.ClearWaypoints(id);
                return JsonValue.Obj().Set("unit", HandleRegistry.VesselHandle(id)).Set("cleared", true).Set("text", u.Name + ": route cleared (F6 ClearWaypoints).");
            }
            var pts = c.Params.GetArr("points");
            if (pts == null || pts.Count == 0) throw SkizzyError.InvalidParam("/points", "give points [{lat, lon, altM}], or clear: true");
            var list = new List<GeoPoint>();
            for (int i = 0; i < pts.Count; i++)
            {
                var o = pts[i];
                list.Add(new GeoPoint(o.GetDouble("lat"), o.GetDouble("lon"), o.GetDouble("altM", u.Pos.Alt)));
            }
            float radius = (float)c.Num("radiusM", 300);
            int laps = c.Int("laps", 1);
            float speed = (float)c.Num("maxSpeedMps", 0);
            bool ok = ctl.SetWaypoints(id, list, radius, laps, speed);
            return JsonValue.Obj().Set("unit", HandleRegistry.VesselHandle(id)).Set("accepted", ok).Set("points", list.Count).Set("laps", laps).Set("radiusM", radius)
                .Set("text", u.Name + ": SetWaypoints -> " + (ok ? "True (" + list.Count + " points, " + laps + " lap(s), radius " + radius.ToString("0", CultureInfo.InvariantCulture) + " m)" : "False (the AI is not active; nothing changed)") +
                             ". Gates arrive as bda.hook.waypoint_reached, the end as bda.hook.route_completed.");
        }

        private JsonValue HookPatrol(CallContext c)
        {
            var ctl = Control();
            uint id = One(c, "unit");
            var u = Own(c, id, "/unit");
            NeedHook(HookSet.F6b);
            RefuseIfManaged(id, "direct patrol settings");
            bool yield = c.Bool("yieldToCombat", true);
            float leash = (float)c.Num("leashM", 0);
            var a = c.Params.GetObj("anchor");
            var anchor = a != null ? new GeoPoint(a.GetDouble("lat"), a.GetDouble("lon"), a.GetDouble("altM", 0)) : default(GeoPoint);
            ctl.SetPatrolEngage(id, yield, leash, anchor, a != null);
            return JsonValue.Obj().Set("unit", HandleRegistry.VesselHandle(id)).Set("yieldToCombat", yield).Set("leashM", leash)
                .Set("text", u.Name + ": waypointsYieldToCombat " + (yield ? "on" : "off") + ", leash " + (leash > 0 ? leash.ToString("0", CultureInfo.InvariantCulture) + " m" : "unlimited") +
                             (a != null ? ", anchor set" : "") + " (F6b). Engage/resume arrive as bda.hook.patrol_engage / patrol_resume.");
        }

        // ------------------------------------------------------------------ BDArmory settings (debug toggles for the checklists)

        private JsonValue SettingsGet(CallContext c)
        {
            var ops = Need();
            string filter = c.Str("filter", "DEBUG");
            var list = new List<AiField>();
            if (ops.ListSettings(filter, list) < 0) throw Refuse("unavailable", "BDArmory settings are not readable.");
            int limit = c.Int("limit", 50);
            var arr = JsonValue.Arr();
            var lines = new List<string>();
            for (int i = 0; i < list.Count && i < limit; i++)
            {
                var f = list[i];
                arr.Add(JsonValue.Obj().Set("name", f.Name).Set("type", f.Type).Set("value", f.Value));
                lines.Add(f.Name + "=" + f.Value);
            }
            return JsonValue.Obj().Set("settings", arr).Set("total", list.Count).Set("truncated", list.Count > limit)
                .Set("text", list.Count + " BDArmory setting(s) matching " + filter + (lines.Count > 0 ? ": " + string.Join(", ", lines.ToArray()) : "") + (list.Count > limit ? " ..." : ""));
        }

        private JsonValue SettingsSet(CallContext c)
        {
            var ops = Need();
            var vals = c.Params.GetObj("values");
            if (vals == null || vals.Count == 0) throw SkizzyError.InvalidParam("/values", "give values {DEBUG_AI: true, ...}");
            var applied = new List<string>();
            var res = JsonValue.Arr();
            for (int i = 0; i < vals.Count; i++)
            {
                string key = vals.KeyAt(i);
                var v = vals.ValueAt(i);
                string sv = v.Kind == JsonKind.String ? v.AsString() : v.Kind == JsonKind.Bool ? (v.AsBool() ? "true" : "false") : v.AsDouble().ToString("R", CultureInfo.InvariantCulture);
                string now;
                string err = ops.SetSetting(key, sv, out now);
                if (err != null && vals.Count == 1)
                {
                    if (err == "unknown_field") throw SkizzyError.InvalidParam("/values/" + key, "BDArmory has no setting " + key, "List them with bda.settings.get.");
                    throw Refuse(err, key + ": " + err.Replace('_', ' ') + ".");
                }
                res.Add(JsonValue.Obj().Set("name", key).Set("ok", err == null).Set("value", now).Set("reason", err));
                if (err == null) applied.Add(key + "=" + now);
            }
            return JsonValue.Obj().Set("results", res).Set("applied", applied.Count)
                .Set("text", "BDArmory settings: " + (applied.Count > 0 ? string.Join(", ", applied.ToArray()) : "nothing applied") + " (in memory; not saved to settings.cfg).");
        }
    }

    /// <summary>Publishes hook events as host events (bda.hook.*) as they happen, whether or not the C2 engine ticks.</summary>
    internal sealed class HookEventPublisher : IHookEvents
    {
        private readonly HostCore _h;
        private readonly List<UnitState> _units = new List<UnitState>();

        public HookEventPublisher(HostCore h) { _h = h; }

        private static string V(uint id) { return id == 0 ? null : HandleRegistry.VesselHandle(id); }

        private void Emit(string name, string summary, JsonValue data)
        {
            try { _h.Events.Emit(name, "bda", summary, data); }
            catch (Exception e) { _h.Log.Warn("bda: event " + name + " failed: " + e.Message); }
        }

        private string N(uint id)
        {
            if (id == 0) return "-";
            var w = _h.C2.World;
            if (w != null)
            {
                _units.Clear();
                try { w.GetUnits(_units); } catch (Exception) { }
                foreach (var u in _units) if (u.Id == id) return u.Name ?? V(id);
            }
            return V(id);
        }

        public void OnOverrideLost(uint unit, uint target, OverrideLostReason reason)
        {
            Emit("bda.hook.override_lost", N(unit) + " lost its external target " + N(target) + ": " + reason,
                JsonValue.Obj().Set("unit", V(unit)).Set("target", V(target)).Set("reason", reason.ToString()));
        }

        public void OnUnitDestroyed(uint unit)
        {
            Emit("bda.hook.unit_destroyed", N(unit) + " destroyed", JsonValue.Obj().Set("unit", V(unit)));
        }

        public void OnTargetDetected(uint target, string side)
        {
            Emit("bda.hook.target_detected", side + " detected " + N(target), JsonValue.Obj().Set("target", V(target)).Set("team", side));
        }

        public void OnTargetLost(uint target, string side)
        {
            Emit("bda.hook.target_lost", side + " lost " + N(target), JsonValue.Obj().Set("target", V(target)).Set("team", side));
        }

        public void OnWaypointReached(uint unit, int index, int lap)
        {
            Emit("bda.hook.waypoint_reached", N(unit) + " reached gate " + index + " lap " + lap, JsonValue.Obj().Set("unit", V(unit)).Set("index", index).Set("lap", lap));
        }

        public void OnRouteCompleted(uint unit)
        {
            Emit("bda.hook.route_completed", N(unit) + " completed its custom route", JsonValue.Obj().Set("unit", V(unit)));
        }

        public void OnPatrolEngage(uint unit, uint target)
        {
            Emit("bda.hook.patrol_engage", N(unit) + " broke off its route to engage " + N(target), JsonValue.Obj().Set("unit", V(unit)).Set("target", V(target)));
        }

        public void OnPatrolResume(uint unit, bool leashBroke)
        {
            Emit("bda.hook.patrol_resume", N(unit) + " resumed its route" + (leashBroke ? " (patrol leash)" : ""), JsonValue.Obj().Set("unit", V(unit)).Set("leashBroke", leashBroke));
        }

        public void OnMissileFired(uint shooter, uint missile, uint target)
        {
            Emit("bda.hook.missile_fired", N(shooter) + " fired at " + N(target), JsonValue.Obj().Set("unit", V(shooter)).Set("missile", V(missile)).Set("target", V(target)));
        }
    }

    /// <summary>A named set of AI settings by KSPField name; fields an AI version lacks are skipped.</summary>
    public sealed class AiPreset
    {
        public string Name;
        public string Kind;
        public string Description;
        public readonly List<KeyValuePair<string, string>> Values = new List<KeyValuePair<string, string>>();

        public AiPreset Set(string field, string value)
        {
            Values.Add(new KeyValuePair<string, string>(field, value));
            return this;
        }
    }

    public static class AiPresetBook
    {
        public static readonly List<AiPreset> All = new List<AiPreset>
        {
            new AiPreset { Name = "pilot.dogfight", Kind = "pilot", Description = "Low and fast turning fight: short extension, aggressive evasion." }
                .Set("defaultAltitude", "1500").Set("minAltitude", "300").Set("maxSpeed", "350").Set("idleSpeed", "200").Set("extendDistanceAirToAir", "300")
                .Set("maxSteer", "1").Set("evasionThreshold", "25"),
            new AiPreset { Name = "pilot.interceptor", Kind = "pilot", Description = "High and fast, long extension for missile shots." }
                .Set("defaultAltitude", "6000").Set("minAltitude", "1000").Set("maxSpeed", "600").Set("idleSpeed", "300").Set("extendDistanceAirToAir", "2500"),
            new AiPreset { Name = "pilot.strike", Kind = "pilot", Description = "Medium altitude ground attack with long extension." }
                .Set("defaultAltitude", "3000").Set("minAltitude", "500").Set("maxSpeed", "400").Set("extendDistanceAirToGround", "3000"),
            new AiPreset { Name = "surface.broadside", Kind = "surface", Description = "Ship fights broadside on at combat speed." }
                .Set("BroadsideAttack", "true").Set("CombatSpeed", "15").Set("MaxSpeed", "20"),
            new AiPreset { Name = "surface.bow_on", Kind = "surface", Description = "Ship closes bow on (no broadside)." }
                .Set("BroadsideAttack", "false").Set("CombatSpeed", "20").Set("MaxSpeed", "25"),
            new AiPreset { Name = "surface.sub_deep", Kind = "surface", Description = "Submarine runs deep and slow." }
                .Set("TargetDepth", "-120").Set("CombatSpeed", "8").Set("MaxSpeed", "12"),
            new AiPreset { Name = "vtol.hover_defence", Kind = "vtol", Description = "VTOL holds low and slow over its area." }
                .Set("defaultAltitude", "300").Set("minAltitude", "100").Set("CombatSpeed", "40").Set("MaxSpeed", "80"),
        };

        public static AiPreset Find(string name)
        {
            foreach (var p in All) if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }
    }
}
