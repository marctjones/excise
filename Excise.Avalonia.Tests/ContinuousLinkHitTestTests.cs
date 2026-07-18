using System;
using System.Collections.Generic;
using AwesomeAssertions;
using Avalonia;
using Excise.Avalonia.Controls;
using Xunit;

namespace Excise.Avalonia.Tests;

/// <summary>
/// Unit tests for the continuous-mode link hit-testing slot geometry (#667):
/// mapping a pointer position in ContinuousItems coordinates to (page,
/// page-local dips) via PdfPageSlot's TopDip/DisplayWidth/DisplayHeight
/// layout math. Pure — no window, no rendering — so it runs in the
/// non-flaky viewer-lib project. The dip→PDF-content-point conversion the
/// production path chains onto this is PdfCoordinateMapper's ContinuousDips
/// space, owned and tested in Excise.Core.
/// </summary>
public class ContinuousLinkHitTestTests
{
    // Matches PdfViewerControl.PageGapDip / the DataTemplate Border bottom margin.
    private const double PageGap = 12.0;

    /// <summary>US-Letter pages laid out the way ApplyContinuousSlotLayout does.</summary>
    private static List<PdfPageSlot> LetterSlots(int count, double zoom)
    {
        var slots = new List<PdfPageSlot>(count);
        double top = 0;
        for (int i = 1; i <= count; i++)
        {
            var slot = new PdfPageSlot(i, widthPt: 612, heightPt: 792, zoom);
            slot.ApplyLayout(top, zoom);
            top += slot.DisplayHeight + PageGap;
            slots.Add(slot);
        }
        return slots;
    }

    [Fact]
    public void PointInsideFirstPage_MapsToPageLocalDips()
    {
        var slots = LetterSlots(3, zoom: 1.0);
        // DisplayWidth = 612 * (96/72) = 816; centered in a 1000-dip items width
        // → 92-dip letterbox margin on each side.
        double margin = (1000 - slots[0].DisplayWidth) / 2;

        var ok = PdfViewerControl.TryMapContinuousPointToPage(
            slots, itemsWidthDip: 1000, new Point(margin + 100, 200),
            out var pageNumber, out var pagePoint);

        ok.Should().BeTrue();
        pageNumber.Should().Be(1);
        pagePoint.X.Should().BeApproximately(100, 0.001);
        pagePoint.Y.Should().BeApproximately(200, 0.001);
    }

    [Fact]
    public void PointInsideLaterPage_SubtractsThatSlotsTop()
    {
        var slots = LetterSlots(5, zoom: 1.0);
        var slot3 = slots[2];
        double margin = (1000 - slot3.DisplayWidth) / 2;

        var ok = PdfViewerControl.TryMapContinuousPointToPage(
            slots, itemsWidthDip: 1000, new Point(margin + 10, slot3.TopDip + 30),
            out var pageNumber, out var pagePoint);

        ok.Should().BeTrue();
        pageNumber.Should().Be(3);
        pagePoint.X.Should().BeApproximately(10, 0.001);
        pagePoint.Y.Should().BeApproximately(30, 0.001);
    }

    [Fact]
    public void PointInTheInterPageGap_MapsToNothing()
    {
        var slots = LetterSlots(3, zoom: 1.0);
        // Just past page 1's bottom edge, inside the 12-dip gap before page 2.
        double gapY = slots[0].DisplayHeight + PageGap / 2;

        PdfViewerControl.TryMapContinuousPointToPage(
                slots, itemsWidthDip: 1000, new Point(500, gapY), out _, out _)
            .Should().BeFalse("the gap between pages is not part of any page");
    }

    [Fact]
    public void PointInTheLetterboxMarginBesideACenteredPage_MapsToNothing()
    {
        var slots = LetterSlots(2, zoom: 1.0);
        double margin = (1000 - slots[0].DisplayWidth) / 2;

        PdfViewerControl.TryMapContinuousPointToPage(
                slots, itemsWidthDip: 1000, new Point(margin - 5, 100), out _, out _)
            .Should().BeFalse("left of the centered page border is not on the page");

        PdfViewerControl.TryMapContinuousPointToPage(
                slots, itemsWidthDip: 1000, new Point(1000 - margin + 5, 100), out _, out _)
            .Should().BeFalse("right of the centered page border is not on the page");
    }

    [Fact]
    public void PointBelowTheLastPage_MapsToNothing()
    {
        var slots = LetterSlots(2, zoom: 1.0);
        double belowAll = slots[1].TopDip + slots[1].DisplayHeight + 100;

        PdfViewerControl.TryMapContinuousPointToPage(
                slots, itemsWidthDip: 1000, new Point(500, belowAll), out _, out _)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void MappingScalesWithZoom_SamePagePointForTheSamePageFraction(double zoom)
    {
        // The same fractional position on the page must land at a page-local
        // dip position that divides by (PointsToDip * zoom) to the same PDF
        // point — i.e. page-local dips scale linearly with zoom, exactly like
        // the slot's DisplayWidth/DisplayHeight do.
        var slots = LetterSlots(3, zoom);
        var slot2 = slots[1];
        double itemsWidth = Math.Max(1000, slot2.DisplayWidth);
        double margin = (itemsWidth - slot2.DisplayWidth) / 2;

        // Point at 25% across, 50% down page 2.
        var probe = new Point(
            margin + slot2.DisplayWidth * 0.25,
            slot2.TopDip + slot2.DisplayHeight * 0.5);

        var ok = PdfViewerControl.TryMapContinuousPointToPage(
            slots, itemsWidth, probe, out var pageNumber, out var pagePoint);

        ok.Should().BeTrue();
        pageNumber.Should().Be(2);

        double dipPerPoint = PdfViewerControl.PointsToDip * zoom;
        (pagePoint.X / dipPerPoint).Should().BeApproximately(612 * 0.25, 0.01,
            "page-local dips divided by the continuous dip-per-point scale must be page points");
        (pagePoint.Y / dipPerPoint).Should().BeApproximately(792 * 0.5, 0.01);
    }

    [Fact]
    public void EmptySlotList_MapsToNothing()
    {
        PdfViewerControl.TryMapContinuousPointToPage(
                Array.Empty<PdfPageSlot>(), itemsWidthDip: 1000, new Point(1, 1), out _, out _)
            .Should().BeFalse();
    }
}
