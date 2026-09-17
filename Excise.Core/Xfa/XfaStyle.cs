using System.Globalization;
using System.Xml.Linq;
using Excise.Core.Graphics;

namespace Excise.Core.Xfa;

/// <summary>Colour parsing for XFA (<c>"r,g,b"</c>) and CSS in rich text.</summary>
internal static class XfaColor
{
    /// <summary>Parse an XFA <c>color value="r,g,b"</c> (0-255 each).</summary>
    public static bool TryParse(string? value, out PdfColor color)
    {
        color = PdfColor.Black;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 3)
            return false;
        var c = new double[3];
        for (int i = 0; i < 3; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                return false;
            c[i] = Math.Clamp(v, 0, 255) / 255.0;
        }
        color = new PdfColor(c[0], c[1], c[2]);
        return true;
    }

    /// <summary>Parse a CSS colour: <c>#rgb</c>, <c>#rrggbb</c>, or <c>rgb(r,g,b)</c>.</summary>
    public static bool TryParseCss(string? value, out PdfColor color)
    {
        color = PdfColor.Black;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var v = value.Trim();
        if (v.StartsWith('#'))
        {
            var hex = v[1..];
            if (hex.Length == 3)
                hex = string.Concat(hex.Select(ch => new string(ch, 2)));
            if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
                return false;
            color = PdfColor.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            return true;
        }
        if (v.StartsWith("rgb(", StringComparison.OrdinalIgnoreCase) && v.EndsWith(')'))
            return TryParse(v[4..^1], out color);
        return v.ToLowerInvariant() switch
        {
            "black" => Set(PdfColor.Black, out color),
            "white" => Set(PdfColor.White, out color),
            "red" => Set(PdfColor.Red, out color),
            "blue" => Set(PdfColor.Blue, out color),
            "green" => Set(new PdfColor(0, 128 / 255.0, 0), out color),
            "gray" or "grey" => Set(PdfColor.FromGray(128 / 255.0), out color),
            _ => false,
        };
    }

    private static bool Set(PdfColor value, out PdfColor color)
    {
        color = value;
        return true;
    }

    /// <summary>The colour of an element's <c>&lt;color&gt;</c> child, or <paramref name="fallback"/>.</summary>
    public static PdfColor Of(XElement? owner, PdfColor fallback)
        => owner?.Child("color") is { } c && TryParse(c.Attr("value"), out var parsed) ? parsed : fallback;
}

/// <summary>A resolved <c>&lt;font&gt;</c>.</summary>
internal sealed record XfaFontSpec(string Typeface, double Size, bool Bold, bool Italic, bool Underline, PdfColor Color)
{
    /// <summary>The XFA defaults: Courier, 10pt, black.</summary>
    public static readonly XfaFontSpec Default = new("Courier", 10, false, false, false, PdfColor.Black);

    public static XfaFontSpec From(XElement? font)
    {
        if (font == null)
            return Default;
        var size = font.Measure("size", "pt") ?? 10;
        return new XfaFontSpec(
            font.AttrOr("typeface", "Courier"),
            size > 0 ? Math.Min(size, 1000) : 10,
            font.AttrOr("weight", "normal") == "bold",
            font.AttrOr("posture", "normal") == "italic",
            font.IntAttr("underline", 0) > 0,
            XfaColor.Of(font.Child("fill"), PdfColor.Black));
    }

    /// <summary>A base-14 font for this spec at <paramref name="size"/> points.</summary>
    public PdfFont ToPdfFont(double? size = null)
        => new("F1", BaseFontName(Typeface, Bold, Italic), size ?? Size);

    public double LineHeight => Size * 1.2;

    /// <summary>
    /// Horizontal scale applied when this typeface is drawn with its base-14
    /// substitute. Myriad Pro, Designer's default face, is narrower than
    /// Helvetica: over the PDFium XFA corpus pdf.js (which carries Myriad
    /// metrics) measured the same strings at a median 0.904 of excise's
    /// Helvetica width. Without the scale, headings that fit in Acrobat and
    /// Firefox wrap here.
    /// </summary>
    public double WidthScale
        => Typeface.Contains("myriad", StringComparison.OrdinalIgnoreCase) ? 0.9 : 1.0;

    /// <summary>The drawn width of <paramref name="text"/>, including <see cref="WidthScale"/>.</summary>
    public double Width(string text) => ToPdfFont().MeasureWidth(text) * WidthScale;

