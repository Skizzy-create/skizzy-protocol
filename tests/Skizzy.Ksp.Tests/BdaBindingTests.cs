using System.Collections.Generic;
using System.Linq;
using Skizzy.Bda;
using Skizzy.C2;
using Xunit;

namespace Skizzy.Ksp.Tests
{
    /// <summary>The binding logic over a fake game: unit mapping, weapon-manager/AI changes, restore, events, rails.</summary>
    public class BdaBindingTests
    {
        private const string Blue = "Blue";
        private const string Red = "Red";

        private readonly FakeGame G = new FakeGame();
        private readonly EventLog Ev = new EventLog();
        private BdaBinding B;
        private readonly List<UnitState> _units = new List<UnitState>();

        private BdaBinding Make()
        {
            B = new BdaBinding(G);
            G.Binding = B;
            B.Connect(Ev);
            return B;
        }

        private List<UnitState> Units()
        {
            _units.Clear();
            B.GetUnits(_units);
            return _units;
        }

        [Fact]
        public void Units_are_loaded_vessels_with_their_class_side_and_callsign_group_and_missiles_in_flight()
        {
            var f = G.Add("Cobra 1", Blue, UnitClass.Fighter);
            var s = G.Add("[CV] Kestrel", Blue, UnitClass.Carrier);
            var r = G.Add("Bandit 2", Red, UnitClass.Fighter);
            var m = G.AddMissile(Red, f.Id);
            var far = G.Add("Far 1", Red, UnitClass.Ship);
            far.Loaded = false;
            Make();
            var u = Units();
            Assert.Equal(4, u.Count);
            Assert.Equal("Cobra", u.First(x => x.Id == f.Id).Group);
            Assert.True(u.First(x => x.Id == s.Id).IsBase);
            Assert.Equal(Red, u.First(x => x.Id == r.Id).Side);
            var mu = u.First(x => x.Id == m.Id);
            Assert.Equal(UnitClass.Missile, mu.Class);
            Assert.Equal(f.Id, mu.MissileTarget);
            Assert.DoesNotContain(u, x => x.Id == far.Id);
            Assert.Same(f.Wm, B.WmOf(f.Id));
            Assert.Same(f.Ai, B.AiOf(f.Id));
        }

        [Fact]
        public void A_new_primary_weapon_manager_gets_the_engines_settings_and_the_old_one_is_restored()
        {
            var f = G.Add("Cobra 1", Blue, UnitClass.Fighter);
            var b = G.Add("Bandit 1", Red, UnitClass.Fighter);
            Make();
            Units();
            f.Wm.Fire = FireAuthorityMode.Free;
            var filter = new TargetFilter(f.Id);
            B.SetOverrideTarget(f.Id, b.Id, 0, true);
            B.SetSecondaryTargets(f.Id, new List<uint> { b.Id }, true);
            B.SetFireAuthority(f.Id, FireAuthorityMode.Tight);
            B.SetTargetFilter(f.Id, filter);
            B.SetGuardMode(f.Id, true);
            B.SetEngageMissiles(f.Id, false);
            B.SetMissileRangeMax(f.Id, true);
            var old = f.Wm;
            Assert.Equal(b.Id, old.Override);
            Assert.False(old.WeaponEngageMissile[0]);
            Assert.Equal(20000f, old.MissileRange[0]);

            // the part holding the primary weapon manager is destroyed: another one takes over
            var next = new FakeWm();
            f.Wm = next;
            Units();
            Assert.Same(next, B.WmOf(f.Id));
            Assert.Equal(b.Id, next.Override);
            Assert.True(next.Sticky);
            Assert.Equal(new List<uint> { b.Id }, next.Secondary);
            Assert.True(next.SecondaryOnly);
            Assert.Equal(FireAuthorityMode.Tight, next.Fire);
            Assert.Same(filter, next.Filter);
            Assert.True(next.Guard);
            Assert.False(next.EngageMissiles);
            Assert.Equal(20000f, next.MissileRange[0]);

            // the old one (still alive, e.g. decoupled) is back to stock
            Assert.Equal(0u, old.Override);
            Assert.Null(old.Secondary);
            Assert.Equal(FireAuthorityMode.Free, old.Fire);
            Assert.Null(old.Filter);
            Assert.True(old.EngageMissiles);
            Assert.Equal(new[] { true, false }, old.WeaponEngageMissile);
            Assert.Equal(5000f, old.MissileRange[0]);
            // clearing the old assignment is the binding's own doing: not reported as a loss
            Assert.Empty(Ev.Items);

            // events from the old weapon manager are dropped
            B.GameOverrideLost(old, b.Id, (int)OverrideLostReason.TargetDestroyed);
            B.GameMissileFired(old, 999, b.Id);
            Assert.Empty(Ev.Items);
            B.GameMissileFired(next, 999, b.Id);
            Assert.Equal("fired " + f.Id + " 999 " + b.Id, Ev.Items.Single());
        }

