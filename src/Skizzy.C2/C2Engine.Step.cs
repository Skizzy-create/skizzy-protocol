using System;
using System.Collections.Generic;
using System.Globalization;

namespace Skizzy.C2
{
    public sealed partial class C2Engine
    {
        private const int OrbitPoints = 8;
        private const int ManyLaps = 1000;
        private const float OrbitGateM = 800f;
        private const float PatternGateM = 1000f;
        /// <summary>An engage-on-contact divert ends when the unit's own sensors have not held the contact for this long.</summary>
        public double ContactLostS = 5;

        private readonly object _advanceOwner = new object();
        private readonly List<GeoPoint> _ptScratch2 = new List<GeoPoint>();

        // per-step scratch (main thread, one unit at a time)
        private uint _orderTarget;
        private bool _orderSticky;
        private float _leash;
        private WeaponsControl _wc;

        /// <summary>
        /// One unit's executor step: order progress first, then behaviours, then the control it wants from BDA.
        /// Precedence for the F1 assignment: self-defence, engage-on-contact divert, the order's target,
        /// VIP priority. Engagement orders drive the unit while they last; otherwise the movement order, then a hold.
        /// </summary>
        private void StepUnit(UnitRec r)
        {
            var w = r.Want;
            w.Reset();
            _orderTarget = 0;
            _orderSticky = true;
            _leash = 0;
            var p = Side(r.S.Side);
            bool air = UnitClasses.IsAircraft(r.S.Class);

            // 1. orders
            if (r.Engage != null) StepEngagement(r, p);
            bool engagementDrives = r.Engage != null && !IsShipMode(r.Engage);
            if (!engagementDrives)
            {
                if (r.Move != null) StepMovement(r, p);
                if (r.Move == null && r.Hold != null) StepHold(r);
            }
            _wc = EffectiveWc(r);
            bool hasOrder = r.Move != null || r.Engage != null || r.Hold != null;

            // 2. behaviours
            StepSelfDefence(r, p, hasOrder);
            StepDivert(r, p);
            StepVipPriority(r, p);
            StepAdvance(r, p);
            StepShipVip(r, p, hasOrder);
            StepEngageMissiles(r, p, hasOrder);

            // 3. the F1 assignment
            if (_wc != WeaponsControl.Hold)
            {
                if (!WantOverride(r, r.SelfDef, r.Doc.SelfDefenceSticky, (float)r.Doc.SelfDefenceOverrideS)
                    && !WantOverride(r, r.Divert, true, 0)
                    && !WantOverride(r, _orderTarget, _orderSticky, 0))
                    WantOverride(r, r.VipThreat, air, 0);
            }

            // 4. fire authority and filter (F3, F4)
            ComputeFire(r, p);

            // 5. patrol engage (F6b, pilot AI only)
            if (air && w.Route == RouteKind.Waypoints && _wc != WeaponsControl.Hold)
            {
                w.Yield = true;
                w.Leash = _leash;
            }

            // 6. ships' SAM range (row 25; posture proactive)
            w.RangeMax = UnitClasses.IsShip(r.S.Class) && r.S.HasWeaponManager &&
                         ((r.Engage != null && r.Engage.RangeMax) || (p.Managed && r.Doc.Posture == Posture.Proactive));

            // claims follow the assignment (WTA: one override per shooter)
            if (r.S.Side != null) SetClaim(r, w.Override);
        }

        private static bool IsShipMode(UnitOrder uo) { return uo.Type == OrderType.AttackNearest && uo.ShipToHome; }

        private bool WantOverride(UnitRec r, uint target, bool sticky, float duration)
        {
            if (target == 0 || r.InBackoff(target, _now) || AliveRec(target) == null) return false;
            r.Want.Override = target;
            r.Want.Sticky = sticky;
            r.Want.Duration = duration;
            return true;
        }

        /// <summary>
        /// Weapons control in force for this unit: doctrine, lifted to Tight for an explicit engagement order on a
        /// Hold unit (the order authorises its own targets), Free for all_attack, and the RTB weapons setting for
        /// aircraft returning to base (the more restrictive of doctrine and <c>rtbWeapons</c> unless the order set one).
        /// </summary>
        private WeaponsControl EffectiveWc(UnitRec r)
        {
            var d = r.Doc;
            var wc = d.WeaponsControl;
            var e = r.Engage;
            if (e != null)
            {
                if (e.ForceFree) return WeaponsControl.Free;
                return wc == WeaponsControl.Hold ? WeaponsControl.Tight : wc;
            }
            if (UnitClasses.IsAircraft(r.S.Class))
            {
                WeaponsControl? set = null;
                bool rtb = false;
                if (r.Move != null && r.Move.Type == OrderType.Rtb) { rtb = true; set = r.Move.Parent.Request.Weapons; }
                else if (r.Move == null && r.Hold != null && r.Hold.Kind == HoldKind.Marshal) { rtb = true; set = r.Hold.Weapons; }
                if (rtb) return set ?? (d.RtbWeapons > wc ? d.RtbWeapons : wc);
            }
            return wc;
        }

        // ------------------------------------------------------------------ engagement orders

        private void StepEngagement(UnitRec r, SidePicture p)
        {
            var uo = r.Engage;
            if (uo.Type == OrderType.Engage) StepEngage(r, p, uo);
            else if (uo.ShipToHome) uo.Phase = "weapons_free";
            else StepHunt(r, p, uo);
        }

        private static bool Gone(UnitRec t) { return t == null || t.Dead || (t.Present && !t.S.Alive); }

