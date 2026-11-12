using System;
using System.Collections.Generic;
using System.Linq;
using Skizzy.C2.Fake;
using Skizzy.C2.Tactical;
using Xunit;

namespace Skizzy.C2.Tests
{
    /// <summary>The track picture engine over FakeBda (08 §12 acceptance items that run off-game).</summary>
    public sealed class TpeTests
    {
        private const string Blue = "Blue", Red = "Red";
        private readonly FakeBda W = new FakeBda();
        private readonly TrackPictureEngine T;
        private readonly List<TacEvent> Ev = new List<TacEvent>();

        public TpeTests()
        {
            T = new TrackPictureEngine(W, new FakeSensors(W));
        }

        private void Run(double s)
        {
            int n = (int)Math.Round(s / 0.2);
            for (int i = 0; i < n; i++)
            {
                W.Step(0.2);
                T.Step();
                T.DrainEvents(Ev);
            }
        }

        private Track Tr(string team, FakeUnit u) { return T.Team(team)?.ByVesselId(u.Id); }

        [Fact]
        public void Hostile_ship_beyond_radar_is_absent_then_unknown_on_detection_then_hostile_on_launch()
        {
            var ddg = W.Add("DDG 1", Blue, UnitClass.Ship, 0, 0);
            ddg.CruiseMps = 0;
            var red = W.Add("Kirov", Red, UnitClass.Ship, 200, 0);
            red.Scripted = true;
            red.RadarRangeM = 0;
            Run(1);
            Assert.Null(Tr(Blue, red));                         // 200 km, radar 150 km
            red.S.HeadingDeg = 270;
            red.S.SpeedMps = 400;                                // closes 80 km in 200 s
            Assert.True(SpinUntil(() => Tr(Blue, red) != null, 200));
            var t = Tr(Blue, red);
            Assert.Equal(Iff.Unknown, t.Iff);                    // radar only, beyond 20 km surface id range
            Assert.Equal(TrackSources.OwnSensor, t.Sources);
            Assert.Equal(UnitClass.Ship, t.Class);               // held by an own sensor: classified
            Assert.Equal(IdState.Classified, t.Id);
            Assert.Contains(Ev, e => e.Kind == TacEventKind.TrackNew && e.Vessel == red.Id && e.Team == Blue);
            W.AddMissile(red, ddg, 140, 0);
            Run(0.4);
            Assert.Equal(Iff.Hostile, Tr(Blue, red).Iff);        // missiles: hostile behaviour declares the launcher's missile
            var m = T.Team(Blue).Tracks.First(x => x.IsMissile);
            Assert.Equal(Iff.Hostile, m.Iff);
            Assert.Equal(ddg.Id, m.MissileTarget);
            Assert.Contains(Ev, e => e.Kind == TacEventKind.MissileInbound && e.Other == ddg.Id);
            Assert.Equal(1, T.Team(Blue).InboundToOwn);
        }

        private bool SpinUntil(Func<bool> f, double maxS)
        {
            for (double s = 0; s < maxS; s += 0.2)
            {
                if (f()) return true;
                Run(0.2);
            }
            return f();
        }

        [Fact]
        public void Radar_loss_dead_reckons_then_drops_after_90_s_and_the_number_is_kept_on_reacquire()
        {
            var f = W.Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            f.CruiseMps = 0;
            var b = W.Add("Bandit", Red, UnitClass.Fighter, 30, 0);
            b.Scripted = true;
            b.RadarRangeM = 0;
            b.S.HeadingDeg = 90;
            b.S.SpeedMps = 200;
            Run(0.4);
            var t = Tr(Blue, b);
            Assert.NotNull(t);
            int number = t.Number;
            f.RadarRangeM = 0;                                    // radar off: nothing holds it
            W.Invalidate();
            Run(2);
            t = Tr(Blue, b);
            Assert.Equal(TrackState.Dr, t.State);
            Assert.True(t.Quality < 0.9 && t.Quality > 0);
            double drKm = W.Geo.Distance(f.S.Pos, t.Pos) / 1000;
            Assert.InRange(drKm, 30.0, 32.0);                     // dead-reckoned east at 200 m/s
            Run(40);
            Assert.Equal(0, Tr(Blue, b).Quality);                 // decays to 0 over 30 s
            Run(52);
            Assert.Null(Tr(Blue, b));                             // dropped after 90 s
            Assert.Contains(Ev, e => e.Kind == TacEventKind.TrackLost && e.Reason == "faded" && e.Vessel == b.Id);
            f.RadarRangeM = 100000000;
            W.Invalidate();
            Run(1.2);
            Assert.Equal(number, Tr(Blue, b).Number);              // stable per (team, vessel)
        }

