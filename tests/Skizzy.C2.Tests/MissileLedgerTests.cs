using System;
using System.Collections.Generic;
using System.Linq;
using Skizzy.C2.Fake;
using Skizzy.C2.Tactical;
using Xunit;

namespace Skizzy.C2.Tests
{
    /// <summary>
    /// The missile ledger over FakeBda and the track picture engine: lifecycle (fired, hit, missed, intercepted, destroyed,
    /// launcher lost mid-flight), team attribution, KSP part costs (multi-part missiles), kills, a 200+ missile raid and
    /// 0 B steady state.
    /// </summary>
    public sealed class MissileLedgerTests
    {
        private const string A = "A", B = "B";
        private readonly FakeBda W = new FakeBda();
        private readonly TrackPictureEngine T;
        private readonly MissileLedger L = new MissileLedger();
        private readonly List<UnitState> _units = new List<UnitState>();
        private readonly Func<uint, UnitState> _lookup;

        public MissileLedgerTests()
        {
            T = new TrackPictureEngine(W, new FakeSensors(W));
            _lookup = T.Unit;
        }

        private void Period(double dt = 0.2)
        {
            W.Step(dt);
            T.Step();
            _units.Clear();
            T.GetUnits(_units);
            L.Update(T.Now, _units, _lookup, W, T.Geo);
        }

        private FakeUnit Plane(string name, double east, double north = 0)
        {
            var u = W.Add(name, A, UnitClass.Fighter, east, north);
            u.Scripted = true; // no BDA behaviour: the test drives everything
            u.S.GuardMode = false;
            return u;
        }

        private FakeUnit Ship(string name, string side, double east, double north = 0)
        {
            var u = W.Add(name, side, UnitClass.Ship, east, north);
            u.Scripted = true;
            u.S.GuardMode = false;
            return u;
        }

        private LedgerEntry Last(LedgerKind k) { return L.Entries.Last(e => e.Kind == k); }

        [Fact]
        public void Fired_then_hit_reports_launcher_target_weapon_team_and_cost()
        {
            var hornet = Plane("Hornet 1", 0);
            var kirov = Ship("Kirov", B, 60);
            var m = W.AddMissile(hornet, kirov, 1, 0, "AGM-84 Harpoon", 1250);
            Period();
            var fired = Last(LedgerKind.Fired);
            Assert.Equal("Team A Hornet 1 fired AGM-84 Harpoon at Team B Kirov (1,250 funds)", fired.Text);
            Assert.Equal(m.Id, fired.Missile);
            Assert.Equal(hornet.Id, fired.Launcher);
            Assert.Equal(kirov.Id, fired.Target);
            Assert.Equal(A, fired.Team);
            Assert.Equal(MissileFate.Flying, L.Find(m.Id).State);
            Assert.Single(L.Flying);
            W.EndMissile(m.Id, MissileFate.Hit, kirov.Id, source: "bda_scoring");
            Period();
            var hit = Last(LedgerKind.Hit);
            Assert.Equal("Team A AGM-84 Harpoon from Hornet 1 hit Kirov (1,250 funds)", hit.Text);
            Assert.Equal(kirov.Id, hit.Other);
            Assert.Equal("bda_scoring", hit.Source);
            Assert.Empty(L.Flying);
            Assert.Equal(MissileFate.Hit, L.Resolved.Single().State);
            var a = L.TotalsOf(A);
            Assert.Equal(1, a.Fired);
            Assert.Equal(1, a.Hits);
            Assert.Equal(0, a.InFlight);
            Assert.Equal(1250, a.Spent);
            Assert.Equal(1250, a.HitCost);
        }

        [Fact]
        public void Intercept_reported_by_the_game_credits_the_interceptor_team_once()
        {
            var hornet = Plane("Hornet 1", 0);
            var kirov = Ship("Kirov", B, 60);
            var asm = W.AddMissile(hornet, kirov, 1, 0, "AGM-84", 1250);
            var sam = W.AddMissile(kirov, asm, 59, 0, "SM-2", 900);
            Period();
            Assert.Equal(2, L.Flying.Count);
            Assert.True(L.Find(sam.Id).TargetIsMissile);
            W.EndMissile(sam.Id, MissileFate.Hit, asm.Id);           // the interceptor detonated on the missile
            W.EndMissile(asm.Id, MissileFate.Destroyed);              // destroyed without exploding, by whom unknown
            Period();
            var lines = L.Entries.Where(e => e.Kind != LedgerKind.Fired).ToList();
            var ic = Assert.Single(lines);
            Assert.Equal(LedgerKind.Intercepted, ic.Kind);
            Assert.Equal("Team A AGM-84 from Hornet 1 intercepted by Team B SM-2 from Kirov (1,250 funds)", ic.Text);
            Assert.Equal(sam.Id, ic.Other);
            Assert.Equal(MissileFate.Intercepted, L.Find(asm.Id).State);
            Assert.Equal(MissileFate.Hit, L.Find(sam.Id).State);
            Assert.Equal(1, L.TotalsOf(A).Intercepted);
            Assert.Equal(1250, L.TotalsOf(A).InterceptedCost);
            Assert.Equal(1, L.TotalsOf(B).InterceptsMade);
            Assert.Equal(0, L.TotalsOf(B).Hits);                      // an intercept is not a hit on a vessel
            Assert.Equal(900, L.TotalsOf(B).Spent);
        }

