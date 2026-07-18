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
            return $"{FormatNumber(Color.R)} g";
        }
        else
        {
            // Use 'rg' for RGB non-stroking color
            return $"{FormatNumber(Color.R)} {FormatNumber(Color.G)} {FormatNumber(Color.B)} rg";
        }
    }

    private static string FormatNumber(double value)
    {
        if (Math.Abs(value - Math.Round(value)) < 0.0001)
            return ((int)Math.Round(value)).ToString();
        return value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }
}
