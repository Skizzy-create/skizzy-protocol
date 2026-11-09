using System;
using System.Collections.Generic;
using System.Globalization;

namespace Skizzy.C2
{
    /// <summary>Weapons control per unit (row 8): Free = guard, Tight = assigned/identified only, Hold = passive.</summary>
    public enum WeaponsControl
    {
        Free = 0,
        Tight = 1,
        Hold = 2,
    }

    /// <summary>What a unit does when a new hostile appears during an order (rows 5 and 6).</summary>
    public enum OnContact
    {
        /// <summary>Stay on task: crossing hostiles are ignored (only self-defence interrupts).</summary>
        Task = 0,
        /// <summary>Engage on contact: divert to a hostile the unit's own radar holds, then continue the order.</summary>
        Engage = 1,
    }

    /// <summary>Row 12: fighters and drones engage missiles.</summary>
    public enum AirEngageMissiles
    {
        WhenNoAir = 0,
        Always = 1,
        Never = 2,
    }

    public enum Posture
    {
        Defensive = 0,
        Proactive = 1,
    }

    /// <summary>What a unit does when it runs out of offensive missiles during an attack order.</summary>
    public enum WinchesterAction
    {
        Rtb = 0,
        GunsOnly = 1,
        Continue = 2,
    }

    public enum DoctrineLevel
    {
        Side = 0,
        Group = 1,
        Unit = 2,
    }

    /// <summary>
    /// The flat effective doctrine of one unit (09 §11.1). Defaults are the mockup's proven behaviour constants
    /// (24 §2) and otherwise reproduce plain BDArmory (weapons free, engage on BDA guard).
    /// </summary>
    public sealed class DoctrineValues
    {
        public WeaponsControl WeaponsControl = WeaponsControl.Free;
        public OnContact OnContact = OnContact.Task;
        public bool AdvanceOnContact = true;
        public AirEngageMissiles AirEngageMissiles = AirEngageMissiles.WhenNoAir;
        public Posture Posture = Posture.Defensive;
        public WeaponsControl RtbWeapons = WeaponsControl.Tight;

        public double PatrolLeashKm = 55;
        public double SelfDefenceFighterKm = 25;
        public double SelfDefenceOverrideS = 15;
        /// <summary>Self-defence F1 is sticky for its duration, so the AI turns into a threat still outside its missile envelope.</summary>
        public bool SelfDefenceSticky = true;

        public double ClassRangeFighterKm = 45, ClassRangeDroneKm = 25, ClassRangeAewKm = 175, ClassRangeShipKm = 150, ClassRangeBomberKm = 45;
        public double OwnRadarFighterKm = 45, OwnRadarDroneKm = 25, OwnRadarBomberKm = 45;

        public double AdvanceRepickS = 2;
        public double ContactRepickS = 0.5;
        public double RefeedKm = 2;

        public double MarshalKm = 8, MarshalBaseAltM = 1200, MarshalStepM = 300, MarshalOrbitKm = 3.5;
        public double LandGapS = 5, DeckTurnaroundS = 90;

        public double VipRadiusKm = 40, ProtectOrbitKm = 7, VipReachFighterKm = 120, VipReachDroneKm = 60;

        public double HuntRepickS = 1, HuntIdleEndS = 20, HuntMissileRatio = 0.6, HuntAirReachKm = 390, HuntMissileReachKm = 120;
        public int HuntMaxClaimsPerAircraft = 2;
        public bool HuntSticky = true;

        public double WinchesterAmmoPct = 0;
        public WinchesterAction WinchesterFighter = WinchesterAction.Rtb, WinchesterDrone = WinchesterAction.GunsOnly, WinchesterBomber = WinchesterAction.Rtb;

        public double ArrivalAirKm = 5, ArrivalSurfaceKm = 0.6, RouteGateAirKm = 3, RouteGateSurfaceKm = 0.8;
        public double FormationSpacingAirKm = 2.2, FormationSpacingSurfaceKm = 1.5, StationOrbitKm = 2.5;

