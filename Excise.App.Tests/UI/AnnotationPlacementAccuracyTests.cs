using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AwesomeAssertions;
using Excise.App.Tests.Utilities;
using Excise.App.Views;
using Excise.Avalonia.Controls;
using Excise.Core.Document;
using Excise.Core.Graphics;
using SkiaSharp;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// WHERE a toolbar-placed annotation lands, not just whether one exists.
/// <see cref="ShapeAnnotationModeWorkflowTests"/> drives the same gesture but
/// only asserts a Square exists, so it stays green with the square anywhere on
/// the page.
/// </summary>
/// <remarks>
/// Each case clicks the palette's Square button with real input, drags around
/// ink found in the published raster (never a DPI formula shared with the app),
/// and checks both ends of the pipeline against the drag:
/// the saved /Rect (input side: pointer -> PDF points) and the pixels that
/// changed on screen after placement (output side: PDF points -> screen).
/// Either side alone cannot tell a stored-rect error from a redraw error.
/// </remarks>
[Collection("AvaloniaTests")]
public class AnnotationPlacementAccuracyTests
{
    // Asymmetric on purpose: a Y-flip, X-mirror or scale error moves it far.
    private const double TargetLeftPt = 110, TargetBottomPt = 560, TargetSizePt = 70;
    private const double PadDips = 10;
    private const double PdfTolerancePt = 2.0;
    private const double ScreenToleranceDips = 4.0;