        [Fact]
        public void Stealth_contact_held_only_by_a_drone_is_own_sensor_only_and_unknown_until_inside_id_range()
        {
            var drone = W.Add("Wasp 1", Blue, UnitClass.Drone, 0, 0);
            drone.CruiseMps = 0;
            W.Add("Halberd", Blue, UnitClass.Ship, -300, 0).CruiseMps = 0;
            var shade = W.Add("Shade", Red, UnitClass.Fighter, 11, 0);
            shade.Rcs = 0.5;                                      // drone radar 25 km -> 12.5 km
            shade.RadarRangeM = 0;
            shade.Scripted = true;
            T.IdRangeAirM = 5000;
            Run(1.2);
            var t = Tr(Blue, shade);
            Assert.NotNull(t);
            Assert.Equal(1, t.Holders);
            Assert.Equal(drone.Id, t.SoleHolder);
            Assert.False(t.SurveillanceHeld);
            Assert.Equal(Iff.Unknown, t.Iff);
            Assert.Null(t.Name);
            shade.S.Pos = W.At(4, 0, 4000);                         // inside the 5 km identification range
            W.Invalidate();
            Run(1.2);
            t = Tr(Blue, shade);
            Assert.Equal(Iff.Hostile, t.Iff);
            Assert.Equal(IdState.Identified, t.Id);
            Assert.Equal(drone.Id, t.IdentifiedBy);
            Assert.Null(t.Name);                                   // revealNames off under fog
            Assert.Contains(Ev, e => e.Kind == TacEventKind.TrackIdentified && e.Vessel == shade.Id);
        }

        [Fact]
        public void Datalink_only_track_is_database_sourced_and_fog_off_makes_iff_truthful()
        {
            W.Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0).CruiseMps = 0;
            var ghost = W.Add("Ghost", Red, UnitClass.Bomber, 0, 400);
            ghost.Scripted = true;
            W.Detect(Blue, ghost.Id);
            Run(0.4);
            var t = Tr(Blue, ghost);
            Assert.Equal(TrackSources.Database, t.Sources);
            Assert.Equal(0.85, t.Quality, 3);
            Assert.Equal(Iff.Unknown, t.Iff);
            Assert.Equal(IdState.Detected, t.Id);
            Assert.Equal(UnitClass.Unknown, t.Class);
            Run(10);
            Assert.Equal(IdState.Classified, Tr(Blue, ghost).Id); // perceived 8 s
            T.Fog = false;
            Run(0.2);
            t = Tr(Blue, ghost);
            Assert.Equal(Iff.Hostile, t.Iff);
            Assert.Equal("Ghost", t.Name);
        }

        [Fact]
        public void Team_change_moves_the_unit_between_pictures_and_its_sensor_contributions()
        {
            var a = W.Add("Turncoat", Blue, UnitClass.Fighter, 0, 0);
            a.CruiseMps = 0;
            var watcher = W.Add("Eye", Blue, UnitClass.Aew, -20, 0);
            watcher.CruiseMps = 0;
            var r = W.Add("Red 1", Red, UnitClass.Fighter, 30, 0);
            r.CruiseMps = 0;
            Run(1.2);
            Assert.Equal(Iff.Friend, Tr(Blue, a).Iff);
            Assert.Equal(2, Tr(Blue, r).Holders);                  // Turncoat and the AEW
            a.S.Side = Red;
            W.Invalidate();
            Run(1.2);
            var asBlue = Tr(Blue, a);
            Assert.NotNull(asBlue);
            Assert.NotEqual(Iff.Friend, asBlue.Iff);               // now a contact in Blue's picture
            Assert.Equal(TrackSources.OwnSensor, asBlue.Sources);  // the AEW holds it
            Assert.Equal(1, Tr(Blue, r).Holders);                  // only the AEW now
            Assert.Equal(Iff.Friend, Tr(Red, a).Iff);
            Assert.Equal(Iff.Friend, Tr(Red, r).Iff);
        }

