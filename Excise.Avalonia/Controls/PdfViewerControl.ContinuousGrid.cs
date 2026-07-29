using System;
using System.Collections.Generic;
using global::Avalonia;
using Excise.Core.Document;
using Excise.Rendering;
using SkiaSharp;

namespace Excise.Avalonia.Controls;

/// <summary>
/// Content-addressed tile grid for the continuous (reading) view (#848).
///
/// The old continuous view kept exactly ONE mutable tile per page, covering a
/// sliding band of the viewport. Because that single tile was re-pointed at a
/// new band on every scroll, a coalesced re-render that fired late (or not at
/// all) could leave the tile covering a STALE band for the current offset — the
/// user saw a blank strip at the leading edge that "recovered on scroll-away-
/// and-back" (#848). The defect is structural: one mutable tile can be
/// *present but wrong for where it is shown*.
///
/// This grid removes the class. A page is divided into a fixed grid of
/// <see cref="ContinuousTileQuantumDip"/>-sized cells in page-local DIP space.
/// Each cell has a stable identity — its (Col, Row) and the page's DIP
/// dimensions fully determine the content region it shows — so a cached cell
/// painted at its fixed grid position is ALWAYS correct for that position. A
/// visible cell is therefore only ever one of: present-and-correct, in-flight,
/// or absent. Never present-but-stale.
///
/// The math here is pure and exhaustively unit-tested
/// (<c>ContinuousTileGridTests</c>), which matters because the compositing bug
/// #848 targets lives in the live Avalonia ItemsControl and cannot be driven by
/// the headless test host or seen by the full-page visual-regression harness.
/// </summary>
public partial class PdfViewerControl
{
    /// <summary>
    /// One cell of the page's tile grid, in page-local DIP coordinates (the
    /// Border's own space). <see cref="Col"/>/<see cref="Row"/> are the stable
    /// grid indices; the rect is the cell clamped to the page (edge cells are
    /// smaller than a full quantum).
    /// </summary>
    internal readonly record struct GridCell(
        int Col, int Row, double XDip, double YDip, double WidthDip, double HeightDip);

