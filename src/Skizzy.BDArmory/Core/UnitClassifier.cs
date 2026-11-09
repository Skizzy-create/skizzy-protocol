using System;
using Skizzy.C2;

namespace Skizzy.Bda
{
    /// <summary>What the game layer measures on a vessel for classification (computed rarely, cached per vessel).</summary>
    public struct VesselFeatures
    {
        public string Name;
        public bool PilotAi;
        public bool VtolAi;
        public bool SurfaceAi;
        public bool OrbitalAi;
        /// <summary>Surface AI movement type Submarine.</summary>
        public bool Submarine;
        /// <summary>Surface AI movement type Stationary.</summary>
        public bool Stationary;
        /// <summary>Surface AI movement type Land.</summary>
        public bool LandVehicle;
        public bool Crewed;
        public double MassT;
        /// <summary>Guns or offensive missiles.</summary>
        public bool Armed;
        public bool HasRadar;
        /// <summary>Catapults, arrestor wires, hangars, elevators (aircraft-carrier parts).</summary>
        public int CarrierParts;
        public bool Landed;
        public bool Splashed;
    }

    public struct Classification
    {
        public UnitClass Class;
        public bool IsBase;
    }

    /// <summary>
    /// Vessel classification for the C2 engine. A bracketed tag in the vessel name wins ("[CV] Kestrel", "Eye [AEW]");
    /// otherwise the BDA AI type and the parts decide (docs/M4-v1-checklist.md lists the tags).
    /// </summary>
    public static class UnitClassifier
    {
        /// <summary>Unmanned aircraft lighter than this are drones (BDA fighters often fly probe cores).</summary>
        public const double DroneMaxMassT = 2.0;

        public static Classification Classify(VesselFeatures f)
        {
            var tagged = FromTag(f.Name);
            if (tagged != UnitClass.Unknown)
                return new Classification { Class = tagged, IsBase = tagged == UnitClass.Carrier || tagged == UnitClass.Airfield };

            if (f.PilotAi || f.VtolAi)
            {
                if (!f.Crewed && f.MassT > 0 && f.MassT < DroneMaxMassT) return C(UnitClass.Drone);
                if (!f.Armed && f.HasRadar) return C(UnitClass.Aew);
                return C(UnitClass.Fighter);
            }
            if (f.SurfaceAi)
            {
                if (f.Submarine) return C(UnitClass.Submarine);
                if (f.Stationary) return f.CarrierParts > 0 ? Base(UnitClass.Airfield) : C(UnitClass.Ground);
                if (f.LandVehicle) return C(UnitClass.Ground);
                if (f.CarrierParts > 0 || NameHas(f.Name, "carrier")) return Base(UnitClass.Carrier);
                return C(UnitClass.Ship);
            }
            if (f.OrbitalAi) return C(UnitClass.Unknown);
            // weapon manager only
            if (f.Splashed) return f.CarrierParts > 0 ? Base(UnitClass.Carrier) : C(UnitClass.Ship);
            if (f.Landed) return f.CarrierParts > 0 ? Base(UnitClass.Airfield) : C(UnitClass.Ground);
            return C(UnitClass.Unknown);
        }

        private static Classification C(UnitClass c) { return new Classification { Class = c }; }
        private static Classification Base(UnitClass c) { return new Classification { Class = c, IsBase = true }; }

