using System.Collections.Generic;
using System.Linq;
using Skizzy.Bda;
using Skizzy.C2;
using Skizzy.C2.Fake;
using Skizzy.C2.Toolbar;
using Xunit;

namespace Skizzy.Ksp.Tests
{
    /// <summary>The C2 window's model over the real runtime and engine (FakeBda world), as the window drives it.</summary>
    public class ToolbarModelTests
    {
        private const string Blue = "Blue";
        private const string Red = "Red";

        private readonly C2Runtime Rt = new C2Runtime();
        private readonly FakeBinding B = new FakeBinding();
        private double _clock = 100;
        private readonly C2ToolbarModel M;

        public ToolbarModelTests()
        {
            M = new C2ToolbarModel(() => Rt.Console, () => _clock);
        }

        private FakeBda W => B.World;

        private FakeUnit Add(string name, string side, UnitClass cls, double eastKm, double northKm)
        {
            var u = W.Add(name, side, cls, eastKm, northKm);
            u.S.Group = UnitClassifier.CallsignGroup(name);
            return u;
        }

        private void Run(double s)
        {
            for (double t = 0; t < s - 1e-9; t += 0.2)
            {
                W.Step(0.2);
                Rt.Tick(C2Runtime.ToolbarOwner);
            }
        }

        private void Start()
        {
            Rt.Attach(B);
            Run(0.4);
        }

        [Fact]
        public void Without_an_engine_the_window_says_why_and_nothing_throws()
        {
            Assert.False(M.Ready);
            Assert.Contains("No C2 engine", M.Status);
            M.Refresh(1);
            Assert.Empty(M.Units);
            Assert.Null(M.RtbAll());
            Assert.Null(M.AllAttack());
            Assert.Null(M.AttackNearest());
            M.SetWeapons(WeaponsControl.Hold);
            M.SetAdvance(true);
            M.ReleaseSelected();
            Assert.False(M.AdvanceOn);
        }

        [Fact]
        public void Selection_follows_the_active_vessel_and_its_callsign_group_and_side()
        {
            var c1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            var c2 = Add("Cobra 2", Blue, UnitClass.Fighter, 0, 2);
            var v1 = Add("Viper 1", Blue, UnitClass.Fighter, 0, 4);
            var b1 = Add("Bandit 1", Red, UnitClass.Fighter, 60, 0);
            var b2 = Add("Bandit 2", Red, UnitClass.Fighter, 60, 2);
            Start();

            M.Refresh(c2.Id);
            Assert.Equal(Blue, M.Side);
            Assert.Equal(new[] { c1.Id, c2.Id, v1.Id }.OrderBy(x => x), M.Units.Select(u => u.Id).OrderBy(x => x));
            Assert.Equal(new HashSet<uint> { c1.Id, c2.Id }, M.Selected);

            M.Toggle(v1.Id);
            M.Refresh(c2.Id); // same active vessel: the user's selection stays
            Assert.Contains(v1.Id, M.Selected);

            M.Refresh(b1.Id); // switched to a Red vessel: the window follows its side
            Assert.Equal(Red, M.Side);
            Assert.Equal(new HashSet<uint> { b1.Id, b2.Id }, M.Selected);

            M.PickSide(Blue); // pinned: switching vessels no longer changes the side
            M.Refresh(b2.Id);
            Assert.Equal(Blue, M.Side);
            Assert.Empty(M.Selected);
            M.SelectGroupOf(v1.Id);
            Assert.Equal(new HashSet<uint> { v1.Id }, M.Selected);

            // a destroyed unit leaves the list and the selection
            M.SelectAll();
            W.Kill(c1.Id);
            Run(0.4);
            M.Refresh(b2.Id);
            Assert.DoesNotContain(c1.Id, M.Selected);
            Assert.DoesNotContain(M.Units, u => u.Id == c1.Id);
        }

        [Fact]
        public void Rtb_shows_the_refusal_of_each_unit()
        {
            W.Hooks = HookSet.None; // stock BDArmory: aircraft RTB needs F6
            var c1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            var c2 = Add("Cobra 2", Blue, UnitClass.Fighter, 0, 2);
            Add("Kestrel [CV]", Blue, UnitClass.Carrier, -30, 0);
            Start();
            M.Refresh(c1.Id);
            var res = M.RtbSelected();
            Assert.NotNull(res);
            Assert.False(res.Accepted);
            Assert.Equal(2, M.Lines.Count);
            Assert.All(M.Lines, l => Assert.Matches("^Cobra [12]: ", l));
            Assert.Contains(M.Lines, l => l.Contains("F6") || l.Contains("HOOK_MISSING"));
            Assert.False(string.IsNullOrEmpty(M.Message));
        }

