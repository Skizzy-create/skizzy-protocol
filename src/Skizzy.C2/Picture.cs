using System;
using System.Collections.Generic;

namespace Skizzy.C2
{
    /// <summary>
    /// The minimal per-side picture the C2 engine needs until the M2 track engine exists: which hostiles are in
    /// view, which are identified by an own unit's own radar inside its class range (row 24), which are held by one
    /// own-sensor only (row 7), and when the first hostile was identified (row 28).
    /// </summary>
    public sealed class SidePicture
    {
        public readonly string Side;

        /// <summary>Alive hostile non-missile units in view (side database or an own unit's own sensors).</summary>
        public readonly List<uint> Hostiles = new List<uint>();
        /// <summary>Alive hostile missiles in view.</summary>
        public readonly List<uint> HostileMissiles = new List<uint>();

        internal readonly HashSet<uint> InView = new HashSet<uint>();
        /// <summary>The same units as <see cref="Hostiles"/> / <see cref="HostileMissiles"/>, as records (no id lookups in hot loops).</summary>
        internal readonly List<UnitRec> HostileRecs = new List<UnitRec>();
        internal readonly List<UnitRec> MissileRecs = new List<UnitRec>();
        internal readonly HashSet<uint> Identified = new HashSet<uint>();
        internal readonly Dictionary<uint, int> Holders = new Dictionary<uint, int>();
        internal readonly Dictionary<uint, uint> SoleHolder = new Dictionary<uint, uint>();
        internal readonly HashSet<uint> SurveillanceHeld = new HashSet<uint>();
        internal readonly HashSet<uint> OwnOnlyReported = new HashSet<uint>();
        internal readonly Dictionary<uint, double> LastInView = new Dictionary<uint, double>();
        /// <summary>Own units marked VIP (side-level flag feeding WTA priority, row 13).</summary>
        internal readonly HashSet<uint> Vips = new HashSet<uint>();
        /// <summary>Claims per target (shooters assigned by this side's orders and behaviours).</summary>
        internal readonly Dictionary<uint, int> Claims = new Dictionary<uint, int>();

        public bool AnyIdentified { get; internal set; }
        public double FirstIdentifiedUt { get; internal set; } = double.NaN;
        public uint FirstIdentified { get; internal set; }
        internal bool AdvanceAnnounced;
        internal bool FirstLaunchSeen;
        internal bool Managed;
        /// <summary>Recomputed each tick: managed, or has units under orders, holds or non-default doctrine.</summary>
        internal bool Active;
        /// <summary>Position in the engine's side list (hostility matrix index).</summary>
        internal int Index;

        public SidePicture(string side) { Side = side; }

        public bool IsInView(uint id) { return InView.Contains(id); }

        public bool IsIdentified(uint id) { return Identified.Contains(id); }

        /// <summary>Held by exactly one own unit's own sensors, and not by any surveillance platform (row 7).</summary>
        public bool IsOwnSensorOnly(uint id)
        {
            int n;
            return Holders.TryGetValue(id, out n) && n == 1 && !SurveillanceHeld.Contains(id);
        }

        public int HolderCount(uint id)
        {
            int n;
            return Holders.TryGetValue(id, out n) ? n : 0;
        }

        public bool IsVip(uint id) { return Vips.Contains(id); }

        public int ClaimCount(uint id)
        {
            int n;
            return Claims.TryGetValue(id, out n) ? n : 0;
        }

        internal void AddClaim(uint target)
        {
            if (target == 0) return;
            int n;
            Claims.TryGetValue(target, out n);
            Claims[target] = n + 1;
        }

        internal void RemoveClaim(uint target)
        {
            if (target == 0) return;
            int n;
            if (!Claims.TryGetValue(target, out n)) return;
            if (n <= 1) Claims.Remove(target);
            else Claims[target] = n - 1;
        }

        internal double SinceInView(uint id, double now)
        {
            double t;
            return LastInView.TryGetValue(id, out t) ? now - t : double.PositiveInfinity;
        }
    }
}
