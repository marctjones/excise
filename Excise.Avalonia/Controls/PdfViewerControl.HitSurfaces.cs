using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Excise.Core.Document;

namespace Excise.Avalonia.Controls;

/// <summary>
/// The two views' halves of the pointer-to-content funnel (#1842). Until the views
/// are their own controls, the viewer implements both surfaces itself; each reads
/// the shared state through <see cref="IViewerState"/> and only its own view's
/// template parts and layout state.
/// </summary>
public partial class PdfViewerControl : IViewerState
{
    private SinglePageHitSurface? _singlePageHitSurface;
    private ContinuousHitSurface? _continuousHitSurface;

    private IPageHitSurface SinglePageSurface => _singlePageHitSurface ??= new SinglePageHitSurface(this, this);
    private IPageHitSurface ContinuousSurface => _continuousHitSurface ??= new ContinuousHitSurface(this, this);

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

    /// <summary>Continuous view: the slot under the pointer, at <c>PointsToDip × zoom</c>.</summary>
    private sealed class ContinuousHitSurface(PdfViewerControl viewer, IViewerState state) : IPageHitSurface
    {
        /// <summary>
        /// True when the pointer event came from a continuous-view field input. The root
        /// handlers listen with handledEventsToo, so without this a press in a field would
        /// also start a text-selection drag and the field would never take focus.
        /// </summary>
        public bool IsOwnOverlayEvent(PointerEventArgs e)
        {
            if (state.InteractionMode is not (InteractionMode.None or InteractionMode.TextSelection))
                return false;

            for (var current = e.Source as StyledElement; current != null; current = current.Parent)
            {
                if (current is Control control && control.Classes.Contains(ContinuousFormFieldClass))
                    return true;
            }
            return false;
        }

        public bool TryMapPointToPage(PointerEventArgs e, out int pageNumber, out PdfPageRect point)
        {
            pageNumber = 0;
            point = default;
            var items = viewer.ContinuousItems;
            var slots = viewer._continuousSlots;
            if (state.Document is not { } doc || items == null || slots == null) return false;
            var zoom = state.ZoomLevel;
            if (zoom <= 0) return false;

            var itemsPoint = e.GetPosition(items);
            if (!TryMapContinuousPointToPage(slots, items.Bounds.Width, itemsPoint, out pageNumber, out var pagePointDip))
                return false;
            if (pageNumber < 1 || pageNumber > doc.PageCount) return false;

            point = new PdfPageRect(pageNumber, pagePointDip.X, pagePointDip.Y, 0, 0,
                PdfCoordinateSpace.ContinuousDips, PointsToDip * zoom);
            return true;
        }
    }
}
