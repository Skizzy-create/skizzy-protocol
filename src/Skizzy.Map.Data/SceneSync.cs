using Skizzy.Map.Render.Model;

namespace Skizzy.Map.Data;

/// <summary>
/// Copies the mirror into one map window's render scene for a time and a viewing side. Each window owns its
/// scene (the renderer is not shared across windows); call <see cref="Sync"/> on the UI thread while holding the
/// window's render lock. Live: extrapolated positions and 1 Hz trails from history. Replay: Hermite-interpolated
/// history and trails rebuilt for the scrub position.
/// </summary>
public sealed class SceneSync
{
    private readonly Dictionary<int, PlotTrack> _byId = new();
    private readonly Dictionary<int, double> _trailT = new();
    private readonly List<int> _drop = new();
    private double _lastT = double.NaN;
    private bool _lastReplay;
    private ViewSide _lastView;

    public SceneSync(PlotScene scene) { Scene = scene; }

    public PlotScene Scene { get; }
    public ViewSide View { get; set; } = ViewSide.Blue;

    /// <summary>Plot filter: tracks failing it stay in the scene but are hidden. Null shows everything.</summary>
    public Func<TrackInfo, bool>? Filter { get; set; }

    /// <summary>Number of tracks the filter hid in the last sync.</summary>
    public int FilteredOut { get; private set; }
    /// <summary>Tracks this view can see (detected, alive at the time) in the last sync.</summary>
    public int Visible { get; private set; }

    public PlotTrack? Find(int id) => _byId.GetValueOrDefault(id);

    public void Sync(MapData data, IReadOnlySet<int> selection, IReadOnlyDictionary<int, float> sizeMul, int hoverId)
    {
        double t = data.ViewTime;
        bool replay = data.InReplay;
        bool jump = double.IsNaN(_lastT) || t < _lastT - 1e-6 || Math.Abs(t - _lastT) > 2.5 || replay != _lastReplay || View != _lastView;
        var scene = Scene;
        scene.Time = t;
        scene.HoverTrackId = hoverId;
        if (scene.Zones.Count != data.Zones.Count || !ReferenceEquals(scene.Zones.FirstOrDefault(), data.Zones.FirstOrDefault()))
        {
            scene.Zones.Clear();
            scene.Zones.AddRange(data.Zones); // zones are immutable once published, so windows share the objects
        }

        int filtered = 0, visible = 0;
        var tracks = data.Tracks;
        for (int ti = 0; ti < tracks.Count; ti++) // indexed: a foreach over the interface boxes the enumerator every frame
        {
            var tr = tracks[ti];
            bool exists = replay ? tr.ExistsAt(t) : tr.Alive;
            if (!exists) continue;
            double x, y; float alt, hdg, spd;
            if (replay)
            {
                if (!tr.History.TrySample(t, out x, out y, out alt, out hdg, out spd)) continue;
            }
            else
            {
                (x, y) = tr.PositionAt(t);
                alt = tr.AltitudeM; hdg = tr.HeadingDeg; spd = tr.SpeedMps;
            }
            if (!_byId.TryGetValue(tr.Id, out var p))
            {
                p = new PlotTrack(tr.Id, tr.Kind);
                _byId[tr.Id] = p;
                scene.Tracks.Add(p);
                _trailT[tr.Id] = double.NaN;
            }
            p.Kind = tr.Kind;
            p.Domain = tr.Domain;
            p.Side = tr.Side;
            p.Affiliation = tr.AffiliationFor(View);
            p.Detection = tr.DetectionFor(View);
            p.Rcs = tr.Rcs;
            p.X = (float)x; p.Y = (float)y; p.AltitudeM = alt; p.HeadingDeg = hdg; p.SpeedMps = spd;
            p.Name = tr.Name;
            p.Control = tr.Control;
            p.Health = tr.Health;
            p.TargetId = tr.TargetId;
            p.Rings = tr.Rings;
            p.Vip = tr.Vip;
            p.VipRadiusKm = tr.VipRadiusKm;
            p.Selected = selection.Contains(tr.Id);
            p.SizeMultiplier = sizeMul.TryGetValue(tr.Id, out var m) ? m : 1f;
            bool pass = Filter?.Invoke(tr) ?? true;
            p.Visible = pass;
            if (p.Detection != TrackDetection.None) { if (pass) visible++; else filtered++; }
            SyncTrail(tr, p, t, jump);
            _seen.Add(tr.Id);
        }

        // drop tracks that no longer exist at this time
        _drop.Clear();
        foreach (var id in _byId.Keys) if (!_seen.Contains(id)) _drop.Add(id);
        if (_drop.Count > 0)
        {
            foreach (var id in _drop) { _byId.Remove(id); _trailT.Remove(id); }
            scene.Tracks.RemoveAll(p => !_seen.Contains(p.Id));
        }
        _seen.Clear();

        // events: the renderer animates each for a few seconds after its time
        scene.Events.Clear();
        var ev = data.Events;
        for (int i = ev.Count - 1; i >= 0; i--)
        {
            var e = ev[i];
            if (e.Time > t + 1e-6) continue;
            if (e.Time < t - 4) break;
            scene.Events.Add(e);
        }

        SyncOrders(data.Orders, replay);

        FilteredOut = filtered;
        Visible = visible;
        _lastT = t;
        _lastReplay = replay;
        _lastView = View;
    }