        public double EscortAheadKm = 14, EscortLeashKm = 30;

        public double RacetrackWidthKm = 6, StackStepM = 300, PatternAltFighterM = 4500, PatternAltDroneM = 1200, PatternStepDroneM = 150, PatternAltAewM = 8500;

        public double AirEngageMissilesViewKm = 75;
        public double TargetGroupRadiusKm = 15;
        public bool PauseOnFirstLaunch = true;

        public DoctrineValues Clone() { return (DoctrineValues)MemberwiseClone(); }

        public double ClassificationRangeM(UnitClass c)
        {
            switch (c)
            {
                case UnitClass.Fighter: return ClassRangeFighterKm * 1000;
                case UnitClass.Drone: return ClassRangeDroneKm * 1000;
                case UnitClass.Aew: return ClassRangeAewKm * 1000;
                case UnitClass.Bomber: return ClassRangeBomberKm * 1000;
                case UnitClass.Ship:
                case UnitClass.Carrier:
                case UnitClass.Airfield:
                    return ClassRangeShipKm * 1000;
                default: return 0;
            }
        }

        /// <summary>Own-radar reach used for engage-on-contact (row 6); the real value is also capped by the craft's radar.</summary>
        public double OwnRadarM(UnitClass c)
        {
            switch (c)
            {
                case UnitClass.Fighter: return OwnRadarFighterKm * 1000;
                case UnitClass.Drone: return OwnRadarDroneKm * 1000;
                case UnitClass.Bomber: return OwnRadarBomberKm * 1000;
                default: return 0;
            }
        }

        public WinchesterAction WinchesterFor(UnitClass c)
        {
            switch (c)
            {
                case UnitClass.Drone: return WinchesterDrone;
                case UnitClass.Bomber: return WinchesterBomber;
                default: return WinchesterFighter;
            }
        }

        public double PatternBaseAltM(UnitClass c)
        {
            switch (c)
            {
                case UnitClass.Drone: return PatternAltDroneM;
                case UnitClass.Aew: return PatternAltAewM;
                default: return PatternAltFighterM;
            }
        }

        public double PatternStepM(UnitClass c) { return c == UnitClass.Drone ? PatternStepDroneM : StackStepM; }
    }

    /// <summary>One doctrine key: its name, kind and accessors on <see cref="DoctrineValues"/>.</summary>
    public sealed class DoctrineKey
    {
        public readonly string Name;
        public readonly string Kind; // number | integer | boolean | enum
        public readonly string[] Options;
        internal readonly Func<DoctrineValues, object> Get;
        internal readonly Action<DoctrineValues, object> Set;

        internal DoctrineKey(string name, string kind, string[] options, Func<DoctrineValues, object> get, Action<DoctrineValues, object> set)
        {
            Name = name;
            Kind = kind;
            Options = options;
            Get = get;
            Set = set;
        }
    }

    /// <summary>The doctrine keys and their parsing. Values are normalised at set time; executors read the flat cache.</summary>
    public static class DoctrineKeys
    {
        private static readonly Dictionary<string, DoctrineKey> ByName = new Dictionary<string, DoctrineKey>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<DoctrineKey> Ordered = new List<DoctrineKey>();

        public static IList<DoctrineKey> All => Ordered;

