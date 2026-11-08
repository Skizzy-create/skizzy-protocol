namespace Skizzy.Map.Render.Layout;

/// <summary>
/// Uniform-grid index of occupied screen rectangles, rebuilt every frame from pooled arrays.
/// Used by label placement so each candidate tests only nearby symbols and labels.
/// </summary>
internal sealed class RectGrid
{
    private const float Cell = 48f;
    private const float Margin = 96f;

    private int _cols, _rows;
    private float _ox, _oy;
    private int[] _head = new int[64];
    private int[] _next = new int[256];
    private int[] _rectOf = new int[256];
    private int _nodes;
    private ScreenRect[] _rects = new ScreenRect[256];
    private int _rectCount;

    public void Reset(int width, int height)
    {
        _ox = -Margin; _oy = -Margin;
        _cols = (int)MathF.Ceiling((width + 2 * Margin) / Cell) + 1;
        _rows = (int)MathF.Ceiling((height + 2 * Margin) / Cell) + 1;
        RenderState.Ensure(ref _head, _cols * _rows);
        Array.Fill(_head, -1, 0, _cols * _rows);
        _nodes = 0;
        _rectCount = 0;
    }

    public void Add(in ScreenRect r)
    {
        RenderState.Ensure(ref _rects, _rectCount + 1);
        int idx = _rectCount++;
        _rects[idx] = r;
        CellRange(r, out int x0, out int y0, out int x1, out int y1);
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                if (_nodes == _next.Length) { Array.Resize(ref _next, _nodes * 2); Array.Resize(ref _rectOf, _nodes * 2); }
                int c = y * _cols + x;
                _rectOf[_nodes] = idx;
                _next[_nodes] = _head[c];
                _head[c] = _nodes++;
            }
    }

    public bool Overlaps(in ScreenRect r)
    {
        CellRange(r, out int x0, out int y0, out int x1, out int y1);
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
                for (int n = _head[y * _cols + x]; n >= 0; n = _next[n])
                    if (_rects[_rectOf[n]].Intersects(r)) return true;
        return false;
    }

    private void CellRange(in ScreenRect r, out int x0, out int y0, out int x1, out int y1)
    {
        x0 = Math.Clamp((int)((r.X - _ox) / Cell), 0, _cols - 1);
        x1 = Math.Clamp((int)((r.Right - _ox) / Cell), 0, _cols - 1);
        y0 = Math.Clamp((int)((r.Y - _oy) / Cell), 0, _rows - 1);
        y1 = Math.Clamp((int)((r.Bottom - _oy) / Cell), 0, _rows - 1);
    }
}
