using System.Collections.Generic;

namespace Skizzy.Protocol.Catalog
{
    /// <summary>
    /// Part-level fields for ksp.query/log v0 with aggregate perPart (or "part.&lt;field&gt;" on module rows).
    /// Resources are read as "resource.&lt;Name&gt;" (amount) and "resource.&lt;Name&gt;.max".
    /// </summary>
    public static class PartFields
    {
        public static readonly VesselFieldInfo[] All =
        {
            new VesselFieldInfo("name", FieldKind.String, null, "part config name"),
            new VesselFieldInfo("title", FieldKind.String, null, "part title"),
            new VesselFieldInfo("handle", FieldKind.String, null, "part handle p:<persistentId>"),
            new VesselFieldInfo("mass", FieldKind.Number, "t", "part mass including resources"),
            new VesselFieldInfo("temperature", FieldKind.Number, "K", "internal temperature"),
            new VesselFieldInfo("maxTemp", FieldKind.Number, "K", "maximum internal temperature"),
            new VesselFieldInfo("skinTemperature", FieldKind.Number, "K", "skin temperature"),
            new VesselFieldInfo("crewCount", FieldKind.Integer, null, "crew in this part"),
            new VesselFieldInfo("inverseStage", FieldKind.Integer, null, "stage in which the part activates"),
        };

        public const string ResourcePrefix = "resource.";

        private static readonly Dictionary<string, int> _map = BuildMap();

        private static Dictionary<string, int> BuildMap()
        {
            var m = new Dictionary<string, int>();
            for (int i = 0; i < All.Length; i++) m[All[i].Name] = i;
            return m;
        }

        public static int IndexOf(string name)
        {
            if (name == null) return -1;
            if (name.StartsWith("part.", System.StringComparison.Ordinal)) name = name.Substring(5);
            return _map.TryGetValue(name, out int idx) ? idx : -1;
        }

        /// <summary>Parses "resource.LiquidFuel" / "resource.LiquidFuel.max" (with optional "part." prefix).</summary>
        public static bool TryParseResource(string name, out string resource, out bool max)
        {
            resource = null;
            max = false;
            if (name == null) return false;
            if (name.StartsWith("part.", System.StringComparison.Ordinal)) name = name.Substring(5);
            if (!name.StartsWith(ResourcePrefix, System.StringComparison.Ordinal)) return false;
            string r = name.Substring(ResourcePrefix.Length);
            if (r.EndsWith(".max", System.StringComparison.Ordinal)) { max = true; r = r.Substring(0, r.Length - 4); }
            if (r.Length == 0) return false;
            resource = r;
            return true;
        }
    }
}
