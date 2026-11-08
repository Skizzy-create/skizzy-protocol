using System;
using System.Collections.Generic;
using Skizzy.C2.Fake;
using Xunit;

namespace Skizzy.C2.Tests
{
    /// <summary>The engine handover point (C2Runtime): attach/detach, tick ownership, report fan-out, faults.</summary>
    public class C2RuntimeTests
    {
        private const string Blue = "Blue";
        private const string Red = "Red";

        private static (C2Runtime rt, FakeBinding b) Make()
        {
            var rt = new C2Runtime();
            var b = new FakeBinding();
            return (rt, b);
        }

        [Fact]
        public void Attach_builds_an_engine_and_connects_the_binding()
        {
            var (rt, b) = Make();
            C2Engine changed = null;
            int changes = 0;
            rt.EngineChanged += e => { changed = e; changes++; };
            Assert.False(rt.Ready);
            Assert.Contains("No C2 engine", rt.Status);

            var engine = rt.Attach(b);
            Assert.True(rt.Ready);
            Assert.Same(engine, rt.Engine);
            Assert.Same(engine, changed);
            Assert.Same(engine, b.Sink);
            Assert.Equal(C2Runtime.ToolbarOwner, rt.Owner);
            Assert.Contains("toolbar", rt.Status);
            Assert.Equal(HookSet.All, rt.Hooks);

            rt.Detach(b);
            Assert.False(rt.Ready);
            Assert.Null(changed);
            Assert.Equal(2, changes);
            Assert.Null(b.Sink);
            Assert.Equal(1, b.ReleaseAllCalls);
            Assert.Equal(HookSet.None, rt.Hooks);
        }

        [Fact]
        public void A_second_attach_detaches_the_first_binding()
        {
            var (rt, b1) = Make();
            var b2 = new FakeBinding();
            var e1 = rt.Attach(b1);
            var e2 = rt.Attach(b2);
            Assert.NotSame(e1, e2);
            Assert.Equal(1, b1.ReleaseAllCalls);
            Assert.Null(b1.Sink);
            rt.Detach(b1); // stale: ignored
            Assert.True(rt.Ready);
            Assert.Equal(0, b2.ReleaseAllCalls);
        }

        [Fact]
        public void Only_the_owner_ticks_and_the_host_can_claim_and_return_it()
        {
            var (rt, b) = Make();
            rt.Attach(b);
            var f = b.World.Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            Assert.True(rt.Tick(C2Runtime.ToolbarOwner));
            Assert.False(rt.Tick("host"));

            Assert.True(rt.Claim("host"));
            Assert.Equal("host", rt.Owner);
            Assert.False(rt.Tick(C2Runtime.ToolbarOwner));
            Assert.True(rt.Tick("host"));
            Assert.Contains("host", rt.Status);

            rt.Unclaim("someone else");
            Assert.Equal("host", rt.Owner);
            rt.Unclaim("host");
            Assert.Equal(C2Runtime.ToolbarOwner, rt.Owner);
            Assert.True(rt.Tick(C2Runtime.ToolbarOwner));
            Assert.False(rt.Claim(""));

            var units = new List<UnitBrief>();
            rt.ListUnits(units, Blue);
            Assert.Single(units);
            Assert.Equal(f.Id, units[0].Id);
        }

