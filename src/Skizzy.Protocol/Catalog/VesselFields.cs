using System.Collections.Generic;

namespace Skizzy.Protocol.Catalog
{
    public enum FieldKind : byte { Null, Bool, Number, Single, Integer, String }

    /// <summary>Description of a vessel-level field readable by ksp.query, streams and log v0.</summary>
    public sealed class VesselFieldInfo
    {
        public readonly string Name;
        public readonly FieldKind Kind;
        public readonly string Unit;
        public readonly string Description;
        public readonly bool LoadedOnly;

        public VesselFieldInfo(string name, FieldKind kind, string unit, string description, bool loadedOnly = false)
        {
            Name = name; Kind = kind; Unit = unit; Description = description; LoadedOnly = loadedOnly;
        }
    }

    /// <summary>
    /// The vessel field table. Names are stable protocol identifiers; KSP and FakeKsp both implement every one
    /// (the host's self-check and a CI test verify that). Module fields (Tier 0) are resolved separately.
    /// </summary>
    public static class VesselFields
    {
        public static readonly VesselFieldInfo[] All =
        {
            new VesselFieldInfo("name", FieldKind.String, null, "vessel name"),
            new VesselFieldInfo("handle", FieldKind.String, null, "vessel handle v:<persistentId>"),
            new VesselFieldInfo("type", FieldKind.String, null, "KSP vessel type (Ship, Plane, Probe, Debris, ...)"),
            new VesselFieldInfo("situation", FieldKind.String, null, "LANDED, SPLASHED, PRELAUNCH, FLYING, SUB_ORBITAL, ORBITING, ESCAPING, DOCKED"),
            new VesselFieldInfo("body", FieldKind.String, null, "reference body name"),
            new VesselFieldInfo("lat", FieldKind.Number, "deg", "latitude"),
            new VesselFieldInfo("lon", FieldKind.Number, "deg", "longitude"),
            new VesselFieldInfo("altitude", FieldKind.Number, "m", "altitude above sea level (negative when submerged)"),
            new VesselFieldInfo("heightFromTerrain", FieldKind.Number, "m", "height above terrain or sea floor", true),
            new VesselFieldInfo("srfSpeed", FieldKind.Number, "m/s", "surface speed"),
            new VesselFieldInfo("obtSpeed", FieldKind.Number, "m/s", "orbital speed"),
            new VesselFieldInfo("verticalSpeed", FieldKind.Number, "m/s", "vertical speed, positive up"),
            new VesselFieldInfo("horizontalSrfSpeed", FieldKind.Number, "m/s", "horizontal surface speed"),
            new VesselFieldInfo("heading", FieldKind.Number, "deg", "true heading of the vessel's nose", true),
            new VesselFieldInfo("pitch", FieldKind.Number, "deg", "pitch above the horizon", true),
            new VesselFieldInfo("roll", FieldKind.Number, "deg", "roll, positive right wing down", true),
            new VesselFieldInfo("course", FieldKind.Number, "deg", "direction of the surface velocity (true)", true),
            new VesselFieldInfo("mach", FieldKind.Number, null, "Mach number", true),
            new VesselFieldInfo("geeForce", FieldKind.Number, "g", "current g-force", true),
            new VesselFieldInfo("dynamicPressurekPa", FieldKind.Number, "kPa", "dynamic pressure", true),
            new VesselFieldInfo("staticPressurekPa", FieldKind.Number, "kPa", "static pressure", true),
            new VesselFieldInfo("atmDensity", FieldKind.Number, "kg/m3", "atmospheric density", true),
            new VesselFieldInfo("externalTemperature", FieldKind.Number, "K", "outside temperature", true),
            new VesselFieldInfo("mass", FieldKind.Number, "t", "total mass", true),
            new VesselFieldInfo("missionTime", FieldKind.Number, "s", "mission elapsed time"),
            new VesselFieldInfo("partCount", FieldKind.Integer, null, "number of parts"),
            new VesselFieldInfo("crewCount", FieldKind.Integer, null, "number of crew"),
            new VesselFieldInfo("currentStage", FieldKind.Integer, null, "current stage index", true),
            new VesselFieldInfo("loaded", FieldKind.Bool, null, "within load range (has parts)"),
            new VesselFieldInfo("packed", FieldKind.Bool, null, "on rails (no physics)"),
            new VesselFieldInfo("landed", FieldKind.Bool, null, "touching ground"),
            new VesselFieldInfo("splashed", FieldKind.Bool, null, "in water"),
            new VesselFieldInfo("isActive", FieldKind.Bool, null, "is the active (player-flown) vessel"),
            new VesselFieldInfo("controllable", FieldKind.Bool, null, "IsControllable (CommNet/crew/probe core)", true),
            new VesselFieldInfo("ctrlState.mainThrottle", FieldKind.Single, "0..1", "throttle setting", true),
            new VesselFieldInfo("ctrlState.pitch", FieldKind.Single, "-1..1", "pitch input", true),
            new VesselFieldInfo("ctrlState.yaw", FieldKind.Single, "-1..1", "yaw input", true),
            new VesselFieldInfo("ctrlState.roll", FieldKind.Single, "-1..1", "roll input", true),
            new VesselFieldInfo("ctrlState.wheelThrottle", FieldKind.Single, "-1..1", "wheel throttle", true),
            new VesselFieldInfo("ctrlState.wheelSteer", FieldKind.Single, "-1..1", "wheel steering", true),
            new VesselFieldInfo("actionGroups.sas", FieldKind.Bool, null, "SAS action group state", true),
            new VesselFieldInfo("actionGroups.rcs", FieldKind.Bool, null, "RCS action group state", true),
            new VesselFieldInfo("actionGroups.gear", FieldKind.Bool, null, "gear action group state", true),
            new VesselFieldInfo("actionGroups.brakes", FieldKind.Bool, null, "brakes action group state", true),
            new VesselFieldInfo("actionGroups.lights", FieldKind.Bool, null, "lights action group state", true),
        };

        /// <summary>Accepted aliases for convenience (scenario §0 uses "throttle").</summary>
        public static readonly Dictionary<string, string> Aliases = new Dictionary<string, string>
        {
            { "throttle", "ctrlState.mainThrottle" },
            { "alt", "altitude" },
            { "latitude", "lat" },
            { "longitude", "lon" },
            { "speed", "srfSpeed" },
            { "vesselName", "name" },
        };

        private static readonly Dictionary<string, VesselFieldInfo> _map = BuildMap();

        private static Dictionary<string, VesselFieldInfo> BuildMap()
        {
            var m = new Dictionary<string, VesselFieldInfo>();
            foreach (var f in All) m[f.Name] = f;
            return m;
        }

        public static VesselFieldInfo Find(string name)
        {
            if (name == null) return null;
            if (name.StartsWith("vessel.", System.StringComparison.Ordinal)) name = name.Substring(7);
            if (Aliases.TryGetValue(name, out var canon)) name = canon;
            return _map.TryGetValue(name, out var fi) ? fi : null;
        }

        /// <summary>Index into <see cref="All"/> after prefix and alias resolution, or -1.</summary>
        public static int IndexOf(string name)
        {
            var f = Find(name);
            return f == null ? -1 : System.Array.IndexOf(All, f);
        }

        public static string Canonical(string name)
        {
            var f = Find(name);
            return f != null ? f.Name : name;
        }
    }
}
