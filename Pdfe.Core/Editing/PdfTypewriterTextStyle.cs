using Pdfe.Core.Graphics;

namespace Pdfe.Core.Editing;

/// <summary>
/// Visual style for text placed with the typewriter tool.
/// </summary>
public sealed record PdfTypewriterTextStyle
{
    public static PdfTypewriterTextStyle Default { get; } = new();

    public PdfTypewriterTextStyle(
        string fontName = PdfFont.StandardFonts.Helvetica,
        double fontSize = 12,
        PdfColor? color = null,
        TextAlignment alignment = TextAlignment.Left,
        double lineSpacing = 1.2)
    {
        if (string.IsNullOrWhiteSpace(fontName))
            throw new ArgumentException("Font name must not be empty.", nameof(fontName));
        if (fontSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(fontSize), "Font size must be positive.");
        if (lineSpacing <= 0)
            throw new ArgumentOutOfRangeException(nameof(lineSpacing), "Line spacing must be positive.");

        FontName = fontName;
        FontSize = fontSize;
        Color = color ?? PdfColor.Black;
        Alignment = alignment;
        LineSpacing = lineSpacing;
    }

    public string FontName { get; }
    public double FontSize { get; }
    public PdfColor Color { get; }
    public TextAlignment Alignment { get; }
    public double LineSpacing { get; }

    internal PdfFont CreateFont() => new("F1", FontName, FontSize);
    internal PdfBrush CreateBrush() => new(Color);
}