        [Fact]
        public void Intercept_from_geometry_when_the_game_says_nothing_and_interceptor_dies_first()
        {
            var hornet = Plane("Hornet 1", 0);
            var kirov = Ship("Kirov", B, 60);
            var asm = W.AddMissile(hornet, kirov, 1, 0, "AGM-84");
            var sam = W.AddMissile(kirov, asm, 59, 0, "SM-2");
            Period();
            W.Remove(sam.Id);                                         // interceptor gone, target still flying this period
            Period();
            Assert.Equal(MissileFate.Flying, L.Find(sam.Id).State);   // pending, not yet called a miss
            Assert.DoesNotContain(L.Entries, e => e.Kind == LedgerKind.Missed);
            W.Remove(asm.Id);
            Period();
            var ic = Assert.Single(L.Entries, e => e.Kind != LedgerKind.Fired);
            Assert.Equal(LedgerKind.Intercepted, ic.Kind);
            Assert.Equal("geometry", ic.Source);
            Assert.Equal(1, L.TotalsOf(B).InterceptsMade);
            Assert.Equal(1, L.TotalsOf(A).CostUnknown);
            Assert.Equal(0, L.TotalsOf(A).Spent);
        }

        [Fact]
        public void Interceptor_whose_target_survives_is_a_miss_after_the_window()
        {
            var hornet = Plane("Hornet 1", 0);
            var kirov = Ship("Kirov", B, 60);
            var asm = W.AddMissile(hornet, kirov, 1, 0, "AGM-84");
            var sam = W.AddMissile(kirov, asm, 59, 0, "SM-2");
            Period();
            W.Remove(sam.Id);
            for (int i = 0; i < 10; i++) Period();
            Assert.Equal(MissileFate.Missed, L.Find(sam.Id)?.State ?? MissileFate.Missed);
            var miss = Assert.Single(L.Entries, e => e.Kind == LedgerKind.Missed);
            Assert.Equal(sam.Id, miss.Missile);
            Assert.Equal(1, L.TotalsOf(B).Misses);
            Assert.Equal(MissileFate.Flying, L.Find(asm.Id).State);
        }

        [Fact]
        public void Missed_and_destroyed_from_the_game_and_missed_from_geometry()
        {
            var hornet = Plane("Hornet 1", 0);
            var kirov = Ship("Kirov", B, 80);
            var m1 = W.AddMissile(hornet, kirov, 1, 0, "AGM-84", 1000);
            var m2 = W.AddMissile(hornet, kirov, 1, 1, "AGM-84", 1000);
            var m3 = W.AddMissile(hornet, kirov, 1, 2, "AGM-84", 1000);
            Period();
            W.EndMissile(m1.Id, MissileFate.Missed, source: "proximity");
            W.EndMissile(m2.Id, MissileFate.Destroyed, kirov.Id, source: "binding"); // CIWS
            W.Remove(m3.Id);                                                    // vanished 78 km from its target
            Period();
            Assert.Equal("Team A AGM-84 from Hornet 1 missed Team B Kirov (1,000 funds)", L.Entries.Single(e => e.Missile == m1.Id && e.Kind == LedgerKind.Missed).Text);
            Assert.Equal("Team A AGM-84 from Hornet 1 destroyed by Team B Kirov (1,000 funds)", L.Entries.Single(e => e.Kind == LedgerKind.Destroyed).Text);
            Assert.Equal("geometry", L.Entries.Single(e => e.Missile == m3.Id && e.Kind == LedgerKind.Missed).Source);
            var a = L.TotalsOf(A);
            Assert.Equal(2, a.Misses);
            Assert.Equal(1, a.Destroyed);
            Assert.Equal(2000, a.MissedCost);
            Assert.Equal(1000, a.DestroyedCost);
            Assert.Equal(3000, a.Spent);
        }

        [Fact]
        public void Hit_from_geometry_when_the_missile_vanishes_at_its_target()
        {
            var hornet = Plane("Hornet 1", 0);
            var kirov = Ship("Kirov", B, 1.0);
            var m = W.AddMissile(hornet, kirov, 0.95, 0, "AGM-84");
            m.S.Pos = kirov.S.Pos;                                     // at the target
            Period();
            W.Remove(m.Id);
            Period();
            var hit = Last(LedgerKind.Hit);
            Assert.Equal(kirov.Id, hit.Other);
            Assert.Equal("geometry", hit.Source);
        }