        [Fact]
        public void Orders_carry_the_toolbar_as_issuer_and_need_a_selection()
        {
            var c1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            Add("Kestrel [CV]", Blue, UnitClass.Carrier, -30, 0);
            Add("Bandit 1", Red, UnitClass.Fighter, 40, 0);
            Start();
            M.Refresh(0);
            Assert.Null(M.AttackNearest());
            Assert.Contains("Select units first", M.Message);

            M.Refresh(c1.Id);
            var res = M.RtbSelected();
            Assert.True(res.Accepted, res.Summary);
            Assert.Equal(C2Runtime.ToolbarOwner, res.Order.Issuer);
            Assert.Empty(M.Lines);
            Assert.Equal(res.Summary, M.Message);
            Assert.True(Rt.Commanded);
            Run(0.4);
            M.Refresh(c1.Id);
            Assert.Equal("RTB", C2ToolbarModel.TaskLabel(M.Units.First(u => u.Id == c1.Id).Task));
        }

        [Fact]
        public void All_attack_and_rtb_all_need_a_second_click_within_three_seconds()
        {
            var c1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            Add("Kestrel [CV]", Blue, UnitClass.Carrier, -30, 0);
            Add("Bandit 1", Red, UnitClass.Fighter, 40, 0);
            Start();
            M.Refresh(c1.Id);

            Assert.Null(M.AllAttack());
            Assert.True(M.AllAttackArmed);
            Assert.Equal("Confirm all attack", M.AllAttackLabel);
            _clock += 3.5; // too late: the first click expired
            Assert.False(M.AllAttackArmed);
            Assert.Null(M.AllAttack());
            _clock += 1;
            var res = M.AllAttack();
            Assert.NotNull(res);
            Assert.Equal(OrderType.AllAttack, res.Order.Type);
            Assert.Equal("All attack", M.AllAttackLabel);

            Assert.Null(M.RtbAll());
            Assert.Equal("Confirm RTB all", M.RtbAllLabel);
            _clock += 2;
            var rtb = M.RtbAll();
            Assert.True(rtb.Accepted, rtb.Summary);
            Assert.True(rtb.Order.Request.SideScope);
        }

        [Fact]
        public void Weapons_control_is_unit_doctrine_for_the_selection_and_release_clears_it()
        {
            var c1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            var c2 = Add("Cobra 2", Blue, UnitClass.Fighter, 0, 2);
            var v1 = Add("Viper 1", Blue, UnitClass.Fighter, 0, 4);
            Start();
            M.Refresh(c1.Id);
            Assert.Equal(WeaponsControl.Free, M.SelectionWeapons);
            M.SetWeapons(WeaponsControl.Hold);
            Assert.Equal("2 units: weapons Hold.", M.Message);
            Run(0.4);
            M.Refresh(c1.Id);
            Assert.Equal(WeaponsControl.Hold, M.SelectionWeapons);
            Assert.Equal(FireAuthorityMode.Hold, c1.Fire);
            Assert.Equal(FireAuthorityMode.Free, v1.Fire);
            M.Toggle(v1.Id);
            Assert.Null(M.SelectionWeapons); // mixed

            M.ReleaseSelected();
            Run(0.4);
            M.Refresh(c1.Id);
            Assert.Equal(WeaponsControl.Free, M.SelectionWeapons);
            Assert.Equal(FireAuthorityMode.Free, c2.Fire);
        }

        [Fact]
        public void Advance_on_contact_manages_the_side_and_off_keeps_stations()
        {
            var c1 = Add("Cobra 1", Blue, UnitClass.Fighter, 0, 0);
            Start();
            M.Refresh(c1.Id);
            Assert.False(M.AdvanceOn);
            Assert.Equal("Advance: off", M.AdvanceLabel);
            M.SetAdvance(true);
            Assert.True(Rt.IsSideManaged(Blue));
            Assert.True(M.AdvanceOn);
            Assert.Equal("Advance on contact on.", M.Message);
            M.SetAdvance(false);
            Assert.False(M.AdvanceOn);
            Assert.False(Rt.SideDoctrine(Blue).AdvanceOnContact);
            Assert.StartsWith("Advance on contact off", M.Message);
        }

        [Fact]
        public void Rows_use_the_mockup_wording()
        {
            var u = new UnitBrief { Name = "Cobra 1", Class = UnitClass.Fighter, Task = "attack_nearest", MissilesReady = 2, MissilesMax = 4, WeaponsControl = WeaponsControl.Tight, Present = true, AiActive = true, HasWeaponManager = true };
            Assert.Equal("FTR  Cobra 1  attack nearest  AAM 2/4  Tight", C2ToolbarModel.Row(u));
            Assert.Equal("BDArmory AI", C2ToolbarModel.TaskLabel("guard"));
            Assert.Equal("racetrack", C2ToolbarModel.TaskLabel("hold_pattern"));
            u.Present = false;
            Assert.EndsWith("(out of range)", C2ToolbarModel.Row(u));
        }
    }
}
