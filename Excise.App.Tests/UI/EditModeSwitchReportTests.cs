using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Excise.Avalonia.Controls;
using Excise.App.Tests.Controls;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using SkiaSharp;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// Report-only measurement of the switch from continuous view into an editing
/// mode (typewriter), which leaves continuous view for single-page (#1473). It
/// asserts nothing about speed. Per repetition it opens the document in a fresh
/// window, navigates, waits for the viewer to go quiet, then records:
/// <list type="bullet">
/// <item>open cost: wall time to quiet, process CPU, managed bytes allocated,
/// and the bytes held by the single-page and continuous caches afterwards;</item>
/// <item>time from <c>ToggleTypewriterModeCommand</c> to a visible page image
/// (placeholder or final) and to the final single-page render;</item>
/// <item>a second window that clicks one layout pass after the toggle (no
/// dispatcher pump, no render wait) and compares the placed box with where the
/// same window point maps once the final render has laid out.</item>
/// </list>
/// Opt in with <c>EXCISE_EDITMODE_BENCH=1</c>. Knobs: <c>EXCISE_EDITMODE_PDF</c>
/// (required), <c>EXCISE_EDITMODE_PAGE</c> (1-based, default 1),
/// <c>EXCISE_EDITMODE_REPS</c> (default 3), <c>EXCISE_EDITMODE_LABEL</c>,
/// <c>EXCISE_EDITMODE_OFFSET=1</c> (also capture placeholder vs final ink
/// bounds once), <c>EXCISE_EDITMODE_OUT</c> (append rows to this file).
/// Thumbnail prewarm is off so its renders do not compete with the ones measured.
/// </summary>
[Collection("AvaloniaTests")]
public sealed class EditModeSwitchReportTests
{
    private static readonly TimeSpan PhaseTimeout = TimeSpan.FromSeconds(90);
    private readonly ITestOutputHelper _out;
    private readonly StringBuilder _lines = new();
    private double _dpr = 1.0;

    public EditModeSwitchReportTests(ITestOutputHelper output) => _out = output;

    [FixedAvaloniaFact]
    public async Task ToggleTypewriter_FromContinuous_ReportsSwitchLatencyOpenCostAndClickPlacement()
    {
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("EXCISE_EDITMODE_BENCH") == "1",
            "Report-only edit-mode switch measurement (#1473); set EXCISE_EDITMODE_BENCH=1 to run it.");
        var pdf = Environment.GetEnvironmentVariable("EXCISE_EDITMODE_PDF");
        Assert.SkipUnless(pdf is not null && File.Exists(pdf),
            "EXCISE_EDITMODE_PDF must name an existing PDF for the edit-mode switch measurement.");

        var page = ReadInt("EXCISE_EDITMODE_PAGE", 1);
        var reps = ReadInt("EXCISE_EDITMODE_REPS", 3);
        var label = Environment.GetEnvironmentVariable("EXCISE_EDITMODE_LABEL") ?? "unlabelled";
        var outPath = Environment.GetEnvironmentVariable("EXCISE_EDITMODE_OUT");
        _dpr = double.TryParse(Environment.GetEnvironmentVariable("EXCISE_EDITMODE_DPR"),
            NumberStyles.Float, CultureInfo.InvariantCulture, out var dpr) && dpr > 0 ? dpr : 1.0;

        Emit($"# label={label} pdf={Path.GetFileName(pdf)} page={page} reps={reps} dpr={F(_dpr, "0.##")} " +
             $"pid={Environment.ProcessId} cores={Environment.ProcessorCount} started={DateTime.Now:O}");
        Emit("label\tfixture\tpage\trep\tidleWaitMs\topenWallMs\topenCpuMs\topenAllocMB\tsingleCacheMB\tsingleEntries\tcontCacheMB\t" +
             "zoomBefore\tzoomAfter\tsingleHitsDelta\ttVisibleMs\ttFinalMs\tvisibleWasFinal\tpollLoops\t" +
             "clickImgSized\tclickSrc\tboxCount\tboxPage\tboxErrPt\tboxLeft\tboxTop\texpLeft\texpTop");

