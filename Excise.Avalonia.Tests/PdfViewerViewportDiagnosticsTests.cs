using Avalonia.Controls;
using Avalonia.Threading;
using AwesomeAssertions;
using Excise.Avalonia.Controls;

namespace Excise.Avalonia.Tests;

/// <summary>
/// Headless wiring tests for the public viewport diagnostics boundary. Private
/// template controls are used only as independent test oracles; production App
/// automation consumes the public contract.
/// </summary>
/// <remarks>
/// Moved here from Excise.App.Tests (#1773): nothing in it touches the App or a
/// rendered page, so it does not need that suite's 8 GB host or its per-test
/// forced GC. Runs on the shared headless session through
/// <see cref="HeadlessSessionGuard"/>, like the other viewer tests in this project.
/// </remarks>
public class PdfViewerViewportDiagnosticsTests
{
    private static Task<T> OnUiThread<T>(Func<T> body) =>
        HeadlessSessionGuard.Session().Dispatch(body, CancellationToken.None);

    [Fact]
    public async Task RenderDiagnostics_ReportTheViewerOwnedCacheLimits_AsConfigured()
    {
        await OnUiThread(() =>
        {
            var viewer = new PdfViewerControl();

            var fresh = viewer.GetRenderDiagnostics();

            fresh.ViewMode.Should().Be(PdfViewMode.SinglePage);
            fresh.SinglePageEntryCount.Should().Be(0);
            fresh.SinglePageHits.Should().Be(0);
            fresh.SinglePageMisses.Should().Be(0);
            fresh.ContinuousEntryCount.Should().Be(0);
            fresh.ContinuousResidentBytes.Should().Be(0);
            fresh.ContinuousHits.Should().Be(0);
            fresh.ContinuousInFlightCount.Should().Be(0);
            // The limits are read from the public knobs, not restated here: a
            // hard-coded copy of the defaults would keep passing on a diagnostics
            // type that returned constants instead of the live viewer state.
            fresh.SinglePageCapacity.Should().Be(viewer.SinglePageCacheCapacity);
            fresh.ContinuousByteBudget.Should().Be(viewer.ContinuousTileCacheByteBudget);

            // ...and a value that is NOT the default must show up, which a
            // constant-returning diagnostics type cannot do.
            viewer.SinglePageCacheCapacity = viewer.SinglePageCacheCapacity + 2;
            viewer.ContinuousTileCacheByteBudget = viewer.ContinuousTileCacheByteBudget / 2 + 1;

            var configured = viewer.GetRenderDiagnostics();
            configured.SinglePageCapacity.Should().Be(fresh.SinglePageCapacity + 2);
            configured.ContinuousByteBudget.Should().Be(fresh.ContinuousByteBudget / 2 + 1);
            return true;
        });
    }

    [Fact]
    public async Task DiagnosticsAndScrollIntents_UseTheActiveViewportInBothModes()
    {
        await OnUiThread(() =>
        {
            var viewer = new PdfViewerControl();
            var window = new Window
            {
                Width = 320,
                Height = 240,
                Content = viewer,
            };
            window.Show();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            var single = viewer.FindControl<ScrollViewer>("PdfScrollViewer")!;
            var continuous = viewer.FindControl<ScrollViewer>("ContinuousScrollViewer")!;

            ConfigureScrollableViewport(window, single, contentHeight: 900);
            var singleSnapshot = viewer.GetViewportDiagnostics();

            singleSnapshot.IsAvailable.Should().BeTrue();
            singleSnapshot.ViewMode.Should().Be(PdfViewMode.SinglePage);
            singleSnapshot.Extent.Height.Should().BeGreaterThan(singleSnapshot.Viewport.Height);
            viewer.TrySetViewportVerticalFraction(0.5).Should().BeTrue();
            viewer.GetViewportDiagnostics().Offset.Y.Should().BeApproximately(
                (single.Extent.Height - single.Viewport.Height) * 0.5,
                0.01);

            viewer.ViewMode = PdfViewMode.Continuous;
            ConfigureScrollableViewport(window, continuous, contentHeight: 1_200);
            var continuousSnapshot = viewer.GetViewportDiagnostics();

            continuousSnapshot.IsAvailable.Should().BeTrue();
            continuousSnapshot.ViewMode.Should().Be(PdfViewMode.Continuous);
            continuousSnapshot.Extent.Height.Should().BeGreaterThan(continuousSnapshot.Viewport.Height);
            viewer.TryScrollViewportBy(75).Should().BeTrue();
            viewer.GetViewportDiagnostics().Offset.Y.Should().BeApproximately(75, 0.01);
            single.Offset.Y.Should().BeGreaterThan(0,
                "continuous scrolling must not mutate the inactive single-page viewport");

            window.Close();
            return true;
        });
    }

    [Fact]
    public async Task ScrollIntents_RejectInvalidNumbers()
    {
        await OnUiThread(() =>
        {
            var viewer = new PdfViewerControl();

            var infiniteDelta = () => viewer.TryScrollViewportBy(double.PositiveInfinity);
            var negativeFraction = () => viewer.TrySetViewportVerticalFraction(-0.1);
            var excessiveFraction = () => viewer.TrySetViewportVerticalFraction(1.1);

            infiniteDelta.Should().Throw<ArgumentOutOfRangeException>();
            negativeFraction.Should().Throw<ArgumentOutOfRangeException>();
            excessiveFraction.Should().Throw<ArgumentOutOfRangeException>();
            return true;
        });
    }

    private static void ConfigureScrollableViewport(
        Window window,
        ScrollViewer viewport,
        double contentHeight)
    {
        viewport.Width = 240;
        viewport.Height = 120;
        viewport.Content = new Border { Width = 240, Height = contentHeight };
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }
}
