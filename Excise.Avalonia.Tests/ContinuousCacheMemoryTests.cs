using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using Avalonia;
using Excise.Avalonia.Controls;
using Xunit;

namespace Excise.Avalonia.Tests;

/// <summary>
/// Measures peak <see cref="global::Avalonia.Media.Imaging.WriteableBitmap"/> byte
/// residency for the continuous-scroll tile cache (#615), and pins the cache's
/// byte budget against that measurement instead of intuition.
/// </summary>
/// <remarks>
/// <para>
/// The cache stores <c>WriteableBitmap</c>s at <c>PixelFormat.Bgra8888</c> (4
/// bytes/pixel — see <c>SkiaInterop.ToAvaloniaBitmap</c>, which forces
/// <c>Bgra8888</c>/<c>Premul</c> for anything Skia hands back;
/// <see cref="PdfViewerControl.ContinuousTileByteSize"/> encodes that same
/// constant and is the exact function production code uses to size the LRU
/// eviction).
/// </para>
/// <para>
/// Under the content-addressed grid (#848), tiles are UNIFORM: every interior
/// cell is a full <see cref="PdfViewerControl.ContinuousTileQuantumDip"/> square,
/// edge cells smaller. So the worst-case resident tile is simply a full-quantum
/// interior cell rendered at the (dpr-scaled) DPI cap. This test builds that cell
/// through the REAL <see cref="PdfViewerControl.RequiredTileCells"/> +
/// <see cref="PdfViewerControl.CellToRequest"/> code paths, converts its
/// <c>ClipRect</c> (PDF points) to device pixels using the renderer's own DPI
/// scale (<c>scale = dpi / 72.0</c>, <c>pixelWidth = ceil(clipRect.Width * scale)</c>
/// — see <c>SkiaRenderer.cs</c>), and runs that through
/// <see cref="PdfViewerControl.ContinuousTileByteSize"/> — the same byte
/// accounting the production eviction loop uses.
/// </para>
/// </remarks>
public class ContinuousCacheMemoryTests
{
    private readonly ITestOutputHelper _output;

    public ContinuousCacheMemoryTests(ITestOutputHelper output) => _output = output;

    // Matches PdfViewerControl.DefaultRenderDpi (private const = 120). Duplicated
    // here because EffectiveContinuousDpi takes it as a parameter rather than
    // exposing the field; if that private const ever changes, re-run this test
    // with the new value — it is the one number in this file NOT derived from a
    // public/internal symbol.
    private const int BaseRenderDpi = 120;

    private static readonly double[] ZoomLevels = [1.0, 1.5, 2.0, 4.0];
    // Device-pixel ratios: standard and Retina/HiDPI. The DPI cap scales with dpr
    // (#682/#683), so a HiDPI display is where a single grid tile is largest.
    private static readonly double[] DevicePixelRatios = [1.0, 2.0];

