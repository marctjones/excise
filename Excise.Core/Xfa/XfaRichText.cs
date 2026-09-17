using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace Excise.Core.Xfa;

/// <summary>One run of styled text inside a rich-text paragraph.</summary>
internal sealed record XfaTextRun(string Text, XfaFontSpec Font);

/// <summary>A paragraph of runs, with its own alignment.</summary>
internal sealed record XfaParagraph(IReadOnlyList<XfaTextRun> Runs, string? HAlign, double SpaceBefore, double SpaceAfter);

/// <summary>
/// XFA rich text (an XHTML subset in <c>exData</c>) reduced to paragraphs of
/// styled runs. Only the CSS the layout can honour is read: font family, size,
/// weight, style, colour and text alignment, plus margins between paragraphs.
/// </summary>
internal static class XfaRichText
{
    private const string XhtmlNamespace = "http://www.w3.org/1999/xhtml";

    /// <summary>The XHTML <c>body</c> (or first XHTML element) under <paramref name="container"/>.</summary>
    public static XElement? FindXhtmlBody(XElement container)
    {
        foreach (var element in container.Elements())
        {
            if (element.Name.NamespaceName == XhtmlNamespace)
                return element;
        }
        return null;
    }

    /// <summary>The body's text with paragraph breaks as newlines.</summary>
    public static string PlainText(XElement body)
    {
        var sb = new StringBuilder();
        foreach (var paragraph in Paragraphs(body, XfaFontSpec.Default, null))
        {
            if (sb.Length > 0)
                sb.Append('\n');
            foreach (var run in paragraph.Runs)
                sb.Append(run.Text);
        }
        return sb.ToString();
    }

    /// <summary>Split the body into paragraphs, inheriting <paramref name="baseFont"/>.</summary>
    public static List<XfaParagraph> Paragraphs(XElement body, XfaFontSpec baseFont, XfaBudget? budget)
    {
        var paragraphs = new List<XfaParagraph>();
        var runs = new List<XfaTextRun>();
        string? align = null;
        double spaceBefore = 0, spaceAfter = 0;

        void Flush()
        {
            paragraphs.Add(new XfaParagraph(runs.ToList(), align, spaceBefore, spaceAfter));
            runs.Clear();
            align = null;
            spaceBefore = spaceAfter = 0;
        }

        void Walk(XElement element, XfaFontSpec font, int depth)
        {
            budget?.Tick();
            XfaBudget.CheckDepth(depth);

            var style = ParseStyle(element.Attribute("style")?.Value);
            var local = element.Name.LocalName;
            if (local is "b" or "strong")
                font = font with { Bold = true };
            if (local is "i" or "em")
                font = font with { Italic = true };
            if (local == "u")
                font = font with { Underline = true };
            font = ApplyStyle(font, style);

            bool block = local is "p" or "div" or "li" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6";
            if (block)
            {
                if (runs.Count > 0)
                    Flush();
                if (style.TryGetValue("text-align", out var ta))
                    align = ta;
                spaceBefore = Length(style, "margin-top") ?? Length(style, "space-before") ?? 0;
                spaceAfter = Length(style, "margin-bottom") ?? Length(style, "space-after") ?? 0;
            }

            foreach (var node in element.Nodes())
            {
                switch (node)
                {
                    case XText text:
                        var value = Collapse(text.Value);
                        // Whitespace between block elements is markup
                        // indentation, not content.
                        if (value.Length > 0 && !(runs.Count == 0 && value == " "))
                            runs.Add(new XfaTextRun(value, font));
                        break;
                    case XElement child when child.Name.LocalName == "br":
                        Flush();
                        break;
                    case XElement child:
                        Walk(child, font, depth + 1);
                        break;
                }
            }

            if (block)
                Flush();
        }

        Walk(body, ApplyStyle(baseFont, ParseStyle(body.Attribute("style")?.Value)), 0);
        if (runs.Count > 0)
            Flush();

        // Trim leading/trailing whitespace of each paragraph; drop empty ones
        // at the end (a trailing block leaves one).
        var result = new List<XfaParagraph>();
        foreach (var p in paragraphs)
        {
            var trimmed = TrimRuns(p.Runs);
            result.Add(p with { Runs = trimmed });
        }
        while (result.Count > 0 && result[^1].Runs.Count == 0)
            result.RemoveAt(result.Count - 1);
        return result;
    }

    private static List<XfaTextRun> TrimRuns(IReadOnlyList<XfaTextRun> runs)
    {
        var list = runs.ToList();
        if (list.Count > 0)
            list[0] = list[0] with { Text = list[0].Text.TrimStart() };
        if (list.Count > 0)
            list[^1] = list[^1] with { Text = list[^1].Text.TrimEnd() };
        list.RemoveAll(r => r.Text.Length == 0);
        return list;
    }

    private static string Collapse(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool space = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) && c != ' ')
            {
                if (!space)
                    sb.Append(' ');
                space = true;
            }
            else
            {
                sb.Append(c);
                space = false;
            }
        }
        return sb.ToString();
    }

    private static Dictionary<string, string> ParseStyle(string? style)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(style))
            return result;
        foreach (var declaration in style.Split(';'))
        {
            var colon = declaration.IndexOf(':');
            if (colon <= 0)
                continue;
            var key = declaration[..colon].Trim();
            var value = declaration[(colon + 1)..].Trim();
            if (key.Length > 0 && value.Length > 0)
                result[key] = value;
        }
        return result;
    }

    private static XfaFontSpec ApplyStyle(XfaFontSpec font, Dictionary<string, string> style)
    {
        if (style.TryGetValue("font-family", out var family))
            font = font with { Typeface = family.Split(',')[0].Trim().Trim('\'', '"') };
        if (Length(style, "font-size") is { } size && size > 0)
            font = font with { Size = size };
        if (style.TryGetValue("font-weight", out var weight))
            font = font with { Bold = weight is "bold" or "bolder" || (int.TryParse(weight, out var w) && w >= 600) };
        if (style.TryGetValue("font-style", out var fontStyle))
            font = font with { Italic = fontStyle is "italic" or "oblique" };
        if (style.TryGetValue("text-decoration", out var decoration))
            font = font with { Underline = decoration.Contains("underline", StringComparison.OrdinalIgnoreCase) };
        if (style.TryGetValue("color", out var color) && XfaColor.TryParseCss(color, out var parsed))
            font = font with { Color = parsed };
        if (style.TryGetValue("font", out var shorthand))
        {
            // "font: bold 10pt Arial" — pick up what we can.
            foreach (var part in shorthand.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (part is "bold")
                    font = font with { Bold = true };
                else if (part is "italic")
                    font = font with { Italic = true };
                else if (XfaMeasure.Parse(part, "pt") is { } s && char.IsAsciiDigit(part[0]))
                    font = font with { Size = s };
            }
        }
        return font;
    }

    private static double? Length(Dictionary<string, string> style, string key)
    {
        if (!style.TryGetValue(key, out var value))
            return null;
        value = value.Trim();
        if (value.EndsWith("px", StringComparison.OrdinalIgnoreCase)
            && double.TryParse(value[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var px))
        {
            return px;
        }
        return XfaMeasure.Parse(value, "pt");
    }
}
