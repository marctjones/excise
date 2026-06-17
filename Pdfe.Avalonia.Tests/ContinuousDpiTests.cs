using AwesomeAssertions;
using Avalonia;
using Pdfe.Avalonia.Controls;
using Xunit;

namespace Pdfe.Avalonia.Tests;

/// <summary>
/// Unit tests for the zoom-aware continuous-render DPI selection (#371 pt1).
/// Pure logic — no rendering — so it runs in the non-flaky viewer-lib project.
/// </summary>
public class ContinuousDpiTests
{
    [Theory]
    [InlineData(1.0, 120)]   // at 100% zoom, render at the base DPI
    [InlineData(1.5, 180)]   // scales with zoom -> crisper
    [InlineData(2.0, 240)]   // at the cap
    [InlineData(4.0, 240)]   // deep zoom is clamped to the cap (bounds memory)
    [InlineData(0.5, 120)]   // never below the base DPI
    public void EffectiveContinuousDpi_ScalesWithZoom_AndClamps(double zoom, int expected)
        => PdfViewerControl.EffectiveContinuousDpi(120, zoom, PdfViewerControl.MaxContinuousDpi)
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

    [Fact]
    public void TryCreateContinuousTileRequest_WhenViewportCutsPage_ConvertsVisualDipToPdfClip()
    {
        var slot = new PdfPageSlot(pageNumber: 2, widthPt: 100, heightPt: 200, zoom: 1.0);

        var ok = PdfViewerControl.TryCreateContinuousTileRequest(
            slot,
            viewportOffset: new Vector(10, 120),
            viewport: new Size(50, 80),
            pageTop: 100,
            zoom: 1.0,
            out var request);

        ok.Should().BeTrue();
        request.XDip.Should().Be(10);
        request.YDip.Should().Be(20);
        request.WidthDip.Should().Be(50);
        request.HeightDip.Should().Be(80);
        request.ClipRect.Left.Should().BeApproximately(7.5f, 0.001f);
        request.ClipRect.Top.Should().BeApproximately(125, 0.001f);
        request.ClipRect.Right.Should().BeApproximately(45, 0.001f);
        request.ClipRect.Bottom.Should().BeApproximately(185, 0.001f);
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
