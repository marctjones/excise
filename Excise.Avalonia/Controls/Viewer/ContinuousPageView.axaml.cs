using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Excise.Core.Document;
using Excise.Core.Text;
using static Excise.Avalonia.Controls.PdfViewerControl;

namespace Excise.Avalonia.Controls;

/// <summary>
/// The continuous (reading) view (#1842, <c>docs/architecture/pdf-viewer-control-architecture.md</c>
/// §3.2): slots, tile cache, render pass, band render, composites, scroll ↔ anchor page,
/// zoom anchor, render-ahead, per-slot text selection and form fields, and the continuous
/// half of the cache budget, trims and gauges.
/// </summary>
/// <remarks>
/// <para>The code moved from the viewer's partials as it was; the members below are the
/// seam. The view reads the viewer's state through <see cref="IViewerState"/> (it declares
/// no styled property of its own, so the viewer's once-per-process class handlers stay
/// single) and reports back through plain events. It registers no attach/detach or root
/// input handlers: the viewer calls <see cref="Detach"/> and <see cref="OnAttached"/>, in
/// the order its own handlers always ran.</para>
/// <para>The pure grid math, constants and key types stay declared on
/// <see cref="PdfViewerControl"/> (tests address them as <c>PdfViewerControl.X</c>) and
/// are imported here with <c>using static</c>.</para>
/// </remarks>
internal sealed partial class ContinuousPageView : UserControl, IPageHitSurface
{
    private PdfViewerControl _viewer = null!;
    private IViewerState _state = null!;
    private IDisposable? _continuousOffsetSubscription;
    private IDisposable? _continuousViewportSubscription;
    private IDisposable? _continuousExtentSubscription;
    private bool _renderAheadEnabled = true;

    public ContinuousPageView() => InitializeComponent();

    /// <summary>Wire the view to its viewer. Called once, from the viewer's template wiring.</summary>
    internal void Attach(PdfViewerControl viewer)
    {
        _viewer = viewer;
        _state = viewer;
        InitializeContinuous();
    }

    /// <summary>The page at the viewport top changed because the reader scrolled (#1650).</summary>
    internal event Action<int>? AnchorPageChanged;

    /// <summary>The continuous viewport's size changed.</summary>
    internal event Action<Size>? ViewportChanged;

    /// <summary>A continuous-view text selection finished (#815, #832).</summary>
    internal event EventHandler<TextSelectedEventArgs>? TextSelected;

    /// <summary>
    /// The per-document caches were dropped; the viewer drops the per-page hit-test
    /// caches that share their lifetime (#667, #1074).
    /// </summary>
    internal event Action? CachesInvalidated;

    // ── the viewer's state, read at the point of use ──────────────────────────
    private PdfDocument? Document => _state.Document;
    private int CurrentPage => _state.CurrentPage;
    private double ZoomLevel => _state.ZoomLevel;
    private PdfViewMode ViewMode => _state.ViewMode;
    private InteractionMode InteractionMode => _state.InteractionMode;
    private double EffectiveRenderScaling => _state.RenderScaling;
    private ReadingOrderStrategy ReadingOrderStrategy => _state.ReadingOrderStrategy;
    private WhitespaceMode WhitespaceMode => _state.WhitespaceMode;
    private Func<int, IReadOnlyList<PdfField>>? PageFormFieldsProvider => _state.PageFormFieldsProvider;
    private bool ShowAnnotations => _state.ShowAnnotations;
    private bool ShowCommentAnnotations => _state.ShowCommentAnnotations;
    private bool ShowFieldAndLinkAnnotations => _state.ShowFieldAndLinkAnnotations;
    private bool RevealHiddenAnnotations => _state.RevealHiddenAnnotations;
    private bool HighlightFormFields => _state.HighlightFormFields;

    private void OnScrollViewerViewportChanged(Size viewport) => ViewportChanged?.Invoke(viewport);

    /// <summary>Turn render-ahead in this view on or off (#1564); the viewer owns the switch.</summary>
    internal void SetRenderAheadEnabled(bool enabled) => _renderAheadEnabled = enabled;

    /// <summary>
    /// The viewer left the visual tree: hard-stop all continuous work so a closed viewer
    /// cannot touch a disposed document (#848), drop the scroll subscriptions and the
    /// container hooks, and cancel in-flight cell renders.
    /// </summary>
    internal void Detach()
    {
        _continuousDetached = true;
        _continuousOffsetSubscription?.Dispose();
        _continuousOffsetSubscription = null;
        _continuousViewportSubscription?.Dispose();
        _continuousViewportSubscription = null;
        _continuousExtentSubscription?.Dispose();
        _continuousExtentSubscription = null;

        if (ContinuousItems != null)
        {
            ContinuousItems.ContainerPrepared -= OnContinuousContainerPrepared;
            ContinuousItems.ContainerClearing -= OnContinuousContainerClearing;
            ContinuousItems.LayoutUpdated -= OnContinuousItemsLayoutUpdated;
        }

        // Cancel in-flight grid-cell renders for the now-detached control and
        // start a fresh generation, so a re-attach renders cleanly (#848).
        CancelContinuousCellRenders();
    }

    /// <summary>The viewer is back in a visual tree (the re-subscription gap is #1929).</summary>
    internal void OnAttached() => _continuousDetached = false;

    // ── IPageHitSurface (#1842 step 5) ──────────────────────────────────────

    /// <summary>
    /// True when the pointer event came from a continuous-view field input. The root
    /// handlers listen with handledEventsToo, so without this a press in a field would
    /// also start a text-selection drag and the field would never take focus.
    /// </summary>
    public bool IsOwnOverlayEvent(PointerEventArgs e)
    {
        if (InteractionMode is not (InteractionMode.None or InteractionMode.TextSelection))
            return false;

        for (var current = e.Source as StyledElement; current != null; current = current.Parent)
        {
            if (current is Control control && control.Classes.Contains(ContinuousFormFieldClass))
                return true;
        }
        return false;
    }

    /// <summary>The slot under the pointer, at <c>PointsToDip × zoom</c>.</summary>
    public bool TryMapPointToPage(PointerEventArgs e, out int pageNumber, out PdfPageRect point)
    {
        pageNumber = 0;
        point = default;
        var items = ContinuousItems;
        var slots = _continuousSlots;
        if (Document is not { } doc || items == null || slots == null) return false;
        var zoom = ZoomLevel;
        if (zoom <= 0) return false;

        var itemsPoint = e.GetPosition(items);
        if (!TryMapContinuousPointToPage(slots, items.Bounds.Width, itemsPoint, out pageNumber, out var pagePointDip))
            return false;
        if (pageNumber < 1 || pageNumber > doc.PageCount) return false;

        point = new PdfPageRect(pageNumber, pagePointDip.X, pagePointDip.Y, 0, 0,
            PdfCoordinateSpace.ContinuousDips, PointsToDip * zoom);
        return true;
    }

    /// <summary>
    /// Select every glyph on <paramref name="page"/> (#1814), draw it and report it as a
    /// finished selection. False when the page has no text.
    /// </summary>
    internal bool SelectAll(int page)
    {
        var letters = GetContinuousPageLetters(page);
        if (letters.Reading.Count == 0) return false;

        ClearContinuousSelectionHighlight();
        _continuousSelectionPage = _continuousSelectionFocusPage = page;
        _continuousSelectionAnchor = letters.Reading[0];
        _continuousSelectionFocus = letters.Reading[^1];
        DrawContinuousSelectionSpan();
        EndContinuousTextSelection();
        return true;
    }
}
