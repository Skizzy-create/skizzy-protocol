using System;
using System.Collections.Generic;
using System.Diagnostics;
using Skizzy.C2.Fake;
using Skizzy.C2.Tactical;
using Xunit;
using Xunit.Abstractions;

namespace Skizzy.C2.Tests
{
    /// <summary>
    /// M2 perf gates: the C2 tick fed by the track picture engine at 148 units (budget 0.2 ms, Release, warmed), the TPE
    /// step at 1000 records / 4 teams (amortised 0.25 ms per physics step = 2.5 ms per 5 Hz period at 50 Hz physics,
    /// 0 B steady-state allocation), and the feed reproducing the legacy picture. CI machines are slower and shared, so
    /// the asserted bounds are loose; the measured numbers are printed.
    /// </summary>
    [CollectionDefinition("perf", DisableParallelization = true)]
    public sealed class PerfCollection { }

    [Collection("perf")]
    public sealed class TpePerfTests
    {
        private readonly ITestOutputHelper _out;

        public TpePerfTests(ITestOutputHelper output) { _out = output; }

        private static C2Harness Battle148(bool tpe)
        {
            var h = new C2Harness(tpe);
            var w = h.World;
            for (int i = 0; i < 60; i++) w.Add("Blue " + i, "Blue", i % 3 == 0 ? UnitClass.Drone : UnitClass.Fighter, 0, i * 1.5);
            for (int i = 0; i < 8; i++) w.Add("DDG " + i, "Blue", UnitClass.Ship, -40, i * 8);
            for (int i = 0; i < 80; i++)
            {
                var r = w.Add("Red " + i, "Red", UnitClass.Fighter, 60, i * 1.2);
                r.Scripted = true;
                r.S.HeadingDeg = 270;
                r.S.SpeedMps = 250;
            }
            w.KillOnShot = false;
            h.Engine.SetSideManaged("Blue", true);
            h.Engine.SetSideManaged("Red", true);
            h.Run(0.2);
            Assert.True(h.Engine.Issue(OrderRequest.AllAttack("Blue")).Accepted);
            return h;
        }

        private static double Measure(C2Harness h, int ticks, out double tpeMs)
        {
            var e = h.Engine;
            if (h.Tpe != null) h.Tpe.StepOnRead = false; // the host steps the TPE on its own schedule
            double total = 0, tpe = 0;
            var events = new List<C2Event>();
            for (int i = 0; i < ticks; i++)
            {
                h.World.Step(0.2);
                long a = Stopwatch.GetTimestamp();
                if (h.Tpe != null) h.Tpe.Tpe.Step();
                long b = Stopwatch.GetTimestamp();
                e.Tick();
                total += (Stopwatch.GetTimestamp() - b) * 1000.0 / Stopwatch.Frequency;
                tpe += (b - a) * 1000.0 / Stopwatch.Frequency;
                e.DrainEvents(events);
                events.Clear();
            }
            tpeMs = tpe / ticks;
            return total / ticks;
        }

        [Fact]
        public void C2_tick_fed_by_the_tpe_at_148_units_is_inside_the_budget()
        {
            var warm = Battle148(true);
            double ignored;
            Measure(warm, 100, out ignored); // JIT
            // best of three 100-tick batches each: other processes share the machine, the minimum is the engine's cost
            double c2 = double.MaxValue, tpe = double.MaxValue, old = double.MaxValue;
            for (int rep = 0; rep < 3; rep++)
            {
                var h = Battle148(true);
                Assert.True(h.Engine.UsesPictureFeed);
                double t;
                double c = Measure(h, 100, out t);
                if (c < c2) { c2 = c; tpe = t; }
                double unused;
                double o = Measure(Battle148(false), 100, out unused);
                if (o < old) old = o;
            }
            _out.WriteLine("148 units: C2 tick over the TPE " + c2.ToString("0.000") + " ms (legacy direct picture " + old.ToString("0.000") +
                           " ms); TPE step " + tpe.ToString("0.000") + " ms per 5 Hz period");
#if DEBUG
            Assert.True(c2 < 8, "C2 tick " + c2 + " ms");
#else
            Assert.True(c2 < 1.0, "C2 tick " + c2 + " ms (budget 0.2 ms on a desktop; 1.0 ms CI bound)");
#endif
        }

