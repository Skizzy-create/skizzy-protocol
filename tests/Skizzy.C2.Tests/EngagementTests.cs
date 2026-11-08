using System.Linq;
using Xunit;

namespace Skizzy.C2.Tests
{
    /// <summary>Rows 4, 5, 6 and 23: WTA spread and retarget, stay on task, engage on contact, self-defence first.</summary>
    public class EngagementTests : TestKit
    {
        [Fact]
        public void Row4_attack_spreads_across_the_group_and_retargets_after_a_kill()
        {
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            var f2 = Add("Cobra 2", Blue, UnitClass.Fighter, 0, 2);
            var b1 = Add("Bomber 1", Red, UnitClass.Bomber, 35, 0);
            var b2 = Add("Bomber 2", Red, UnitClass.Bomber, 35, 3);
            b1.S.Group = b2.S.Group = "Raid";
            b1.RadarRangeM = b2.RadarRangeM = 0;
            Settle(0.2);
            var res = Ok(OrderRequest.Engage(Blue, b1.Id, f1.Id, f2.Id));
            Settle(0.4);
            Assert.Equal(2, UnitOrderOf(res, f1).TargetSet.Count);
            Assert.True(f1.Sticky && f2.Sticky); // row 5: sticky F1
            Assert.NotEqual(f1.Override, f2.Override);
            Assert.Contains("rest of its group", res.Summary);

            uint killed = f2.Override;
            W.Kill(killed);
            Settle(0.4);
            Assert.Equal(f1.Override, f2.Override); // both on the survivor
            Assert.True(H.Logged(C2EventKind.OrderProgress, "re-assigned to"));
            Assert.Equal(OrderStatus.Executing, res.Order.Status);

            W.Kill(f1.Override);
            Settle(0.4);
            Assert.Equal(OrderStatus.Achieved, res.Order.Status);
            Assert.Equal(Reasons.TargetsDestroyed, res.Order.Units[0].Reason);
            Assert.Equal(0u, f1.Override);
        }

        [Fact]
        public void Engage_fails_when_the_targets_leave_the_picture()
        {
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            f1.S.MissilesReady = 0; // keep the target alive; guns only
            var b1 = Add("Bomber 1", Red, UnitClass.Bomber, 40, 0);
            b1.RadarRangeM = 0;
            Settle(0.2);
            var res = Ok(OrderRequest.Engage(Blue, b1.Id, f1.Id));
            Settle(1);
            b1.Rcs = 0; // goes dark
            Assert.True(H.RunUntil(() => res.Order.IsTerminal, 40), H.Log());
            Assert.Equal(OrderStatus.Failed, res.Order.Status);
            Assert.Equal(Reasons.TargetLost, res.Order.Units[0].Reason);
        }

        [Fact]
        public void Row5_stay_on_task_ignores_crossing_hostiles()
        {
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            var crosser = Add("Crossing bomber", Red, UnitClass.Bomber, 30, 8);
            crosser.RadarRangeM = 0;
            Settle(0.2);
            Ok(OrderRequest.MoveTo(Blue, W.At(80, 0), f1.Id));
            H.Run(30);
            Assert.Equal(FireAuthorityMode.Tight, f1.Fire);
            Assert.NotNull(f1.Filter);
            Assert.False(f1.Filter.Allows(crosser.Id));
            Assert.Equal(0u, f1.CurrentTarget);
            Assert.False(f1.PatrolEngaged);
            Assert.DoesNotContain(W.Shots, s => s.Shooter == f1.Id);
            Assert.Equal(OnContact.Task, V(f1).OnContact);
        }

        [Fact]
        public void Row5_engage_stays_on_its_targets()
        {
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            var target = Add("Bomber 1", Red, UnitClass.Bomber, 60, 0);
            var crosser = Add("Bomber 2", Red, UnitClass.Bomber, 25, 0);
            target.RadarRangeM = crosser.RadarRangeM = 0;
            target.Rcs = 2; // in view at 60 km
            var res = new IssueResult();
            Settle(0.2);
            var req = OrderRequest.Engage(Blue, target.Id, f1.Id);
            req.Targets = new System.Collections.Generic.List<uint> { target.Id };
            Ok(req);
            H.Run(20);
            Assert.Equal(target.Id, f1.Override);
            Assert.True(f1.Filter.Allows(target.Id));
            Assert.False(f1.Filter.Allows(crosser.Id));
            Assert.DoesNotContain(W.Shots, s => s.Target == crosser.Id);
        }

        [Fact]
        public void Row6_engage_on_contact_diverts_and_restores_the_move()
        {
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            var contact = Add("Bandit", Red, UnitClass.Bomber, 70, 0);
            contact.RadarRangeM = 0;
            SetUnitDoctrine(f1, "onContact", "engage");
            Settle(0.2);
            var move = Ok(OrderRequest.MoveTo(Blue, W.At(150, 0), f1.Id));
            Settle(0.4);
            Assert.Equal(FireAuthorityMode.Free, f1.Fire);
            Assert.Null(f1.Filter);

            Assert.True(H.RunUntil(() => V(f1).Divert == contact.Id, 200), H.Log());
            Settle(0.4);
            Assert.Equal(contact.Id, f1.Override);
            Assert.True(f1.Sticky);
            Assert.True(H.Logged(C2EventKind.Diversion, "own radar contact Bandit"));
            Assert.True(f1.OnRoute); // the move stays loaded; F6b yields to the fight
            Assert.Equal(OrderStatus.Executing, move.Order.Status);

            W.Kill(contact.Id);
            Settle(0.4);
            Assert.Equal(0u, V(f1).Divert);
            Assert.Equal(0u, f1.Override);
            Assert.True(H.Logged(C2EventKind.BackOnTask, "back on move_to"));
            Assert.Equal(OrderStatus.Executing, move.Order.Status);
        }