        private void StepEngage(UnitRec r, SidePicture p, UnitOrder uo)
        {
            if (r.S.MissilesMax > 0 && r.S.MissilesReady <= 0 && !r.S.HasGuns)
            {
                Winchester(r, uo, OrderStatus.Failed, Reasons.OutOfAmmo);
                return;
            }
            int alive = 0;
            double since = double.PositiveInfinity;
            uint firstAlive = 0;
            for (int i = 0; i < uo.TargetSet.Count; i++)
            {
                var t = Rec(uo.TargetSet[i]);
                if (Gone(t)) continue;
                alive++;
                if (firstAlive == 0) firstAlive = t.Id;
                double s = p.SinceInView(t.Id, _now);
                if (s < since) since = s;
            }
            if (alive == 0)
            {
                EndUnitOrder(r, uo, OrderStatus.Achieved, Reasons.TargetsDestroyed,
                    r.Name + ": target" + (uo.TargetSet.Count > 1 ? "s" : "") + " destroyed (" + uo.TargetSet.Count + "); attack complete.", true);
                return;
            }
            var curRec = Rec(uo.Assigned);
            bool curGone = Gone(curRec);
            bool curOk = !curGone && p.InView.Contains(uo.Assigned) && !r.InBackoff(uo.Assigned, _now);
            if (!curOk)
            {
                var next = PickEngageTarget(r, p, uo);
                if (next != null)
                {
                    if (next.Id != uo.Assigned)
                    {
                        string why = curGone ? (curRec != null ? curRec.Name : "target") + " destroyed" : (curRec != null ? curRec.Name : "target") + " not engageable";
                        uo.Assigned = next.Id;
                        Emit(C2EventKind.OrderProgress, "orders", uo.Parent.Side, uo.Parent.Id, r.Id, "retarget",
                            r.Name + ": " + why + "; re-assigned to " + next.Name + ", " + Km(r, next) + " km (" + alive + " left).");
                    }
                }
                else
                {
                    if (since > TargetLostS)
                    {
                        EndUnitOrder(r, uo, OrderStatus.Failed, Reasons.TargetLost,
                            r.Name + ": target" + (alive > 1 ? "s" : "") + " lost from the picture for " + TargetLostS.ToString("0", CultureInfo.InvariantCulture) + " s; attack ended.", true);
                        return;
                    }
                    if (curGone) uo.Assigned = firstAlive;
                }
            }
            uo.Phase = p.InView.Contains(uo.Assigned) ? "engaging" : "searching";
            _orderTarget = uo.Assigned;
            _orderSticky = true;
        }

        /// <summary>WTA for an engage order: the alive, in-view target with the fewest other shooters, then the nearest.</summary>
        private UnitRec PickEngageTarget(UnitRec r, SidePicture p, UnitOrder uo)
        {
            UnitRec best = null;
            int bc = int.MaxValue;
            double bd = double.MaxValue;
            for (int i = 0; i < uo.TargetSet.Count; i++)
            {
                uint id = uo.TargetSet[i];
                var t = AliveRec(id);
                if (t == null || !p.InView.Contains(id) || r.InBackoff(id, _now)) continue;
                int c = p.ClaimCount(id) - (r.Claim == id ? 1 : 0);
                double dd = Dist(r, t);
                if (c < bc || (c == bc && dd < bd)) { best = t; bc = c; bd = dd; }
            }
            return best;
        }

        /// <summary>Out of missiles during an attack order: RTB per doctrine (when a base exists), otherwise end and resume.</summary>
        private void Winchester(UnitRec r, UnitOrder uo, OrderStatus status, string reason)
        {
            bool rtb = UnitClasses.IsAircraft(r.S.Class) && r.Doc.WinchesterFor(r.S.Class) == WinchesterAction.Rtb && ResolveBase(null, r) != null;
            EndUnitOrder(r, uo, status, reason,
                r.Name + ": winchester (no missiles left); " + Orders.Wire(uo.Type) + " ended" + (rtb ? ", returning to base." : "."), !rtb);
            if (rtb)
            {
                var req = new OrderRequest { Type = OrderType.Rtb, Side = r.S.Side, Issuer = "c2", Role = IssuerRole.Script, Label = r.Name };
                req.Units.Add(r.Id);
                Issue(req);
            }
        }

        private void StepHunt(UnitRec r, SidePicture p, UnitOrder uo)
        {
            var d = r.Doc;
            if (IsWinchester(r))
            {
                var act = d.WinchesterFor(r.S.Class);
                if (act == WinchesterAction.Rtb || (act == WinchesterAction.GunsOnly && !r.S.HasGuns))
                {
                    Winchester(r, uo, OrderStatus.Achieved, Reasons.Winchester);
                    return;
                }
            }
            var curRec = Rec(uo.Assigned);
            var cur = AliveRec(uo.Assigned);
            bool valid = cur != null && p.InView.Contains(cur.Id) && !r.InBackoff(cur.Id, _now);
            if (!valid || _now >= uo.NextPick)
            {
                uo.NextPick = _now + d.HuntRepickS + (r.Id % 5) * 0.1;
                var pick = HuntPick(r, p);
                if (pick == null) uo.Assigned = 0;
                else if (pick.Id != uo.Assigned)
                {
                    bool announce = uo.Assigned == 0 || !valid;
                    uo.Assigned = pick.Id;
                    if (announce)
                        Emit(C2EventKind.OrderProgress, "orders", uo.Parent.Side, uo.Parent.Id, r.Id, "hunt_target",
                            r.Name + ": hunting " + pick.Name + " (" + (pick.S.IsMissile ? "missile" : UnitClasses.Name(pick.S.Class)) + "), " + Km(r, pick) + " km" +
                            (curRec != null && Gone(curRec) ? ", after " + curRec.Name + " was destroyed" : "") + ".");
                }
            }
            if (uo.Assigned == 0)
            {
                if (double.IsNaN(uo.IdleSince)) uo.IdleSince = _now;
                uo.Phase = "searching";
                if (_now - uo.IdleSince >= d.HuntIdleEndS)
                    EndUnitOrder(r, uo, OrderStatus.Achieved, Reasons.NothingLeft, r.Name + ": nothing left to hunt; attack nearest ended.", true);
                return;
            }
            uo.IdleSince = double.NaN;
            uo.Phase = "hunting";
            _orderTarget = uo.Assigned;
            _orderSticky = d.HuntSticky;
        }

        /// <summary>
        /// Row 26 (mockup huntTarget): the nearest identified hostile aircraft with fewer than
        /// <c>huntMaxClaimsPerAircraft</c> hunters (VIP threats first, then unclaimed, then closing), or a missile nobody
        /// engages; the missile wins when it is nearer than <c>huntMissileRatio</c> times the aircraft.
        /// </summary>
        private UnitRec HuntPick(UnitRec r, SidePicture p)
        {
            var d = r.Doc;
            bool armed = r.S.MissilesReady > 0 || (r.S.MissilesMax == 0 && r.S.HasGuns);
            UnitRec air = null;
            int bv = 0, bc = 0, bo = 0;
            double bd = 0;
            if (armed)
            {
                double reach = d.HuntAirReachKm * 1000;
                for (int i = 0; i < p.Hostiles.Count; i++)
                {
                    var t = AliveRec(p.Hostiles[i]);
                    if (t == null || !UnitClasses.IsAircraftTarget(t.S.Class) || !p.Identified.Contains(t.Id) || r.InBackoff(t.Id, _now)) continue;
                    double dd = Dist(r, t);
                    if (dd > reach) continue;
                    int c = p.ClaimCount(t.Id) - (r.Claim == t.Id ? 1 : 0);
                    if (c >= d.HuntMaxClaimsPerAircraft) continue;
                    int v = IsVipThreat(p, t, 1.0) ? 0 : 1;
                    int o = Closing(r, t) ? 0 : 1;
                    int cc = c > 0 ? 1 : 0;
                    if (air == null || v < bv || (v == bv && (cc < bc || (cc == bc && (o < bo || (o == bo && dd < bd))))))
                    {
                        air = t; bv = v; bc = cc; bo = o; bd = dd;
                    }
                }
            }
            UnitRec msl = null;
            int mv = 0;
            double md = 0;
            double mreach = d.HuntMissileReachKm * 1000 * (r.S.MissilesReady > 0 ? 1 : 0.5);
            for (int i = 0; i < p.HostileMissiles.Count; i++)
            {
                var m = AliveRec(p.HostileMissiles[i]);
                if (m == null || r.InBackoff(m.Id, _now)) continue;
                bool mine = r.Claim == m.Id;
                if (!mine && (p.ClaimCount(m.Id) > 0 || _world.FriendliesEngaging(p.Side, m.Id) > 0)) continue;
                double dd = Dist(r, m);
                if (dd > mreach) continue;
                int v = IsVipThreat(p, m, 1.0) ? 0 : 1;
                if (msl == null || v < mv || (v == mv && dd < md)) { msl = m; mv = v; md = dd; }
            }
            if (air != null && msl != null) return md < d.HuntMissileRatio * bd ? msl : air;
            return air ?? msl;
        }

