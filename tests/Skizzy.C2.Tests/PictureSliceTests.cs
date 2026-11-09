using System.Collections.Generic;
using System.Linq;
using Skizzy.C2.Fake;
using Xunit;

namespace Skizzy.C2.Tests
{
    /// <summary>
    /// The time-sliced own-sensor picture (C2Engine.ContactSlices): each unit's contacts are re-read every few ticks,
    /// staggered, and holder counts are kept incrementally. They must match a full recount, and a dead or re-teamed
    /// holder must leave the counts at once.
    /// </summary>
    public class PictureSliceTests : TestKit
    {
        private sealed class Counting : IC2World
        {
            private readonly FakeBda _w;
            public int ContactCalls;
            public Counting(FakeBda w) { _w = w; }
            public double Now => _w.Now;
            public double BodyRadius => _w.BodyRadius;
            public HookSet Hooks => _w.Hooks;
            public void GetUnits(List<UnitState> into) { _w.GetUnits(into); }
            public void GetOwnContacts(uint unit, List<uint> into) { ContactCalls++; _w.GetOwnContacts(unit, into); }
            public bool SideDetects(string side, uint target) { return _w.SideDetects(side, target); }
            public bool AreHostile(string a, string b) { return _w.AreHostile(a, b); }
            public int FriendliesEngaging(string side, uint target, uint except) { return _w.FriendliesEngaging(side, target, except); }
        }

        private readonly List<FakeUnit> _blue = new List<FakeUnit>();
        private readonly List<FakeUnit> _red = new List<FakeUnit>();

        private void Static(FakeUnit u)
        {
            u.Scripted = true;
            u.S.SpeedMps = 0;
        }

        private void Build()
        {
            for (int i = 0; i < 12; i++) { var b = Add("Cobra " + i, Blue, UnitClass.Fighter, 0, i * 4); Static(b); _blue.Add(b); }
            for (int i = 0; i < 10; i++) { var r = Add("Bandit " + i, Red, UnitClass.Fighter, 30, i * 5); Static(r); _red.Add(r); }
            W.KillOnShot = false;
            E.SetSideManaged(Blue, true);
            E.SetSideManaged(Red, true);
        }

        /// <summary>Holders of <paramref name="target"/> on <paramref name="side"/>, recounted from the world now.</summary>
        private int Recount(string side, uint target)
        {
            int n = 0;
            var c = new List<uint>();
            foreach (var u in W.List)
            {
                if (!u.S.Alive || u.S.Side != side || u.S.IsMissile) continue;
                c.Clear();
                W.GetOwnContacts(u.Id, c);
                if (c.Contains(target)) n++;
            }
            return n;
        }

        private bool Holds(FakeUnit u, FakeUnit target)
        {
            var c = new List<uint>();
            W.GetOwnContacts(u.Id, c);
            return c.Contains(target.Id);
        }

        [Fact]
        public void Holder_counts_match_a_full_recount_and_contacts_are_read_in_slices()
        {
            Build();
            Assert.Equal(5, E.ContactSlices);
            Settle(1.2); // every unit refreshed at least once
            foreach (var r in _red) Assert.Equal(Recount(Blue, r.Id), E.Picture(Blue).HolderCount(r.Id));
            foreach (var b in _blue) Assert.Equal(Recount(Red, b.Id), E.Picture(Red).HolderCount(b.Id));
            Assert.Contains(_red, r => E.Picture(Blue).HolderCount(r.Id) > 0);
        }

        [Fact]
        public void Each_tick_reads_about_one_slice_of_the_units()
        {
            var w = new FakeBda();
            var counting = new Counting(w);
            var e = new C2Engine(counting, w);
            for (int i = 0; i < 20; i++) w.Add("Cobra " + i, Blue, UnitClass.Fighter, 0, i * 2);
            for (int i = 0; i < 20; i++) w.Add("Bandit " + i, Red, UnitClass.Fighter, 30, i * 2);
            e.SetSideManaged(Blue, true);
            e.SetSideManaged(Red, true);
            w.Step(0.2);
            e.Tick();
            Assert.Equal(40, counting.ContactCalls); // first tick: everyone
            int before = counting.ContactCalls;
            for (int i = 0; i < 10; i++) { w.Step(0.2); e.Tick(); }
            Assert.Equal(80, counting.ContactCalls - before); // 40 units x 10 ticks / 5 slices
        }

        [Fact]
        public void A_dead_or_re_teamed_holder_leaves_the_counts_at_once()
        {
            Build();
            Settle(1.2);
            var target = _red.First(r => E.Picture(Blue).HolderCount(r.Id) >= 2);
            int held = E.Picture(Blue).HolderCount(target.Id);
            var holder = _blue.First(b => Holds(b, target));

            W.Kill(holder.Id);
            Settle(0.2);
            Assert.Equal(held - 1, E.Picture(Blue).HolderCount(target.Id));

            var other = _blue.First(b => b.S.Alive && b != holder && Holds(b, target));
            other.S.Side = Red; // BDA team change
            Settle(0.2);
            Assert.Equal(held - 2, E.Picture(Blue).HolderCount(target.Id));
            Settle(1.2);
            foreach (var r in _red) Assert.Equal(Recount(Blue, r.Id), E.Picture(Blue).HolderCount(r.Id));
        }

        [Fact]
        public void Slices_of_one_reread_every_tick()
        {
            Build();
            E.ContactSlices = 1;
            Settle(0.4);
            foreach (var r in _red) Assert.Equal(Recount(Blue, r.Id), E.Picture(Blue).HolderCount(r.Id));
        }
    }
}