        static DoctrineKeys()
        {
            Enum("weaponsControl", new[] { "free", "tight", "hold" }, d => (int)d.WeaponsControl, (d, v) => d.WeaponsControl = (WeaponsControl)v);
            Enum("onContact", new[] { "task", "engage" }, d => (int)d.OnContact, (d, v) => d.OnContact = (OnContact)v);
            Bool("advanceOnContact", d => d.AdvanceOnContact, (d, v) => d.AdvanceOnContact = v);
            Enum("airEngageMissiles", new[] { "when_no_air", "always", "never" }, d => (int)d.AirEngageMissiles, (d, v) => d.AirEngageMissiles = (AirEngageMissiles)v);
            Enum("posture", new[] { "defensive", "proactive" }, d => (int)d.Posture, (d, v) => d.Posture = (Posture)v);
            Enum("rtbWeapons", new[] { "free", "tight", "hold" }, d => (int)d.RtbWeapons, (d, v) => d.RtbWeapons = (WeaponsControl)v);
            Num("patrolLeashKm", d => d.PatrolLeashKm, (d, v) => d.PatrolLeashKm = v);
            Num("selfDefenceFighterKm", d => d.SelfDefenceFighterKm, (d, v) => d.SelfDefenceFighterKm = v);
            Num("selfDefenceOverrideS", d => d.SelfDefenceOverrideS, (d, v) => d.SelfDefenceOverrideS = v);
            Bool("selfDefenceSticky", d => d.SelfDefenceSticky, (d, v) => d.SelfDefenceSticky = v);
            Num("classificationRangeKm.fighter", d => d.ClassRangeFighterKm, (d, v) => d.ClassRangeFighterKm = v);
            Num("classificationRangeKm.drone", d => d.ClassRangeDroneKm, (d, v) => d.ClassRangeDroneKm = v);
            Num("classificationRangeKm.aew", d => d.ClassRangeAewKm, (d, v) => d.ClassRangeAewKm = v);
            Num("classificationRangeKm.ship", d => d.ClassRangeShipKm, (d, v) => d.ClassRangeShipKm = v);
            Num("classificationRangeKm.bomber", d => d.ClassRangeBomberKm, (d, v) => d.ClassRangeBomberKm = v);
            Num("ownRadarKm.fighter", d => d.OwnRadarFighterKm, (d, v) => d.OwnRadarFighterKm = v);
            Num("ownRadarKm.drone", d => d.OwnRadarDroneKm, (d, v) => d.OwnRadarDroneKm = v);
            Num("ownRadarKm.bomber", d => d.OwnRadarBomberKm, (d, v) => d.OwnRadarBomberKm = v);
            Num("advanceRepickS", d => d.AdvanceRepickS, (d, v) => d.AdvanceRepickS = v);
            Num("contactRepickS", d => d.ContactRepickS, (d, v) => d.ContactRepickS = v);
            Num("refeedKm", d => d.RefeedKm, (d, v) => d.RefeedKm = v);
            Num("marshalKm", d => d.MarshalKm, (d, v) => d.MarshalKm = v);
            Num("marshalBaseAltM", d => d.MarshalBaseAltM, (d, v) => d.MarshalBaseAltM = v);
            Num("marshalStepM", d => d.MarshalStepM, (d, v) => d.MarshalStepM = v);
            Num("marshalOrbitKm", d => d.MarshalOrbitKm, (d, v) => d.MarshalOrbitKm = v);
            Num("landGapS", d => d.LandGapS, (d, v) => d.LandGapS = v);
            Num("deckTurnaroundS", d => d.DeckTurnaroundS, (d, v) => d.DeckTurnaroundS = v);
            Num("vipRadiusKm", d => d.VipRadiusKm, (d, v) => d.VipRadiusKm = v);
            Num("protectOrbitKm", d => d.ProtectOrbitKm, (d, v) => d.ProtectOrbitKm = v);
            Num("vipReachKm.fighter", d => d.VipReachFighterKm, (d, v) => d.VipReachFighterKm = v);
            Num("vipReachKm.drone", d => d.VipReachDroneKm, (d, v) => d.VipReachDroneKm = v);
            Num("huntRepickS", d => d.HuntRepickS, (d, v) => d.HuntRepickS = v);
            Num("huntIdleEndS", d => d.HuntIdleEndS, (d, v) => d.HuntIdleEndS = v);
            Num("huntMissileRatio", d => d.HuntMissileRatio, (d, v) => d.HuntMissileRatio = v);
            Num("huntAirReachKm", d => d.HuntAirReachKm, (d, v) => d.HuntAirReachKm = v);
            Num("huntMissileReachKm", d => d.HuntMissileReachKm, (d, v) => d.HuntMissileReachKm = v);
            Int("huntMaxClaimsPerAircraft", d => d.HuntMaxClaimsPerAircraft, (d, v) => d.HuntMaxClaimsPerAircraft = v);
            Bool("huntSticky", d => d.HuntSticky, (d, v) => d.HuntSticky = v);
            Num("winchesterAmmoPct", d => d.WinchesterAmmoPct, (d, v) => d.WinchesterAmmoPct = v);
            var wa = new[] { "rtb", "guns_only", "continue" };
            Enum("winchesterAction.fighter", wa, d => (int)d.WinchesterFighter, (d, v) => d.WinchesterFighter = (WinchesterAction)v);
            Enum("winchesterAction.drone", wa, d => (int)d.WinchesterDrone, (d, v) => d.WinchesterDrone = (WinchesterAction)v);
            Enum("winchesterAction.bomber", wa, d => (int)d.WinchesterBomber, (d, v) => d.WinchesterBomber = (WinchesterAction)v);
            Num("arrivalAirKm", d => d.ArrivalAirKm, (d, v) => d.ArrivalAirKm = v);
            Num("arrivalSurfaceKm", d => d.ArrivalSurfaceKm, (d, v) => d.ArrivalSurfaceKm = v);
            Num("routeGateAirKm", d => d.RouteGateAirKm, (d, v) => d.RouteGateAirKm = v);
            Num("routeGateSurfaceKm", d => d.RouteGateSurfaceKm, (d, v) => d.RouteGateSurfaceKm = v);
            Num("formationSpacingAirKm", d => d.FormationSpacingAirKm, (d, v) => d.FormationSpacingAirKm = v);
            Num("formationSpacingSurfaceKm", d => d.FormationSpacingSurfaceKm, (d, v) => d.FormationSpacingSurfaceKm = v);
            Num("stationOrbitKm", d => d.StationOrbitKm, (d, v) => d.StationOrbitKm = v);
            Num("escortAheadKm", d => d.EscortAheadKm, (d, v) => d.EscortAheadKm = v);
            Num("escortLeashKm", d => d.EscortLeashKm, (d, v) => d.EscortLeashKm = v);
            Num("racetrackWidthKm", d => d.RacetrackWidthKm, (d, v) => d.RacetrackWidthKm = v);
            Num("stackStepM", d => d.StackStepM, (d, v) => d.StackStepM = v);
            Num("patternAltM.fighter", d => d.PatternAltFighterM, (d, v) => d.PatternAltFighterM = v);
            Num("patternAltM.drone", d => d.PatternAltDroneM, (d, v) => d.PatternAltDroneM = v);
            Num("patternStepM.drone", d => d.PatternStepDroneM, (d, v) => d.PatternStepDroneM = v);
            Num("patternAltM.aew", d => d.PatternAltAewM, (d, v) => d.PatternAltAewM = v);
            Num("airEngageMissilesViewKm", d => d.AirEngageMissilesViewKm, (d, v) => d.AirEngageMissilesViewKm = v);
            Num("targetGroupRadiusKm", d => d.TargetGroupRadiusKm, (d, v) => d.TargetGroupRadiusKm = v);
            Bool("pauseOnFirstLaunch", d => d.PauseOnFirstLaunch, (d, v) => d.PauseOnFirstLaunch = v);
        }

