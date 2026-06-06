using System.Text;
using Pdfe.Core.Document;
using Pdfe.Core.Primitives;

namespace Pdfe.Core.Graphics;

/// <summary>
/// Represents a font for drawing text in PDF graphics.
/// </summary>
public class PdfFont
{
    /// <summary>
    /// The font resource name (e.g., "F1", "F2").
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// The base font name (e.g., "Helvetica", "Times-Roman").
    /// </summary>
    public string BaseFont { get; }

    /// <summary>
    /// The font size in points.
    /// </summary>
    public double Size { get; }

    /// <summary>
    /// Whether this is a standard PDF font.
    /// </summary>
    public bool IsStandard14 { get; }

    // Font metrics for standard fonts (approximate widths in 1000 units per em)
    private readonly Dictionary<char, int> _widths;
    private readonly int _defaultWidth;

    /// <summary>
    /// Creates a font with the specified parameters.
    /// </summary>
    public PdfFont(string name, string baseFont, double size)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        BaseFont = baseFont ?? throw new ArgumentNullException(nameof(baseFont));
        Size = size > 0 ? size : throw new ArgumentOutOfRangeException(nameof(size), "Font size must be positive");
        IsStandard14 = IsStandard14Font(baseFont);
        _widths = GetStandardFontWidths(baseFont);
        _defaultWidth = GetDefaultWidth(baseFont);
    }

    /// <summary>
    /// Standard 14 PDF fonts that are always available.
    /// </summary>
    public static class StandardFonts
    {
        public const string Helvetica = "Helvetica";
        public const string HelveticaBold = "Helvetica-Bold";
        public const string HelveticaOblique = "Helvetica-Oblique";
        public const string HelveticaBoldOblique = "Helvetica-BoldOblique";
        public const string TimesRoman = "Times-Roman";
        public const string TimesBold = "Times-Bold";
        public const string TimesItalic = "Times-Italic";
        public const string TimesBoldItalic = "Times-BoldItalic";
        public const string Courier = "Courier";
        public const string CourierBold = "Courier-Bold";
        public const string CourierOblique = "Courier-Oblique";
        public const string CourierBoldOblique = "Courier-BoldOblique";
        public const string Symbol = "Symbol";
        public const string ZapfDingbats = "ZapfDingbats";
    }

    /// <summary>
    /// Create a Helvetica font with the specified size.
    /// </summary>
    public static PdfFont Helvetica(double size) => new("F1", StandardFonts.Helvetica, size);

    /// <summary>
    /// Create a Helvetica Bold font with the specified size.
    /// </summary>
    public static PdfFont HelveticaBold(double size) => new("F1", StandardFonts.HelveticaBold, size);

    /// <summary>
    /// Create a Times Roman font with the specified size.
    /// </summary>
    public static PdfFont TimesRoman(double size) => new("F1", StandardFonts.TimesRoman, size);

    /// <summary>
    /// Create a Courier font with the specified size.
    /// </summary>
    public static PdfFont Courier(double size) => new("F1", StandardFonts.Courier, size);

    /// <summary>
    /// Create a Times Bold font with the specified size.
    /// </summary>
    public static PdfFont TimesBold(double size) => new("F1", StandardFonts.TimesBold, size);

    /// <summary>
    /// Create a Times Italic font with the specified size.
    /// </summary>
    public static PdfFont TimesItalic(double size) => new("F1", StandardFonts.TimesItalic, size);

    /// <summary>
    /// Create a Courier Bold font with the specified size.
    /// </summary>
    public static PdfFont CourierBold(double size) => new("F1", StandardFonts.CourierBold, size);

    /// <summary>
    /// Create a Courier Oblique font with the specified size.
    /// </summary>
    public static PdfFont CourierOblique(double size) => new("F1", StandardFonts.CourierOblique, size);

    /// <summary>
    /// Create a Helvetica Oblique font with the specified size.
    /// </summary>
    public static PdfFont HelveticaOblique(double size) => new("F1", StandardFonts.HelveticaOblique, size);

    /// <summary>
    /// Create a new font with a different size.
    /// </summary>
    public PdfFont WithSize(double size) => new(Name, BaseFont, size);

    /// <summary>
    /// Create a new font with a different resource name.
    /// </summary>
    public PdfFont WithName(string name) => new(name, BaseFont, Size);

    /// <summary>
    /// Create an embedded font from a TrueType/OpenType file on disk. The font
    /// is embedded (and the text encoded) so arbitrary Unicode renders and stays
    /// extractable. See <see cref="FromTrueType(byte[], double)"/>.
    /// </summary>
    public static PdfFont FromFile(string path, double size) =>
        FromTrueType(File.ReadAllBytes(path), size);

    /// <summary>
    /// Create an embedded font from TrueType/OpenType font bytes. Produces a
    /// Type0/Identity-H font with a ToUnicode CMap; <see cref="EncodeString"/>
    /// emits glyph ids and the whole font is embedded on first use.
    /// (Subsetting is a future optimization — see #378.)
    /// </summary>
    public static PdfFont FromTrueType(byte[] fontData, double size) =>
        new PdfTrueTypeFont(fontData, size);

    /// <summary>
    /// Create an embedded font from a TrueType/OpenType stream.
    /// </summary>
    public static PdfFont FromTrueType(Stream fontStream, double size)
    {
        ArgumentNullException.ThrowIfNull(fontStream);
        using var ms = new MemoryStream();
        fontStream.CopyTo(ms);
        return FromTrueType(ms.ToArray(), size);
    }

    /// <summary>
    /// Measures the width of a string in points.
    /// </summary>
    public virtual double MeasureWidth(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        double totalWidth = 0;
        foreach (var c in text)
        {
            totalWidth += GetCharWidth(c);
        }

        // Convert from 1000 units per em to points
        return totalWidth * Size / 1000.0;
    }

    /// <summary>
    /// Gets the approximate line height (ascender + descender).
    /// </summary>
    public virtual double LineHeight => Size * 1.2; // Typical line height is ~1.2x font size

    /// <summary>
    /// Gets the ascender height (distance from baseline to top).
    /// </summary>
    public virtual double Ascender => Size * 0.8; // Typical ascender is ~0.8x font size

    /// <summary>
    /// Gets the descender depth (distance from baseline to bottom).
    /// </summary>
    public virtual double Descender => Size * 0.2; // Typical descender is ~0.2x font size

    /// <summary>
    /// Build the font dictionary, registering any required indirect objects in
    /// <paramref name="document"/> (embedded fonts add stream objects). The base
    /// implementation returns the inline standard-font dictionary.
    /// </summary>
    internal virtual PdfDictionary BuildFontDictionary(PdfDocument document) => CreateFontDictionary();

    /// <summary>
    /// Whether the font dictionary should be stored as an indirect object in the
    /// page resources rather than inline. Embedded composite fonts set this so
    /// the font dict has its own object id (better tool compatibility); standard
    /// fonts stay inline.
    /// </summary>
    internal virtual bool PreferIndirectFontDictionary => false;

    /// <summary>
    /// Creates a PDF font dictionary for embedding in resources.
    /// </summary>
    public PdfDictionary CreateFontDictionary()
    {
        var dict = new PdfDictionary();
        dict["Type"] = new PdfName("Font");
        dict["Subtype"] = new PdfName("Type1");
        dict["BaseFont"] = new PdfName(BaseFont);

        // Use WinAnsiEncoding for standard fonts
        if (IsStandard14 && BaseFont != StandardFonts.Symbol && BaseFont != StandardFonts.ZapfDingbats)
        {
            dict["Encoding"] = new PdfName("WinAnsiEncoding");
        }

        return dict;
    }

    /// <summary>
    /// Encodes a string for use in PDF text operators.
    /// </summary>
    public virtual string EncodeString(string text)
    {
        if (string.IsNullOrEmpty(text))
            return "()";

        var sb = new StringBuilder();
        sb.Append('(');

        foreach (var c in text)
        {
            // Escape special characters
            switch (c)
            {
                case '(':
                    sb.Append("\\(");
                    break;
                case ')':
                    sb.Append("\\)");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    if (c < 32 || c > 126)
                    {
                        // Use octal for non-printable characters
                        sb.Append($"\\{(int)c:000}");
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }

        sb.Append(')');
        return sb.ToString();
    }

    private int GetCharWidth(char c)
    {
        return _widths.TryGetValue(c, out var width) ? width : _defaultWidth;
    }

    private static bool IsStandard14Font(string baseFont)
    {
        return baseFont switch
        {
            StandardFonts.Helvetica or
            StandardFonts.HelveticaBold or
            StandardFonts.HelveticaOblique or
            StandardFonts.HelveticaBoldOblique or
            StandardFonts.TimesRoman or
            StandardFonts.TimesBold or
            StandardFonts.TimesItalic or
            StandardFonts.TimesBoldItalic or
            StandardFonts.Courier or
            StandardFonts.CourierBold or
            StandardFonts.CourierOblique or
            StandardFonts.CourierBoldOblique or
            StandardFonts.Symbol or
            StandardFonts.ZapfDingbats => true,
            _ => false
        };
    }

    private static int GetDefaultWidth(string baseFont)
    {
        // Courier is monospace - all characters are 600 units wide
        if (baseFont.StartsWith("Courier"))
            return 600;

        // Default width for proportional fonts
        return 500;
    }

    private static Dictionary<char, int> GetStandardFontWidths(string baseFont)
    {
        // Simplified character widths for standard fonts (in 1000 units per em)
        // These are approximate values based on typical font metrics

        if (baseFont.StartsWith("Courier"))
        {
            // Courier is monospace - all characters same width
            var courier = new Dictionary<char, int>();
            for (int i = 32; i < 127; i++)
                courier[(char)i] = 600;
            return courier;
        }

        // Helvetica-like widths (approximate)
        var widths = new Dictionary<char, int>
        {
            [' '] = 278,
            ['!'] = 278,
            ['"'] = 355,
            ['#'] = 556,
            ['$'] = 556,
            ['%'] = 889,
            ['&'] = 667,
            ['\''] = 191,
            ['('] = 333,
            [')'] = 333,
            ['*'] = 389,
            ['+'] = 584,
            [','] = 278,
            ['-'] = 333,
            ['.'] = 278,
            ['/'] = 278,
            ['0'] = 556,
            ['1'] = 556,
            ['2'] = 556,
            ['3'] = 556,
            ['4'] = 556,
            ['5'] = 556,
            ['6'] = 556,
            ['7'] = 556,
            ['8'] = 556,
            ['9'] = 556,
            [':'] = 278,
            [';'] = 278,
            ['<'] = 584,
            ['='] = 584,
            ['>'] = 584,
            ['?'] = 556,
            ['@'] = 1015,
            ['A'] = 667,
            ['B'] = 667,
            ['C'] = 722,
            ['D'] = 722,
            ['E'] = 667,
            ['F'] = 611,
            ['G'] = 778,
            ['H'] = 722,
            ['I'] = 278,
            ['J'] = 500,
            ['K'] = 667,
            ['L'] = 556,
            ['M'] = 833,
            ['N'] = 722,
            ['O'] = 778,
            ['P'] = 667,
            ['Q'] = 778,
            ['R'] = 722,
            ['S'] = 667,
            ['T'] = 611,
            ['U'] = 722,
            ['V'] = 667,
            ['W'] = 944,
            ['X'] = 667,
            ['Y'] = 667,
            ['Z'] = 611,
            ['['] = 278,
            ['\\'] = 278,
            [']'] = 278,
            ['^'] = 469,
            ['_'] = 556,
            ['`'] = 333,
            ['a'] = 556,
            ['b'] = 556,
            ['c'] = 500,
            ['d'] = 556,
            ['e'] = 556,
            ['f'] = 278,
            ['g'] = 556,
            ['h'] = 556,
            ['i'] = 222,
            ['j'] = 222,
            ['k'] = 500,
            ['l'] = 222,
            ['m'] = 833,
            ['n'] = 556,
            ['o'] = 556,
            ['p'] = 556,
            ['q'] = 556,
            ['r'] = 333,
            ['s'] = 500,
            ['t'] = 278,
            ['u'] = 556,
            ['v'] = 500,
            ['w'] = 722,
            ['x'] = 500,
            ['y'] = 500,
            ['z'] = 500,
            ['{'] = 334,
            ['|'] = 260,
            ['}'] = 334,
            ['~'] = 584
        };

        // Times has slightly different widths, but use Helvetica as approximation
        // for simplicity. A full implementation would have separate tables.

        return widths;
    }

    /// <inheritdoc />
    public override string ToString() => $"{BaseFont} {Size}pt";
}
