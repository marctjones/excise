using Pdfe.Core.Graphics;

namespace Pdfe.Core.Authoring;

/// <summary>
/// One of the three base-14 font families available without embedding.
/// (Unicode / embedded TrueType-OpenType fonts are tracked by issue #378.)
/// </summary>
public enum FontFamily
{
    /// <summary>Sans-serif (Helvetica).</summary>
    SansSerif,
    /// <summary>Serif (Times).</summary>
    Serif,
    /// <summary>Monospace (Courier).</summary>
    Monospace
}

/// <summary>
/// The look of a run of text: family, size, weight/slant, color, alignment,
/// line spacing, and the gap left after the block. Immutable — the
/// <c>With…</c> helpers return a modified copy, so styles compose cleanly.
/// </summary>
public sealed record TextStyle
{
    /// <summary>Font family. Default <see cref="FontFamily.SansSerif"/>.</summary>
    public FontFamily Family { get; init; } = FontFamily.SansSerif;

    /// <summary>Font size in points. Default 11.</summary>
    public double Size { get; init; } = 11;

    /// <summary>Bold weight. Default false.</summary>
    public bool Bold { get; init; }

    /// <summary>Italic/oblique slant. Default false.</summary>
    public bool Italic { get; init; }

    /// <summary>Text color. Default black.</summary>
    public PdfColor Color { get; init; } = PdfColor.Black;

    /// <summary>Horizontal alignment within the content column. Default left.</summary>
    public TextAlignment Alignment { get; init; } = TextAlignment.Left;

    /// <summary>Line height as a multiple of the font size. Default 1.2.</summary>
    public double LineSpacing { get; init; } = 1.2;

    /// <summary>Vertical gap left after the block, in points. Default 6.</summary>
    public double SpaceAfter { get; init; } = 6;

    /// <summary>
    /// An explicit font (e.g. an embedded Unicode font from
    /// <see cref="PdfFont.FromFile"/>). When set it overrides
    /// <see cref="Family"/>/<see cref="Bold"/>/<see cref="Italic"/>, and the
    /// style's <see cref="Size"/> is applied to it. Null = the base-14 family.
    /// </summary>
    public PdfFont? Font { get; init; }

    /// <summary>The default body style (11-pt Helvetica, left, black).</summary>
    public static TextStyle Body => new();

    /// <summary>Returns a copy with the given size.</summary>
    public TextStyle WithSize(double size) => this with { Size = size };

    /// <summary>Returns a bold copy.</summary>
    public TextStyle AsBold() => this with { Bold = true };

    /// <summary>Returns an italic copy.</summary>
    public TextStyle AsItalic() => this with { Italic = true };

    /// <summary>Returns a copy with the given color.</summary>
    public TextStyle WithColor(PdfColor color) => this with { Color = color };

    /// <summary>Returns a copy with the given alignment.</summary>
    public TextStyle WithAlignment(TextAlignment alignment) => this with { Alignment = alignment };

    /// <summary>Returns a copy with the given trailing gap (points).</summary>
    public TextStyle WithSpaceAfter(double points) => this with { SpaceAfter = points };

    /// <summary>
    /// Returns a copy that draws with <paramref name="font"/> (e.g. an embedded
    /// Unicode font from <see cref="PdfFont.FromFile"/>) instead of a base-14 family.
    /// </summary>
    public TextStyle WithFont(PdfFont font) => this with { Font = font };

    /// <summary>Resolves this style to a concrete <see cref="PdfFont"/> at its size.</summary>
    public PdfFont ResolveFont() => Font is not null ? Font.WithSize(Size) : new("F1", ResolveBaseFont(), Size);

    /// <summary>The brush matching this style's color.</summary>
    public PdfBrush ResolveBrush() => new(Color);

    /// <summary>Line height in points (Size × LineSpacing).</summary>
    public double LineHeight => Size * LineSpacing;

    private string ResolveBaseFont() => Family switch
    {
        FontFamily.Serif => (Bold, Italic) switch
        {
            (true, true) => PdfFont.StandardFonts.TimesBoldItalic,
            (true, false) => PdfFont.StandardFonts.TimesBold,
            (false, true) => PdfFont.StandardFonts.TimesItalic,
            _ => PdfFont.StandardFonts.TimesRoman
        },
        FontFamily.Monospace => (Bold, Italic) switch
        {
            (true, true) => PdfFont.StandardFonts.CourierBoldOblique,
            (true, false) => PdfFont.StandardFonts.CourierBold,
            (false, true) => PdfFont.StandardFonts.CourierOblique,
            _ => PdfFont.StandardFonts.Courier
        },
        _ => (Bold, Italic) switch
        {
            (true, true) => PdfFont.StandardFonts.HelveticaBoldOblique,
            (true, false) => PdfFont.StandardFonts.HelveticaBold,
            (false, true) => PdfFont.StandardFonts.HelveticaOblique,
            _ => PdfFont.StandardFonts.Helvetica
        }
    };
}