        private static void Add(DoctrineKey k) { ByName[k.Name] = k; Ordered.Add(k); }

        private static void Num(string name, Func<DoctrineValues, double> get, Action<DoctrineValues, double> set)
        {
            Add(new DoctrineKey(name, "number", null, d => get(d), (d, v) => set(d, (double)v)));
        }

        private static void Int(string name, Func<DoctrineValues, int> get, Action<DoctrineValues, int> set)
        {
            Add(new DoctrineKey(name, "integer", null, d => get(d), (d, v) => set(d, (int)v)));
        }

        private static void Bool(string name, Func<DoctrineValues, bool> get, Action<DoctrineValues, bool> set)
        {
            Add(new DoctrineKey(name, "boolean", null, d => get(d), (d, v) => set(d, (bool)v)));
        }

        private static void Enum(string name, string[] options, Func<DoctrineValues, int> get, Action<DoctrineValues, int> set)
        {
            Add(new DoctrineKey(name, "enum", options, d => get(d), (d, v) => set(d, (int)v)));
        }

        public static DoctrineKey Find(string name)
        {
            DoctrineKey k;
            return name != null && ByName.TryGetValue(name, out k) ? k : null;
        }

        /// <summary>
        /// Normalises <paramref name="raw"/> for <paramref name="key"/>: numbers for number/integer keys, booleans
        /// (also "on"/"off"), and enum option names (case-insensitive; "-" and "_" ignored) or enum values. Null on error.
        /// </summary>
        public static object Normalise(DoctrineKey key, object raw, out string error)
        {
            error = null;
            switch (key.Kind)
            {
                case "number":
                {
                    double d;
                    if (TryNumber(raw, out d) && !double.IsNaN(d) && !double.IsInfinity(d) && d >= 0) return d;
                    error = key.Name + " needs a number >= 0";
                    return null;
                }
                case "integer":
                {
                    double d;
                    if (TryNumber(raw, out d) && d >= 0 && d <= 1000 && Math.Floor(d) == d) return (int)d;
                    error = key.Name + " needs a whole number from 0 to 1000";
                    return null;
                }
                case "boolean":
                {
                    if (raw is bool b) return b;
                    var s = raw as string;
                    if (s != null)
                    {
                        s = s.Trim().ToLowerInvariant();
                        if (s == "on" || s == "true" || s == "yes") return true;
                        if (s == "off" || s == "false" || s == "no") return false;
                    }
                    error = key.Name + " needs true/false (or on/off)";
                    return null;
                }
                default:
                {
                    if (raw is string str)
                    {
                        string n = Squash(str);
                        for (int i = 0; i < key.Options.Length; i++)
                            if (Squash(key.Options[i]) == n) return i;
                    }
                    else if (raw != null && !(raw is bool))
                    {
                        double d;
                        if (TryNumber(raw, out d) && d >= 0 && d < key.Options.Length && Math.Floor(d) == d) return (int)d;
                    }
                    error = key.Name + " must be one of " + string.Join(", ", key.Options);
                    return null;
                }
            }
        }

