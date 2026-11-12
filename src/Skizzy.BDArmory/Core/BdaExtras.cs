using System;
using System.Collections.Generic;
using Skizzy.C2;
using Skizzy.C2.Tactical;

namespace Skizzy.Bda
{
    /// <summary>
    /// What the M2 track picture and EMCON need from the game beyond <see cref="IBdaGame"/>: each weapon manager's
    /// current target, its missile warnings (F12 LastScan incoming missiles) and radar on/off. Optional: a game
    /// without it gives the picture no engagement columns or warnings and the engine no radar control.
    /// </summary>
    public interface IBdaExtras
    {
        /// <summary>The vessel id the weapon manager targets now, 0 for none.</summary>
        uint CurrentTarget(object wm);

        /// <summary>Vessel ids of missiles the weapon manager's last scan reports incoming.</summary>
        void GetMissileWarnings(object wm, List<uint> into);

        /// <summary>Switches the vessel's radars on or off; false when it has none.</summary>
        bool SetRadarActive(object vessel, bool active);
    }

    public sealed partial class BdaBinding : ITacticalSensors, IRadarControl, IMissileOutcomes
    {
        private IBdaExtras Extras => _game as IBdaExtras;

        /// <summary>M2 missile ledger: how a missile ended, from the game layer when it knows (KspBdaGame reads BDArmory's MissileBase).</summary>
        public bool TryGetEnd(uint missile, out MissileEnd end)
        {
            end = default(MissileEnd);
            var o = _game as IMissileOutcomes;
            if (o == null) return false;
            try { return o.TryGetEnd(missile, out end); }
            catch (Exception e) { Fault("missile end " + missile, e); return false; }
        }

        public void DrainKills(List<KillReport> into)
        {
            var o = _game as IMissileOutcomes;
            if (o == null) return;
            try { o.DrainKills(into); }
            catch (Exception e) { Fault("kills", e); }
        }

        public uint CurrentTarget(uint unit)
        {
            var x = Extras;
            if (x == null || !_slots.TryGetValue(unit, out var s) || !s.Present || s.Wm == null) return 0;
            try { return x.CurrentTarget(s.Wm); }
            catch (Exception e) { Fault("target " + unit, e); return 0; }
        }

        public void GetMissileWarnings(uint unit, List<uint> into)
        {
            var x = Extras;
            if (x == null || !Has(HookSet.F12) || !_slots.TryGetValue(unit, out var s) || !s.Present || s.Wm == null) return;
            try { x.GetMissileWarnings(s.Wm, into); }
            catch (Exception e) { Fault("warnings " + unit, e); }
        }

        public bool SetRadarActive(uint unit, bool active)
        {
            var x = Extras;
            if (x == null || !_slots.TryGetValue(unit, out var s) || !s.Present || s.Vessel == null) return false;
            try { return x.SetRadarActive(s.Vessel, active); }
            catch (Exception e) { Fault("radar " + unit, e); return false; }
        }
    }
}
