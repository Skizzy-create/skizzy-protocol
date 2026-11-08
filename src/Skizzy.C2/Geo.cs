using System;

namespace Skizzy.C2
{
    /// <summary>
    /// A body-relative position: latitude and longitude in degrees, altitude in metres. For unit positions the
    /// altitude is above sea level; for waypoints handed to BDArmory (F6) it is above terrain, which is the same
    /// over water.
    /// </summary>
    public struct GeoPoint
    {
        public double Lat;
        public double Lon;
        public double Alt;

        public GeoPoint(double lat, double lon, double alt = 0)
        {
            Lat = lat;
            Lon = lon;
            Alt = alt;
        }

        public GeoPoint WithAlt(double alt) { return new GeoPoint(Lat, Lon, alt); }

        public bool IsZero => Lat == 0 && Lon == 0 && Alt == 0;

        public override string ToString()
        {
            return Lat.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture) + "," +
                   Lon.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture) + "," +
                   Alt.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Great-circle geometry on a spherical body (Kerbin radius 600 km by default). The engine and FakeBda use the
    /// same functions, so tests and the game agree on distances and bearings. Pure, allocation-free.
    /// </summary>
    public sealed class Geo
    {
        public const double KerbinRadius = 600000.0;
        private const double D2R = Math.PI / 180.0;
        private const double R2D = 180.0 / Math.PI;

        public readonly double Radius;

        public Geo(double radius)
        {
            Radius = radius > 0 ? radius : KerbinRadius;
        }

        /// <summary>Surface distance in metres (altitude ignored).</summary>
        public double Distance(GeoPoint a, GeoPoint b)
        {
            double la1 = a.Lat * D2R, la2 = b.Lat * D2R;
            double dLa = la2 - la1, dLo = (b.Lon - a.Lon) * D2R;
            double s1 = Math.Sin(dLa * 0.5), s2 = Math.Sin(dLo * 0.5);
            double h = s1 * s1 + Math.Cos(la1) * Math.Cos(la2) * s2 * s2;
            if (h > 1) h = 1;
            return 2 * Radius * Math.Asin(Math.Sqrt(h));
        }

        /// <summary>Slant distance in metres including the altitude difference.</summary>
        public double Distance3(GeoPoint a, GeoPoint b)
        {
            double d = Distance(a, b), dz = b.Alt - a.Alt;
            return Math.Sqrt(d * d + dz * dz);
        }

        /// <summary>Initial bearing from a to b in degrees, [0, 360).</summary>
        public double Bearing(GeoPoint a, GeoPoint b)
        {
            double la1 = a.Lat * D2R, la2 = b.Lat * D2R, dLo = (b.Lon - a.Lon) * D2R;
            double y = Math.Sin(dLo) * Math.Cos(la2);
            double x = Math.Cos(la1) * Math.Sin(la2) - Math.Sin(la1) * Math.Cos(la2) * Math.Cos(dLo);
            return Norm360(Math.Atan2(y, x) * R2D);
        }

        /// <summary>The point reached from p after <paramref name="distM"/> metres on <paramref name="bearingDeg"/>. Altitude is kept.</summary>
        public GeoPoint Destination(GeoPoint p, double bearingDeg, double distM)
        {
            double d = distM / Radius, b = bearingDeg * D2R;
            double la1 = p.Lat * D2R, lo1 = p.Lon * D2R;
            double sinLa2 = Math.Sin(la1) * Math.Cos(d) + Math.Cos(la1) * Math.Sin(d) * Math.Cos(b);
            if (sinLa2 > 1) sinLa2 = 1; else if (sinLa2 < -1) sinLa2 = -1;
            double la2 = Math.Asin(sinLa2);
            double lo2 = lo1 + Math.Atan2(Math.Sin(b) * Math.Sin(d) * Math.Cos(la1), Math.Cos(d) - Math.Sin(la1) * sinLa2);
            return new GeoPoint(la2 * R2D, NormLon(lo2 * R2D), p.Alt);
        }

        /// <summary>Offsets <paramref name="p"/> by metres east and north (small offsets; good to a few metres over tens of km).</summary>
        public GeoPoint Offset(GeoPoint p, double eastM, double northM)
        {
            double d = Math.Sqrt(eastM * eastM + northM * northM);
            if (d < 1e-9) return p;
            return Destination(p, Math.Atan2(eastM, northM) * R2D, d);
        }

        /// <summary>The midpoint of a and b (altitude averaged).</summary>
        public GeoPoint Midpoint(GeoPoint a, GeoPoint b)
        {
            double d = Distance(a, b);
            if (d < 1e-6) return new GeoPoint(a.Lat, a.Lon, (a.Alt + b.Alt) * 0.5);
            var m = Destination(a, Bearing(a, b), d * 0.5);
            m.Alt = (a.Alt + b.Alt) * 0.5;
            return m;
        }

        /// <summary>
        /// Range rate in m/s between an observer and a target (negative = closing), from positions, headings and speeds.
        /// Uses a flat local frame at the observer, which is accurate enough for closing tests.
        /// </summary>
        public double RangeRate(GeoPoint observer, double obsHeadingDeg, double obsSpeed, GeoPoint target, double tgtHeadingDeg, double tgtSpeed)
        {
            double brg = Bearing(observer, target) * D2R;
            double ux = Math.Sin(brg), uy = Math.Cos(brg); // unit line of sight (east, north)
            double ovx = Math.Sin(obsHeadingDeg * D2R) * obsSpeed, ovy = Math.Cos(obsHeadingDeg * D2R) * obsSpeed;
            double tvx = Math.Sin(tgtHeadingDeg * D2R) * tgtSpeed, tvy = Math.Cos(tgtHeadingDeg * D2R) * tgtSpeed;
            return (tvx - ovx) * ux + (tvy - ovy) * uy;
        }

        public static double Norm360(double deg)
        {
            deg %= 360.0;
            return deg < 0 ? deg + 360.0 : deg;
        }

        /// <summary>Signed smallest difference b - a in degrees, (-180, 180].</summary>
        public static double AngleDiff(double a, double b)
        {
            double d = Norm360(b - a);
            return d > 180 ? d - 360 : d;
        }

        public static double NormLon(double lon)
        {
            lon = (lon + 180.0) % 360.0;
            if (lon < 0) lon += 360.0;
            return lon - 180.0;
        }
    }
}
