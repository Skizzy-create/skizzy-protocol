using System;
using System.Collections.Generic;

namespace Skizzy.C2
{
    public sealed partial class C2Engine
    {
        /// <summary>Seconds between retries of a BDA command the AI refused (AI inactive).</summary>
        public double AiRetryS = 1;

        /// <summary>
        /// Writes the difference between <see cref="UnitRec.Want"/> and <see cref="UnitRec.Have"/> through
        /// <see cref="IBdaControl"/>. Order: filter contents and F4, F3, F1, F6b, F6 / fly-to, F2, missile settings,
        /// guard. The filter is updated before F1 so a new assignment is never rejected by a stale filter.
        /// </summary>
        private void Apply(UnitRec r)
        {
            var w = r.Want;
            var h = r.Have;
            uint id = r.Id;
            bool air = UnitClasses.IsAircraft(r.S.Class);

            // F4 (contents are live: the binding's delegate reads the TargetFilter object)
            if (w.UseFilter)
            {
                if (r.Filter == null) r.Filter = new TargetFilter(id);
                r.Filter.SetTo(w.Allow);
                if (!h.Filter)
                {
                    _ctl.SetTargetFilter(id, r.Filter);
                    h.Filter = true;
                }
            }
            else if (h.Filter)
            {
                _ctl.SetTargetFilter(id, null);
                h.Filter = false;
                r.Filter?.Clear();
            }

            // F3
            if (w.Fire != h.Fire)
            {
                _ctl.SetFireAuthority(id, w.Fire);
                h.Fire = w.Fire;
            }

            // F1
            if (w.Override == 0)
            {
                if (h.Override != 0)
                {
                    h.Override = 0;
                    h.Sticky = false;
                    _ctl.ClearOverrideTarget(id);
                }
            }
            else if (w.Override != h.Override || w.Sticky != h.Sticky)
            {
                h.Override = w.Override;
                h.Sticky = w.Sticky;
                h.Duration = w.Duration;
                _ctl.SetOverrideTarget(id, w.Override, w.Duration, w.Sticky);
            }

            // F6b (before the route so a new route starts with the right settings)
            if (air && (w.Yield != h.Yield || w.Leash != h.Leash || w.HasAnchor != h.HasAnchor || (w.HasAnchor && !Same(w.Anchor, h.Anchor))))
            {
                _ctl.SetPatrolEngage(id, w.Yield, w.Leash, w.Anchor, w.HasAnchor);
                h.Yield = w.Yield;
                h.Leash = w.Leash;
                h.Anchor = w.Anchor;
                h.HasAnchor = w.HasAnchor;
            }

            // F6 / fly-to
            ApplyRoute(r);

            // F2
            if (w.HasSecondary != h.HasSecondary || (w.HasSecondary && !SameList(w.Secondary, h.Secondary)))
            {
                _ctl.SetSecondaryTargets(id, w.HasSecondary ? w.Secondary : null, false);
                h.HasSecondary = w.HasSecondary;
                h.Secondary.Clear();
                if (w.HasSecondary) h.Secondary.AddRange(w.Secondary);
            }

            // row 12; when the engine stops driving it (release, side no longer managed) the captured setting comes back
            if (w.SetEngageMissiles)
            {
                if (!h.EngageMissilesKnown || h.EngageMissiles != w.EngageMissiles)
                {
                    _ctl.SetEngageMissiles(id, w.EngageMissiles);
                    h.EngageMissilesKnown = true;
                    h.EngageMissiles = w.EngageMissiles;
                }
            }
            else if (h.EngageMissilesKnown)
            {
                _ctl.RestoreEngageMissiles(id);
                h.EngageMissilesKnown = false;
                h.EngageMissiles = false;
                r.MissilesKnown = false;
            }

            // row 25
            if (w.RangeMax != h.RangeMax)
            {
                _ctl.SetMissileRangeMax(id, w.RangeMax);
                h.RangeMax = w.RangeMax;
            }

            // guard mode on for units Skizzy drives (row 8: Free and Tight are guard on; Hold scans with guard on)
            if (r.S.GuardMode) r.GuardForced = false;
            else if (r.Managed && r.S.HasWeaponManager && !r.GuardForced)
            {
                _ctl.SetGuardMode(id, true);
                r.GuardForced = true;
            }
        }