    /// <summary>
    /// The measurement (#615/#848): the worst single resident grid tile is a
    /// full-quantum interior cell at the dpr-scaled DPI cap. Sweeps zoom x dpr,
    /// finds that tile, and confirms the byte budget holds it many times over
    /// (uniform tiles ⇒ generous scroll-back buffer). Run with
    /// <c>dotnet test --filter ContinuousCacheMemory --logger "console;verbosity=detailed"</c>
    /// to see the table.
    /// </summary>
    [Fact]
    public void MeasureContinuousTileCache_WorstCaseGridCell()
    {
        // A page comfortably larger than a quantum + overscan in both dimensions,
        // so an interior full-quantum cell exists. Large-format D-size scan.
        const double widthPt = 2592, heightPt = 3456;
        var contentBox = new Excise.Core.Document.PdfRectangle(0, 0, widthPt, heightPt);
        int q = PdfViewerControl.ContinuousTileQuantumDip;

        var rows = new List<(double Zoom, double Dpr, int WidthPx, int HeightPx, long Bytes)>();

        foreach (var zoom in ZoomLevels)
        {
            foreach (var dpr in DevicePixelRatios)
            {
                var slot = new PdfPageSlot(1, widthPt, heightPt, zoom);
                int dpi = PdfViewerControl.EffectiveContinuousDpi(
                    BaseRenderDpi, zoom, PdfViewerControl.MaxContinuousDpi, renderScaling: dpr);

                // Middle of the page so a full interior quantum cell is required.
                double offsetX = Math.Max(0, (slot.DisplayWidth - 800) / 2);
                double offsetY = Math.Max(0, (slot.DisplayHeight - 600) / 2);
                var cells = PdfViewerControl.RequiredTileCells(
                    slot.DisplayWidth, slot.DisplayHeight, 0,
                    new Vector(offsetX, offsetY), new Size(800, 600), q,
                    PdfViewerControl.ContinuousTileOverscanDip);

                // The largest cell is a full quantum square.
                var cell = cells
                    .Where(c => c.WidthDip >= q - 0.5 && c.HeightDip >= q - 0.5)
                    .OrderByDescending(c => c.WidthDip * c.HeightDip)
                    .First();

                var request = PdfViewerControl.CellToRequest(cell, zoom, rotation: 0, contentBox);

                double scale = dpi / 72.0;
                int pixelWidth = (int)Math.Ceiling(request.ClipRect.Width * scale);
                int pixelHeight = (int)Math.Ceiling(request.ClipRect.Height * scale);
                long bytes = PdfViewerControl.ContinuousTileByteSize(pixelWidth, pixelHeight);

                rows.Add((zoom, dpr, pixelWidth, pixelHeight, bytes));
            }
        }

        rows.Should().NotBeEmpty();

        _output.WriteLine($"{"Zoom",6} {"Dpr",5} {"PxW",6} {"PxH",6} {"MB",8}");
        foreach (var r in rows.OrderByDescending(r => r.Bytes))
            _output.WriteLine($"{r.Zoom,6:0.0} {r.Dpr,5:0.0} {r.WidthPx,6} {r.HeightPx,6} {r.Bytes / 1024.0 / 1024.0,8:0.00}");

        var worst = rows.MaxBy(r => r.Bytes);
        long worstTileBytes = worst.Bytes;
        double worstTileMb = worstTileBytes / 1024.0 / 1024.0;

        _output.WriteLine("");
        _output.WriteLine($"Worst single grid tile: zoom {worst.Zoom:0.0}x dpr {worst.Dpr:0.0} " +
                           $"= {worst.WidthPx}x{worst.HeightPx}px = {worstTileMb:0.00} MB");
        _output.WriteLine($"Byte budget: {ContinuousCacheByteBudgetForTest / 1024.0 / 1024.0:0} MB " +
                           $"(~{ContinuousCacheByteBudgetForTest / (double)worstTileBytes:0.0} worst-case tiles)");

        // Uniform tiles mean the budget holds many of them — a generous scroll-back
        // buffer so scrolling away and back is a cache hit, not a re-render (#848).
        // If tile geometry changes (quantum, overscan, DPI cap) and this starts
        // failing, re-derive the budget rather than raising it blindly.
        (ContinuousCacheByteBudgetForTest / worstTileBytes).Should().BeGreaterThanOrEqualTo(8,
            $"worst uniform tile is {worstTileMb:0.00} MB; the byte budget should hold many, " +
            "giving the visible grid plus a generous scroll-back buffer");
    }

    // Mirrors PdfViewerControl.ContinuousCacheByteBudget (private const) so this
    // file's narrative numbers track the real value without reflection tricks in
    // the main assertion. Kept in lockstep by
    // ContinuousCacheByteBudget_MatchesValueThisTestMeasuredAgainst below.
    private const long ContinuousCacheByteBudgetForTest = 200L * 1024 * 1024;

