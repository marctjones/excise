using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using Excise.Rendering.Shadings;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests;

/// <summary>
/// #1386 moved the mesh shading pixel loops from per-pixel
/// <c>SKBitmap.SetPixel</c> to span stores. These tests keep a verbatim copy
/// of the old SetPixel loops and require the new rasterizer to write the same
/// bytes over seeded random geometry, including triangles and patches that
/// hang off every edge of the bitmap.
/// </summary>
public class MeshRasterizerTests
{
    private const double MinX = -3.25;
    private const double MinY = 10.5;
    private const double MaxX = 140.75;
    private const double MaxY = 88.0;

    [Fact]
    public void RasterizeTriangle_WritesTheSameBytesAsTheSetPixelLoop()
    {
        var random = new Random(1386);
        using var expected = NewBitmap(97, 61);
        using var actual = NewBitmap(97, 61);
        var target = new MeshRasterTarget(actual);

        for (var i = 0; i < 400; i++)
        {
            var a = RandomPoint(random);
            var b = RandomPoint(random);
            var c = RandomPoint(random);
            var ca = RandomColor(random);
            var cb = RandomColor(random);
            var cc = RandomColor(random);
            double triMinX = Math.Min(a.X, Math.Min(b.X, c.X));
            double triMaxX = Math.Max(a.X, Math.Max(b.X, c.X));
            double triMinY = Math.Min(a.Y, Math.Min(b.Y, c.Y));
            double triMaxY = Math.Max(a.Y, Math.Max(b.Y, c.Y));

            ReferenceRasterizeTriangle(expected, a, b, c, ca, cb, cc, triMinX, triMaxX, triMinY, triMaxY);
            MeshRasterizer.RasterizeTriangle(
                target, a, b, c, ca, cb, cc, triMinX, triMaxX, triMinY, triMaxY, MinX, MinY, MaxX, MaxY);
        }
        actual.NotifyPixelsChanged();

        var expectedBytes = expected.GetPixelSpan().ToArray();
        expectedBytes.Count(value => value != 0).Should().BeGreaterThan(expectedBytes.Length / 2,
            "the reference loop must actually paint, or byte equality proves nothing");
        actual.GetPixelSpan().ToArray().SequenceEqual(expectedBytes).Should().BeTrue(
            "span stores must be byte-identical to SKBitmap.SetPixel for mesh triangles");
    }

    [Fact]
    public void RasterizeBoundingBoxPatch_WritesTheSameBytesAsTheSetPixelLoop()
    {
        var random = new Random(7);
        using var expected = NewBitmap(83, 57);
        using var actual = NewBitmap(83, 57);
        var target = new MeshRasterTarget(actual);

        for (var i = 0; i < 60; i++)
        {
            var points = new List<SKPoint> { RandomPoint(random), RandomPoint(random), RandomPoint(random), RandomPoint(random) };
            var colors = new[] { RandomColor(random), RandomColor(random), RandomColor(random), RandomColor(random) };
            var patch = MeshPatch.From(points, colors);

            ReferenceRasterizeBoundingBoxPatch(expected, patch);
            MeshRasterizer.RasterizeBoundingBoxPatch(target, patch, MinX, MinY, MaxX, MaxY);
        }
        actual.NotifyPixelsChanged();

        var expectedBytes = expected.GetPixelSpan().ToArray();
        expectedBytes.Count(value => value != 0).Should().BeGreaterThan(expectedBytes.Length / 2,
            "the reference loop must actually paint, or byte equality proves nothing");
        actual.GetPixelSpan().ToArray().SequenceEqual(expectedBytes).Should().BeTrue(
            "span stores must be byte-identical to SKBitmap.SetPixel for the bounding-box patch fallback");
    }

    [Fact]
    public void MeshRasterTarget_RejectsBitmapsItCannotWriteByteExactly()
    {
        using var unpremul = new SKBitmap(4, 4, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bgra = new SKBitmap(4, 4, SKColorType.Bgra8888, SKAlphaType.Premul);

        var unpremulTarget = () => { _ = new MeshRasterTarget(unpremul); };
        var bgraTarget = () => { _ = new MeshRasterTarget(bgra); };

        unpremulTarget.Should().Throw<ArgumentException>();
        bgraTarget.Should().Throw<ArgumentException>();
    }

    private static SKBitmap NewBitmap(int width, int height)
    {
        var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.Transparent);
        return bitmap;
    }

    private static SKPoint RandomPoint(Random random)
        => new(
            (float)(MinX - 20 + random.NextDouble() * (MaxX - MinX + 40)),
            (float)(MinY - 20 + random.NextDouble() * (MaxY - MinY + 40)));