    private readonly HashSet<int> _seen = new();

    // orders: copied into pooled scene objects only when the picture changed (live view only; replay shows none)
    private readonly List<PlotOrder> _orderPool = new();
    private readonly List<PlotRoute> _routePool = new();
    private readonly List<PlotRacetrack> _trackPool = new();
    private long _ordersVersion = -1;
    private bool _ordersHidden;

    private void SyncOrders(OrderPicture src, bool replay)
    {
        if (src.Version == _ordersVersion && replay == _ordersHidden) return;
        _ordersVersion = src.Version;
        _ordersHidden = replay;
        var scene = Scene;
        scene.Orders.Clear();
        scene.Routes.Clear();
        scene.Racetracks.Clear();
        if (replay) return;
        for (int i = 0; i < src.Orders.Count; i++)
        {
            if (_orderPool.Count <= i) _orderPool.Add(new PlotOrder());
            var o = _orderPool[i];
            var s = src.Orders[i];
            o.TrackId = s.TrackId; o.Type = s.Type; o.Destination = s.Destination; o.TargetTrackId = s.TargetTrackId;
            o.RouteId = s.RouteId; o.RouteIndex = s.RouteIndex; o.RouteOffset = s.RouteOffset; o.PatternId = s.PatternId;
            o.RtbPhase = s.RtbPhase; o.MarshalAsternKm = s.MarshalAsternKm;
            scene.Orders.Add(o);
        }
        for (int i = 0; i < src.Routes.Count; i++)
        {
            if (_routePool.Count <= i) _routePool.Add(new PlotRoute());
            var r = _routePool[i];
            var s = src.Routes[i];
            r.Id = s.Id;
            r.Points.Clear();
            for (int k = 0; k < s.Points.Count; k++) r.Points.Add(s.Points[k]);
            scene.Routes.Add(r);
        }
        for (int i = 0; i < src.Racetracks.Count; i++)
        {
            if (_trackPool.Count <= i) _trackPool.Add(new PlotRacetrack());
            var r = _trackPool[i];
            var s = src.Racetracks[i];
            r.Id = s.Id; r.A = s.A; r.B = s.B; r.WidthKm = s.WidthKm; r.TurnRight = s.TurnRight;
            scene.Racetracks.Add(r);
        }
    }

    private void SyncTrail(TrackInfo tr, PlotTrack p, double t, bool rebuild)
    {
        var h = tr.History;
        var trail = p.Trail;
        double last = _trailT[tr.Id];
        if (rebuild || double.IsNaN(last))
        {
            trail.Clear();
            int end = h.IndexAtOrBefore(t);
            int start = Math.Max(0, end - trail.Capacity + 1);
            for (int i = start; i <= end; i++) { var (x, y) = h.PointAt(i); trail.Add(x, y); }
            _trailT[tr.Id] = end >= 0 ? h.TimeAt(end) : double.NegativeInfinity;
            return;
        }
        // live: append history points newer than the last one we copied
        int n = h.Count;
        if (n == 0 || h.TimeAt(n - 1) <= last) return;
        int k = n - 1;
        while (k > 0 && h.TimeAt(k - 1) > last) k--;
        for (int i = k; i < n; i++)
        {
            if (h.TimeAt(i) > t + 1e-6) break;
            var (x, y) = h.PointAt(i);
            trail.Add(x, y);
            _trailT[tr.Id] = h.TimeAt(i);
        }
    }
}
