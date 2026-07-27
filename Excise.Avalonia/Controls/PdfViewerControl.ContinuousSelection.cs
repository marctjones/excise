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
/// Bounded, correct first version (#815): a selection lives on a SINGLE page —
/// the page the press landed on. A drag that wanders onto another page is
/// clamped (moves that map to a different page are ignored) rather than drawing a
/// broken cross-page range the engine cannot express. Cross-page selection is
/// deferred.
/// </summary>
public partial class PdfViewerControl
{
    private int _continuousSelectionPage;
    private Letter? _continuousSelectionAnchor;
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
            var reading = TextSelectionEngine.SortReadingOrder(raw);
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

        if (!TryContinuousPointToLetter(e, out var page, out var letter, out _))
            return;

        // Remember the page even if the press missed a glyph, so a drag that
        // starts in a margin and moves onto text on the SAME page can still begin.
        _continuousSelectionPage = page;
        if (letter == null) return;

        _continuousSelectionAnchor = letter;
        _continuousSelectionFocus = letter;
        DrawContinuousSelection(page, new[] { letter });
    }

    private void UpdateContinuousTextSelection(PointerEventArgs e)
    {
        if (!TryContinuousPointToLetter(e, out var page, out var letter, out var letters))
            return;

        // Bounded to the anchor page (#815). A move onto a different page is
        // ignored rather than drawing a cross-page range.
        if (_continuousSelectionAnchor == null)
        {
            // The press missed a glyph but latched the page; adopt the first
            // glyph the drag reaches on that same page as the anchor.
            if (page != _continuousSelectionPage || letter == null) return;
            _continuousSelectionAnchor = letter;
            _continuousSelectionFocus = letter;
            DrawContinuousSelection(page, new[] { letter });
            return;
        }

        if (page != _continuousSelectionPage || letter == null) return;
        if (ReferenceEquals(letter, _continuousSelectionFocus)) return;

        _continuousSelectionFocus = letter;
        var range = TextSelectionEngine.ColumnAwareRange(
            letters.Reading, _continuousSelectionAnchor, _continuousSelectionFocus, letters.ColumnGap);
        DrawContinuousSelection(page, range);
    }

    private void EndContinuousTextSelection()
    {
        if (Document == null ||
            _continuousSelectionAnchor == null || _continuousSelectionFocus == null ||
            _continuousSelectionPage < 1)
            return;

        var letters = GetContinuousPageLetters(_continuousSelectionPage);
        var selection = TextSelectionEngine.BuildSelection(
            letters.Reading, letters.Raw,
            _continuousSelectionAnchor, _continuousSelectionFocus, letters.ColumnGap);

        var page = Document.GetPage(_continuousSelectionPage);
        var dipRects = selection.VisualRange
            .Select(l => ContinuousGlyphToPageLocalRect(page, l.GlyphRectangle))
            .ToList();
        Rect? bbox = dipRects.Count > 0 ? UnionRects(dipRects) : null;
        TextSelected?.Invoke(this, new TextSelectedEventArgs(bbox ?? new Rect(), selection.Text, dipRects));
    }

    private void DrawContinuousSelection(int pageNumber, IReadOnlyList<Letter> letters)
    {
        ClearContinuousSelectionHighlight();
        if (Document == null || _continuousSlots == null ||
            pageNumber < 1 || pageNumber > _continuousSlots.Count)
            return;

        var slot = _continuousSlots[pageNumber - 1];
        PdfPage page;
        try { page = Document.GetPage(pageNumber); }
        catch { return; }

        foreach (var l in letters)
        {
            var r = ContinuousGlyphToPageLocalRect(page, l.GlyphRectangle);
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
