namespace Skizzy.Map.Render.Model;

/// <summary>
/// Fixed-capacity ring of past positions (km), oldest first. Adding never allocates.
/// The mockup samples at 1 Hz and keeps 30 points.
/// </summary>
public sealed class TrailBuffer
{
    private readonly float[] _xy;
    private int _start;

    public TrailBuffer(int capacity = 30)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
        _xy = new float[capacity * 2];
    }

    public int Capacity { get; }
    public int Count { get; private set; }

    public void Add(float x, float y)
    {
        int slot;
        if (Count < Capacity) { slot = (_start + Count) % Capacity; Count++; }
        else { slot = _start; _start = (_start + 1) % Capacity; }
        _xy[slot * 2] = x;
        _xy[slot * 2 + 1] = y;
    }

    public void Clear() { _start = 0; Count = 0; }

    /// <summary>Point <paramref name="i"/>, where 0 is the oldest.</summary>
    public (float X, float Y) this[int i]
    {
        get
        {
            if ((uint)i >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(i));
            int slot = (_start + i) % Capacity;
            return (_xy[slot * 2], _xy[slot * 2 + 1]);
        }
    }
}
