using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Excise.Core.Document;

namespace Excise.Avalonia.Controls;

/// <summary>
/// The two views' halves of the pointer-to-content funnel (#1842). The continuous
/// view is its own control and surface; until the single-page view is too, the viewer
/// implements that surface itself, reading the shared state through
/// <see cref="IViewerState"/> and only the single-page template parts.
/// </summary>
public partial class PdfViewerControl : IViewerState
{
    private SinglePageHitSurface? _singlePageHitSurface;

    private IPageHitSurface SinglePageSurface => _singlePageHitSurface ??= new SinglePageHitSurface(this, this);

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

    /// <summary>Single-page view: the overlay canvas, at the logical render DPI.</summary>
    private sealed class SinglePageHitSurface(PdfViewerControl viewer, IViewerState state) : IPageHitSurface
    {
        public bool IsOwnOverlayEvent(PointerEventArgs e)
        {
            var layer = viewer.TypewriterLayer;
            if (layer == null || e.Source is not Control source)
                return false;

            for (Control? current = source; current != null; current = current.Parent as Control)
            {
                if (ReferenceEquals(current, layer))
                    return true;
            }

            return false;
        }

        public bool TryMapPointToPage(PointerEventArgs e, out int pageNumber, out PdfPageRect point)
        {
            point = default;
            pageNumber = state.CurrentPage;
            if (state.Document is not { } doc || pageNumber < 1 || pageNumber > doc.PageCount) return false;

            var dipPoint = viewer.GetPressPoint(e);
            point = PdfPageRect.ViewerDips(pageNumber, dipPoint.X, dipPoint.Y, 0, 0,
                viewer._currentSinglePageRenderDpi);
            return true;
        }
    }
}
