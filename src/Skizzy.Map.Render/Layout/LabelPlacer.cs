using System.Globalization;
using SkiaSharp;
using Skizzy.Map.Render.Model;
using Skizzy.Map.Render.Text;

namespace Skizzy.Map.Render.Layout;

/// <summary>
/// Greedy four-position label placement (design/23 §4.8), ported from the mockup's <c>drawLabels()</c>, with the
/// doc's rule added: a label never covers a symbol. Candidates go in priority order
/// (selected &gt; own &gt; larger RCS; dots last). Each tries right, left, above, below; the first position whose
/// backing box hits neither a placed label nor any symbol footprint wins; otherwise the label is culled.
/// Dots get no label unless selected. Missile labels need the selection or a zoom of
/// <see cref="PlotOptions.MissileLabelMinScale"/> px/km.
/// </summary>
internal static class LabelPlacer
{
    public const float LabelHeight = 12f;
    public const float BoxPadX = 2f;
    private const float BadgeRadius = 5.5f;

    public static void Place(PlotScene scene, in Viewport vp, RenderState st, RectGrid grid, SKFont labelFont)
    {
        var items = st.Items;
        int count = st.ItemCount;
        var tracks = scene.Tracks;
        var opt = st.Options;

        grid.Reset(vp.Width, vp.Height);
        for (int i = 0; i < st.SymbolBoxCount; i++) grid.Add(st.SymbolBoxes[i]);

        // candidates, sorted by priority (stable via the index tie-break)
        RenderState.Ensure(ref st.Order, count);
        RenderState.Ensure(ref st.SortKeys, count);
        var order = st.Order;
        var keys = st.SortKeys;
        int m = 0;
        for (int i = 0; i < count; i++)
        {
            ref var it = ref items[i];
            if (it.Tier == SymbolTier.Dot && !it.Selected) continue;
            if (it.Missile && !it.Selected && vp.Scale < opt.MissileLabelMinScale) continue;
            var t = tracks[it.TrackIndex];
            double pri = (it.Selected ? 1e9 : 0) + (t.Affiliation == Affiliation.Friend ? 2e5 : 0) + t.Rcs * 10.0
                         + (it.Tier == SymbolTier.Dot ? -1e6 : 0);
            keys[m] = -pri + i * 1e-5;
            order[m] = i;
            m++;
        }
        Array.Sort(keys, order, 0, m);

        RenderState.Ensure(ref st.LabelBoxes, m);
        RenderState.Ensure(ref st.LabelOwner, m);
        int placed = 0, culled = 0;
        const float h = LabelHeight;
        for (int k = 0; k < m; k++)
        {
            int i = order[k];
            ref var it = ref items[i];
            var run = LabelFor(tracks[it.TrackIndex], it.Visual, labelFont);
            if (run.Blob is null) continue;
            float w = run.Width;
            // ext: how far the symbol reaches from its centre, so a label clears its own symbol at any size
            float ext = MathF.Max(it.S / 2f, it.Half - 1f);
            float right = it.Badge ? MathF.Max(ext + 5f, it.S / 2f + BadgeRadius + 1f + BoxPadX) : ext + 5f;
            bool ok = false;
            for (int p = 0; p < 4 && !ok; p++)
            {
                float dx, dy;
                switch (p)
                {
                    case 0: dx = right; dy = -h / 2; break;
                    case 1: dx = -ext - 5f - w; dy = -h / 2; break;
                    case 2: dx = -w / 2; dy = -ext - h - 3f; break;
                    default: dx = -w / 2; dy = ext + 6f; break;
                }
                var box = new ScreenRect(it.Sx + dx - BoxPadX, it.Sy + dy, w + 2 * BoxPadX, h);
                if (grid.Overlaps(box)) continue;
                grid.Add(box);
                st.LabelBoxes[placed] = box;
                st.LabelOwner[placed] = i;
                placed++;
                ok = true;
            }
            if (!ok) culled++;
        }
        st.LabelBoxCount = placed;
        var stats = st.LastFrame;
        stats.LabelsPlaced = placed;
        stats.LabelsCulled = culled;
        st.LastFrame = stats;
    }

    /// <summary>Label text: missiles show the name; others the name plus altitude (the mockup's <c>altText()</c>). Cached per track.</summary>
    internal static TextRun LabelFor(PlotTrack t, TrackVisual v, SKFont font)
    {
        bool missile = t.IsMissile;
        int key = AltKey(t, missile);
        if (v.Label is not null && ReferenceEquals(v.LabelName, t.Name) && v.LabelAltKey == key && v.LabelIsMissile == missile)
            return v.Label;
        string alt = missile ? "" : AltText(t);
        string text = alt.Length == 0 ? t.Name : t.Name + "  " + alt;
        v.SetLabel(TextCache.Shape(text, font));
        v.LabelName = t.Name;
        v.LabelAltKey = key;
        v.LabelIsMissile = missile;
        return v.Label!;
    }

    private static int AltKey(PlotTrack t, bool missile)
    {
        if (missile) return int.MaxValue;
        if (t.Domain == TrackDomain.Subsurface) return 2_000_000 + (int)MathF.Round(MathF.Abs(t.AltitudeM));
        if (t.Domain == TrackDomain.Surface) return int.MinValue + 1;
        return t.AltitudeM < 1000 ? (int)MathF.Round(t.AltitudeM) : 1_000_000 + (int)MathF.Round(t.AltitudeM / 100f);
    }

    internal static string AltText(PlotTrack t)
    {
        if (t.Domain == TrackDomain.Subsurface) return "−" + MathF.Round(MathF.Abs(t.AltitudeM)).ToString(CultureInfo.InvariantCulture) + " m";
        if (t.Domain == TrackDomain.Surface) return "";
        if (t.AltitudeM < 1000) return MathF.Round(t.AltitudeM).ToString(CultureInfo.InvariantCulture) + " m";
        return (t.AltitudeM / 1000f).ToString("0.0", CultureInfo.InvariantCulture) + "k";
    }
}
