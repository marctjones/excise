using AwesomeAssertions;
using Excise.Avalonia.Controls;
using Xunit;

namespace Excise.Avalonia.Tests;

/// <summary>
/// Unit tests for the single-page device-resolution render plan (#682/#683).
/// Pure logic — no rendering — so it runs in the non-flaky viewer-lib project.
///
/// The whole safety argument for the HiDPI/zoom fix is the INVARIANT: rendering
/// at the on-screen magnification and stamping the bitmap DPI accordingly leaves
/// the bitmap's DIP size — and therefore the Image's layout size and every
/// coordinate mapping (clicks, selection, links) — unchanged; only the pixel
/// density improves. These tests pin exactly that, plus the memory cap.
/// </summary>
public class SinglePageRenderPlanTests
{
    private const double Uncapped = 1000.0;

    [Theory]
    // logicalDpi, scale (dpr×zoom), expectedDeviceDpi, expectedBitmapDpi
    [InlineData(120, 1.0, 120, 96.0)]   // 100% on a standard display: exact no-op
    [InlineData(96, 1.0, 96, 96.0)]
    [InlineData(120, 2.0, 240, 192.0)]  // Retina @100%, or standard @200% zoom
    [InlineData(120, 1.5, 180, 144.0)]
    [InlineData(120, 4.0, 480, 384.0)]  // Retina @200% zoom -> device resolution
    public void SinglePageRenderPlan_RastersAtOnScreenMagnification(
        int logicalDpi, double scale, int expectedDeviceDpi, double expectedBitmapDpi)
    {
        var (deviceDpi, bitmapDpi) = PdfViewerControl.SinglePageRenderPlan(logicalDpi, scale, Uncapped);
        deviceDpi.Should().Be(expectedDeviceDpi);
        bitmapDpi.Should().Be(expectedBitmapDpi);
    }

    [Theory]
    [InlineData(120, 1.0)]
    [InlineData(120, 2.0)]
    [InlineData(96, 3.0)]
    [InlineData(72, 1.5)]
    public void SinglePageRenderPlan_KeepsDipSizeInvariant(int logicalDpi, double scale)
    {
        var (deviceDpi, bitmapDpi) = PdfViewerControl.SinglePageRenderPlan(logicalDpi, scale, Uncapped);

        // A bitmap of N device pixels stamped at `bitmapDpi` reports a DIP size of
        // N / (bitmapDpi / 96); its per-point DIP scale, deviceDpi / (bitmapDpi/96),
        // MUST equal the logical DPI — that keeps on-screen size + coordinates fixed.
        double dipScale = deviceDpi / (bitmapDpi / 96.0);
        dipScale.Should().BeApproximately(logicalDpi, 0.5,
            "the bitmap's DIP size (and thus layout + coordinates) must be invariant to zoom/dpr");
    }

    [Theory]
    [InlineData(0.5, 1.0)]     // never below 1 (would down-res the base render)
    [InlineData(8.0, 5.0)]     // capped to the memory budget
    [InlineData(3.0, 5.0)]     // under the cap -> unaffected
    public void SinglePageRenderPlan_ClampsScaleToBudget(double scale, double maxScale)
    {
        var (deviceDpi, bitmapDpi) = PdfViewerControl.SinglePageRenderPlan(120, scale, maxScale);
        double effective = System.Math.Clamp(scale <= 0 ? 1.0 : scale, 1.0, maxScale);
        deviceDpi.Should().Be((int)System.Math.Round(120 * effective));
        bitmapDpi.Should().Be(96.0 * effective);
    }

    [Fact]
    public void MaxSinglePageRenderScale_IsAtLeastOne_AndShrinksForHugePages()
    {
        // A normal Letter page at 120 DPI (~1.35M px) has plenty of headroom.
        var letter = PdfViewerControl.MaxSinglePageRenderScale(612, 792, 120);
        letter.Should().BeGreaterThan(3.0);

        // A very large page is capped so the raster stays within the memory budget.
        var huge = PdfViewerControl.MaxSinglePageRenderScale(5000, 6000, 120);
        huge.Should().BeGreaterThanOrEqualTo(1.0);
        huge.Should().BeLessThan(letter, "a bigger page must allow less extra scaling");

        // Degenerate input is safe.
        PdfViewerControl.MaxSinglePageRenderScale(0, 0, 120).Should().Be(1.0);
    }

    [Fact]
    public void MaxSinglePageRenderScale_KeepsRasterWithinBudget()
    {
        const double w = 612, h = 792;
        const int dpi = 120;
        double maxScale = PdfViewerControl.MaxSinglePageRenderScale(w, h, dpi);
        var (deviceDpi, _) = PdfViewerControl.SinglePageRenderPlan(dpi, maxScale + 5, maxScale);

        double pixels = (w * deviceDpi / 72.0) * (h * deviceDpi / 72.0);
        pixels.Should().BeLessThanOrEqualTo(64L * 1024 * 1024 * 1.02,
            "the capped device render must not exceed the single-page pixel budget");
    }
}