    /// <summary>
    /// Map an XFA typeface to a base-14 family. Real forms name fonts excise
    /// does not embed (Myriad Pro, Minion Pro, Arial); the family class is the
    /// most that base-14 can honour.
    /// </summary>
    internal static string BaseFontName(string typeface, bool bold, bool italic)
    {
        var t = typeface.ToLowerInvariant();
        if (t.Contains("symbol", StringComparison.Ordinal))
            return PdfFont.StandardFonts.Symbol;
        if (t.Contains("dingbat", StringComparison.Ordinal) || t.Contains("wingding", StringComparison.Ordinal))
            return PdfFont.StandardFonts.ZapfDingbats;

        string family;
        if (t.Contains("courier", StringComparison.Ordinal) || t.Contains("mono", StringComparison.Ordinal)
            || t.Contains("consol", StringComparison.Ordinal) || t.Contains("ocr", StringComparison.Ordinal)
            || t.Contains("letter gothic", StringComparison.Ordinal))
        {
            family = "Courier";
        }
        else if (t.Contains("times", StringComparison.Ordinal) || t.Contains("minion", StringComparison.Ordinal)
            || t.Contains("garamond", StringComparison.Ordinal) || t.Contains("georgia", StringComparison.Ordinal)
            || t.Contains("palatino", StringComparison.Ordinal) || t.Contains("antiqua", StringComparison.Ordinal)
            || t.Contains("cambria", StringComparison.Ordinal) || t.Contains("baskerville", StringComparison.Ordinal)
            || t.Contains("century", StringComparison.Ordinal)
            || (t.Contains("serif", StringComparison.Ordinal) && !t.Contains("sans", StringComparison.Ordinal)))
        {
            family = "Times";
        }
        else
        {
            family = "Helvetica";
        }

        return family switch
        {
            "Courier" => bold && italic ? PdfFont.StandardFonts.CourierBoldOblique
                : bold ? PdfFont.StandardFonts.CourierBold
                : italic ? PdfFont.StandardFonts.CourierOblique
                : PdfFont.StandardFonts.Courier,
            "Times" => bold && italic ? PdfFont.StandardFonts.TimesBoldItalic
                : bold ? PdfFont.StandardFonts.TimesBold
                : italic ? PdfFont.StandardFonts.TimesItalic
                : PdfFont.StandardFonts.TimesRoman,
            _ => bold && italic ? PdfFont.StandardFonts.HelveticaBoldOblique
                : bold ? PdfFont.StandardFonts.HelveticaBold
                : italic ? PdfFont.StandardFonts.HelveticaOblique
                : PdfFont.StandardFonts.Helvetica,
        };
    }
}

/// <summary>A resolved <c>&lt;para&gt;</c>.</summary>
internal sealed record XfaParaSpec(
    string HAlign, string VAlign, double MarginLeft, double MarginRight,
    double SpaceAbove, double SpaceBelow, double TextIndent, double? LineHeight)
{
    public static readonly XfaParaSpec Default = new("left", "top", 0, 0, 0, 0, 0, null);

    public static XfaParaSpec From(XElement? para)
    {
        if (para == null)
            return Default;
        var lineHeight = para.Measure("lineHeight", "pt");
        return new XfaParaSpec(
            para.AttrOr("hAlign", "left"),
            para.AttrOr("vAlign", "top"),
            para.Measure("marginLeft", "pt") ?? 0,
            para.Measure("marginRight", "pt") ?? 0,
            para.Measure("spaceAbove", "pt") ?? 0,
            para.Measure("spaceBelow", "pt") ?? 0,
            para.Measure("textIndent", "pt") ?? 0,
            lineHeight > 0 ? lineHeight : null);
    }
}

/// <summary>Insets from a <c>&lt;margin&gt;</c>.</summary>
internal readonly record struct XfaInsets(double Left, double Top, double Right, double Bottom)
{
    public static readonly XfaInsets Zero = new(0, 0, 0, 0);

    public double Horizontal => Left + Right;

    public double Vertical => Top + Bottom;

    public static XfaInsets From(XElement? margin)
        => margin == null
            ? Zero
            : new XfaInsets(
                Math.Max(0, margin.Measure("leftInset") ?? 0),
                Math.Max(0, margin.Measure("topInset") ?? 0),
                Math.Max(0, margin.Measure("rightInset") ?? 0),
                Math.Max(0, margin.Measure("bottomInset") ?? 0));
}

/// <summary>One side of a border.</summary>
internal sealed record XfaEdgeSpec(bool Visible, double Thickness, PdfColor Color, string Stroke);

/// <summary>A resolved <c>&lt;border&gt;</c>: edges top, right, bottom, left.</summary>
internal sealed record XfaBorderSpec(IReadOnlyList<XfaEdgeSpec> Edges, double Radius, PdfColor? Fill, string Hand)
{
    /// <summary>The fill is a gradient, pattern or stipple, drawn as its base colour.</summary>
    public bool FillApproximated { get; init; }

    /// <summary>True when <paramref name="fill"/> paints more than a solid colour.</summary>
    public static bool IsApproximatedFill(XElement? fill)
        => fill?.Elements().Any(e => e.Name.LocalName is "linear" or "radial" or "pattern" or "stipple") == true;

    public bool HasVisibleEdge => Edges.Any(e => e.Visible && e.Thickness > 0);

    /// <summary>Null when the border is absent or not drawn at all.</summary>
    public static XfaBorderSpec? From(XElement? border)
    {
        if (border == null || border.Presence() != "visible")
            return null;

        var edges = border.ChildrenNamed("edge").Take(4).Select(EdgeFrom).ToList();
        var last = edges.Count > 0 ? edges[^1] : new XfaEdgeSpec(true, 0.5, PdfColor.Black, "solid");
        while (edges.Count < 4)
            edges.Add(last);

        double radius = 0;
        var corner = border.ChildrenNamed("corner").FirstOrDefault();
        if (corner != null && corner.AttrOr("join", "square") == "round")
            radius = Math.Max(0, corner.Measure("radius") ?? 0);

        PdfColor? fill = null;
        var fillElement = border.Child("fill");
        if (fillElement != null && fillElement.Presence() == "visible")
            fill = XfaColor.Of(fillElement, PdfColor.White);

        return new XfaBorderSpec(edges, radius, fill, border.AttrOr("hand", "even"))
        {
            FillApproximated = fill != null && IsApproximatedFill(fillElement),
        };
    }

    private static XfaEdgeSpec EdgeFrom(XElement edge)
        => new(
            edge.Presence() == "visible",
            Math.Clamp(edge.Measure("thickness") ?? 0.5, 0, 100),
            XfaColor.Of(edge, PdfColor.Black),
            edge.AttrOr("stroke", "solid"));
}