        [Fact]
        public void A_dead_old_weapon_manager_is_not_touched()
        {
            var f = G.Add("Cobra 1", Blue, UnitClass.Fighter);
            Make();
            Units();
            B.SetFireAuthority(f.Id, FireAuthorityMode.Hold);
            f.Wm.Live = false;
            int calls = G.Calls.Count;
            f.Wm = null;
            Units();
            Assert.Null(B.WmOf(f.Id));
            Assert.Equal(calls, G.Calls.Count);
            f.Wm = new FakeWm();
            Units();
            Assert.Equal(FireAuthorityMode.Hold, f.Wm.Fire);
            Assert.Equal(0, B.FaultCount);
        }

        [Fact]
        public void A_new_ai_continues_the_route_from_the_next_gate()
        {
            var f = G.Add("Cobra 1", Blue, UnitClass.Fighter);
            Make();
            Units();
            var pts = new List<GeoPoint> { new GeoPoint(0, 1), new GeoPoint(0, 2), new GeoPoint(0, 3) };
            Assert.True(B.SetWaypoints(f.Id, pts, 500, 1, 0));
            B.SetPatrolEngage(f.Id, true, 20000, default(GeoPoint), false);
            B.GameWaypointReached(f.Ai, 0, 1);
            Assert.Equal("wp " + f.Id + " 0 1", Ev.Items.Last());

            var oldAi = f.Ai;
            f.Ai = new FakeAi();
            Units();
            Assert.Null(oldAi.Route); // old AI's custom route cleared
            Assert.False(oldAi.Patrol.Yield);
            Assert.Equal(2, f.Ai.Route.Count);
            Assert.Equal(2, f.Ai.Route[0].Lon);
            Assert.True(f.Ai.Patrol.Yield);
            Assert.Equal(20000f, f.Ai.Patrol.LeashM);

            B.GameWaypointReached(f.Ai, 0, 1); // the new AI's gate 0 is the route's gate 1
            Assert.Equal("wp " + f.Id + " 1 1", Ev.Items.Last());
            B.GameWaypointReached(oldAi, 1, 1); // stale AI: dropped
            Assert.Equal("wp " + f.Id + " 1 1", Ev.Items.Last());
            B.GameRouteCompleted(f.Ai);
            Assert.Equal("done " + f.Id, Ev.Items.Last());
        }

        [Fact]
        public void A_route_for_an_inactive_ai_is_refused_and_a_pending_reapply_waits_for_the_ai()
        {
            var f = G.Add("Cobra 1", Blue, UnitClass.Fighter);
            Make();
            Units();
            f.Ai.Active = false;
            Assert.False(B.SetWaypoints(f.Id, new List<GeoPoint> { new GeoPoint(0, 1) }, 500, 1, 0));
            Assert.False(B.CommandFlyTo(f.Id, new GeoPoint(0, 1)));
            f.Ai.Active = true;
            Assert.True(B.SetWaypoints(f.Id, new List<GeoPoint> { new GeoPoint(0, 1), new GeoPoint(0, 2) }, 500, 1, 0));
            var inactive = new FakeAi { Active = false };
            f.Ai = inactive;
            Units();
            Assert.Null(inactive.Route);
            inactive.Active = true;
            Units();
            Assert.Equal(2, inactive.Route.Count);
        }