        private void ApplyRoute(UnitRec r)
        {
            var w = r.Want;
            var h = r.Have;
            uint id = r.Id;
            switch (w.Route)
            {
                case RouteKind.None:
                    if (h.Route == RouteKind.Waypoints) _ctl.ClearWaypoints(id);
                    else if (h.Route == RouteKind.FlyTo) _ctl.ReleaseCommand(id);
                    h.Route = RouteKind.None;
                    h.RouteId = 0;
                    break;
                case RouteKind.Waypoints:
                {
                    if (h.Route == RouteKind.Waypoints && h.RouteId == w.RouteId) break;
                    if (w.RouteId == r.CompletedRouteId) break; // flown to the end; not fed again
                    if (_now < r.AiRetryAt) break;
                    if (h.Route == RouteKind.FlyTo) _ctl.ReleaseCommand(id);
                    if (_ctl.SetWaypoints(id, r.RoutePoints, w.Radius, w.Laps, 0))
                    {
                        h.Route = RouteKind.Waypoints;
                        h.RouteId = w.RouteId;
                        r.AiFailures = 0;
                    }
                    else
                    {
                        if (h.Route == RouteKind.FlyTo) { h.Route = RouteKind.None; h.RouteId = 0; }
                        AiFailure(r);
                    }
                    break;
                }
                case RouteKind.FlyTo:
                {
                    if (h.Route == RouteKind.FlyTo && Same(h.FlyTo, w.FlyTo)) break;
                    if (_now < r.AiRetryAt) break;
                    if (h.Route == RouteKind.Waypoints)
                    {
                        _ctl.ClearWaypoints(id);
                        h.Route = RouteKind.None;
                        h.RouteId = 0;
                    }
                    if (_ctl.CommandFlyTo(id, w.FlyTo))
                    {
                        h.Route = RouteKind.FlyTo;
                        h.RouteId = 0;
                        h.FlyTo = w.FlyTo;
                        r.AiFailures = 0;
                    }
                    else AiFailure(r);
                    break;
                }
            }
        }

        /// <summary>The AI refused a route (not active). After <see cref="AiFailureLimit"/> tries the owning order fails with ai_inactive.</summary>
        private void AiFailure(UnitRec r)
        {
            r.AiFailures++;
            r.AiRetryAt = _now + AiRetryS;
            if (r.AiFailures < AiFailureLimit) return;
            r.AiFailures = 0;
            r.AiRetryAt = 0;
            var owner = r.RouteOwner;
            r.RouteOwner = null;
            r.RouteId = 0;
            var uo = owner as UnitOrder;
            if (uo != null && !uo.IsTerminal)
            {
                EndUnitOrder(r, uo, OrderStatus.Failed, Reasons.AiInactive,
                    r.Name + ": its BDA AI is not active; " + Orders.Wire(uo.Type) + " failed.", true);
                return;
            }
            if (owner != null && ReferenceEquals(owner, r.Hold))
            {
                r.Hold = null;
                Emit(C2EventKind.Info, "orders", r.S.Side, 0, r.Id, Reasons.AiInactive, r.Name + ": its BDA AI is not active; hold released.");
                return;
            }
            if (ReferenceEquals(owner, _advanceOwner))
            {
                r.Advance = 0;
                r.NextAdvancePick = _now + 30;
            }
        }

        private static bool Same(GeoPoint a, GeoPoint b)
        {
            return a.Lat == b.Lat && a.Lon == b.Lon && a.Alt == b.Alt;
        }

        private static bool SameList(List<uint> a, List<uint> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }
}
