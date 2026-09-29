using System.Threading.Tasks;
using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Input;
using Excise.Core.Document;

namespace Excise.Avalonia.Controls;

/// <summary>
/// The viewer's side of the single-page view (#1842 step 7): the wiring, the
/// public overlay API it draws, and the render state the view publishes through
/// the viewer's own styled properties. Tests reach the view's parts and seams
/// through <c>SinglePagePart</c> itself (#1842 Phase B).
/// </summary>
public partial class PdfViewerControl
{
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
}