        private static bool NameHas(string name, string word)
        {
            return name != null && name.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>The class named by the first recognised bracketed tag in <paramref name="name"/>, or Unknown.</summary>
        public static UnitClass FromTag(string name)
        {
            if (string.IsNullOrEmpty(name)) return UnitClass.Unknown;
            int i = 0;
            while ((i = name.IndexOf('[', i)) >= 0)
            {
                int j = name.IndexOf(']', i + 1);
                if (j < 0) break;
                var c = TagClass(name, i + 1, j - i - 1);
                if (c != UnitClass.Unknown) return c;
                i = j + 1;
            }
            return UnitClass.Unknown;
        }

        private static UnitClass TagClass(string s, int start, int len)
        {
            switch (len)
            {
                case 2:
                    if (Is(s, start, "CV")) return UnitClass.Carrier;
                    if (Is(s, start, "AF")) return UnitClass.Airfield;
                    if (Is(s, start, "SS")) return UnitClass.Submarine;
                    if (Is(s, start, "DD") || Is(s, start, "FF") || Is(s, start, "CG")) return UnitClass.Ship;
                    break;
                case 3:
                    if (Is(s, start, "CVN")) return UnitClass.Carrier;
                    if (Is(s, start, "AEW")) return UnitClass.Aew;
                    if (Is(s, start, "UAV")) return UnitClass.Drone;
                    if (Is(s, start, "SSN") || Is(s, start, "SUB")) return UnitClass.Submarine;
                    if (Is(s, start, "FTR")) return UnitClass.Fighter;
                    if (Is(s, start, "BMR")) return UnitClass.Bomber;
                    if (Is(s, start, "GND")) return UnitClass.Ground;
                    break;
                case 4:
                    if (Is(s, start, "UCAV")) return UnitClass.Drone;
                    if (Is(s, start, "SHIP")) return UnitClass.Ship;
                    if (Is(s, start, "BASE")) return UnitClass.Airfield;
                    break;
                case 5:
                    if (Is(s, start, "AWACS")) return UnitClass.Aew;
                    if (Is(s, start, "DRONE")) return UnitClass.Drone;
                    break;
                case 6:
                    if (Is(s, start, "BOMBER")) return UnitClass.Bomber;
                    break;
                case 7:
                    if (Is(s, start, "CARRIER")) return UnitClass.Carrier;
                    if (Is(s, start, "FIGHTER")) return UnitClass.Fighter;
                    break;
                case 8:
                    if (Is(s, start, "AIRFIELD")) return UnitClass.Airfield;
                    break;
            }
            return UnitClass.Unknown;
        }

        private static bool Is(string s, int start, string tag)
        {
            return string.Compare(s, start, tag, 0, tag.Length, StringComparison.OrdinalIgnoreCase) == 0;
        }

        /// <summary>
        /// The callsign group of a vessel name: "Cobra 1" → "Cobra", "Cobra 1-2", "Cobra-3", "Cobra_4", "[UAV] Cobra 2"
        /// → "Cobra". Null when the name does not end in a number (a unique vessel such as "Kestrel").
        /// </summary>
        public static string CallsignGroup(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            // drop bracketed tags
            string s = name;
            if (s.IndexOf('[') >= 0)
            {
                var sb = new System.Text.StringBuilder(s.Length);
                int depth = 0;
                foreach (char ch in s)
                {
                    if (ch == '[') { depth++; continue; }
                    if (ch == ']') { if (depth > 0) depth--; continue; }
                    if (depth == 0) sb.Append(ch);
                }
                s = sb.ToString();
            }
            int end = s.Length;
            while (end > 0 && char.IsWhiteSpace(s[end - 1])) end--;
            int k = end;
            bool digits = false;
            // trailing number groups: "1", "1-2", "1_2"
            while (k > 0)
            {
                int d = k;
                while (d > 0 && char.IsDigit(s[d - 1])) d--;
                if (d == k) break;
                digits = true;
                k = d;
                if (k > 0 && (s[k - 1] == '-' || s[k - 1] == '_') && k - 1 > 0 && char.IsDigit(s[k - 2])) { k--; continue; }
                break;
            }
            if (!digits) return null;
            while (k > 0 && (char.IsWhiteSpace(s[k - 1]) || s[k - 1] == '-' || s[k - 1] == '_' || s[k - 1] == '#')) k--;
            int b = 0;
            while (b < k && char.IsWhiteSpace(s[b])) b++;
            if (k <= b) return null;
            return s.Substring(b, k - b);
        }
    }
}