        [Fact]
        public void Reports_are_drained_once_kept_in_a_ring_and_raised()
        {
            var (rt, b) = Make();
            rt.Attach(b);
            var w = b.World;
            var f = w.Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            var seen = new List<C2Event>();
            rt.Reported += e => seen.Add(e);
            rt.Reported += e => throw new InvalidOperationException("a bad subscriber");
            rt.Tick(C2Runtime.ToolbarOwner);

            int serial = rt.ReportSerial;
            var res = rt.Issue(OrderRequest.MoveTo(Blue, w.At(30, 0), f.Id));
            Assert.True(res.Accepted, res.Summary);
            Assert.True(rt.ReportSerial > serial);
            Assert.Contains(seen, e => e.Kind == C2EventKind.OrderAccepted);

            // drained by the runtime: the engine has nothing left
            var left = new List<C2Event>();
            rt.Engine.DrainEvents(left);
            Assert.Empty(left);

            for (int i = 0; i < 100; i++) rt.Issue(OrderRequest.Rtb(Blue, 999)); // refusals, many reports
            var recent = new List<C2Event>();
            rt.CopyReports(recent, 500);
            Assert.Equal(C2Runtime.ReportRing, recent.Count);
            Assert.Equal(seen[seen.Count - 1], recent[recent.Count - 1]);
            recent.Clear();
            rt.CopyReports(recent, 3);
            Assert.Equal(3, recent.Count);
            Assert.Equal(seen[seen.Count - 1], recent[2]);
        }

        [Fact]
        public void Console_calls_without_an_engine_are_safe()
        {
            var rt = new C2Runtime();
            var res = rt.Issue(OrderRequest.RtbAll(Blue));
            Assert.False(res.Accepted);
            Assert.Contains("No C2 engine", res.Summary);
            Assert.False(rt.Cancel(1, "x"));
            Assert.False(rt.Release(1, "x"));
            Assert.Single(rt.SetDoctrine(DoctrineLevel.Side, Blue, new Dictionary<string, object> { { "weaponsControl", "hold" } }));
            rt.UnsetDoctrine(DoctrineLevel.Side, Blue, new[] { "weaponsControl" });
            rt.SetSideManaged(Blue, true);
            Assert.False(rt.IsSideManaged(Blue));
            Assert.Null(rt.GetUnitView(1));
            Assert.Null(rt.SideDoctrine(Blue));
            Assert.False(rt.Tick(C2Runtime.ToolbarOwner));
            var l = new List<string>();
            rt.ListSides(l);
            Assert.Empty(l);
        }

        [Fact]
        public void An_interposed_console_is_what_uis_get()
        {
            var rt = new C2Runtime();
            Assert.Same(rt, rt.Console);
            var other = new C2Runtime();
            rt.SetConsole(other);
            Assert.Same(other, rt.Console);
            rt.SetConsole(null);
            Assert.Same(rt, rt.Console);
        }

        private sealed class ThrowingWorld : IC2World
        {
            public double Now => 1;
            public double BodyRadius => Geo.KerbinRadius;
            public HookSet Hooks => HookSet.All;
            public void GetUnits(List<UnitState> into) { throw new InvalidOperationException("binding broke"); }
            public void GetOwnContacts(uint unit, List<uint> into) { }
            public bool SideDetects(string side, uint target) { return false; }
            public bool AreHostile(string a, string b) { return a != b; }
            public int FriendliesEngaging(string side, uint target, uint exceptUnit) { return 0; }
        }

        private sealed class ThrowingBinding : IC2Binding
        {
            private readonly FakeBda _ctl = new FakeBda();
            public IC2World World { get; } = new ThrowingWorld();
            public IBdaControl Control => _ctl;
            public string Description => "throws";
            public void Connect(IHookEvents sink) { }
            public void ReleaseAll() { }
        }

        [Fact]
        public void Engine_faults_are_logged_and_stop_ticking_after_the_limit()
        {
            var rt = new C2Runtime();
            var log = new List<string>();
            rt.LogError = s => log.Add(s);
            rt.Attach(new ThrowingBinding());
            for (int i = 0; i < C2Runtime.FaultLimit; i++) Assert.True(rt.Tick(C2Runtime.ToolbarOwner));
            Assert.NotNull(rt.Fault);
            Assert.Contains("binding broke", rt.Status);
            Assert.False(rt.Tick(C2Runtime.ToolbarOwner));
            Assert.True(log.Count >= C2Runtime.FaultLimit);

            rt.Attach(new FakeBinding()); // a new scene starts clean
            Assert.Null(rt.Fault);
            Assert.True(rt.Tick(C2Runtime.ToolbarOwner));
        }
    }
}