        private bool Closing(UnitRec r, UnitRec t)
        {
            return _geo.RangeRate(r.S.Pos, r.S.HeadingDeg, r.S.SpeedMps, t.S.Pos, t.S.HeadingDeg, t.S.SpeedMps) < 0;
        }

        /// <summary>A hostile aircraft within the VIP radius of one of the side's VIPs, or a missile aimed at a VIP or inside that radius.</summary>
        private bool IsVipThreat(SidePicture p, UnitRec t, double factor)
        {
            if (p.Vips.Count == 0) return false;
            if (t.S.IsMissile && t.S.MissileTarget != 0 && p.Vips.Contains(t.S.MissileTarget)) return true;
            if (!t.S.IsMissile && !UnitClasses.IsAircraftTarget(t.S.Class)) return false;
            foreach (var id in p.Vips)
            {
                var v = AliveRec(id);
                if (v == null) continue;
                if (Dist(v, t) <= v.Doc.VipRadiusKm * 1000 * factor) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ movement orders

        private void StepMovement(UnitRec r, SidePicture p)
        {
            var uo = r.Move;
            if (uo.Status == OrderStatus.Suspended)
            {
                // the engagement that suspended it is gone without resuming it (defensive)
                uo.Status = OrderStatus.Executing;
                uo.Fed = false;
            }
            switch (uo.Type)
            {
                case OrderType.MoveTo: StepMoveTo(r, uo); break;
                case OrderType.MoveRoute: StepRoute(r, uo); break;
                case OrderType.HoldPattern: StepPattern(r, uo); break;
                case OrderType.Rtb:
                    if (UnitClasses.IsAircraft(r.S.Class)) StepRtbAir(r, uo); else StepRtbShip(r, uo);
                    break;
                case OrderType.DefendUnit: StepDefend(r, p, uo); break;
                case OrderType.Escort: StepEscort(r, uo); break;
            }
        }

        private bool RouteCurrent(UnitRec r, object owner, int version)
        {
            return r.RouteId != 0 && ReferenceEquals(r.RouteOwner, owner) && r.RouteVersion == version;
        }

        /// <summary>Makes <paramref name="pts"/> the unit's desired waypoint route (a new route id, fed at Apply).</summary>
        private void SetRoute(UnitRec r, object owner, int version, List<GeoPoint> pts)
        {
            r.RouteOwner = owner;
            r.RouteVersion = version;
            r.RouteId = ++_routeSerial;
            r.RoutePoints.Clear();
            r.RoutePoints.AddRange(pts);
            r.RouteCompleted = false;
            r.WaypointIndex = -1;
            r.WaypointLap = 0;
        }

        private void WantRoute(UnitRec r, float radius, int laps)
        {
            var w = r.Want;
            w.Route = RouteKind.Waypoints;
            w.RouteId = r.RouteId;
            w.Radius = radius;
            w.Laps = laps;
        }

        private void WantOnePoint(UnitRec r, object owner, int version, GeoPoint point, float radius)
        {
            if (!RouteCurrent(r, owner, version))
            {
                _ptScratch.Clear();
                _ptScratch.Add(point);
                SetRoute(r, owner, version, _ptScratch);
            }
            WantRoute(r, radius, 1);
        }

        private void WantOrbit(UnitRec r, object owner, int version, GeoPoint center, double radiusM, double alt)
        {
            if (!RouteCurrent(r, owner, version))
            {
                _ptScratch.Clear();
                Patterns.Ring(_geo, center, radiusM, OrbitPoints, alt, 0, _ptScratch);
                Patterns.RotateToNearest(_geo, _ptScratch, r.S.Pos, _ptScratch2);
                SetRoute(r, owner, version, _ptScratch);
            }
            WantRoute(r, OrbitGateM, ManyLaps);
        }

        private void WantFlyTo(UnitRec r, object owner, GeoPoint point)
        {
            if (!ReferenceEquals(r.RouteOwner, owner))
            {
                r.RouteOwner = owner;
                r.RouteId = 0;
                r.RoutePoints.Clear();
            }
            r.Want.Route = RouteKind.FlyTo;
            r.Want.FlyTo = point;
        }

        /// <summary>Re-targets a moving station only when it moved more than <c>refeedKm</c> (or changed altitude).</summary>
        private void Follow(UnitOrder uo, GeoPoint point, double refeedM)
        {
            if (!uo.Fed || _geo.Distance(uo.FedAnchor, point) > refeedM || Math.Abs(uo.FedAnchor.Alt - point.Alt) > 50)
            {
                uo.FedAnchor = point;
                uo.Fed = true;
                uo.RouteVersion++;
            }
        }

        private void Refeed(UnitOrder uo)
        {
            if (uo.Fed) return;
            uo.Fed = true;
            uo.RouteVersion++;
        }

        private void StepMoveTo(UnitRec r, UnitOrder uo)
        {
            bool air = UnitClasses.IsAircraft(r.S.Class);
            double arrive = (air ? r.Doc.ArrivalAirKm : r.Doc.ArrivalSurfaceKm) * 1000;
            if (_geo.Distance(r.S.Pos, uo.Dest) <= arrive || (air && ReferenceEquals(r.RouteOwner, uo) && r.RouteCompleted))
            {
                Arrive(r, uo, air);
                return;
            }
            Refeed(uo);
            if (air) WantOnePoint(r, uo, uo.RouteVersion, uo.Dest, (float)arrive);
            else WantFlyTo(r, uo, uo.Dest);
            uo.Phase = "transit";
        }

        private void StepRoute(UnitRec r, UnitOrder uo)
        {
            bool air = UnitClasses.IsAircraft(r.S.Class);
            var d = r.Doc;
            double gate = (air ? d.RouteGateAirKm : d.RouteGateSurfaceKm) * 1000;
            double arrive = (air ? d.ArrivalAirKm : d.ArrivalSurfaceKm) * 1000;
            int n = uo.Path.Count;
            bool last = uo.NextIndex >= n - 1;
            if (uo.NextIndex >= n || (ReferenceEquals(r.RouteOwner, uo) && r.RouteCompleted) || (last && _geo.Distance(r.S.Pos, uo.Dest) <= arrive))
            {
                Arrive(r, uo, air);
                return;
            }
            Refeed(uo);
            if (!RouteCurrent(r, uo, uo.RouteVersion))
            {
                _ptScratch.Clear();
                for (int k = uo.NextIndex; k < n; k++) _ptScratch.Add(uo.Path[k]);
                uo.FedStart = uo.NextIndex;
                SetRoute(r, uo, uo.RouteVersion, _ptScratch);
            }
            WantRoute(r, (float)gate, 1);
            uo.Phase = "transit";
        }

        /// <summary>A move_to or move_route is achieved on arrival and leaves a hold residual at the point.</summary>
        private void Arrive(UnitRec r, UnitOrder uo, bool air)
        {
            var at = uo.Dest;
            EndUnitOrder(r, uo, OrderStatus.Achieved, Reasons.Arrived,
                r.Name + (air ? " on station, orbiting the ordered point." : " arrived; holding position."), false);
            r.Hold = new HoldSpec
            {
                Kind = HoldKind.Point,
                Point = at,
                Alt = air ? (double.IsNaN(uo.Alt) ? Math.Max(300, r.S.Pos.Alt) : uo.Alt) : 0,
                RingM = r.Doc.StationOrbitKm * 1000,
                From = uo.Type,
            };
        }

        private void StepPattern(UnitRec r, UnitOrder uo)
        {
            var d = r.Doc;
            Refeed(uo);
            if (!RouteCurrent(r, uo, uo.RouteVersion))
            {
                _ptScratch.Clear();
                _ptScratch.AddRange(uo.Path);
                Patterns.RotateToNearest(_geo, _ptScratch, r.S.Pos, _ptScratch2);
                SetRoute(r, uo, uo.RouteVersion, _ptScratch);
            }
            WantRoute(r, PatternGateM, ManyLaps);
            _leash = (float)(d.PatrolLeashKm * 1000);
            if (!uo.Reached)
            {
                double best = double.MaxValue;
                for (int i = 0; i < uo.Path.Count; i++)
                {
                    double dd = _geo.Distance(r.S.Pos, uo.Path[i]);
                    if (dd < best) best = dd;
                }
                if (best <= d.ArrivalAirKm * 1000)
                {
                    uo.Reached = true;
                    Emit(C2EventKind.OrderProgress, "orders", uo.Parent.Side, uo.Parent.Id, r.Id, "on_station",
                        r.Name + " on the racetrack at " + uo.Alt.ToString("0", CultureInfo.InvariantCulture) + " m.");
                }
            }
            uo.Phase = r.PatrolEngaged ? "engaging" : uo.Reached ? "on_station" : "transit";
        }

        private void StepRtbAir(UnitRec r, UnitOrder uo)
        {
            var d = r.Doc;
            var b = AliveRec(uo.Base);
            if (b == null || !b.S.IsBase)
            {
                var lost = Rec(uo.Base);
                string lostName = lost != null ? lost.Name : "the base";
                var nb = ResolveBase(null, r);
                if (nb == null)
                {
                    EndUnitOrder(r, uo, OrderStatus.Failed, Reasons.BaseLost,
                        r.Name + ": " + lostName + " is lost and no carrier or airfield is left; RTB ended.", false);
                    return;
                }
                Emit(C2EventKind.OrderProgress, "orders", uo.Parent.Side, uo.Parent.Id, r.Id, "base_changed",
                    r.Name + ": " + lostName + " is lost; diverting to " + nb.Name + ".");
                uo.Base = nb.Id;
                uo.Fed = false;
                b = nb;
            }
            if (double.IsNaN(uo.Alt)) uo.Alt = Math.Max(d.MarshalBaseAltM, r.S.Pos.Alt);
            var m = Patterns.Marshal(_geo, b.S.Pos, b.S.HeadingDeg, d.MarshalKm * 1000);
            double arrive = d.ArrivalAirKm * 1000;
            if (_geo.Distance(r.S.Pos, m) <= arrive || (ReferenceEquals(r.RouteOwner, uo) && r.RouteCompleted))
            {
                ArriveMarshal(r, uo, b);
                return;
            }
            Follow(uo, m.WithAlt(uo.Alt), d.RefeedKm * 1000);
            WantOnePoint(r, uo, uo.RouteVersion, uo.FedAnchor, (float)arrive);
            uo.Phase = "transit";
        }

        private int NextMarshalSlot(uint baseId)
        {
            int s;
            _marshalSeq.TryGetValue(baseId, out s);
            _marshalSeq[baseId] = ++s;
            return s;
        }

        /// <summary>Position in the marshal stack: units at the same base with an earlier slot (mockup rank).</summary>
        private int MarshalRank(UnitRec r, uint baseId, int slot)
        {
            int rank = 0;
            for (int i = 0; i < _unitList.Count; i++)
            {
                var u = _unitList[i];
                if (u == r || !u.Alive || u.Hold == null || u.Hold.Kind != HoldKind.Marshal || u.Hold.Base != baseId) continue;
                if (u.Hold.Slot < slot) rank++;
            }
            return rank;
        }

        private void ArriveMarshal(UnitRec r, UnitOrder uo, UnitRec b)
        {
            var d = r.Doc;
            int slot = NextMarshalSlot(b.Id);
            int rank = MarshalRank(r, b.Id, slot);
            double alt = d.MarshalBaseAltM + rank * d.MarshalStepM;
            var weapons = uo.Parent.Request.Weapons;
            EndUnitOrder(r, uo, OrderStatus.Achieved, Reasons.OnStation,
                r.Name + " at the marshal point " + d.MarshalKm.ToString("0.#", CultureInfo.InvariantCulture) + " km astern of " + b.Name +
                ", stack position " + (rank + 1) + " at " + alt.ToString("0", CultureInfo.InvariantCulture) + " m. Landing waits for the recover adapter.", false);
            r.Hold = new HoldSpec
            {
                Kind = HoldKind.Marshal,
                Base = b.Id,
                Slot = slot,
                Alt = alt,
                RingM = d.MarshalOrbitKm * 1000,
                From = OrderType.Rtb,
                Weapons = weapons,
            };
        }

        private void StepRtbShip(UnitRec r, UnitOrder uo)
        {
            var d = r.Doc;
            GeoPoint dest;
            if (uo.ShipToHome) dest = r.S.HasHome ? r.S.Home : uo.Dest;
            else
            {
                var b = AliveRec(uo.Base);
                if (b == null)
                {
                    var lost = Rec(uo.Base);
                    var nb = ResolveBase(null, r);
                    if (nb == null)
                    {
                        EndUnitOrder(r, uo, OrderStatus.Failed, Reasons.BaseLost,
                            r.Name + ": " + (lost != null ? lost.Name : "the carrier") + " is lost and no friendly carrier is left; RTB ended.", false);
                        return;
                    }
                    Emit(C2EventKind.OrderProgress, "orders", uo.Parent.Side, uo.Parent.Id, r.Id, "base_changed",
                        r.Name + ": " + (lost != null ? lost.Name : "the carrier") + " is lost; joining " + nb.Name + ".");
                    uo.Base = nb.Id;
                    uo.Fed = false;
                    b = nb;
                }
                dest = _geo.Destination(b.S.Pos, b.S.HeadingDeg + uo.StationBearing, uo.StationDistM);
            }
            uo.Dest = dest;
            if (_geo.Distance(r.S.Pos, dest) <= d.ArrivalSurfaceKm * 1000)
            {
                EndUnitOrder(r, uo, OrderStatus.Achieved, Reasons.Arrived, r.Name + " back on station.", false);
                r.Hold = new HoldSpec
                {
                    Kind = HoldKind.Home,
                    Point = dest,
                    Base = uo.ShipToHome ? 0 : uo.Base,
                    Bearing = uo.StationBearing,
                    DistM = uo.StationDistM,
                    From = OrderType.Rtb,
                };
                return;
            }
            Follow(uo, dest, d.RefeedKm * 1000);
            WantFlyTo(r, uo, uo.FedAnchor);
            uo.Phase = "transit";
        }

        private void StepDefend(UnitRec r, SidePicture p, UnitOrder uo)
        {
            var d = r.Doc;
            var v = AliveRec(uo.Anchor);
            if (v == null)
            {
                var lost = Rec(uo.Anchor);
                EndUnitOrder(r, uo, OrderStatus.Failed, Reasons.ProtectedLost,
                    r.Name + ": " + (lost != null ? lost.Name : "the protected unit") + " is lost; protect order ended.", false);
                return;
            }
            if (!UnitClasses.IsAircraft(r.S.Class))
            {
                var st = _geo.Destination(v.S.Pos, v.S.HeadingDeg + uo.StationBearing, uo.RingRadiusM);
                Follow(uo, st, d.RefeedKm * 1000);
                WantFlyTo(r, uo, uo.FedAnchor);
                uo.Phase = "station";
                return;
            }
            if (double.IsNaN(uo.Alt)) uo.Alt = 1000;
            Follow(uo, v.S.Pos.WithAlt(uo.Alt), d.RefeedKm * 1000);
            WantOrbit(r, uo, uo.RouteVersion, uo.FedAnchor, uo.RingRadiusM, uo.Alt);

            var cur = AliveRec(uo.Assigned);
            bool valid = cur != null && p.InView.Contains(cur.Id) && Dist(cur, v) <= uo.StationDistM * 1.1 && !r.InBackoff(cur.Id, _now);
            if (!valid)
            {
                uo.Assigned = 0;
                if (_now >= uo.NextPick)
                {
                    uo.NextPick = _now + d.HuntRepickS;
                    var t = PickDefendThreat(r, p, v, uo.StationDistM);
                    if (t != null)
                    {
                        uo.Assigned = t.Id;
                        Emit(C2EventKind.OrderProgress, "orders", uo.Parent.Side, uo.Parent.Id, r.Id, "threat",
                            r.Name + ": engaging " + t.Name + ", a threat to " + v.Name + " at " + Km(v, t) + " km.");
                    }
                }
            }
            _orderTarget = uo.Assigned;
            _orderSticky = true;
            uo.Phase = uo.Assigned != 0 ? "engaging" : "escorting";
        }

        private UnitRec PickDefendThreat(UnitRec r, SidePicture p, UnitRec vip, double radiusM)
        {
            UnitRec best = null;
            int bc = int.MaxValue;
            double bd = double.MaxValue;
            bool missiles = r.Doc.AirEngageMissiles != AirEngageMissiles.Never;
            for (int pass = 0; pass < 2; pass++)
            {
                var list = pass == 0 ? p.Hostiles : p.HostileMissiles;
                if (pass == 1 && !missiles) break;
                for (int i = 0; i < list.Count; i++)
                {
                    var t = AliveRec(list[i]);
                    if (t == null || r.InBackoff(t.Id, _now)) continue;
                    if (!t.S.IsMissile && !UnitClasses.IsAircraftTarget(t.S.Class)) continue;
                    double dv = Dist(vip, t);
                    if (dv > radiusM && !(t.S.IsMissile && t.S.MissileTarget == vip.Id)) continue;
                    int c = p.ClaimCount(t.Id) - (r.Claim == t.Id ? 1 : 0);
                    if (c < bc || (c == bc && dv < bd)) { best = t; bc = c; bd = dv; }
                }
            }
            return best;
        }

        private void StepEscort(UnitRec r, UnitOrder uo)
        {
            var d = r.Doc;
            var l = AliveRec(uo.Anchor);
            if (l == null)
            {
                var lost = Rec(uo.Anchor);
                EndUnitOrder(r, uo, OrderStatus.Failed, Reasons.EscorteeLost,
                    r.Name + ": " + (lost != null ? lost.Name : "the escorted unit") + " is lost; escort ended.", false);
                return;
            }
            bool air = UnitClasses.IsAircraft(r.S.Class);
            double lateral = (air ? d.FormationSpacingAirKm : d.FormationSpacingSurfaceKm) * 1000;
            var slot = Patterns.EscortSlot(_geo, l.S.Pos, l.S.HeadingDeg, uo.Slot, uo.StationDistM, uo.MarshalSlot, lateral);
            slot.Alt = air ? uo.Alt : 0;
            Follow(uo, slot, d.RefeedKm * 1000);
            if (air)
            {
                WantOnePoint(r, uo, uo.RouteVersion, uo.FedAnchor, 1000f);
                _leash = (float)(d.EscortLeashKm * 1000);
            }
            else WantFlyTo(r, uo, uo.FedAnchor);
            bool on = _geo.Distance(r.S.Pos, slot) <= (air ? d.ArrivalAirKm : d.ArrivalSurfaceKm) * 1000;
            if (on && !uo.Reached)
            {
                uo.Reached = true;
                Emit(C2EventKind.OrderProgress, "orders", uo.Parent.Side, uo.Parent.Id, r.Id, "on_station",
                    r.Name + " in its escort slot " + (uo.Slot == EscortSlot.Ahead ? "ahead of " : uo.Slot == EscortSlot.Abreast ? "abreast of " : "astern of ") + l.Name + ".");
            }
            uo.Phase = r.PatrolEngaged ? "engaging" : on ? "on_station" : "transit";
        }

        // ------------------------------------------------------------------ holds

        private void StepHold(UnitRec r)
        {
            var h = r.Hold;
            var d = r.Doc;
            bool air = UnitClasses.IsAircraft(r.S.Class);
            switch (h.Kind)
            {
                case HoldKind.Point:
                    if (air) WantOrbit(r, h, h.Version, h.Point, h.RingM, h.Alt);
                    else WantFlyTo(r, h, h.Point);
                    break;
                case HoldKind.Marshal:
                {
                    var b = AliveRec(h.Base);
                    if (b == null)
                    {
                        var lost = Rec(h.Base);
                        var nb = ResolveBase(null, r);
                        if (nb == null)
                        {
                            Emit(C2EventKind.Info, "orders", r.S.Side, 0, r.Id, Reasons.BaseLost,
                                r.Name + ": " + (lost != null ? lost.Name : "the base") + " is lost and no carrier or airfield is left; leaving the marshal stack.");
                            r.Hold = null;
                            return;
                        }
                        Emit(C2EventKind.Info, "orders", r.S.Side, 0, r.Id, "base_changed",
                            r.Name + ": " + (lost != null ? lost.Name : "the base") + " is lost; moving to the marshal stack of " + nb.Name + ".");
                        h.Base = nb.Id;
                        h.Slot = NextMarshalSlot(nb.Id);
                        h.HasFed = false;
                        b = nb;
                    }
                    double alt = d.MarshalBaseAltM + MarshalRank(r, h.Base, h.Slot) * d.MarshalStepM;
                    var m = Patterns.Marshal(_geo, b.S.Pos, b.S.HeadingDeg, d.MarshalKm * 1000);
                    if (!h.HasFed || _geo.Distance(h.Fed, m) > d.RefeedKm * 1000 || Math.Abs(h.Alt - alt) > 1)
                    {
                        h.Fed = m;
                        h.HasFed = true;
                        h.Alt = alt;
                        h.Version++;
                    }
                    WantOrbit(r, h, h.Version, h.Fed, h.RingM, h.Alt);
                    break;
                }
                case HoldKind.Home:
                {
                    var at = h.Point;
                    if (h.Base != 0)
                    {
                        var b = AliveRec(h.Base);
                        if (b != null) at = _geo.Destination(b.S.Pos, b.S.HeadingDeg + h.Bearing, h.DistM);
                    }
                    if (!h.HasFed || _geo.Distance(h.Fed, at) > d.RefeedKm * 1000)
                    {
                        h.Fed = at;
                        h.HasFed = true;
                    }
                    WantFlyTo(r, h, h.Fed);
                    break;
                }
            }
        }

        // ------------------------------------------------------------------ behaviours

        private string TaskPhrase(UnitRec r)
        {
            if (r.Engage != null) return Orders.Wire(r.Engage.Type);
            if (r.Move != null) return Orders.Wire(r.Move.Type);
            if (r.Hold != null) return r.Hold.Kind == HoldKind.Marshal ? "the marshal stack" : "station";
            if (r.Advance != 0) return "the advance";
            return "guard";
        }

        /// <summary>Row 23: an own fighter or drone turns on a hostile fighter or drone inside <c>selfDefenceFighterKm</c>, ahead of its order.</summary>
        private void StepSelfDefence(UnitRec r, SidePicture p, bool hasOrder)
        {
            var d = r.Doc;
            bool eligible = UnitClasses.IsAirCombat(r.S.Class) && _wc != WeaponsControl.Hold && (r.S.MissilesReady > 0 || r.S.HasGuns) &&
                            (hasOrder || p.Managed) && d.SelfDefenceFighterKm > 0;
            if (!eligible)
            {
                r.SelfDef = 0;
                return;
            }
            double range = d.SelfDefenceFighterKm * 1000;
            if (r.SelfDef != 0)
            {
                var t = AliveRec(r.SelfDef);
                string why = null;
                if (t == null) { var g = Rec(r.SelfDef); why = (g != null ? g.Name : "the threat") + " destroyed"; }
                else if (!p.InView.Contains(t.Id)) why = t.Name + " lost from the picture";
                else
                {
                    double dd = Dist(r, t);
                    if (dd > range * 1.2 || (dd > range && !Closing(r, t))) why = t.Name + " opening, " + Km(r, t) + " km";
                }
                if (why == null) return;
                r.SelfDef = 0;
                Emit(C2EventKind.BackOnTask, "orders", r.S.Side, OrderIdOf(r), r.Id, "self_defence_end",
                    r.Name + ": " + why + "; back on " + TaskPhrase(r) + ".");
            }
            UnitRec best = null;
            double bd = range;
            for (int i = 0; i < p.Hostiles.Count; i++)
            {
                var t = AliveRec(p.Hostiles[i]);
                if (t == null || !UnitClasses.IsAirCombat(t.S.Class) || t.Id == _orderTarget || t.Id == r.Divert || r.InBackoff(t.Id, _now)) continue;
                double dd = Dist(r, t);
                if (dd <= bd) { best = t; bd = dd; }
            }
            if (best == null) return;
            r.SelfDef = best.Id;
            Emit(C2EventKind.Interruption, "orders", r.S.Side, OrderIdOf(r), r.Id, "self_defence",
                r.Name + ": self-defence against " + best.Name + " (" + UnitClasses.Name(best.S.Class) + "), " + Km(r, best) + " km; " + TaskPhrase(r) + " paused.");
        }

        /// <summary>Row 6: under engage on contact, an armed aircraft on move_to, move_route or engage diverts to a hostile its own sensors hold.</summary>
        private void StepDivert(UnitRec r, SidePicture p)
        {
            var d = r.Doc;
            bool task = (r.Engage != null && r.Engage.Type == OrderType.Engage) ||
                        (r.Engage == null && r.Move != null && (r.Move.Type == OrderType.MoveTo || r.Move.Type == OrderType.MoveRoute));
            bool eligible = task && d.OnContact == OnContact.Engage && _wc != WeaponsControl.Hold && UnitClasses.IsAircraft(r.S.Class) &&
                            r.S.HasWeaponManager && (r.S.MissilesReady > 0 || r.S.HasGuns);
            if (!eligible)
            {
                r.Divert = 0;
                return;
            }
            if (r.Divert != 0)
            {
                var t = AliveRec(r.Divert);
                bool held = t != null && r.Contacts.Contains(r.Divert);
                if (held) r.DivertSeenUt = _now;
                string why = null;
                if (t == null) { var g = Rec(r.Divert); why = "contact " + (g != null ? g.Name : "") + " destroyed"; }
                else if (!held && _now - r.DivertSeenUt >= ContactLostS) why = "lost contact with " + t.Name;
                if (why == null) return;
                r.Divert = 0;
                Emit(C2EventKind.BackOnTask, "orders", r.S.Side, OrderIdOf(r), r.Id, "divert_end",
                    r.Name + ": " + why + "; back on " + TaskPhrase(r) + ".");
            }
            if (_now < r.NextContactPick) return;
            r.NextContactPick = _now + d.ContactRepickS;
            double limit = d.OwnRadarM(r.S.Class);
            UnitRec best = null;
            double bd = double.MaxValue;
            for (int i = 0; i < r.Contacts.Count; i++)
            {
                var t = AliveRec(r.Contacts[i]);
                if (t == null || t.S.IsMissile || !Hostile(r, t) || t.Id == _orderTarget || r.InBackoff(t.Id, _now)) continue;
                if (r.Engage != null && r.Engage.TargetSet.Contains(t.Id)) continue;
                double dd = Dist(r, t);
                if (limit > 0 && dd > limit) continue;
                if (dd < bd) { best = t; bd = dd; }
            }
            if (best == null) return;
            r.Divert = best.Id;
            r.DivertSeenUt = _now;
            Emit(C2EventKind.Diversion, "orders", r.S.Side, OrderIdOf(r), r.Id, "divert",
                r.Name + ": own radar contact " + best.Name + " (" + UnitClasses.Name(best.S.Class) + ") at " + Km(r, best) + " km, diverting to engage; " +
                TaskPhrase(r) + " continues after.");
        }

        /// <summary>Row 13 (side-level VIP flag): an idle fighter or drone of a managed side takes the threat to a VIP with the fewest shooters.</summary>
        private void StepVipPriority(UnitRec r, SidePicture p)
        {
            if (UnitClasses.IsShip(r.S.Class)) return; // ships: StepShipVip
            var d = r.Doc;
            bool idle = r.Move == null && r.Engage == null && (r.Hold == null || r.Hold.Kind == HoldKind.Point);
            bool eligible = p.Managed && p.Vips.Count > 0 && idle && UnitClasses.IsAirCombat(r.S.Class) && _wc != WeaponsControl.Hold &&
                            r.S.HasWeaponManager && !IsWinchester(r);
            if (!eligible)
            {
                r.VipThreat = 0;
                return;
            }
            if (r.VipThreat != 0)
            {
                var t = AliveRec(r.VipThreat);
                if (t != null && p.InView.Contains(t.Id) && IsVipThreat(p, t, 1.25)) return;
                r.VipThreat = 0;
                Emit(C2EventKind.BackOnTask, "orders", r.S.Side, 0, r.Id, "vip_threat_end",
                    r.Name + ": VIP threat " + (t != null ? t.Name + " gone" : "destroyed") + "; back on " + TaskPhrase(r) + ".");
            }
            if (_now < r.NextVipPick) return;
            r.NextVipPick = _now + d.HuntRepickS;
            double reach = (r.S.Class == UnitClass.Drone ? d.VipReachDroneKm : d.VipReachFighterKm) * 1000;
            bool missiles = d.AirEngageMissiles != AirEngageMissiles.Never;
            UnitRec best = null;
            int bc = int.MaxValue;
            double bd = double.MaxValue;
            for (int pass = 0; pass < 2; pass++)
            {
                var list = pass == 0 ? p.Hostiles : p.HostileMissiles;
                if (pass == 1 && !missiles) break;
                for (int i = 0; i < list.Count; i++)
                {
                    var t = AliveRec(list[i]);
                    if (t == null || r.InBackoff(t.Id, _now) || !IsVipThreat(p, t, 1.0)) continue;
                    double dd = Dist(r, t);
                    if (dd > reach) continue;
                    int c = p.ClaimCount(t.Id);
                    if (c < bc || (c == bc && dd < bd)) { best = t; bc = c; bd = dd; }
                }
            }
            if (best == null) return;
            r.VipThreat = best.Id;
            Emit(C2EventKind.Diversion, "orders", r.S.Side, 0, r.Id, "vip_priority",
                r.Name + ": VIP priority, engaging " + best.Name + " (" + (best.S.IsMissile ? "missile" : UnitClasses.Name(best.S.Class)) + "), " + Km(r, best) + " km.");
        }

        /// <summary>
        /// Row 28: from the first identified hostile (any hostile in view under posture proactive), airborne fighters
        /// and drones of a managed side without an order get a one-point route to the nearest such hostile, re-picked
        /// every <c>advanceRepickS</c>. Guard stays on, so BDA attacks in range. Any order replaces it.
        /// </summary>
        private void StepAdvance(UnitRec r, SidePicture p)
        {
            var d = r.Doc;
            bool proactive = d.Posture == Posture.Proactive;
            bool eligible = p.Managed && d.AdvanceOnContact && UnitClasses.IsAirCombat(r.S.Class) && !r.S.Landed && r.S.AiActive &&
                            _wc != WeaponsControl.Hold && r.Move == null && r.Engage == null && r.Hold == null && r.VipThreat == 0 &&
                            !IsWinchester(r) && (proactive ? p.Hostiles.Count > 0 : p.AnyIdentified);
            if (!eligible)
            {
                r.Advance = 0;
                return;
            }
            var cur = AliveRec(r.Advance);
            if (cur == null || !p.InView.Contains(cur.Id) || _now >= r.NextAdvancePick)
            {
                r.NextAdvancePick = _now + d.AdvanceRepickS;
                UnitRec best = null;
                double bd = double.MaxValue;
                for (int i = 0; i < p.Hostiles.Count; i++)
                {
                    var t = AliveRec(p.Hostiles[i]);
                    if (t == null || (!proactive && !p.Identified.Contains(t.Id))) continue;
                    double dd = Dist(r, t);
                    if (dd < bd) { best = t; bd = dd; }
                }
                r.Advance = best != null ? best.Id : 0;
                cur = best;
            }
            if (cur == null) return;
            if (!p.AdvanceAnnounced)
            {
                p.AdvanceAnnounced = true;
                Emit(C2EventKind.AdvanceStarted, "orders", p.Side, 0, r.Id, "advance",
                    "Advance on contact: " + p.Side + " fighters and drones without orders advance toward the nearest " + (proactive ? "" : "identified ") + "hostile (" + cur.Name + ").");
            }
            var point = cur.S.Pos.WithAlt(Math.Max(1000, r.S.Pos.Alt));
            if (!ReferenceEquals(r.RouteOwner, _advanceOwner) || _geo.Distance(r.AdvanceFed, point) > d.RefeedKm * 1000)
            {
                r.AdvanceFed = point;
                r.AdvanceVersion++;
            }
            WantOnePoint(r, _advanceOwner, r.AdvanceVersion, r.AdvanceFed, 1000f);
        }

        /// <summary>
        /// Row 14: ships of a side with VIPs get the threats to those VIPs as F2 secondary targets (missiles aimed at a
        /// VIP first, then aircraft inside the VIP radius), and the top one as a non-sticky F1 assignment once it is in range.
        /// </summary>
        private void StepShipVip(UnitRec r, SidePicture p, bool hasOrder)
        {
            if (!UnitClasses.IsShip(r.S.Class)) return;
            r.VipThreat = 0;
            if (!r.S.HasWeaponManager || !(p.Managed || hasOrder) || p.Vips.Count == 0 || _wc == WeaponsControl.Hold) return;
            var w = r.Want;
            _ids2.Clear();
            for (int pass = 0; pass < 2; pass++)
            {
                var list = pass == 0 ? p.HostileMissiles : p.Hostiles;
                int start = _ids2.Count;
                for (int i = 0; i < list.Count; i++)
                {
                    var t = AliveRec(list[i]);
                    if (t != null && IsVipThreat(p, t, 1.0)) _ids2.Add(t.Id);
                }
                SortByDistance(r, _ids2, start);
            }
            if (_ids2.Count == 0) return;
            w.HasSecondary = true;
            for (int i = 0; i < _ids2.Count && i < 8; i++) w.Secondary.Add(_ids2[i]);
            var top = AliveRec(_ids2[0]);
            if (top != null && (r.S.MissileRangeM <= 0 || Dist(r, top) <= r.S.MissileRangeM)) r.VipThreat = top.Id;
        }

        private void SortByDistance(UnitRec r, List<uint> ids, int start)
        {
            // insertion sort: lists are short and the order must be deterministic
            for (int i = start + 1; i < ids.Count; i++)
            {
                uint id = ids[i];
                double di = Dist(r, Rec(id));
                int j = i - 1;
                while (j >= start && Dist(r, Rec(ids[j])) > di)
                {
                    ids[j + 1] = ids[j];
                    j--;
                }
                ids[j + 1] = id;
            }
        }

        /// <summary>Row 12: fighters and drones engage missiles per <c>airEngageMissiles</c> (when no hostile aircraft is within view range).</summary>
        private void StepEngageMissiles(UnitRec r, SidePicture p, bool hasOrder)
        {
            if (!UnitClasses.IsAirCombat(r.S.Class) || !r.S.HasWeaponManager || !(p.Managed || hasOrder)) return;
            var d = r.Doc;
            bool on;
            switch (d.AirEngageMissiles)
            {
                case AirEngageMissiles.Always: on = true; break;
                case AirEngageMissiles.Never: on = false; break;
                default: on = !HostileAirWithin(r, p, d.AirEngageMissilesViewKm * 1000); break;
            }
            r.Want.SetEngageMissiles = true;
            r.Want.EngageMissiles = on;
        }

        private bool HostileAirWithin(UnitRec r, SidePicture p, double rangeM)
        {
            for (int i = 0; i < p.Hostiles.Count; i++)
            {
                var t = AliveRec(p.Hostiles[i]);
                if (t != null && UnitClasses.IsAircraftTarget(t.S.Class) && Dist(r, t) <= rangeM) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ fire authority and filter

        /// <summary>
        /// Rows 5, 8, 13: Hold = F3 Hold. Free = F3 Free, no filter, except that a unit staying on task (onContact task
        /// during move_to, move_route or engage) or protecting a VIP gets Tight with a filter. Tight = F3 Tight with the
        /// filter. The filter admits the task's targets, close threats (hostile fighters and drones inside
        /// <c>selfDefenceFighterKm</c>; any hostile aircraft for ships) and the current assignment (a filter rejection
        /// drops even a sticky F1). Missiles are never filtered by BDA.
        /// </summary>
        private void ComputeFire(UnitRec r, SidePicture p)
        {
            var w = r.Want;
            if (_wc == WeaponsControl.Hold)
            {
                w.Fire = FireAuthorityMode.Hold;
                return;
            }
            bool defendAir = r.Engage == null && r.Move != null && r.Move.Type == OrderType.DefendUnit && UnitClasses.IsAircraft(r.S.Class);
            bool stayOnTask = r.Doc.OnContact == OnContact.Task &&
                              ((r.Engage != null && r.Engage.Type == OrderType.Engage) ||
                               (r.Engage == null && r.Move != null && (r.Move.Type == OrderType.MoveTo || r.Move.Type == OrderType.MoveRoute)));
            if (_wc == WeaponsControl.Free && !defendAir && !stayOnTask)
            {
                w.Fire = FireAuthorityMode.Free;
                return;
            }
            w.Fire = FireAuthorityMode.Tight;
            w.UseFilter = true;
            var allow = w.Allow;
            // task targets
            if (r.Engage != null)
            {
                if (r.Engage.Type == OrderType.Engage)
                {
                    for (int i = 0; i < r.Engage.TargetSet.Count; i++)
                        if (AliveRec(r.Engage.TargetSet[i]) != null) allow.Add(r.Engage.TargetSet[i]);
                }
                else if (r.Engage.Assigned != 0) allow.Add(r.Engage.Assigned);
            }
            if (defendAir)
            {
                var v = AliveRec(r.Move.Anchor);
                if (v != null)
                {
                    for (int i = 0; i < p.Hostiles.Count; i++)
                    {
                        var t = AliveRec(p.Hostiles[i]);
                        if (t != null && Dist(v, t) <= r.Move.StationDistM) allow.Add(t.Id);
                    }
                }
            }
            for (int i = 0; i < w.Secondary.Count; i++) allow.Add(w.Secondary[i]);
            // close threats: hostile fighters and drones inside selfDefenceFighterKm (any hostile aircraft for surface units)
            double close = r.Doc.SelfDefenceFighterKm * 1000;
            bool selfAir = UnitClasses.IsAircraft(r.S.Class);
            for (int i = 0; i < p.Hostiles.Count; i++)
            {
                var t = AliveRec(p.Hostiles[i]);
                if (t == null || Dist(r, t) > close) continue;
                if (selfAir ? UnitClasses.IsAirCombat(t.S.Class) : UnitClasses.IsAircraftTarget(t.S.Class)) allow.Add(t.Id);
            }
            if (r.SelfDef != 0) allow.Add(r.SelfDef);
            if (r.Divert != 0) allow.Add(r.Divert);
            if (r.VipThreat != 0) allow.Add(r.VipThreat);
            if (w.Override != 0) allow.Add(w.Override);
        }
    }
}
