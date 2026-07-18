using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Excise.Core.Document;
using Excise.Core.Editing;

namespace Excise.Avalonia.Controls;

public partial class PdfViewerControl
{
    private const double MinimumTypewriterWidthDips = 48;
    private const double MinimumTypewriterHeightDips = 24;
    private const double DefaultTypewriterWidthDips = 220;
    private const double DefaultTypewriterHeightDips = 42;

    private Rectangle? _tempTypewriterRect;

    private void RedrawTypewriterLayer()
    {
        if (_typewriterLayer == null)
            return;

        _typewriterLayer.Children.Clear();

        if (Document == null || TypewriterTextOperations == null)
            return;

        foreach (var operation in TypewriterTextOperations
                     .Where(o => o.IsPending && o.PageNumber == CurrentPage))
        {
            var rect = PdfRectToViewerDips(operation.Bounds, operation.PageNumber);
            var editor = CreateTypewriterEditor(operation, rect);

            Canvas.SetLeft(editor, rect.X);
            Canvas.SetTop(editor, rect.Y);
            _typewriterLayer.Children.Add(editor);
        }
    }

    private Control CreateTypewriterEditor(PdfTypewriterTextOperation operation, Rect rect)
    {
        var shell = new Grid
        {
            Width = Math.Max(rect.Width, MinimumTypewriterWidthDips),
            Height = Math.Max(rect.Height, MinimumTypewriterHeightDips),
            MinWidth = MinimumTypewriterWidthDips,
            MinHeight = MinimumTypewriterHeightDips,
            ClipToBounds = false,
            IsHitTestVisible = InteractionMode == InteractionMode.Typewriter,
        };
        shell.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        shell.RowDefinitions.Add(new RowDefinition(new GridLength(1, GridUnitType.Star)));

        var editing = InteractionMode == InteractionMode.Typewriter;
        var frame = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x0A, 0x00, 0x7A, 0xCC)),
            BorderBrush = editing
                ? new SolidColorBrush(Color.FromArgb(0xE0, 0x00, 0x7A, 0xCC))
                : Brushes.Transparent,
            BorderThickness = editing ? new Thickness(1.5) : new Thickness(0),
            CornerRadius = new CornerRadius(2),
        };
        Grid.SetRowSpan(frame, 2);
        shell.Children.Add(frame);

        var dragHandle = new Border
        {
            Height = 10,
            Background = editing
                ? new SolidColorBrush(Color.FromArgb(0x55, 0x00, 0x7A, 0xCC))
                : Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.SizeAll),
            IsVisible = editing,
        };
        Grid.SetRow(dragHandle, 0);
        shell.Children.Add(dragHandle);

        var textBox = new TextBox
        {
            Text = operation.Text,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 1, 4, 3),
            FontSize = Math.Max(8, operation.Style.FontSize * ViewerUnitsPerPoint),
            Foreground = ToAvaloniaBrush(operation.Style.Color),
            TextAlignment = ToAvaloniaTextAlignment(operation.Style.Alignment),
            VerticalContentAlignment = VerticalAlignment.Top,
            IsReadOnly = !editing,
        };
        textBox.TextChanged += (_, _) =>
        {
            TypewriterTextEdited?.Invoke(this,
                new TypewriterTextEditedEventArgs(
                    operation.Id,
                    textBox.Text ?? string.Empty,
                    operation.PageNumber));
        };
        Grid.SetRow(textBox, 1);
        shell.Children.Add(textBox);

        var deleteButton = new Button
        {
            Content = "x",
            Width = 20,
            Height = 18,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            IsVisible = editing,
        };
        ToolTip.SetTip(deleteButton, "Delete typewriter text");
        deleteButton.Click += (_, _) =>
        {
            TypewriterTextDeleted?.Invoke(this,
                new TypewriterTextDeletedEventArgs(operation.Id, operation.PageNumber));
        };
        Grid.SetRowSpan(deleteButton, 2);
        shell.Children.Add(deleteButton);

        var resizeGrip = new Border
        {
            Width = 12,
            Height = 12,
            Background = editing
                ? new SolidColorBrush(Color.FromArgb(0xCC, 0x00, 0x7A, 0xCC))
                : Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Cursor = new Cursor(StandardCursorType.TopLeftCorner),
            IsVisible = editing,
        };
        Grid.SetRowSpan(resizeGrip, 2);
        shell.Children.Add(resizeGrip);

        AttachTypewriterMoveBehavior(shell, dragHandle, operation);
        AttachTypewriterResizeBehavior(shell, resizeGrip, operation);

        if (editing && string.IsNullOrEmpty(operation.Text))
        {
            Dispatcher.UIThread.Post(() => textBox.Focus(), DispatcherPriority.Background);
        }

        return shell;
    }

    private void AttachTypewriterMoveBehavior(Control shell, Control dragHandle, PdfTypewriterTextOperation operation)
    {
        var isMoving = false;
        var startPointer = default(Point);
        var startLeft = 0.0;
        var startTop = 0.0;

        dragHandle.PointerPressed += (_, e) =>
        {
            if (_typewriterLayer == null)
                return;

            isMoving = true;
            startPointer = e.GetPosition(_typewriterLayer);
            startLeft = Canvas.GetLeft(shell);
            startTop = Canvas.GetTop(shell);
            if (double.IsNaN(startLeft)) startLeft = 0;
            if (double.IsNaN(startTop)) startTop = 0;
            e.Pointer.Capture(dragHandle);
            e.Handled = true;
        };

        dragHandle.PointerMoved += (_, e) =>
        {
            if (!isMoving || _typewriterLayer == null)
                return;

            var current = e.GetPosition(_typewriterLayer);
            var rect = NormalizeTypewriterDipRect(new Rect(
                startLeft + current.X - startPointer.X,
                startTop + current.Y - startPointer.Y,
                shell.Width,
                shell.Height));

            Canvas.SetLeft(shell, rect.X);
            Canvas.SetTop(shell, rect.Y);
            e.Handled = true;
        };

        dragHandle.PointerReleased += (_, e) =>
        {
            if (!isMoving)
                return;

            isMoving = false;
            e.Pointer.Capture(null);
            RaiseTypewriterBoundsChanged(shell, operation);
            e.Handled = true;
        };
    }

    private void AttachTypewriterResizeBehavior(Control shell, Control resizeGrip, PdfTypewriterTextOperation operation)
    {
        var isResizing = false;
        var startPointer = default(Point);
        var startWidth = 0.0;
        var startHeight = 0.0;

        resizeGrip.PointerPressed += (_, e) =>
        {
            if (_typewriterLayer == null)
                return;

            isResizing = true;
            startPointer = e.GetPosition(_typewriterLayer);
            startWidth = shell.Width;
            startHeight = shell.Height;
            e.Pointer.Capture(resizeGrip);
            e.Handled = true;
        };

        resizeGrip.PointerMoved += (_, e) =>
        {
            if (!isResizing || _typewriterLayer == null)
                return;

            var current = e.GetPosition(_typewriterLayer);
            var left = Canvas.GetLeft(shell);
            var top = Canvas.GetTop(shell);
            if (double.IsNaN(left)) left = 0;
            if (double.IsNaN(top)) top = 0;

            var rect = NormalizeTypewriterDipRect(new Rect(
                left,
                top,
                Math.Max(MinimumTypewriterWidthDips, startWidth + current.X - startPointer.X),
                Math.Max(MinimumTypewriterHeightDips, startHeight + current.Y - startPointer.Y)));

            shell.Width = rect.Width;
            shell.Height = rect.Height;
            Canvas.SetLeft(shell, rect.X);
            Canvas.SetTop(shell, rect.Y);
            e.Handled = true;
        };

        resizeGrip.PointerReleased += (_, e) =>
        {
            if (!isResizing)
                return;

            isResizing = false;
            e.Pointer.Capture(null);
            RaiseTypewriterBoundsChanged(shell, operation);
            e.Handled = true;
        };
    }

    private void RaiseTypewriterBoundsChanged(Control shell, PdfTypewriterTextOperation operation)
    {
        var left = Canvas.GetLeft(shell);
        var top = Canvas.GetTop(shell);
        if (double.IsNaN(left)) left = 0;
        if (double.IsNaN(top)) top = 0;

        var rect = NormalizeTypewriterDipRect(new Rect(left, top, shell.Width, shell.Height));
        TypewriterTextBoundsChanged?.Invoke(this,
            new TypewriterTextBoundsChangedEventArgs(
                operation.Id,
                ViewerDipsToPdfRect(rect, operation.PageNumber),
                operation.PageNumber));
    }

    private void DrawTemporaryTypewriterRectangle(Point start, Point end)
    {
        if (_interactionLayer == null)
            return;

        var rect = CreateRect(start, end);

        if (_tempTypewriterRect == null)
        {
            _tempTypewriterRect = new Rectangle
            {
                Fill = new SolidColorBrush(Color.FromArgb(0x24, 0x00, 0x7A, 0xCC)),
                Stroke = new SolidColorBrush(Color.FromArgb(0xEE, 0x00, 0x7A, 0xCC)),
                StrokeThickness = 1.5,
                StrokeDashArray = new global::Avalonia.Collections.AvaloniaList<double> { 4, 3 },
            };
            _interactionLayer.Children.Add(_tempTypewriterRect);
        }

        Canvas.SetLeft(_tempTypewriterRect, rect.X);
        Canvas.SetTop(_tempTypewriterRect, rect.Y);
        _tempTypewriterRect.Width = rect.Width;
        _tempTypewriterRect.Height = rect.Height;
        _tempTypewriterRect.IsVisible = true;
    }

    private bool IsTypewriterOverlayEvent(PointerEventArgs e)
    {
        if (_typewriterLayer == null || e.Source is not Control source)
            return false;

        for (Control? current = source; current != null; current = current.Parent as Control)
        {
            if (ReferenceEquals(current, _typewriterLayer))
                return true;
        }

        return false;
    }

    private Rect NormalizeTypewriterDipRect(Rect rect)
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

    private PdfRectangle ViewerDipsToPdfRect(Rect dipRect, int pageNumber)
    {
        if (Document == null || pageNumber < 1 || pageNumber > Document.PageCount)
            return new PdfRectangle(0, 0, 0, 0);

        var page = Document.GetPage(pageNumber);
        return PdfCoordinateMapper
            .ToContentPoints(page, ViewerDipsRect(dipRect, pageNumber))
            .ToPdfRectangle()
            .Normalize();
    }

    private Rect PdfRectToViewerDips(PdfRectangle pdfRect, int pageNumber)
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
