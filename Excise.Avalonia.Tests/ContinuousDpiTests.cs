using System;
using AwesomeAssertions;
using Avalonia;
using Excise.Avalonia.Controls;
using Xunit;

namespace Excise.Avalonia.Tests;

/// <summary>
/// Unit tests for the zoom-aware continuous-render DPI selection (#371 pt1).
/// Pure logic — no rendering — so it runs in the non-flaky viewer-lib project.
/// </summary>
public class ContinuousDpiTests
{
    [Theory]
    // zoom, renderScaling (device-pixel-ratio), expected DPI
    // --- standard display (dpr 1.0): behaviour is unchanged from before #682 ---
    [InlineData(1.0, 1.0, 120)]   // at 100% zoom, render at the base DPI
    [InlineData(1.5, 1.0, 180)]   // scales with zoom -> crisper
    [InlineData(2.0, 1.0, 240)]   // at the cap
    [InlineData(4.0, 1.0, 240)]   // deep zoom is clamped to the cap (bounds memory)
    [InlineData(0.5, 1.0, 120)]   // never below the base DPI
    // --- HiDPI / Retina (dpr 2.0): render scales with the device pixel ratio (#682/#683) ---
    [InlineData(1.0, 2.0, 240)]   // 100% on a 2x display -> 2x pixels = crisp, not upscaled
    [InlineData(1.5, 2.0, 360)]
    [InlineData(2.0, 2.0, 480)]   // the cap scales with dpr, so the same *visual* zoom stays crisp
    [InlineData(4.0, 2.0, 480)]   // clamped to the dpr-scaled cap
    [InlineData(0.5, 2.0, 240)]   // floor also scales with dpr
    public void EffectiveContinuousDpi_ScalesWithZoomAndDpr_AndClamps(double zoom, double dpr, int expected)
        => PdfViewerControl.EffectiveContinuousDpi(120, zoom, PdfViewerControl.MaxContinuousDpi, dpr)
            .Should().Be(expected);

    [Fact]
    public void TryCreateContinuousTileRequest_WhenViewportCoversPage_UsesFullPageClip()
    {
        var slot = new PdfPageSlot(pageNumber: 1, widthPt: 100, heightPt: 200, zoom: 1.0);

        var ok = PdfViewerControl.TryCreateContinuousTileRequest(
            slot,
            viewportOffset: new Vector(0, 0),
            viewport: new Size(500, 500),
            pageTop: 0,
            zoom: 1.0,
            out var request);

        ok.Should().BeTrue();
        request.XDip.Should().Be(0);
        request.YDip.Should().Be(0);
        request.WidthDip.Should().Be(134);
        request.HeightDip.Should().Be(267);
        request.ClipRect.Left.Should().BeApproximately(0, 0.001f);
        request.ClipRect.Top.Should().BeApproximately(0, 0.001f);
        request.ClipRect.Right.Should().BeApproximately(100, 0.001f);
        request.ClipRect.Bottom.Should().BeApproximately(200, 0.001f);
    }

