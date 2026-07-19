using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using AwesomeAssertions;
using Excise.Avalonia.Controls;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using ReactiveUI;
using SkiaSharp;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// Live-report repro harness: "the overlay for selecting text was way off and
/// the fit button did not zoom properly when I was in text select mode"
/// (real-world book, single-page select-text mode, after zooming).
///
/// The existing selection tests all run at zoom 1.0 — where any missed or
/// doubled zoom factor in the pointer→letter→overlay chain is mathematically
/// invisible (×1). This battery drags a real mouse selection across the first
/// text line of a REAL book page at zoom ≠ 1 (and dpr 2), then verifies with
/// pixels that the drawn selection highlight actually covers the text it
/// selected; and it presses Fit Width inside select-text mode and verifies the
/// page's displayed width actually fits the viewport.
/// </summary>
[Collection("AvaloniaTests")]
public class TextSelectionAlignmentTests
{
    private const string BookPath = "test-pdfs/local-real-world/business-success-with-open-source_P1.0.pdf";

    public static TheoryData<double, double> ZoomByDpr()
    {
        var data = new TheoryData<double, double>();
        foreach (var zoom in new[] { 1.0, 0.6, 1.5 })
        foreach (var dpr in new[] { 1.0, 2.0 })
            data.Add(zoom, dpr);
        return data;
    }

    [FixedAvaloniaTheory]
    [MemberData(nameof(ZoomByDpr))]
    public async Task DragSelection_HighlightCoversTheSelectedText(double zoom, double dpr)
    {
        var book = FindBook();
        Assert.SkipWhen(book == null, "local real-world book corpus not present");

        var vm = new MainWindowViewModel { ThumbnailPrewarmEnabled = false };
        var window = new MainWindow { DataContext = vm, Width = 1100, Height = 900 };
        window.Show();
        try
        {
            await vm.LoadDocumentAsync(book!);
            var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl")!;
            viewer.RenderScalingOverride = dpr;

            // Enter select-text mode (forces single-page) on a body-text page.
            vm.ToggleTextSelectionModeCommand.Execute().Subscribe();
            vm.CurrentPageIndex = 19; // page 20: paragraphs of body text
            await PumpUntilAsync(window, () =>
                viewer.FindControl<Image>("PdfImage")?.Source != null && !viewer.IsLoading);

            // Set the zoom under test and wait for the raster to settle to it.
            vm.ZoomLevel = zoom;
            await SettleAsync(window, viewer, zoom, dpr);

            // Find the text ink in VIEWER coordinates from a clean capture.
            using var beforeSel = Capture(viewer);
            var textBounds = InkBounds(beforeSel);
            textBounds.Width.Should().BeGreaterThan(50, "page 20 must show body text");

            // Drag a selection across the first line of text (window coords).
            var origin = viewer.TranslatePoint(new Point(0, 0), window)!.Value;
            var startX = origin.X + textBounds.Left + 2;
            var y = origin.Y + textBounds.Top + 6;
            var endX = origin.X + textBounds.Right - 2;
            window.MouseDown(new Point(startX, y), MouseButton.Left);
            for (var x = startX; x < endX; x += 40)
                window.MouseMove(new Point(x, y));
            window.MouseMove(new Point(endX, y));
            window.MouseUp(new Point(endX, y), MouseButton.Left);
            await PumpUntilAsync(window, () =>
                (viewer.FindControl<Canvas>("TextSelectionLayer")?.Children.Count ?? 0) > 0,
                timeoutMs: 10000,
                failure: $"dragging across the text at zoom={zoom} dpr={dpr} must produce selection highlights");

            // The highlight layer's rects, mapped to viewer coordinates, must
            // OVERLAP the line we dragged across — 'way off' fails here.
            var layer = viewer.FindControl<Canvas>("TextSelectionLayer")!;
            var rects = layer.Children.OfType<Rectangle>()
                .Select(r =>
                {
                    var tl = layer.TranslatePoint(
                        new Point(Canvas.GetLeft(r), Canvas.GetTop(r)), viewer)!.Value;
                    return new Rect(tl.X, tl.Y, r.Width * ZoomOf(viewer), r.Height * ZoomOf(viewer));
                })
                .ToList();
            rects.Should().NotBeEmpty();
            var union = rects.Aggregate((a, b) => a.Union(b));

            var dragLine = new Rect(textBounds.Left, textBounds.Top,
                textBounds.Width, Math.Max(20, textBounds.Height * 0.15));
            union.Intersects(dragLine).Should().BeTrue(
                $"the selection highlight (at {union}) must overlap the dragged text line " +
                $"(at {dragLine}) — zoom={zoom} dpr={dpr}; a miss means the overlay is 'way off'");

            // And the highlight must not be wildly bigger than the text region.
            union.Width.Should().BeLessThan(textBounds.Width * 1.5,
                "a selection across one line must not paint far beyond the text");
        }
        finally
        {
            window.Close();
        }
    }

