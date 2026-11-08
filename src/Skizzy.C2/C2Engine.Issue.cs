using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Skizzy.C2
{
    public sealed partial class C2Engine
    {
        /// <summary>
        /// Validates and issues an order (c2.order.issue). Every recipient gets an outcome: accepted, or refused with
        /// a typed code and a reason (23 §6 point 10 rule 1). Accepted units supersede or suspend their previous orders
        /// with reports. If no unit accepts, <see cref="IssueResult.Order"/> is null and <see cref="IssueResult.Code"/>
        /// says why. Takes effect at the next <see cref="Tick"/>.
        /// </summary>
        public IssueResult Issue(OrderRequest req)
        {
            _now = _world.Now;
            var res = new IssueResult();
            if (req == null) return RefuseAll(res, null, RefusalCode.InvalidParams, "No order given.");
            RefreshUnits();
            RefreshDoctrine();

            string paramError = ValidateParams(req);
            if (paramError != null) return RefuseAll(res, req, RefusalCode.InvalidParams, paramError);

            if (req.SideScope && req.Role != IssuerRole.Seat && req.Role != IssuerRole.Referee && req.Role != IssuerRole.Director)
                return RefuseAll(res, req, RefusalCode.NotPermitted, "Side-wide orders need the command seat or the referee (not_seat).");

            var recipients = new List<uint>();
            CollectRecipients(req, recipients);
            if (recipients.Count == 0)
                return RefuseAll(res, req, RefusalCode.EmptySelection, req.SideScope ? "No units of side " + req.Side + " can take this order." : "Select one or more of your own units first.");

            var accepted = new List<UnitOrder>();
            var substitutes = new List<UnitRec>();
            switch (req.Type)
            {
                case OrderType.MoveTo:
                case OrderType.MoveRoute:
                    IssueMove(req, recipients, res, accepted);
                    break;
                case OrderType.Engage:
                    IssueEngage(req, recipients, res, accepted);
                    break;
                case OrderType.HoldPattern:
                    IssuePattern(req, recipients, res, accepted);
                    break;
                case OrderType.Rtb:
                    IssueRtb(req, recipients, res, accepted);
                    break;
                case OrderType.DefendUnit:
                    IssueDefend(req, recipients, res, accepted);
                    break;
                case OrderType.AttackNearest:
                case OrderType.AllAttack:
                    IssueHunt(req, recipients, res, accepted, substitutes);
                    break;
                case OrderType.Escort:
                    IssueEscort(req, recipients, res, accepted);
                    break;
            }

            ReportRefusals(req, res);

            if (accepted.Count == 0)
            {
                res.Code = FirstRefusal(res);
                res.Message = res.Summary;
                if (substitutes.Count > 0) IssueSubstituteRtb(req, substitutes, res);
                return res;
            }

            var order = new Order
            {
                Id = _nextOrderId++,
                Type = req.Type,
                Issuer = req.Issuer,
                Side = req.Side ?? AliveRec(accepted[0].Unit)?.S.Side,
                Request = req,
                Status = OrderStatus.Executing,
                CreatedUt = _now,
                UpdatedUt = _now,
            };
            _orders[order.Id] = order;
            _active.Add(order);
            for (int i = 0; i < accepted.Count; i++)
            {
                var uo = accepted[i];
                uo.Parent = order;
                uo.Status = OrderStatus.Executing;
                uo.StartedUt = _now;
                order.Units.Add(uo);
            }
            res.Order = order;
            string summary = Summary(req, order, res);
            res.Summary = res.Summary == null ? summary : summary + " " + res.Summary;
            Emit(C2EventKind.OrderAccepted, "orders", order.Side, order.Id, 0, null, summary);
            for (int i = 0; i < accepted.Count; i++) Bind(Rec(accepted[i].Unit), accepted[i]);
            if (substitutes.Count > 0) IssueSubstituteRtb(req, substitutes, res);
            return res;
        }

        // ------------------------------------------------------------------ validation

        private static bool ValidPoint(GeoPoint p)
        {
            return !double.IsNaN(p.Lat) && !double.IsNaN(p.Lon) && !double.IsInfinity(p.Lat) && !double.IsInfinity(p.Lon) &&
                   p.Lat >= -90 && p.Lat <= 90 && p.Lon >= -540 && p.Lon <= 540 && !p.IsZero;
        }

        private string ValidateParams(OrderRequest r)
        {
            switch (r.Type)
            {
                case OrderType.MoveTo:
                    if (!ValidPoint(r.Point)) return "move_to needs a point (lat, lon).";
                    break;
                case OrderType.MoveRoute:
                    if (r.Route == null || r.Route.Count == 0) return "move_route needs at least one point.";
                    for (int i = 0; i < r.Route.Count; i++) if (!ValidPoint(r.Route[i])) return "move_route point " + i + " is not a valid position.";
                    break;
                case OrderType.Engage:
                    if (r.Target == 0 && (r.Targets == null || r.Targets.Count == 0)) return "engage needs a target.";
                    break;
                case OrderType.HoldPattern:
                    if (!ValidPoint(r.PatternA)) return "hold_pattern needs point a.";
                    if (!r.PatternB.IsZero && !ValidPoint(r.PatternB)) return "hold_pattern point b is not a valid position.";
                    if (!double.IsNaN(r.WidthKm) && (r.WidthKm < 1 || r.WidthKm > 100)) return "hold_pattern widthKm must be 1-100.";
                    break;
                case OrderType.DefendUnit:
                    if (r.Protect == 0) return "defend_unit needs the unit to protect.";
                    if (!double.IsNaN(r.RadiusKm) && (r.RadiusKm < 1 || r.RadiusKm > 500)) return "defend_unit radiusKm must be 1-500.";
                    break;
                case OrderType.Escort:
                    if (r.EscortOf == 0) return "escort needs the unit to escort.";
                    break;
                case OrderType.AllAttack:
                    if (!r.SideScope || r.Side == null) return "all_attack applies to a whole side; set side.";
                    break;
            }
            if (r.SideScope && r.Side == null) return "A side-wide order needs side.";
            if (!double.IsNaN(r.AltitudeM) && (r.AltitudeM < 0 || r.AltitudeM > 30000)) return "altitudeM must be 0-30000.";
            return null;
        }

        private void CollectRecipients(OrderRequest req, List<uint> into)
        {
            var seen = new HashSet<uint>();
            if (req.Units != null)
                for (int i = 0; i < req.Units.Count; i++)
                    if (req.Units[i] != 0 && seen.Add(req.Units[i])) into.Add(req.Units[i]);
            if (req.Group != null)
            {
                var g = GroupMembers(req.Group);
                for (int i = 0; i < g.Count; i++) if (seen.Add(g[i])) into.Add(g[i]);
            }
            if (req.SideScope)
            {
                for (int i = 0; i < _unitList.Count; i++)
                {
                    var r = _unitList[i];
                    if (!r.Alive || r.S.IsMissile || r.S.Side != req.Side) continue;
                    if (req.DomainFilter.HasValue && r.S.Domain != req.DomainFilter.Value) continue;
                    if (seen.Add(r.Id)) into.Add(r.Id);
                }
            }
        }

        /// <summary>Checks common to every order. Returns null when the unit may take it.</summary>
        private UnitOutcome CheckUnit(OrderRequest req, uint id, out UnitRec r)
        {
            r = Rec(id);
            if (r == null || (r.LastSeenUt == 0 && !r.Present && !r.Dead))
                return Refusal(id, null, RefusalCode.UnknownUnit, "unknown unit v:" + id.ToString(CultureInfo.InvariantCulture));
            if (r.Dead || (r.Present && !r.S.Alive))
                return Refusal(id, r.Name, RefusalCode.UnitDestroyed, r.Name + " was destroyed");
            if (!r.Present)
                return Refusal(id, r.Name, RefusalCode.UnknownUnit, r.Name + " is not loaded (out of physics range)");
            if (r.S.IsMissile)
                return Refusal(id, r.Name, RefusalCode.NotApplicable, r.Name + " is a missile in flight");
            if (req.Side != null && r.S.Side != req.Side)
                return Refusal(id, r.Name, RefusalCode.NotOwnSide, r.Name + " belongs to " + (r.S.Side ?? "no side"));
            return null;
        }

        private static UnitOutcome Refusal(uint id, string name, RefusalCode code, string reason)
        {
            return new UnitOutcome { Unit = id, Name = name, Accepted = false, Code = code, Reason = reason };
        }

        private bool NeedsAi(UnitRec r, IssueResult res)
        {
            if (r.S.AiActive) return true;
            res.Units.Add(Refusal(r.Id, r.Name, RefusalCode.AiInactive, r.Name + ": its BDA AI is not active (take off or activate it first)"));
            return false;
        }

        private bool NeedsHooks(UnitRec r, HookSet needed, IssueResult res)
        {
            var missing = needed & ~_world.Hooks;
            if (missing == HookSet.None) return true;
            res.Units.Add(Refusal(r.Id, r.Name, RefusalCode.HookMissing, r.Name + ": needs BDArmory Overdrive hook(s) " + missing));
            return false;
        }

        private UnitOrder NewUnitOrder(UnitRec r, OrderType type, IssueResult res)
        {
            res.Units.Add(new UnitOutcome { Unit = r.Id, Name = r.Name, Accepted = true });
            return new UnitOrder { Unit = r.Id, Type = type, Status = OrderStatus.Executing, Phase = "starting" };
        }

        private bool InViewNow(string side, uint target)
        {
            var p = Picture(side);
            if (p != null && p.InView.Contains(target)) return true;
            if (_world.SideDetects(side, target)) return true;
            for (int i = 0; i < _unitList.Count; i++)
            {
                var r = _unitList[i];
                if (!r.Alive || r.S.Side != side || r.S.IsMissile) continue;
                _ids.Clear();
                _world.GetOwnContacts(r.Id, _ids);
                if (_ids.Contains(target)) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ per type

        private void IssueMove(OrderRequest req, List<uint> recipients, IssueResult res, List<UnitOrder> accepted)
        {
            var ok = new List<UnitRec>();
            foreach (var id in recipients)
            {
                UnitRec r;
                var refusal = CheckUnit(req, id, out r);
                if (refusal != null) { res.Units.Add(refusal); continue; }
                if (r.S.Class == UnitClass.Airfield || r.S.Domain == Domain.Land)
                {
                    res.Units.Add(Refusal(id, r.Name, RefusalCode.NotApplicable, r.Name + " cannot move"));
                    continue;
                }
                if (!NeedsAi(r, res)) continue;
                if (UnitClasses.IsAircraft(r.S.Class) || req.Type == OrderType.MoveRoute)
                {
                    if (!NeedsHooks(r, HookSet.F6, res)) continue;
                }
                ok.Add(r);
            }
            for (int i = 0; i < ok.Count; i++)
            {
                var r = ok[i];
                bool air = UnitClasses.IsAircraft(r.S.Class);
                double sp = (air ? r.Doc.FormationSpacingAirKm : r.Doc.FormationSpacingSurfaceKm) * 1000;
                double e, n;
                Patterns.FormationOffset(i, ok.Count, sp, out e, out n);
                var uo = NewUnitOrder(r, req.Type, res);
                uo.Alt = air ? (!double.IsNaN(req.AltitudeM) ? req.AltitudeM : Math.Max(300, r.S.Pos.Alt)) : 0;
                if (req.Type == OrderType.MoveTo)
                {
                    uo.Dest = _geo.Offset(req.Point, e, n);
                    uo.Dest.Alt = uo.Alt;
                }
                else
                {
                    uo.Path = new List<GeoPoint>(req.Route.Count);
                    for (int k = 0; k < req.Route.Count; k++)
                    {
                        var p = _geo.Offset(req.Route[k], e, n);
                        p.Alt = air ? (!double.IsNaN(req.AltitudeM) ? req.AltitudeM : (req.Route[k].Alt > 0 ? req.Route[k].Alt : uo.Alt)) : 0;
                        uo.Path.Add(p);
                    }
                    uo.Dest = uo.Path[uo.Path.Count - 1];
                }
                uo.Phase = "transit";
                accepted.Add(uo);
            }
        }

        private void IssueEngage(OrderRequest req, List<uint> recipients, IssueResult res, List<UnitOrder> accepted)
        {
            // target and target set
            var targets = new List<uint>();
            string side = req.Side ?? FirstSide(recipients);
            UnitRec primary = null;
            if (req.Target != 0)
            {
                primary = Rec(req.Target);
                RefusalCode code = RefusalCode.None;
                string why = null;
                if (primary == null || (!primary.Present && !primary.Dead)) { code = RefusalCode.TargetUnknown; why = "unknown target v:" + req.Target.ToString(CultureInfo.InvariantCulture); }
                else if (!primary.Alive) { code = RefusalCode.TargetDestroyed; why = "target " + primary.Name + " is already destroyed"; }
                else if (side != null && (primary.S.Side == null || !_world.AreHostile(side, primary.S.Side))) { code = RefusalCode.TargetNotHostile; why = primary.Name + " is not hostile to " + side; }
                else if (side != null && !InViewNow(side, primary.Id)) { code = RefusalCode.TargetNotInView; why = primary.Name + " is not in " + side + "'s picture"; }
                if (code != RefusalCode.None)
                {
                    foreach (var id in recipients) res.Units.Add(Refusal(id, Rec(id)?.Name, code, why));
                    return;
                }
                targets.Add(primary.Id);
            }
            if (req.Targets != null)
            {
                foreach (var t in req.Targets)
                {
                    var tr = AliveRec(t);
                    if (tr == null || targets.Contains(t)) continue;
                    if (side != null && (tr.S.Side == null || !_world.AreHostile(side, tr.S.Side))) continue;
                    targets.Add(t);
                }
                if (primary == null && targets.Count > 0) primary = Rec(targets[0]);
            }
            else if (primary != null && !primary.S.IsMissile)
            {
                // attacking one member spreads the shooters across its group (learning, row 4)
                for (int i = 0; i < _unitList.Count; i++)
                {
                    var t = _unitList[i];
                    if (!t.Alive || t == primary || t.S.IsMissile || t.S.Side != primary.S.Side) continue;
                    bool member = primary.S.Group != null ? t.S.Group == primary.S.Group
                        : t.S.Class == primary.S.Class && Dist(t, primary) <= primary.Doc.TargetGroupRadiusKm * 1000;
                    if (member && (side == null || InViewNow(side, t.Id))) targets.Add(t.Id);
                }
            }
            if (targets.Count == 0)
            {
                foreach (var id in recipients) res.Units.Add(Refusal(id, Rec(id)?.Name, RefusalCode.TargetUnknown, "no valid target"));
                return;
            }
            int j = 0;
            foreach (var id in recipients)
            {
                UnitRec r;
                var refusal = CheckUnit(req, id, out r);
                if (refusal != null) { res.Units.Add(refusal); continue; }
                if (!r.S.HasWeaponManager) { res.Units.Add(Refusal(id, r.Name, RefusalCode.NoWeaponManager, r.Name + " has no weapon manager")); continue; }
                if (!r.S.IsArmed) { res.Units.Add(Refusal(id, r.Name, RefusalCode.NotArmed, r.Name + " carries no weapons")); continue; }
                if (r.S.MissilesMax > 0 && r.S.MissilesReady <= 0 && !r.S.HasGuns)
                {
                    res.Units.Add(Refusal(id, r.Name, RefusalCode.Winchester, r.Name + ": no missiles left, attack refused. Send it home with rtb?"));
                    continue;
                }
                if (!NeedsHooks(r, HookSet.F1, res)) continue;
                var uo = NewUnitOrder(r, OrderType.Engage, res);
                uo.TargetSet.AddRange(targets);
                uo.Assigned = targets[j++ % targets.Count];
                uo.Phase = "engaging";
                accepted.Add(uo);
            }
        }

        private void IssuePattern(OrderRequest req, List<uint> recipients, IssueResult res, List<UnitOrder> accepted)
        {
            var a = req.PatternA;
            var b = req.PatternB;
            if (b.IsZero || _geo.Distance(a, b) < 3000) b = _geo.Destination(a, 90, 20000);
            var counters = new Dictionary<int, int>();
            foreach (var id in recipients)
            {
                UnitRec r;
                var refusal = CheckUnit(req, id, out r);
                if (refusal != null) { res.Units.Add(refusal); continue; }
                if (!UnitClasses.IsAircraft(r.S.Class))
                {
                    res.Units.Add(Refusal(id, r.Name, RefusalCode.NotApplicable, r.Name + ": ships cannot fly a holding pattern. Use move_to or move_route"));
                    continue;
                }
                if (!NeedsAi(r, res)) continue;
                if (!NeedsHooks(r, HookSet.F6 | HookSet.F6b, res)) continue;
                int kind = r.S.Class == UnitClass.Drone ? 1 : r.S.Class == UnitClass.Aew ? 2 : 0;
                int k;
                counters.TryGetValue(kind, out k);
                counters[kind] = k + 1;
                double baseAlt = !double.IsNaN(req.AltitudeM) ? req.AltitudeM : r.Doc.PatternBaseAltM(r.S.Class);
                double step = !double.IsNaN(req.AltStackM) ? req.AltStackM : r.Doc.PatternStepM(r.S.Class);
                double width = (!double.IsNaN(req.WidthKm) ? req.WidthKm : r.Doc.RacetrackWidthKm) * 1000;
                var uo = NewUnitOrder(r, OrderType.HoldPattern, res);
                uo.Alt = baseAlt + k * step;
                uo.Path = new List<GeoPoint>();
                Patterns.Racetrack(_geo, a, b, width, req.TurnRight, uo.Alt, uo.Path);
                uo.Dest = _geo.Midpoint(a, b);
                uo.Phase = "transit";
                accepted.Add(uo);
            }
        }

        /// <summary>RTB base resolution per unit (09 §2.1.1): explicit base, home, nearest friendly base.</summary>
        private UnitRec ResolveBase(OrderRequest req, UnitRec r)
        {
            if (req != null && req.Base != 0)
            {
                var b = AliveRec(req.Base);
                if (b != null && b.S.IsBase && b.S.Side == r.S.Side) return b;
            }
            var home = AliveRec(r.S.HomeBase);
            if (home != null && home.S.IsBase && home.S.Side == r.S.Side) return home;
            UnitRec best = null;
            double bd = double.MaxValue;
            for (int i = 0; i < _unitList.Count; i++)
            {
                var c = _unitList[i];
                if (!c.Alive || !c.S.IsBase || c.S.Side != r.S.Side || c == r) continue;
                double d = Dist(r, c);
                if (d < bd) { bd = d; best = c; }
            }
            return best;
        }

        private void IssueRtb(OrderRequest req, List<uint> recipients, IssueResult res, List<UnitOrder> accepted)
        {
            foreach (var id in recipients)
            {
                UnitRec r;
                var refusal = CheckUnit(req, id, out r);
                if (refusal != null) { res.Units.Add(refusal); continue; }
                var uo = BuildRtb(req, r, res);
                if (uo != null) accepted.Add(uo);
            }
        }

        private UnitOrder BuildRtb(OrderRequest req, UnitRec r, IssueResult res)
        {
            if (r.S.IsBase)
            {
                res.Units.Add(Refusal(r.Id, r.Name, RefusalCode.NotApplicable, r.Name + " is a " + UnitClasses.Name(r.S.Class) + " (a base)"));
                return null;
            }
            if (UnitClasses.IsAircraft(r.S.Class))
            {
                var b = ResolveBase(req, r);
                if (b == null) { res.Units.Add(Refusal(r.Id, r.Name, RefusalCode.NoBase, r.Name + ": no carrier or airfield left")); return null; }
                if (!NeedsAi(r, res)) return null;
                if (!NeedsHooks(r, HookSet.F6, res)) return null;
                var uo = NewUnitOrder(r, OrderType.Rtb, res);
                uo.Base = b.Id;
                uo.Phase = "transit";
                return uo;
            }
            if (r.S.Domain == Domain.Surface || r.S.Domain == Domain.Subsurface)
            {
                var b = r.S.HasHome ? null : ResolveBase(req, r);
                if (!r.S.HasHome && b == null) { res.Units.Add(Refusal(r.Id, r.Name, RefusalCode.NoBase, r.Name + ": no home station and no friendly carrier")); return null; }
                if (!NeedsAi(r, res)) return null;
                var uo = NewUnitOrder(r, OrderType.Rtb, res);
                uo.ShipToHome = r.S.HasHome;
                uo.Base = b != null ? b.Id : 0;
                uo.StationBearing = 180;
                uo.StationDistM = 3000;
                uo.Phase = "transit";
                return uo;
            }
            res.Units.Add(Refusal(r.Id, r.Name, RefusalCode.NotApplicable, r.Name + " cannot return to base"));
            return null;
        }

        private void IssueDefend(OrderRequest req, List<uint> recipients, IssueResult res, List<UnitOrder> accepted)
        {
            var vip = Rec(req.Protect);
            string side = req.Side ?? FirstSide(recipients);
            RefusalCode code = RefusalCode.None;
            string why = null;
            if (vip == null || (!vip.Present && !vip.Dead)) { code = RefusalCode.TargetUnknown; why = "unknown unit to protect v:" + req.Protect.ToString(CultureInfo.InvariantCulture); }
            else if (!vip.Alive) { code = RefusalCode.TargetDestroyed; why = vip.Name + " is already lost"; }
            else if (side != null && vip.S.Side != side) { code = RefusalCode.NotApplicable; why = "can only protect own units, not " + vip.Name; }
            if (code != RefusalCode.None)
            {
                foreach (var id in recipients) res.Units.Add(Refusal(id, Rec(id)?.Name, code, why));
                return;
            }
            var ships = new List<UnitRec>();
            var air = new List<UnitRec>();
            foreach (var id in recipients)
            {
                UnitRec r;
                var refusal = CheckUnit(req, id, out r);
                if (refusal != null) { res.Units.Add(refusal); continue; }
                if (r == vip) { res.Units.Add(Refusal(id, r.Name, RefusalCode.NotApplicable, r.Name + " cannot protect itself")); continue; }
                if (!NeedsAi(r, res)) continue;
                if (UnitClasses.IsAircraft(r.S.Class))
                {
                    if (!NeedsHooks(r, HookSet.F1 | HookSet.F4 | HookSet.F6, res)) continue;
                    air.Add(r);
                }
                else ships.Add(r);
            }
            if (air.Count + ships.Count > 0) Side(vip.S.Side).Vips.Add(vip.Id);
            foreach (var r in air)
            {
                var uo = NewUnitOrder(r, OrderType.DefendUnit, res);
                uo.Anchor = vip.Id;
                uo.RingRadiusM = r.Doc.ProtectOrbitKm * 1000 + (r.Id % 3) * 1500;
                uo.StationDistM = (!double.IsNaN(req.RadiusKm) ? req.RadiusKm : r.Doc.VipRadiusKm) * 1000; // threat radius
                uo.Alt = !double.IsNaN(req.AltitudeM) ? req.AltitudeM : Math.Max(1000, r.S.Pos.Alt);
                uo.Phase = "escorting";
                accepted.Add(uo);
            }
            for (int i = 0; i < ships.Count; i++)
            {
                var r = ships[i];
                var uo = NewUnitOrder(r, OrderType.DefendUnit, res);
                uo.Anchor = vip.Id;
                uo.StationBearing = 360.0 * i / ships.Count;
                uo.RingRadiusM = 3000;
                uo.StationDistM = (!double.IsNaN(req.RadiusKm) ? req.RadiusKm : r.Doc.VipRadiusKm) * 1000;
                uo.Phase = "station";
                accepted.Add(uo);
            }
        }

        private void IssueHunt(OrderRequest req, List<uint> recipients, IssueResult res, List<UnitOrder> accepted, List<UnitRec> substitutes)
        {
            foreach (var id in recipients)
            {
                UnitRec r;
                var refusal = CheckUnit(req, id, out r);
                if (refusal != null) { res.Units.Add(refusal); continue; }
                if (r.S.Class == UnitClass.Aew || r.S.Class == UnitClass.Submarine || r.S.Class == UnitClass.Airfield)
                {
                    res.Units.Add(Refusal(id, r.Name, RefusalCode.NotApplicable, r.Name + ": not armed for this"));
                    continue;
                }
                if (!r.S.HasWeaponManager) { res.Units.Add(Refusal(id, r.Name, RefusalCode.NoWeaponManager, r.Name + " has no weapon manager")); continue; }
                if (!r.S.IsArmed) { res.Units.Add(Refusal(id, r.Name, RefusalCode.NotArmed, r.Name + " carries no weapons")); continue; }
                bool ship = UnitClasses.IsShip(r.S.Class);
                if (!ship && IsWinchester(r) && r.Doc.WinchesterFor(r.S.Class) == WinchesterAction.Rtb)
                {
                    // winchester aircraft refuse the claim and get rtb (09 §2.1.3)
                    var outcome = Refusal(id, r.Name, RefusalCode.Winchester, r.Name + ": winchester, sent home instead");
                    outcome.SubstitutedBy = OrderType.Rtb;
                    res.Units.Add(outcome);
                    substitutes.Add(r);
                    continue;
                }
                if (!ship && !NeedsAi(r, res)) continue;
                if (!NeedsHooks(r, ship ? HookSet.F3 : HookSet.F1, res)) continue;
                var uo = NewUnitOrder(r, OrderType.AttackNearest, res);
                uo.Phase = ship ? "weapons_free" : "hunting";
                uo.ShipToHome = ship; // ship mode: weapons free at full range, movement order continues
                uo.RangeMax = ship && (req.Type == OrderType.AllAttack || req.ExtendRange);
                uo.ForceFree = ship || req.Type == OrderType.AllAttack;
                accepted.Add(uo);
            }
        }

        private void IssueSubstituteRtb(OrderRequest parent, List<UnitRec> units, IssueResult res)
        {
            var rtb = new OrderRequest { Type = OrderType.Rtb, Side = parent.Side, Issuer = parent.Issuer, Role = parent.Role, Label = "Winchester units" };
            foreach (var r in units) rtb.Units.Add(r.Id);
            var sub = Issue(rtb);
            foreach (var u in sub.Units)
            {
                var o = res.For(u.Unit);
                if (o != null && !u.Accepted) o.Reason += " (rtb refused: " + u.Reason + ")";
            }
        }

        private void IssueEscort(OrderRequest req, List<uint> recipients, IssueResult res, List<UnitOrder> accepted)
        {
            var leader = Rec(req.EscortOf);
            string side = req.Side ?? FirstSide(recipients);
            RefusalCode code = RefusalCode.None;
            string why = null;
            if (leader == null || (!leader.Present && !leader.Dead)) { code = RefusalCode.TargetUnknown; why = "unknown unit to escort"; }
            else if (!leader.Alive) { code = RefusalCode.TargetDestroyed; why = leader.Name + " is already lost"; }
            else if (side != null && leader.S.Side != side) { code = RefusalCode.NotApplicable; why = "can only escort own units, not " + leader.Name; }
            if (code != RefusalCode.None)
            {
                foreach (var id in recipients) res.Units.Add(Refusal(id, Rec(id)?.Name, code, why));
                return;
            }
            int index = 0;
            foreach (var id in recipients)
            {
                UnitRec r;
                var refusal = CheckUnit(req, id, out r);
                if (refusal != null) { res.Units.Add(refusal); continue; }
                if (r == leader) { res.Units.Add(Refusal(id, r.Name, RefusalCode.NotApplicable, r.Name + " cannot escort itself")); continue; }
                if (!NeedsAi(r, res)) continue;
                if (UnitClasses.IsAircraft(r.S.Class) && !NeedsHooks(r, HookSet.F6 | HookSet.F6b, res)) continue;
                var uo = NewUnitOrder(r, OrderType.Escort, res);
                uo.Anchor = leader.Id;
                uo.Slot = req.Slot;
                uo.StationDistM = (!double.IsNaN(req.DistanceKm) ? req.DistanceKm : (req.Slot == EscortSlot.Ahead ? r.Doc.EscortAheadKm : 3)) * 1000;
                uo.MarshalSlot = index++;
                uo.Alt = !double.IsNaN(req.AltitudeM) ? req.AltitudeM : (UnitClasses.IsAircraft(r.S.Class) ? Math.Max(300, r.S.Pos.Alt) : 0);
                uo.Phase = "transit";
                accepted.Add(uo);
            }
        }

        // ------------------------------------------------------------------ binding

        /// <summary>Gives a unit its new order: engagement orders suspend the movement order; movement orders supersede both.</summary>
        private void Bind(UnitRec r, UnitOrder uo)
        {
            string by = " by " + uo.Parent.Handle + " " + Orders.Wire(uo.Parent.Type);
            bool shipModifier = uo.Type == OrderType.AttackNearest && uo.ShipToHome;
            if (Orders.IsEngagement(uo.Type))
            {
                if (r.Engage != null) EndUnitOrder(r, r.Engage, OrderStatus.Superseded, Reasons.Superseded, r.Name + ": " + Orders.Wire(r.Engage.Type) + " superseded" + by + ".", false);
                if (!shipModifier && r.Move != null && r.Move.Status == OrderStatus.Executing)
                {
                    r.Move.Status = OrderStatus.Suspended;
                    Emit(C2EventKind.OrderSuspended, "orders", r.Move.Parent.Side, r.Move.Parent.Id, r.Id, "suspended",
                        r.Name + ": " + Orders.Wire(r.Move.Type) + " suspended" + by + "; it resumes when the attack ends.");
                }
                r.Engage = uo;
            }
            else
            {
                if (r.Engage != null) EndUnitOrder(r, r.Engage, OrderStatus.Superseded, Reasons.Superseded, r.Name + ": " + Orders.Wire(r.Engage.Type) + " superseded" + by + ".", false);
                if (r.Move != null) EndUnitOrder(r, r.Move, OrderStatus.Superseded, Reasons.Superseded, r.Name + ": " + Orders.Wire(r.Move.Type) + " superseded" + by + ".", false);
                r.Hold = null;
                r.Move = uo;
            }
            if (r.Claim != 0) SetClaim(r, 0);
            r.Divert = 0;
            r.SelfDef = 0;
            r.Advance = 0;
            r.VipThreat = 0;
            r.AiFailures = 0;
        }

        // ------------------------------------------------------------------ reports

        private IssueResult RefuseAll(IssueResult res, OrderRequest req, RefusalCode code, string message)
        {
            res.Code = code;
            res.Message = message;
            res.Summary = (req != null ? Orders.Wire(req.Type) : "order") + " refused: " + message;
            Emit(C2EventKind.OrderRefused, "orders", req?.Side, 0, 0, Orders.Wire(code), res.Summary);
            return res;
        }

        private static RefusalCode FirstRefusal(IssueResult res)
        {
            foreach (var u in res.Units) if (!u.Accepted) return u.Code;
            return RefusalCode.EmptySelection;
        }

        private void ReportRefusals(OrderRequest req, IssueResult res)
        {
            var sb = new StringBuilder();
            int n = 0;
            RefusalCode first = RefusalCode.None;
            foreach (var u in res.Units)
            {
                if (u.Accepted) continue;
                if (n == 0) first = u.Code;
                if (n < 6) { if (n > 0) sb.Append("; "); sb.Append(u.Reason); }
                n++;
            }
            if (n == 0) return;
            if (n > 6) sb.Append("; and ").Append(n - 6).Append(" more");
            string text = Orders.Wire(req.Type) + " refused for " + n + " unit" + (n > 1 ? "s" : "") + ": " + sb + ".";
            res.Summary = text;
            Emit(C2EventKind.OrderRefused, "orders", req.Side, 0, 0, Orders.Wire(first), text);
        }

        private string Names(Order o)
        {
            if (o.Units.Count > 4) return o.Units.Count + " units";
            var sb = new StringBuilder();
            for (int i = 0; i < o.Units.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(Rec(o.Units[i].Unit)?.Name);
            }
            return sb.ToString();
        }

        private string Summary(OrderRequest req, Order o, IssueResult res)
        {
            string who = req.Label ?? Names(o);
            var first = o.Units[0];
            switch (req.Type)
            {
                case OrderType.MoveTo:
                    return who + ": move to " + req.Point + ".";
                case OrderType.MoveRoute:
                    return who + ": follow route, " + req.Route.Count + " point" + (req.Route.Count > 1 ? "s" : "") + ".";
                case OrderType.Engage:
                {
                    var t = Rec(first.TargetSet.Count > 0 ? first.TargetSet[0] : req.Target);
                    int n = first.TargetSet.Count;
                    return who + ": attack " + (t != null ? t.Name : "target") + (n > 1 ? " and the rest of its group (" + n + " targets)" : "") + ".";
                }
                case OrderType.HoldPattern:
                {
                    var b = req.PatternB.IsZero || _geo.Distance(req.PatternA, req.PatternB) < 3000 ? _geo.Destination(req.PatternA, 90, 20000) : req.PatternB;
                    double width = !double.IsNaN(req.WidthKm) ? req.WidthKm : _doctrine.Defaults.RacetrackWidthKm;
                    return who + ": racetrack holding pattern, " + KmText(_geo.Distance(req.PatternA, b)) + " km legs, " +
                           width.ToString("0.#", CultureInfo.InvariantCulture) + " km wide, " +
                           _geo.Bearing(req.PatternA, b).ToString("000", CultureInfo.InvariantCulture) + "\u00b0" +
                           (o.Units.Count > 1 ? ", stacked by altitude." : " at " + first.Alt.ToString("0", CultureInfo.InvariantCulture) + " m.");
                }
                case OrderType.Rtb:
                {
                    var bases = new List<string>();
                    int air = 0, ships = 0;
                    foreach (var uo in o.Units)
                    {
                        var b = Rec(uo.Base);
                        if (UnitClasses.IsAircraft(Rec(uo.Unit).S.Class)) { air++; if (b != null && !bases.Contains(b.Name)) bases.Add(b.Name); }
                        else ships++;
                    }
                    var d = _doctrine.Defaults;
                    return who + ": RTB" + (bases.Count > 0 ? " to " + string.Join(" and ", bases.ToArray()) : "") + "." +
                           (air > 0 ? " " + air + " aircraft to the marshal stack " + d.MarshalKm.ToString("0.#", CultureInfo.InvariantCulture) + " km astern; landing waits for the recover adapter." : "") +
                           (ships > 0 ? " " + ships + " ship" + (ships > 1 ? "s" : "") + " back to station." : "");
                }
                case OrderType.DefendUnit:
                {
                    var v = Rec(req.Protect);
                    return who + ": protect " + v.Name + ". They stay with it and engage anything that comes within " +
                           KmText(first.StationDistM) + " km.";
                }
                case OrderType.AttackNearest:
                case OrderType.AllAttack:
                {
                    int air = 0, ships = 0;
                    foreach (var uo in o.Units) { if (uo.ShipToHome) ships++; else air++; }
                    var notArmed = new List<string>();
                    foreach (var u in res.Units) if (!u.Accepted && u.Code == RefusalCode.NotApplicable && notArmed.Count < 4) notArmed.Add(u.Name);
                    return (req.Type == OrderType.AllAttack ? (req.Label ?? "All " + req.Side + " forces") : who) + ": attack nearest. " +
                           air + " aircraft hunt the nearest hostiles and any missile nobody is engaging" +
                           (ships > 0 ? "; " + ships + " ship" + (ships > 1 ? "s" : "") + " weapons free" + (req.Type == OrderType.AllAttack || req.ExtendRange ? " at full range" : "") : "") + "." +
                           (notArmed.Count > 0 ? " Not armed for this: " + string.Join(", ", notArmed.ToArray()) + "." : "");
                }
                case OrderType.Escort:
                {
                    var l = Rec(req.EscortOf);
                    return who + ": escort " + l.Name + ", " + (req.Slot == EscortSlot.Ahead ? "screening " + KmText(first.StationDistM) + " km ahead" : req.Slot == EscortSlot.Abreast ? "abreast" : "astern") + ".";
                }
            }
            return who + ": " + Orders.Wire(req.Type) + ".";
        }

        private string FirstSide(List<uint> recipients)
        {
            foreach (var id in recipients)
            {
                var r = Rec(id);
                if (r != null && r.S.Side != null) return r.S.Side;
            }
            return null;
        }
    }
}
