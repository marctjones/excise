using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Excise.Avalonia.Services;
using Excise.Core.Document;
using Excise.Core.Text;

namespace Excise.Avalonia.Controls;

public partial class PdfViewerControl
{
    #region Interaction

    private void OnInteractionLayerPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (IsTypewriterOverlayEvent(e))
            return;

        // First chance: internal-link click in any mode (including None).
        // Links are treated as ambient affordances — like a browser, not
        // a drawing tool — so the redaction/text-selection mode shouldn't
        // suppress them. We only consume the event when a link actually
        // hits, otherwise let the rest of the press handling continue.
        // HitTestLinkForEvent picks the coordinate pipeline for the active
        // view mode (single-page overlay vs continuous slot geometry, #667)
        // but both funnel into this one dispatch switch — the link-activation
        // policy (incl. #625's external/dangerous handling) is never forked.
        var linkHit = HitTestLinkForEvent(e);
        if (linkHit != null)
        {
            switch (linkHit.Kind)
            {
                case PdfLinkKind.InternalDestination:
                    LinkClicked?.Invoke(this, new LinkClickedEventArgs(linkHit.DestinationPage));
                    break;
                case PdfLinkKind.ExternalUri:
                    ExternalLinkClicked?.Invoke(this, new ExternalLinkClickedEventArgs(linkHit.Uri!));
                    break;
                case PdfLinkKind.Dangerous:
                    DangerousLinkClicked?.Invoke(this, new DangerousLinkClickedEventArgs(linkHit.DangerousActionType!));
                    break;
            }
            e.Handled = true;
            return;
        }

        if (InteractionMode == InteractionMode.None)
            return;

        var point = GetPressPoint(e);
        _dragStart = point;
        _isDragging = true;

        if (InteractionMode == InteractionMode.TextSelection)
        {
            // Text-selection mode: hit-test letters instead of drawing a
            // 2-D rectangle. Anchor is the letter under (or nearest to)
            // the press point; focus tracks pointer-moved.
            EnsurePageLettersLoaded();
            _selectionAnchor = HitTestLetterAt(point);
            _selectionFocus = _selectionAnchor;
            ClearSelectionHighlight();
            if (_selectionAnchor != null)
                DrawSelectionRange(new[] { _selectionAnchor });
        }