    private static SKColor RandomColor(Random random)
        => new((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), 255);

    // ── Verbatim copies of the pre-#1386 SetPixel loops (SkiaRenderer.Patterns.cs) ──

    private static void ReferenceRasterizeTriangle(
        SKBitmap bitmap, SKPoint a, SKPoint b, SKPoint c, SKColor colorA, SKColor colorB, SKColor colorC,
        double triangleMinX, double triangleMaxX, double triangleMinY, double triangleMaxY)
    {
        const double minX = MinX, minY = MinY, maxX = MaxX, maxY = MaxY;
        var startX = Math.Clamp((int)Math.Floor((triangleMinX - minX) / (maxX - minX) * bitmap.Width), 0, bitmap.Width - 1);
        var endX = Math.Clamp((int)Math.Ceiling((triangleMaxX - minX) / (maxX - minX) * bitmap.Width), 0, bitmap.Width - 1);
        var startY = Math.Clamp((int)Math.Floor((triangleMinY - minY) / (maxY - minY) * bitmap.Height), 0, bitmap.Height - 1);
        var endY = Math.Clamp((int)Math.Ceiling((triangleMaxY - minY) / (maxY - minY) * bitmap.Height), 0, bitmap.Height - 1);

        var denominator =
            (b.Y - c.Y) * (a.X - c.X) +
            (c.X - b.X) * (a.Y - c.Y);
        if (Math.Abs(denominator) < 1e-9)
            return;

        for (var y = startY; y <= endY; y++)
        {
            var py = minY + ((y + 0.5) / bitmap.Height) * (maxY - minY);
            for (var x = startX; x <= endX; x++)
            {
                var px = minX + ((x + 0.5) / bitmap.Width) * (maxX - minX);
                var wa = ((b.Y - c.Y) * (px - c.X) + (c.X - b.X) * (py - c.Y)) / denominator;
                var wb = ((c.Y - a.Y) * (px - c.X) + (a.X - c.X) * (py - c.Y)) / denominator;
                var wc = 1 - wa - wb;
                const double epsilon = -0.001;
                if (wa < epsilon || wb < epsilon || wc < epsilon)
                    continue;

                bitmap.SetPixel(x, y, ReferenceBarycentric(colorA, colorB, colorC, wa, wb, wc));
            }
        }
    }

    private static void ReferenceRasterizeBoundingBoxPatch(SKBitmap bitmap, MeshPatch patch)
    {
        const double minX = MinX, minY = MinY, maxX = MaxX, maxY = MaxY;
        var startX = Math.Clamp((int)Math.Floor((patch.MinX - minX) / (maxX - minX) * bitmap.Width), 0, bitmap.Width - 1);
        var endX = Math.Clamp((int)Math.Ceiling((patch.MaxX - minX) / (maxX - minX) * bitmap.Width), 0, bitmap.Width - 1);
        var startY = Math.Clamp((int)Math.Floor((patch.MinY - minY) / (maxY - minY) * bitmap.Height), 0, bitmap.Height - 1);
        var endY = Math.Clamp((int)Math.Ceiling((patch.MaxY - minY) / (maxY - minY) * bitmap.Height), 0, bitmap.Height - 1);

        for (var y = startY; y <= endY; y++)
        {
            var py = minY + ((y + 0.5) / bitmap.Height) * (maxY - minY);
            var v = patch.MaxY > patch.MinY ? (py - patch.MinY) / (patch.MaxY - patch.MinY) : 0;
            v = Math.Clamp(v, 0, 1);

            for (var x = startX; x <= endX; x++)
            {
                var px = minX + ((x + 0.5) / bitmap.Width) * (maxX - minX);
                var u = patch.MaxX > patch.MinX ? (px - patch.MinX) / (patch.MaxX - patch.MinX) : 0;
                u = Math.Clamp(u, 0, 1);
                bitmap.SetPixel(x, bitmap.Height - 1 - y, ReferenceBilinear(patch.Colors, u, v));
            }
        }
    }

    private static SKColor ReferenceBarycentric(SKColor a, SKColor b, SKColor c, double wa, double wb, double wc)
        => new(
            (byte)Math.Clamp((a.Red * wa) + (b.Red * wb) + (c.Red * wc), 0, 255),
            (byte)Math.Clamp((a.Green * wa) + (b.Green * wb) + (c.Green * wc), 0, 255),
            (byte)Math.Clamp((a.Blue * wa) + (b.Blue * wb) + (c.Blue * wc), 0, 255),
            255);

    private static SKColor ReferenceBilinear(SKColor[] colors, double u, double v)
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
