using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Input;
using Excise.Avalonia.Services;
using Excise.Core.Document;
using Excise.Core.Text;

namespace Excise.Avalonia.Controls;

/// <summary>
/// Text selection in the continuous (reading) view (#815).
///
/// Single-page selection draws onto the <c>TextSelectionLayer</c> overlay above
/// the one rendered page. The continuous view has no such single overlay — it is
/// a virtualized stack of page bitmaps — so the highlight is drawn per page: each
/// <see cref="PdfPageSlot"/> carries an observable list of highlight rectangles
/// (page-local DIPs) that the DataTemplate binds to a Canvas overlay on that
/// page's Border.
///
/// The gesture reuses the exact single-page engine (<see cref="TextSelectionEngine"/>)
/// and the exact continuous pointer→page mapping the link hit-test uses
/// (<see cref="TryMapContinuousPointToPage"/> +
/// <see cref="PdfCoordinateMapper"/>'s ContinuousDips space). Nothing is
/// duplicated.
///
/// Cross-page selection (#832): the anchor (press) and focus (drag) may sit on
/// DIFFERENT pages. The span is decomposed per page — the anchor page from the
/// anchor glyph to its last reading-order glyph, every intervening page in full,
/// the focus page from its first glyph to the focus glyph (direction-aware) —
/// and each page's slot is highlighted independently via the same per-slot
/// SelectionRects overlay. The combined copied text joins the pages in reading
/// (page-number) order. Per-page reading order and word spacing still come from
/// <see cref="TextSelectionEngine"/>; a paragraph that flows across a page break
/// gets a hard line break at the boundary (the whitespace layer, #824/#826, does
/// not reflow across pages — a documented limit, not a regression).
/// </summary>
public partial class PdfViewerControl
{
    private int _continuousSelectionPage;        // anchor (press) page, 1-based
    private Letter? _continuousSelectionAnchor;
    private int _continuousSelectionFocusPage;   // focus (current drag) page, 1-based
    private Letter? _continuousSelectionFocus;

    /// <summary>Per-page letter caches for continuous selection, cleared with the tile cache.</summary>
    private readonly Dictionary<int, ContinuousPageLetters> _continuousPageLetterCache = new();

    private sealed record ContinuousPageLetters(
        IReadOnlyList<Letter> Raw, List<Letter> Reading, double ColumnGap)
    {
        public static readonly ContinuousPageLetters Empty =
            new(System.Array.Empty<Letter>(), new List<Letter>(), double.PositiveInfinity);
    }

    private ContinuousPageLetters GetContinuousPageLetters(int pageNumber)
    {
        if (_continuousPageLetterCache.TryGetValue(pageNumber, out var cached))
            return cached;

        ContinuousPageLetters result;
        try
        {
            var page = Document!.GetPage(pageNumber);
            var raw = page.Letters?.ToList() ?? new List<Letter>();
            var reading = TextSelectionEngine.SortReadingOrder(raw, ReadingOrderStrategy);
            var gap = TextSelectionEngine.EstimateColumnGap(reading);
            result = new ContinuousPageLetters(raw, reading, gap);
        }
        catch
        {
            result = ContinuousPageLetters.Empty;
        }
        _continuousPageLetterCache[pageNumber] = result;
        return result;
    }

    /// <summary>
    /// Map a continuous-view pointer event to the page under it and the letter (if
    /// any) at that point, plus that page's cached letters. Uses the same slot
    /// geometry + ContinuousDips content mapping as the link hit-test (#667).
    /// </summary>
    private bool TryContinuousPointToLetter(
        PointerEventArgs e, out int pageNumber, out Letter? letter, out ContinuousPageLetters letters)
    {
        pageNumber = 0;
        letter = null;
        letters = ContinuousPageLetters.Empty;

        var doc = Document;
        if (doc == null || _continuousItems == null || _continuousSlots == null) return false;
        var zoom = ZoomLevel;
        if (zoom <= 0) return false;

        var itemsPoint = e.GetPosition(_continuousItems);
        if (!TryMapContinuousPointToPage(
                _continuousSlots, _continuousItems.Bounds.Width, itemsPoint,
                out pageNumber, out var pagePointDip))
            return false;
        if (pageNumber < 1 || pageNumber > doc.PageCount) return false;

        var page = doc.GetPage(pageNumber);
        var contentPoint = PdfCoordinateMapper.ToContentPoints(
            page,
            new PdfPageRect(pageNumber, pagePointDip.X, pagePointDip.Y, 0, 0,
                PdfCoordinateSpace.ContinuousDips, PointsToDip * zoom));

        letters = GetContinuousPageLetters(pageNumber);
        letter = TextSelectionEngine.HitTest(letters.Raw, contentPoint.X, contentPoint.Y);
        return true;
    }

