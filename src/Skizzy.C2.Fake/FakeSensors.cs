using System.Collections.Generic;
using Skizzy.C2.Tactical;

namespace Skizzy.C2.Fake
{
    /// <summary>
    /// <see cref="ITacticalSensors"/> over <see cref="FakeBda"/>: each weapon manager's current target, and missile
    /// warnings for missiles homing on a unit within <see cref="WarningRangeM"/> (the fake's F12 LastScan).
    /// </summary>
    public sealed class FakeSensors : ITacticalSensors
    {
        private readonly FakeBda _w;
        private readonly Dictionary<uint, List<uint>> _inbound = new Dictionary<uint, List<uint>>();
        private double _builtAt = double.NaN;
        private int _builtCount = -1;
        public double WarningRangeM = 25000;

        public FakeSensors(FakeBda world) { _w = world; }

        public uint CurrentTarget(uint unit)
        {
            var u = _w.Get(unit);
            return u != null && u.S.Alive && !u.S.IsMissile ? u.CurrentTarget : 0;
        }

        public void GetMissileWarnings(uint unit, List<uint> into)
        {
            if (_builtAt != _w.Now || _builtCount != _w.List.Count) Build();
            List<uint> l;
            var u = _w.Get(unit);
            if (u == null || !u.S.Alive || !_inbound.TryGetValue(unit, out l)) return;
            for (int i = 0; i < l.Count; i++)
            {
                var m = _w.Get(l[i]);
                if (m != null && m.S.Alive && _w.Geo.Distance(m.S.Pos, u.S.Pos) <= WarningRangeM) into.Add(m.Id);
            }
        }

        private void Build()
        {
            _builtAt = _w.Now;
            _builtCount = _w.List.Count;
            foreach (var l in _inbound.Values) l.Clear();
            for (int i = 0; i < _w.List.Count; i++)
            {
                var m = _w.List[i];
                if (!m.S.Alive || !m.S.IsMissile || m.S.MissileTarget == 0) continue;
                List<uint> l;
                if (!_inbound.TryGetValue(m.S.MissileTarget, out l)) _inbound[m.S.MissileTarget] = l = new List<uint>();
                l.Add(m.Id);
            }
        }
    }
}
