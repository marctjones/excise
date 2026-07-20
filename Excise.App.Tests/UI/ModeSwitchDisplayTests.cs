using System;
using System.IO;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using AwesomeAssertions;
using Excise.Avalonia.Controls;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using ReactiveUI;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// Display-state invariants for the interaction-mode toolbar buttons
/// (Redact / Select Text / Typewriter / Form Authoring). Prompted by a live
/// report of "something weird happens to the display" when clicking these.
///
/// Every editing mode forces the viewer from the default Continuous view into
/// SinglePage — which is exactly the render path reworked for device-resolution
/// output (#682/#683/#685/#686). Those changes are a functional no-op at
/// devicePixelRatio 1 (all the headless test host reports), so this battery
/// runs each mode switch at BOTH dpr 1.0 and a simulated Retina dpr 2.0 (via
/// <see cref="PdfViewerControl.RenderScalingOverride"/>) and asserts the
/// invariants that make the mode switch visually sane:
///
///   • the single-page image appears, with a Source;
///   • its layout (DIP) size equals the page's logical size — INDEPENDENT of
///     dpr (the size-invariance contract of the device-resolution render);
///   • its backing pixels scale WITH dpr (the crispness contract);
///   • mode flags are mutually exclusive; toggling off returns to Continuous.
/// </summary>
[Collection("AvaloniaTests")]
public class ModeSwitchDisplayTests
{
    private const double LetterWidthPt = 612;

    public static TheoryData<string, double> ModeByDpr()
    {
        var data = new TheoryData<string, double>();
        foreach (var mode in new[] { "redact", "select-text", "typewriter", "form-authoring" })
        foreach (var dpr in new[] { 1.0, 2.0 })
            data.Add(mode, dpr);
        return data;
    }

    [FixedAvaloniaTheory]
    [MemberData(nameof(ModeByDpr))]
    public async Task ModeSwitch_ShowsSinglePage_WithSizeInvariantImage(string mode, double dpr)
    {
        var (vm, window, viewer, path) = await OpenTestDocumentAsync();
        try
        {
            viewer.RenderScalingOverride = dpr;
            vm.ViewMode.Should().Be(PdfViewMode.Continuous, "reading view is the default");

            ModeCommand(vm, mode).Execute().Subscribe();
            await PumpUntilAsync(window, () => SinglePageImage(viewer)?.Source != null);

            // The switch lands in single-page with a rendered page.
            vm.ViewMode.Should().Be(PdfViewMode.SinglePage,
                $"{mode} mode is single-page only");
            ModeFlag(vm, mode).Should().BeTrue();

            var img = SinglePageImage(viewer)!;
            img.IsVisible.Should().BeTrue();
            var source = img.Source!;

            // SIZE INVARIANCE: layout size equals the page's logical size no
            // matter the display density. A dpr-dependent layout size is
            // exactly the class of "weird display" this battery exists to catch.
            var logicalDpi = 120; // DefaultRenderDpi for a normal page
            var expectedDipWidth = LetterWidthPt * logicalDpi / 72.0;
            img.Width.Should().BeApproximately(expectedDipWidth, 2.0,
                "the page's on-screen size must not depend on the device pixel ratio");
            (source as global::Avalonia.Media.Imaging.Bitmap)!.Size.Width.Should().BeApproximately(
                (source as global::Avalonia.Media.Imaging.Bitmap)!.PixelSize.Width, 0.5,
                "the bitmap must be 96-dpi-stamped (Size == PixelSize) — Avalonia's Image " +
                "mispaints non-96-stamped bitmaps as a magnified top-left crop (#697); " +
                "the logical page size lives on the Image's Width instead");

            // CRISPNESS: the backing pixels match the on-screen magnification —
            // the app enters modes at its current (often fit-width) zoom, so the
            // device scale is zoom × dpr, floored at 1 (never below base render).
            //
            // The zoom-triggered re-render (#686) is async: right after the mode
            // switch the Image may briefly hold the PREVIOUS zoom's raster while
            // fit-width settles. Asserting instantly raced that re-render and
            // flaked on macOS/Windows CI (found 2040 = zoom 1.0 raster while
            // live zoom was already 0.79). Pump until the raster corresponds to
            // the CURRENT zoom, then assert with the settled values.
            int ExpectedPx() => (int)Math.Round(
                expectedDipWidth * Math.Max(1.0, viewer.ZoomLevel * dpr));
            int PixelWidth() => (SinglePageImage(viewer)?.Source as
                global::Avalonia.Media.Imaging.Bitmap)?.PixelSize.Width ?? -1;
            var settleDeadline = Environment.TickCount64 + 20000;
            while (Math.Abs(PixelWidth() - ExpectedPx()) > 8 &&
                   Environment.TickCount64 < settleDeadline)
            {
                await Task.Delay(50);
                window.UpdateLayout();
            }
            PixelWidth().Should().BeCloseTo(ExpectedPx(), 8,
                $"the raster must settle to zoom×dpr pixels (zoom={viewer.ZoomLevel:F2}, dpr={dpr}) so text is crisp");
            if (dpr > 1)
                PixelWidth().Should().BeGreaterThan((int)expectedDipWidth,
                    "on HiDPI the raster must exceed the logical size — otherwise it upscales and softens");

            // Only this mode is active.
            foreach (var other in new[] { "redact", "select-text", "typewriter", "form-authoring" })
                if (other != mode)
                    ModeFlag(vm, other).Should().BeFalse($"{other} must be off while {mode} is active");

            // Toggling off returns to the reading view.
            ModeCommand(vm, mode).Execute().Subscribe();
            await PumpUntilAsync(window, () => vm.ViewMode == PdfViewMode.Continuous);
            ModeFlag(vm, mode).Should().BeFalse();
        }
        finally
        {
            window.Close();
            TestPdfGenerator.CleanupTestFile(path);
        }
    }

