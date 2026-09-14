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
/// #1489: the published single-page Image must be laid out at exactly the
/// coordinate space pointer input maps into (<c>pt × logicalDpi / 72</c> DIPs),
/// at any render scale.
/// </summary>
/// <remarks>
/// The overlay, <c>ViewerDipsToPdfRect</c> and every redaction/typewriter/form
/// rect map through <c>pt × 120 / 72</c>. Before the fix the Image was sized
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
        { 612, 792, 1.0, 1.429 },   // 120 × 1.429 = 171.48: the render DPI rounds down to 171
        { 612, 792, 1.0, 1.43 },    // 171.6 rounds up to 172: the raster overshoots instead
        { 2000, 1400, 0.5, 2.858 }, // a large page; zoom × dpr = 1.429 again
    };

    [FixedAvaloniaTheory]
    [MemberData(nameof(NonIntegerRenderScales))]
    public async Task PublishedPage_IsLaidOutAtExactlyTheInputCoordinateSpace(
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

            // The far corner of what is drawn is the far corner of the page in
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
