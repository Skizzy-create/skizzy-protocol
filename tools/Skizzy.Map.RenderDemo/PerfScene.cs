using Skizzy.Map.Render;
using Skizzy.Map.Render.Model;

namespace Skizzy.Map.RenderDemo;

/// <summary>
/// A synthetic large picture for performance work: <c>n</c> tracks in 40 clustered groups over a 300 x 170 km
/// theatre. About 2 % are ships (a third of them own, with weapon and radar rings), the rest fighters, strike
/// aircraft, drones and missiles; every track has a 20-point trail; one in 17 has an order badge. Deterministic.
/// </summary>
public static class PerfScene
{
    public static PlotScene Build(int n, int seed = 7)
    {
        var rng = new SampleScene.Rng(seed);
        var scene = new PlotScene();
        TrackKind[] mix =
        [
            TrackKind.Fighter, TrackKind.Fighter, TrackKind.Fighter, TrackKind.Strike, TrackKind.Ucav, TrackKind.Ucav,
            TrackKind.Uav, TrackKind.AntiShipMissile, TrackKind.AntiShipMissile, TrackKind.SurfaceToAirMissile,
            TrackKind.AirToAirMissile, TrackKind.Ucav, TrackKind.EarlyWarning,
        ];
        for (int i = 0; i < n; i++)
        {
            // ~2 % ships (4 carriers, 36 destroyers), the rest aircraft, drones and missiles
            var kind = i < 4 ? TrackKind.Carrier : i < 40 ? TrackKind.Destroyer : mix[(int)(rng.Next() * mix.Length)];
            // clustered: 40 groups, each a few km across, like real formations and raids
            int g = (int)(rng.Next() * 40);
            float gx = -40 + (g * 37 % 290), gy = -80 + (g * 53 % 160);
            var t = new PlotTrack(i + 1, kind)
            {
                Side = g % 3 == 0 ? Side.Blue : Side.Red,
                Affiliation = g % 3 == 0 ? Affiliation.Friend : g % 7 == 0 ? Affiliation.Unknown : Affiliation.Hostile,
                X = gx + (float)(rng.Next() - .5) * 14,
                Y = gy + (float)(rng.Next() - .5) * 14,
                AltitudeM = KindInfo.Of(kind).Domain == TrackDomain.Surface ? 0 : (float)(rng.Next() * 9000),
                HeadingDeg = (float)(rng.Next() * 360),
                SpeedMps = KindInfo.Of(kind).IsMissile ? 300 + (float)rng.Next() * 600 : 8 + (float)rng.Next() * 250,
                Name = $"T{i + 1:0000}",
                Control = i % 17 == 0 ? ControlState.Order : ControlState.Mission,
                Health = KindInfo.Of(kind).Domain == TrackDomain.Surface ? 0.8f : -1f,
            };
            if (kind is TrackKind.Destroyer or TrackKind.Carrier && t.Affiliation == Affiliation.Friend)
                t.Rings = [new RangeRing(38, "SM-2 38 km"), new RangeRing(22, "ESSM 22 km"), new RangeRing(150, "Radar 150 km", true)];
            float h = t.HeadingDeg * MathF.PI / 180f, v = t.SpeedMps / 1000f;
            for (int k = 20; k >= 1; k--) t.Trail.Add(t.X - MathF.Sin(h) * v * k, t.Y - MathF.Cos(h) * v * k);
            scene.Tracks.Add(t);
        }
        scene.Tracks[0].Selected = true;
        scene.View = Viewport.Fit(-50, -90, 260, 90, 1920, 1080);
        return scene;
    }
}