        e.Handled = true;
    }

    private void OnInteractionLayerPointerMoved(object? sender, PointerEventArgs e)
    {
        if (IsTypewriterOverlayEvent(e))
            return;

        // Link hover feedback (cursor + status text) is an ambient affordance
        // like the click handler above — active in any interaction mode,
        // including mid-drag would be misleading, so skip while dragging (#625).
        // This now runs on *every* pointer move (previously pointer-moved
        // did nothing at all outside a drag), so it must never throw — a
        // cosmetic hover-feedback bug must not be able to break clicking or
        // scrolling by corrupting the interaction pipeline.
        if (!_isDragging)
        {
            try
            {
                UpdateLinkHoverState(HitTestLinkForEvent(e));
            }
            catch
            {
                UpdateLinkHoverState(null);
            }
        }

        if (!_isDragging || InteractionMode == InteractionMode.None)
            return;

        var currentPoint = GetPressPoint(e);

        if (InteractionMode == InteractionMode.Redaction ||
            InteractionMode == InteractionMode.FormAuthoring)
        {
            DrawTemporaryRedactionRectangle(_dragStart, currentPoint);
        }
        else if (InteractionMode == InteractionMode.Typewriter)
        {
            DrawTemporaryTypewriterRectangle(_dragStart, currentPoint);
        }
        else if (InteractionMode == InteractionMode.TextSelection)
        {
            // Letter-by-letter highlight as the user drags from anchor.
            if (_selectionAnchor == null || _readingOrderedLetters == null) return;
            var hit = HitTestLetterAt(currentPoint);
            if (hit == null) return;
            // Re-draw only when focus actually moves to a different letter.
            if (ReferenceEquals(hit, _selectionFocus)) return;
            _selectionFocus = hit;
            var range = TextSelectionEngine.RangeBetween(
                _readingOrderedLetters, _selectionAnchor, _selectionFocus);
            DrawSelectionRange(range);
        }

        e.Handled = true;
    }

    private void OnInteractionLayerPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (IsTypewriterOverlayEvent(e))
            return;

        if (!_isDragging)
            return;

        _isDragging = false;

        if (InteractionMode == InteractionMode.Redaction)
        {
            var endPoint = GetPressPoint(e);
            var rect = CreateRect(_dragStart, endPoint);
            // GetPressPoint returns coordinates in the rendered page's
            // pre-zoom DIP/pixel space. Do not divide by ZoomLevel here;
            // doing so shifts the selected area before redaction conversion.
            // See issue #472.
            RedactionDrawn?.Invoke(this, new RedactionDrawnEventArgs(ViewerDipsRect(rect, CurrentPage)));
            ClearTemporaryDrawings();
        }
        else if (InteractionMode == InteractionMode.FormAuthoring)
        {
            var endPoint = GetPressPoint(e);
            var dipRect = CreateRect(_dragStart, endPoint);
            ClearTemporaryDrawings();
            // Convert from viewer DIPs to PDF points (Y-flipped).
            if (Document != null && dipRect.Width > 4 && dipRect.Height > 4)
            {
                var page = Document.GetPage(CurrentPage);
                var pdfRect = PdfCoordinateMapper
                    .ToContentPoints(page, ViewerDipsRect(dipRect, CurrentPage))
                    .ToPdfRectangle()
                    .Normalize();
                FormFieldRectDrawn?.Invoke(this,
                    new FormFieldRectDrawnEventArgs(pdfRect, CurrentPage));
            }
        }
        else if (InteractionMode == InteractionMode.Typewriter)
        {
            var endPoint = GetPressPoint(e);
            var dipRect = NormalizeTypewriterDipRect(CreateRect(_dragStart, endPoint));
            ClearTemporaryDrawings();

            if (Document != null && dipRect.Width > 4 && dipRect.Height > 4)
            {
                var pdfRect = ViewerDipsToPdfRect(dipRect, CurrentPage);
                TypewriterTextCreated?.Invoke(this,
                    new TypewriterTextCreatedEventArgs(pdfRect, CurrentPage));
            }
        }
        else if (InteractionMode == InteractionMode.TextSelection &&
                 _selectionAnchor != null && _selectionFocus != null &&
                 _readingOrderedLetters != null)
        {
            var range = TextSelectionEngine.RangeBetween(
                _readingOrderedLetters, _selectionAnchor, _selectionFocus);
            var text = TextSelectionEngine.JoinText(range);
            var letterDips = range
                .Select(l => PdfRectangleToDips(l.GlyphRectangle))
                .ToList();
            // Bounding box of the whole run — keeps backwards compat with
            // listeners that just want a single Rect.
            Rect? bbox = letterDips.Count > 0
                ? UnionRects(letterDips)
                : null;
            TextSelected?.Invoke(this, new TextSelectedEventArgs(
                bbox ?? new Rect(), text, letterDips));
        }
        else
        {
            ClearTemporaryDrawings();
        }

        e.Handled = true;
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
            _readingOrderedLetters = TextSelectionEngine.SortReadingOrder(_currentPageLetters);
            _lettersPageNumber = CurrentPage;
        }
        catch
        {
            _currentPageLetters = new List<Letter>();
            _readingOrderedLetters = new List<Letter>();
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
    private Point GetPressPoint(PointerEventArgs e)
    {
        if (_overlayCanvas != null) return e.GetPosition(_overlayCanvas);
        if (_pdfImage != null) return e.GetPosition(_pdfImage);
        return e.GetPosition(this);
    }

    private void EnsurePageLinksLoaded()
    {
        if (Document == null) return;
        if (_linksPageNumber == CurrentPage && _currentPageLinks != null) return;
        try
        {
            var page = Document.GetPage(CurrentPage);
            _currentPageLinks = page.GetLinks();
            _linksPageNumber = CurrentPage;
        }
        catch
        {
            _currentPageLinks = Array.Empty<PdfLink>();
            _linksPageNumber = CurrentPage;
        }
    }

    /// <summary>
    /// Link hit-test for a pointer event in whatever view mode is active
    /// (#667). Single-page mode reads the pointer relative to the overlay
    /// canvas (bitmap-native, pre-zoom — see <see cref="GetPressPoint"/>);
    /// continuous mode maps the pointer through the per-slot geometry the
    /// tile renderer already uses. Both end in the same PDF content-space
    /// rect containment, so a link behaves identically in either mode.
    /// </summary>
    private PdfLink? HitTestLinkForEvent(PointerEventArgs e)
    {
        return ViewMode == PdfViewMode.Continuous
            ? HitTestContinuousLinkAt(e)
            : HitTestLinkAt(GetPressPoint(e));
    }

    private PdfLink? HitTestLinkAt(Point dipPoint)
    {
        EnsurePageLinksLoaded();
        if (_currentPageLinks == null || _currentPageLinks.Count == 0) return null;
        if (Document == null) return null;
        var page = Document.GetPage(CurrentPage);
        var contentPoint = PdfCoordinateMapper.ToContentPoints(
            page,
            PdfPageRect.ViewerDips(CurrentPage, dipPoint.X, dipPoint.Y, 0, 0, _currentSinglePageRenderDpi));
        return FindLinkAt(_currentPageLinks, contentPoint.X, contentPoint.Y);
    }

    /// <summary>
    /// Continuous-mode link hit-test (#667). The pointer position relative to
    /// the ContinuousItems ItemsControl is mapped to (page, in-page dips) via
    /// the same <see cref="PdfPageSlot"/> TopDip/DisplayWidth/DisplayHeight
    /// layout math tile rendering uses, then to PDF content points through
    /// <see cref="PdfCoordinateMapper"/>'s ContinuousDips space — which
    /// handles the dip→point scale, the Y flip, and page /Rotate exactly the
    /// way every other overlay conversion does. No new coordinate math.
    /// </summary>
    private PdfLink? HitTestContinuousLinkAt(PointerEventArgs e)
    {
        var doc = Document;
        if (doc == null || _continuousItems == null || _continuousSlots == null) return null;
        var zoom = ZoomLevel;
        if (zoom <= 0) return null;

        var itemsPoint = e.GetPosition(_continuousItems);
        if (!TryMapContinuousPointToPage(
                _continuousSlots, _continuousItems.Bounds.Width, itemsPoint,
                out var pageNumber, out var pagePointDip))
            return null;
        if (pageNumber < 1 || pageNumber > doc.PageCount) return null;

        var links = GetContinuousPageLinks(pageNumber);
        if (links.Count == 0) return null;

        var page = doc.GetPage(pageNumber);
        var contentPoint = PdfCoordinateMapper.ToContentPoints(
            page,
            new PdfPageRect(pageNumber, pagePointDip.X, pagePointDip.Y, 0, 0,
                PdfCoordinateSpace.ContinuousDips, PointsToDip * zoom));
        return FindLinkAt(links, contentPoint.X, contentPoint.Y);
    }

    /// <summary>Rect containment over a page's link annotations, in PDF content points.</summary>
    private static PdfLink? FindLinkAt(IReadOnlyList<PdfLink> links, double pdfX, double pdfY)
    {
        foreach (var link in links)
        {
            var r = link.Rect;
            if (pdfX >= r.Left && pdfX <= r.Right &&
                pdfY >= r.Bottom && pdfY <= r.Top)
                return link;
        }
        return null;
    }

    /// <summary>
    /// Per-page link cache for continuous mode. Hover hit-testing runs on
    /// every pointer move over the reading view and the view shows many
    /// pages, so re-parsing /Annots per move is off the table — each page's
    /// links are parsed once and kept until the document changes or the host
    /// bumps RenderVersion (both call <see cref="InvalidateContinuousCache"/>,
    /// which clears this cache). Link lists are tiny (a few rects), so an
    /// unbounded per-document dictionary is fine where bitmaps were not.
    /// </summary>
    private IReadOnlyList<PdfLink> GetContinuousPageLinks(int pageNumber)
    {
        if (_continuousPageLinks.TryGetValue(pageNumber, out var cached))
            return cached;

        IReadOnlyList<PdfLink> links;
        try
        {
            links = Document?.GetPage(pageNumber).GetLinks() ?? Array.Empty<PdfLink>();
        }
        catch
        {
            links = Array.Empty<PdfLink>();
        }
        _continuousPageLinks[pageNumber] = links;
        return links;
    }

    /// <summary>
    /// Cursor + <see cref="LinkHovered"/> feedback for whatever link (if any)
    /// is currently under the pointer (#625). Reference-compares against the
    /// last hover target so this is a no-op while the pointer sits still or
    /// moves within the same link's rect, and only fires on genuine enter/exit.
    /// </summary>
    private void UpdateLinkHoverState(PdfLink? link)
    {
        if (ReferenceEquals(link, _lastHoveredLink))
            return;
        _lastHoveredLink = link;

        if (link == null)
        {
            Cursor = Cursor.Default;
            LinkHovered?.Invoke(this, new LinkHoveredEventArgs(null));
            return;
        }

        Cursor = new Cursor(StandardCursorType.Hand);
        var displayText = link.Kind switch
        {
            PdfLinkKind.ExternalUri => link.Uri,
            PdfLinkKind.InternalDestination => $"Go to page {link.DestinationPage}",
            PdfLinkKind.Dangerous => "Blocked link",
            _ => null
        };
        LinkHovered?.Invoke(this, new LinkHoveredEventArgs(displayText));
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

    private static Rect UnionRects(IReadOnlyList<Rect> rects)
    {
        var x1 = double.PositiveInfinity; var y1 = double.PositiveInfinity;
        var x2 = double.NegativeInfinity; var y2 = double.NegativeInfinity;
        foreach (var r in rects)
        {
            if (r.X < x1) x1 = r.X;
            if (r.Y < y1) y1 = r.Y;
            if (r.Right > x2) x2 = r.Right;
            if (r.Bottom > y2) y2 = r.Bottom;
        }
        return new Rect(x1, y1, x2 - x1, y2 - y1);
    }

    private void DrawSelectionRange(IReadOnlyList<Letter> letters)
    {
        var layer = this.FindControl<Canvas>("TextSelectionLayer");
        if (layer == null) return;
        layer.Children.Clear();
        var fill = new SolidColorBrush(Color.FromArgb(0x60, 0x33, 0x99, 0xFF));
        foreach (var l in letters)
        {
            var r = PdfRectangleToDips(l.GlyphRectangle);
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
    public void ClearSelectionHighlight()
    {
        var layer = this.FindControl<Canvas>("TextSelectionLayer");
        layer?.Children.Clear();
    }

    private static Rect CreateRect(Point p1, Point p2)
    {
        var left = Math.Min(p1.X, p2.X);
        var top = Math.Min(p1.Y, p2.Y);
        var right = Math.Max(p1.X, p2.X);
        var bottom = Math.Max(p1.Y, p2.Y);

        return new Rect(left, top, right - left, bottom - top);
    }

    private Rectangle? _tempRedactionRect;
    private Rectangle? _tempSelectionRect;

    private void DrawTemporaryRedactionRectangle(Point start, Point end)
    {
        if (_interactionLayer == null) return;

        var rect = CreateRect(start, end);

        if (_tempRedactionRect == null)
        {
            _tempRedactionRect = new Rectangle
            {
                Fill = new SolidColorBrush(Color.FromArgb(0x40, 0x00, 0x00, 0x00)), // Semi-transparent black
                Stroke = Brushes.Red,
                StrokeThickness = 2,
                StrokeDashArray = new AvaloniaList<double> { 5, 5 }
            };
            _interactionLayer.Children.Add(_tempRedactionRect);
        }

        Canvas.SetLeft(_tempRedactionRect, rect.X);
        Canvas.SetTop(_tempRedactionRect, rect.Y);
        _tempRedactionRect.Width = rect.Width;
        _tempRedactionRect.Height = rect.Height;
        _tempRedactionRect.IsVisible = true;
    }

    private void DrawTemporarySelectionRectangle(Point start, Point end)
    {
        if (_interactionLayer == null) return;

        var rect = CreateRect(start, end);

        if (_tempSelectionRect == null)
        {
            _tempSelectionRect = new Rectangle
            {
                Fill = new SolidColorBrush(Color.FromArgb(0x40, 0x4C, 0xAF, 0x50)), // Semi-transparent green
                Stroke = new SolidColorBrush(Color.FromArgb(0xFF, 0x4C, 0xAF, 0x50)),
                StrokeThickness = 2,
                StrokeDashArray = new AvaloniaList<double> { 5, 5 }
            };
            _interactionLayer.Children.Add(_tempSelectionRect);
        }

        Canvas.SetLeft(_tempSelectionRect, rect.X);
        Canvas.SetTop(_tempSelectionRect, rect.Y);
        _tempSelectionRect.Width = rect.Width;
        _tempSelectionRect.Height = rect.Height;
        _tempSelectionRect.IsVisible = true;
    }

    private void ClearTemporaryDrawings()
    {
        if (_tempRedactionRect != null)
        {
            _tempRedactionRect.IsVisible = false;
        }

        if (_tempSelectionRect != null)
        {
            _tempSelectionRect.IsVisible = false;
        }

        if (_tempTypewriterRect != null)
        {
            _tempTypewriterRect.IsVisible = false;
        }
    }

    #endregion
}
