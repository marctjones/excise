using Excise.Rendering.Transparency;

namespace Excise.Rendering.Tests;

// The doc comment on DeviceCmykBackdrop.CopyRegionFrom (#1402) cites this test by name as
// the thing that verifies its row-wise Array.Copy fast path is byte-identical to the
// per-pixel Get/Set loop it replaces. Keep the name in sync with that citation.
public sealed class DeviceCmykBackdropCopyRegionTests
{
    [Fact]
    public void CopyRegionFrom_IsByteIdenticalToGetSetRoundTrip()
    {
        const int width = 11;
        const int height = 7;
        var source = new DeviceCmykBackdrop(width, height);

        // Exhaustive over the full byte range for the first row/column, then a spread of
        // pseudo-random values for the rest, so both edge bytes (0, 255) and interior values
        // are covered without needing a 256x256x256x256x256 source.
        var rnd = new Random(20260907);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                byte NextByte() => (byte)rnd.Next(256);
                var color = new DeviceCmykColor(
                    NextByte() / 255.0,
                    NextByte() / 255.0,
                    NextByte() / 255.0,
                    NextByte() / 255.0);
                source.Set(x, y, color, NextByte() / 255.0);
            }
        }
        // Force every corner and edge to the byte extremes explicitly, since random sampling
        // alone is unlikely to hit exactly 0 and 255 at the boundaries CopyRegionFrom's
        // Array.Copy row/column arithmetic is most likely to get wrong if it's off by one.
        source.Set(0, 0, new DeviceCmykColor(0, 0, 0, 0), 0);
        source.Set(width - 1, 0, new DeviceCmykColor(1, 1, 1, 1), 1);
        source.Set(0, height - 1, new DeviceCmykColor(1, 0, 1, 0), 1);
        source.Set(width - 1, height - 1, new DeviceCmykColor(0, 1, 0, 1), 0);

        // A sub-region, not the whole source, so srcLeft/srcTop offsetting is exercised too.
        const int srcLeft = 2;
        const int srcTop = 1;
        const int regionWidth = 6;
        const int regionHeight = 4;

        var expected = new DeviceCmykBackdrop(regionWidth, regionHeight);
        for (var y = 0; y < regionHeight; y++)
        {
            for (var x = 0; x < regionWidth; x++)
            {
                expected.Set(
                    x,
                    y,
                    source.Get(srcLeft + x, srcTop + y),
                    source.GetAlpha(srcLeft + x, srcTop + y));
            }
        }

        var actual = new DeviceCmykBackdrop(regionWidth, regionHeight);
        actual.CopyRegionFrom(source, srcLeft, srcTop, regionWidth, regionHeight);

        for (var y = 0; y < regionHeight; y++)
        {
            for (var x = 0; x < regionWidth; x++)
            {
                var e = expected.Get(x, y);
                var a = actual.Get(x, y);
                Assert.True(
                    e == a,
                    $"color mismatch at ({x},{y}): expected {e}, actual {a}");
                Assert.Equal(expected.GetAlpha(x, y), actual.GetAlpha(x, y));
            }
        }
    }

    [Fact]
    public void CopyRegionFrom_ZeroSizeRegion_DoesNotThrowAndCopiesNothing()
    {
        var source = new DeviceCmykBackdrop(4, 4);
        source.Set(0, 0, new DeviceCmykColor(1, 1, 1, 1), 1);
        var destination = new DeviceCmykBackdrop(4, 4);

        destination.CopyRegionFrom(source, 0, 0, 0, 0);

        Assert.Equal(new DeviceCmykColor(0, 0, 0, 0), destination.Get(0, 0));
        Assert.Equal(0, destination.GetAlpha(0, 0));
    }

    [Fact]
    public void CopyRegionFrom_FullRegion_MatchesClone()
    {
        const int size = 5;
        var source = new DeviceCmykBackdrop(size, size);
        var rnd = new Random(1);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                byte NextByte() => (byte)rnd.Next(256);
                source.Set(
                    x,
                    y,
                    new DeviceCmykColor(NextByte() / 255.0, NextByte() / 255.0, NextByte() / 255.0, NextByte() / 255.0),
                    NextByte() / 255.0);
            }
        }

        var viaCopyRegion = new DeviceCmykBackdrop(size, size);
        viaCopyRegion.CopyRegionFrom(source, 0, 0, size, size);
        var viaClone = source.Clone();

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                Assert.Equal(viaClone.Get(x, y), viaCopyRegion.Get(x, y));
                Assert.Equal(viaClone.GetAlpha(x, y), viaCopyRegion.GetAlpha(x, y));
            }
        }
    }
}