        public static string Format(DoctrineKey key, object normalised)
        {
            if (key.Kind == "enum") return key.Options[(int)normalised];
            if (key.Kind == "boolean") return (bool)normalised ? "true" : "false";
            return Convert.ToDouble(normalised, CultureInfo.InvariantCulture).ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string Squash(string s) { return s.Replace("_", "").Replace("-", "").Replace(" ", "").ToLowerInvariant(); }

        private static bool TryNumber(object raw, out double d)
        {
            d = 0;
            if (raw == null || raw is bool) return false;
            if (raw is string s) return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d);
            if (raw is IConvertible c)
            {
                try { d = c.ToDouble(CultureInfo.InvariantCulture); return true; }
                catch (FormatException) { return false; }
                catch (InvalidCastException) { return false; }
            }
            return false;
        }
    }

    /// <summary>One line of <c>c2.doctrine.effective</c>: the value and the level it came from.</summary>
    public sealed class DoctrineExplain
    {
        public string Key;
        public string Value;
        /// <summary>"default", "side:Blue", "group:cap-a" or "unit:v:123".</summary>
        public string From;
        public bool Locked;
    }

    /// <summary>
    /// Doctrine layers side → group → unit with locks (09 §11.1; missions join in M6). The nearest level that sets
    /// a key wins unless a higher level locked it; setting a locked key at a lower level is refused with
    /// <c>locked_by:&lt;level&gt;</c>. Changes bump <see cref="Version"/>; the engine re-resolves affected units.
    /// </summary>
    public sealed class DoctrineStore
    {
        private sealed class Layer
        {
            public readonly Dictionary<string, object> Values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> Locks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        private readonly DoctrineValues _defaults = new DoctrineValues();
        private readonly Dictionary<string, Layer> _sides = new Dictionary<string, Layer>(StringComparer.Ordinal);
        private readonly Dictionary<string, Layer> _groups = new Dictionary<string, Layer>(StringComparer.Ordinal);
        private readonly Dictionary<uint, Layer> _units = new Dictionary<uint, Layer>();