        [Fact]
        public void An_unresolvable_f1_target_is_reported_NotEngageable_and_a_friendly_one_NotHostile()
        {
            var f = G.Add("Cobra 1", Blue, UnitClass.Fighter);
            var b = G.Add("Bandit 1", Red, UnitClass.Fighter);
            var friend = G.Add("Cobra 2", Blue, UnitClass.Fighter);
            Make();
            Units();
            B.SetOverrideTarget(f.Id, b.Id, 30, true);
            Assert.Equal(b.Id, f.Wm.Override);

            B.SetOverrideTarget(f.Id, 4242, 30, true);
            Assert.Equal("lost " + f.Id + " 4242 NotEngageable", Ev.Items.Single());
            Assert.Equal(0u, f.Wm.Override); // the old assignment does not outlive the failed replacement

            B.SetOverrideTarget(f.Id, friend.Id, 30, true);
            Assert.Equal("lost " + f.Id + " " + friend.Id + " NotHostile", Ev.Items.Last());

            b.Loaded = false; // out of physics range: no target record
            B.SetOverrideTarget(f.Id, b.Id, 30, true);
            Assert.Equal("lost " + f.Id + " " + b.Id + " NotEngageable", Ev.Items.Last());
            Assert.Equal(3, Ev.Items.Count);
        }

        [Fact]
        public void Self_caused_losses_are_suppressed_and_bda_losses_forwarded_with_unknown_reasons_mapped()
        {
            var f = G.Add("Cobra 1", Blue, UnitClass.Fighter);
            var b1 = G.Add("Bandit 1", Red, UnitClass.Fighter);
            var b2 = G.Add("Bandit 2", Red, UnitClass.Fighter);
            Make();
            Units();
            B.SetOverrideTarget(f.Id, b1.Id, 0, true);
            B.SetOverrideTarget(f.Id, b2.Id, 0, true); // BDA raises Replaced(b1)
            B.ClearOverrideTarget(f.Id); // BDA raises Cleared(b2)
            Assert.Empty(Ev.Items);
            Assert.Contains("F1 clear " + f.Id, G.Calls);

            B.SetOverrideTarget(f.Id, b1.Id, 0, true);
            G.BdaDropsOverride(f, OverrideLostReason.Cleared); // leash broke: BDA cleared it
            Assert.Equal("lost " + f.Id + " " + b1.Id + " Cleared", Ev.Items.Last());
            B.SetOverrideTarget(f.Id, b1.Id, 0, true);
            G.BdaDropsOverride(f, OverrideLostReason.NotHostile);
            Assert.Equal("lost " + f.Id + " " + b1.Id + " NotHostile", Ev.Items.Last());
            B.SetOverrideTarget(f.Id, b1.Id, 0, true);
            G.BdaDropsOverride(f, (OverrideLostReason)57); // a newer BDArmory
            Assert.Equal("lost " + f.Id + " " + b1.Id + " NotEngageable", Ev.Items.Last());

            // a cleared mirror is not re-applied to a new weapon manager
            f.Wm = new FakeWm();
            Units();
            Assert.Equal(0u, f.Wm.Override);
        }

        [Fact]
        public void An_expired_assignment_is_reported_instead_of_being_reapplied()
        {
            var f = G.Add("Cobra 1", Blue, UnitClass.Fighter);
            var b = G.Add("Bandit 1", Red, UnitClass.Fighter);
            Make();
            Units();
            B.SetOverrideTarget(f.Id, b.Id, 10, false);
            G.Time += 4;
            f.Wm = new FakeWm();
            Units();
            Assert.Equal(b.Id, f.Wm.Override);
            Assert.InRange(f.Wm.Duration, 5.9f, 6.1f); // the remaining time
            G.Time += 7;
            f.Wm = new FakeWm();
            Units();
            Assert.Equal(0u, f.Wm.Override);
            Assert.Equal("lost " + f.Id + " " + b.Id + " Expired", Ev.Items.Last());
        }

