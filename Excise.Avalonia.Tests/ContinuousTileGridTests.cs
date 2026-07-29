using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using Avalonia;
using Excise.Avalonia.Controls;
using Xunit;

namespace Excise.Avalonia.Tests;

/// <summary>
/// Exhaustive contract for the content-addressed continuous tile grid (#848).
///
/// This is the real safety net for #848: the blank-strip bug lives in the live
/// Avalonia ItemsControl compositing, which the headless test host cannot drive
/// and the full-page visual-regression harness cannot see (it renders the
/// SkiaRenderer, not the tile grid). So the property that fixes the bug —
/// "every visible cell is present-and-correct, in-flight, or absent, never
/// present-but-stale" — is proven here at the pure-math layer that decides which
/// cells exist and where each one sits.
/// </summary>
public class ContinuousTileGridTests
{
    private const int Q = PdfViewerControl.ContinuousTileQuantumDip;   // 256
    private const int O = PdfViewerControl.ContinuousTileOverscanDip;  // 256

    public static IEnumerable<object[]> Cases() => new[]
    {
        // pageWidthDip, pageHeightDip, pageTopDip, offsetX, offsetY, viewW, viewH
        new object[] { 800.0, 3000.0,    0.0,   0.0,    0.0, 800.0, 600.0 }, // top of a tall page
        new object[] { 800.0, 3000.0,    0.0,   0.0, 1200.0, 800.0, 600.0 }, // mid-scroll
        new object[] { 800.0, 3000.0,  500.0,   0.0,  900.0, 800.0, 600.0 }, // page offset in the stack
        new object[] { 2000.0, 3000.0, 100.0, 700.0, 1000.0, 400.0, 600.0 }, // zoomed-in, panned right
        new object[] { 813.0, 1057.0,  55.0,  37.0,  913.0, 333.0, 271.0 },  // deliberately unaligned
        new object[] { 200.0,  200.0,   0.0,   0.0,    0.0, 800.0, 600.0 },  // page smaller than viewport
        new object[] { 1064.0, 1376.0,  0.0,   0.0,  300.0, 900.0, 700.0 },  // 87% zoom of an 8.5x11 (612*1.333*0.87..)
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Grid_UnionCoversVisibleSlice_AndStaysOnPage(
        double pw, double ph, double top, double ox, double oy, double vw, double vh)
    {
        var cells = PdfViewerControl.RequiredTileCells(
            pw, ph, top, new Vector(ox, oy), new Size(vw, vh), Q, O);

        // Visible slice of this page, page-local dips (same convention as the grid).
        double visLeft   = Math.Max(0, ox);
        double visTop    = Math.Max(0, oy - top);
        double visRight  = Math.Min(pw, ox + vw);
        double visBottom = Math.Min(ph, oy - top + vh);

        cells.Should().NotBeEmpty("the viewport intersects the page in every case here");

        // 1. CONTAINMENT — no cell escapes the page.
        foreach (var c in cells)
        {
            c.XDip.Should().BeGreaterThanOrEqualTo(0);
            c.YDip.Should().BeGreaterThanOrEqualTo(0);
            (c.XDip + c.WidthDip).Should().BeLessThanOrEqualTo(pw + 1e-6);
            (c.YDip + c.HeightDip).Should().BeLessThanOrEqualTo(ph + 1e-6);
            c.WidthDip.Should().BeGreaterThan(0);
            c.HeightDip.Should().BeGreaterThan(0);
        }

        // 2. COVERAGE — the union of cells contains every visible pixel. The grid
        //    is a contiguous block colStart..colEnd × rowStart..rowEnd, so it
        //    covers the visible slice iff its bounding rect does. Under-cover by a
        //    single pixel is exactly the #848 blank strip.
        double coverLeft   = cells.Min(c => c.XDip);
        double coverTop    = cells.Min(c => c.YDip);
        double coverRight  = cells.Max(c => c.XDip + c.WidthDip);
        double coverBottom = cells.Max(c => c.YDip + c.HeightDip);

        coverLeft.Should().BeLessThanOrEqualTo(visLeft, "no blank strip on the left");
        coverTop.Should().BeLessThanOrEqualTo(visTop, "no blank strip at the top — the #848 symptom");
        coverRight.Should().BeGreaterThanOrEqualTo(visRight, "no blank strip on the right");
        coverBottom.Should().BeGreaterThanOrEqualTo(visBottom, "no blank strip at the bottom");
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Grid_CellsTileWithoutOverlap_OnAQuantumLattice(
        double pw, double ph, double top, double ox, double oy, double vw, double vh)
    {
        var cells = PdfViewerControl.RequiredTileCells(
            pw, ph, top, new Vector(ox, oy), new Size(vw, vh), Q, O);

        // Every cell sits on the fixed quantum lattice: origin is an exact
        // multiple of the quantum. This is what makes (Col, Row) a stable
        // identity independent of scroll.
        foreach (var c in cells)
        {
            c.XDip.Should().Be(c.Col * (double)Q);
            c.YDip.Should().Be(c.Row * (double)Q);
        }

        // No two cells share a (Col, Row) — the grid is a set, not a bag.
        cells.Select(c => (c.Col, c.Row)).Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// THE #848 invariant. A given (Col, Row) maps to the SAME rect regardless of
    /// scroll offset. That is why a cached tile painted at its grid position is
    /// always correct for that position — it can never be "present but stale for
    /// where it is shown", which was the whole failure mode of the old single
    /// sliding tile.
    /// </summary>
    [Fact]
    public void Grid_SameCell_HasIdenticalRect_AcrossDifferentScrollOffsets()
    {
        const double pw = 900, ph = 4000, vw = 800, vh = 600;

        var byCell = new Dictionary<(int, int), PdfViewerControl.GridCell>();
        // Sweep the viewport down the page in fine, deliberately unaligned steps.
        for (double oy = 0; oy + vh <= ph; oy += 37.5)
        {
            var cells = PdfViewerControl.RequiredTileCells(
                pw, ph, 0, new Vector(0, oy), new Size(vw, vh), Q, O);

            foreach (var c in cells)
            {
                var id = (c.Col, c.Row);
                if (byCell.TryGetValue(id, out var seen))
                {
                    c.XDip.Should().Be(seen.XDip);
                    c.YDip.Should().Be(seen.YDip);
                    c.WidthDip.Should().Be(seen.WidthDip);
                    c.HeightDip.Should().Be(seen.HeightDip,
                        "a grid cell's geometry must not depend on the scroll offset (#848)");
                }
                else
                {
                    byCell[id] = c;
                }
            }
        }

        byCell.Should().NotBeEmpty();
    }

    [Fact]
    public void Grid_WhenPageNotVisible_ReturnsNoCells()
    {
        var cells = PdfViewerControl.RequiredTileCells(
            pageWidthDip: 200, pageHeightDip: 200, pageTopDip: 5000,
            new Vector(0, 0), new Size(800, 600), Q, O);
        cells.Should().BeEmpty();
    }

    /// <summary>
    /// The cache-key collision that the page dims in <see cref="PdfViewerControl.CellKey"/>
    /// exist to prevent: at deep zoom the render DPI caps, so two different zooms
    /// share a DPI while an interior cell keeps the same (Col, Row) — the keys
    /// MUST still differ, or the cache shows one zoom's band at another zoom.
    /// </summary>
    [Fact]
    public void CellKey_AtCappedDpi_DistinguishesZoomByPageDims()
    {
        var cell = new PdfViewerControl.GridCell(0, 0, 0, 0, Q, Q);
        int cappedDpi = PdfViewerControl.MaxContinuousDpi;

        // Same page, same capped DPI, same interior cell — but different zoom, so
        // different page DIP dimensions.
        var keyZoom3 = PdfViewerControl.CellKey(page: 1, cappedDpi, pageWidthDip: 2448, pageHeightDip: 3168, cell);
        var keyZoom4 = PdfViewerControl.CellKey(page: 1, cappedDpi, pageWidthDip: 3264, pageHeightDip: 4224, cell);

        keyZoom3.Should().NotBe(keyZoom4,
            "an interior cell at the same capped DPI but different zoom shows different content");
    }

    /// <summary>
    /// A cell's DIP rect and its CONTENT-space clip must describe the same region:
    /// render one part of the page, show it as another, and content lands in the
    /// wrong place. Held at non-integer zoom (87%) and under 90°/270° rotation
    /// where the visual/content axes swap (#846).
    /// </summary>
    [Theory]
    [InlineData(0, 0.87)]
    [InlineData(0, 1.0)]
    [InlineData(90, 0.87)]
    [InlineData(180, 1.5)]
    [InlineData(270, 0.87)]
    public void CellToRequest_ClipRect_MatchesCellDipRect(int rotation, double zoom)
    {
        const double widthPt = 612, heightPt = 792;
        var contentBox = new Excise.Core.Document.PdfRectangle(0, 0, widthPt, heightPt);
        double dipPerPoint = PdfViewerControl.PointsToDip * zoom;

        // An interior cell positioned so it stays inside the content box under
        // BOTH axis orientations (0/180 map Y→tall axis, 90/270 map Y→short axis),
        // i.e. within the shorter page dimension in every direction — otherwise a
        // legitimate edge clamp, not a mismatch, would trip the assertion.
        var cell = new PdfViewerControl.GridCell(1, 1, Q, Q, Q, Q);

        var request = PdfViewerControl.CellToRequest(cell, zoom, rotation, contentBox);

        request.XDip.Should().Be((int)Math.Floor(cell.XDip));
        request.YDip.Should().Be((int)Math.Floor(cell.YDip));
        request.WidthDip.Should().Be((int)Math.Ceiling(cell.WidthDip));
        request.HeightDip.Should().Be((int)Math.Ceiling(cell.HeightDip));

        // The clip's extent in points must equal the cell's extent in dips /
        // dipPerPoint. For 90/270 the axes swap, so compare the clip's larger/
        // smaller spans against the cell's — a square cell makes width==height,
        // so this holds regardless of the swap, while still pinning magnitude.
        double cellWpt = cell.WidthDip / dipPerPoint;
        double cellHpt = cell.HeightDip / dipPerPoint;
        double clipW = request.ClipRect.Width;
        double clipH = request.ClipRect.Height;

        bool axesSwap = rotation % 180 == 90;
        if (axesSwap)
        {
            clipW.Should().BeApproximately((float)cellHpt, 0.05f);
            clipH.Should().BeApproximately((float)cellWpt, 0.05f);
        }
        else
        {
            clipW.Should().BeApproximately((float)cellWpt, 0.05f);
            clipH.Should().BeApproximately((float)cellHpt, 0.05f);
        }
    }
}