    [Fact]
    public void ContinuousCacheByteBudget_MatchesValueThisTestMeasuredAgainst()
    {
        // If someone changes ContinuousCacheByteBudget without re-running the
        // measurement above, this fails loudly rather than letting the budget
        // narrative silently describe a stale number.
        typeof(PdfViewerControl)
            .GetField("ContinuousCacheByteBudget", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetRawConstantValue()
            .Should().Be(ContinuousCacheByteBudgetForTest,
                "this file's narrative was derived against this specific budget value; if you change " +
                "ContinuousCacheByteBudget, re-run MeasureContinuousTileCache_AcrossDocumentViewportZoomMatrix " +
                "and update ContinuousCacheByteBudgetForTest to match");
    }

    [Theory]
    [InlineData(1, 1, 4)]
    [InlineData(4160, 2880, 4160L * 2880 * 4)]
    [InlineData(256, 256, 262_144)]
    public void ContinuousTileByteSize_IsWidthTimesHeightTimesFourBytesPerPixel(int width, int height, long expectedBytes)
        => PdfViewerControl.ContinuousTileByteSize(width, height).Should().Be(expectedBytes);

    // ── #1466: page composites ────────────────────────────────────────────
    // Everything above measures TILES. The continuous view also holds one
    // band-sized COMPOSITE per realized page (PdfPageSlot.Bitmap), which the tile
    // budget never counted. These tests measure composites through the same
    // production paths RecomposeSlotCore sizes them with: RequiredTileCells for
    // the band, ContinuousCellPixelExtent + ComputeMosaic for its pixels
    // (ContinuousCompositeByteSize), EffectiveContinuousDpi for the density.

    // The envelope PdfViewerControl.ContinuousCompositeByteBound is documented
    // for. Zoom starts at the app's floor (DocumentViewportSession.MinimumZoom =
    // 0.25) on purpose: the DPI is floored at DefaultRenderDpi x dpr, so pixels
    // per DIP GROW as zoom shrinks, and the lowest zoom is where composites peak.
    private static readonly (string Name, double WidthPt, double HeightPt)[] EnvelopePages =
    [
        ("Letter", 612, 792), ("Letter landscape", 792, 612), ("A4", 595, 842), ("Legal", 612, 1008),
    ];
    private static readonly Size[] EnvelopeViewports = [new(1280, 800), new(1920, 1080), new(2560, 1440)];
    private static readonly double[] EnvelopeZooms = [0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 3.0, 4.0, 5.0];

    /// <summary>
    /// The composite measurement (#1466): the worst total composite bytes held
    /// for any one viewport position, over the documented envelope, must stay
    /// within <see cref="PdfViewerControl.ContinuousCompositeByteBound"/> — and
    /// the bound must stay close enough to that worst case that a composite
    /// regression (larger overscan, a lower DPI floor, a band no longer clipped
    /// to the page) fails here instead of hiding under slack.
    /// </summary>
    [Fact]
    public void MeasureContinuousComposites_WorstViewportStaysWithinTheDocumentedBound()
    {
        var rows = new List<(string Page, Size Viewport, double Dpr, double Zoom, int Pages, long Bytes)>();
        foreach (var (name, widthPt, heightPt) in EnvelopePages)
            foreach (var viewport in EnvelopeViewports)
                foreach (var dpr in DevicePixelRatios)
                    foreach (var zoom in EnvelopeZooms)
                    {
                        var (bytes, pages) = WorstCompositeBytes(widthPt, heightPt, viewport, dpr, zoom);
                        rows.Add((name, viewport, dpr, zoom, pages, bytes));
                    }

        _output.WriteLine($"{"Page",-17} {"Viewport",-10} {"Dpr",4} {"Zoom",5} {"Pages",5} {"MB",8}");
        foreach (var r in rows.OrderByDescending(r => r.Bytes).Take(12))
            _output.WriteLine($"{r.Page,-17} {r.Viewport.Width,4}x{r.Viewport.Height,-5} {r.Dpr,4:0.0} {r.Zoom,5:0.00} " +
                              $"{r.Pages,5} {r.Bytes / 1024.0 / 1024.0,8:0.0}");

        // Informational, NOT asserted: a large-format sheet is outside the
        // envelope. At the zoom floor on a 2x display its band alone outgrows both
        // this bound and the tile budget.
        var (dSizeBytes, dSizePages) = WorstCompositeBytes(2592, 3456, new Size(2560, 1440), 2.0, 0.25);
        _output.WriteLine($"(outside envelope) D-size 2560x1440 dpr 2 zoom 0.25: pages={dSizePages} " +
                          $"{dSizeBytes / 1024.0 / 1024.0:0.0} MB");

        var worst = rows.MaxBy(r => r.Bytes);
        double worstMb = worst.Bytes / 1024.0 / 1024.0;
        double boundMb = PdfViewerControl.ContinuousCompositeByteBound / 1024.0 / 1024.0;
        _output.WriteLine($"Worst: {worst.Page} {worst.Viewport.Width}x{worst.Viewport.Height} dpr {worst.Dpr:0.0} " +
                          $"zoom {worst.Zoom:0.00} = {worstMb:0.0} MB over {worst.Pages} pages; bound {boundMb:0} MB");

        worst.Zoom.Should().Be(EnvelopeZooms.Min(),
            "composites peak at the zoom floor; if they no longer do, the DPI/zoom model changed — re-derive the bound");
        worst.Bytes.Should().BeLessThanOrEqualTo(PdfViewerControl.ContinuousCompositeByteBound,
            $"worst composite total is {worstMb:0.0} MB; the documented bound is {boundMb:0} MB");
        ((double)PdfViewerControl.ContinuousCompositeByteBound / worst.Bytes).Should().BeLessThan(1.5,
            "a bound far above the measurement would let a composite-byte regression through; " +
            "re-derive it from this table rather than restating it");
    }

    [Fact]
    public void ContinuousCompositeByteSize_IsTheMosaicOfFlooredCellContentPixels()
    {
        // Two columns (a full quantum and a 100-DIP edge cell) by one row, at
        // 1.25 px/DIP: floor(320) + floor(125) wide, floor(320) tall, 4 bytes/px.
        int q = PdfViewerControl.ContinuousTileQuantumDip;
        var cells = new[]
        {
            new PdfViewerControl.GridCell(0, 0, 0, 0, q, q),
            new PdfViewerControl.GridCell(1, 0, q, 0, 100, q),
        };

        PdfViewerControl.ContinuousCompositeByteSize(cells, pxPerDip: 1.25)
            .Should().Be((320L + 125) * 320 * 4);
    }

    /// <summary>
    /// The largest total composite bytes over one page period of scroll offsets,
    /// for a long document of identical pages laid out as the continuous view
    /// lays them out, and how many pages held a composite at that offset.
    /// </summary>
    private static (long Bytes, int Pages) WorstCompositeBytes(
        double widthPt, double heightPt, Size viewport, double dpr, double zoom)
    {
        int dpi = PdfViewerControl.EffectiveContinuousDpi(
            BaseRenderDpi, zoom, PdfViewerControl.MaxContinuousDpi, renderScaling: dpr);
        double pxPerDip = dpi / (96.0 * zoom);

        const int pageCount = 48;
        var slots = new List<PdfPageSlot>(pageCount);
        double top = 0;
        for (int i = 0; i < pageCount; i++)
        {
            var slot = new PdfPageSlot(i + 1, widthPt, heightPt, zoom);
            slot.ApplyLayout(top, zoom);
            top += slot.DisplayHeight + PdfViewerControl.PageGapDip;
            slots.Add(slot);
        }

        double period = slots[1].TopDip - slots[0].TopDip;
        double pageWidth = slots[0].DisplayWidth;
        var offsetsX = pageWidth <= viewport.Width
            ? new[] { 0.0 }
            : new[] { 0.0, (pageWidth - viewport.Width) / 2 };

        long worstBytes = 0;
        int worstPages = 0;
        const int samples = 48;
        for (int k = 0; k < samples; k++)
        {
            double offsetY = slots[pageCount / 2].TopDip + period * k / samples;
            foreach (var offsetX in offsetsX)
            {
                long bytes = 0;
                int pages = 0;
                foreach (var slot in slots)
                {
                    var cells = PdfViewerControl.RequiredTileCells(
                        slot.DisplayWidth, slot.DisplayHeight, slot.TopDip,
                        new Vector(offsetX, offsetY), viewport,
                        PdfViewerControl.ContinuousTileQuantumDip, PdfViewerControl.ContinuousTileOverscanDip);
                    if (cells.Count == 0) continue;
                    bytes += PdfViewerControl.ContinuousCompositeByteSize(cells, pxPerDip);
                    pages++;
                }
                if (bytes > worstBytes)
                {
                    worstBytes = bytes;
                    worstPages = pages;
                }
            }
        }
        return (worstBytes, worstPages);
    }
}