        [Fact]
        public void Persistent_settings_are_restored_on_rails_and_reapplied_off_rails_and_after_a_reload()
        {
            var f = G.Add("Cobra 1", Blue, UnitClass.Fighter);
            Make();
            Units();
            B.SetEngageMissiles(f.Id, false);
            B.SetMissileRangeMax(f.Id, true);
            B.SetFireAuthority(f.Id, FireAuthorityMode.Hold);
            Assert.False(f.Wm.WeaponEngageMissile[0]);

            // goes on rails: KSP may save the vessel now, so the persistent fields go back
            f.Packed = true;
            B.GamePacked(f.Id);
            Assert.True(f.Wm.WeaponEngageMissile[0]);
            Assert.Equal(5000f, f.Wm.MissileRange[0]);
            Assert.Equal(FireAuthorityMode.Hold, f.Wm.Fire); // not persistent: stays
            Units();
            B.SetEngageMissiles(f.Id, false); // while packed: remembered, not applied
            Assert.True(f.Wm.WeaponEngageMissile[0]);

            f.Packed = false;
            Units();
            Assert.False(f.Wm.WeaponEngageMissile[0]);
            Assert.Equal(20000f, f.Wm.MissileRange[0]);

            // unloaded: modules destroyed; reloaded: fresh modules get the mirror
            f.Packed = true;
            B.GamePacked(f.Id);
            f.Loaded = false;
            f.Wm.Live = false;
            B.GameUnloaded(f.Id);
            Assert.Empty(Units());
            f.Wm = new FakeWm();
            f.Loaded = true;
            f.Packed = false;
            Units();
            Assert.False(f.Wm.WeaponEngageMissile[1]);
            Assert.Equal(FireAuthorityMode.Hold, f.Wm.Fire);
            Assert.Equal(40000f, f.Wm.MissileRange[1]);
            Assert.Equal(0, B.FaultCount);
        }

        [Fact]
        public void Destruction_is_reported_once_by_event_by_scoring_name_or_by_disappearing()
        {
            var a = G.Add("Cobra 1", Blue, UnitClass.Fighter);
            var b = G.Add("Cobra 2", Blue, UnitClass.Fighter);
            var c = G.Add("Cobra 3", Blue, UnitClass.Fighter);
            Make();
            Units();
            B.GameDeath("Cobra 1");
            a.Live = false;
            B.GameVesselDestroyed(a.Id);
            B.GameVesselDestroyed(b.Id);
            b.Live = false;
            c.Live = false; // gone without an event
            Units();
            Assert.Equal(new[] { "destroyed " + a.Id, "destroyed " + b.Id, "destroyed " + c.Id }, Ev.Items);
            Assert.Equal(0, B.UnitCount);
            B.GameDeath("nobody");
            Assert.Equal(3, Ev.Items.Count);
        }

        [Fact]
        public void ReleaseAll_restores_everything_the_binding_changed()
        {
            var f = G.Add("Cobra 1", Blue, UnitClass.Fighter);
            var b = G.Add("Bandit 1", Red, UnitClass.Fighter);
            var dd = G.Add("Halberd", Blue, UnitClass.Ship);
            f.Wm.Fire = FireAuthorityMode.Tight; // set by someone before the engine
            f.Ai.Patrol = new PatrolSettings { Yield = false, LeashM = 777 };
            Make();
            Units();
            B.SetOverrideTarget(f.Id, b.Id, 0, true);
            B.SetSecondaryTargets(f.Id, new List<uint> { b.Id }, false);
            B.SetFireAuthority(f.Id, FireAuthorityMode.Hold);
            B.SetTargetFilter(f.Id, new TargetFilter(f.Id));
            B.SetWaypoints(f.Id, new List<GeoPoint> { new GeoPoint(0, 1) }, 500, 1, 200);
            B.SetPatrolEngage(f.Id, true, 30000, new GeoPoint(1, 2, 3), true);
            B.SetEngageMissiles(f.Id, false);
            B.SetMissileRangeMax(f.Id, true);
            B.CommandFlyTo(dd.Id, new GeoPoint(0, 5));

            B.ReleaseAll();
            Assert.Equal(0u, f.Wm.Override);
            Assert.Null(f.Wm.Secondary);
            Assert.Equal(FireAuthorityMode.Tight, f.Wm.Fire);
            Assert.Null(f.Wm.Filter);
            Assert.Null(f.Ai.Route);
            Assert.False(f.Ai.Patrol.Yield);
            Assert.Equal(777f, f.Ai.Patrol.LeashM);
            Assert.True(f.Wm.EngageMissiles);
            Assert.Equal(new[] { true, false }, f.Wm.WeaponEngageMissile);
            Assert.Equal(5000f, f.Wm.MissileRange[0]);
            Assert.False(dd.Ai.Commanded);
            Assert.Empty(Ev.Items);
            Assert.Equal(0, B.UnitCount);
            B.ReleaseAll(); // idempotent
        }