    private void BeginContinuousTextSelection(PointerEventArgs e)
    {
        ClearContinuousSelectionHighlight();
        _continuousSelectionAnchor = null;
        _continuousSelectionFocus = null;
        _continuousSelectionPage = 0;
        _continuousSelectionFocusPage = 0;

        if (!TryContinuousPointToLetter(e, out var page, out var letter, out _))
            return;

        // Remember the page even if the press missed a glyph, so a drag that
        // starts in a margin and moves onto text can still begin.
        _continuousSelectionPage = page;
        _continuousSelectionFocusPage = page;
        if (letter == null) return;

        _continuousSelectionAnchor = letter;
        _continuousSelectionFocus = letter;
        DrawContinuousSelectionSpan();
    }

    private void UpdateContinuousTextSelection(PointerEventArgs e)
    {
        if (!TryContinuousPointToLetter(e, out var page, out var letter, out _))
            return;

        if (_continuousSelectionAnchor == null)
        {
            // The press missed a glyph but latched a page; adopt the first glyph
            // the drag reaches (on ANY page, #832) as the anchor.
            if (letter == null) return;
            _continuousSelectionPage = page;
            _continuousSelectionAnchor = letter;
            _continuousSelectionFocusPage = page;
            _continuousSelectionFocus = letter;
            DrawContinuousSelectionSpan();
            return;
        }

        // A move over a gap/margin (no glyph) keeps the current selection rather
        // than collapsing it — avoids flicker while crossing a page boundary.
        if (letter == null) return;
        if (page == _continuousSelectionFocusPage && ReferenceEquals(letter, _continuousSelectionFocus)) return;

        _continuousSelectionFocusPage = page;
        _continuousSelectionFocus = letter;
        DrawContinuousSelectionSpan();
    }

    /// <summary>
    /// Decompose the current anchor→focus selection into per-page (from, to)
    /// reading-order endpoints, in page order (#832). Same page → a single
    /// entry; a span → anchor-page tail, whole intervening pages, focus-page head
    /// (ordered so the lower page number is the start regardless of drag
    /// direction). Empty pages are skipped.
    /// </summary>
    private List<(int Page, Letter From, Letter To)> ComputeContinuousSpanEndpoints()
    {
        var result = new List<(int, Letter, Letter)>();
        if (_continuousSelectionAnchor == null || _continuousSelectionFocus == null)
            return result;

        int aPage = _continuousSelectionPage, fPage = _continuousSelectionFocusPage;
        Letter a = _continuousSelectionAnchor, f = _continuousSelectionFocus;

        if (aPage == fPage)
        {
            result.Add((aPage, a, f));
            return result;
        }

        int startPage; Letter startLet; int endPage; Letter endLet;
        if (aPage < fPage) { startPage = aPage; startLet = a; endPage = fPage; endLet = f; }
        else { startPage = fPage; startLet = f; endPage = aPage; endLet = a; }

        for (int p = startPage; p <= endPage; p++)
        {
            var lp = GetContinuousPageLetters(p);
            if (lp.Reading.Count == 0) continue;
            Letter from = p == startPage ? startLet : lp.Reading[0];
            Letter to = p == endPage ? endLet : lp.Reading[lp.Reading.Count - 1];
            result.Add((p, from, to));
        }
        return result;
    }

