using Avalonia.Collections;
using Avalonia.Controls.Shapes;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia;
using Excise.Core.Document;
using Excise.Core.Text;
using System.Collections.Generic;
using System.Linq;
using System;
using static Excise.Avalonia.Controls.PdfViewerControl;

namespace Excise.Avalonia.Controls;

/// <summary>
/// Single-page letter selection: the page's letters, the hit-test, the highlight and the
/// finished selection (#373, #815), and the pointer basis every single-page gesture uses.
/// </summary>
internal sealed partial class SinglePageView
{
    /// <summary>
    /// Build the selection for the current anchor and focus and raise <see cref="TextSelected"/>.
    /// Shared by a finished drag and <see cref="SelectAllText"/>, so the two cannot report a
    /// selection differently.
    /// </summary>
    private void RaiseSinglePageTextSelected()
    {
        if (_selectionAnchor == null || _selectionFocus == null || _readingOrderedLetters == null)
            return;

        // Highlight rects follow visual order (contiguous glyphs, incl.
        // within an RTL run); the copied text is re-ordered to logical
        // reading order so Arabic/Hebrew reads correctly (#373). Column
        // gutters are respected so a column-local drag stays in-column.
        var selection = TextSelectionEngine.BuildSelection(
            _readingOrderedLetters,
            _currentPageLetters ?? _readingOrderedLetters,
            _selectionAnchor, _selectionFocus, _columnGapThreshold, WhitespaceMode);
        var text = selection.Text;
        var letterDips = selection.VisualRange
            .Select(l => PdfRectangleToDips(l.GlyphRectangle))
            .ToList();
        // Bounding box of the whole run, bound to the page it is on.
        Rect? bbox = letterDips.Count > 0
            ? UnionRects(letterDips)
            : null;
        TextSelected?.Invoke(this, new TextSelectedEventArgs(
            text, bbox is { Width: > 0, Height: > 0 } b ? ViewerDipsRect(b, CurrentPage) : null));
    }

    /// <summary>
    /// Cache the current page's letters (in PDF points) keyed by page
    /// number so repeated text-selection drags on the same page don't
    /// re-extract. Letters are always re-fetched when CurrentPage changes.
    /// </summary>
    private void EnsurePageLettersLoaded()
    {
        if (Document == null) return;
        if (_lettersPageNumber == CurrentPage && _currentPageLetters != null) return;
        try
        {
            var page = Document.GetPage(CurrentPage);
            _currentPageLetters = page.Letters?.ToList() ?? new List<Letter>();
            _readingOrderedLetters = TextSelectionEngine.SortReadingOrder(_currentPageLetters, ReadingOrderStrategy);
            // Column-gutter width depends only on the page's glyph metrics, so
            // compute it once here rather than on every pointer-move (#373).
            _columnGapThreshold = TextSelectionEngine.EstimateColumnGap(_readingOrderedLetters);
            _lettersPageNumber = CurrentPage;
        }
        catch
        {
            _currentPageLetters = new List<Letter>();
            _readingOrderedLetters = new List<Letter>();
            _columnGapThreshold = double.PositiveInfinity;
            _lettersPageNumber = CurrentPage;
        }
    }

    /// <summary>
    /// Pointer coords in bitmap-native (pre-zoom) DIPs. We need a control
    /// INSIDE the LayoutTransformControl wrapper to get pre-zoom values;
    /// asking the wrapper itself returns post-zoom values that miss
    /// every link/letter rect when zoom != 1 (which auto-fit makes the
    /// default).
    /// </summary>
    internal Point GetPressPoint(PointerEventArgs e)
    {
        // The overlay canvas is the correct basis: it shares the ZoomHost
        // transform, so GetPosition inverts the zoom. The fallbacks do NOT
        // share that transform at the same offset — if one is ever taken at
        // zoom != 1, every mapped point is off by the zoom factor. Trace which
        // basis served the point so a live 'overlay way off' report can be
        // pinned to its source (#693 investigation).
        if (OverlayCanvas != null)
        {
            var p = e.GetPosition(OverlayCanvas);
            Trace($"PressPoint basis=overlay p=({p.X:F0},{p.Y:F0}) zoom={ZoomLevel:F3}");
            return p;
        }
        if (PdfImage != null)
        {
            var p = e.GetPosition(PdfImage);
            Trace($"PressPoint basis=IMAGE-FALLBACK p=({p.X:F0},{p.Y:F0}) zoom={ZoomLevel:F3}");
            return p;
        }
        var root = e.GetPosition(this);
        Trace($"PressPoint basis=ROOT-FALLBACK p=({root.X:F0},{root.Y:F0}) zoom={ZoomLevel:F3}");
        return root;
    }

