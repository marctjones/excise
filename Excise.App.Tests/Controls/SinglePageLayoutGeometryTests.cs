using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AwesomeAssertions;
using Excise.App.Tests.Utilities;
using Excise.Avalonia.Controls;
using Excise.Core.Document;
using Xunit;

namespace Excise.App.Tests.Controls;

/// <summary>
/// Single-page geometry: the Image is laid out in the logical coordinate space
/// input maps into (#1489), while the raster behind it is at the display's
/// device resolution (#1487). The two are independent; these tests pin both.
/// </summary>
/// <remarks>
/// The overlay, <c>ViewerDipsToPdfRect</c> and every redaction/typewriter/form
/// rect map through <c>pt × 120 / 72</c>. Before #1489 the Image was sized
/// from the raster instead, <c>px × 96 / bitmapDpi</c>, which differs from that
/// space twice over: the render DPI was rounded (<c>round(120·s)</c>) while the
/// bitmap DPI was not (<c>96·s</c>), and the renderer ceils the pixel count.
/// Whenever <c>120 × zoom × dpr</c> was not an integer the drawn page was up to
/// ~0.4% larger or smaller than the space input lands in, so a box drawn around
/// text near the far edge of a large page mapped several points away from it.
/// The old plan gate (<c>SinglePageRenderPlan_KeepsDipSizeInvariant</c>)
/// tolerated 0.5 DPI in 120, which is the defect's own size.
/// </remarks>
[Collection("AvaloniaTests")]
public class SinglePageLayoutGeometryTests
{
    public static TheoryData<double, double, double, double> NonIntegerRenderScales() => new()
    {
        // widthPt, heightPt, zoom, dpr
        { 612, 792, 1.0, 1.429 },   // 120 × 1.429 = 171.48: the old render DPI rounded down to 171
        { 612, 792, 1.0, 1.43 },    // 171.6 rounded up to 172: the raster overshot instead
        { 2000, 1400, 0.5, 2.858 }, // a large page; zoom × dpr = 1.429 again
    };

    [FixedAvaloniaTheory]
    [MemberData(nameof(NonIntegerRenderScales))]
    public async Task PublishedPage_IsLaidOutAtExactlyTheInputCoordinateSpace(
        double widthPt, double heightPt, double zoom, double dpr)
    {
        var (window, viewer) = await OpenSinglePageAsync(widthPt, heightPt, zoom, dpr);
        try
        {
            var image = viewer.FindControl<Image>("PdfImage")!;
            var bitmap = (Bitmap)image.Source!;
            var logicalWidth = widthPt * 120.0 / 72.0;
            var logicalHeight = heightPt * 120.0 / 72.0;

            image.Width.Should().BeApproximately(logicalWidth, 1e-6,
                $"the page must be sized to exactly the input coordinate space " +
                $"(raster {bitmap.PixelSize.Width}x{bitmap.PixelSize.Height} at zoom {zoom} × dpr {dpr})");
            image.Height.Should().BeApproximately(logicalHeight, 1e-6);

            // What is actually drawn: Avalonia's layout rounding snaps the arranged
            // size to the window's pixel grid, so Bounds may differ by less than one
            // layout pixel — a constant, not a drift that grows across the page.
            // Before #1489 this was 2–8 DIPs on these cases.
            var layoutPixel = 1.0 / window.RenderScaling;
            image.Bounds.Width.Should().BeApproximately(logicalWidth, layoutPixel);
            image.Bounds.Height.Should().BeApproximately(logicalHeight, layoutPixel);

            // The far corner of the page as sized is the far corner of the page in
            // the space a redaction or typewriter rect is built from.
            var corner = viewer.ViewerDipsToPdfRect(new Rect(image.Width, image.Height, 0, 0), 1);
            corner.Left.Should().BeApproximately(widthPt, 1e-6);
            corner.Bottom.Should().BeApproximately(0, 1e-6);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// #1487: the single page renders at the display's device resolution,
    /// <c>96 × zoom × dpr</c> DPI (a DIP is 1/96 inch), not <c>120 × zoom × dpr</c>
    /// — the logical layout DPI used as a render DPI, which is 1.25× the linear
    /// resolution and 1.56× the pixels. The layout, and with it every input
    /// mapping, stays at 120. The <c>zoom × dpr ≥ 1</c> floor is kept.
    /// </summary>
    [FixedAvaloniaTheory]
    [InlineData(1.0, 1.0)]
    [InlineData(1.0, 2.0)]
    [InlineData(2.0, 1.0)]
    [InlineData(2.0, 2.0)]
    public async Task PublishedRaster_IsAtDeviceResolution_WhileLayoutStaysLogical(double zoom, double dpr)
    {
        const double widthPt = 612, heightPt = 792;
        var (window, viewer) = await OpenSinglePageAsync(widthPt, heightPt, zoom, dpr);
        try
        {
            var image = viewer.FindControl<Image>("PdfImage")!;
            var bitmap = (Bitmap)image.Source!;
            var deviceDpi = (int)Math.Round(96 * Math.Max(1.0, zoom * dpr));

            bitmap.PixelSize.Width.Should().Be((int)Math.Ceiling(widthPt * deviceDpi / 72.0),
                $"one raster pixel per device pixel at zoom {zoom} × dpr {dpr} ({deviceDpi} DPI)");
            bitmap.PixelSize.Height.Should().Be((int)Math.Ceiling(heightPt * deviceDpi / 72.0));
            image.Width.Should().BeApproximately(widthPt * 120.0 / 72.0, 1e-6,
                "the layout, and so every input mapping, must not move with the render DPI");
            image.Height.Should().BeApproximately(heightPt * 120.0 / 72.0, 1e-6);
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task<(Window Window, PdfViewerControl Viewer)> OpenSinglePageAsync(
        double widthPt, double heightPt, double zoom, double dpr)
    {
        var viewer = new PdfViewerControl { ViewMode = PdfViewMode.SinglePage, RenderScalingOverride = dpr };
        var window = new Window { Content = viewer, Width = 900, Height = 700 };
        window.Show();
        try
        {
            viewer.ZoomLevel = zoom;
            viewer.Document = PdfDocument.Open(BlankPage(widthPt, heightPt));
            await WaitForFinalRenderAsync(window, viewer);
            return (window, viewer);
        }
        catch
        {
            window.Close();
            throw;
        }
    }

    private static byte[] BlankPage(double widthPt, double heightPt)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-layout-geometry-{Guid.NewGuid():N}.pdf");
        try
        {
            using (var doc = PdfDocument.CreateNew())
            {
                doc.Pages.AddBlank(widthPt, heightPt);
                doc.Save(path);
            }
            return File.ReadAllBytes(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task WaitForFinalRenderAsync(Window window, PdfViewerControl viewer)
    {
        await SinglePageViewerWaits.WaitForSinglePageLaidOutAsync(window, viewer);
        var sw = Stopwatch.StartNew();
        while (viewer.SinglePagePublishCount == 0 || viewer.IsLoading || viewer.SinglePagePlaceholderForTests != null)
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(30))
                throw new TimeoutException("the single-page render never published");
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(25);
        }
        window.UpdateLayout();
    }
}