        public int Version { get; private set; }

        /// <summary>Built-in defaults; editing it changes every unit without its own setting.</summary>
        public DoctrineValues Defaults => _defaults;

        /// <summary>
        /// Sets <paramref name="values"/> at a level. All-or-nothing: on any error nothing changes and the errors
        /// are returned (unknown key, bad value, or <c>locked_by:side|group</c>).
        /// </summary>
        public List<string> Set(DoctrineLevel level, string side, string group, uint unit, IDictionary<string, object> values, IList<string> lockKeys = null)
        {
            var errors = new List<string>();
            var normalised = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (values != null)
            {
                foreach (var kv in values)
                {
                    var key = DoctrineKeys.Find(kv.Key);
                    if (key == null) { errors.Add("unknown doctrine key '" + kv.Key + "'"); continue; }
                    string err;
                    var v = DoctrineKeys.Normalise(key, kv.Value, out err);
                    if (err != null) { errors.Add(err); continue; }
                    string lockedBy = LockedAbove(level, side, group, key.Name);
                    if (lockedBy != null) { errors.Add(key.Name + ": locked_by:" + lockedBy); continue; }
                    normalised[key.Name] = v;
                }
            }
            if (lockKeys != null)
            {
                foreach (var lk in lockKeys)
                {
                    var key = DoctrineKeys.Find(lk);
                    if (key == null) errors.Add("unknown doctrine key '" + lk + "' in lock");
                    else if (level == DoctrineLevel.Unit) errors.Add(key.Name + ": a unit-level setting cannot be locked");
                }
            }
            if (errors.Count > 0) return errors;
            var layer = LayerFor(level, side, group, unit, true);
            if (layer == null) { errors.Add("missing target for level " + level); return errors; }
            foreach (var kv in normalised) layer.Values[kv.Key] = kv.Value;
            if (lockKeys != null) foreach (var lk in lockKeys) layer.Locks.Add(DoctrineKeys.Find(lk).Name);
            Version++;
            return errors;
        }

        /// <summary>Removes keys (and their locks) from a level.</summary>
        public void Unset(DoctrineLevel level, string side, string group, uint unit, IList<string> keys)
        {
            var layer = LayerFor(level, side, group, unit, false);
            if (layer == null || keys == null) return;
            bool changed = false;
            foreach (var k in keys)
            {
                changed |= layer.Values.Remove(k);
                changed |= layer.Locks.Remove(k);
            }
            if (changed) Version++;
        }

        /// <summary>
        /// Removes <paramref name="keys"/> (values and locks) from every side, group and unit layer, so the defaults apply
        /// again (the host's kill switch: fire authority back to plain BDArmory). Returns how many entries were removed.
        /// </summary>
        public int UnsetEverywhere(IList<string> keys)
        {
            if (keys == null) return 0;
            int n = 0;
            foreach (var l in _sides.Values) n += Strip(l, keys);
            foreach (var l in _groups.Values) n += Strip(l, keys);
            foreach (var l in _units.Values) n += Strip(l, keys);
            if (n > 0) Version++;
            return n;
        }

