using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Excise.Core.Document;
using Excise.Core.Editing;
using Excise.Core.Text;
using Excise.Rendering;
using static Excise.Avalonia.Controls.PdfViewerControl;

namespace Excise.Avalonia.Controls;

/// <summary>
/// The single-page view (#1842, <c>docs/architecture/pdf-viewer-control-architecture.md</c>
/// §3.2): the page render and its bitmap cache, the placeholder, render-ahead, the overlay
/// layers the host draws on, letter selection, the typewriter layer, and the loading/error
/// chrome.
/// </summary>
/// <remarks>
/// <para>The code moved from the viewer's partials as it was; the members below are the
/// seam. The view reads the viewer's state through <see cref="IViewerState"/> and declares no
/// styled property of its own. The render state it produces (loading, error) it writes into
/// the viewer's own styled properties, at the points it always did, so the viewer's API and
/// the hosts that watch those properties see no change. Finished selections and typewriter
/// edits are reported through plain events the viewer re-raises. It registers no
/// attach/detach or root input handlers: the viewer calls <see cref="Detach"/>.</para>
/// <para>The pure render-plan math, the DPI constants and the key types stay declared on
/// <see cref="PdfViewerControl"/> (tests address them as <c>PdfViewerControl.X</c>) and are
/// imported here with <c>using static</c>.</para>
/// </remarks>
internal sealed partial class SinglePageView : UserControl, IPageHitSurface, IReadingPositionSource, ITypewriterEditSink
{
    private PdfViewerControl _viewer = null!;
    private IViewerState _state = null!;
    private IPagePreviewSource _preview = null!;
    private readonly SkiaRenderer _renderer = new();
    private ScaleTransform? _zoomScaleTransform;
    private bool _renderAheadEnabled = true;
    private long _singlePagePublishCount;

    public SinglePageView() => InitializeComponent();

    /// <summary>Wire the view to its viewer. Called once, from the viewer's template wiring.</summary>
    internal void Attach(PdfViewerControl viewer, IPagePreviewSource preview)
    {
        _viewer = viewer;
        _state = viewer;
        _preview = preview;

        // Single scale transform on the LayoutTransformControl wrapper. Both
        // the Image and the OverlayCanvas live inside it, so they scale and
        // align together — no need for two parallel RenderTransforms.
        if (ZoomHost != null)
        {
            _zoomScaleTransform = ZoomHost.LayoutTransform as ScaleTransform;
            // The XAML default is ScaleX/Y=1; apply the display-scale
            // correction (see SinglePageDisplayScale) from the start so the
            // first single-page view is already at pt × 96/72 × zoom.
            UpdateZoomTransform();
        }
    }

    /// <summary>A single-page text selection finished (#373).</summary>
    internal event EventHandler<TextSelectedEventArgs>? TextSelected;

    internal event EventHandler<TypewriterTextCreatedEventArgs>? TypewriterTextCreated;
    internal event EventHandler<TypewriterTextEditedEventArgs>? TypewriterTextEdited;
    internal event EventHandler<TypewriterTextBoundsChangedEventArgs>? TypewriterTextBoundsChanged;
    internal event EventHandler<TypewriterTextDeletedEventArgs>? TypewriterTextDeleted;

    // ── the viewer's state, read at the point of use ──────────────────────────
    private PdfDocument? Document => _state.Document;
    private int CurrentPage => _state.CurrentPage;
    private double ZoomLevel => _state.ZoomLevel;
    private PdfViewMode ViewMode => _state.ViewMode;
    private InteractionMode InteractionMode => _state.InteractionMode;
    private double EffectiveRenderScaling => _state.RenderScaling;
    private ReadingOrderStrategy ReadingOrderStrategy => _state.ReadingOrderStrategy;
    private WhitespaceMode WhitespaceMode => _state.WhitespaceMode;
    private bool ShowAnnotations => _state.ShowAnnotations;
    private bool ShowCommentAnnotations => _state.ShowCommentAnnotations;
    private bool ShowFieldAndLinkAnnotations => _state.ShowFieldAndLinkAnnotations;
    private bool RevealHiddenAnnotations => _state.RevealHiddenAnnotations;
    private bool HighlightFormFields => _state.HighlightFormFields;
    private IEnumerable<PdfAnnotation>? Annotations => _state.Annotations;
    private IReadOnlyList<PdfField>? FormFields => _state.FormFields;
    private IEnumerable<HiddenTextHighlight>? HiddenTextHighlights => _state.HiddenTextHighlights;
    private IEnumerable<PdfTypewriterTextOperation>? TypewriterTextOperations => _state.TypewriterTextOperations;