    /// <summary>
    /// The set of grid cells a page must have rendered to cover the current
    /// viewport (plus overscan), in page-local DIP space. Pure — no rendering,
    /// no control state — so the coverage contract can be tested exhaustively.
    ///
    /// A cell's (Col, Row) and rect depend ONLY on the page geometry and the
    /// grid constants, never on the scroll offset: scrolling changes WHICH cells
    /// are required, never where a given cell sits or what it shows. That
    /// stability is the property that makes cached cells safe to reuse and is
    /// the structural fix for #848.
    /// </summary>
    /// <param name="pageWidthDip">Slot DisplayWidth (zoomed page width in DIPs).</param>
    /// <param name="pageHeightDip">Slot DisplayHeight (zoomed page height in DIPs).</param>
    /// <param name="pageTopDip">Slot TopDip (page's top in the scrolled items coordinate space).</param>
    /// <param name="viewportOffset">The ScrollViewer offset (items coordinate space).</param>
    /// <param name="viewport">The ScrollViewer viewport size.</param>
    /// <param name="quantumDip">Grid cell size in DIPs.</param>
    /// <param name="overscanDip">Extra margin rendered around the viewport, in DIPs.</param>
    internal static IReadOnlyList<GridCell> RequiredTileCells(
        double pageWidthDip, double pageHeightDip, double pageTopDip,
        Vector viewportOffset, Size viewport,
        int quantumDip, int overscanDip)
    {
        var cells = new List<GridCell>();
        if (pageWidthDip <= 0 || pageHeightDip <= 0 ||
            viewport.Width <= 0 || viewport.Height <= 0 || quantumDip <= 0)
            return cells;

        // Visible slice of THIS page in page-local dips. Matches the historical
        // TryCreateContinuousTileRequest convention: page-local x == items x
        // (the page fills the width when zoomed in; when narrower than the
        // viewport, offset.X is 0 and [0, pageWidth] is fully visible).
        double visibleLeft   = Math.Clamp(viewportOffset.X, 0, pageWidthDip);
        double visibleTop    = Math.Clamp(viewportOffset.Y - pageTopDip, 0, pageHeightDip);
        double visibleRight  = Math.Clamp(viewportOffset.X + viewport.Width, 0, pageWidthDip);
        double visibleBottom = Math.Clamp(viewportOffset.Y + viewport.Height - pageTopDip, 0, pageHeightDip);

        if (visibleRight <= visibleLeft || visibleBottom <= visibleTop)
            return cells; // page not visible

        double exLeft   = Math.Max(0, visibleLeft - overscanDip);
        double exTop    = Math.Max(0, visibleTop - overscanDip);
        double exRight  = Math.Min(pageWidthDip, visibleRight + overscanDip);
        double exBottom = Math.Min(pageHeightDip, visibleBottom + overscanDip);

        int colStart = (int)Math.Floor(exLeft / quantumDip);
        int colEnd   = (int)Math.Ceiling(exRight / quantumDip);   // exclusive
        int rowStart = (int)Math.Floor(exTop / quantumDip);
        int rowEnd   = (int)Math.Ceiling(exBottom / quantumDip);  // exclusive

        for (int row = rowStart; row < rowEnd; row++)
        {
            double cellY = row * (double)quantumDip;
            double cellBottom = Math.Min(cellY + quantumDip, pageHeightDip);
            double cellH = cellBottom - cellY;
            if (cellH <= 0) continue;

            for (int col = colStart; col < colEnd; col++)
            {
                double cellX = col * (double)quantumDip;
                double cellRight = Math.Min(cellX + quantumDip, pageWidthDip);
                double cellW = cellRight - cellX;
                if (cellW <= 0) continue;

                cells.Add(new GridCell(col, row, cellX, cellY, cellW, cellH));
            }
        }

        return cells;
    }

    /// <summary>
    /// The stable cache/identity key for a grid cell. Includes the page DIP
    /// dimensions because at deep zoom the render DPI caps
    /// (<see cref="MaxContinuousDpi"/>): two different zooms can produce the same
    /// capped <paramref name="dpi"/> while an interior cell keeps the same
    /// (Col, Row, 256×256) geometry — so without the page dims those two cells
    /// would collide in the cache and show the wrong band. The page dims
    /// disambiguate the zoom, keeping every key mapped to exactly one content
    /// region.
    /// </summary>
    internal static ContinuousTileKey CellKey(
        int page, int dpi, double pageWidthDip, double pageHeightDip, GridCell cell) =>
        new(page, dpi,
            (int)Math.Round(pageWidthDip), (int)Math.Round(pageHeightDip),
            cell.Col, cell.Row);

    /// <summary>
    /// Convert a grid cell to the renderer request: the page-local DIP rect the
    /// tile occupies plus the CONTENT-space clip rect (rotation-mapped via
    /// <see cref="ContinuousTileClip"/>, #846) that <see cref="SkiaRenderer"/>
    /// clips to.
    /// </summary>
    internal static ContinuousTileRequest CellToRequest(
        GridCell cell, double zoom, int rotation, PdfRectangle contentBox)
    {
        double dipPerPoint = PointsToDip * zoom;
        var clip = ContinuousTileClip.VisualBandToContentClip(
            rotation, contentBox,
            cell.XDip / dipPerPoint,
            cell.YDip / dipPerPoint,
            cell.WidthDip / dipPerPoint,
            cell.HeightDip / dipPerPoint);

        return new ContinuousTileRequest(
            clip,
            (int)Math.Floor(cell.XDip),
            (int)Math.Floor(cell.YDip),
            Math.Max(1, (int)Math.Ceiling(cell.WidthDip)),
            Math.Max(1, (int)Math.Ceiling(cell.HeightDip)));
    }
}
