namespace Excise.Core.Graphics;

/// <summary>
/// Represents a pen for stroking paths in PDF graphics.
/// </summary>
public class PdfPen
{
    /// <summary>
    /// The stroke color.
    /// </summary>
    public PdfColor Color { get; }

    /// <summary>
    /// The line width in points.
    /// </summary>
    public double Width { get; }

    /// <summary>
    /// Creates a pen with the specified color and width.
    /// </summary>
    public PdfPen(PdfColor color, double width = 1)
    {
        Color = color;
        Width = Math.Max(0, width);
    }

    // Standard pens
    public static readonly PdfPen Black = new(PdfColor.Black);
    public static readonly PdfPen White = new(PdfColor.White);
    public static readonly PdfPen Red = new(PdfColor.Red);

    /// <summary>
    /// Generates PDF operators to set this pen as stroke color.
    /// </summary>
    internal string GetStrokeColorOperator()
    {
        if (Color.IsGrayscale)
        {
            // Use 'G' for grayscale stroking color
            return $"{FormatNumber(Color.R)} G";
        }
        else
        {
            // Use 'RG' for RGB stroking color
            return $"{FormatNumber(Color.R)} {FormatNumber(Color.G)} {FormatNumber(Color.B)} RG";
        }
    }

    /// <summary>
    /// Generates PDF operator to set line width.
    /// </summary>
    internal string GetLineWidthOperator()
    {
        return $"{FormatNumber(Width)} w";
    }

    private static string FormatNumber(double value)
    {
        if (Math.Abs(value - Math.Round(value)) < 0.0001)
            return ((int)Math.Round(value)).ToString();
        return value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }
}
