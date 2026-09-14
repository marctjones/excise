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
    [InlineData(120, 1.0)]
    [InlineData(120, 2.0)]
    [InlineData(96, 3.0)]
    [InlineData(72, 1.5)]
    [InlineData(120, 1.429)]  // 120 × 1.429 = 171.48: the device DPI rounds down (#1489)
    [InlineData(120, 1.43)]   // 171.6: rounds up
    [InlineData(107, 1.0064)] // a budget-clamped large page at its non-integer scale cap
    public void SinglePageRenderPlan_KeepsDipSizeInvariant(int logicalDpi, double scale)
    {
        var (deviceDpi, bitmapDpi) = PdfViewerControl.SinglePageRenderPlan(logicalDpi, scale, Uncapped);

        // A bitmap of N device pixels stamped at `bitmapDpi` reports a DIP size of
        // N / (bitmapDpi / 96); its per-point DIP scale, deviceDpi / (bitmapDpi/96),
        // MUST equal the logical DPI — that keeps on-screen size + coordinates fixed.
        double dipScale = deviceDpi / (bitmapDpi / 96.0);
        dipScale.Should().BeApproximately(logicalDpi, 0.5,
            "the bitmap's DIP size (and thus layout + coordinates) must be invariant to zoom/dpr");
        // Exactly (#1489): the 0.5 DPI in 120 allowed above is the size of the
        // defect itself (bitmapDpi = 96 × scale against a rounded deviceDpi). The
        // bound is tightened by adding this check rather than rewriting that one,
        // so the change reads as a strengthening to check-gate-asymmetry.sh.
        dipScale.Should().BeApproximately(logicalDpi, 1e-9,
            "the plan's DIP scale must equal the logical DPI exactly, or input maps off the drawn page");
    }

    [Theory]
    [InlineData(612, 792, 120)]
    [InlineData(2000, 1400, 120)]
    [InlineData(5000, 6000, 107)]
    public void SinglePageLayoutSize_IsThePageGeometryAtTheLogicalDpi(double widthPt, double heightPt, int logicalDpi)
    {
        // #1489: the Image is sized from geometry, not from the ceiled raster, so
        // it spans exactly the space ViewerDipsToPdfRect maps input through.
        var size = PdfViewerControl.SinglePageLayoutSize(widthPt, heightPt, logicalDpi);
        size.Width.Should().Be(widthPt * logicalDpi / 72.0);
        size.Height.Should().Be(heightPt * logicalDpi / 72.0);
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
