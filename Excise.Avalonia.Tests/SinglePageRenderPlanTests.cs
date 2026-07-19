using AwesomeAssertions;
using Excise.Avalonia.Controls;
using Xunit;

namespace Excise.Avalonia.Tests;

/// <summary>
/// Unit tests for the single-page device-resolution render plan (#682).
/// Pure logic — no rendering — so it runs in the non-flaky viewer-lib project.
///
/// The whole safety argument for the HiDPI fix is the INVARIANT: rendering at
/// device resolution and stamping the bitmap DPI accordingly leaves the bitmap's
/// DIP size — and therefore the Image's layout size and every coordinate mapping
/// (clicks, selection, links) — unchanged; only the pixel density improves. These
/// tests pin exactly that, so a future edit that breaks the invariant (and would
/// silently mis-place content or clicks) fails loudly.
/// </summary>
public class SinglePageRenderPlanTests
{
    [Theory]
    // logicalDpi, dpr, expectedDeviceDpi, expectedBitmapDpi
    [InlineData(120, 1.0, 120, 96.0)]   // standard display: exact no-op
    [InlineData(96, 1.0, 96, 96.0)]
    [InlineData(120, 2.0, 240, 192.0)]  // Retina: 2x pixels, stamped 2x DPI
    [InlineData(96, 2.0, 192, 192.0)]
    [InlineData(120, 1.5, 180, 144.0)]  // fractional scaling
    [InlineData(120, 3.0, 360, 288.0)]
    public void SinglePageRenderPlan_RendersAtDeviceResolution(
        int logicalDpi, double dpr, int expectedDeviceDpi, double expectedBitmapDpi)
    {
        var (deviceDpi, bitmapDpi) = PdfViewerControl.SinglePageRenderPlan(logicalDpi, dpr);
        deviceDpi.Should().Be(expectedDeviceDpi);
        bitmapDpi.Should().Be(expectedBitmapDpi);
    }

    [Theory]
    [InlineData(120, 1.0)]
    [InlineData(120, 2.0)]
    [InlineData(96, 2.0)]
    [InlineData(72, 1.5)]
    [InlineData(300, 2.0)]
    public void SinglePageRenderPlan_KeepsDipSizeInvariant(int logicalDpi, double dpr)
    {
        var (deviceDpi, bitmapDpi) = PdfViewerControl.SinglePageRenderPlan(logicalDpi, dpr);

        // A bitmap of N device pixels stamped at `bitmapDpi` reports a DIP size of
        // N / (bitmapDpi / 96). Its per-point DIP scale is therefore
        // deviceDpi / (bitmapDpi / 96), which MUST equal the logical DPI — that is
        // what keeps the on-screen size and all coordinate mapping unchanged.
        double dipScale = deviceDpi / (bitmapDpi / 96.0);
        dipScale.Should().BeApproximately(logicalDpi, 0.5,
            "the bitmap's DIP size (and thus layout + coordinates) must be invariant to the device-pixel-ratio");
    }

    [Theory]
    [InlineData(0.0, 1.0)]    // unattached / bogus -> treated as 1.0
    [InlineData(-2.0, 1.0)]
    [InlineData(8.0, 4.0)]    // clamped to a sane upper bound
    public void SinglePageRenderPlan_ClampsDpr(double dpr, double effectiveDpr)
    {
        var (deviceDpi, bitmapDpi) = PdfViewerControl.SinglePageRenderPlan(120, dpr);
        deviceDpi.Should().Be((int)(120 * effectiveDpr));
        bitmapDpi.Should().Be(96.0 * effectiveDpr);
    }
}