        var rows = new List<RepResult>();
        for (var rep = 1; rep <= reps; rep++)
        {
            var timing = await TimingRepAsync(pdf!, page);
            var click = await ClickRepAsync(pdf!, page, timing.AimWindowPoint);
            rows.Add(new RepResult(timing, click));
            Emit(string.Join('\t', label, Path.GetFileName(pdf), page, rep,
                F(timing.Open.IdleWaitMs), F(timing.Open.WallMs), F(timing.Open.CpuMs), F(Mb(timing.Open.AllocatedBytes)),
                F(Mb(timing.Open.SingleCacheBytes)), timing.Open.SingleEntries, F(Mb(timing.Open.ContinuousBytes)),
                F(timing.ZoomBefore, "0.000"), F(timing.ZoomAfter, "0.000"), timing.SingleHitsDelta,
                F(timing.VisibleMs), F(timing.FinalMs), timing.VisibleWasFinal, timing.PollLoops,
                click.ImageSizedAtClick, click.SourceAtClick, click.BoxCount, click.BoxPage,
                F(click.ErrorPt, "0.00"), F(click.BoxLeft), F(click.BoxTop), F(click.ExpectedLeft), F(click.ExpectedTop)));
        }

        Emit(string.Join('\t', "# median", label, Path.GetFileName(pdf), page,
            $"openWallMs={F(Median(rows.Select(r => r.Timing.Open.WallMs)))}",
            $"openCpuMs={F(Median(rows.Select(r => r.Timing.Open.CpuMs)))}",
            $"openAllocMB={F(Median(rows.Select(r => Mb(r.Timing.Open.AllocatedBytes))))}",
            $"singleCacheMB={F(Median(rows.Select(r => Mb(r.Timing.Open.SingleCacheBytes))))}",
            $"contCacheMB={F(Median(rows.Select(r => Mb(r.Timing.Open.ContinuousBytes))))}",
            $"tVisibleMs={F(Median(rows.Select(r => r.Timing.VisibleMs)))}",
            $"tFinalMs={F(Median(rows.Select(r => r.Timing.FinalMs)))}",
            $"clickErrPt={F(Median(rows.Select(r => r.Click.ErrorPt)), "0.00")}",
            $"clickBoxes={string.Join(',', rows.Select(r => r.Click.BoxCount))}"));

        if (Environment.GetEnvironmentVariable("EXCISE_EDITMODE_OFFSET") == "1")
            await OffsetRepAsync(pdf!, page, label);

