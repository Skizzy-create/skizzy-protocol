using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Skizzy.C2.Tactical
{
    public enum TacUnits : byte
    {
        /// <summary>nm, ft, kt (03 §6 default).</summary>
        Naval,
        /// <summary>km, m, kt.</summary>
        Metric,
    }

    /// <summary>A BRAA reference: a unit or a point.</summary>
    public struct TacRef
    {
        public bool Has;
        public GeoPoint Pos;
        public string Name;
        public uint Unit;

        public static TacRef Of(UnitState s)
        {
            return s == null ? default(TacRef) : new TacRef { Has = true, Pos = s.Pos, Name = s.Name, Unit = s.Id };
        }

        public static TacRef At(GeoPoint p, string name) { return new TacRef { Has = true, Pos = p, Name = name }; }
    }

    /// <summary>
    /// The compact LLM-facing text of the tactical picture (03 §6, 08 §6-7): one line per track in a fixed column
    /// order declared by a header, the ≤ 6-line summary and the ranked threat list. Pure functions over a TPE snapshot.
    /// </summary>
    public static class TacticalText
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        public const double FtPerM = 3.28084, KtPerMps = 1.943844, MPerNm = 1852;

        public const string LineGrammar = "T<id> <IFF:FRD|HOS|NEU|UNK> <DOM:AIR|SUR|SUB|LND|MSL> <CLS> B<brg> R<rng> A<alt|D depth> KT<spd> <ASPECT> thr<score> eng:<unit+n(missiles)> q<quality> age<s> [NEW|DR|LOST] [n:<name>]";
        public const string ThreatGrammar = "T<id> <IFF> <CLS> B<brg> R<rng> <ASPECT> thr<score> [t<tcpa>s] to:<unit> eng:<n>";

        public static string IffTok(Iff i)
        {
            switch (i)
            {
                case Iff.Friend: return "FRD";
                case Iff.Hostile: return "HOS";
                case Iff.Neutral: return "NEU";
                case Iff.Pending: return "PND";
                default: return "UNK";
            }
        }

        public static string DomTok(Domain d)
        {
            switch (d)
            {
                case Domain.Air: return "AIR";
                case Domain.Surface: return "SUR";
                case Domain.Subsurface: return "SUB";
                case Domain.Missile: return "MSL";
                default: return "LND";
            }
        }

        public static string ClassTok(UnitClass c)
        {
            switch (c)
            {
                case UnitClass.Fighter: return "FTR";
                case UnitClass.Drone: return "DRN";
                case UnitClass.Bomber: return "BMR";
                case UnitClass.Aew: return "AEW";
                case UnitClass.Carrier: return "CV";
                case UnitClass.Ship: return "SHP";
                case UnitClass.Submarine: return "SUB";
                case UnitClass.Airfield: return "AFB";
                case UnitClass.Missile: return "MSL";
                case UnitClass.Ground: return "GND";
                default: return "?";
            }
        }

        public static string AspectTok(Aspect a)
        {
            switch (a)
            {
                case Aspect.Hot: return "HOT";
                case Aspect.Flank: return "FLANK";
                case Aspect.Beam: return "BEAM";
                case Aspect.Cold: return "COLD";
                default: return "DRAG";
            }
        }

        /// <summary>Names in compact text: spaces become underscores so a line stays space-delimited.</summary>
        public static string Tok(string name)
        {
            if (string.IsNullOrEmpty(name)) return "-";
            return name.IndexOf(' ') < 0 ? name : name.Replace(' ', '_');
        }

        /// <summary>Aspect of a track to a point: the angle between its course and its line of sight to the point.</summary>
        public static Aspect AspectTo(Geo geo, Track t, GeoPoint to)
        {
            if (t.SpeedMps <= 3) return Aspect.Beam;
            double los = geo.Bearing(t.Pos, to);
            double a = Math.Abs(Geo.AngleDiff(t.HeadingDeg, los));
            return a <= 30 ? Aspect.Hot : a <= 70 ? Aspect.Flank : a <= 110 ? Aspect.Beam : a <= 150 ? Aspect.Cold : Aspect.Drag;
        }

        /// <summary>Bearing (true, degrees from the reference) and range (metres) of a track.</summary>
        public static void Braa(Geo geo, GeoPoint from, Track t, out double bearingDeg, out double rangeM)
        {
            bearingDeg = geo.Bearing(from, t.Pos);
            rangeM = geo.Distance(from, t.Pos);
        }

        public static string Range(double m, TacUnits u)
        {
            double v = u == TacUnits.Naval ? m / MPerNm : m / 1000.0;
            return v.ToString(v < 100 ? "0.0" : "0", Inv);
        }

        public static string Alt(double m, TacUnits u)
        {
            double v = u == TacUnits.Naval ? m * FtPerM : m;
            if (v < -0.5) return "D" + (-v).ToString("0", Inv);
            return "A" + Math.Max(0, v).ToString("0", Inv);
        }

        public static string Frac(double v)
        {
            if (double.IsNaN(v) || v <= 0) return ".00";
            if (v >= 0.995) return "1";
            return "." + ((int)Math.Round(v * 100)).ToString("00", Inv);
        }

        public static string UnitsText(TacUnits u) { return u == TacUnits.Naval ? "nm/kt/ft" : "km/kt/m"; }

        public static string Header(string kind, TrackPictureEngine tpe, TeamPicture p, TacRef r, TacUnits u, int rows, int total)
        {
            var sb = new StringBuilder(96);
            sb.Append('#').Append(kind).Append(" seq=").Append(tpe.Seq).Append(" ut=").Append(tpe.Now.ToString("0.0", Inv))
              .Append(" view=").Append(p.Index < 0 ? "god" : Tok(p.Team));
            if (p.Index >= 0 && tpe.Fog) sb.Append(" fog");
            sb.Append(" ref=").Append(r.Has ? Tok(r.Name ?? "point") : "nearest").Append(" frame=BRAA units=").Append(UnitsText(u))
              .Append(" n=").Append(rows);
            if (total > rows) sb.Append('/').Append(total);
            return sb.ToString();
        }

        /// <summary>One track in the 03 §6 grammar. Without a reference the BRAA is taken from the track's threatened unit.</summary>
        public static string Line(TrackPictureEngine tpe, TeamPicture p, Track t, TacRef r, TacUnits u)
        {
            var geo = tpe.Geo;
            double now = tpe.Now;
            var sb = new StringBuilder(96);
            sb.Append('T').Append(t.Number).Append(' ');
            if (p.Index < 0) sb.Append(t.Side != null ? Tok(t.Side.Length > 6 ? t.Side.Substring(0, 6) : t.Side).ToUpperInvariant() : "NONE");
            else sb.Append(IffTok(t.Iff));
            sb.Append(' ').Append(DomTok(t.Domain)).Append(' ').Append(ClassTok(t.Class)).Append(' ');
            var from = r;
            if (!from.Has && t.ThreatTo != 0) from = TacRef.Of(tpe.Unit(t.ThreatTo));
            if (from.Has && (from.Unit == 0 || from.Unit != t.Vessel))
            {
                double b, rng;
                Braa(geo, from.Pos, t, out b, out rng);
                sb.Append('B').Append(((int)Math.Round(b) % 360).ToString("000", Inv)).Append(" R").Append(Range(rng, u)).Append(' ');
            }
            else sb.Append("B--- R- ");
            sb.Append(Alt(t.Pos.Alt, u)).Append(" KT").Append((t.SpeedMps * KtPerMps).ToString("0", Inv)).Append(' ');
            sb.Append(from.Has && from.Unit != t.Vessel ? AspectTok(AspectTo(geo, t, from.Pos)) : "-").Append(' ');
            sb.Append("thr").Append(Frac(t.Threat)).Append(' ');
            sb.Append("eng:").Append(Engagement(tpe, t)).Append(' ');
            sb.Append('q').Append(Frac(t.Quality)).Append(" age").Append(((int)t.AgeS(now)).ToString(Inv));
            if (t.State == TrackState.Dead) sb.Append(" LOST");
            else if (t.State == TrackState.Dr) sb.Append(" DR");
            else if (t.Iff != Iff.Friend && p.Index >= 0 && t.IsNew(now, tpe.NewForS)) sb.Append(" NEW");
            if (t.Name != null) sb.Append(" n:").Append(Tok(t.Name));
            return sb.ToString();
        }

        public static string Engagement(TrackPictureEngine tpe, Track t)
        {
            if (t.EngagedBy <= 0 && t.MissilesAt <= 0) return "-";
            var sb = new StringBuilder(24);
            if (t.EngagedBy > 0)
            {
                var e = t.FirstEngager != 0 ? tpe.Unit(t.FirstEngager) : null;
                sb.Append(e != null ? Tok(e.Name) : "unit");
                if (t.EngagedBy > 1) sb.Append('+').Append(t.EngagedBy - 1);
            }
            if (t.MissilesAt > 0) sb.Append('(').Append(t.MissilesAt).Append(')');
            return sb.ToString();
        }

        // ------------------------------------------------------------------ threats

        /// <summary>
        /// Non-friendly live or dead-reckoned tracks ranked as threats: missiles inbound to own units first by time to
        /// closest approach, then by threat score. <paramref name="to"/> limits to threats against one unit (0 = all).
        /// </summary>
        public static void Threats(TeamPicture p, uint to, List<Track> into)
        {
            into.Clear();
            for (int i = 0; i < p.Tracks.Count; i++)
            {
                var t = p.Tracks[i];
                if (t.Iff == Iff.Friend || t.State == TrackState.Dead) continue;
                if (to != 0 && t.ThreatTo != to) continue;
                into.Add(t);
            }
            into.Sort(CompareThreat);
        }

        private static bool Inbound(Track t) { return t.IsMissile && t.MissileTarget != 0 && t.ThreatTo == t.MissileTarget && !double.IsNaN(t.Tcpa); }

        public static int CompareThreat(Track a, Track b)
        {
            bool ia = Inbound(a), ib = Inbound(b);
            if (ia != ib) return ia ? -1 : 1;
            if (ia)
            {
                int c = a.Tcpa.CompareTo(b.Tcpa);
                if (c != 0) return c;
            }
            int s = b.Threat.CompareTo(a.Threat);
            return s != 0 ? s : a.Number.CompareTo(b.Number);
        }

        public static string ThreatLine(TrackPictureEngine tpe, TeamPicture p, Track t, TacUnits u)
        {
            var geo = tpe.Geo;
            var sb = new StringBuilder(64);
            sb.Append('T').Append(t.Number).Append(' ').Append(IffTok(t.Iff)).Append(' ').Append(ClassTok(t.Class)).Append(' ');
            var to = t.ThreatTo != 0 ? tpe.Unit(t.ThreatTo) : null;
            if (to != null)
            {
                double b, rng;
                Braa(geo, to.Pos, t, out b, out rng);
                sb.Append('B').Append(((int)Math.Round(b) % 360).ToString("000", Inv)).Append(" R").Append(Range(rng, u)).Append(' ')
                  .Append(AspectTok(t.ThreatAspect)).Append(' ');
            }
            sb.Append("thr").Append(Frac(t.Threat));
            if (!double.IsNaN(t.Tcpa) && t.Tcpa < 600) sb.Append(" t").Append(t.Tcpa.ToString("0", Inv)).Append('s');
            if (to != null) sb.Append(" to:").Append(Short(to.Name, 10));
            sb.Append(" eng:").Append(t.EngagedBy + t.MissilesAt);
            if (t.State == TrackState.Dr) sb.Append(" DR");
            return sb.ToString();
        }

        private static string Short(string name, int max)
        {
            var s = Tok(name);
            return s.Length <= max ? s : s.Substring(0, max);
        }

        /// <summary>The ranked "what's the threat" answer: a header and up to <paramref name="limit"/> threat lines.</summary>
        public static string ThreatText(TrackPictureEngine tpe, TeamPicture p, List<Track> ranked, int limit, TacUnits u)
        {
            int n = Math.Min(limit, ranked.Count);
            var sb = new StringBuilder(64 + n * 56);
            sb.Append("#thr view=").Append(p.Index < 0 ? "god" : Tok(p.Team)).Append(" ut=").Append(tpe.Now.ToString("0.0", Inv))
              .Append(" units=").Append(u == TacUnits.Naval ? "nm" : "km").Append(" n=").Append(n);
            if (ranked.Count > n) sb.Append('/').Append(ranked.Count);
            if (n == 0) sb.Append("\nno threats held");
            for (int i = 0; i < n; i++) sb.Append('\n').Append(ThreatLine(tpe, p, ranked[i], u));
            return sb.ToString();
        }

        // ------------------------------------------------------------------ summary (08 §6 tac.summary)

        /// <summary>The whole picture in at most 6 lines and 500 characters.</summary>
        public static string Summary(TrackPictureEngine tpe, TeamPicture p, TacRef r, TacUnits u, List<Track> scratch)
        {
            var lines = new List<string>(6);
            lines.Add("#sum seq=" + tpe.Seq + " ut=" + tpe.Now.ToString("0.0", Inv) + " view=" + (p.Index < 0 ? "god" : Tok(p.Team)) +
                      (p.Index >= 0 && tpe.Fog ? " fog" : "") + " ref=" + (r.Has ? Tok(r.Name) : "nearest") + " units=" + (u == TacUnits.Naval ? "nm" : "km"));
            int air = 0, msl = 0, sur = 0, sub = 0, lnd = 0, unk = 0, frd = 0, frdAir = 0, frdSur = 0, frdOther = 0, ready = 0, max = 0, engaging = 0, ownMsl = 0, dr = 0;
            for (int i = 0; i < p.Tracks.Count; i++)
            {
                var t = p.Tracks[i];
                if (t.State == TrackState.Dead) continue;
                if (t.Iff == Iff.Friend)
                {
                    if (t.IsMissile) { ownMsl++; continue; }
                    frd++;
                    if (t.Domain == Domain.Air) frdAir++;
                    else if (t.Domain == Domain.Surface) frdSur++;
                    else frdOther++;
                    if (t.MissilesReadyKnown) { ready += t.MissilesReady; max += t.MissilesMax; }
                    continue;
                }
                if (t.State == TrackState.Dr) dr++;
                if (t.Iff == Iff.Unknown) { unk++; continue; }
                if (t.Iff != Iff.Hostile) continue;
                switch (t.Domain)
                {
                    case Domain.Air: air++; break;
                    case Domain.Missile: msl++; break;
                    case Domain.Surface: sur++; break;
                    case Domain.Subsurface: sub++; break;
                    default: lnd++; break;
                }
                if (t.EngagedBy > 0) engaging += t.EngagedBy;
            }
            int hos = air + msl + sur + sub + lnd;
            var sb = new StringBuilder(120);
            sb.Append("HOS ").Append(hos);
            if (hos > 0)
            {
                sb.Append(" (");
                bool first = true;
                Part(sb, "AIR", air, ref first); Part(sb, "MSL", msl, ref first); Part(sb, "SUR", sur, ref first); Part(sb, "SUB", sub, ref first); Part(sb, "LND", lnd, ref first);
                sb.Append(')');
            }
            sb.Append("  UNK ").Append(unk).Append("  FRD ").Append(frd);
            if (dr > 0) sb.Append("  DR ").Append(dr);
            sb.Append("  inbound to own: ").Append(p.InboundToOwn);
            if (p.InboundToOwn > 0 && !double.IsNaN(p.MinTcpa)) sb.Append(" (tcpa min ").Append(p.MinTcpa.ToString("0", Inv)).Append("s)");
            lines.Add(sb.ToString());
            Threats(p, 0, scratch);
            string own = "OWN " + frd + " (AIR " + frdAir + ", SUR " + frdSur + (frdOther > 0 ? ", other " + frdOther : "") + ")" +
                         (max > 0 ? " missiles " + ready + "/" + max : "") + " | engaging " + engaging + " | own msl in flight " + ownMsl;
            for (int top = Math.Min(3, scratch.Count); top >= 0; top--)
            {
                var tsb = new StringBuilder(160);
                if (top > 0)
                {
                    tsb.Append("TOP ");
                    for (int i = 0; i < top; i++)
                    {
                        if (i > 0) tsb.Append(" | ");
                        tsb.Append(ThreatLine(tpe, p, scratch[i], u));
                    }
                }
                int len = 0;
                for (int i = 0; i < lines.Count; i++) len += lines[i].Length + 1;
                len += own.Length + (top > 0 ? tsb.Length + 1 : 0);
                if (len <= 500 || top == 0)
                {
                    if (top > 0) lines.Add(tsb.ToString());
                    break;
                }
            }
            lines.Add(own);
            var text = string.Join("\n", lines);
            return text.Length <= 500 ? text : text.Substring(0, 500);
        }

        private static void Part(StringBuilder sb, string tok, int n, ref bool first)
        {
            if (n == 0) return;
            if (!first) sb.Append(", ");
            sb.Append(tok).Append(' ').Append(n);
            first = false;
        }
    }
}
