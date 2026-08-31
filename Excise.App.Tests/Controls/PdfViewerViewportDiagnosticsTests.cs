using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using AwesomeAssertions;
using Excise.Avalonia.Controls;

namespace Excise.App.Tests.Controls;

/// <summary>
/// Headless wiring tests for the public viewport diagnostics boundary. Private
/// template controls are used only as independent test oracles; production App
/// automation consumes the public contract.
/// </summary>
public class PdfViewerViewportDiagnosticsTests
{
    [FixedAvaloniaFact]
    public void RenderDiagnostics_IdentifyBothViewerOwnedCaches()
    {
        var viewer = new PdfViewerControl();

        var diagnostics = viewer.GetRenderDiagnostics();

        diagnostics.ViewMode.Should().Be(PdfViewMode.SinglePage);
        diagnostics.SinglePageEntryCount.Should().Be(0);
        diagnostics.SinglePageCapacity.Should().Be(6);
        diagnostics.SinglePageHits.Should().Be(0);
        diagnostics.SinglePageMisses.Should().Be(0);
        diagnostics.ContinuousEntryCount.Should().Be(0);
        diagnostics.ContinuousResidentBytes.Should().Be(0);
        diagnostics.ContinuousByteBudget.Should().Be(200L * 1024 * 1024);
        diagnostics.ContinuousHits.Should().Be(0);
        diagnostics.ContinuousInFlightCount.Should().Be(0);
    }

    [FixedAvaloniaFact]
    public void DiagnosticsAndScrollIntents_UseTheActiveViewportInBothModes()
    {
        var viewer = new PdfViewerControl();
        var window = new Window
        {
            Width = 320,
            Height = 240,
            Content = viewer,
        };
        window.Show();

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
    }

    [FixedAvaloniaFact]
    public void ScrollIntents_RejectInvalidNumbers()
    {
        var viewer = new PdfViewerControl();

        var infiniteDelta = () => viewer.TryScrollViewportBy(double.PositiveInfinity);
        var negativeFraction = () => viewer.TrySetViewportVerticalFraction(-0.1);
        var excessiveFraction = () => viewer.TrySetViewportVerticalFraction(1.1);

        infiniteDelta.Should().Throw<ArgumentOutOfRangeException>();
        negativeFraction.Should().Throw<ArgumentOutOfRangeException>();
        excessiveFraction.Should().Throw<ArgumentOutOfRangeException>();
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
