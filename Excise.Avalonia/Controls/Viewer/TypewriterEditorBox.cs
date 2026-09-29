using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Excise.Core.Editing;

namespace Excise.Avalonia.Controls;

/// <summary>
/// One pending typewriter box on the typewriter layer (#780): a frame, a drag
/// handle, the text editor, a delete button and a resize grip, with move and
/// resize captured against the layer. What the user does is reported to an
/// <see cref="ITypewriterEditSink"/>; which box wears the editing chrome is the
/// sink's decision, applied through <see cref="ApplyChrome"/> (#1648).
/// </summary>
internal sealed class TypewriterEditorBox
{
    internal const double MinimumWidthDips = 48;
    internal const double MinimumHeightDips = 24;

    private readonly PdfTypewriterTextOperation _operation;
    private readonly Canvas _layer;
    private readonly ITypewriterEditSink _sink;
    private readonly Border _frame;
    private readonly Border _dragHandle;
    private readonly Button _deleteButton;
    private readonly Border _resizeGrip;

    /// <summary>The box, to place on the layer at the rect it was built from.</summary>
    internal Grid Shell { get; }

    /// <summary>The text editor, for a caller that wants to focus a new box.</summary>
    internal TextBox Editor { get; }

    internal TypewriterEditorBox(
        PdfTypewriterTextOperation operation, Rect rect, double unitsPerPoint, Canvas layer, ITypewriterEditSink sink)
    {
        _operation = operation;
        _layer = layer;
        _sink = sink;

        var shell = new Grid
        {
            Width = Math.Max(rect.Width, MinimumWidthDips),
            Height = Math.Max(rect.Height, MinimumHeightDips),
            MinWidth = MinimumWidthDips,
            MinHeight = MinimumHeightDips,
            ClipToBounds = false,
        };
        shell.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        shell.RowDefinitions.Add(new RowDefinition(new GridLength(1, GridUnitType.Star)));
        Shell = shell;

        // #1648: chrome belongs to the box being EDITED, not to the mode. Every
        // other box — pending or not, mode on or off — renders as the document
        // will render it.
        _frame = new Border
        {
            CornerRadius = new CornerRadius(2),
        };
        Grid.SetRowSpan(_frame, 2);
        shell.Children.Add(_frame);

        _dragHandle = new Border
        {
            Height = 10,
            Background = new SolidColorBrush(Color.FromArgb(0x55, 0x00, 0x7A, 0xCC)),
            Cursor = new Cursor(StandardCursorType.SizeAll),
        };
        Grid.SetRow(_dragHandle, 0);
        shell.Children.Add(_dragHandle);

        var textBox = new TextBox
        {
            Text = operation.Text,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 1, 4, 3),
            FontSize = Math.Max(8, operation.Style.FontSize * unitsPerPoint),
            Foreground = ToAvaloniaBrush(operation.Style.Color),
            TextAlignment = ToAvaloniaTextAlignment(operation.Style.Alignment),
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        textBox.TextChanged += (_, _) => _sink.TextEdited(operation, textBox.Text ?? string.Empty);
        // Esc on the active box (#780). An EMPTY box is removed — the user
        // backed out before typing, so nothing is lost. A NON-empty box keeps
        // its typed text (already committed via TextChanged) and only exits
        // editing focus: Esc must never silently drop text the user entered.
        textBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape)
                return;

            if (string.IsNullOrEmpty(textBox.Text))
                _sink.DeleteRequested(operation);
            else
                _sink.ReleaseEditorFocus(); // blur the editor, keep the text