        private static int Strip(Layer l, IList<string> keys)
        {
            int n = 0;
            foreach (var k in keys)
            {
                if (l.Values.Remove(k)) n++;
                if (l.Locks.Remove(k)) n++;
            }
            return n;
        }

        /// <summary>Resolves the effective doctrine for one unit into <paramref name="into"/> (allocation-free apart from boxing at set time).</summary>
        public void Resolve(string side, string group, uint unit, DoctrineValues into)
        {
            CopyDefaults(into);
            Layer s = null, g = null, u = null;
            if (side != null) _sides.TryGetValue(side, out s);
            if (group != null) _groups.TryGetValue(group, out g);
            _units.TryGetValue(unit, out u);
            if (s != null) foreach (var kv in s.Values) DoctrineKeys.Find(kv.Key).Set(into, kv.Value);
            if (g != null)
                foreach (var kv in g.Values)
                    if (s == null || !s.Locks.Contains(kv.Key)) DoctrineKeys.Find(kv.Key).Set(into, kv.Value);
            if (u != null)
                foreach (var kv in u.Values)
                    if ((s == null || !s.Locks.Contains(kv.Key)) && (g == null || !g.Locks.Contains(kv.Key))) DoctrineKeys.Find(kv.Key).Set(into, kv.Value);
        }

        public DoctrineValues Resolve(string side, string group, uint unit)
        {
            var d = new DoctrineValues();
            Resolve(side, group, unit, d);
            return d;
        }

        /// <summary><c>c2.doctrine.effective</c>: every key with its value and origin.</summary>
        public List<DoctrineExplain> Explain(string side, string group, uint unit)
        {
            var eff = Resolve(side, group, unit);
            Layer s = null, g = null, u = null;
            if (side != null) _sides.TryGetValue(side, out s);
            if (group != null) _groups.TryGetValue(group, out g);
            _units.TryGetValue(unit, out u);
            var list = new List<DoctrineExplain>();
            foreach (var key in DoctrineKeys.All)
            {
                string from = "default";
                bool sLock = s != null && s.Locks.Contains(key.Name), gLock = g != null && g.Locks.Contains(key.Name);
                if (s != null && s.Values.ContainsKey(key.Name)) from = "side:" + side;
                if (!sLock && g != null && g.Values.ContainsKey(key.Name)) from = "group:" + group;
                if (!sLock && !gLock && u != null && u.Values.ContainsKey(key.Name)) from = "unit:v:" + unit.ToString(CultureInfo.InvariantCulture);
                list.Add(new DoctrineExplain { Key = key.Name, Value = DoctrineKeys.Format(key, key.Get(eff)), From = from, Locked = sLock || gLock });
            }
            return list;
        }

        private void CopyDefaults(DoctrineValues into)
        {
            foreach (var key in DoctrineKeys.All) key.Set(into, key.Get(_defaults));
        }

        private string LockedAbove(DoctrineLevel level, string side, string group, string key)
        {
            Layer l;
            if (level >= DoctrineLevel.Group && side != null && _sides.TryGetValue(side, out l) && l.Locks.Contains(key)) return "side";
            if (level >= DoctrineLevel.Unit && group != null && _groups.TryGetValue(group, out l) && l.Locks.Contains(key)) return "group";
            return null;
        }

        private Layer LayerFor(DoctrineLevel level, string side, string group, uint unit, bool create)
        {
            Layer l = null;
            switch (level)
            {
                case DoctrineLevel.Side:
                    if (side == null) return null;
                    if (!_sides.TryGetValue(side, out l) && create) _sides[side] = l = new Layer();
                    return l;
                case DoctrineLevel.Group:
                    if (group == null) return null;
                    if (!_groups.TryGetValue(group, out l) && create) _groups[group] = l = new Layer();
                    return l;
                default:
                    if (unit == 0) return null;
                    if (!_units.TryGetValue(unit, out l) && create) _units[unit] = l = new Layer();
                    return l;
            }
        }
    }
}
