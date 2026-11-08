namespace Skizzy.Map.Render;

/// <summary>
/// Maps the km plane to screen pixels. <see cref="Scale"/> is pixels per km; screen Y grows down.
/// Matches the mockup's <c>toS</c>/<c>toW</c>.
/// </summary>
public readonly record struct Viewport(float CenterX, float CenterY, float Scale, int Width, int Height)
{
    public float ToScreenX(float x) => Width * 0.5f + (x - CenterX) * Scale;
    public float ToScreenY(float y) => Height * 0.5f - (y - CenterY) * Scale;
    public float ToWorldX(float sx) => CenterX + (sx - Width * 0.5f) / Scale;
    public float ToWorldY(float sy) => CenterY - (sy - Height * 0.5f) / Scale;

    /// <summary>Fit a world rectangle into the view with the mockup's margins (80 px across, 160 px down).</summary>
    public static Viewport Fit(float minX, float minY, float maxX, float maxY, int width, int height)
    {
        float scale = MathF.Min((width - 80f) / MathF.Max(5f, maxX - minX), (height - 160f) / MathF.Max(5f, maxY - minY));
        scale = Math.Clamp(scale, 0.35f, 60f);
        return new Viewport((minX + maxX) / 2f, (minY + maxY) / 2f, scale, width, height);
    }
}
