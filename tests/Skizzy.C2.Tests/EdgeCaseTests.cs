using System.Diagnostics;
using System.Linq;
using Xunit;

namespace Skizzy.C2.Tests
{
    /// <summary>Edge cases across rows: precedence, mid-order doctrine changes, claim limits, lost contacts, stale events, scale.</summary>
    public class EdgeCaseTests : TestKit
    {
        [Fact]
        public void Rtb_supersedes_an_engagement_and_the_suspended_movement_order()
        {
            var cvn = Add("Kestrel", Blue, UnitClass.Carrier, -20, 0);
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            var b1 = Add("Bomber", Red, UnitClass.Bomber, 30, 0);
            b1.RadarRangeM = 0;
            Settle(0.2);
            var move = Ok(OrderRequest.MoveTo(Blue, W.At(60, 0), f1.Id));
            var attack = Ok(OrderRequest.Engage(Blue, b1.Id, f1.Id));
            var rtb = Ok(OrderRequest.Rtb(Blue, f1.Id));
            Settle(0.4);
            Assert.Equal(OrderStatus.Superseded, attack.Order.Status);
            Assert.Equal(OrderStatus.Superseded, move.Order.Status);
            Assert.Equal("rtb", V(f1).Task);
            Assert.Equal(0u, f1.Override);
        }

        [Fact]
        public void Cancelling_an_engagement_resumes_the_movement_order()
        {
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            var b1 = Add("Bomber", Red, UnitClass.Bomber, 30, 0);
            b1.RadarRangeM = 0;
            Settle(0.2);
            var move = Ok(OrderRequest.MoveTo(Blue, W.At(0, 80), f1.Id));
            var attack = Ok(OrderRequest.Engage(Blue, b1.Id, f1.Id));
            Settle(0.4);
            Assert.False(f1.OnRoute);
            E.Cancel(attack.Order.Id, "seat");
            Settle(0.4);
            Assert.Equal(OrderStatus.Cancelled, attack.Order.Status);
            Assert.Equal(OrderStatus.Executing, move.Order.Units[0].Status);
            Assert.True(f1.OnRoute);
            Assert.Equal(0u, f1.Override);
        }

        [Fact]
        public void Doctrine_change_mid_order_takes_effect_at_the_next_tick()
        {
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            Settle(0.2);
            Ok(OrderRequest.HoldPattern(Blue, W.At(0, 0), W.At(20, 0), f1.Id));
            Settle(0.4);
            Assert.True(f1.Yield);
            Assert.Equal(FireAuthorityMode.Free, f1.Fire);
            SetUnitDoctrine(f1, "weaponsControl", "hold");
            Settle(0.2);
            Assert.False(f1.Yield);
            Assert.Equal(FireAuthorityMode.Hold, f1.Fire);
            SetUnitDoctrine(f1, "weaponsControl", "tight");
            Settle(0.2);
            Assert.True(f1.Yield);
            Assert.Equal(FireAuthorityMode.Tight, f1.Fire);
            Assert.NotNull(f1.Filter);
        }

        [Fact]
        public void At_most_two_hunters_per_aircraft_the_third_waits()
        {
            var aew = Add("Eye", Blue, UnitClass.Aew, -20, 0);
            aew.CruiseMps = 0;
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            var f2 = Add("Cobra 2", Blue, UnitClass.Fighter, 0, 2);
            var f3 = Add("Cobra 3", Blue, UnitClass.Fighter, 0, 4);
            var b1 = Add("Bandit", Red, UnitClass.Fighter, 150, 0);
            b1.Scripted = true;
            b1.RadarRangeM = 0;
            Settle(0.2);
            Ok(OrderRequest.AttackNearest(Blue, f1.Id, f2.Id, f3.Id));
            Settle(1);
            Assert.Equal(2, new[] { f1, f2, f3 }.Count(f => f.Override == b1.Id));
            Assert.Equal(2, E.Picture(Blue).ClaimCount(b1.Id));
        }

        [Fact]
        public void Divert_ends_when_own_radar_loses_the_contact()
        {
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            f1.S.MissilesReady = 0; f1.S.HasGuns = true;
            var contact = Add("Bandit", Red, UnitClass.Bomber, 30, 0);
            contact.RadarRangeM = 0;
            SetUnitDoctrine(f1, "onContact", "engage");
            Settle(0.2);
            Ok(OrderRequest.MoveTo(Blue, W.At(-100, 0), f1.Id));
            Assert.True(H.RunUntil(() => V(f1).Divert == contact.Id, 5));
            contact.Rcs = 0; // gone from own radar (and the picture)
            Assert.True(H.RunUntil(() => V(f1).Divert == 0, 10), H.Log());
            Assert.True(H.Logged(C2EventKind.BackOnTask, "lost contact with Bandit"));
        }

