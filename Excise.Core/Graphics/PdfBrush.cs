using Excise.Core.Primitives;

namespace Excise.Core.Graphics;

/// <summary>
/// Represents a brush for filling shapes in PDF graphics.
/// </summary>
public class PdfBrush
{
    /// <summary>
    /// The fill color.
    /// </summary>
    public PdfColor Color { get; }

    private readonly double _opacity = 1;

    /// <summary>
    /// Fill opacity from 0 (transparent) to 1 (opaque, the default), clamped to that
    /// range; applies to shapes and text painted with this brush. Written as the
    /// <c>/ca</c> entry of a page ExtGState resource.
    /// </summary>
    public double Opacity
    {
        get => _opacity;
        init => _opacity = double.IsNaN(value)
            ? throw new ArgumentOutOfRangeException(nameof(Opacity), value, null)
            : Math.Clamp(value, 0, 1);
    }

    /// <summary>
    /// Creates a brush with the specified color.
    /// </summary>
    public PdfBrush(PdfColor color)
    {
        Color = color;
    }

    // Standard brushes
    public static readonly PdfBrush Black = new(PdfColor.Black);
    public static readonly PdfBrush White = new(PdfColor.White);
    public static readonly PdfBrush Red = new(PdfColor.Red);
    public static readonly PdfBrush Green = new(PdfColor.Green);
    public static readonly PdfBrush Blue = new(PdfColor.Blue);

    /// <summary>
    /// Generates PDF operators to set this brush as fill color.
    /// </summary>
    internal string GetFillColorOperator()
    {
        if (Color.IsGrayscale)
        {
            // Use 'g' for grayscale non-stroking color
            return $"{PdfNumberFormatter.Format(Color.R)} g";
        }
        else
        {
            // Use 'rg' for RGB non-stroking color
            return $"{PdfNumberFormatter.Format(Color.R)} {PdfNumberFormatter.Format(Color.G)} {PdfNumberFormatter.Format(Color.B)} rg";
        }
    }
}
