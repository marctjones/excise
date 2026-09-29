using System;
using System.Collections.Generic;
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

    private Rectangle? _tempTypewriterRect;


    private void DrawTemporaryTypewriterRectangle(Point start, Point end)
    {
        if (InteractionLayer == null)
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
            InteractionLayer.Children.Add(_tempTypewriterRect);
        }

        Canvas.SetLeft(_tempTypewriterRect, rect.X);
        Canvas.SetTop(_tempTypewriterRect, rect.Y);
        _tempTypewriterRect.Width = rect.Width;
        _tempTypewriterRect.Height = rect.Height;
        _tempTypewriterRect.IsVisible = true;
    }

}