        [Fact]
        public void Destroyed_target_is_lost_and_removed_after_ten_seconds()
        {
            W.Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0).CruiseMps = 0;
            var b = W.Add("Bandit", Red, UnitClass.Fighter, 20, 0);
            b.Scripted = true;
            Run(0.4);
            W.Kill(b.Id);
            Run(0.2);
            Assert.Equal(TrackState.Dead, Tr(Blue, b).State);
            Assert.Contains(Ev, e => e.Kind == TacEventKind.TrackLost && e.Reason == "destroyed" && e.Vessel == b.Id);
            Assert.Contains(Ev, e => e.Kind == TacEventKind.UnitDestroyed && e.Vessel == b.Id);
            Run(10.2);
            Assert.Null(Tr(Blue, b));
        }

        [Fact]
        public void Braa_from_a_ship_matches_a_hand_computation_within_1_deg_and_50_m()
        {
            var ddg = W.Add("DDG 1", Blue, UnitClass.Ship, 0, 0);
            ddg.CruiseMps = 0;
            var b = W.Add("Bandit", Red, UnitClass.Fighter, 18, 24, 3000); // 30 km at 036.87 deg on a flat approximation
            b.Scripted = true;
            b.S.HeadingDeg = 216.87;
            b.S.SpeedMps = 250;
            Run(0.2);
            var t = Tr(Blue, b);
            double brg, rng;
            TacticalText.Braa(T.Geo, ddg.S.Pos, t, out brg, out rng);
            Assert.InRange(brg, 35.87, 37.87);
            Assert.InRange(rng, 29950, 30050);
            var line = TacticalText.Line(T, T.Team(Blue), t, TacRef.Of(ddg.S), TacUnits.Metric);
            Assert.StartsWith("T", line);
            Assert.Contains(" R30.0 ", line);
            Assert.Contains(" HOT ", line);                        // pointed at the ship
            Assert.Contains(" A3000 ", line);
            Assert.Contains(" KT486 ", line);
        }

        [Fact]
        public void Forty_inbound_missiles_rank_by_tcpa_and_compact_answers_stay_inside_the_caps()
        {
            var ships = new List<FakeUnit>();
            for (int i = 0; i < 4; i++)
            {
                var s = W.Add("DDG " + (i + 1), Blue, UnitClass.Ship, 0, i * 5);
                s.CruiseMps = 0;
                ships.Add(s);
            }
            var shooter = W.Add("Kirov", Red, UnitClass.Ship, 300, 0);
            shooter.Scripted = true;
            for (int i = 0; i < 40; i++) W.AddMissile(shooter, ships[i % 4], 20 + i * 1.5, (i % 4) * 5);
            Run(0.4);
            var p = T.Team(Blue);
            Assert.Equal(40, p.InboundToOwn);
            var ranked = new List<Track>();
            TacticalText.Threats(p, 0, ranked);
            Assert.True(ranked.Count >= 40);
            for (int i = 1; i < 40; i++) Assert.True(ranked[i - 1].Tcpa <= ranked[i].Tcpa, "ranked by tcpa");
            Assert.True(ranked[0].Threat >= 0.8);
            var top = TacticalText.ThreatText(T, p, ranked, 10, TacUnits.Naval);
            Assert.True(top.Length < 600, top.Length + ": " + top);
            Assert.Equal(11, top.Split('\n').Length);
            var sum = TacticalText.Summary(T, p, default(TacRef), TacUnits.Naval, new List<Track>());
            Assert.True(sum.Split('\n').Length <= 6);
            Assert.True(sum.Length <= 500, sum.Length + ": " + sum);
            Assert.Contains("inbound to own: 40", sum);
        }

        [Fact]
        public void Engagement_counts_come_from_weapon_managers_and_missiles()
        {
            var f1 = W.Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            var f2 = W.Add("Cobra 2", Blue, UnitClass.Fighter, 0, 2);
            var b = W.Add("Bandit", Red, UnitClass.Fighter, 15, 0);
            b.Scripted = true;
            f1.CruiseMps = f2.CruiseMps = 0;
            f1.CurrentTarget = b.Id;
            f2.CurrentTarget = b.Id;
            W.AddMissile(f1, b, 5, 0);
            Run(0.2);
            var t = Tr(Blue, b);
            Assert.Equal(2, t.EngagedBy);
            Assert.Equal(1, t.MissilesAt);
            Assert.Equal(2, T.FriendliesEngaging(Blue, b.Id, f1.Id));  // Cobra 2 + the missile
            Assert.Equal(3, T.FriendliesEngaging(Blue, b.Id, 0));
            Assert.Equal("Cobra_1+1(1)", TacticalText.Engagement(T, t));
        }

        [Fact]
        public void God_view_lists_every_unit_with_truth()
        {
            W.Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            W.Add("Far", Red, UnitClass.Ship, 900, 0);
            Run(0.2);
            Assert.Equal(2, T.God.Tracks.Count);
            Assert.All(T.God.Tracks, t => Assert.Equal(1, t.Quality));
            Assert.Contains(T.God.Tracks, t => t.Name == "Far");
        }
    }
}