    [FixedAvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public async Task SwitchingBetweenModes_KeepsSinglePageAndImageStable(double dpr)
    {
        var (vm, window, viewer, path) = await OpenTestDocumentAsync();
        try
        {
            viewer.RenderScalingOverride = dpr;

            ModeCommand(vm, "redact").Execute().Subscribe();
            await PumpUntilAsync(window, () => SinglePageImage(viewer)?.Source != null);
            var widthInRedact = SinglePageImage(viewer)!.Width;

            // Hop directly between editing modes — no bounce through continuous.
            foreach (var next in new[] { "select-text", "typewriter", "form-authoring", "redact" })
            {
                ModeCommand(vm, next).Execute().Subscribe();
                await PumpUntilAsync(window, () => ModeFlag(vm, next));
                vm.ViewMode.Should().Be(PdfViewMode.SinglePage,
                    $"switching to {next} must stay in single-page view");
                var img = SinglePageImage(viewer)!;
                img.Source.Should().NotBeNull($"the page image must survive switching to {next}");
                img.Width.Should().BeApproximately(widthInRedact, 2.0,
                    $"the page's on-screen size must not jump when switching to {next}");
            }
        }
        finally
        {
            window.Close();
            TestPdfGenerator.CleanupTestFile(path);
        }
    }

    [FixedAvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public async Task ZoomInsideMode_KeepsLayoutSize_AndSharpensPixels(double dpr)
    {
        var (vm, window, viewer, path) = await OpenTestDocumentAsync();
        try
        {
            viewer.RenderScalingOverride = dpr;
            ModeCommand(vm, "redact").Execute().Subscribe();
            await PumpUntilAsync(window, () => SinglePageImage(viewer)?.Source != null);

            var img = SinglePageImage(viewer)!;
            var dipBefore = img.Width;
            var pixelsBefore = ((global::Avalonia.Media.Imaging.Bitmap)img.Source!).PixelSize.Width;

            viewer.ZoomLevel = 2.0;
            await PumpUntilAsync(window, () =>
                SinglePageImage(viewer)?.Source is global::Avalonia.Media.Imaging.Bitmap b &&
                b.PixelSize.Width > pixelsBefore);

            // #683: zoom re-renders at higher resolution, but layout (pre-
            // ScaleTransform) size is unchanged — coordinates stay valid.
            img.Width.Should().BeApproximately(dipBefore, 2.0,
                "zoom must not change the image's layout size (the ScaleTransform handles magnification)");
        }
        finally
        {
            window.Close();
            TestPdfGenerator.CleanupTestFile(path);
        }
    }

    /// <summary>
    /// #693 (size half): entering an editing mode must not change the page's
    /// on-screen size. Continuous lays out at 96-dpi dips (pt × 96/72 × zoom);
    /// before the fix, single-page displayed its 120-dpi layout at raw zoom —
    /// the same page appeared 25% larger the moment a mode button was clicked.
    /// The ZoomHost's transformed bounds ARE the displayed size.
    /// </summary>
    [FixedAvaloniaTheory]
    [InlineData(1.0, 1.0)]
    [InlineData(1.0, 2.0)]
    [InlineData(0.6, 1.0)]
    [InlineData(0.6, 2.0)]
    public async Task ModeEntry_PreservesDisplayedPageSize(double zoom, double dpr)
    {
        var (vm, window, viewer, path) = await OpenTestDocumentAsync();
        try
        {
            viewer.RenderScalingOverride = dpr;
            vm.SetManualZoom(zoom);
            await Task.Delay(200); window.UpdateLayout();

            ModeCommand(vm, "select-text").Execute().Subscribe();
            await PumpUntilAsync(window, () => SinglePageImage(viewer)?.Source != null);
            window.UpdateLayout();

            var zoomHost = viewer.FindControl<global::Avalonia.Controls.LayoutTransformControl>("ZoomHost")!;
            var displayed = zoomHost.Bounds.Width;
            var continuousEquivalent = LetterWidthPt * (96.0 / 72.0) * viewer.ZoomLevel;
            displayed.Should().BeApproximately(continuousEquivalent, 3.0,
                $"the single-page displayed width must equal the continuous layout width " +
                $"(pt × 96/72 × zoom) — a jump means the mode switch rescales the page (#693); " +
                $"zoom={viewer.ZoomLevel:F3} dpr={dpr}");
        }
        finally
        {
            window.Close();
            TestPdfGenerator.CleanupTestFile(path);
        }
    }

    /// <summary>
    /// #700: zooming in continuous view must anchor the viewport. Before the
    /// fix, a re-layout kept the numeric scroll offset while every slot
    /// changed size — zooming out slid the viewport pages away from the
    /// reading position, and because Offset never changed, no scroll event
    /// fired and CurrentPage silently froze (live: four zoom-outs left the
    /// label on page 17 while the screen showed page ~22). Correct anchoring
    /// preserves the offset/extent RATIO (slots scale uniformly) and keeps
    /// CurrentPage truthful.
    /// </summary>
    [FixedAvaloniaTheory]
    [InlineData(0.5)]
    [InlineData(2.0)]
    public async Task ContinuousZoom_AnchorsTheViewportAndKeepsPageSync(double zoomFactor)
    {
        var (vm, window, viewer, path) = await OpenTestDocumentAsync();
        try
        {
            var cont = viewer.FindControl<ScrollViewer>("ContinuousScrollViewer")!;
            await PumpUntilAsync(window, () => cont.Extent.Height > cont.Viewport.Height + 100);

            // Scroll mid-document, then nudge to land mid-page (see the
            // reading-position test for why the two-step dance).
            cont.Offset = new global::Avalonia.Vector(cont.Offset.X, cont.Extent.Height * 0.45);
            await Task.Delay(150); window.UpdateLayout();
            cont.Offset = new global::Avalonia.Vector(cont.Offset.X, cont.Offset.Y + cont.Extent.Height / 3 * 0.4);
            await Task.Delay(150); window.UpdateLayout();

            var pageBefore = vm.CurrentPageIndex;
            var ratioBefore = cont.Offset.Y / cont.Extent.Height;
            ratioBefore.Should().BeGreaterThan(0.1, "the test must start scrolled into the document");

            var zoomBefore = viewer.ZoomLevel;
            var extentBefore = cont.Extent.Height;
            var offsetAtZoom = cont.Offset.Y;
            vm.SetManualZoom(viewer.ZoomLevel * zoomFactor);
            // The anchored offset is applied once layout gives the
            // ScrollViewer its new extent — wait for convergence rather
            // than a fixed delay.
            // Correct anchoring preserves the offset/extent ratio, EXCEPT
            // when the position is no longer reachable (deep zoom-out near
            // the document end: the viewport covers proportionally more, so
            // the max scroll ratio shrinks) — then pinning at max IS the
            // anchor.
            double ExpectedRatio() => Math.Min(ratioBefore,
                Math.Max(0, cont.Extent.Height - cont.Viewport.Height) / cont.Extent.Height);
            try
            {
                await PumpUntilAsync(window, () =>
                    Math.Abs(cont.Offset.Y / cont.Extent.Height - ExpectedRatio()) <= 0.03,
                    timeoutMs: 10000);
            }
            catch (TimeoutException)
            {
                throw new Xunit.Sdk.XunitException(
                    $"anchor never converged: offset {offsetAtZoom:F0}->{cont.Offset.Y:F0} " +
                    $"extent {extentBefore:F0}->{cont.Extent.Height:F0} " +
                    $"ratio {ratioBefore:F3}->{cont.Offset.Y / cont.Extent.Height:F3} " +
                    $"(expected {ExpectedRatio():F3}) " +
                    $"zoom {zoomBefore:F3}->{viewer.ZoomLevel:F3} page={vm.CurrentPageIndex} " +
                    $"viewport={cont.Viewport.Height:F0}");
            }

            var ratioAfter = cont.Offset.Y / cont.Extent.Height;
            ratioAfter.Should().BeApproximately(ExpectedRatio(), 0.03,
                $"zooming ×{zoomFactor} must keep the same document position at the viewport top " +
                $"(offset/extent ratio {ratioBefore:F3} → {ratioAfter:F3}); a drift means the " +
                "re-layout kept the numeric offset and slid the reader pages away (#700)");
            // The page label must stay truthful across the re-layout: it must
            // equal the page actually at the viewport top now (for reachable
            // anchors that is the page we were on; when pinned at max the
            // sync may legitimately land later in the document).
            var pageAfter = vm.CurrentPageIndex;
            if (Math.Abs(ratioAfter - ratioBefore) <= 0.03)
                pageAfter.Should().Be(pageBefore,
                    "the page label must stay truthful across a zoom re-layout — a frozen sync " +
                    "reports a page the user is no longer looking at (#700)");
        }
        finally
        {
            window.Close();
            TestPdfGenerator.CleanupTestFile(path);
        }
    }

    /// <summary>
    /// #693 (scroll half): the reading position must survive the mode switch.
    /// Before the fix, entering an editing mode dropped the reader at the top
    /// of the page (singleOffset 0/…) and switching back re-anchored to the
    /// page top. Round-trip: continuous → select-text → continuous must come
    /// back to (about) the same scroll offset, and the intermediate
    /// single-page view must be scrolled into the page, not at its top.
    /// </summary>
    [FixedAvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public async Task ModeSwitch_PreservesReadingPosition(double dpr)
    {
        var (vm, window, viewer, path) = await OpenTestDocumentAsync();
        try
        {
            viewer.RenderScalingOverride = dpr;
            var cont = viewer.FindControl<ScrollViewer>("ContinuousScrollViewer")!;
            await PumpUntilAsync(window, () => cont.Extent.Height > cont.Viewport.Height + 100);

            // Scroll mid-document. A page-crossing scroll can settle snapped
            // to the new page's top, so nudge a second time — WITHOUT
            // crossing a page — to land mid-page, then read the settled
            // position.
            var target = cont.Extent.Height * 0.45;
            cont.Offset = new global::Avalonia.Vector(cont.Offset.X, target);
            await Task.Delay(150); window.UpdateLayout();
            var pageHeight = cont.Extent.Height / 3; // 3-page test doc
            cont.Offset = new global::Avalonia.Vector(cont.Offset.X, cont.Offset.Y + pageHeight * 0.4);
            await Task.Delay(150); window.UpdateLayout();
            var offBefore = cont.Offset.Y;
            var pageBefore = vm.CurrentPageIndex;
            offBefore.Should().BeGreaterThan(100, "the test must start scrolled into the document");

            ModeCommand(vm, "select-text").Execute().Subscribe();
            await PumpUntilAsync(window, () => SinglePageImage(viewer)?.Source != null);
            var single = viewer.FindControl<ScrollViewer>("PdfScrollViewer")!;
            // The carried fraction applies via bounded deferred retries once
            // the freshly-rendered page has an extent — wait for it.
            try
            {
                await PumpUntilAsync(window, () => single.Extent.Height > 1 && single.Offset.Y > 1,
                    timeoutMs: 5000);
            }
            catch (TimeoutException)
            {
                var img = SinglePageImage(viewer);
                throw new Xunit.Sdk.XunitException(
                    "the carried reading position was not applied to the single-page scroller — " +
                    $"extent={single.Extent} offset={single.Offset} viewport={single.Viewport} " +
                    $"visible={single.IsVisible} page={vm.CurrentPageIndex} zoom={viewer.ZoomLevel:F3} " +
                    $"imgW={img?.Width} imgSrc={(img?.Source != null)} offBefore={offBefore:F0} " +
                    $"contExtent={cont.Extent.Height:F0}");
            }

            vm.CurrentPageIndex.Should().Be(pageBefore, "the mode switch must stay on the same page");
            (single.Offset.Y / single.Extent.Height).Should().BeGreaterThan(0.05,
                "the single-page view must open at the carried reading position, not the page top (#693)");

            ModeCommand(vm, "select-text").Execute().Subscribe();
            await PumpUntilAsync(window, () => cont.IsVisible);
            await PumpUntilAsync(window, () => Math.Abs(cont.Offset.Y - offBefore) < 40,
                timeoutMs: 10000);
        }
        finally
        {
            window.Close();
            TestPdfGenerator.CleanupTestFile(path);
        }
    }

    // ── harness ─────────────────────────────────────────────────────────────

    private static async Task<(MainWindowViewModel vm, MainWindow window, PdfViewerControl viewer, string path)>
        OpenTestDocumentAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-mode-switch-{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 3);
        var vm = new MainWindowViewModel { ThumbnailPrewarmEnabled = false };
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await vm.LoadDocumentAsync(path);
        var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl");
        viewer.Should().NotBeNull("MainWindow must host the PdfViewerControl");
        return (vm, window, viewer!, path);
    }

    private static ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> ModeCommand(MainWindowViewModel vm, string mode) => mode switch
    {
        "redact" => vm.ToggleRedactionModeCommand,
        "select-text" => vm.ToggleTextSelectionModeCommand,
        "typewriter" => vm.ToggleTypewriterModeCommand,
        "form-authoring" => vm.ToggleFormAuthoringModeCommand,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    private static bool ModeFlag(MainWindowViewModel vm, string mode) => mode switch
    {
        "redact" => vm.IsRedactionMode,
        "select-text" => vm.IsTextSelectionMode,
        "typewriter" => vm.IsTypewriterMode,
        "form-authoring" => vm.IsFormAuthoringMode,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    private static Image? SinglePageImage(PdfViewerControl viewer) =>
        viewer.FindControl<Image>("PdfImage");

    private static async Task PumpUntilAsync(Window window, Func<bool> condition, int timeoutMs = 20000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException("condition not met while pumping the dispatcher");
            await Task.Delay(50);
            window.UpdateLayout();
        }
    }
}