    // The render state is the viewer's to publish; this view is its only writer.
    private bool IsLoading
    {
        get => _state.IsLoading;
        set => _viewer.SetIsLoadingFromSinglePageView(value);
    }

    private bool HasError
    {
        get => _state.HasError;
        set => _viewer.SetHasErrorFromSinglePageView(value);
    }

    private string? ErrorMessage
    {
        get => _state.ErrorMessage;
        set => _viewer.SetErrorMessageFromSinglePageView(value);
    }

    /// <summary>
    /// The viewer left the visual tree. A detached single-page viewer must not publish an
    /// in-flight result into controls that are no longer attached. Keep cached bitmaps
    /// alive: the control may be reattached and its Image still owns that binding.
    /// </summary>
    internal void Detach()
    {
        _singlePageRenderLifetime.CancelRender();
        CancelSinglePageLookAhead();
        IsLoading = false;
    }

    /// <summary>Turn render-ahead in this view on or off (#1564); the viewer owns the switch.</summary>
    internal void SetRenderAheadEnabled(bool enabled) => _renderAheadEnabled = enabled;

    /// <summary>Zoom changed: the single scale transform gives instant visual zoom.</summary>
    internal void OnZoomLevelChanged()
    {
        if (_zoomScaleTransform != null)
        {
            Trace($"Zoom -> {ZoomLevel:F3} mode={ViewMode} page={CurrentPage} displayScale={SinglePageDisplayScale:F3}");
            UpdateZoomTransform();
        }
    }

    // ── IReadingPositionSource (#693) ────────────────────────────────────────

    public double CaptureIntraPageFraction() => SingleIntraPageFraction();

    /// <summary>
    /// Back to single-page: make sure the current page is rendered. The carried fraction is
    /// applied from the render-completion paths, NOT posted here: a post now would burn all
    /// its retries through the dispatcher before the async render gives the ScrollViewer a
    /// real extent, then give up.
    /// </summary>
    public void ShowAt(double intraPageFraction)
    {
        _pendingSingleFraction = intraPageFraction;
        _ = RenderCurrentPageAsync();
    }

    // ── IPageHitSurface (#1842 step 5) ──────────────────────────────────────

    /// <summary>True when the event came from the typewriter layer (a box being edited).</summary>
    public bool IsOwnOverlayEvent(PointerEventArgs e)
    {
        var layer = TypewriterLayer;
        if (layer == null || e.Source is not Control source)
            return false;

        for (Control? current = source; current != null; current = current.Parent as Control)
        {
            if (ReferenceEquals(current, layer))
                return true;
        }

        return false;
    }

    /// <summary>The overlay canvas, at the logical render DPI.</summary>
    public bool TryMapPointToPage(PointerEventArgs e, out int pageNumber, out PdfPageRect point)
    {
        point = default;
        pageNumber = CurrentPage;
        if (Document is not { } doc || pageNumber < 1 || pageNumber > doc.PageCount) return false;

        var dipPoint = GetPressPoint(e);
        point = PdfPageRect.ViewerDips(pageNumber, dipPoint.X, dipPoint.Y, 0, 0,
            _currentSinglePageRenderDpi);
        return true;
    }

    // ── letter selection, for the viewer's pointer handlers and Select All ───

    /// <summary>
    /// Press in text-selection mode: hit-test letters instead of drawing a 2-D rectangle.
    /// Anchor is the letter under (or nearest to) the press point; focus tracks pointer-moved.
    /// </summary>
    internal void BeginLetterSelection(Point point)
    {
        EnsurePageLettersLoaded();
        _selectionAnchor = HitTestLetterAt(point);
        _selectionFocus = _selectionAnchor;
        ClearSelectionHighlight();
        if (_selectionAnchor != null)
            DrawSelectionRange(new[] { _selectionAnchor });
    }

    /// <summary>
    /// Drag in text-selection mode: letter-by-letter highlight from the anchor. False when
    /// there is nothing to extend, or the focus did not move to a different letter.
    /// </summary>
    internal bool ExtendLetterSelection(Point currentPoint)
    {
        if (_selectionAnchor == null || _readingOrderedLetters == null) return false;
        var hit = HitTestLetterAt(currentPoint);
        if (hit == null) return false;
        // Re-draw only when focus actually moves to a different letter.
        if (ReferenceEquals(hit, _selectionFocus)) return false;
        _selectionFocus = hit;
        // Column-gutter aware so a drag inside one column doesn't vacuum up
        // an adjacent column sharing a Y-band (#373). Highlight follows
        // visual order — DrawSelectionRange paints each glyph rect.
        var range = TextSelectionEngine.ColumnAwareRange(
            _readingOrderedLetters, _selectionAnchor, _selectionFocus, _columnGapThreshold);
        DrawSelectionRange(range);
        return true;
    }