        [Fact]
        public void Row6_divert_during_engage_repoints_then_restores_the_task_target()
        {
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            var task = Add("Carrier target", Red, UnitClass.Ship, 120, 0);
            task.RadarRangeM = 0;
            task.Rcs = 3; // in view at 120 km
            var pop = Add("Pop-up", Red, UnitClass.Drone, 20, 5);
            pop.RadarRangeM = 0;
            SetUnitDoctrine(f1, "onContact", "engage");
            Settle(0.2);
            var req = OrderRequest.Engage(Blue, task.Id, f1.Id);
            req.Targets = new System.Collections.Generic.List<uint> { task.Id };
            Ok(req);
            Assert.True(H.RunUntil(() => f1.Override == pop.Id, 10), H.Log());
            W.Kill(pop.Id);
            Settle(0.6);
            Assert.Equal(task.Id, f1.Override);
        }

        [Fact]
        public void Row23_self_defence_interrupts_the_order_and_restores_it()
        {
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            f1.S.MissilesReady = 0; // no kills by itself; the test kills
            var threat = Add("Flanker", Red, UnitClass.Fighter, 60, 0);
            threat.Scripted = true;
            threat.S.HeadingDeg = 270;
            threat.S.SpeedMps = 250;
            Settle(0.2);
            var move = Ok(OrderRequest.MoveTo(Blue, W.At(200, 0), f1.Id));
            Assert.True(H.RunUntil(() => V(f1).SelfDefence == threat.Id, 200), H.Log());
            Settle(0.2);
            Assert.True(W.DistKm(f1, threat) <= 25.5);
            Assert.Equal(threat.Id, f1.Override);
            Assert.True(f1.Sticky); // selfDefenceSticky (default): held for its duration even outside the envelope
            Assert.True(f1.OverrideUntil > W.Now && f1.OverrideUntil <= W.Now + 15.01);
            Assert.True(f1.Filter.Allows(threat.Id)); // Tight admits the threat
            Assert.True(H.Logged(C2EventKind.Interruption, "self-defence against Flanker"));

            W.Kill(threat.Id);
            Settle(0.4);
            Assert.Equal(0u, V(f1).SelfDefence);
            Assert.True(H.Logged(C2EventKind.BackOnTask, "back on move_to"));
            Assert.Equal(OrderStatus.Executing, move.Order.Status);
            Assert.Equal(0u, f1.Override);
        }

        [Fact]
        public void Row23_self_defence_ends_when_the_threat_opens()
        {
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            f1.S.MissilesReady = 0;
            f1.CruiseMps = 0; // stays put
            var threat = Add("Flanker", Red, UnitClass.Fighter, 20, 0);
            threat.Scripted = true;
            threat.S.HeadingDeg = 90; // running away
            threat.S.SpeedMps = 300;
            E.SetSideManaged(Blue, true);
            Settle(1.2); // acquisition scans run at 1 Hz per unit
            Assert.Equal(threat.Id, V(f1).SelfDefence);
            Assert.True(H.RunUntil(() => V(f1).SelfDefence == 0, 60), H.Log());
            Assert.True(H.Logged(C2EventKind.BackOnTask, "opening"));
        }

        [Fact]
        public void Row23_no_self_defence_on_weapons_hold()
        {
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            var threat = Add("Flanker", Red, UnitClass.Fighter, 15, 0);
            SetUnitDoctrine(f1, "weaponsControl", "hold");
            Settle(0.2);
            W.Shots.Clear(); // the fake fired once before the engine's first tick applied Hold
            Ok(OrderRequest.MoveTo(Blue, W.At(100, 0), f1.Id));
            Settle(2);
            Assert.Equal(0u, V(f1).SelfDefence);
            Assert.Equal(FireAuthorityMode.Hold, f1.Fire);
            Assert.DoesNotContain(W.Shots, s => s.Shooter == f1.Id);
            Assert.False(f1.Yield);
        }

        [Fact]
        public void Replaced_assignment_backs_off_then_reasserts()
        {
            var f1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            var b1 = Add("Bomber 1", Red, UnitClass.Bomber, 30, 0);
            b1.RadarRangeM = 0;
            Settle(0.2);
            var req = OrderRequest.Engage(Blue, b1.Id, f1.Id);
            req.Targets = new System.Collections.Generic.List<uint> { b1.Id };
            Ok(req);
            Settle(0.4);
            Assert.Equal(b1.Id, f1.Override);
            // a human (stock SetOverrideTarget) replaces it in game
            f1.Override = 0;
            E.OnOverrideLost(f1.Id, b1.Id, OverrideLostReason.Replaced);
            Settle(1);
            Assert.True(H.Logged(C2EventKind.Info, "replaced in game"));
            Assert.Equal(0u, f1.Override);
            Settle(10);
            Assert.Equal(b1.Id, f1.Override);
        }
    }
}
