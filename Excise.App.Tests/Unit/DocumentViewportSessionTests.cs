using System;
using AwesomeAssertions;
using Excise.Avalonia.Controls;
using Excise.App.ViewModels;
using Xunit;

namespace Excise.App.Tests.Unit;

public class DocumentViewportSessionTests
{
    [Fact]
    public void NavigationAndViewPolicy_HaveReaderDefaultsAndIdempotentTransitions()
    {
        var session = new DocumentViewportSession();

        session.CurrentPageIndex.Should().Be(0);
        session.ViewMode.Should().Be(PdfViewMode.Continuous);
        session.ContinuousScrollPreference.Should().BeTrue();

        session.SetCurrentPageIndex(4).Should().BeTrue();
        session.SetCurrentPageIndex(4).Should().BeFalse();
        session.SetViewMode(PdfViewMode.SinglePage).Should().BeTrue();
        session.SetViewMode(PdfViewMode.SinglePage).Should().BeFalse();
        session.SetContinuousScrollPreference(false).Should().BeTrue();
        session.SetContinuousScrollPreference(false).Should().BeFalse();

        session.CurrentPageIndex.Should().Be(4);
        session.ViewMode.Should().Be(PdfViewMode.SinglePage);
        session.ContinuousScrollPreference.Should().BeFalse();
    }

    [Fact]
    public void SetManualZoom_EndsFitModeAndRequestsPersistenceForMeaningfulChanges()
    {
        var session = new DocumentViewportSession();

        var changed = session.SetManualZoom(1.5);
        var tinyChange = session.SetManualZoom(1.5005);

        session.FitMode.Should().Be(ZoomFitMode.Manual);
        session.ZoomLevel.Should().Be(1.5005);
        changed.Should().Be(new ZoomTransition(ZoomChanged: true, ShouldPersist: true));
        tinyChange.Should().Be(new ZoomTransition(ZoomChanged: true, ShouldPersist: false));
    }

    [Fact]
    public void SetAutomaticFitZoom_LatchesModeAndViewportReportsChangedDimensions()
    {
        var session = new DocumentViewportSession();
        session.SetManualZoom(2.0);

        var zoom = session.SetAutomaticFitZoom(ZoomFitMode.FitPage, 0.8);
        var viewport = session.UpdateViewport(width: 700, height: 500);

        session.FitMode.Should().Be(ZoomFitMode.FitPage);
        session.ZoomLevel.Should().Be(0.8);
        zoom.Should().Be(new ZoomTransition(ZoomChanged: true, ShouldPersist: true));
        viewport.Should().Be(new ViewportTransition(WidthChanged: true, HeightChanged: true));
        session.ViewportWidth.Should().Be(700);
        session.ViewportHeight.Should().Be(500);
    }

    [Fact]
    public void RestoreAndReset_DoNotPersistAndResetPreservesTheActiveFitMode()
    {
        var session = new DocumentViewportSession();

        var restored = session.RestoreManualZoom(1.75);
        session.SetAutomaticFitZoom(ZoomFitMode.FitWidth, 1.25);
        var reset = session.ResetZoomWithoutPersisting();

        restored.Should().Be(new ZoomTransition(ZoomChanged: true, ShouldPersist: false));
        reset.Should().Be(new ZoomTransition(ZoomChanged: true, ShouldPersist: false));
        session.ZoomLevel.Should().Be(1.0);
        session.FitMode.Should().Be(
            ZoomFitMode.FitWidth,
            "a display reset must not silently change the explicit fit policy");
    }

    [Fact]
    public void LoadZoomPreference_ChangesOnlyTheRawZoomValue()
    {
        var session = new DocumentViewportSession();

        session.LoadZoomPreference(1.4);

        session.ZoomLevel.Should().Be(1.4);
        session.FitMode.Should().Be(ZoomFitMode.FitWidth);
    }

    [Fact]
    public void SetAutomaticFitZoom_RejectsManualMode()
    {
        var session = new DocumentViewportSession();

        session.Invoking(subject => subject.SetAutomaticFitZoom(ZoomFitMode.Manual, 1.0))
            .Should().Throw<ArgumentOutOfRangeException>();
    }
}