    private void EndContinuousTextSelection()
    {
        if (Document == null ||
            _continuousSelectionAnchor == null || _continuousSelectionFocus == null ||
            _continuousSelectionPage < 1)
            return;

        var endpoints = ComputeContinuousSpanEndpoints();
        if (endpoints.Count == 0) return;

        var parts = new List<string>(endpoints.Count);
        var singlePageRects = new List<Rect>();

        foreach (var (p, from, to) in endpoints)
        {
            var lp = GetContinuousPageLetters(p);
            var selection = TextSelectionEngine.BuildSelection(
                lp.Reading, lp.Raw, from, to, lp.ColumnGap, WhitespaceMode);
            if (!string.IsNullOrEmpty(selection.Text)) parts.Add(selection.Text);

            // The event's Area/rects are page-local, so they are only meaningful
            // for a single-page selection (they drive CurrentTextSelectionPageArea,
            // which is bound to one page). For a cross-page span leave them empty —
            // the highlight lives in the per-slot overlay, and the copied Text is
            // what a multi-page selection is for.
            if (endpoints.Count == 1)
            {
                var page = Document.GetPage(p);
                singlePageRects = selection.VisualRange
                    .Select(l => ContinuousGlyphToPageLocalRect(page, l.GlyphRectangle))
                    .ToList();
            }
        }

        var text = string.Join("\n", parts);
        Rect? bbox = singlePageRects.Count > 0 ? UnionRects(singlePageRects) : null;
        TextSelected?.Invoke(this, new TextSelectedEventArgs(bbox ?? new Rect(), text, singlePageRects));
    }

    /// <summary>Redraw the whole anchor→focus span, clearing every page first.</summary>
    private void DrawContinuousSelectionSpan()
    {
        ClearContinuousSelectionHighlight();
        if (Document == null || _continuousSlots == null) return;

        foreach (var (p, from, to) in ComputeContinuousSpanEndpoints())
        {
            var lp = GetContinuousPageLetters(p);
            var range = TextSelectionEngine.ColumnAwareRange(lp.Reading, from, to, lp.ColumnGap);
            AddContinuousPageHighlights(p, range);
        }
    }

    /// <summary>Append highlight rects for one page's selected letters (no clear).</summary>
    private void AddContinuousPageHighlights(int pageNumber, IReadOnlyList<Letter> letters)
    {
        if (_continuousSlots == null || pageNumber < 1 || pageNumber > _continuousSlots.Count)
            return;

        var slot = _continuousSlots[pageNumber - 1];
        PdfPage page;
        try { page = Document!.GetPage(pageNumber); }
        catch { return; }

        for (int i = 0; i < letters.Count; i++)
        {
            // #833: widen degenerate ~0-width glyphs to their advance so the
            // highlight is visible on fonts that report no glyph width.
            var glyph = TextSelectionEngine.EffectiveHighlightRect(letters, i);
            var r = ContinuousGlyphToPageLocalRect(page, glyph);
            slot.SelectionRects.Add(new PdfSelectionHighlight(r.X, r.Y, r.Width, r.Height));
        }
    }

    /// <summary>A glyph's content rectangle as page-local continuous-view DIPs (the slot Border's space).</summary>
    private Rect ContinuousGlyphToPageLocalRect(PdfPage page, PdfRectangle glyph)
    {
        var dips = PdfCoordinateMapper.ToContinuousDips(
            page,
            PdfPageRect.FromContentPoints(page.PageNumber, glyph),
            PointsToDip * ZoomLevel);
        return new Rect(dips.X, dips.Y, dips.Width, dips.Height);
    }

    /// <summary>Clear every page's continuous selection highlight.</summary>
    public void ClearContinuousSelectionHighlight()
    {
        if (_continuousSlots == null) return;
        foreach (var slot in _continuousSlots)
            if (slot.SelectionRects.Count > 0)
                slot.SelectionRects.Clear();
    }
}

/// <summary>
/// One text-selection highlight rectangle in a continuous-view page's local DIP
/// space (#815). Immutable; bound by the DataTemplate to a Rectangle on the
/// page's Canvas overlay.
/// </summary>
internal sealed class PdfSelectionHighlight
{
    public PdfSelectionHighlight(double x, double y, double width, double height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public double X { get; }
    public double Y { get; }
    public double Width { get; }
    public double Height { get; }
}