    [FixedAvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public async Task FitWidth_InsideSelectTextMode_ActuallyFitsTheViewport(double dpr)
    {
        var book = FindBook();
        Assert.SkipWhen(book == null, "local real-world book corpus not present");

        var vm = new MainWindowViewModel { ThumbnailPrewarmEnabled = false };
        var window = new MainWindow { DataContext = vm, Width = 1100, Height = 900 };
        window.Show();
        try
        {
            await vm.LoadDocumentAsync(book!);
            var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl")!;
            viewer.RenderScalingOverride = dpr;

            vm.ToggleTextSelectionModeCommand.Execute().Subscribe();
            vm.CurrentPageIndex = 19;
            await PumpUntilAsync(window, () =>
                viewer.FindControl<Image>("PdfImage")?.Source != null && !viewer.IsLoading);

            // Churn the zoom the way the live session did, then press Fit Width.
            vm.ZoomLevel = 0.525;
            await SettleAsync(window, viewer, 0.525, dpr);
            vm.ZoomFitWidthCommand.Execute().Subscribe();
            await Task.Delay(200);
            window.UpdateLayout();
            var zoom = ZoomOf(viewer);
            await SettleAsync(window, viewer, zoom, dpr);

            // The page's DISPLAYED width (image dip × zoom) must fit the
            // single-page scroll viewport, and use most of it.
            var img = viewer.FindControl<Image>("PdfImage")!;
            var scroller = viewer.FindControl<ScrollViewer>("PdfScrollViewer")!;
            var displayedWidth = img.Width * zoom;
            var viewport = scroller.Viewport.Width;
            viewport.Should().BeGreaterThan(100, "single-page scroller must have a real viewport");
            displayedWidth.Should().BeLessThanOrEqualTo(viewport + 1,
                $"Fit Width must not overflow the viewport (displayed={displayedWidth:F0}, viewport={viewport:F0}, zoom={zoom:F3})");
            displayedWidth.Should().BeGreaterThan(viewport * 0.75,
                $"Fit Width must actually use the viewport (displayed={displayedWidth:F0}, viewport={viewport:F0}, zoom={zoom:F3})");
        }
        finally
        {
            window.Close();
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static string? FindBook()
    {
        var root = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && root != null; i++, root = System.IO.Path.GetDirectoryName(root))
        {
            var candidate = System.IO.Path.Combine(root, BookPath);
            if (File.Exists(candidate)) return candidate;
        }
        return File.Exists(BookPath) ? System.IO.Path.GetFullPath(BookPath) : null;
    }

    private static double ZoomOf(PdfViewerControl viewer) => viewer.ZoomLevel;

    /// <summary>Wait until the single-page raster corresponds to the zoom×dpr scale.</summary>
    private static async Task SettleAsync(Window window, PdfViewerControl viewer, double zoom, double dpr)
    {
        var deadline = Environment.TickCount64 + 20000;
        while (Environment.TickCount64 < deadline)
        {
            var src = viewer.FindControl<Image>("PdfImage")?.Source as Bitmap;
            if (src != null)
            {
                var scale = Math.Max(1.0, zoom * dpr);
                var expected = src.Size.Width * scale; // dip × scale = device px
                if (Math.Abs(src.PixelSize.Width - expected) <= 10) return;
            }
            await Task.Delay(50);
            window.UpdateLayout();
        }
    }

    private static SKBitmap Capture(PdfViewerControl viewer)
    {
        var w = Math.Max(1, (int)viewer.Bounds.Width);
        var h = Math.Max(1, (int)viewer.Bounds.Height);
        using var rt = new RenderTargetBitmap(new PixelSize(w, h));
        rt.Render(viewer);
        using var ms = new MemoryStream();
        rt.Save(ms);
        ms.Position = 0;
        return SKBitmap.Decode(ms) ?? throw new InvalidOperationException("capture decode failed");
    }

    private const int ChromeMarginPx = 20;

    private static Rect InkBounds(SKBitmap bmp)
    {
        int minX = bmp.Width, minY = bmp.Height, maxX = -1, maxY = -1;
        for (int y = 0; y < bmp.Height - ChromeMarginPx; y++)
        for (int x = 0; x < bmp.Width - ChromeMarginPx; x++)
        {
            var c = bmp.GetPixel(x, y);
            if (c.Alpha > 128 && c.Red + c.Green + c.Blue < 384)
            {
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
        }
        return maxX < 0 ? default : new Rect(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    private static async Task PumpUntilAsync(
        Window window, Func<bool> condition, int timeoutMs = 20000, string? failure = null)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException(failure ?? "condition not met while pumping the dispatcher");
            await Task.Delay(50);
            window.UpdateLayout();
        }
    }

    /// <summary>
    /// Exact choreography of the live automated repro that showed orphaned
    /// highlights over blank space after Fit (screenshots diag-after-fit /
    /// safe-C): continuous -> zoom to 42% -> enter select-text -> drag-select
    /// -> Fit Width. Asserts the display stays coherent: the single-page
    /// scroller is the visible one, and the page image under the selection
    /// highlights actually contains text ink.
    /// </summary>
    [FixedAvaloniaTheory]
    [InlineData(2.0)]
    [InlineData(1.0)]
    public async Task FitAfterSelection_KeepsHighlightsOnRenderedText(double dpr)
    {
        var book = FindBook();
        Assert.SkipWhen(book == null, "local real-world book corpus not present");

        var vm = new MainWindowViewModel { ThumbnailPrewarmEnabled = false };
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        try
        {
            await vm.LoadDocumentAsync(book!);
            var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl")!;
            viewer.RenderScalingOverride = dpr;

            // live sequence: zoom out in CONTINUOUS first, then enter the mode
            vm.CurrentPageIndex = 16;
            vm.ZoomLevel = 0.42;
            await Task.Delay(400); window.UpdateLayout();
            vm.ToggleTextSelectionModeCommand.Execute().Subscribe();
            await PumpUntilAsync(window, () =>
                viewer.FindControl<Image>("PdfImage")?.Source != null && !viewer.IsLoading);
            await SettleAsync(window, viewer, viewer.ZoomLevel, dpr);

            // drag across the page middle to build a selection
            using (var pre = Capture(viewer))
            {
                var ink = InkBounds(pre);
                var origin = viewer.TranslatePoint(new Point(0, 0), window)!.Value;
                var y = origin.Y + ink.Top + ink.Height * 0.4;
                window.MouseDown(new Point(origin.X + ink.Left + 4, y), MouseButton.Left);
                window.MouseMove(new Point(origin.X + ink.Right - 4, y));
                window.MouseUp(new Point(origin.X + ink.Right - 4, y), MouseButton.Left);
            }
            await PumpUntilAsync(window, () =>
                (viewer.FindControl<Canvas>("TextSelectionLayer")?.Children.Count ?? 0) > 0);

            // FIT — the live-repro trigger
            vm.ZoomFitWidthCommand.Execute().Subscribe();
            await Task.Delay(300); window.UpdateLayout();
            await SettleAsync(window, viewer, viewer.ZoomLevel, dpr);
            await Task.Delay(200); window.UpdateLayout();

            // 1) only the single-page scroller may be visible in an editing mode
            var single = viewer.FindControl<ScrollViewer>("PdfScrollViewer")!;
            var continuous = viewer.FindControl<ScrollViewer>("ContinuousScrollViewer")!;
            single.IsVisible.Should().BeTrue("select-text mode displays the single-page scroller");
            continuous.IsVisible.Should().BeFalse(
                "the continuous scroller must be hidden in an editing mode — a visible one paints stale tiles over/under the page");

            // 2) the highlight rects must sit on ink (text), not blank space
            var layer = viewer.FindControl<Canvas>("TextSelectionLayer")!;
            layer.Children.Count.Should().BeGreaterThan(0, "the selection must survive the fit");
            using var after = Capture(viewer);
            var rect0 = layer.Children.OfType<global::Avalonia.Controls.Shapes.Rectangle>().First();
            var tl = layer.TranslatePoint(new Point(Canvas.GetLeft(rect0), Canvas.GetTop(rect0)), viewer)!.Value;
            var zoom = viewer.ZoomLevel;
            var probe = new SKRectI(
                Math.Max(0, (int)(tl.X - 30)), Math.Max(0, (int)(tl.Y - 10)),
                Math.Min(after.Width, (int)(tl.X + rect0.Width * zoom + 60)),
                Math.Min(after.Height, (int)(tl.Y + rect0.Height * zoom + 30)));
            var inkNearHighlight = RegionInk(after, probe);
            inkNearHighlight.Should().BeGreaterThan(0,
                $"after Fit the highlight (viewer {tl}) must still sit on rendered text, " +
                "not float over blank space (live repro: diag-after-fit.png)");
        }
        finally
        {
            window.Close();
        }
    }

    private static int RegionInk(SKBitmap bmp, SKRectI r)
    {
        int count = 0;
        for (int y = r.Top; y < r.Bottom; y++)
        for (int x = r.Left; x < r.Right; x++)
        {
            var c = bmp.GetPixel(x, y);
            if (c.Alpha > 128 && c.Red + c.Green + c.Blue < 384) count++;
        }
        return count;
    }

}
