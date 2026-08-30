using System;

namespace Excise.App.ViewModels;

/// <summary>
/// Owns per-document zoom and transient viewport state. The window view model
/// remains the ReactiveUI binding adapter and performs persistence, while this
/// session makes manual, automatic-fit, restore, and reset transitions explicit.
/// </summary>
internal sealed class DocumentViewportSession
{
    internal const double MinimumZoom = 0.25;
    internal const double MaximumZoom = 5.0;

    internal double ZoomLevel { get; private set; } = 1.0;
    internal ZoomFitMode FitMode { get; private set; } = ZoomFitMode.FitWidth;
    internal double ViewportWidth { get; private set; } = 800;
    internal double ViewportHeight { get; private set; } = 600;

    internal ZoomTransition SetManualZoom(double zoom) =>
        SetZoom(zoom, ZoomFitMode.Manual, shouldPersist: true);

    internal ZoomTransition SetAutomaticFitZoom(ZoomFitMode mode, double zoom)
    {
        if (mode == ZoomFitMode.Manual)
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Automatic zoom requires a fit mode.");

        return SetZoom(zoom, mode, shouldPersist: true);
    }

    internal ZoomTransition RestoreManualZoom(double zoom) =>
        SetZoom(zoom, ZoomFitMode.Manual, shouldPersist: false);

    internal ZoomTransition ResetZoomWithoutPersisting(double zoom = 1.0) =>
        SetZoom(zoom, mode: null, shouldPersist: false);

    internal void LoadZoomPreference(double zoom)
    {
        ZoomLevel = zoom;
    }

    internal ViewportTransition UpdateViewport(double width, double height)
    {
        var widthChanged = !(Math.Abs(ViewportWidth - width) < 0.5);
        var heightChanged = !(Math.Abs(ViewportHeight - height) < 0.5);
        if (widthChanged)
            ViewportWidth = width;
        if (heightChanged)
            ViewportHeight = height;
        return new ViewportTransition(widthChanged, heightChanged);
    }

    private ZoomTransition SetZoom(
        double zoom,
        ZoomFitMode? mode,
        bool shouldPersist)
    {
        var delta = Math.Abs(ZoomLevel - zoom);
        var changed = !ZoomLevel.Equals(zoom);
        ZoomLevel = zoom;
        if (mode.HasValue)
            FitMode = mode.Value;
        return new ZoomTransition(changed, delta > 0.001 && shouldPersist);
    }
}

internal readonly record struct ZoomTransition(
    bool ZoomChanged,
    bool ShouldPersist);

internal readonly record struct ViewportTransition(
    bool WidthChanged,
    bool HeightChanged);

/// <summary>
/// Zoom-mode latching for the viewer. PDF readers traditionally let users
/// pick a fit mode (Width / Page) that survives window resizes; manual zoom
/// ends that latch.
/// </summary>
public enum ZoomFitMode
{
    Manual,
    FitWidth,
    FitPage,
}