        if (!string.IsNullOrEmpty(outPath))
            File.AppendAllText(outPath, _lines.ToString());
    }

    private async Task<TimingResult> TimingRepAsync(string pdf, int page)
    {
        var (vm, window, viewer) = await ShowAsync();
        try
        {
            var open = await OpenAndSettleAsync(vm, window, viewer, pdf, page);

            var zoomBefore = vm.ZoomLevel;
            var hits0 = viewer.GetRenderDiagnostics().SinglePageHits;
            var publish0 = viewer.SinglePagePublishCount;
            double visibleMs = -1, finalMs = -1;
            var visibleWasFinal = false;
            long loops = 0;

            var sw = Stopwatch.StartNew();
            vm.ToggleTypewriterModeCommand.Execute().Subscribe();
            while (sw.Elapsed < PhaseTimeout)
            {
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                var visible = PageImageVisible(viewer);
                var published = viewer.SinglePagePublishCount > publish0;
                if (visibleMs < 0 && visible)
                {
                    visibleMs = sw.Elapsed.TotalMilliseconds;
                    visibleWasFinal = published;
                }
                if (visible && published && !viewer.IsLoading)
                {
                    finalMs = sw.Elapsed.TotalMilliseconds;
                    break;
                }
                loops++;
                await Task.Yield();
            }
            if (finalMs < 0)
                throw new TimeoutException($"single-page render never published: {Describe(viewer)}");

            await SettleAsync(window, 3);
            var aim = AimWindowPoint(window, viewer);
            return new TimingResult(open, zoomBefore, vm.ZoomLevel,
                viewer.GetRenderDiagnostics().SinglePageHits - hits0,
                visibleMs, finalMs, visibleWasFinal, loops, aim);
        }
        finally
        {
            window.Close();
        }
    }

    private async Task<ClickResult> ClickRepAsync(string pdf, int page, Point aim)
    {
        var (vm, window, viewer) = await ShowAsync();
        try
        {
            await OpenAndSettleAsync(vm, window, viewer, pdf, page);
            var publish0 = viewer.SinglePagePublishCount;
            var image = viewer.FindControl<Image>("PdfImage")!;
            var zoomHost = viewer.FindControl<Control>("ZoomHost")!;

            // "0 ms": the toggle, then the single layout pass the next frame
            // would run, then the click. No dispatcher pump and no render wait.
            vm.ToggleTypewriterModeCommand.Execute().Subscribe();
            window.UpdateLayout();
            var imageSized = zoomHost.Bounds.Width > 0 && zoomHost.Bounds.Height > 0;
            var sourceAtClick = image.Source != null;
            window.MouseDown(aim, MouseButton.Left);
            window.MouseUp(aim, MouseButton.Left);

            var sw = Stopwatch.StartNew();
            while (!(PageImageVisible(viewer) && viewer.SinglePagePublishCount > publish0 && !viewer.IsLoading))
            {
                if (sw.Elapsed > PhaseTimeout)
                    throw new TimeoutException($"single-page render never published after the click: {Describe(viewer)}");
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(10);
            }
            await SettleAsync(window, 3);

            // Where the same window point lands on the final layout: the box a
            // click on the sharp page would have placed.
            var overlay = viewer.FindControl<Canvas>("OverlayCanvas")!;
            var local = window.TranslatePoint(aim, overlay)
                ?? throw new InvalidOperationException("overlay not attached to the window");
            var expected = viewer.ViewerDipsToPdfRect(
                viewer.NormalizeTypewriterDipRect(new Rect(local.X, local.Y, 0, 0)), page);

            var ops = vm.TypewriterTextOperations.ToList();
            if (ops.Count == 0)
                return new ClickResult(imageSized, sourceAtClick, 0, 0, double.NaN,
                    double.NaN, double.NaN, expected.Left, expected.Top);
            var op = ops[0];
            var error = Math.Sqrt(Math.Pow(op.Bounds.Left - expected.Left, 2) + Math.Pow(op.Bounds.Top - expected.Top, 2));
            return new ClickResult(imageSized, sourceAtClick, ops.Count, op.PageNumber, error,
                op.Bounds.Left, op.Bounds.Top, expected.Left, expected.Top);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Once per fixture: capture the page image the moment it is visible and
    /// again once the final render lands, then compare the ink bounds inside the
    /// ZoomHost. The two rasters come from different DPIs, so whole-image
    /// shifts would be dominated by resampling; ink-bounds edges are not.
    /// </summary>
    private async Task OffsetRepAsync(string pdf, int page, string label)
    {
        var (vm, window, viewer) = await ShowAsync();
        try
        {
            await OpenAndSettleAsync(vm, window, viewer, pdf, page);
            var publish0 = viewer.SinglePagePublishCount;
            vm.ToggleTypewriterModeCommand.Execute().Subscribe();
            var sw = Stopwatch.StartNew();
            while (!PageImageVisible(viewer))
            {
                if (sw.Elapsed > PhaseTimeout) throw new TimeoutException(Describe(viewer));
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                await Task.Yield();
            }
            var firstWasFinal = viewer.SinglePagePublishCount > publish0;
            var firstGeometry = Geometry(viewer);
            using var first = CaptureZoomHost(viewer);

            while (!(viewer.SinglePagePublishCount > publish0 && !viewer.IsLoading))
            {
                if (sw.Elapsed > PhaseTimeout) throw new TimeoutException(Describe(viewer));
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(10);
            }
            await SettleAsync(window, 3);
            var finalGeometry = Geometry(viewer);
            using var final = CaptureZoomHost(viewer);

            var a = InkBounds(first);
            var b = InkBounds(final);
            Emit(string.Join('\t', "# offset", label, Path.GetFileName(pdf), page,
                $"firstWasFinal={firstWasFinal}",
                $"capture={first.Width}x{first.Height}/{final.Width}x{final.Height}",
                $"firstInk={a}", $"finalInk={b}",
                $"dLeft={b.Left - a.Left}", $"dTop={b.Top - a.Top}",
                $"dRight={b.Right - a.Right}", $"dBottom={b.Bottom - a.Bottom}",
                $"inkFraction={InkFraction(first):0.0000}/{InkFraction(final):0.0000}"));
            Emit(string.Join('\t', "# geometry", label, Path.GetFileName(pdf), page,
                $"first: {firstGeometry}", $"final: {finalGeometry}"));
        }
        finally
        {
            window.Close();
        }
    }

    private async Task<(MainWindowViewModel Vm, MainWindow Window, PdfViewerControl Viewer)> ShowAsync()
    {
        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await SettleAsync(window, 4);
        var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl")
            ?? throw new InvalidOperationException("MainWindow has no PdfViewerControl");
        // The headless host always reports RenderScaling 1; a Retina display
        // renders four times the pixels, so the switch is measured at both.
        viewer.RenderScalingOverride = _dpr;
        return (vm, window, viewer);
    }

    /// <summary>
    /// Waits until the process has been nearly idle for two consecutive 200 ms
    /// windows, so background work left by the previous window (text indexing,
    /// #1469) is not billed to the next open. Capped at 30 s.
    /// </summary>
    private static async Task<double> WaitForProcessIdleAsync(Window window)
    {
        using var process = Process.GetCurrentProcess();
        var sw = Stopwatch.StartNew();
        var quietWindows = 0;
        while (quietWindows < 2 && sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            process.Refresh();
            var cpu0 = process.TotalProcessorTime;
            var wall0 = sw.Elapsed;
            await Task.Delay(200);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            process.Refresh();
            var coreShare = (process.TotalProcessorTime - cpu0).TotalMilliseconds
                / Math.Max(1, (sw.Elapsed - wall0).TotalMilliseconds);
            quietWindows = coreShare < 0.15 ? quietWindows + 1 : 0;
        }
        return sw.Elapsed.TotalMilliseconds;
    }

    private static async Task<OpenCost> OpenAndSettleAsync(
        MainWindowViewModel vm, Window window, PdfViewerControl viewer, string pdf, int page)
    {
        using var process = Process.GetCurrentProcess();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var idleWaitMs = await WaitForProcessIdleAsync(window);
        process.Refresh();
        var cpu0 = process.TotalProcessorTime;
        var alloc0 = GC.GetTotalAllocatedBytes(precise: false);
        var sw = Stopwatch.StartNew();

        await vm.LoadDocumentAsync(pdf);
        if (page > 1)
            vm.CurrentPageIndex = page - 1;

        var items = viewer.FindControl<ItemsControl>("ContinuousItems")
            ?? throw new InvalidOperationException("viewer has no ContinuousItems");
        await ContinuousTileEvictionCompositeTests.WaitForSettledCompositeAsync(window, viewer, items, page);
        var quiet = 0;
        while (quiet < 4)
        {
            if (sw.Elapsed > PhaseTimeout)
                throw new TimeoutException($"viewer did not go quiet after open: {Describe(viewer)}");
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            quiet = viewer.HasPendingSinglePageWork || viewer.ContinuousInFlightCount > 0 ? 0 : quiet + 1;
            await Task.Delay(25);
        }

        var wall = sw.Elapsed.TotalMilliseconds;
        process.Refresh();
        var cpu = (process.TotalProcessorTime - cpu0).TotalMilliseconds;
        var alloc = GC.GetTotalAllocatedBytes(precise: false) - alloc0;
        if (viewer.ViewMode != PdfViewMode.Continuous || viewer.CurrentPage != page)
            throw new InvalidOperationException($"expected continuous view on page {page}: {Describe(viewer)}");

        var diagnostics = viewer.GetRenderDiagnostics();
        return new OpenCost(wall, cpu, alloc, viewer.SinglePageCacheResidentBytes(),
            diagnostics.SinglePageEntryCount, diagnostics.ContinuousResidentBytes, idleWaitMs);
    }

    private static bool PageImageVisible(PdfViewerControl viewer)
    {
        var image = viewer.FindControl<Image>("PdfImage");
        var zoomHost = viewer.FindControl<Control>("ZoomHost");
        return viewer.ViewMode == PdfViewMode.SinglePage
            && image?.Source != null
            && image.IsEffectivelyVisible
            && zoomHost != null && zoomHost.Bounds.Width > 0 && zoomHost.Bounds.Height > 0;
    }

    /// <summary>The window point over (50%, 40%) of the laid-out page.</summary>
    private static Point AimWindowPoint(Window window, PdfViewerControl viewer)
    {
        var image = viewer.FindControl<Image>("PdfImage")!;
        var overlay = viewer.FindControl<Canvas>("OverlayCanvas")!;
        var local = new Point(image.Bounds.Width * 0.5, image.Bounds.Height * 0.4);
        return overlay.TranslatePoint(local, window)
            ?? throw new InvalidOperationException("overlay not attached to the window");
    }

    /// <summary>Layout geometry of the page Image and ZoomHost, for the offset line.</summary>
    private static string Geometry(PdfViewerControl viewer)
    {
        var image = viewer.FindControl<Image>("PdfImage")!;
        var zoomHost = viewer.FindControl<Control>("ZoomHost")!;
        var origin = zoomHost.TranslatePoint(default, viewer) ?? default;
        var pixels = image.Source is Bitmap b ? $"{b.PixelSize.Width}x{b.PixelSize.Height}" : "none";
        return string.Create(CultureInfo.InvariantCulture,
            $"imgWH={image.Width:F2}x{image.Height:F2} zoomHostWH={zoomHost.Bounds.Width:F2}x{zoomHost.Bounds.Height:F2} zoomHostOrigin={origin.X:F2},{origin.Y:F2} srcPx={pixels}");
    }

    private static SKBitmap CaptureZoomHost(PdfViewerControl viewer)
    {
        var w = Math.Max(1, (int)viewer.Bounds.Width);
        var h = Math.Max(1, (int)viewer.Bounds.Height);
        using var rt = new RenderTargetBitmap(new PixelSize(w, h));
        rt.Render(viewer);
        using var ms = new MemoryStream();
        rt.Save(ms, PngBitmapEncoderOptions.Default);
        ms.Position = 0;
        using var whole = SKBitmap.Decode(ms)
            ?? throw new InvalidOperationException("could not decode the viewer capture");

        var zoomHost = viewer.FindControl<Control>("ZoomHost")!;
        var topLeft = zoomHost.TranslatePoint(default, viewer) ?? default;
        var host = zoomHost.Bounds;
        var bottomRight = zoomHost.TranslatePoint(new Point(host.Width, host.Height), viewer) ?? default;
        var crop = SKRectI.Intersect(
            new SKRectI((int)Math.Floor(topLeft.X), (int)Math.Floor(topLeft.Y),
                (int)Math.Ceiling(bottomRight.X), (int)Math.Ceiling(bottomRight.Y)),
            new SKRectI(0, 0, whole.Width, whole.Height));
        var result = new SKBitmap(Math.Max(1, crop.Width), Math.Max(1, crop.Height));
        using var canvas = new SKCanvas(result);
        canvas.DrawBitmap(whole, crop, new SKRect(0, 0, result.Width, result.Height));
        return result;
    }

    private static SKRectI InkBounds(SKBitmap bmp)
    {
        int minX = bmp.Width, minY = bmp.Height, maxX = -1, maxY = -1;
        for (var y = 0; y < bmp.Height; y++)
        for (var x = 0; x < bmp.Width; x++)
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
        return maxX < 0 ? SKRectI.Empty : new SKRectI(minX, minY, maxX + 1, maxY + 1);
    }

    private static double InkFraction(SKBitmap bmp)
    {
        long ink = 0;
        for (var y = 0; y < bmp.Height; y++)
        for (var x = 0; x < bmp.Width; x++)
        {
            var c = bmp.GetPixel(x, y);
            if (c.Alpha > 128 && c.Red + c.Green + c.Blue < 384) ink++;
        }
        return (double)ink / Math.Max(1, (long)bmp.Width * bmp.Height);
    }

    private static async Task SettleAsync(Window window, int pumps)
    {
        for (var i = 0; i < pumps; i++)
        {
            await Task.Delay(50);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static string Describe(PdfViewerControl viewer)
    {
        var image = viewer.FindControl<Image>("PdfImage");
        var zoomHost = viewer.FindControl<Control>("ZoomHost");
        return $"viewMode={viewer.ViewMode} page={viewer.CurrentPage} loading={viewer.IsLoading} " +
               $"src={image?.Source != null} zoomHost={zoomHost?.Bounds} published={viewer.SinglePagePublishCount} " +
               $"contInFlight={viewer.ContinuousInFlightCount} error={viewer.ErrorMessage}";
    }

    private void Emit(string line)
    {
        _out.WriteLine(line);
        _lines.AppendLine(line);
    }

    private static int ReadInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v : fallback;

    private static double Mb(long bytes) => bytes / 1024.0 / 1024.0;

    private static string F(double value, string format = "0.0") =>
        value.ToString(format, CultureInfo.InvariantCulture);

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Where(v => !double.IsNaN(v)).OrderBy(v => v).ToArray();
        if (sorted.Length == 0) return double.NaN;
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    private readonly record struct OpenCost(
        double WallMs, double CpuMs, long AllocatedBytes, long SingleCacheBytes, int SingleEntries, long ContinuousBytes,
        double IdleWaitMs);

    private readonly record struct TimingResult(
        OpenCost Open, double ZoomBefore, double ZoomAfter, long SingleHitsDelta,
        double VisibleMs, double FinalMs, bool VisibleWasFinal, long PollLoops, Point AimWindowPoint);

    private readonly record struct ClickResult(
        bool ImageSizedAtClick, bool SourceAtClick, int BoxCount, int BoxPage, double ErrorPt,
        double BoxLeft, double BoxTop, double ExpectedLeft, double ExpectedTop);

    private readonly record struct RepResult(TimingResult Timing, ClickResult Click);
}