    /// <summary>
    /// The tile request's CONTRACT, stated as invariants rather than pinned
    /// numbers (#617).
    /// </summary>
    /// <remarks>
    /// This test used to assert eight exact values (XDip == 256, ClipRect.Left ==
    /// 192f, …). Every one of them was an artifact of the current quantization and
    /// overscan constants, not of what the tile is *for*. So when the perf change
    /// in 8a8e661 altered those constants, it simply **rewrote the expected values
    /// of a correctness test** — in the same commit, with no signal. That is the
    /// mechanism by which optimization quietly redefines "correct".
    ///
    /// A pinned-value test can neither survive a legal change nor catch an illegal
    /// one. These invariants do both: they hold before and after quantization or
    /// overscan is tuned, and they fail loudly on a tile that under-covers the
    /// viewport (blank strips), escapes the page, disagrees with its own clip rect,
    /// or balloons in area (the memory cost tracked in #615).
    /// </remarks>
    [Theory]
    // viewport offset X/Y, viewport W/H, pageTop, zoom
    [InlineData(700, 1_000, 400, 600, 100, 1.0)]
    [InlineData(0, 0, 800, 600, 0, 1.0)]
    [InlineData(1_200, 2_400, 500, 500, 0, 2.0)]
    [InlineData(37, 913, 333, 271, 55, 0.5)]     // deliberately unaligned
    public void ContinuousTileRequest_SatisfiesItsContract(
        double offsetX, double offsetY, double viewW, double viewH, double pageTop, double zoom)
    {
        const double widthPt = 2_000, heightPt = 3_000;
        var slot = new PdfPageSlot(pageNumber: 2, widthPt: widthPt, heightPt: heightPt, zoom: zoom);

        var ok = PdfViewerControl.TryCreateContinuousTileRequest(
            slot,
            viewportOffset: new Vector(offsetX, offsetY),
            viewport: new Size(viewW, viewH),
            pageTop: pageTop,
            zoom: zoom,
            out var request);

        ok.Should().BeTrue("the viewport intersects the page in every case here");

        // The visible slice of THIS page, in page-local dip coordinates.
        double visibleLeft   = Math.Max(0, offsetX);
        double visibleTop    = Math.Max(0, offsetY - pageTop);
        double visibleRight  = Math.Min(slot.DisplayWidth,  offsetX + viewW);
        double visibleBottom = Math.Min(slot.DisplayHeight, offsetY - pageTop + viewH);

        // 1. COVERAGE — the rendered tile must contain everything the user can see.
        //    Under-cover by a single pixel and the user gets a blank strip.
        ((double)request.XDip).Should().BeLessThanOrEqualTo(visibleLeft,
            "the tile must start at or before the first visible pixel");
        ((double)request.YDip).Should().BeLessThanOrEqualTo(visibleTop,
            "the tile must start at or above the first visible pixel");
        ((double)(request.XDip + request.WidthDip)).Should().BeGreaterThanOrEqualTo(visibleRight,
            "the tile must extend past the last visible pixel");
        ((double)(request.YDip + request.HeightDip)).Should().BeGreaterThanOrEqualTo(visibleBottom,
            "the tile must extend past the last visible pixel");

        // 2. CONTAINMENT — and never render outside the page.
        request.XDip.Should().BeGreaterThanOrEqualTo(0);
        request.YDip.Should().BeGreaterThanOrEqualTo(0);
        ((double)(request.XDip + request.WidthDip)).Should().BeLessThanOrEqualTo(slot.DisplayWidth + 0.001);
        ((double)(request.YDip + request.HeightDip)).Should().BeLessThanOrEqualTo(slot.DisplayHeight + 0.001);

        // 3. SELF-CONSISTENCY — the PDF-point clip rect must describe the same
        //    region as the dip tile. If these ever disagree, we render one part of
        //    the page and display it as another: content appears in the wrong place.
        double dipPerPoint = PdfViewerControl.PointsToDip * zoom;
        request.ClipRect.Left.Should().BeApproximately((float)(request.XDip / dipPerPoint), 0.01f);
        request.ClipRect.Right.Should().BeApproximately(
            (float)((request.XDip + request.WidthDip) / dipPerPoint), 0.01f);
        (request.ClipRect.Bottom - request.ClipRect.Top).Should().BeApproximately(
            (float)(request.HeightDip / dipPerPoint), 0.01f,
            "the clip's height in points must match the tile's height in dips");

        // 4. BOUNDEDNESS — overscan is allowed to be generous, but not unbounded.
        //    Tiles are cached (ContinuousCacheCapacity), so their area IS memory
        //    (#615). This is the assertion that would have priced the perf trade.
        double visibleArea = Math.Max(1, (visibleRight - visibleLeft) * (visibleBottom - visibleTop));
        double tileArea = request.WidthDip * request.HeightDip;
        (tileArea / visibleArea).Should().BeLessThan(64,
            "a tile may overscan, but not by an unbounded multiple of what is on screen — " +
            "cached tile area is memory, and nothing else in the codebase bounds it");
    }

    [Fact]
    public void TryCreateContinuousTileRequest_WhenViewportMissesPage_ReturnsFalse()
    {
        var slot = new PdfPageSlot(pageNumber: 2, widthPt: 100, heightPt: 200, zoom: 1.0);

        var ok = PdfViewerControl.TryCreateContinuousTileRequest(
            slot,
            viewportOffset: new Vector(0, 0),
            viewport: new Size(100, 100),
            pageTop: 500,
            zoom: 1.0,
            out _);

        ok.Should().BeFalse();
    }
}
