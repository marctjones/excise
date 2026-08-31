using System;
using Avalonia;
using Avalonia.Controls;

namespace Excise.Avalonia.Controls;

/// <summary>
/// Immutable diagnostic snapshot of the viewer's active viewport.
/// </summary>
/// <remarks>
/// This deliberately carries values rather than the underlying
/// <see cref="ScrollViewer"/>. Hosts and automation can observe scroll state
/// without depending on the viewer's XAML template or taking ownership of its
/// controls.
/// </remarks>
public readonly struct PdfViewerViewportDiagnostics
{
    /// <summary>Create a viewport diagnostic snapshot.</summary>
    public PdfViewerViewportDiagnostics(
        PdfViewMode viewMode,
        Size extent,
        Size viewport,
        Vector offset,
        bool isAvailable)
    {
        ViewMode = viewMode;
        Extent = extent;
        Viewport = viewport;
        Offset = offset;
        IsAvailable = isAvailable;
    }

    /// <summary>The view mode whose active viewport was sampled.</summary>
    public PdfViewMode ViewMode { get; }

    /// <summary>Total laid-out content size in DIPs.</summary>
    public Size Extent { get; }

    /// <summary>Visible viewport size in DIPs.</summary>
    public Size Viewport { get; }

    /// <summary>Current viewport offset in DIPs.</summary>
    public Vector Offset { get; }

    /// <summary>Whether the active viewport is initialized and available.</summary>
    public bool IsAvailable { get; }
}

public partial class PdfViewerControl
{
    /// <summary>
    /// Capture the active single-page or continuous viewport without exposing
    /// template implementation details.
    /// </summary>
    public PdfViewerViewportDiagnostics GetViewportDiagnostics()
    {
        var viewport = ActiveViewportScrollViewer();
        return viewport == null
            ? new PdfViewerViewportDiagnostics(ViewMode, default, default, default, false)
            : new PdfViewerViewportDiagnostics(
                ViewMode,
                viewport.Extent,
                viewport.Viewport,
                viewport.Offset,
                true);
    }

    /// <summary>
    /// Request a vertical scroll by <paramref name="deltaY"/> DIPs in the
    /// active viewport. Returns false when the viewer template is unavailable.
    /// </summary>
    public bool TryScrollViewportBy(double deltaY)
    {
        if (!double.IsFinite(deltaY))
            throw new ArgumentOutOfRangeException(nameof(deltaY), "Scroll delta must be finite.");

        var viewport = ActiveViewportScrollViewer();
        if (viewport == null)
            return false;

        SetVerticalOffset(viewport, viewport.Offset.Y + deltaY);
        return true;
    }

    /// <summary>
    /// Request a vertical position as a fraction of the active viewport's
    /// scrollable range: 0 is the top and 1 is the bottom. Returns false when
    /// the viewer template is unavailable.
    /// </summary>
    public bool TrySetViewportVerticalFraction(double fraction)
    {
        if (!double.IsFinite(fraction) || fraction < 0 || fraction > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fraction),
                "Viewport fraction must be finite and between 0 and 1.");
        }

        var viewport = ActiveViewportScrollViewer();
        if (viewport == null)
            return false;

        var maximum = Math.Max(0, viewport.Extent.Height - viewport.Viewport.Height);
        SetVerticalOffset(viewport, maximum * fraction);
        return true;
    }

    private ScrollViewer? ActiveViewportScrollViewer() =>
        ViewMode == PdfViewMode.Continuous ? _continuousScrollViewer : _scrollViewer;

    private static void SetVerticalOffset(ScrollViewer viewport, double requestedY)
    {
        var maximum = Math.Max(0, viewport.Extent.Height - viewport.Viewport.Height);
        var target = Math.Clamp(requestedY, 0, maximum);
        viewport.Offset = new Vector(viewport.Offset.X, target);
    }
}