    private Letter? HitTestLetterAt(Point dipPoint)
    {
        if (_currentPageLetters == null || _currentPageLetters.Count == 0) return null;
        if (Document == null) return null;
        var page = Document.GetPage(CurrentPage);
        // Pointer coords are in pre-zoom DIPs of the InteractionLayer.
        // Route through the tagged mapper so scale, Y direction, and page
        // rotation stay consistent with overlays and redaction.
        var contentPoint = PdfCoordinateMapper.ToContentPoints(
            page,
            PdfPageRect.ViewerDips(CurrentPage, dipPoint.X, dipPoint.Y, 0, 0, _currentSinglePageRenderDpi));
        var pdfX = contentPoint.X;
        var pdfY = contentPoint.Y;
        return TextSelectionEngine.HitTest(_currentPageLetters, pdfX, pdfY);
    }

    private Rect PdfRectangleToDips(PdfRectangle r)
    {
        if (Document == null) return default;
        var page = Document.GetPage(CurrentPage);
        return ToAvaloniaRect(ToViewerDips(PdfPageRect.FromContentPoints(page.PageNumber, r)));
    }

    /// <summary>
    /// Test seam (#815): the single-page content-points → viewer-DIP mapping used
    /// to position a glyph's selection highlight. Exposed so a GUI test can compute
    /// the pointer position of a known glyph to drive a real selection gesture. It
    /// is deliberately NOT the on-screen-position oracle — the test verifies the
    /// drawn highlight's real layout geometry (origin share + MediaBox fraction in
    /// the page image), which this method cannot vouch for.
    /// </summary>
    internal Rect GlyphRectToViewerDipsForTest(PdfRectangle glyphRect) => PdfRectangleToDips(glyphRect);

    private void DrawSelectionRange(IReadOnlyList<Letter> letters)
    {
        var layer = TextSelectionLayer;
        if (layer == null) return;
        layer.Children.Clear();
        if (letters.Count > 0)
        {
            var first = PdfRectangleToDips(letters[0].GlyphRectangle);
            // The origin probe: if the highlight canvas and the page image do
            // not share an origin in viewer space, every highlight is offset by
            // the delta — the live 'highlight far to the left' report.
            var imgO = PdfImage?.TranslatePoint(new Point(0, 0), this);
            var layO = layer.TranslatePoint(new Point(0, 0), this);
            Trace($"DrawSelection n={letters.Count} first=({first.X:F0},{first.Y:F0} {first.Width:F0}x{first.Height:F0}) " +
                  $"page={CurrentPage} zoom={ZoomLevel:F3} " +
                  $"imgOrigin=({imgO?.X:F0},{imgO?.Y:F0}) layerOrigin=({layO?.X:F0},{layO?.Y:F0}) " +
                  $"imgW={PdfImage?.Width:F0} layerW={layer.Bounds.Width:F0}");
        }
        var fill = new SolidColorBrush(Color.FromArgb(0x60, 0x33, 0x99, 0xFF));
        for (int i = 0; i < letters.Count; i++)
        {
            // #833: widen degenerate ~0-width glyphs to their advance so the
            // highlight is visible on fonts that report no glyph width.
            var glyph = TextSelectionEngine.EffectiveHighlightRect(letters, i);
            var r = PdfRectangleToDips(glyph);
            var rect = new Rectangle
            {
                Fill = fill,
                Width = r.Width,
                Height = r.Height
            };
            Canvas.SetLeft(rect, r.X);
            Canvas.SetTop(rect, r.Y);
            layer.Children.Add(rect);
        }
    }

    /// <summary>Clear any in-progress text selection (e.g. switching pages).</summary>
    internal void ClearSelectionHighlight()
    {
        var layer = TextSelectionLayer;
        layer?.Children.Clear();
    }

}