    /// <summary>Whether a release would finish a selection: an anchor, a focus and the letters they index.</summary>
    internal bool HasLetterSelection =>
        _selectionAnchor != null && _selectionFocus != null && _readingOrderedLetters != null;

    /// <summary>Report the finished selection (<see cref="TextSelected"/>).</summary>
    internal void FinishLetterSelection() => RaiseSinglePageTextSelected();

    /// <summary>Select every letter on the displayed page (#1814). False when it has none.</summary>
    internal bool SelectAll()
    {
        EnsurePageLettersLoaded();
        if (_readingOrderedLetters == null || _readingOrderedLetters.Count == 0) return false;

        _selectionAnchor = _readingOrderedLetters[0];
        _selectionFocus = _readingOrderedLetters[^1];
        DrawSelectionRange(TextSelectionEngine.ColumnAwareRange(
            _readingOrderedLetters, _selectionAnchor, _selectionFocus, _columnGapThreshold));
        RaiseSinglePageTextSelected();
        return true;
    }

    /// <summary>
    /// The displayed page's letters in reading order, loading them if needed; null or empty
    /// when there are none. The accessibility tree reads the same cache (#631).
    /// </summary>
    internal List<Letter>? LoadedReadingOrderedLetters()
    {
        EnsurePageLettersLoaded();
        return _readingOrderedLetters;
    }

    /// <summary>Drop the cached letters (a different page, document or content).</summary>
    internal void ForgetPageLetters()
    {
        _currentPageLetters = null;
        _readingOrderedLetters = null;
        _lettersPageNumber = -1;
    }

    /// <summary>Drop only the reading order (a different reading-order strategy, #774).</summary>
    internal void ForgetReadingOrder()
    {
        _readingOrderedLetters = null;
        _lettersPageNumber = -1;
    }

    /// <summary>Drop the selection endpoints; they index letters that are gone.</summary>
    internal void ForgetLetterSelection()
    {
        _selectionAnchor = null;
        _selectionFocus = null;
    }

    // ── cache governance and diagnostics (single-page half) ─────────────────

    /// <summary>Cancel the render in flight, if any; the viewer's document change calls this.</summary>
    internal void CancelRender() => _singlePageRenderLifetime.CancelRender();

    /// <summary>Drop the cached bitmaps and the render-ahead plan.</summary>
    internal void InvalidateCache()
    {
        InvalidateSinglePageLookAhead();
        _singlePageRenderLifetime.InvalidateCache();
    }

    /// <summary>How many pages the cache keeps; lowering it never drops the page on screen (#1887).</summary>
    internal int CacheCapacity
    {
        get => _singlePageRenderLifetime.GetCacheDiagnostics().Capacity;
        set
        {
            var shown = PdfImage?.Source as WriteableBitmap;
            _singlePageRenderLifetime.SetCapacity(value, bitmap => ReferenceEquals(bitmap, shown));
        }
    }

    /// <summary>
    /// Release every cached bitmap except the one on screen (#1478). Reference identity with
    /// the Image's source IS the never-drop rule. When the page has settled, that bitmap is
    /// the current page at the current device DPI; while a render is in flight it is whatever
    /// the user still sees. In continuous view the hidden Image has no source (#1473), so
    /// every entry goes.
    /// </summary>
    internal (int Count, long Bytes) TrimCache()
    {
        var shown = PdfImage?.Source as WriteableBitmap;
        return _singlePageRenderLifetime.Trim(
            bitmap => ReferenceEquals(bitmap, shown),
            static bitmap => ContinuousTileByteSize(bitmap.PixelSize.Width, bitmap.PixelSize.Height));
    }

    internal SinglePageRenderLifetime<WriteableBitmap>.CacheDiagnostics CacheDiagnostics() =>
        _singlePageRenderLifetime.GetCacheDiagnostics();

    /// <summary>
    /// Times a finished single-page render (fresh or cached) was bound to the page Image. A
    /// placeholder does not count. Report-only, for the edit-mode switch measurements.
    /// </summary>
    internal long SinglePagePublishCount => _singlePagePublishCount;

    /// <summary>Bytes held by the single-page LRU (BGRA, 4 bytes per pixel).</summary>
    internal long SinglePageCacheResidentBytes() =>
        _singlePageRenderLifetime.ResidentBytes(b => (long)b.PixelSize.Width * b.PixelSize.Height * 4);
}
