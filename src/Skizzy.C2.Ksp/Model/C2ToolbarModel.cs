using System;
using System.Collections.Generic;
using System.Text;

namespace Skizzy.C2.Toolbar
{
    /// <summary>
    /// What the in-game C2 window shows and does, without Unity (unit-tested off-game). It talks only to the narrow
    /// <see cref="IC2Console"/> (normally <c>C2Runtime.Current.Console</c>), never to the engine. Wording follows
    /// design/mockups/command-screen-v1.html. Main thread only.
    /// </summary>
    public sealed class C2ToolbarModel
    {
        public const string Issuer = C2Runtime.ToolbarOwner;
        /// <summary>Second click of a two-click button must come within this many seconds (mockup: 3 s).</summary>
        public const double ConfirmWindowS = 3;
        public const int MaxLines = 8;

        private readonly Func<IC2Console> _console;
        private readonly Func<double> _clock;
        private readonly List<UnitBrief> _all = new List<UnitBrief>();
        private readonly List<uint> _ids = new List<uint>();
        private readonly List<uint> _drop = new List<uint>();
        private uint _lastActive;
        private double _allAttackArmedAt = double.NegativeInfinity;
        private double _rtbAllArmedAt = double.NegativeInfinity;

        public readonly List<string> Sides = new List<string>();
        /// <summary>The side's live units (no missiles), in engine order.</summary>
        public readonly List<UnitBrief> Units = new List<UnitBrief>();
        public readonly HashSet<uint> Selected = new HashSet<uint>();
        /// <summary>Per-unit refusals and errors of the last action.</summary>
        public readonly List<string> Lines = new List<string>();

        /// <summary>The side the window commands (the active vessel's unless the user picked one).</summary>
        public string Side { get; private set; }
        public bool SidePicked { get; private set; }
        /// <summary>Selection follows the active vessel and its group when the active vessel changes.</summary>
        public bool FollowActive = true;
        /// <summary>The operator-facing line of the last action.</summary>
        public string Message;

