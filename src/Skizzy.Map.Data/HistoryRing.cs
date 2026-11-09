namespace Skizzy.Map.Data;

/// <summary>
/// Per-track kinematic history: one sample per recording interval (1 s by default), oldest first, bounded.
/// Feeds trails and the replay scrubber. Storage grows on demand up to <see cref="Capacity"/>, so short-lived
/// tracks (missiles) stay small. Times are seconds on the map clock; positions are km on the plot plane.
/// </summary>
public sealed class HistoryRing
{
    private double[] _t;
    private float[] _x, _y, _alt, _hdg, _spd;
    private int _start;

    public HistoryRing(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 2);
        Capacity = capacity;
        int initial = Math.Min(capacity, 8);
        _t = new double[initial];
        _x = new float[initial];
        _y = new float[initial];
        _alt = new float[initial];
        _hdg = new float[initial];
        _spd = new float[initial];
    }

    public int Capacity { get; }
    public int Count { get; private set; }
    public double FirstT => Count == 0 ? double.NaN : _t[Slot(0)];
    public double LastT => Count == 0 ? double.NaN : _t[Slot(Count - 1)];

    private int Slot(int i) => (_start + i) % _t.Length;

    /// <summary>Appends a sample. A sample at the same time as the last one replaces it; an older one is ignored.</summary>
    public void Add(double t, double x, double y, float alt, float hdgDeg, float spdMps)
    {
        if (Count > 0 && t <= LastT + 1e-9)
        {
            if (t >= LastT - 1e-9) Write(Slot(Count - 1), t, x, y, alt, hdgDeg, spdMps); // same instant: newest wins
            return; // older than the newest sample: ignore
        }
        if (Count == _t.Length && _t.Length < Capacity) Grow();
        int slot;
        if (Count < _t.Length) { slot = Slot(Count); Count++; }
        else { slot = _start; _start = (_start + 1) % _t.Length; }
        Write(slot, t, x, y, alt, hdgDeg, spdMps);
    }

    private void Write(int s, double t, double x, double y, float alt, float hdg, float spd)
    {
        _t[s] = t; _x[s] = (float)x; _y[s] = (float)y; _alt[s] = alt; _hdg[s] = hdg; _spd[s] = spd;
    }

    private void Grow()
    {
        int n = Math.Min(Capacity, _t.Length * 2);
        _t = Unroll(_t, n); _x = Unroll(_x, n); _y = Unroll(_y, n); _alt = Unroll(_alt, n); _hdg = Unroll(_hdg, n); _spd = Unroll(_spd, n);
        _start = 0;
    }

    private T[] Unroll<T>(T[] a, int n)
    {
        var r = new T[n];
        for (int i = 0; i < Count; i++) r[i] = a[(_start + i) % a.Length];
        return r;
    }

    public void Clear() { _start = 0; Count = 0; }

    public double TimeAt(int i) => _t[Slot(i)];

    public (float X, float Y) PointAt(int i) { int s = Slot(i); return (_x[s], _y[s]); }

    /// <summary>Index of the last sample at or before <paramref name="t"/>, or -1 when t is before the first sample.</summary>
    public int IndexAtOrBefore(double t)
    {
        int lo = 0, hi = Count - 1, best = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (_t[Slot(mid)] <= t) { best = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return best;
    }

    /// <summary>
    /// Kinematic state at time t. Between samples: cubic Hermite on position with the recorded velocities as
    /// tangents, so replayed tracks curve through turns instead of cutting corners. Up to <paramref name="maxAhead"/>
    /// seconds past the last sample it extrapolates; earlier than the first sample it returns false.
    /// </summary>
    public bool TrySample(double t, out double x, out double y, out float alt, out float hdgDeg, out float spdMps, double maxAhead = 1.5)
    {
        x = y = 0; alt = hdgDeg = spdMps = 0;
        if (Count == 0) return false;
        int i = IndexAtOrBefore(t);
        if (i < 0) return false;
        int a = Slot(i);
        if (i == Count - 1)
        {
            double dt = t - _t[a];
            if (dt > maxAhead) return false;
            Velocity(_hdg[a], _spd[a], out double vx, out double vy);
            x = _x[a] + vx * dt; y = _y[a] + vy * dt; alt = _alt[a]; hdgDeg = _hdg[a]; spdMps = _spd[a];
            return true;
        }
        int b = Slot(i + 1);
        double h = _t[b] - _t[a];
        double s = h <= 1e-9 ? 0 : (t - _t[a]) / h;
        Velocity(_hdg[a], _spd[a], out double v0x, out double v0y);
        Velocity(_hdg[b], _spd[b], out double v1x, out double v1y);
        double s2 = s * s, s3 = s2 * s;
        double h00 = 2 * s3 - 3 * s2 + 1, h10 = s3 - 2 * s2 + s, h01 = -2 * s3 + 3 * s2, h11 = s3 - s2;
        // Tangents are only trusted when they roughly agree with the chord (a teleport or a respawn breaks Hermite).
        double cx = _x[b] - _x[a], cy = _y[b] - _y[a];
        double chord = Math.Sqrt(cx * cx + cy * cy), vlen = (Math.Sqrt(v0x * v0x + v0y * v0y) + Math.Sqrt(v1x * v1x + v1y * v1y)) * 0.5 * h;
        if (chord > 3 * vlen + 0.5) { h10 = h11 = 0; h00 = 1 - s; h01 = s; }
        x = h00 * _x[a] + h10 * h * v0x + h01 * _x[b] + h11 * h * v1x;
        y = h00 * _y[a] + h10 * h * v0y + h01 * _y[b] + h11 * h * v1y;
        alt = (float)(_alt[a] + (_alt[b] - _alt[a]) * s);
        spdMps = (float)(_spd[a] + (_spd[b] - _spd[a]) * s);
        float dh = ((_hdg[b] - _hdg[a]) % 360 + 540) % 360 - 180;
        hdgDeg = ((float)(_hdg[a] + dh * s) % 360 + 360) % 360;
        return true;
    }

    /// <summary>km/s on the plot plane from a compass heading (degrees) and a speed (m/s).</summary>
    public static void Velocity(float hdgDeg, float spdMps, out double vx, out double vy)
    {
        double r = hdgDeg * Math.PI / 180;
        vx = Math.Sin(r) * spdMps / 1000;
        vy = Math.Cos(r) * spdMps / 1000;
    }
}
