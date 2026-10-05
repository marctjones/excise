using AwesomeAssertions;
using Excise.Core.Document;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Visual;

/// <summary>
/// Guards the size of the DeviceCMYK overprint coverage mask allocated by
/// RasterizeDeviceCmykCoverageMask (#1924). A stroke (or a dashed fill) used
/// to always request a mask the size of the WHOLE page — a boundMaskToWindow
/// condition that only ever matched an undashed fill — to overprint even a
/// hairline rule; on a US-Letter page at typical render DPI that is a 10+ MB
/// allocation for a few points of ink, and the same waste repeats for every
/// such stroke on the page. Both callers (TryPaintDeviceCmykBlendPath,
/// TryPaintDeviceCmykOverprintPath) only ever read mask pixels within their
/// own paint-bounds-derived window, so the mask never needed to be larger
/// than that window for any paint style.
/// </summary>
public sealed class DeviceCmykOverprintMaskSizeTests
{
    // The shared 300x300 fixture from OverprintRenderingTests, rendered at
    // its default 72 DPI, so the full page is 300x300 = 90,000 device
    // pixels. The 20pt-wide horizontal stroke "60 100 m 140 100 l S" spans
    // roughly 80x20 device units once stroke width and AA padding are
    // included — nowhere near the page's extent.
    private const int PageAreaPixels = 300 * 300;

    [Fact]
    public void OverprintStroke_MaskStaysBoundedToTheStrokeWindow_NotThePage()
    {
        RenderContext.RasterizedMaskPeakPixels = 0;

        using var doc = PdfDocument.Open(
            OverprintRenderingTests.BuildSinglePagePdf(
                OverprintRenderingTests.OverprintStroke,
                OverprintRenderingTests.Resources,
                deviceCmykGroup: false));
        using (new SkiaRenderer().RenderPage(
                   doc.GetPage(1),
                   new RenderOptions { Dpi = 72, BackgroundColor = SKColors.White }))
        {
        }

        var peak = RenderContext.RasterizedMaskPeakPixels;

        peak.Should().BeGreaterThan(0,
            "the overprint stroke must have engaged the DeviceCMYK direct-write path at all");
        peak.Should().BeLessThan(PageAreaPixels / 4,
            "a stroke's coverage mask must be bounded to roughly its own geometry, not the " +
            "whole page (#1924) — a full-page request here means the boundMaskToWindow-style " +
            "guard regressed to style/path-effect-gated bounding");
    }
}
