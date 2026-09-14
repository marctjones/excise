using SkiaSharp;

namespace Excise.Rendering.Shadings;

/// <summary>
/// Pixel target for mesh shading rasterization (ShadingType 4–7): one span over
/// a premultiplied Rgba8888 bitmap with no colour space.
/// </summary>
/// <remarks>
/// #1386: the mesh loops used <c>SKBitmap.SetPixel</c>, plus two
/// <c>SKBitmap.Width</c>/<c>Height</c> reads that each fetch <c>SKBitmap.Info</c>
/// through native code, once per pixel. On the mesh-shading-type7 bench fixture
/// that was ~51% of the page (RasterizeMeshTriangle 42.9% exclusive,
/// SKBitmap.get_Info 6.4%). Stores go through
/// <see cref="RenderContext.WritePremulRgba"/>, which
/// DeviceCmykBlendPixelContractTests pins byte-for-byte to SetPixel. Unlike
/// SetPixel, span writes do not bump the bitmap generation: the caller must
/// call <c>NotifyPixelsChanged</c> once after rasterizing.
/// </remarks>
internal readonly unsafe ref struct MeshRasterTarget
{
    private readonly Span<byte> _pixels;

    public MeshRasterTarget(SKBitmap bitmap)
    {
        var info = bitmap.Info;
        if (info.ColorType != SKColorType.Rgba8888
            || info.AlphaType != SKAlphaType.Premul
            || info.ColorSpace is not null)
        {
            throw new ArgumentException(
                "Mesh rasterization writes premultiplied Rgba8888 bytes and needs a bitmap with no colour space.",
                nameof(bitmap));
        }

        Width = info.Width;
        Height = info.Height;
        RowBytes = bitmap.RowBytes;
        _pixels = new Span<byte>((void*)bitmap.GetPixels(), RowBytes * Height);
    }

    public int Width { get; }
    public int Height { get; }
    public int RowBytes { get; }

    public void Store(int x, int y, SKColor color)
        => RenderContext.WritePremulRgba(
            _pixels,
            (y * RowBytes) + (x * 4),
            color.Red,
            color.Green,
            color.Blue,
            color.Alpha);
}

/// <summary>
/// Per-pixel mesh shading loops, moved out of SkiaRenderer.Patterns.cs for
/// #1386. The arithmetic is unchanged expression for expression (including the
/// float-typed <see cref="SKPoint"/> terms), so output is byte-identical;
/// MeshRasterizerTests pins that against a SetPixel copy of the old loops.
/// </summary>
internal static class MeshRasterizer
{
    public static void RasterizeTriangle(
        MeshRasterTarget target,
        SKPoint a,
        SKPoint b,
        SKPoint c,
        SKColor colorA,
        SKColor colorB,
        SKColor colorC,
        double triangleMinX,
        double triangleMaxX,
        double triangleMinY,
        double triangleMaxY,
        double minX,
        double minY,
        double maxX,
        double maxY)
    {
        var width = target.Width;
        var height = target.Height;
        var startX = Math.Clamp((int)Math.Floor((triangleMinX - minX) / (maxX - minX) * width), 0, width - 1);
        var endX = Math.Clamp((int)Math.Ceiling((triangleMaxX - minX) / (maxX - minX) * width), 0, width - 1);
        var startY = Math.Clamp((int)Math.Floor((triangleMinY - minY) / (maxY - minY) * height), 0, height - 1);
        var endY = Math.Clamp((int)Math.Ceiling((triangleMaxY - minY) / (maxY - minY) * height), 0, height - 1);

        var denominator =
            (b.Y - c.Y) * (a.X - c.X) +
            (c.X - b.X) * (a.Y - c.Y);
        if (Math.Abs(denominator) < 1e-9)
            return;

        for (var y = startY; y <= endY; y++)
        {
            var py = minY + ((y + 0.5) / height) * (maxY - minY);
            for (var x = startX; x <= endX; x++)
            {
                var px = minX + ((x + 0.5) / width) * (maxX - minX);
                var wa = ((b.Y - c.Y) * (px - c.X) + (c.X - b.X) * (py - c.Y)) / denominator;
                var wb = ((c.Y - a.Y) * (px - c.X) + (a.X - c.X) * (py - c.Y)) / denominator;
                var wc = 1 - wa - wb;
                const double epsilon = -0.001;
                if (wa < epsilon || wb < epsilon || wc < epsilon)
                    continue;

                target.Store(x, y, Barycentric(colorA, colorB, colorC, wa, wb, wc));
            }
        }
    }

