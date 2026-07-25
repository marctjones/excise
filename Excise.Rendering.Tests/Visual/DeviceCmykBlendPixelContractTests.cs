using AwesomeAssertions;
using SkiaSharp;

namespace Excise.Rendering.Tests.Visual;

/// <summary>
/// Pins the two SkiaSharp equivalences the #599 DeviceCMYK blend-path
/// optimization depends on for pixel-identical output. The hot loop in
/// RenderContext.TryPaintDeviceCmykBlendPath replaced per-pixel
/// SKBitmap.GetPixel/SetPixel P/Invokes with raw span access over
/// premultiplied Rgba8888 pixels; that is only sound while:
///   1. RenderContext.WritePremulRgba stores exactly the bytes
///      SKBitmap.SetPixel would store, and
///   2. the raw alpha byte equals GetPixel(x, y).Alpha.
/// If a SkiaSharp upgrade ever changes either contract, these tests fail
/// before the visual suite has to notice.
/// </summary>
public sealed class DeviceCmykBlendPixelContractTests
{
    [Fact]
    public void WritePremulRgba_MatchesSetPixel_ForAllAlphaChannelPairs()
    {
        using var bitmap = new SKBitmap(256, 1, SKColorType.Rgba8888, SKAlphaType.Premul);
        var expected = new byte[256 * 4];
        var actual = new byte[256 * 4];

        for (var alpha = 0; alpha <= 255; alpha++)
        {
            for (var value = 0; value <= 255; value++)
                bitmap.SetPixel(value, 0, new SKColor((byte)value, (byte)value, (byte)value, (byte)alpha));
            bitmap.GetPixelSpan().CopyTo(expected);

            for (var value = 0; value <= 255; value++)
            {
                RenderContext.WritePremulRgba(
                    actual, value * 4, (byte)value, (byte)value, (byte)value, (byte)alpha);
            }

            actual.Should().Equal(expected,
                $"WritePremulRgba must store exactly what SKBitmap.SetPixel stores (alpha={alpha})");
        }
    }

    [Fact]
    public void RawAlphaByte_MatchesGetPixelAlpha_ForPremulPixels()
    {
        using var bitmap = new SKBitmap(64, 1, SKColorType.Rgba8888, SKAlphaType.Premul);
        var random = new Random(599);

        for (var i = 0; i < 2000; i++)
        {
            var x = random.Next(64);
            var alpha = (byte)random.Next(256);
            var premul = (byte)random.Next(alpha + 1); // valid premul channel <= alpha
            var pixels = new byte[] { premul, premul, premul, alpha };

            var span = GetWritableSpan(bitmap);
            pixels.CopyTo(span.Slice(x * 4, 4));
            bitmap.NotifyPixelsChanged();

            bitmap.GetPixel(x, 0).Alpha.Should().Be(alpha,
                "premultiplication never alters the alpha byte, so raw reads must agree with GetPixel");
        }
    }

    private static unsafe Span<byte> GetWritableSpan(SKBitmap bitmap)
        => new((void*)bitmap.GetPixels(), bitmap.RowBytes * bitmap.Height);
}