        [Fact]
        public void Launcher_destroyed_mid_flight_keeps_attribution()
        {
            var hornet = Plane("Hornet 1", 0);
            var kirov = Ship("Kirov", B, 60);
            var m = W.AddMissile(hornet, kirov, 1, 0, "AGM-84", 1200);
            Period();
            W.Remove(hornet.Id);                                       // shot down while its missile flies on
            Period();
            Period();
            Assert.Equal(MissileFate.Flying, L.Find(m.Id).State);
            W.EndMissile(m.Id, MissileFate.Hit, kirov.Id);
            Period();
            var hit = Last(LedgerKind.Hit);
            Assert.Equal("Team A AGM-84 from Hornet 1 hit Kirov (1,200 funds)", hit.Text);
            Assert.Equal(hornet.Id, hit.Launcher);
            Assert.Equal(1, L.TotalsOf(A).Hits);
        }

        [Fact]
        public void Team_comes_from_the_missile_then_the_launcher()
        {
            var hornet = Plane("Hornet 1", 0);
            var kirov = Ship("Kirov", B, 60);
            var m = W.AddMissile(hornet, kirov, 1, 0, "AGM-84");
            m.S.Side = "C";                                            // BDArmory's MissileBase.Team wins
            var n = W.AddMissile(hornet, kirov, 1, 1, "AGM-84");
            n.S.Side = null;                                           // no team on the missile: the launcher's
            Period();
            Assert.Equal("C", L.Find(m.Id).Team);
            Assert.Equal(A, L.Find(n.Id).Team);
            Assert.Equal(1, L.TotalsOf("C").Fired);
            Assert.Equal(1, L.TotalsOf(A).Fired);
            Assert.Equal(B, L.Find(m.Id).TargetTeam);
        }

        [Fact]
        public void Costs_sum_every_part_of_a_multi_part_missile()
        {
            Assert.Equal(1400, MissileCosts.Sum(new[] { 900.0, 250, 250 }));      // body + two boosters
            Assert.Equal(1150, MissileCosts.Part(1000, 150));                      // catalog + module costs
            Assert.Equal(0, MissileCosts.Part(-5, 0));
            Assert.True(double.IsNaN(MissileCosts.Sum(new double[0])));
            var hornet = Plane("Hornet 1", 0);
            var kirov = Ship("Kirov", B, 60);
            var modular = W.AddMissile(hornet, kirov, 1, 0, "Modular cruise", 900, 250, 250);
            var single = W.AddMissile(hornet, kirov, 1, 1, "AIM-9", 600);
            var unpriced = W.AddMissile(hornet, kirov, 1, 2, "Mystery");
            Period();
            Assert.Equal(1400, L.Find(modular.Id).Cost);
            var a = L.TotalsOf(A);
            Assert.Equal(2000, a.Spent);
            Assert.Equal(1, a.CostUnknown);
            W.EndMissile(modular.Id, MissileFate.Hit, kirov.Id);
            W.EndMissile(single.Id, MissileFate.Missed);
            W.EndMissile(unpriced.Id, MissileFate.Hit, kirov.Id);
            Period();
            Assert.Equal(1400, a.HitCost);
            Assert.Equal(600, a.MissedCost);
            Assert.Equal(2, a.Hits);
            Assert.Contains("(1,400 funds)", L.Entries.First(e => e.Kind == LedgerKind.Fired && e.Missile == modular.Id).Text);
        }

        [Fact]
        public void Kills_from_the_ledger_and_from_bda_scoring_without_duplicates()
        {
            var hornet = Plane("Hornet 1", 0);
            var kirov = Ship("Kirov", B, 60);
            var slava = Ship("Slava", B, 60, 5);
            var m1 = W.AddMissile(hornet, kirov, 1, 0, "AGM-84");
            var m2 = W.AddMissile(hornet, slava, 1, 1, "AGM-84");
            Period();
            W.EndMissile(m1.Id, MissileFate.Hit, kirov.Id);
            W.EndMissile(m2.Id, MissileFate.Hit, slava.Id);
            Period();
            W.Kill(kirov.Id);                                         // dies after the hit: the ledger's kill
            W.ScoreKill(slava.Id, "Hornet 1");                         // BDArmory scoring says so
            Period();
            Period();
            var kills = L.Entries.Where(e => e.Kind == LedgerKind.Killed).ToList();
            Assert.Equal(2, kills.Count);
            Assert.Contains(kills, k => k.Other == kirov.Id && k.Source == "ledger" && k.Text == "Kirov (Team B) destroyed, last missile hit Team A Hornet 1 AGM-84");
            Assert.Contains(kills, k => k.Other == slava.Id && k.Source == "bda_scoring" && k.Text.Contains("killed by Team A Hornet 1"));
            Assert.Equal(2, L.TotalsOf(A).Kills);
        }