    /// <summary>
    /// Fallback for a patch with too few control points to tessellate: fills
    /// the patch's bounding box bilinearly. Rows are stored bottom-up
    /// (<c>Height - 1 - y</c>), unlike the triangle path — kept as it was.
    /// </summary>
    public static void RasterizeBoundingBoxPatch(
        MeshRasterTarget target,
        MeshPatch patch,
        double minX,
        double minY,
        double maxX,
        double maxY)
    {
        var width = target.Width;
        var height = target.Height;
        var startX = Math.Clamp((int)Math.Floor((patch.MinX - minX) / (maxX - minX) * width), 0, width - 1);
        var endX = Math.Clamp((int)Math.Ceiling((patch.MaxX - minX) / (maxX - minX) * width), 0, width - 1);
        var startY = Math.Clamp((int)Math.Floor((patch.MinY - minY) / (maxY - minY) * height), 0, height - 1);
        var endY = Math.Clamp((int)Math.Ceiling((patch.MaxY - minY) / (maxY - minY) * height), 0, height - 1);

        for (var y = startY; y <= endY; y++)
        {
            var py = minY + ((y + 0.5) / height) * (maxY - minY);
            var v = patch.MaxY > patch.MinY ? (py - patch.MinY) / (patch.MaxY - patch.MinY) : 0;
            v = Math.Clamp(v, 0, 1);

            for (var x = startX; x <= endX; x++)
            {
                var px = minX + ((x + 0.5) / width) * (maxX - minX);
                var u = patch.MaxX > patch.MinX ? (px - patch.MinX) / (patch.MaxX - patch.MinX) : 0;
                u = Math.Clamp(u, 0, 1);
                target.Store(x, height - 1 - y, Bilinear(patch.Colors, u, v));
            }
        }
    }

    public static SKColor Barycentric(SKColor a, SKColor b, SKColor c, double wa, double wb, double wc)
    {
        return new SKColor(
            (byte)Math.Clamp((a.Red * wa) + (b.Red * wb) + (c.Red * wc), 0, 255),
            (byte)Math.Clamp((a.Green * wa) + (b.Green * wb) + (c.Green * wc), 0, 255),
            (byte)Math.Clamp((a.Blue * wa) + (b.Blue * wb) + (c.Blue * wc), 0, 255),
            255);
    }

    public static SKColor Bilinear(SKColor[] colors, double u, double v)
    {
        static double Lerp(double a, double b, double t) => a + (b - a) * t;
        var r0 = Lerp(colors[0].Red, colors[1].Red, u);
        var r1 = Lerp(colors[3].Red, colors[2].Red, u);
        var g0 = Lerp(colors[0].Green, colors[1].Green, u);
        var g1 = Lerp(colors[3].Green, colors[2].Green, u);
        var b0 = Lerp(colors[0].Blue, colors[1].Blue, u);
        var b1 = Lerp(colors[3].Blue, colors[2].Blue, u);
        return new SKColor(
            (byte)Math.Clamp(Lerp(r0, r1, v), 0, 255),
            (byte)Math.Clamp(Lerp(g0, g1, v), 0, 255),
            (byte)Math.Clamp(Lerp(b0, b1, v), 0, 255),
            255);
    }
}
