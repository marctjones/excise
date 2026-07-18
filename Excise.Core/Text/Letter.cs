using Excise.Core.Document;

namespace Excise.Core.Text;

/// <summary>
/// Represents a single letter (glyph) extracted from a PDF page.
/// Contains the character value, position, font information, and bounding box.
/// </summary>
public class Letter
{
    /// <summary>
    /// The Unicode character(s) represented by this glyph.
    /// May be more than one character for ligatures.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// The bounding box of the glyph in PDF coordinates (origin at bottom-left).
    /// </summary>
    public PdfRectangle GlyphRectangle { get; }

    /// <summary>
    /// The font size in points.
    /// </summary>
    public double FontSize { get; }

    /// <summary>
    /// The name of the font used to render this glyph.
    /// </summary>
    public string FontName { get; }

    /// <summary>
    /// The X coordinate of the glyph start position.
    /// </summary>
    public double StartX { get; }

    /// <summary>
    /// The Y coordinate of the glyph baseline.
    /// </summary>
    public double StartY { get; }

    /// <summary>
    /// The width of the glyph in text space units.
    /// </summary>
    public double Width { get; }

    /// <summary>
    /// The character code in the font's encoding (before Unicode mapping).
    /// </summary>
    public int CharacterCode { get; }

    /// <summary>
    /// Number of bytes the source code occupies in the content stream: 1 for
    /// simple fonts and for Type0/CID fonts with a 1-byte codespace (#659),
    /// 2 for Type0/CID fonts using Identity-H/V (the common CJK case) or any
    /// other 2-byte codespace.
    /// </summary>
    public int CodeByteLength { get; }

    /// <summary>
    /// Whether this letter came from a Type0/composite (CID-keyed) font, of
    /// ANY byte-code stride. Used by redaction to re-encode kept glyphs with
    /// their original CID bytes instead of Unicode, which a CID font cannot
    /// render (issue #353). Deliberately independent of
    /// <see cref="CodeByteLength"/>: before #659, "is this a CID font" and
    /// "is the code 2 bytes" were the same test, because every real-world
    /// Type0 font used Identity-H/V's 2-byte codespace. #659 broke that
    /// correlation — a Type0 font can legally use a 1-byte codespace — so a
    /// 1-byte CID-keyed glyph still needs its original byte preserved on
    /// reconstruction, exactly like a 2-byte one.
    /// </summary>
    public bool IsCidFont { get; set; }

    /// <summary>
    /// Whether this letter was rendered inside an Optional Content Group (OCG)
    /// that is OFF by default (hidden). This is a security concern: while invisible
    /// in the default viewer, the text is fully extractable via the structure tree
    /// or by toggling the OCG on in other tools. When true, redaction operations
    /// should include this letter to prevent recovery of "hidden" content.
    /// Defaults to false (not in a hidden OCG).
    /// </summary>
    public bool IsInHiddenOptionalContent { get; set; }

    /// <summary>
    /// The baseline start point of the glyph.
    /// </summary>
    public PdfPoint StartBaseLine => new(StartX, StartY);

    /// <summary>
    /// The baseline end point of the glyph.
    /// </summary>
    public PdfPoint EndBaseLine => new(StartX + Width, StartY);

    public Letter(
        string value,
        PdfRectangle glyphRectangle,
        double fontSize,
        string fontName,
        double startX,
        double startY,
        double width,
        int characterCode,
        int codeByteLength = 1)
    {
        Value = value;
        GlyphRectangle = glyphRectangle;
        FontSize = fontSize;
        FontName = fontName;
        StartX = startX;
        StartY = startY;
        Width = width;
        CharacterCode = characterCode;
        CodeByteLength = codeByteLength < 1 ? 1 : codeByteLength;
    }

    public override string ToString() => $"'{Value}' at ({GlyphRectangle.Left:F1}, {GlyphRectangle.Bottom:F1})";
}
