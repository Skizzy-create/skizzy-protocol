using Skizzy.Map.Render.Model;

namespace Skizzy.Map.Render.Layout;

/// <summary>
/// Symbol sizing (design/23 §4.2–4.4, doc 11 §2.1 rules 2–3), ported from the mockup's <c>draw()</c>:
/// <code>
/// desired = rcsBase × zoomFactor × sizeScale
/// allowed = max(3, 0.85 × distance to nearest PEER)          (Auto mode only)
/// size    = min(min(desired, allowed) × item × (selected ? 1.2 : 1), 44)
/// </code>
/// Peers: for a ship or aircraft, missiles and tracks with RCS &lt; 1/50 of its own never count (they are drawn
/// on top instead). Missiles count every neighbour. Neighbours come from a screen-space grid hash (36 px cells,
/// ±2 cells), so the pass is O(N). Sizes ease over ~150 ms; tiers change with ±10 % hysteresis.
/// </summary>
internal static class SymbolSizer
{
    public const float CellPx = 36f;
    public const int CellReach = 2;
    public const float NoNeighbourPx = 110f;
    public const float CullMarginPx = 60f;
    public const float MinSizePx = 3f;
    public const float MaxSizePx = 44f;
    public const float SpacingFactor = 0.85f;
    public const float SelectedFactor = 1.2f;
    public const float PeerRcsRatio = 50f;

    public const float FullPx = 16f;
    public const float FramePx = 8f;
    public const float MissilePx = 5f;
    public const float Hysteresis = 0.10f;

    /// <summary>Easing time constant. The mockup moves 18 % per 60 Hz frame (τ ≈ 84 ms, ~90 % settled at 150 ms).</summary>
    public const float EaseTauSeconds = 0.084f;
    /// <summary>The mockup snaps instead of easing when the change exceeds 30 px.</summary>
    public const float SnapDeltaPx = 30f;

    /// <summary>The mockup's zoom factor: clamp((scale/3)^0.32, 0.55, 1.9).</summary>
    public static float ZoomFactor(float scale) => Math.Clamp(MathF.Pow(scale / 3f, 0.32f), 0.55f, 1.9f);

    /// <summary>The mockup's <c>baseSize()</c> before the per-item and selection factors.</summary>
    public static float BaseSize(float rcs, bool missile, SizeMode mode, float zf, float sizeScale)
    {
        float s = mode is SizeMode.Fixed or SizeMode.Zoom
            ? (missile ? 9f : 16f)
            : 6.5f + 4.6f * MathF.Log10(1f + MathF.Max(0f, rcs) * 10f);
        if (mode is SizeMode.Auto or SizeMode.Zoom) s *= zf;
        return s * sizeScale;
    }

    /// <summary>Tier with ±10 % hysteresis around each threshold. <paramref name="previous"/> is null for a new track.</summary>
    public static SymbolTier TierFor(float s, bool missile, SymbolTier? previous)
    {
        if (missile)
        {
            bool wasMissile = previous == SymbolTier.Missile;
            if (previous is null) return s >= MissilePx ? SymbolTier.Missile : SymbolTier.Dot;
            float t = wasMissile ? MissilePx * (1 - Hysteresis) : MissilePx * (1 + Hysteresis);
            return s >= t ? SymbolTier.Missile : SymbolTier.Dot;
        }

        int rank = previous switch { SymbolTier.Full => 2, SymbolTier.Frame => 1, SymbolTier.Dot => 0, _ => -1 };
        // A threshold the track is already above needs a 10 % drop to cross; one it is below needs a 10 % rise.
        float fullT = rank < 0 ? FullPx : rank >= 2 ? FullPx * (1 - Hysteresis) : FullPx * (1 + Hysteresis);
        float frameT = rank < 0 ? FramePx : rank >= 1 ? FramePx * (1 - Hysteresis) : FramePx * (1 + Hysteresis);
        if (s >= fullT) return SymbolTier.Full;
        if (s >= frameT) return SymbolTier.Frame;
        return SymbolTier.Dot;
    }

