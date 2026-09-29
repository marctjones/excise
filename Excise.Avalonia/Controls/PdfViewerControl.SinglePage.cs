using System.Threading.Tasks;
using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Input;
using Excise.Core.Document;

namespace Excise.Avalonia.Controls;

/// <summary>
/// The viewer's side of the single-page view (#1842 step 7): the template-part
/// accessors, the wiring, and the render state the view publishes through the
/// viewer's own styled properties.
/// </summary>
public partial class PdfViewerControl
{
    // ── template parts of the single-page view, reached through the viewer ───
    // (#1842 design §6 decision 2: the tests and the viewer's own input code keep
    // addressing the viewer.)
    internal ScrollViewer PdfScrollViewer => SinglePagePart.PdfScrollViewer;
    internal LayoutTransformControl ZoomHost => SinglePagePart.ZoomHost;
    internal Grid ContentGrid => SinglePagePart.ContentGrid;
    internal Image PdfImage => SinglePagePart.PdfImage;
    internal Canvas OverlayCanvas => SinglePagePart.OverlayCanvas;
    internal Canvas AnnotationsLayer => SinglePagePart.AnnotationsLayer;
    internal Canvas SearchHighlightsLayer => SinglePagePart.SearchHighlightsLayer;
    internal Canvas AppliedRedactionsLayer => SinglePagePart.AppliedRedactionsLayer;
    internal Canvas PendingRedactionsLayer => SinglePagePart.PendingRedactionsLayer;
    internal Canvas TextSelectionLayer => SinglePagePart.TextSelectionLayer;
    internal Canvas HiddenTextRevealLayer => SinglePagePart.HiddenTextRevealLayer;
    internal Canvas FormFieldsLayer => SinglePagePart.FormFieldsLayer;
    internal Canvas InteractionLayer => SinglePagePart.InteractionLayer;
    internal Canvas TypewriterLayer => SinglePagePart.TypewriterLayer;
    internal ProgressBar LoadingProgressBar => SinglePagePart.LoadingProgressBar;
    internal Grid LoadingOverlay => SinglePagePart.LoadingOverlay;
    internal Grid ErrorOverlay => SinglePagePart.ErrorOverlay;
    internal TextBlock ErrorMessageText => SinglePagePart.ErrorMessageText;

    /// <summary>
    /// Wire the single-page view to this viewer: it reads the viewer's state, takes its
    /// placeholder picture from the continuous view, and reports finished selections and
    /// typewriter edits back here. Called once, from <see cref="WireTemplateParts"/>.
    /// </summary>
    private void WireSinglePageView()
    {
        SinglePagePart.TextSelected += (_, e) => TextSelected?.Invoke(this, e);
        SinglePagePart.TypewriterTextCreated += (_, e) => TypewriterTextCreated?.Invoke(this, e);
        SinglePagePart.TypewriterTextEdited += (_, e) => TypewriterTextEdited?.Invoke(this, e);
        SinglePagePart.TypewriterTextBoundsChanged += (_, e) => TypewriterTextBoundsChanged?.Invoke(this, e);
        SinglePagePart.TypewriterTextDeleted += (_, e) => TypewriterTextDeleted?.Invoke(this, e);
        SinglePagePart.Attach(this, ContinuousPart);
    }

    // The single-page view is the only writer of the viewer's render state; the
    // properties stay the viewer's so hosts and bindings see no change.
    internal void SetIsLoadingFromSinglePageView(bool value) => IsLoading = value;
    internal void SetHasErrorFromSinglePageView(bool value) => HasError = value;
    internal void SetErrorMessageFromSinglePageView(string? value) => ErrorMessage = value;

    // ── public overlay API, drawn by the single-page view ─────────────────────

    /// <summary>
    /// Add a search highlight rectangle at the specified coordinates.
    /// </summary>
    public void AddSearchHighlight(PdfPageRect area) => SinglePagePart.AddSearchHighlight(area);

    /// <summary>
    /// Clear all search highlights.
    /// </summary>
    public void ClearSearchHighlights() => SinglePagePart.ClearSearchHighlights();

