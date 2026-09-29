using Avalonia.Controls.Shapes;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia;
using Excise.Core.Document;
using Excise.Core.Editing;
using System.Collections.Generic;
using System.Linq;
using System;
using static Excise.Avalonia.Controls.PdfViewerControl;

namespace Excise.Avalonia.Controls;

/// <summary>
/// The typewriter layer (#780, #1648): the pending boxes on the current page, which one is being
/// edited, and the DIP ↔ PDF mapping their placement and bounds go through.
/// </summary>
internal sealed partial class SinglePageView
{
    private const double MinimumTypewriterWidthDips = TypewriterEditorBox.MinimumWidthDips;
    private const double MinimumTypewriterHeightDips = TypewriterEditorBox.MinimumHeightDips;
    private const double DefaultTypewriterWidthDips = 220;
    private const double DefaultTypewriterHeightDips = 42;

    /// <summary>
    /// The one box being edited, or null (#1648).
    /// </summary>
    /// <remarks>
    /// Chrome — the outline, the tinted fill, the drag handle, the delete
    /// button, the resize grip — used to be keyed to the MODE, so every pending
    /// box wore it whenever typewriter mode was on. Clicking away from a box
    /// you had just typed in left its outline sitting on the page, and there
    /// was no way to see what the document would actually look like without
    /// leaving the mode entirely. Reported live: "when I click off of a text
    /// box I just made the box outline should not be visible again... when I am
    /// not in the box editing it the text should just be displayed as it will
    /// show up in the saved document."
    /// </remarks>
    private Guid? _focusedTypewriterId;

    /// <summary>
    /// How to (un)dress each box on screen, by operation id. Rebuilt with the
    /// layer; used so focusing one box can strip the chrome from the rest
    /// without tearing down and recreating their editors — a rebuild would
    /// throw away the caret position of the box being clicked into.
    /// </summary>
    private readonly Dictionary<Guid, Action<bool>> _typewriterChrome = new();

    /// <summary>
    /// Give <paramref name="id"/> the chrome and take it from everything else.
    /// </summary>
    private void FocusTypewriterBox(Guid id)
    {
        _focusedTypewriterId = id;
        foreach (var (operationId, applyChrome) in _typewriterChrome)
            applyChrome(operationId == id);
    }

    /// <summary>Nothing is being edited (#1648).</summary>
    internal void ClearTypewriterFocus() => _focusedTypewriterId = null;

    /// <summary>
    /// Discard every pending box nobody typed in (#1648). An empty box is a
    /// click the user backed out of.
    /// </summary>
    internal void DiscardEmptyPendingTypewriterText(Guid? except = null)
    {
        if (TypewriterTextOperations == null)
            return;

        foreach (var operation in TypewriterTextOperations
                     .Where(o => o.IsPending && string.IsNullOrEmpty(o.Text) && o.Id != except)
                     .ToList())
        {
            TypewriterTextDeleted?.Invoke(this,
                new TypewriterTextDeletedEventArgs(operation.Id, operation.PageNumber));
        }
    }

    internal void RedrawTypewriterLayer()
    {
        if (TypewriterLayer == null)
            return;

        TypewriterLayer.Children.Clear();
        _typewriterChrome.Clear();

        if (Document == null || TypewriterTextOperations == null)
            return;

        foreach (var operation in TypewriterTextOperations
                     .Where(o => o.IsPending && o.PageNumber == CurrentPage))
        {
            var rect = PdfRectToViewerDips(operation.Bounds, operation.PageNumber);
            var editor = CreateTypewriterEditor(operation, rect);

            Canvas.SetLeft(editor, rect.X);
            Canvas.SetTop(editor, rect.Y);
            TypewriterLayer.Children.Add(editor);
        }
    }

    private Control CreateTypewriterEditor(PdfTypewriterTextOperation operation, Rect rect)
    {
        // #1648: chrome belongs to the box being EDITED, not to the mode.
        var inTypewriterMode = InteractionMode == InteractionMode.Typewriter;
        var editing = inTypewriterMode && _focusedTypewriterId == operation.Id;
        var box = new TypewriterEditorBox(operation, rect, ViewerUnitsPerPoint, TypewriterLayer!, this);

        _typewriterChrome[operation.Id] = box.ApplyChrome;
        box.ApplyChrome(editing);

        // A box in the mode is clickable so it can be edited again; out of the
        // mode it is inert and the page reads normally.
        box.Shell.IsHitTestVisible = inTypewriterMode;

        if (inTypewriterMode && string.IsNullOrEmpty(operation.Text))
        {
            Dispatcher.UIThread.Post(() => box.Editor.Focus(), DispatcherPriority.Background);
        }

        return box.Shell;
    }

    // ── ITypewriterEditSink (#1842): what a box reports, mapped onto the viewer ──

    void ITypewriterEditSink.TextEdited(PdfTypewriterTextOperation operation, string text) =>
        TypewriterTextEdited?.Invoke(this,
            new TypewriterTextEditedEventArgs(operation.Id, text, operation.PageNumber));