            e.Handled = true;
        };
        Grid.SetRow(textBox, 1);
        shell.Children.Add(textBox);
        Editor = textBox;

        _deleteButton = new Button
        {
            Content = "x",
            Width = 20,
            Height = 18,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
        };
        ToolTip.SetTip(_deleteButton, "Delete typewriter text");
        _deleteButton.Click += (_, _) => _sink.DeleteRequested(operation);
        Grid.SetRowSpan(_deleteButton, 2);
        shell.Children.Add(_deleteButton);

        _resizeGrip = new Border
        {
            Width = 12,
            Height = 12,
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x00, 0x7A, 0xCC)),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Cursor = new Cursor(StandardCursorType.TopLeftCorner),
        };
        Grid.SetRowSpan(_resizeGrip, 2);
        shell.Children.Add(_resizeGrip);

        AttachMoveBehavior();
        AttachResizeBehavior();

        textBox.GotFocus += (_, _) => _sink.FocusEntered(operation);
        textBox.LostFocus += (_, _) =>
        {
            // Chrome only. An empty box is NOT deleted here: focus leaves for
            // the style flyout too, and picking a colour before typing is a
            // real way to use this — deleting there broke
            // TypewriterColorPresetAccessibilityTests, which styles a box it
            // has not typed into yet. Empty boxes are swept where they would
            // actually be left behind: when another box is created, and when
            // the mode is left.
            _sink.FocusLeft(operation);
            ApplyChrome(false);
        };
    }

    /// <summary>Dress the box for editing, or show it as the saved document will (#1648).</summary>
    internal void ApplyChrome(bool focused)
    {
        _frame.Background = focused
            ? new SolidColorBrush(Color.FromArgb(0x0A, 0x00, 0x7A, 0xCC))
            : Brushes.Transparent;
        _frame.BorderBrush = focused
            ? new SolidColorBrush(Color.FromArgb(0xE0, 0x00, 0x7A, 0xCC))
            : Brushes.Transparent;
        _frame.BorderThickness = focused ? new Thickness(1.5) : new Thickness(0);
        _dragHandle.IsVisible = focused;
        _deleteButton.IsVisible = focused;
        _resizeGrip.IsVisible = focused;
        // Read-only rather than disabled: a read-only TextBox still takes
        // focus, so clicking the text enters editing with the caret where
        // the click landed. Rebuilding the editor to switch modes would
        // throw that caret away.
        Editor.IsReadOnly = !focused;
    }

    private void AttachMoveBehavior()
    {
        var shell = Shell;
        var isMoving = false;
        var startPointer = default(Point);
        var startLeft = 0.0;
        var startTop = 0.0;

        _dragHandle.PointerPressed += (_, e) =>
        {
            isMoving = true;
            startPointer = e.GetPosition(_layer);
            startLeft = Canvas.GetLeft(shell);
            startTop = Canvas.GetTop(shell);
            if (double.IsNaN(startLeft)) startLeft = 0;
            if (double.IsNaN(startTop)) startTop = 0;
            e.Pointer.Capture(_dragHandle);
            e.Handled = true;
        };

        _dragHandle.PointerMoved += (_, e) =>
        {
            if (!isMoving)
                return;

            var current = e.GetPosition(_layer);
            var rect = _sink.NormalizeDipRect(new Rect(
                startLeft + current.X - startPointer.X,
                startTop + current.Y - startPointer.Y,
                shell.Width,
                shell.Height));

            Canvas.SetLeft(shell, rect.X);
            Canvas.SetTop(shell, rect.Y);
            e.Handled = true;
        };

        _dragHandle.PointerReleased += (_, e) =>
        {
            if (!isMoving)
                return;

            isMoving = false;
            e.Pointer.Capture(null);
            ReportBounds();
            e.Handled = true;
        };
    }

    private void AttachResizeBehavior()
    {
        var shell = Shell;
        var isResizing = false;
        var startPointer = default(Point);
        var startWidth = 0.0;
        var startHeight = 0.0;

        _resizeGrip.PointerPressed += (_, e) =>
        {
            isResizing = true;
            startPointer = e.GetPosition(_layer);
            startWidth = shell.Width;
            startHeight = shell.Height;
            e.Pointer.Capture(_resizeGrip);
            e.Handled = true;
        };

        _resizeGrip.PointerMoved += (_, e) =>
        {
            if (!isResizing)
                return;

            var current = e.GetPosition(_layer);
            var left = Canvas.GetLeft(shell);
            var top = Canvas.GetTop(shell);
            if (double.IsNaN(left)) left = 0;
            if (double.IsNaN(top)) top = 0;

            var rect = _sink.NormalizeDipRect(new Rect(
                left,
                top,
                Math.Max(MinimumWidthDips, startWidth + current.X - startPointer.X),
                Math.Max(MinimumHeightDips, startHeight + current.Y - startPointer.Y)));

            shell.Width = rect.Width;
            shell.Height = rect.Height;
            Canvas.SetLeft(shell, rect.X);
            Canvas.SetTop(shell, rect.Y);
            e.Handled = true;
        };

        _resizeGrip.PointerReleased += (_, e) =>
        {
            if (!isResizing)
                return;

            isResizing = false;
            e.Pointer.Capture(null);
            ReportBounds();
            e.Handled = true;
        };
    }

    private void ReportBounds()
    {
        var left = Canvas.GetLeft(Shell);
        var top = Canvas.GetTop(Shell);
        if (double.IsNaN(left)) left = 0;
        if (double.IsNaN(top)) top = 0;
        _sink.BoundsChanged(_operation, new Rect(left, top, Shell.Width, Shell.Height));
    }

    private static SolidColorBrush ToAvaloniaBrush(Excise.Core.Graphics.PdfColor color)
    {
        static byte Channel(double value) => (byte)Math.Clamp(Math.Round(value * 255), 0, 255);
        return new SolidColorBrush(Color.FromRgb(
            Channel(color.R),
            Channel(color.G),
            Channel(color.B)));
    }

    private static global::Avalonia.Media.TextAlignment ToAvaloniaTextAlignment(Excise.Core.Graphics.TextAlignment alignment) =>
        alignment switch
        {
            Excise.Core.Graphics.TextAlignment.Center => global::Avalonia.Media.TextAlignment.Center,
            Excise.Core.Graphics.TextAlignment.Right => global::Avalonia.Media.TextAlignment.Right,
            _ => global::Avalonia.Media.TextAlignment.Left,
        };
}