    /// <summary>Fills <see cref="RenderState.Items"/> with the visible tracks, their screen positions, eased sizes and tiers.</summary>
    public static void Compute(PlotScene scene, in Viewport vp, RenderState st)
    {
        var tracks = scene.Tracks;
        var opt = st.Options;
        int n = tracks.Count;
        RenderState.Ensure(ref st.Items, n);
        var items = st.Items;
        int count = 0;
        float minX = -CullMarginPx, maxX = vp.Width + CullMarginPx, minY = -CullMarginPx, maxY = vp.Height + CullMarginPx;

        st.IndexById.Clear();
        for (int i = 0; i < n; i++)
        {
            var t = tracks[i];
            st.IndexById[t.Id] = i;
            if (!t.Visible) continue;
            float sx = vp.ToScreenX(t.X), sy = vp.ToScreenY(t.Y);
            if (sx <= minX || sx >= maxX || sy <= minY || sy >= maxY) continue;
            ref var it = ref items[count++];
            it.TrackIndex = i;
            it.Id = t.Id;
            it.Sx = sx;
            it.Sy = sy;
            it.Missile = t.IsMissile;
            it.Selected = t.Selected;
        }
        st.Pills.Clear();
        if (opt.Counts) count = GroupForCounts(tracks, items, count, st);
        st.ItemCount = count;

        // Screen-space grid hash: counting sort of items into cells over the culled region.
        int cols = (int)MathF.Ceiling((maxX - minX) / CellPx) + 1;
        int rows = (int)MathF.Ceiling((maxY - minY) / CellPx) + 1;
        int cells = cols * rows;
        RenderState.Ensure(ref st.CellStart, cells + 1);
        RenderState.Ensure(ref st.CellItems, count);
        RenderState.Ensure(ref st.Order, count);
        var start = st.CellStart;
        var cellItems = st.CellItems;
        var cellOf = st.Order; // scratch: cell index per item
        Array.Clear(start, 0, cells + 1);
        for (int i = 0; i < count; i++)
        {
            int cx = (int)((items[i].Sx - minX) / CellPx), cy = (int)((items[i].Sy - minY) / CellPx);
            int c = cy * cols + cx;
            cellOf[i] = c;
            start[c + 1]++;
        }
        for (int c = 0; c < cells; c++) start[c + 1] += start[c];
        // place, using CellItems as output and a running cursor kept in SortKeys (pooled)
        RenderState.Ensure(ref st.SortKeys, cells);
        var cursor = st.SortKeys;
        for (int c = 0; c < cells; c++) cursor[c] = start[c];
        for (int i = 0; i < count; i++) cellItems[(int)cursor[cellOf[i]]++] = i;

        float zf = ZoomFactor(vp.Scale);
        float alpha = st.SnapNextFrame ? 1f : 1f - MathF.Exp(-MathF.Max(0f, st.FrameSeconds) / EaseTauSeconds);
        long frame = st.FrameIndex;

        for (int i = 0; i < count; i++)
        {
            ref var it = ref items[i];
            var t = tracks[it.TrackIndex];
            float myRcs = t.Rcs;
            bool iM = it.Missile;
            float peerFloor = myRcs / PeerRcsRatio;

            // A neighbour only matters if it is close enough to cap the size: 0.85 x distance < desired.
            // Searching just that radius gives the same result as the mockup's fixed +-2 cells, for less work.
            float desired = BaseSize(myRcs, iM, opt.SizeMode, zf, opt.SizeScale);
            float s = desired;
            if (opt.SizeMode == SizeMode.Auto)
            {
                float reach = MathF.Min(desired / SpacingFactor, CellReach * CellPx);
                float nn = reach, nn2 = reach * reach;
                int c = cellOf[i];
                int ccx = c % cols, ccy = c / cols;
                int cr = Math.Min(CellReach, (int)MathF.Ceiling(reach / CellPx));
                int x0 = Math.Max(0, ccx - cr), x1 = Math.Min(cols - 1, ccx + cr);
                int y0 = Math.Max(0, ccy - cr), y1 = Math.Min(rows - 1, ccy + cr);
                for (int gy = y0; gy <= y1; gy++)
                {
                    int row = gy * cols;
                    for (int gx = x0; gx <= x1; gx++)
                    {
                        int cell = row + gx;
                        for (int k = start[cell], e = start[cell + 1]; k < e; k++)
                        {
                            int j = cellItems[k];
                            if (j == i) continue;
                            ref var b = ref items[j];
                            float dx = b.Sx - it.Sx, dy = b.Sy - it.Sy;
                            float d2 = dx * dx + dy * dy;
                            if (d2 >= nn2) continue;
                            // only peers count: missiles and much smaller tracks never shrink a ship or an aircraft
                            if (!iM && (b.Missile || tracks[b.TrackIndex].Rcs < peerFloor)) continue;
                            nn2 = d2;
                        }
                    }
                }
                nn = MathF.Sqrt(nn2);
                s = MathF.Min(desired, MathF.Max(MinSizePx, nn * SpacingFactor));
            }
            s *= t.SizeMultiplier * (it.Selected ? SelectedFactor : 1f);
            s = MathF.Min(s, MaxSizePx);

            if (!st.Visuals.TryGetValue(t.Id, out var v))
            {
                v = new TrackVisual();
                st.Visuals[t.Id] = v;
            }
            bool fresh = v.Size < 0 || v.LastFrame < frame - 1;
            if (fresh || MathF.Abs(v.Size - s) > SnapDeltaPx) v.Size = s;
            else v.Size += (s - v.Size) * alpha;
            s = v.Size;

            var tier = TierFor(s, iM, v.HasTier && !fresh ? v.Tier : null);
            v.Tier = tier;
            v.HasTier = true;
            v.LastFrame = frame;

            it.S = s;
            it.Tier = tier;
            it.Visual = v;
            it.Badge = t.Affiliation == Affiliation.Friend
                       && t.Control is ControlState.Manual or ControlState.Agent or ControlState.Order
                       && s >= 10f && tier != SymbolTier.Dot && !iM;
            it.Half = tier switch
            {
                SymbolTier.Dot => DotRadius(s) + (t.Affiliation == Affiliation.Hostile && !iM ? 0.6f : 0f) + 0.5f,
                SymbolTier.Missile => 0.6f * MathF.Max(MissilePx, s) + 0.5f,
                _ => s / 2f + 1f,
            };
        }
    }

