using System;
using AwesomeAssertions;
using Avalonia;
using Excise.Avalonia.Controls;
using Xunit;

namespace Excise.Avalonia.Tests;

/// <summary>
/// Unit tests for the zoom-aware continuous-render DPI selection (#371 pt1,
/// #682/#683, #1472/#1480). Pure logic — no rendering — so it runs in the
/// non-flaky viewer-lib project.
/// </summary>
public class ContinuousDpiTests
{
    private static int Dpi(double zoom, double dpr) =>
        PdfViewerControl.EffectiveContinuousDpi(
            PdfViewerControl.ContinuousBaseDpi, zoom, PdfViewerControl.MaxContinuousDpi, dpr);

    [Theory]
    // zoom, renderScaling (device-pixel-ratio), expected DPI
    // --- standard display (dpr 1.0): 96 DPI = one render pixel per DIP ---
    [InlineData(1.0, 1.0, 96)]
    [InlineData(1.5, 1.0, 144)]   // scales with zoom -> crisper
    [InlineData(2.5, 1.0, 240)]   // reaches the cap
    [InlineData(4.0, 1.0, 240)]   // deep zoom is clamped to the cap (bounds memory)
    [InlineData(0.5, 1.0, 48)]    // zoomed out: no floor above device resolution (#1472)
    [InlineData(0.25, 1.0, 24)]   // the app's minimum zoom
    // --- HiDPI / Retina (dpr 2.0): render scales with the device pixel ratio (#682/#683) ---
    [InlineData(1.0, 2.0, 192)]   // 100% on a 2x display -> the display's 192 DPI, not 240 (#1480)
    [InlineData(1.5, 2.0, 288)]
    [InlineData(2.5, 2.0, 480)]   // the cap scales with dpr, so the same *visual* zoom stays crisp
    [InlineData(4.0, 2.0, 480)]   // clamped to the dpr-scaled cap
    [InlineData(0.5, 2.0, 96)]
    [InlineData(0.25, 2.0, 48)]   // #1472: was 240, i.e. 10 render px per DIP on a 2 px/DIP display
    public void EffectiveContinuousDpi_ScalesWithZoomAndDpr_AndClamps(double zoom, double dpr, int expected)
        => Dpi(zoom, dpr).Should().Be(expected);

    /// <summary>
    /// The property #1472 and #1480 ask for, over every zoom below the cap: a
    /// continuous tile holds one render pixel per device pixel. A slot lays a
    /// page out at <c>pt × 96/72 × zoom</c> DIP (<see cref="PdfPageSlot.ApplyZoom"/>)
    /// and the renderer draws <c>pt × dpi/72</c> pixels, so render pixels per DIP
    /// are <c>dpi / (96 × zoom)</c>; the display has <c>dpr</c>. The only slack
    /// is rounding the DPI to an integer.
    /// </summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    [InlineData(3.0)]
    public void RenderPixelsPerDip_MatchTheDisplay_AtEveryZoomBelowTheCap(double dpr)
    {
        for (double zoom = 0.25; zoom <= 2.5; zoom += 0.01)
        {
            int dpi = Dpi(zoom, dpr);
            double renderPxPerDip = dpi / (96.0 * zoom);
            double roundingSlack = 0.5 / (96.0 * zoom);
            renderPxPerDip.Should().BeApproximately(dpr, roundingSlack + 1e-9,
                $"at zoom {zoom:0.00} and dpr {dpr} the render must match the display's pixels " +
                "(below: soft text; above: pixels the screen cannot show)");
        }
    }

    [Fact]
    public void ContinuousBaseDpi_IsOneRenderPixelPerDip()
    {
        // A DIP is 1/96 inch and a slot lays a point out at PointsToDip DIP, so
        // one render pixel per DIP at zoom 1 is PointsToDip x 72 DPI.
        PdfViewerControl.ContinuousBaseDpi.Should().Be((int)Math.Round(PdfViewerControl.PointsToDip * 72));
    }

    // The single-sliding-band tile request (TryCreateContinuousTileRequest) was
    // removed in the #848 content-addressed-grid rework. Its coverage / clip
    // self-consistency contract now lives, per grid cell, in
    // ContinuousTileGridTests.
}
