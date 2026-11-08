using System;
using System.Collections.Generic;

namespace Skizzy.C2
{
    /// <summary>Route geometry: racetracks, orbit rings, formation offsets, marshal points and escort slots.</summary>
    public static class Patterns
    {
        public const int LegSegments = 10;
        public const int TurnSegments = 12;

        /// <summary>
        /// The racetrack of 09 §2.1.2 (mockup <c>trackPts</c>): two straight legs a→b, joined by half circles of
        /// diameter <paramref name="widthM"/>. Right turns (clockwise from above) unless <paramref name="turnRight"/> is false.
        /// Adds 2·(LegSegments+1) + 2·(TurnSegments−1) points at altitude <paramref name="alt"/>.
        /// </summary>
        public static void Racetrack(Geo geo, GeoPoint a, GeoPoint b, double widthM, bool turnRight, double alt, List<GeoPoint> into)
        {
            double r = widthM * 0.5;
            double len = geo.Distance(a, b);
            double brg = geo.Bearing(a, b) * Math.PI / 180.0;
            // local frame at a: x east, y north
            double bx = Math.Sin(brg) * len, by = Math.Cos(brg) * len;
            double ux = len > 0 ? bx / len : 1, uy = len > 0 ? by / len : 0;
            // left normal of the a→b direction; the outbound leg sits on it so the turn at b is to the right
            double nx = -uy, ny = ux;
            if (!turnRight) { nx = -nx; ny = -ny; }
            double an = Math.Atan2(ny, nx);
            double sign = turnRight ? -1 : 1; // clockwise = decreasing angle in the (east, north) frame
            for (int k = 0; k <= LegSegments; k++)
            {
                double t = (double)k / LegSegments;
                Add(geo, a, nx * r + bx * t, ny * r + by * t, alt, into);
            }
            for (int k = 1; k < TurnSegments; k++)
            {
                double th = an + sign * Math.PI * k / TurnSegments;
                Add(geo, a, bx + Math.Cos(th) * r, by + Math.Sin(th) * r, alt, into);
            }
            for (int k = 0; k <= LegSegments; k++)
            {
                double t = (double)k / LegSegments;
                Add(geo, a, bx - nx * r - bx * t, by - ny * r - by * t, alt, into);
            }
            for (int k = 1; k < TurnSegments; k++)
            {
                double th = an + Math.PI + sign * Math.PI * k / TurnSegments;
                Add(geo, a, Math.Cos(th) * r, Math.Sin(th) * r, alt, into);
            }
        }

        private static void Add(Geo geo, GeoPoint origin, double east, double north, double alt, List<GeoPoint> into)
        {
            var p = geo.Offset(origin, east, north);
            p.Alt = alt;
            into.Add(p);
        }

        /// <summary>A closed ring of <paramref name="n"/> points around <paramref name="center"/>, clockwise, starting at <paramref name="startBearing"/>.</summary>
        public static void Ring(Geo geo, GeoPoint center, double radiusM, int n, double alt, double startBearing, List<GeoPoint> into)
        {
            for (int i = 0; i < n; i++)
            {
                var p = geo.Destination(center, startBearing + 360.0 * i / n, radiusM);
                p.Alt = alt;
                into.Add(p);
            }
        }

        /// <summary>Rotates <paramref name="pts"/> so it starts at the point after the one nearest <paramref name="from"/> (join a loop where you are).</summary>
        public static void RotateToNearest(Geo geo, List<GeoPoint> pts, GeoPoint from, List<GeoPoint> scratch)
        {
            if (pts.Count < 3) return;
            int best = 0;
            double bd = double.MaxValue;
            for (int i = 0; i < pts.Count; i++)
            {
                double d = geo.Distance(from, pts[i]);
                if (d < bd) { bd = d; best = i; }
            }
            int start = (best + 1) % pts.Count;
            if (start == 0) return;
            scratch.Clear();
            for (int i = 0; i < pts.Count; i++) scratch.Add(pts[(start + i) % pts.Count]);
            pts.Clear();
            pts.AddRange(scratch);
        }

        /// <summary>Grid offset (east, north) in metres for unit <paramref name="i"/> of <paramref name="n"/> (mockup issueOrder spread).</summary>
        public static void FormationOffset(int i, int n, double spacingM, out double east, out double north)
        {
            int cols = (int)Math.Ceiling(Math.Sqrt(n));
            if (cols < 1) cols = 1;
            int rows = (int)Math.Ceiling((double)n / cols);
            int r = i / cols, c = i % cols;
            east = (c - (cols - 1) * 0.5) * spacingM;
            north = -(r - (rows - 1) * 0.5) * spacingM;
        }

        /// <summary>Marshal point <paramref name="asternM"/> behind a base on its heading (09 §2.1.2).</summary>
        public static GeoPoint Marshal(Geo geo, GeoPoint basePos, double baseHeadingDeg, double asternM)
        {
            return geo.Destination(basePos, Geo.Norm360(baseHeadingDeg + 180), asternM);
        }

        /// <summary>Escort slot relative to the leader (row 22): ahead on its heading, abreast to alternating sides, or astern.</summary>
        public static GeoPoint EscortSlot(Geo geo, GeoPoint leader, double leaderHeading, EscortSlot slot, double distM, int index, double lateralM)
        {
            double side = (index % 2 == 0 ? 1 : -1) * ((index + 1) / 2);
            GeoPoint p;
            switch (slot)
            {
                case Skizzy.C2.EscortSlot.Abreast:
                    p = geo.Destination(leader, leaderHeading + (index % 2 == 0 ? 90 : -90), distM + lateralM * (index / 2));
                    break;
                case Skizzy.C2.EscortSlot.Astern:
                    p = geo.Destination(leader, leaderHeading + 180, distM);
                    if (side != 0) p = geo.Destination(p, leaderHeading + 90, lateralM * side);
                    break;
                default:
                    p = geo.Destination(leader, leaderHeading, distM);
                    if (side != 0) p = geo.Destination(p, leaderHeading + 90, lateralM * side);
                    break;
            }
            p.Alt = leader.Alt;
            return p;
        }
    }
}
