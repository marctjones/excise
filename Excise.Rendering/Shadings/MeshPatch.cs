using SkiaSharp;

namespace Excise.Rendering.Shadings;

internal sealed class MeshPatch
{
    private MeshPatch(IReadOnlyList<SKPoint> points, SKColor[] colors)
    {
        Points = points;
        Colors = colors;
        MinX = points.Min(p => p.X);
        MaxX = points.Max(p => p.X);
        MinY = points.Min(p => p.Y);
        MaxY = points.Max(p => p.Y);
    }

    public IReadOnlyList<SKPoint> Points { get; }
    public SKColor[] Colors { get; }
    public double MinX { get; }
    public double MaxX { get; }
    public double MinY { get; }
    public double MaxY { get; }

    public static MeshPatch From(IReadOnlyList<SKPoint> points, SKColor[] colors) => new(points, colors);
}
