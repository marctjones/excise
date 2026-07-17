using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using Avalonia;
using Pdfe.Avalonia.Controls;
using Xunit;

namespace Pdfe.Avalonia.Tests;

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
/// eviction). A tile's pixel size is NOT the dip tile size from
/// <see cref="PdfViewerControl.TryCreateContinuousTileRequest"/> — it is that
/// tile rendered at <see cref="PdfViewerControl.EffectiveContinuousDpi"/>. This
/// test reproduces the exact scale <c>SkiaRenderer</c> applies
/// (<c>scale = dpi / 72.0</c>, then <c>pixelWidth = ceil(clipRect.Width * scale)</c>
/// — see <c>SkiaRenderer.cs</c> lines ~47 and ~84) applied to the REAL
/// <see cref="PdfViewerControl.ContinuousTileRequest.ClipRect"/> produced by the
/// real tile-request function, rather than re-deriving the dip-to-point-to-pixel
/// algebra by hand.
/// </para>
/// <para>
/// Measurement approach (reproducible without a live memory profiler, per #615):
/// for a representative matrix of page sizes x viewport sizes x zoom levels, call
/// the real <see cref="PdfViewerControl.TryCreateContinuousTileRequest"/> to get
/// the actual overscanned/quantized tile geometry, convert its <c>ClipRect</c>
/// (PDF points) to device pixels using the renderer's own DPI scale, and run that
/// through <see cref="PdfViewerControl.ContinuousTileByteSize"/> — the same byte
/// accounting the production eviction loop uses. That is a tile's true resident
/// cost. The worst observed tile, and the budget it must fit under alongside a
/// few neighbours, is exactly the number this issue asked to be measured before
/// sizing the cache.
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

    private static readonly (string Name, double WidthPt, double HeightPt)[] Documents =
    [
        ("US Letter (typical short doc)", 612, 792),
        ("D-size scan, 36x48in (large-format long doc)", 2592, 3456),
    ];

    private static readonly (string Name, double Width, double Height)[] Viewports =
    [
        ("1280x800 laptop", 1280, 800),
        ("1920x1080 desktop", 1920, 1080),
        ("2560x1440 large monitor", 2560, 1440),
    ];

    private static readonly double[] ZoomLevels = [1.0, 1.5, 2.0, 4.0];

    /// <summary>
    /// The actual measurement (#615): walks the document x viewport x zoom matrix,
    /// finds the single largest resident tile, and reports what a full cache
    /// budget of ~4x that tile means for both large-format and ordinary
    /// documents. Run with
    /// <c>dotnet test --filter ContinuousCacheMemory --logger "console;verbosity=detailed"</c>
    /// to see the table.
    /// </summary>
    [Fact]
    public void MeasureContinuousTileCache_AcrossDocumentViewportZoomMatrix()
    {
        var rows = new List<(string Doc, string Viewport, double Zoom, int WidthPx, int HeightPx, long Bytes)>();

        foreach (var doc in Documents)
        {
            foreach (var vp in Viewports)
            {
                foreach (var zoom in ZoomLevels)
                {
                    var slot = new PdfPageSlot(1, doc.WidthPt, doc.HeightPt, zoom);

                    // Position the viewport away from the page edges (when the page
                    // is large enough) so overscan is not clipped by page bounds —
                    // the worst-case, memory-maximizing position for a reader
                    // mid-document.
                    double offsetX = Math.Max(0, (slot.DisplayWidth - vp.Width) / 2);
                    double offsetY = Math.Max(0, (slot.DisplayHeight - vp.Height) / 2);

                    var ok = PdfViewerControl.TryCreateContinuousTileRequest(
                        slot, new Vector(offsetX, offsetY), new Size(vp.Width, vp.Height), 0, zoom, out var request);
                    if (!ok) continue;

                    int dpi = PdfViewerControl.EffectiveContinuousDpi(BaseRenderDpi, zoom, PdfViewerControl.MaxContinuousDpi);

                    // Reproduces SkiaRenderer.RenderPage's own device-pixel sizing:
                    // scale = dpi/72, pixel width/height = ceil(clipRect dimension * scale).
                    double scale = dpi / 72.0;
                    int pixelWidth = (int)Math.Ceiling((request.ClipRect.Right - request.ClipRect.Left) * scale);
                    int pixelHeight = (int)Math.Ceiling((request.ClipRect.Bottom - request.ClipRect.Top) * scale);
                    long bytes = PdfViewerControl.ContinuousTileByteSize(pixelWidth, pixelHeight);

                    rows.Add((doc.Name, vp.Name, zoom, pixelWidth, pixelHeight, bytes));
                }
            }
        }

        rows.Should().NotBeEmpty();

        _output.WriteLine($"{"Document",-46} {"Viewport",-20} {"Zoom",6} {"PxW",6} {"PxH",6} {"MB",8}");
        foreach (var r in rows.OrderByDescending(r => r.Bytes))
        {
            _output.WriteLine($"{r.Doc,-46} {r.Viewport,-20} {r.Zoom,6:0.0} {r.WidthPx,6} {r.HeightPx,6} {r.Bytes / 1024.0 / 1024.0,8:0.00}");
        }

        var worst = rows.MaxBy(r => r.Bytes);
        long worstTileBytes = worst.Bytes;
        double worstTileMb = worstTileBytes / 1024.0 / 1024.0;

        const int oldFlatCapacity = 10;

        _output.WriteLine("");
        _output.WriteLine($"Worst single tile: {worst.Doc} / {worst.Viewport} @ {worst.Zoom:0.0}x " +
                           $"= {worst.WidthPx}x{worst.HeightPx}px = {worstTileMb:0.00} MB");
        _output.WriteLine($"Full cache @ OLD flat count ({oldFlatCapacity}): {worstTileMb * oldFlatCapacity:0.0} MB (unmeasured, unbounded across doc sizes)");
        _output.WriteLine($"NEW byte budget: {ContinuousCacheByteBudgetForTest / 1024.0 / 1024.0:0} MB " +
                           $"(~{ContinuousCacheByteBudgetForTest / (double)worstTileBytes:0.0}x the worst measured tile)");

        // The invariant this issue asked for: the cache's memory ceiling must be
        // sized against a measured worst-case tile, not picked by intuition.
        // Budget: keep the worst-observed tile fitting at least
        // ContinuousCacheMinEntries times over inside the byte budget, so the
        // worst case (large-format doc, large monitor, mid-zoom) still gets a
        // small amount of scroll-buffer headroom rather than being reduced to a
        // single resident tile. If tile geometry changes (quantum, overscan, DPI
        // cap) and this starts failing, that is the signal to re-derive the
        // budget, not to raise it blindly.
        (ContinuousCacheByteBudgetForTest / worstTileBytes).Should().BeGreaterThanOrEqualTo(2,
            $"worst tile is {worstTileMb:0.00} MB; the byte budget must comfortably fit at least a couple of " +
            "worst-case tiles so a large-format document isn't reduced to a single-tile cache");
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
}
