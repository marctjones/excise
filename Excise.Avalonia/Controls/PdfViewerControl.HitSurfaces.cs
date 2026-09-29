using Avalonia.Input;

namespace Excise.Avalonia.Controls;

/// <summary>
/// The two views' halves of the pointer-to-content funnel (#1842). Each view is its
/// own control and surface, and reads the shared state through <see cref="IViewerState"/>.
/// </summary>
public partial class PdfViewerControl : IViewerState
{
    /// <summary>The single-page view is its own surface (#1842 step 7).</summary>
    private IPageHitSurface SinglePageSurface => SinglePagePart;

    /// <summary>The continuous view is its own surface (#1842 step 6).</summary>
    private IPageHitSurface ContinuousSurface => ContinuousPart;

    double IViewerState.RenderScaling => EffectiveRenderScaling;

    /// <summary>The surface of the view on screen.</summary>
    private IPageHitSurface ActiveHitSurface =>
        ViewMode == PdfViewMode.Continuous ? ContinuousSurface : SinglePageSurface;

    /// <summary>
    /// Whether the event belongs to either view's own overlay. Both are asked, as
    /// the root handlers always did; only the visible view's overlays can raise one.
    /// </summary>
    private bool IsOwnOverlayEvent(PointerEventArgs e) =>
        SinglePageSurface.IsOwnOverlayEvent(e) || ContinuousSurface.IsOwnOverlayEvent(e);
}