        [Fact]
        public void Without_hooks_no_hook_is_ever_called_and_stock_commands_still_work()
        {
            G.HookSetValue = HookSet.None;
            var f = G.Add("Cobra 1", Blue, UnitClass.Fighter);
            var b = G.Add("Bandit 1", Red, UnitClass.Fighter);
            Make();
            Units();
            Assert.Equal(HookSet.None, B.Hooks);
            B.SetOverrideTarget(f.Id, b.Id, 0, true);
            B.ClearOverrideTarget(f.Id);
            B.SetSecondaryTargets(f.Id, new List<uint> { b.Id }, true);
            B.SetFireAuthority(f.Id, FireAuthorityMode.Hold);
            B.SetTargetFilter(f.Id, new TargetFilter(f.Id));
            Assert.False(B.SetWaypoints(f.Id, new List<GeoPoint> { new GeoPoint(0, 1) }, 500, 1, 0));
            B.ClearWaypoints(f.Id);
            B.SetPatrolEngage(f.Id, true, 1, default(GeoPoint), false);
            var contacts = new List<uint>();
            B.GetOwnContacts(f.Id, contacts);
            f.Wm = new FakeWm();
            Units();
            B.ReleaseAll();
            Assert.Equal(0, B.FaultCount); // FakeGame throws MissingMethodException on any hook call
            Assert.Empty(Ev.Items);

            Make();
            Units();
            Assert.True(B.CommandFlyTo(f.Id, new GeoPoint(0, 3)));
            B.SetGuardMode(f.Id, true);
            B.SetEngageMissiles(f.Id, false);
            Assert.True(f.Ai.Commanded);
            Assert.True(f.Wm.Guard);
            Assert.False(f.Wm.EngageMissiles);
            B.ReleaseAll();
            Assert.False(f.Ai.Commanded);
            Assert.True(f.Wm.EngageMissiles);
            Assert.Equal(0, B.FaultCount);
        }

        [Fact]
        public void Calls_for_unknown_units_are_ignored_and_game_errors_never_reach_the_engine()
        {
            var f = G.Add("Cobra 1", Blue, UnitClass.Fighter);
            Make();
            var log = new List<string>();
            B.Log = log.Add;
            B.SetOverrideTarget(77, 1, 0, true);
            Assert.False(B.SetWaypoints(77, new List<GeoPoint> { new GeoPoint(0, 1) }, 500, 1, 0));
            B.RestoreEngageMissiles(77);
            Units();
            G.HookSetValue = HookSet.None; // the game breaks under the binding (it still believes F3 exists)
            B.SetFireAuthority(f.Id, FireAuthorityMode.Hold);
            Assert.Equal(1, B.FaultCount);
            Assert.Contains("MissingMethodException", log.Single());
        }

        [Fact]
        public void Patrol_and_target_events_are_forwarded_and_self_caused_resumes_dropped()
        {
            var f = G.Add("Cobra 1", Blue, UnitClass.Fighter);
            Make();
            Units();
            B.GamePatrolEngage(f.Ai, 55);
            B.GamePatrolResume(f.Ai, true);
            B.GameTargetDetected(55, Red);
            B.GameTargetLost(55, Red);
            B.GameTargetDetected(0, Red);
            B.GamePatrolEngage(new FakeAi(), 1);
            Assert.Equal(new[] { "engage " + f.Id + " 55", "resume " + f.Id + " True", "detected 55 Red", "targetlost 55 Red" }, Ev.Items);
        }

        [Fact]
        public void A_decoupled_weapon_manager_moving_to_a_new_vessel_belongs_to_the_new_unit()
        {
            var f = G.Add("Cobra 1", Blue, UnitClass.Fighter);
            Make();
            Units();
            B.SetFireAuthority(f.Id, FireAuthorityMode.Hold);
            var wm = f.Wm;
            var debris = G.Add("Cobra 1 Debris", Blue, UnitClass.Fighter, ai: false);
            debris.Wm = wm;
            f.Wm = null;
            G.Vessels.Remove(debris);
            G.Vessels.Insert(0, debris); // the new vessel is scanned first
            Units();
            Assert.Same(wm, B.WmOf(debris.Id));
            Assert.Equal(FireAuthorityMode.Free, wm.Fire); // the old unit's settings were taken off it
            B.GameMissileFired(wm, 1, 2);
            Assert.Equal("fired " + debris.Id + " 1 2", Ev.Items.Single());
        }
    }
}