    void ITypewriterEditSink.DeleteRequested(PdfTypewriterTextOperation operation) =>
        TypewriterTextDeleted?.Invoke(this,
            new TypewriterTextDeletedEventArgs(operation.Id, operation.PageNumber));

    void ITypewriterEditSink.BoundsChanged(PdfTypewriterTextOperation operation, Rect dipRect)
    {
        var rect = NormalizeTypewriterDipRect(dipRect);
        TypewriterTextBoundsChanged?.Invoke(this,
            new TypewriterTextBoundsChangedEventArgs(
                operation.Id,
                ViewerDipsToPdfRect(rect, operation.PageNumber),
                operation.PageNumber));
    }

    Rect ITypewriterEditSink.NormalizeDipRect(Rect dipRect) => NormalizeTypewriterDipRect(dipRect);

    void ITypewriterEditSink.FocusEntered(PdfTypewriterTextOperation operation) => FocusTypewriterBox(operation.Id);

    void ITypewriterEditSink.FocusLeft(PdfTypewriterTextOperation operation)
    {
        if (_focusedTypewriterId == operation.Id)
            _focusedTypewriterId = null;
    }

    // The viewer takes focus back: this view is not focusable, and the viewer's
    // key handler is where Esc and the page keys land.
    void ITypewriterEditSink.ReleaseEditorFocus() => _viewer.Focus();

    /// <summary>
    /// Places a pending type-over box from a pointer gesture and raises
    /// <see cref="TypewriterTextCreated"/>. A plain click (start == end, or a
    /// sub-4-DIP drag) places a DEFAULT-sized box at the press point; a larger
    /// drag places a box sized to the drag (#780). <see
    /// cref="NormalizeTypewriterDipRect"/> substitutes the default size for
    /// sub-4 dimensions and clamps the box onto the page, so there is no
    /// "drag &gt; 4 DIPs" gate here — click-to-place is a first-class path.
    /// Internal so the pointer-driven creation path is unit-testable without
    /// synthesising raw pointer events (#780 test gap).
    /// </summary>
    internal void CreateTypewriterTextFromPointer(Point start, Point end)
    {
        if (Document == null)
            return;

        var dipRect = NormalizeTypewriterDipRect(CreateRect(start, end));
        var pdfRect = ViewerDipsToPdfRect(dipRect, CurrentPage);
        TypewriterTextCreated?.Invoke(this,
            new TypewriterTextCreatedEventArgs(pdfRect, CurrentPage));
    }

    internal Rect NormalizeTypewriterDipRect(Rect rect)
    {
        if (Document == null || CurrentPage < 1 || CurrentPage > Document.PageCount)
        {
            var fallbackWidth = rect.Width < 4 ? DefaultTypewriterWidthDips : rect.Width;
            var fallbackHeight = rect.Height < 4 ? DefaultTypewriterHeightDips : rect.Height;
            return new Rect(rect.X, rect.Y, fallbackWidth, fallbackHeight);
        }

        var page = Document.GetPage(CurrentPage);
        var pageWidth = page.VisualWidth * ViewerUnitsPerPoint;
        var pageHeight = page.VisualHeight * ViewerUnitsPerPoint;

        var width = rect.Width < 4 ? DefaultTypewriterWidthDips : rect.Width;
        var height = rect.Height < 4 ? DefaultTypewriterHeightDips : rect.Height;
        width = Math.Clamp(width, MinimumTypewriterWidthDips, Math.Max(MinimumTypewriterWidthDips, pageWidth));
        height = Math.Clamp(height, MinimumTypewriterHeightDips, Math.Max(MinimumTypewriterHeightDips, pageHeight));

        var maxLeft = Math.Max(0, pageWidth - width);
        var maxTop = Math.Max(0, pageHeight - height);
        var left = Math.Clamp(rect.X, 0, maxLeft);
        var top = Math.Clamp(rect.Y, 0, maxTop);
        return new Rect(left, top, width, height);
    }

    internal PdfRectangle ViewerDipsToPdfRect(Rect dipRect, int pageNumber)
    {
        if (Document == null || pageNumber < 1 || pageNumber > Document.PageCount)
            return new PdfRectangle(0, 0, 0, 0);

        var page = Document.GetPage(pageNumber);
        return PdfCoordinateMapper
            .ToContentPoints(page, ViewerDipsRect(dipRect, pageNumber))
            .ToPdfRectangle()
            .Normalize();
    }

    internal Rect PdfRectToViewerDips(PdfRectangle pdfRect, int pageNumber)
    {
        if (Document == null || pageNumber < 1 || pageNumber > Document.PageCount)
            return default;

        var page = Document.GetPage(pageNumber);
        var viewerRect = PdfCoordinateMapper.ToViewerDips(
            page,
            PdfPageRect.FromContentPoints(pageNumber, pdfRect),
            _currentSinglePageRenderDpi);
        return NormalizeTypewriterDipRect(ToAvaloniaRect(viewerRect));
    }

}