        /// <param name="console">The console to use (looked up on every call, so the host can interpose one later).</param>
        /// <param name="clock">Real-time seconds, for the two-click confirmations.</param>
        public C2ToolbarModel(Func<IC2Console> console, Func<double> clock)
        {
            _console = console ?? throw new ArgumentNullException(nameof(console));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        private IC2Console C
        {
            get
            {
                var c = _console();
                return c != null && c.Ready ? c : null;
            }
        }

        public bool Ready => C != null;

        public string Status
        {
            get
            {
                var c = _console();
                return c == null ? "No C2 engine." : c.Status;
            }
        }

        // ---- refresh and selection --------------------------------------------------------------

        /// <summary>Re-reads sides and units; follows the active vessel (persistentId, 0 = none) when it changes.</summary>
        public void Refresh(uint activeVessel)
        {
            Sides.Clear();
            Units.Clear();
            _all.Clear();
            var c = C;
            if (c == null)
            {
                Selected.Clear();
                return;
            }
            c.ListSides(Sides);
            c.ListUnits(_all, null);
            int active = -1;
            for (int i = 0; i < _all.Count; i++)
                if (_all[i].Id == activeVessel && activeVessel != 0) { active = i; break; }
            if (!SidePicked && active >= 0) Side = _all[active].Side;
            if (Side == null || !Sides.Contains(Side)) Side = Sides.Count > 0 ? Sides[0] : null;

            for (int i = 0; i < _all.Count; i++)
            {
                var u = _all[i];
                if (u.Side == Side && u.Alive) Units.Add(u);
            }

            if (FollowActive && activeVessel != _lastActive && active >= 0 && _all[active].Side == Side)
            {
                Selected.Clear();
                SelectWithGroup(_all[active]);
            }
            _lastActive = activeVessel;

            foreach (var id in Selected)
                if (IndexOf(id) < 0) _drop.Add(id);
            for (int i = 0; i < _drop.Count; i++) Selected.Remove(_drop[i]);
            _drop.Clear();
        }

        private void SelectWithGroup(UnitBrief u)
        {
            Selected.Add(u.Id);
            var g = GroupOf(u);
            if (g == null) return;
            for (int i = 0; i < Units.Count; i++)
                if (GroupOf(Units[i]) == g) Selected.Add(Units[i].Id);
        }

        /// <summary>The C2 group if set, else the binding's callsign group.</summary>
        public static string GroupOf(UnitBrief u) { return u.C2Group ?? u.Group; }

        public int IndexOf(uint id)
        {
            for (int i = 0; i < Units.Count; i++)
                if (Units[i].Id == id) return i;
            return -1;
        }

        /// <summary>The user picks the side to command; null goes back to following the active vessel.</summary>
        public void PickSide(string side)
        {
            SidePicked = side != null;
            if (side != null && side != Side)
            {
                Side = side;
                Selected.Clear();
            }
            _lastActive = 0; // re-follow on the next refresh
        }

        public void Toggle(uint id)
        {
            if (!Selected.Remove(id) && IndexOf(id) >= 0) Selected.Add(id);
        }

        public void SelectGroupOf(uint id)
        {
            int i = IndexOf(id);
            if (i >= 0) SelectWithGroup(Units[i]);
        }

        public void SelectAll()
        {
            for (int i = 0; i < Units.Count; i++) Selected.Add(Units[i].Id);
        }

        public void ClearSelection() { Selected.Clear(); }

        /// <summary>The weapons control every selected unit shares, or null (mixed or nothing selected).</summary>
        public WeaponsControl? SelectionWeapons
        {
            get
            {
                WeaponsControl? wc = null;
                for (int i = 0; i < Units.Count; i++)
                {
                    if (!Selected.Contains(Units[i].Id)) continue;
                    if (wc == null) wc = Units[i].WeaponsControl;
                    else if (wc != Units[i].WeaponsControl) return null;
                }
                return wc;
            }
        }

        /// <summary>Advance on contact is live for the side: the side is managed and its doctrine says so.</summary>
        public bool AdvanceOn
        {
            get
            {
                var c = C;
                if (c == null || Side == null || !c.IsSideManaged(Side)) return false;
                var d = c.SideDoctrine(Side);
                return d != null && d.AdvanceOnContact;
            }
        }

        public bool AllAttackArmed => _clock() - _allAttackArmedAt <= ConfirmWindowS;
        public bool RtbAllArmed => _clock() - _rtbAllArmedAt <= ConfirmWindowS;
        public string AllAttackLabel => AllAttackArmed ? "Confirm all attack" : "All attack";
        public string RtbAllLabel => RtbAllArmed ? "Confirm RTB all" : "RTB all";
        public string AdvanceLabel => AdvanceOn ? "Advance: on" : "Advance: off";

        // ---- actions ----------------------------------------------------------------------------

        private bool NeedSelection()
        {
            if (Selected.Count > 0) return true;
            Lines.Clear();
            Message = "Select units first (the active vessel and its group are selected when you switch to them).";
            return false;
        }

        private uint[] SelectedIds()
        {
            _ids.Clear();
            for (int i = 0; i < Units.Count; i++)
                if (Selected.Contains(Units[i].Id)) _ids.Add(Units[i].Id);
            return _ids.ToArray();
        }

        /// <summary>RTB &amp; land for the selection: aircraft to their carrier, ships back to station.</summary>
        public IssueResult RtbSelected()
        {
            if (C == null || !NeedSelection()) return null;
            return Issue(OrderRequest.Rtb(Side, SelectedIds()));
        }

        /// <summary>First click arms (3 s), second click sends every aircraft of the side home. Null while armed.</summary>
        public IssueResult RtbAll()
        {
            if (C == null || Side == null) return null;
            double now = _clock();
            if (now - _rtbAllArmedAt > ConfirmWindowS)
            {
                _rtbAllArmedAt = now;
                Lines.Clear();
                Message = "Click RTB all again within 3 s to send every " + Side + " aircraft home to their carriers.";
                return null;
            }
            _rtbAllArmedAt = double.NegativeInfinity;
            return Issue(OrderRequest.RtbAll(Side));
        }

        /// <summary>First click arms (3 s), second click: every unit of the side attacks the nearest hostiles. Null while armed.</summary>
        public IssueResult AllAttack()
        {
            if (C == null || Side == null) return null;
            double now = _clock();
            if (now - _allAttackArmedAt > ConfirmWindowS)
            {
                _allAttackArmedAt = now;
                Lines.Clear();
                Message = "Click All attack again within 3 s: every " + Side + " unit attacks the nearest hostiles and the carriers launch their decks.";
                return null;
            }
            _allAttackArmedAt = double.NegativeInfinity;
            return Issue(OrderRequest.AllAttack(Side));
        }

        public IssueResult AttackNearest()
        {
            if (C == null || !NeedSelection()) return null;
            return Issue(OrderRequest.AttackNearest(Side, SelectedIds()));
        }

        /// <summary>Weapons control (fire authority) for the selection, as unit doctrine.</summary>
        public void SetWeapons(WeaponsControl wc)
        {
            var c = C;
            if (c == null || !NeedSelection()) return;
            Lines.Clear();
            string value = wc == WeaponsControl.Free ? "free" : wc == WeaponsControl.Tight ? "tight" : "hold";
            var values = new Dictionary<string, object> { { "weaponsControl", value } };
            int ok = 0;
            foreach (var id in SelectedIds())
            {
                var errors = c.SetDoctrine(DoctrineLevel.Unit, id.ToString(System.Globalization.CultureInfo.InvariantCulture), values);
                if (errors == null || errors.Count == 0) ok++;
                else AddLine(NameOf(id) + ": " + string.Join("; ", errors.ToArray()));
            }
            Message = Names(Selected.Count) + ": weapons " + (wc == WeaponsControl.Free ? "Free" : wc == WeaponsControl.Tight ? "Tight" : "Hold")
                + (ok < Selected.Count ? " (" + (Selected.Count - ok) + " refused)." : ".");
        }

        /// <summary>Advance on contact for the side. Turning it on also puts the side under Skizzy control.</summary>
        public void SetAdvance(bool on)
        {
            var c = C;
            if (c == null || Side == null) return;
            Lines.Clear();
            if (on) c.SetSideManaged(Side, true);
            var errors = c.SetDoctrine(DoctrineLevel.Side, Side, new Dictionary<string, object> { { "advanceOnContact", on } });
            if (errors != null)
                foreach (var e in errors) AddLine(e);
            Message = on ? "Advance on contact on." : "Advance on contact off: aircraft hold their stations until ordered.";
        }

        /// <summary>Ends the selection's orders and unit weapons control: back to the BDArmory AI.</summary>
        public void ReleaseSelected()
        {
            var c = C;
            if (c == null || !NeedSelection()) return;
            Lines.Clear();
            var keys = new[] { "weaponsControl" };
            foreach (var id in SelectedIds())
            {
                c.Release(id, Issuer);
                c.UnsetDoctrine(DoctrineLevel.Unit, id.ToString(System.Globalization.CultureInfo.InvariantCulture), keys);
            }
            Message = Names(Selected.Count) + ": released to the BDArmory AI.";
        }

        private IssueResult Issue(OrderRequest req)
        {
            req.Issuer = Issuer;
            req.Role = IssuerRole.Seat;
            var res = C.Issue(req);
            Lines.Clear();
            Message = !string.IsNullOrEmpty(res.Summary) ? res.Summary : res.Message;
            int refused = 0;
            foreach (var u in res.Units)
            {
                if (u.Accepted) continue;
                refused++;
                if (refused <= MaxLines) Lines.Add((u.Name ?? NameOf(u.Unit)) + ": " + (u.Reason ?? Orders.Wire(u.Code)));
            }
            if (refused > MaxLines) Lines.Add("+" + (refused - MaxLines) + " more refused.");
            return res;
        }

        private void AddLine(string s)
        {
            if (Lines.Count < MaxLines) Lines.Add(s);
        }

        private string NameOf(uint id)
        {
            int i = IndexOf(id);
            return i >= 0 ? Units[i].Name : "#" + id;
        }

        private string Names(int count)
        {
            if (count == 1)
                foreach (var id in Selected) return NameOf(id);
            return count + " units";
        }

        // ---- formatting -------------------------------------------------------------------------

        /// <summary>The unit's task in the mockup's words ("BDArmory AI", "attack nearest", "RTB" ...).</summary>
        public static string TaskLabel(string task)
        {
            switch (task)
            {
                case null:
                case "":
                case "guard": return "BDArmory AI";
                case "rtb": return "RTB";
                case "move_to": return "move";
                case "move_route": return "route";
                case "engage": return "attack";
                case "hold_pattern": return "racetrack";
                case "defend_unit": return "protect";
                case "vip_priority": return "protect VIP";
                default: return task.Replace('_', ' ');
            }
        }

        public static string ClassLabel(UnitClass c)
        {
            switch (c)
            {
                case UnitClass.Fighter: return "FTR";
                case UnitClass.Drone: return "UAV";
                case UnitClass.Bomber: return "BMR";
                case UnitClass.Aew: return "AEW";
                case UnitClass.Carrier: return "CV";
                case UnitClass.Ship: return "SHIP";
                case UnitClass.Submarine: return "SUB";
                case UnitClass.Airfield: return "AF";
                case UnitClass.Ground: return "GND";
                default: return "?";
            }
        }

        /// <summary>One unit-list row: "FTR  Cobra 1  attack nearest  AAM 2/4  Tight".</summary>
        public static string Row(UnitBrief u)
        {
            var sb = new StringBuilder(64);
            sb.Append(ClassLabel(u.Class)).Append("  ").Append(u.Name).Append("  ").Append(TaskLabel(u.Task));
            if (u.MissilesMax > 0) sb.Append("  ").Append(UnitClasses.IsAircraft(u.Class) ? "AAM " : "MSL ").Append(u.MissilesReady).Append('/').Append(u.MissilesMax);
            if (u.WeaponsControl != WeaponsControl.Free) sb.Append("  ").Append(u.WeaponsControl == WeaponsControl.Tight ? "Tight" : "Hold");
            if (!u.Present) sb.Append("  (out of range)");
            else if (!u.AiActive && u.HasWeaponManager) sb.Append("  (AI off)");
            return sb.ToString();
        }
    }
}
