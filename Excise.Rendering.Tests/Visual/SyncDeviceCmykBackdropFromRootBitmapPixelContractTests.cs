using AwesomeAssertions;
using SkiaSharp;

namespace Excise.Rendering.Tests.Visual;

/// <summary>
/// Pins the equivalences #1425/#1426's raw-span rewrite of
/// <c>RenderContext.SyncDeviceCmykBackdropFromRootBitmap</c> depends on. The
/// old code read <c>SKBitmap.GetPixel(x, y)</c> (straight/unpremultiplied
/// RGB) for both its skip-threshold comparison and its backdrop-estimate
/// formula; the new code reads the raw premultiplied bytes directly and
/// recovers straight values via <see cref="RenderContext.UnpremultiplyChannel"/>.
///
/// An earlier version of this fix tried to avoid unpremultiplying entirely
/// by re-deriving the formula in premultiplied terms and premultiplying the
/// comparison's other side too. That is NOT equivalent — differences shrink
/// roughly proportionally to alpha in premultiplied space but not in
/// straight space, so the same straight-space difference produces a smaller
/// premultiplied-space difference at low alpha, silently loosening the
/// 12-unit skip threshold exactly where it matters least visibly. The first
/// test below is what caught that (kept in spirit, retuned to the corrected
/// implementation); the second measures how close the ACTUAL fix's
/// unpremultiply comes to Skia's own.
/// </summary>
public sealed class SyncDeviceCmykBackdropFromRootBitmapPixelContractTests
{
    [Fact]
    public void UnpremultiplyChannel_MatchesGetPixel_ToWithinOneStep_ForAllAlphaValuePairs()
    {
        using var bitmap = new SKBitmap(256, 1, SKColorType.Rgba8888, SKAlphaType.Premul);
        var maxDiff = 0;
        var mismatches = 0;

        for (var alpha = 1; alpha <= 255; alpha++)
        {
            for (var value = 0; value <= 255; value++)
                bitmap.SetPixel(value, 0, new SKColor((byte)value, (byte)value, (byte)value, (byte)alpha));

            var rawPixels = GetReadableSpan(bitmap);
            for (var value = 0; value <= 255; value++)
            {
                var raw = rawPixels[value * 4];
                var expected = bitmap.GetPixel(value, 0).Red;
                var actual = RenderContext.UnpremultiplyChannel(raw, (byte)alpha);
                var diff = Math.Abs(actual - expected);
                if (diff > 0)
                {
                    mismatches++;
                    maxDiff = Math.Max(maxDiff, diff);
                }
            }
        }

        // Measured baseline (2026-09-08): 322/65280 pairs differ, always by
        // exactly 1 -- Skia's own unpremultiply uses a different tie-break
        // than round(raw*255/alpha), not a fundamentally different result.
        maxDiff.Should().BeLessThanOrEqualTo(1, "UnpremultiplyChannel must never diverge from GetPixel by more than one 8-bit step");
        mismatches.Should().BeLessThanOrEqualTo(400, "the measured mismatch count (322) should not regress substantially");
    }

    [Fact]
    public void SkipThresholdDecision_AgreesWithGetPixelBasedComparison_ForRandomPixelsAndRetainedEstimates()
    {
        // The <= 12 skip-threshold check's straight-space comparison must
        // agree with the original GetPixel-based comparison for the actual
        // fix (UnpremultiplyChannel-recovered straight values), across a
        // wide spread of rendered-pixel and retained-estimate combinations
        // (independent of each other -- the retained estimate comes from
        // the CMYK backdrop tracker, not from this pixel).
        var random = new Random(1425);
        using var bitmap = new SKBitmap(1, 1, SKColorType.Rgba8888, SKAlphaType.Premul);
        var mismatches = 0;
        const int trials = 200_000;

        for (var i = 0; i < trials; i++)
        {
            byte r = (byte)random.Next(256);
            byte g = (byte)random.Next(256);
            byte b = (byte)random.Next(256);
            byte alpha = (byte)random.Next(256);
            byte retainedR = (byte)random.Next(256);
            byte retainedG = (byte)random.Next(256);
            byte retainedB = (byte)random.Next(256);

            bitmap.SetPixel(0, 0, new SKColor(r, g, b, alpha));
            var pixel = bitmap.GetPixel(0, 0);
            var rawPixels = GetReadableSpan(bitmap);

            var oldSum = Math.Abs(pixel.Red - retainedR) + Math.Abs(pixel.Green - retainedG) + Math.Abs(pixel.Blue - retainedB);
            var oldSkip = oldSum <= 12;

            var rawAlpha = rawPixels[3];
            var straightR = RenderContext.UnpremultiplyChannel(rawPixels[0], rawAlpha);
            var straightG = RenderContext.UnpremultiplyChannel(rawPixels[1], rawAlpha);
            var straightB = RenderContext.UnpremultiplyChannel(rawPixels[2], rawAlpha);
            var newSum = Math.Abs(straightR - retainedR) + Math.Abs(straightG - retainedG) + Math.Abs(straightB - retainedB);
            var newSkip = newSum <= 12;

            if (newSkip != oldSkip)
                mismatches++;
        }

        // Measured baseline (2026-09-08): 1/200,000 -- an exact tie at the
        // threshold (oldSum == 12), inherent to the fact this is already an
        // approximate "close enough" heuristic, not a hard invariant.
        mismatches.Should().BeLessThanOrEqualTo(5,
            $"skip-decision agreement must stay near the measured 1-in-200000 tie rate (got {mismatches}/{trials})");
    }

    [Fact]
    public void RawAlphaByte_MatchesGetPixelAlpha_ForPremulPixels()
    {
        using var bitmap = new SKBitmap(64, 1, SKColorType.Rgba8888, SKAlphaType.Premul);
        var random = new Random(1426);

        for (var i = 0; i < 2000; i++)
        {
            var x = random.Next(64);
            var alpha = (byte)random.Next(256);
            var premul = (byte)random.Next(alpha + 1);
            var pixels = new byte[] { premul, premul, premul, alpha };

            var span = GetReadableSpan(bitmap);
            pixels.CopyTo(span.Slice(x * 4, 4));
            bitmap.NotifyPixelsChanged();

            bitmap.GetPixel(x, 0).Alpha.Should().Be(alpha,
                "premultiplication never alters the alpha byte, so raw reads must agree with GetPixel");
        }
    }

    private static unsafe Span<byte> GetReadableSpan(SKBitmap bitmap)
        => new((void*)bitmap.GetPixels(), bitmap.RowBytes * bitmap.Height);
}
