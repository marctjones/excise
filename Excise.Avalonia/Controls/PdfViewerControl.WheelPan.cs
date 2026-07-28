using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Excise.Avalonia.Controls;

public partial class PdfViewerControl
{
    #region Wheel zoom + middle-button pan (#827)

    // Pan gesture state. Pan is an ambient middle-button-drag gesture available
    // in every InteractionMode (it is NOT tied to the dead InteractionMode.Pan
    // enum value — that value is left in place for a possible future explicit
    // hand-tool). Grabbing the page and dragging moves the active ScrollViewer's
    // offset so the content follows the pointer (browser "grab" convention).
    private bool _isPanning;
    private Point _panStartPointer;
    private Vector _panStartOffset;

    /// <summary>
    /// The ScrollViewer that currently owns the viewport — the continuous
    /// stack in Continuous mode, otherwise the single-page scroller.
    /// </summary>
    private ScrollViewer? ActiveScrollViewer =>
        ViewMode == PdfViewMode.Continuous && _continuousScrollViewer != null
            ? _continuousScrollViewer
            : _scrollViewer;

    /// <summary>
    /// Ctrl (or Meta/⌘) + wheel zooms; a plain wheel is left untouched so the
    /// ScrollViewer scrolls natively. Registered on the Tunnel pass at the root
    /// so it runs before the inner ScrollViewer's wheel handler; marking the
    /// event Handled for the zoom case suppresses the native scroll.
    /// </summary>
    private void OnViewerPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        bool zoomModifier = e.KeyModifiers.HasFlag(KeyModifiers.Control) ||
                            e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (!zoomModifier)
            return; // plain wheel → let the ScrollViewer scroll normally

        if (e.Delta.Y > 0)
            ZoomIn();
        else if (e.Delta.Y < 0)
            ZoomOut();
        else
            return;

        // Consumed: stop the ScrollViewer from also scrolling on this wheel tick.
        e.Handled = true;
    }

    private void OnPanPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed)
            return;

        var scroller = ActiveScrollViewer;
        if (scroller == null)
            return;

        _isPanning = true;
        _panStartPointer = e.GetPosition(this);
        _panStartOffset = scroller.Offset;
        e.Pointer.Capture(this);
        Cursor = new Cursor(StandardCursorType.SizeAll);
        e.Handled = true;
    }

    private void OnPanPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isPanning)
            return;

        var scroller = ActiveScrollViewer;
        if (scroller == null)
            return;

        // Content follows the pointer: dragging down reveals content above, so
        // the offset moves opposite to the pointer delta.
        var current = e.GetPosition(this);
        var delta = current - _panStartPointer;
        scroller.Offset = _panStartOffset - delta;
        e.Handled = true;
    }

    private void OnPanPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isPanning)
            return;

        _isPanning = false;
        e.Pointer.Capture(null);
        Cursor = Cursor.Default;
        e.Handled = true;
    }

    #endregion
}