        [Fact]
        public void Own_repointing_does_not_look_like_a_replacement_in_game()
        {
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            var b1 = Add("Bomber 1", Red, UnitClass.Bomber, 30, 0);
            var b2 = Add("Bomber 2", Red, UnitClass.Bomber, 30, 3);
            b1.S.Group = b2.S.Group = "Raid";
            b1.RadarRangeM = b2.RadarRangeM = 0;
            Settle(0.2);
            Ok(OrderRequest.Engage(Blue, b1.Id, f1.Id));
            Settle(0.4);
            uint first = f1.Override;
            W.Kill(first);
            Settle(0.6);
            Assert.NotEqual(first, f1.Override);
            Assert.NotEqual(0u, f1.Override);
            Assert.False(H.Logged(C2EventKind.Info, "replaced in game"));
        }

        [Fact]
        public void Not_engageable_assignment_backs_off_instead_of_looping()
        {
            var ddg = Add("Halberd", Blue, UnitClass.Ship, 0, 0);
            ddg.CruiseMps = 0;
            var b1 = Add("Bomber", Red, UnitClass.Bomber, 100, 0); // beyond 60 km SAM range
            b1.RadarRangeM = 0;
            SetUnitDoctrine(ddg, "huntSticky", false);
            Settle(0.2);
            var req = OrderRequest.Engage(Blue, b1.Id, ddg.Id);
            req.Targets = new System.Collections.Generic.List<uint> { b1.Id };
            Ok(req);
            Settle(10);
            // engage is sticky, so it holds even out of range: one F1 call
            Assert.Equal(1, W.CallCount("F1 set " + ddg.Id + " "));
            Assert.Equal(b1.Id, ddg.Override);
        }

        [Fact]
        public void Unmanaged_units_without_orders_are_left_to_bda()
        {
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            var b1 = Add("Bandit", Red, UnitClass.Fighter, 20, 0);
            Settle(5);
            Assert.Empty(W.Calls);
            Assert.Empty(E.Picture(Blue).Hostiles); // no picture work for a side Skizzy does not drive
        }

        [Fact]
        public void Unit_destroyed_mid_order_releases_its_claim()
        {
            var aew = Add("Eye", Blue, UnitClass.Aew, -20, 0);
            aew.CruiseMps = 0;
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            var b1 = Add("Bandit", Red, UnitClass.Fighter, 150, 0);
            b1.Scripted = true;
            b1.RadarRangeM = 0;
            Settle(0.2);
            var res = Ok(OrderRequest.AttackNearest(Blue, f1.Id));
            Settle(0.4);
            Assert.Equal(1, E.Picture(Blue).ClaimCount(b1.Id));
            W.Kill(f1.Id);
            Settle(0.4);
            Assert.Equal(0, E.Picture(Blue).ClaimCount(b1.Id));
            Assert.Equal(OrderStatus.Failed, res.Order.Status);
            Assert.Equal(Reasons.UnitDestroyed, res.Order.Units[0].Reason);
        }

        [Fact]
        public void Filter_contents_are_stable_in_steady_state()
        {
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            var b1 = Add("Bomber 1", Red, UnitClass.Bomber, 120, 0);
            b1.RadarRangeM = 0;
            b1.Rcs = 3;
            Settle(0.2);
            var req = OrderRequest.Engage(Blue, b1.Id, f1.Id);
            req.Targets = new System.Collections.Generic.List<uint> { b1.Id }; // override is also a task target
            Ok(req);
            Settle(1);
            int v = f1.Filter.Version;
            Settle(5);
            Assert.Equal(v, f1.Filter.Version);
            Assert.Equal(1, f1.Filter.Count);
        }

        [Fact]
        public void Large_battle_ticks_within_budget()
        {
            for (int i = 0; i < 60; i++) Add("Blue " + i, Blue, i % 3 == 0 ? UnitClass.Drone : UnitClass.Fighter, 0, i * 1.5);
            for (int i = 0; i < 8; i++) Add("DDG " + i, Blue, UnitClass.Ship, -40, i * 8);
            for (int i = 0; i < 80; i++)
            {
                var r = Add("Red " + i, Red, UnitClass.Fighter, 60, i * 1.2);
                r.Scripted = true;
                r.S.HeadingDeg = 270;
                r.S.SpeedMps = 250;
            }
            W.KillOnShot = false; // keep the battle populated for the whole measurement
            E.SetSideManaged(Blue, true);
            E.SetSideManaged(Red, true);
            Settle(0.2);
            Ok(OrderRequest.AllAttack(Blue));
            E.Profile = true;
            var sw = new Stopwatch();
            double worst = 0;
            for (int i = 0; i < 300; i++)
            {
                W.Step(0.2);
                long t0 = Stopwatch.GetTimestamp();
                sw.Start();
                E.Tick();
                sw.Stop();
                double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                if (i > 10 && ms > worst) worst = ms;
            }
            E.DrainEvents(H.Events);
            double mean = sw.Elapsed.TotalMilliseconds / 300;
            // engine time only (the fake world is excluded); generous bounds for CI machines
            string phases = string.Join(" / ", E.PhaseMs.Select(x => (x / 300).ToString("0.000")));
            // regression guard (Debug build, any CI machine); Release measures ~0.8 ms mean at this size
            Assert.True(mean < 8, "mean tick " + mean.ToString("0.00") + " ms, worst " + worst.ToString("0.00") + " ms; refresh/picture/step/apply/total " + phases);
            Assert.Contains(W.Shots, s => W.Get(s.Shooter).S.Side == Blue);
        }
    }
}
