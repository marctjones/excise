using System;
using System.IO;
using System.Reactive.Linq;
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
            source.Size.Width.Should().BeApproximately(expectedDipWidth, 2.0,
                "the bitmap's DIP size must equal the logical page size");

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