    /// <summary>
    /// Add a pending redaction overlay at the specified coordinates.
    /// </summary>
    public void AddPendingRedaction(PdfPageRect area) => SinglePagePart.AddPendingRedaction(area);

    /// <summary>
    /// Clear all pending redaction overlays.
    /// </summary>
    public void ClearPendingRedactions() => SinglePagePart.ClearPendingRedactions();

    /// <summary>
    /// Add an applied redaction overlay (black rectangle) at the specified coordinates.
    /// </summary>
    public void AddAppliedRedaction(PdfPageRect area) => SinglePagePart.AddAppliedRedaction(area);

    /// <summary>
    /// Clear all applied redaction overlays.
    /// </summary>
    public void ClearAppliedRedactions() => SinglePagePart.ClearAppliedRedactions();

    /// <summary>Clear any in-progress text selection (e.g. switching pages).</summary>
    public void ClearSelectionHighlight() => SinglePagePart.ClearSelectionHighlight();

    // ── the viewer's own calls into the single-page view, under the names its
    //    reactions and input handlers have always used ─────────────────────────
    private Task RenderCurrentPageAsync() => SinglePagePart.RenderCurrentPageAsync();
    private void ClearDisplay() => SinglePagePart.ClearDisplay();
    private void RedrawTypewriterLayer() => SinglePagePart.RedrawTypewriterLayer();
    private void RedrawHiddenTextOverlays() => SinglePagePart.RedrawHiddenTextOverlays();
    private void DiscardEmptyPendingTypewriterText(System.Guid? except = null) =>
        SinglePagePart.DiscardEmptyPendingTypewriterText(except);
    private Point GetPressPoint(PointerEventArgs e) => SinglePagePart.GetPressPoint(e);
    private PdfPageRect ViewerDipsRect(Rect rect, int pageNumber) => SinglePagePart.ViewerDipsRect(rect, pageNumber);

    // ── Test seams of the single-page view, forwarded under their old names so the
    //    tests keep addressing the viewer (#1842, design §3.1 principle 8). ──────
    internal Rect PdfRectToViewerDips(PdfRectangle pdfRect, int pageNumber) => SinglePagePart.PdfRectToViewerDips(pdfRect, pageNumber);
    internal PdfRectangle ViewerDipsToPdfRect(Rect dipRect, int pageNumber) => SinglePagePart.ViewerDipsToPdfRect(dipRect, pageNumber);
    internal Rect NormalizeTypewriterDipRect(Rect rect) => SinglePagePart.NormalizeTypewriterDipRect(rect);
    internal void CreateTypewriterTextFromPointer(Point start, Point end) => SinglePagePart.CreateTypewriterTextFromPointer(start, end);
    internal Rect GlyphRectToViewerDipsForTest(PdfRectangle glyphRect) => SinglePagePart.GlyphRectToViewerDipsForTest(glyphRect);
    internal bool SinglePageCacheContainsForTests(int page) => SinglePagePart.SinglePageCacheContainsForTests(page);
    internal int SinglePageLookAheadStartCount => SinglePagePart.SinglePageLookAheadStartCount;
    internal int SinglePageLookAheadCancellationCount => SinglePagePart.SinglePageLookAheadCancellationCount;
    internal int SinglePageLookAheadJoinCount => SinglePagePart.SinglePageLookAheadJoinCount;
    internal bool SinglePageLookAheadInFlight => SinglePagePart.SinglePageLookAheadInFlight;
    internal System.Action<int>? SinglePageLookAheadStartingForTests
    {
        get => SinglePagePart.SinglePageLookAheadStartingForTests;
        set => SinglePagePart.SinglePageLookAheadStartingForTests = value;
    }
    internal global::Avalonia.Media.Imaging.WriteableBitmap? SinglePagePlaceholderForTests => SinglePagePart.SinglePagePlaceholderForTests;
    internal long SinglePagePlaceholderSizedOnlyCount => SinglePagePart.SinglePagePlaceholderSizedOnlyCount;
}