        [Fact]
        public void Raid_of_220_missiles_keeps_a_consistent_log_and_totals()
        {
            // 20 planes x 2 cruise missiles at one ship; the ship fires 180 interceptors: 120 hit, 60 miss.
            var ship = Ship("Kirov", B, 0);
            var planes = new List<FakeUnit>();
            for (int i = 0; i < 20; i++) planes.Add(Plane("Hornet " + (i + 1), -80, i * 2 - 20));
            var asms = new List<FakeUnit>();
            foreach (var p in planes)
                for (int k = 0; k < 2; k++) asms.Add(W.AddMissile(p, ship, -70, p.S.Pos.Lat * 0 + k, "AGM-84", 900, 250));
            Period();
            var sams = new List<FakeUnit>();
            for (int i = 0; i < 180; i++) sams.Add(W.AddMissile(ship, asms[i % asms.Count], -1, 0, "SM-2", 800));
            Period();
            Assert.Equal(220, L.Flying.Count);
            Assert.Equal(220, L.Entries.Count(e => e.Kind == LedgerKind.Fired));
            // first wave: 30 cruise missiles intercepted by their first interceptor
            for (int i = 0; i < 30; i++) { W.EndMissile(sams[i].Id, MissileFate.Hit, asms[i].Id); W.EndMissile(asms[i].Id, MissileFate.Destroyed); }
            // the other interceptors aimed at those 30 now miss (their target is gone)
            for (int i = 30; i < 180; i++) if (i % 40 < 30) W.EndMissile(sams[i].Id, MissileFate.Missed);
            Period();
            // remaining cruise missiles: 6 hit the ship, 4 are shot down by CIWS
            for (int i = 30; i < 36; i++) W.EndMissile(asms[i].Id, MissileFate.Hit, ship.Id);
            for (int i = 36; i < 40; i++) W.EndMissile(asms[i].Id, MissileFate.Destroyed, ship.Id);
            for (int i = 30; i < 180; i++) if (i % 40 >= 30) W.EndMissile(sams[i].Id, MissileFate.Missed);
            Period();
            for (int i = 0; i < 10; i++) Period();
            Assert.Empty(L.Flying);
            var a = L.TotalsOf(A);
            var b = L.TotalsOf(B);
            Assert.Equal(40, a.Fired);
            Assert.Equal(30, a.Intercepted);
            Assert.Equal(6, a.Hits);
            Assert.Equal(4, a.Destroyed);
            Assert.Equal(a.Fired, a.Hits + a.Misses + a.Intercepted + a.Destroyed);
            Assert.Equal(40 * 1150, a.Spent);
            Assert.Equal(30 * 1150, a.InterceptedCost);
            Assert.Equal(6 * 1150, a.HitCost);
            Assert.Equal(180, b.Fired);
            Assert.Equal(30, b.InterceptsMade);
            Assert.Equal(150, b.Misses);
            Assert.Equal(180 * 800, b.Spent);
            // one line per launch and per end; intercepts are a single line
            Assert.Equal(220 + 30 + 6 + 4 + 150, L.Entries.Count);
            Assert.True(L.Entries.Select(e => e.Seq).SequenceEqual(L.Entries.Select(e => e.Seq).OrderBy(x => x)));
            Assert.True(L.MaxUpdateMs < 50, "ledger update took " + L.MaxUpdateMs + " ms");
        }

        [Fact]
        public void Steady_state_with_150_missiles_allocates_nothing()
        {
            var ship = Ship("Kirov", B, 0);
            var planes = new List<FakeUnit>();
            for (int i = 0; i < 15; i++) planes.Add(Plane("Hornet " + i, -150, i));
            for (int i = 0; i < 150; i++) W.AddMissile(planes[i % 15], ship, -140, i % 15, "AGM-84", 1000);
            for (int i = 0; i < 10; i++) Period();
            Assert.Equal(150, L.Flying.Count);
            T.Step();
            _units.Clear();
            T.GetUnits(_units);
            long best = long.MaxValue;
            for (int attempt = 0; attempt < 5 && best != 0; attempt++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 200; i++) L.Update(T.Now + 0.2 * (i + 1) + attempt * 100, _units, _lookup, W, T.Geo);
                long d = GC.GetAllocatedBytesForCurrentThread() - before;
                if (d < best) best = d;
            }
            Assert.Equal(0, best);
            Assert.Equal(150, L.Flying.Count);
        }
    }
}