        [Fact]
        public void Tpe_at_1000_records_and_4_teams_is_inside_the_budget_and_allocates_nothing_when_steady()
        {
            var w = new FakeBda();
            string[] teams = { "Blue", "Red", "Green", "Gold" };
            for (int i = 0; i < 1000; i++)
            {
                var cls = i % 10 == 0 ? UnitClass.Ship : i % 10 == 1 ? UnitClass.Aew : i % 3 == 0 ? UnitClass.Drone : UnitClass.Fighter;
                // 40 x 25 grid, 25 km apart (1000 x 600 km): realistic sensor density (a fighter holds ~10 contacts, a ship or AEW 100-150)
                var u = w.Add("U" + i, teams[i % 4], cls, (i % 40) * 25.0, (i / 40) * 25.0);
                u.Scripted = true;
                u.S.HeadingDeg = (i * 37) % 360;
                u.S.SpeedMps = cls == UnitClass.Ship ? 10 : 200;
            }
            var tpe = new TrackPictureEngine(w, new FakeSensors(w));
            for (int i = 0; i < 150; i++) { w.Step(0.2); tpe.Step(); } // JIT tiers settle
            var events = new List<TacEvent>();
            tpe.DrainEvents(events);
            tpe.MaxStepMs = 0;
            tpe.TotalStepMs = 0;
            Array.Clear(tpe.PhaseMs, 0, 4);
            long steps0 = tpe.Steps;
            long alloc;
            for (int i = 0; i < 50; i++)
            {
                w.Step(0.2); // world motion (the fake's sensing) outside the measurement
                tpe.Step();
                tpe.DrainEvents(events);
                events.Clear();
            }
            double mean = tpe.TotalStepMs / (tpe.Steps - steps0);
            double period = tpe.MaxStepMs;
            // the host runs one slice per physics step: the worst physics step is the worst slice
            var slices = new List<double>();
            for (int i = 0; i < 50; i++)
            {
                w.Step(0.2);
                bool done = false;
                while (!done)
                {
                    long a = Stopwatch.GetTimestamp();
                    done = tpe.StepSlice();
                    slices.Add((Stopwatch.GetTimestamp() - a) * 1000.0 / Stopwatch.Frequency);
                }
                tpe.DrainEvents(events);
                events.Clear();
            }
            slices.Sort();
            double worst = slices[slices.Count - 1], p99 = slices[(int)(slices.Count * 0.99)];
            // steady state: freeze the world so no track appears, drops or changes identity, then count bytes
            foreach (var u in w.List) u.S.SpeedMps = 0;
            for (int i = 0; i < 20; i++) { w.Step(0.2); tpe.Step(); tpe.DrainEvents(events); events.Clear(); }
            alloc = 0;
            for (int i = 0; i < 50; i++)
            {
                w.Step(0.2);
                long a = GC.GetAllocatedBytesForCurrentThread();
                tpe.Step();
                alloc += GC.GetAllocatedBytesForCurrentThread() - a;
            }
            int tracks = 0;
            foreach (var p in tpe.Teams) tracks += p.Tracks.Count;
            _out.WriteLine("1000 units, 4 teams, " + tracks + " team tracks: TPE step mean " + mean.ToString("0.000") + " ms (amortised over 10 physics steps " +
                           (mean / 10).ToString("0.000") + " ms), worst period " + period.ToString("0.000") + " ms, slices (one per physics step, " + tpe.SlicesPerPeriod + " per period) p99 " + p99.ToString("0.000") + " ms, max " + worst.ToString("0.000") + " ms; " + alloc + " B allocated in 50 steady steps");
            Assert.True(tracks > 1000);
#if !DEBUG
            Assert.True(mean / 10 < 0.25 * 4, "amortised " + mean / 10 + " ms (gate 0.25 ms; 4x CI slack)");
            Assert.True(p99 < 1.0 * 10, "p99 slice " + p99 + " ms (gate 1.0 ms worst step; 10x CI slack)");
#endif
            // steady state: no new tracks, no events -> no allocation (track churn and event text allocate by design)
            Assert.True(alloc < 4096, alloc + " B allocated in 50 steady steps");
        }

        [Fact]
        public void Feed_reproduces_the_legacy_picture_tick_for_tick()
        {
            var a = new C2Harness(false);
            var b = new C2Harness(true);
            foreach (var h in new[] { a, b })
            {
                var w = h.World;
                w.Add("Eye", "Blue", UnitClass.Aew, -60, 0).CruiseMps = 0;
                for (int i = 0; i < 6; i++) w.Add("Cobra " + i, "Blue", UnitClass.Fighter, 0, i * 4).CruiseMps = 0;
                var ghost = w.Add("Ghost", "Red", UnitClass.Bomber, 0, 400);
                ghost.Scripted = true;
                w.Detect("Blue", ghost.Id);
                for (int i = 0; i < 12; i++)
                {
                    var r = w.Add("Red " + i, "Red", i % 4 == 0 ? UnitClass.Drone : UnitClass.Fighter, 140, i * 3);
                    r.Scripted = true;
                    r.S.HeadingDeg = 270;
                    r.S.SpeedMps = 300;
                    r.Rcs = i % 3 == 0 ? 0.4 : 1;
                }
                h.Engine.SetSideManaged("Blue", true);
                h.Engine.SetSideManaged("Red", true);
            }
            for (int tick = 0; tick < 150; tick++)
            {
                a.Step();
                b.Step();
                foreach (var side in new[] { "Blue", "Red" })
                {
                    var pa = a.Engine.Picture(side);
                    var pb = b.Engine.Picture(side);
                    foreach (var u in a.World.List)
                    {
                        Assert.True(pa.IsInView(u.Id) == pb.IsInView(u.Id), "tick " + tick + " " + side + " in view " + u.S.Name);
                        Assert.True(pa.HolderCount(u.Id) == pb.HolderCount(u.Id), "tick " + tick + " " + side + " holders " + u.S.Name);
                        Assert.True(pa.IsIdentified(u.Id) == pb.IsIdentified(u.Id), "tick " + tick + " " + side + " identified " + u.S.Name);
                        Assert.True(pa.IsOwnSensorOnly(u.Id) == pb.IsOwnSensorOnly(u.Id), "tick " + tick + " " + side + " own-only " + u.S.Name);
                    }
                }
            }
        }
    }
}
