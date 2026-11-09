using Skizzy.Map.Render.Model;

namespace Skizzy.Map.Data.Demo;

/// <summary>
/// N tracks on straight lines, clustered like raids, every one sampled every frame: the perf gate's load
/// (the worst case for the data path: no track ever rests). Used by the perf tests and `skizzy-map --bench`.
/// </summary>
public sealed class SyntheticSource : IDataSource
{
    private readonly int _n;
    private MapData? _data;
    private readonly List<(TrackInfo t, double x, double y, double vx, double vy, float alt, float hdg, float spd)> _tracks = new();
    private double _t;

    public SyntheticSource(int n) => _n = n;

    public string Name => "Synthetic";
    public string Description => $"Perf gate · {_n:#,0} synthetic tracks";
    public SourceKind Kind => SourceKind.Demo;
    public SourceCaps Caps => SourceCaps.Pause;
    public SourceState State => SourceState.Online;
    public string StatusText => "synthetic";
    public bool Paused { get; private set; }
    public double Speed => 1;
    public IReadOnlyList<CatalogEntry> Catalog => [];
    public HealthSnapshot Health => new(Name, "Online", null, null, null, null, _n, 0, []);

    public void Attach(MapData data)
    {
        _data = data;
        data.Reset();
        data.Epoch = 0;
        var rng = new Random(7);
        TrackKind[] mix = [TrackKind.Fighter, TrackKind.Fighter, TrackKind.Strike, TrackKind.Ucav, TrackKind.Uav, TrackKind.AntiShipMissile, TrackKind.SurfaceToAirMissile, TrackKind.AirToAirMissile, TrackKind.EarlyWarning];
        for (int i = 0; i < _n; i++)
        {
            var kind = i < 4 ? TrackKind.Carrier : i < 40 ? TrackKind.Destroyer : mix[rng.Next(mix.Length)];
            int g = rng.Next(40);
            double gx = -40 + (g * 37 % 290), gy = -80 + (g * 53 % 160);
            var tr = data.AddWithId(i + 1, "s" + i, 0);
            var info = KindInfo.Of(kind);
            tr.Kind = kind;
            tr.Domain = info.Domain;
            tr.Side = g % 3 == 0 ? Side.Blue : Side.Red;
            tr.AffBlue = tr.Side == Side.Blue ? Affiliation.Friend : g % 7 == 0 ? Affiliation.Unknown : Affiliation.Hostile;
            tr.AffRed = tr.Side == Side.Red ? Affiliation.Friend : Affiliation.Hostile;
            tr.DetBlue = tr.DetRed = TrackDetection.Shared;
            tr.Name = $"T{i + 1:0000}";
            tr.Rcs = info.IsMissile ? 0.1f : info.Domain == TrackDomain.Surface ? 10000 : 5;
            tr.Health = info.Domain == TrackDomain.Surface ? 0.8f : -1;
            float hdg = (float)(rng.NextDouble() * 360), spd = info.IsMissile ? 300 + (float)rng.NextDouble() * 600 : info.Domain == TrackDomain.Surface ? 12 : 120 + (float)rng.NextDouble() * 150;
            HistoryRing.Velocity(hdg, spd, out double vx, out double vy);
            _tracks.Add((tr, gx + (rng.NextDouble() - .5) * 14, gy + (rng.NextDouble() - .5) * 14, vx, vy, info.Domain == TrackDomain.Surface ? 0 : (float)(rng.NextDouble() * 9000), hdg, spd));
        }
        Publish();
    }

    public void Tick(double realDt)
    {
        if (_data is null || Paused) return;
        _t += realDt;
        Publish();
    }

    private void Publish()
    {
        var d = _data!;
        d.LiveTime = _t;
        foreach (var (t, x, y, vx, vy, alt, hdg, spd) in _tracks)
        {
            t.SetSample(_t, x + vx * _t, y + vy * _t, alt, hdg, spd, vx, vy, _t);
            t.Record(d.HistoryInterval);
        }
        d.TouchRevision();
    }

    public void SetPaused(bool paused) => Paused = paused;
    public void SetSpeed(double speed) { }
    public void Restart() { _t = 0; }
    public Task<string> CallAsync(string method, string argsJson, CancellationToken ct) => throw new NotSupportedException();
    public void Dispose() => _data = null;
}