    /// <summary>Optional Counts view: hostile anti-ship missiles and drones collapse into one pill per 80 px cell and kind.</summary>
    private static int GroupForCounts(List<PlotTrack> tracks, DrawItem[] items, int count, RenderState st)
    {
        const float pillCell = 80f;
        int kept = 0;
        for (int i = 0; i < count; i++)
        {
            var t = tracks[items[i].TrackIndex];
            bool groupable = t.Affiliation != Affiliation.Friend && t.Kind is TrackKind.AntiShipMissile or TrackKind.Uav;
            if (!groupable) { items[kept++] = items[i]; continue; }
            bool msl = t.Kind == TrackKind.AntiShipMissile;
            long cx = (long)MathF.Floor(items[i].Sx / pillCell) + 4096, cy = (long)MathF.Floor(items[i].Sy / pillCell) + 4096;
            long key = ((msl ? 1L : 0L) << 40) | (cx << 20) | cy;
            st.Pills.TryGetValue(key, out var acc);
            acc.Missile = msl; acc.N++; acc.SumX += items[i].Sx; acc.SumY += items[i].Sy;
            st.Pills[key] = acc;
        }
        return kept;
    }

    /// <summary>The mockup's dot radius: clamp(s × 0.36, 1.5, 3).</summary>
    public static float DotRadius(float s) => Math.Clamp(s * 0.36f, 1.5f, 3f);
}