    [FixedAvaloniaTheory(Timeout = 120000)]
    [InlineData(1.0, 1, null)]
    [InlineData(2.0, 1, null)]
    [InlineData(2.0, 2, null)]
    [InlineData(2.0, 2, 1.5)]
    public async Task PaletteSquare_RealDragAroundTarget_LandsOnTheTarget(
        double renderScaling, int pageNumber, double? manualZoom)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"excise-annot-place-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var source = Path.Combine(dir, "in.pdf");
        var output = Path.Combine(dir, "out.pdf");
        CreateFixture(source, pageNumber);

        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        try
        {
            var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl")!;
            viewer.RenderScalingOverride = renderScaling;
            await vm.LoadDocumentAsync(source);
            await WaitForIdleLayout(window);

            // The app opens in the continuous view; navigate there like a reader
            // would before picking a tool.
            vm.CurrentPageIndex = pageNumber - 1;
            if (manualZoom is { } z) vm.ZoomLevel = z;
            await WaitForIdleLayout(window);

            vm.ToggleAnnotationPaletteCommand.Execute().Subscribe();
            await WaitForIdleLayout(window);
            var palette = window.AnnotationPalette!;
            var squareButton = palette.FindControl<Button>("PaletteSquareButton")!;
            var buttonCenter = squareButton.TranslatePoint(
                new Point(squareButton.Bounds.Width / 2, squareButton.Bounds.Height / 2), palette)!.Value;
            palette.MouseDown(buttonCenter, MouseButton.Left);
            palette.MouseUp(buttonCenter, MouseButton.Left);
            await KeyboardTestHelpers.FlushDispatcherAsync();

            vm.IsShapeAnnotationMode.Should().BeTrue("a real click on the palette's Square button arms the tool");
            await SinglePageViewerWaits.WaitForSinglePageLaidOutAsync(window, viewer);
            await WaitForIdleLayout(window);
            await WaitForFinalSinglePageRender(window, viewer);
            viewer.CurrentPage.Should().Be(pageNumber, "arming a tool must keep the page the reader was on");

            var image = viewer.FindControl<Image>("PdfImage")!;
            var bitmap = (Bitmap)image.Source!;
            var ink = InkBoundsPx(bitmap);
            ink.Width.Should().BeGreaterThan(0,
                $"the target square must be visible on the displayed page {viewer.CurrentPage}");

            // Raster px -> Image-local DIPs through the Image's LAID-OUT size,
            // which is where the user's eye puts things.
            double dipPerPxX = image.Bounds.Width / bitmap.PixelSize.Width;
            double dipPerPxY = image.Bounds.Height / bitmap.PixelSize.Height;
            var inkDips = new Rect(ink.X * dipPerPxX, ink.Y * dipPerPxY,
                ink.Width * dipPerPxX, ink.Height * dipPerPxY);
            var dragLocal = inkDips.Inflate(PadDips);
            var startW = image.TranslatePoint(dragLocal.TopLeft, window)!.Value;
            var endW = image.TranslatePoint(dragLocal.BottomRight, window)!.Value;
            var dragInViewer = new Rect(
                image.TranslatePoint(dragLocal.TopLeft, viewer)!.Value,
                image.TranslatePoint(dragLocal.BottomRight, viewer)!.Value);

            using var before = Capture(window, viewer);

            window.MouseMove(startW);
            window.MouseDown(startW, MouseButton.Left);
            await Task.Delay(30);
            window.MouseMove(new Point((startW.X + endW.X) / 2, (startW.Y + endW.Y) / 2));
            window.MouseMove(endW);
            await Task.Delay(30);
            window.MouseUp(endW, MouseButton.Left);
            // Park the pointer off the page so hover chrome is not in the diff.
            window.MouseMove(new Point(2, 2));
            await WaitForIdleLayout(window);
            await WaitForFinalSinglePageRender(window, viewer);

            // ---- Input side: the saved /Rect is the drag, in PDF points. ----
            var page = vm.PdfCoreDocument!.GetPage(pageNumber);
            double ptPerDip = page.VisualWidth / image.Bounds.Width;
            double padPt = PadDips * ptPerDip;
            var expected = new PdfRectangle(
                TargetLeftPt - padPt, TargetBottomPt - padPt,
                TargetLeftPt + TargetSizePt + padPt, TargetBottomPt + TargetSizePt + padPt);

            await vm.SaveFileAsAsync(output);
            using (var reopened = PdfDocument.Open(File.ReadAllBytes(output)))
            {
                var square = reopened.GetPage(pageNumber).GetAnnotations()
                    .Should().ContainSingle(a => a.Subtype == PdfAnnotationSubtype.Square).Subject;
                var r = square.Rect.Normalize();
                var context =
                    $"dpr={renderScaling} page={pageNumber} zoom={viewer.ZoomLevel:F3}: " +
                    $"dragged around the target, expected /Rect ≈ {Fmt(expected)}, saved {Fmt(r)} " +
                    $"(Δleft={r.Left - expected.Left:F1} Δbottom={r.Bottom - expected.Bottom:F1} " +
                    $"Δright={r.Right - expected.Right:F1} Δtop={r.Top - expected.Top:F1} pt)";
                r.Left.Should().BeApproximately(expected.Left, PdfTolerancePt, context);
                r.Bottom.Should().BeApproximately(expected.Bottom, PdfTolerancePt, context);
                r.Right.Should().BeApproximately(expected.Right, PdfTolerancePt, context);
                r.Top.Should().BeApproximately(expected.Top, PdfTolerancePt, context);
            }

            // ---- Output side: what changed on screen is where the user dragged. ----
            using var after = Capture(window, viewer);
            var changed = ChangedBounds(before, after);
            changed.Width.Should().BeGreaterThan(0, "placing a square must change the viewer's pixels");
            var screenContext =
                $"dpr={renderScaling} page={pageNumber} zoom={viewer.ZoomLevel:F3}: " +
                $"dragged {Fmt(dragInViewer)} in viewer DIPs, the new pixels are at {Fmt(changed)}";
            changed.Left.Should().BeApproximately(dragInViewer.Left, ScreenToleranceDips, screenContext);
            changed.Top.Should().BeApproximately(dragInViewer.Top, ScreenToleranceDips, screenContext);
            changed.Right.Should().BeApproximately(dragInViewer.Right, ScreenToleranceDips, screenContext);
            changed.Bottom.Should().BeApproximately(dragInViewer.Bottom, ScreenToleranceDips, screenContext);
        }
        finally
        {
            window.Close();
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Three Letter pages; only <paramref name="targetPage"/> carries the black
    /// target, so landing on the wrong page shows up as "target not visible".
    /// </summary>
    private static void CreateFixture(string path, int targetPage)
    {
        using var doc = PdfDocument.CreateNew();
        for (var i = 1; i <= 3; i++)
        {
            var page = doc.Pages.AddBlank();
            if (i != targetPage) continue;
            using var g = page.GetGraphics();
            g.DrawRectangle(TargetLeftPt, TargetBottomPt, TargetSizePt, TargetSizePt, PdfBrush.Black);
            g.Flush();
        }
        doc.Save(path);
    }

    private static PixelRect InkBoundsPx(Bitmap bitmap)
    {
        using var sk = Decode(bitmap);
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var y = 0; y < sk.Height; y++)
        for (var x = 0; x < sk.Width; x++)
        {
            var c = sk.GetPixel(x, y);
            if (c.Red >= 128 || c.Green >= 128 || c.Blue >= 128) continue;
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }
        return maxX < 0 ? default : new PixelRect(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>Bounds, in viewer DIPs, of pixels that differ between two captures.</summary>
    internal static Rect ChangedBounds(SKBitmap a, SKBitmap b)
    {
        int w = Math.Min(a.Width, b.Width), h = Math.Min(a.Height, b.Height);
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            SKColor p = a.GetPixel(x, y), q = b.GetPixel(x, y);
            if (Math.Abs(p.Red - q.Red) + Math.Abs(p.Green - q.Green) + Math.Abs(p.Blue - q.Blue) < 60) continue;
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }
        return maxX < 0 ? default : new Rect(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>
    /// The viewer's region of the window's real composited frame, in viewer DIPs
    /// (headless scaling is 1). The compositor path, not RenderTargetBitmap: the
    /// latter re-walks every Image and trips over sources the viewer already
    /// retired.
    /// </summary>
    internal static SKBitmap Capture(Window window, PdfViewerControl viewer)
    {
        using var frame = window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("no rendered frame; check UseHeadlessDrawing=false");
        using var full = Decode(frame);
        var origin = viewer.TranslatePoint(new Point(0, 0), window)!.Value;
        var crop = SKRectI.Create((int)origin.X, (int)origin.Y,
            Math.Min((int)viewer.Bounds.Width, full.Width - (int)origin.X),
            Math.Min((int)viewer.Bounds.Height, full.Height - (int)origin.Y));
        var result = new SKBitmap(crop.Width, crop.Height);
        full.ExtractSubset(result, crop).Should().BeTrue();
        return result.Copy();
    }

    internal static SKBitmap Decode(Bitmap bitmap)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms, PngBitmapEncoderOptions.Default);
        ms.Position = 0;
        return SKBitmap.Decode(ms)
            ?? throw new InvalidOperationException("could not decode bitmap; check UseHeadlessDrawing=false");
    }

    internal static async Task WaitForIdleLayout(Window window)
    {
        for (var i = 0; i < 8; i++) { await Task.Delay(75); window.UpdateLayout(); }
        await KeyboardTestHelpers.FlushDispatcherAsync();
    }

    internal static async Task WaitForFinalSinglePageRender(Window window, PdfViewerControl viewer)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (viewer.SinglePagePublishCount == 0 || viewer.IsLoading || viewer.SinglePagePlaceholderForTests != null)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("the single-page render never published");
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(25);
        }
        window.UpdateLayout();
    }

    private static string Fmt(PdfRectangle r) => $"[{r.Left:F1},{r.Bottom:F1} → {r.Right:F1},{r.Top:F1}]";
    private static string Fmt(Rect r) => $"[{r.Left:F0},{r.Top:F0} → {r.Right:F0},{r.Bottom:F0}]";
}
