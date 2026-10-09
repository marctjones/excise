using System.Globalization;
using System.Text;
using Excise.Core.Primitives;

namespace Excise.Core.Document;

/// <summary>
/// #2017: redraw the <c>/AP /N</c> of a text or combo-box widget excise did
/// NOT author in this session (every document opened from a file, including one
/// Acrobat filled), from the widget's own <c>/DA</c> and the AcroForm
/// <c>/DR</c> fonts (ISO 32000-2 12.7.4.3, the route #1508 describes).
///
/// <para>The caller has already detached the stale appearance; this only adds
/// a new one. When it refuses (returns false), the widget is left with NO
/// <c>/AP</c> and the caller sets <c>/NeedAppearances</c>: <c>/V</c> holds the
/// value, a reader that honours the flag redraws it, and nothing carries or
/// draws the old value. That is not data loss, so a refusal never throws.</para>
///
/// <para>Refused (left to the reader): list boxes (their appearance draws the
/// whole option list), rotated widgets (<c>/MK /R</c>), composite (Type0) and
/// Type3 fonts, symbolic fonts, an embedded SUBSET font (its program may lack
/// the new glyphs), a font with no widths for a character, an encoding this
/// class cannot map a character through, and a <c>/DA</c> without a font.</para>
/// </summary>
internal static class FieldAppearanceRegenerator
{
    private const int FlagMultiline = 1 << 12;
    private const int FlagPassword = 1 << 13;
    private const int FlagCombo = 1 << 17;
    private const int FlagComb = 1 << 24;

    /// <summary>Default size for an auto-sized (<c>0 Tf</c>) multiline field, before shrinking to fit.</summary>
    private const double AutoSizeMultilineStart = 12;
    private const double AutoSizeMinimum = 2;

    internal static bool TryRegenerate(PdfDocument document, PdfField field, PdfDictionary widget, string? value,
        PdfDictionary? previousAppearanceResources)
    {
        var flags = field.Flags;
        var isChoice = field.FieldType == PdfFieldType.Choice;
        if (isChoice && (flags & FlagCombo) == 0)
            return false; // list box: its appearance draws every option
        if (field.FieldType != PdfFieldType.Text && !isChoice)
            return false;

        if (!TryReadRect(document, widget, out var llx, out var lly, out var urx, out var ury))
            return false;
        var width = urx - llx;
        var height = ury - lly;
        if (width <= 0 || height <= 0)
            return false;

        var mk = ResolveDict(document, widget.GetOptional("MK"));
        if (mk != null && mk.GetOptional("R") is { } rotObj && document.Resolve(rotObj).TryGetNumber(out var rotation)
            && ((int)Math.Round(rotation) % 360 + 360) % 360 != 0)
            return false;

        var acroForm = ResolveDict(document, document.Catalog.GetOptional("AcroForm"));
        var da = InheritedString(document, widget, "DA") ?? (acroForm?.GetOptional("DA") is { } formDa
            ? (document.Resolve(formDa) as PdfString)?.Value
            : null);
        if (da == null || !TryParseDefaultAppearance(da, out var fontName, out var daSize, out var colorOps))
            return false;

        var fontObject = FindFontResource(document, acroForm, widget, fontName, previousAppearanceResources);
        if (fontObject == null || !SimpleFontMetrics.TryCreate(document, fontObject, out var metrics))
            return false;

        var text = DisplayText(document, field, widget, value);
        if ((flags & FlagPassword) != 0)
            text = new string('*', new StringInfo(text).LengthInTextElements);

        var multiline = field.FieldType == PdfFieldType.Text && ((flags & FlagMultiline) != 0 || field.IsMultiline);
        if (!multiline)
            text = text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');

        var maxLen = InheritedInt(document, widget, "MaxLen");
        var comb = field.FieldType == PdfFieldType.Text && (flags & FlagComb) != 0 && maxLen is > 0
            && (flags & (FlagMultiline | FlagPassword)) == 0;

        if (!metrics.TryEncode(text, out var codes))
            return false;

        var quadding = InheritedInt(document, widget, "Q") ?? acroForm?.GetInt("Q", 0) ?? 0;

        // Border and background (§12.5.6.19 /MK, §12.5.4 /BS).
        var content = new StringBuilder();
        var background = mk == null ? null : ColorOperator(document, mk.GetOptional("BG"), fill: true);
        var borderColor = mk == null ? null : ColorOperator(document, mk.GetOptional("BC"), fill: false);
        var (borderWidth, borderStyle, dash) = BorderStyle(document, widget);
        if (borderColor == null) borderWidth = 0;

        if (background != null)
            content.Append("q ").Append(background).Append(" 0 0 ").Append(Fmt(width)).Append(' ').Append(Fmt(height)).Append(" re f Q\n");
        if (borderWidth > 0)
        {
            content.Append("q ").Append(borderColor).Append(' ').Append(Fmt(borderWidth)).Append(" w ");
            if (borderStyle == "D" && dash != null) content.Append(dash).Append(" 0 d ");
            var half = borderWidth / 2;
            if (borderStyle == "U")
                content.Append("0 ").Append(Fmt(half)).Append(" m ").Append(Fmt(width)).Append(' ').Append(Fmt(half)).Append(" l S Q\n");
            else
                content.Append(Fmt(half)).Append(' ').Append(Fmt(half)).Append(' ')
                    .Append(Fmt(width - borderWidth)).Append(' ').Append(Fmt(height - borderWidth)).Append(" re S Q\n");
        }

        // Variable text (§12.7.4.3): everything that depends on the value sits
        // inside /Tx BMC ... EMC.
        var inset = borderStyle is "B" or "I" ? 2 * borderWidth : borderWidth;
        var textPad = inset + 2;
        var clipW = Math.Max(0, width - 2 * inset);
        var clipH = Math.Max(0, height - 2 * inset);
        var availW = Math.Max(0, width - 2 * textPad);
        var availH = Math.Max(0, height - 2 * inset);
        var ascent = metrics.Ascent / 1000.0;
        var descent = metrics.Descent / 1000.0; // negative
        var lineFactor = Math.Max(1.0, ascent - descent);

        content.Append("/Tx BMC\n");
        if (codes.Length > 0)
        {
            content.Append("q\n").Append(Fmt(inset)).Append(' ').Append(Fmt(inset)).Append(' ')
                .Append(Fmt(clipW)).Append(' ').Append(Fmt(clipH)).Append(" re W n\nBT\n").Append(colorOps).Append('\n');

            if (comb)
            {
                var cells = maxLen!.Value;
                var cellW = width / cells;
                var shown = codes.Length > cells ? codes[..cells] : codes;
                var size = daSize;
                if (size <= 0)
                {
                    var widest = shown.Max(c => metrics.Width(c));
                    size = Math.Max(AutoSizeMinimum, Math.Min(availH / lineFactor, widest > 0 ? cellW * 1000 / widest : availH));
                }
                var baseline = (height - (ascent - descent) * size) / 2 - descent * size;
                AppendFont(content, fontName, size);
                for (var i = 0; i < shown.Length; i++)
                {
                    var glyphW = metrics.Width(shown[i]) * size / 1000;
                    var x = i * cellW + (cellW - glyphW) / 2;
                    AppendShow(content, x, baseline, new[] { shown[i] });
                }
            }
            else if (multiline)
            {
                var size = daSize;
                List<byte[]> lines;
                if (size > 0)
                {
                    lines = Wrap(codes, metrics, size, availW);
                }
                else
                {
                    size = AutoSizeMultilineStart;
                    lines = Wrap(codes, metrics, size, availW);
                    while (size > AutoSizeMinimum && lines.Count * size * lineFactor > availH)
                    {
                        size = Math.Max(AutoSizeMinimum, size - 0.5);
                        lines = Wrap(codes, metrics, size, availW);
                    }
                }
                AppendFont(content, fontName, size);
                var lineHeight = size * lineFactor;
                var y = height - inset - 1 - ascent * size;
                foreach (var line in lines)
                {
                    AppendShow(content, AlignedX(line, metrics, size, quadding, textPad, availW), y, line);
                    y -= lineHeight;
                }
            }
            else
            {
                var size = daSize;
                if (size <= 0)
                {
                    var units = metrics.Width(codes);
                    var byHeight = availH / lineFactor;
                    var byWidth = units > 0 ? availW * 1000 / units : byHeight;
                    size = Math.Max(AutoSizeMinimum, Math.Min(byHeight, byWidth));
                }
                var baseline = (height - (ascent - descent) * size) / 2 - descent * size;
                AppendFont(content, fontName, size);
                AppendShow(content, AlignedX(codes, metrics, size, quadding, textPad, availW), baseline, codes);
            }

            content.Append("ET\nQ\n");
        }
        content.Append("EMC\n");

        var fonts = new PdfDictionary();
        fonts[fontName] = fontObject;
        var resources = new PdfDictionary();
        resources["Font"] = fonts;

        var bytes = Encoding.Latin1.GetBytes(content.ToString());
        var stream = new PdfDictionary();
        stream.SetName("Type", "XObject");
        stream.SetName("Subtype", "Form");
        var bbox = new PdfArray();
        bbox.Add((PdfObject)new PdfReal(0));
        bbox.Add((PdfObject)new PdfReal(0));
        bbox.Add((PdfObject)new PdfReal(width));
        bbox.Add((PdfObject)new PdfReal(height));
        stream["BBox"] = bbox;
        stream["Resources"] = resources;
        stream.SetInt("Length", bytes.Length);

        var ap = new PdfDictionary();
        ap["N"] = document.AddIndirectObject(new PdfStream(stream, bytes));
        widget["AP"] = ap;
        return true;
    }

    // ── text ────────────────────────────────────────────────────────────────

    /// <summary>The text the widget shows: a combo box shows the display string of an <c>[export display]</c> option.</summary>
    private static string DisplayText(PdfDocument document, PdfField field, PdfDictionary widget, string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (field.FieldType != PdfFieldType.Choice) return value;

        var optObj = InheritedObject(document, widget, "Opt");
        if (document.Resolve(optObj ?? PdfNull.Instance) is PdfArray options)
        {
            foreach (var item in options)
            {
                if (document.Resolve(item) is PdfArray pair && pair.Count >= 2
                    && TextOf(document, pair[0]) == value && TextOf(document, pair[1]) is { } display)
                    return display;
            }
        }
        return value;
    }

    private static string? TextOf(PdfDocument document, PdfObject obj) => document.Resolve(obj) switch
    {
        PdfString s => s.Value,
        PdfName n => n.Value,
        _ => null,
    };

    private static List<byte[]> Wrap(byte[] codes, SimpleFontMetrics metrics, double size, double availWidth)
    {
        var lines = new List<byte[]>();
        var maxUnits = size > 0 ? availWidth * 1000 / size : double.MaxValue;
        foreach (var paragraph in SplitParagraphs(codes))
        {
            if (paragraph.Count == 0) { lines.Add(Array.Empty<byte>()); continue; }

            var line = new List<byte>();
            double lineUnits = 0;
            var i = 0;
            while (i < paragraph.Count)
            {
                // The next word, with the spaces that precede it.
                var start = i;
                while (i < paragraph.Count && paragraph[i] == (byte)' ') i++;
                while (i < paragraph.Count && paragraph[i] != (byte)' ') i++;
                var word = paragraph.GetRange(start, i - start);
                var wordUnits = word.Sum(c => metrics.Width(c));

                if (line.Count == 0 || lineUnits + wordUnits <= maxUnits)
                {
                    line.AddRange(word);
                    lineUnits += wordUnits;
                }
                else
                {
                    lines.Add(line.ToArray());
                    var trimmed = word.SkipWhile(c => c == (byte)' ').ToList();
                    line = trimmed;
                    lineUnits = trimmed.Sum(c => metrics.Width(c));
                }

                // A word wider than the line is broken between characters.
                while (lineUnits > maxUnits && line.Count > 1)
                {
                    var take = 0;
                    double units = 0;
                    while (take < line.Count && units + metrics.Width(line[take]) <= maxUnits) units += metrics.Width(line[take++]);
                    take = Math.Max(1, take);
                    lines.Add(line.GetRange(0, take).ToArray());
                    line = line.GetRange(take, line.Count - take);
                    lineUnits = line.Sum(c => metrics.Width(c));
                }
            }
            lines.Add(line.ToArray());
        }
        return lines;
    }

    private static IEnumerable<List<byte>> SplitParagraphs(byte[] codes)
    {
        var current = new List<byte>();
        for (var i = 0; i < codes.Length; i++)
        {
            var c = codes[i];
            if (c == (byte)'\r' || c == (byte)'\n')
            {
                if (c == (byte)'\r' && i + 1 < codes.Length && codes[i + 1] == (byte)'\n') i++;
                yield return current;
                current = new List<byte>();
                continue;
            }
            current.Add(c);
        }
        yield return current;
    }

    private static double AlignedX(byte[] line, SimpleFontMetrics metrics, double size, int quadding, double pad, double availW)
    {
        var textW = metrics.Width(line) * size / 1000;
        return quadding switch
        {
            1 => pad + (availW - textW) / 2,
            2 => pad + availW - textW,
            _ => pad,
        };
    }

    private static void AppendFont(StringBuilder content, string fontName, double size)
        => content.Append(PdfNameLiteral(fontName)).Append(' ').Append(Fmt(size)).Append(" Tf\n");

    private static void AppendShow(StringBuilder content, double x, double y, byte[] codes)
    {
        content.Append("1 0 0 1 ").Append(Fmt(x)).Append(' ').Append(Fmt(y)).Append(" Tm\n(");
        foreach (var b in codes)
        {
            switch (b)
            {
                case (byte)'(': content.Append("\\("); break;
                case (byte)')': content.Append("\\)"); break;
                case (byte)'\\': content.Append("\\\\"); break;
                default:
                    if (b < 0x20 || b > 0x7E)
                        content.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
                    else
                        content.Append((char)b);
                    break;
            }
        }
        content.Append(") Tj\n");
    }

    private static string PdfNameLiteral(string name)
    {
        var sb = new StringBuilder("/");
        foreach (var b in Encoding.UTF8.GetBytes(name))
        {
            if (b < 0x21 || b > 0x7E || "()<>[]{}/%#".IndexOf((char)b) >= 0)
                sb.Append('#').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            else
                sb.Append((char)b);
        }
        return sb.ToString();
    }

    // ── /DA ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The font name and size of the last <c>Tf</c> in <paramref name="da"/>, and
    /// its colour operators (<c>g</c>, <c>rg</c>, <c>k</c>; black when none).
    /// Anything else in a <c>/DA</c> is ignored.
    /// </summary>
    internal static bool TryParseDefaultAppearance(string da, out string fontName, out double size, out string colorOps)
    {
        fontName = string.Empty;
        size = 0;
        colorOps = "0 g";
        var tokens = da.Split(new[] { ' ', '\t', '\r', '\n', '\f', '\0' }, StringSplitOptions.RemoveEmptyEntries);
        var found = false;
        string? color = null;
        for (var i = 0; i < tokens.Length; i++)
        {
            switch (tokens[i])
            {
                case "Tf" when i >= 2 && tokens[i - 2].StartsWith('/')
                    && double.TryParse(tokens[i - 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var s):
                    fontName = DecodeName(tokens[i - 2][1..]);
                    size = Math.Abs(s);
                    found = fontName.Length > 0;
                    break;
                case "g" when Operands(tokens, i, 1) is { } g: color = g + " g"; break;
                case "rg" when Operands(tokens, i, 3) is { } rg: color = rg + " rg"; break;
                case "k" when Operands(tokens, i, 4) is { } k: color = k + " k"; break;
            }
        }
        if (color != null) colorOps = color;
        return found;
    }

    private static string? Operands(string[] tokens, int op, int count)
    {
        if (op < count) return null;
        var values = new string[count];
        for (var j = 0; j < count; j++)
        {
            if (!double.TryParse(tokens[op - count + j], NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                return null;
            values[j] = Fmt(Math.Clamp(v, 0, 1));
        }
        return string.Join(' ', values);
    }

    private static string DecodeName(string raw)
    {
        if (raw.IndexOf('#') < 0) return raw;
        var bytes = new List<byte>();
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '#' && i + 2 < raw.Length
                && byte.TryParse(raw.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            {
                bytes.Add(b);
                i += 2;
            }
            else
            {
                bytes.Add((byte)raw[i]);
            }
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    /// <summary>The <c>/DA</c> font: AcroForm <c>/DR /Font</c>, else the old appearance's own resources (#1508).</summary>
    private static PdfObject? FindFontResource(PdfDocument document, PdfDictionary? acroForm, PdfDictionary widget,
        string fontName, PdfDictionary? previousAppearanceResources)
    {
        foreach (var resources in new[]
                 {
                     ResolveDict(document, acroForm?.GetOptional("DR")),
                     ResolveDict(document, widget.GetOptional("DR")),
                     previousAppearanceResources,
                 })
        {
            if (resources == null) continue;
            if (ResolveDict(document, resources.GetOptional("Font")) is { } fonts && fonts.TryGetValue(fontName, out var font))
                return font;
        }
        return null;
    }

    // ── widget geometry and border ──────────────────────────────────────────

    private static (double Width, string Style, string? Dash) BorderStyle(PdfDocument document, PdfDictionary widget)
    {
        var width = 1.0;
        var style = "S";
        string? dash = null;
        if (ResolveDict(document, widget.GetOptional("BS")) is { } bs)
        {
            if (bs.GetOptional("W") is { } w && document.Resolve(w).TryGetNumber(out var bw)) width = Math.Max(0, bw);
            style = bs.GetNameOrNull("S") ?? "S";
            if (style == "D")
            {
                var parts = new List<string>();
                if (document.Resolve(bs.GetOptional("D") ?? PdfNull.Instance) is PdfArray d)
                    foreach (var item in d)
                        if (document.Resolve(item).TryGetNumber(out var n) && n >= 0) parts.Add(Fmt(n));
                dash = "[" + (parts.Count > 0 ? string.Join(' ', parts) : "3") + "]";
            }
        }
        else if (document.Resolve(widget.GetOptional("Border") ?? PdfNull.Instance) is PdfArray border && border.Count >= 3
                 && document.Resolve(border[2]).TryGetNumber(out var legacy))
        {
            width = Math.Max(0, legacy);
        }
        return (width, style, dash);
    }

    private static string? ColorOperator(PdfDocument document, PdfObject? colorObj, bool fill)
    {
        if (document.Resolve(colorObj ?? PdfNull.Instance) is not PdfArray color) return null;
        var values = new List<string>();
        foreach (var item in color)
        {
            if (!document.Resolve(item).TryGetNumber(out var v)) return null;
            values.Add(Fmt(Math.Clamp(v, 0, 1)));
        }
        var op = values.Count switch
        {
            1 => fill ? "g" : "G",
            3 => fill ? "rg" : "RG",
            4 => fill ? "k" : "K",
            _ => null,
        };
        return op == null ? null : string.Join(' ', values) + " " + op;
    }

    private static bool TryReadRect(PdfDocument document, PdfDictionary widget,
        out double llx, out double lly, out double urx, out double ury)
    {
        llx = lly = urx = ury = 0;
        if (document.Resolve(widget.GetOptional("Rect") ?? PdfNull.Instance) is not PdfArray array || array.Count < 4)
            return false;
        var v = new double[4];
        for (var i = 0; i < 4; i++)
            if (!document.Resolve(array[i]).TryGetNumber(out v[i]) || !double.IsFinite(v[i]))
                return false;
        llx = Math.Min(v[0], v[2]); urx = Math.Max(v[0], v[2]);
        lly = Math.Min(v[1], v[3]); ury = Math.Max(v[1], v[3]);
        return true;
    }

    // ── inheritance (§12.7.4.4) ─────────────────────────────────────────────

    private static PdfObject? InheritedObject(PdfDocument document, PdfDictionary widget, string key)
    {
        var current = widget;
        for (var depth = 0; depth < 64 && current != null; depth++)
        {
            if (current.GetOptional(key) is { } value) return value;
            current = ResolveDict(document, current.GetOptional("Parent"));
        }
        return null;
    }

    private static string? InheritedString(PdfDocument document, PdfDictionary widget, string key)
        => document.Resolve(InheritedObject(document, widget, key) ?? PdfNull.Instance) is PdfString s ? s.Value : null;

    private static int? InheritedInt(PdfDocument document, PdfDictionary widget, string key)
        => InheritedObject(document, widget, key) is { } obj && document.Resolve(obj).TryGetNumber(out var n)
            ? (int)n
            : null;

    private static PdfDictionary? ResolveDict(PdfDocument document, PdfObject? obj)
        => obj == null ? null : document.Resolve(obj) as PdfDictionary;

    private static string Fmt(double value) => PdfNumberFormatter.Format(value);

    // ── font metrics ────────────────────────────────────────────────────────

    /// <summary>
    /// A simple (single-byte) font as the <c>/DR</c> declares it: its encoding
    /// (Unicode → code) and its advance widths, read from the font dictionary
    /// (<c>/Widths</c>), else the standard-14 metrics. Refuses what it cannot
    /// encode or measure honestly.
    /// </summary>
    private sealed class SimpleFontMetrics
    {
        private readonly PdfDocument _document;
        private readonly string _baseFont;
        private readonly int _firstChar;
        private readonly PdfArray? _widths;
        private readonly double? _missingWidth;
        private readonly bool _winAnsi;
        private readonly bool _asciiOnlyStandard;
        private readonly HashSet<int> _differences;

        public double Ascent { get; }
        public double Descent { get; }

        private SimpleFontMetrics(PdfDocument document, string baseFont, int firstChar, PdfArray? widths,
            double? missingWidth, bool winAnsi, bool asciiOnlyStandard, HashSet<int> differences, double ascent, double descent)
        {
            _document = document;
            _baseFont = baseFont;
            _firstChar = firstChar;
            _widths = widths;
            _missingWidth = missingWidth;
            _winAnsi = winAnsi;
            _asciiOnlyStandard = asciiOnlyStandard;
            _differences = differences;
            Ascent = ascent;
            Descent = descent;
        }

        public static bool TryCreate(PdfDocument document, PdfObject fontObject, out SimpleFontMetrics metrics)
        {
            metrics = null!;
            if (document.Resolve(fontObject) is not PdfDictionary font) return false;
            var subtype = font.GetNameOrNull("Subtype");
            if (subtype is not ("Type1" or "TrueType" or "MMType1")) return false;

            var baseFont = font.GetNameOrNull("BaseFont") ?? string.Empty;
            var stripped = StripSubset(baseFont);
            if (stripped.StartsWith("Symbol", StringComparison.Ordinal) || stripped.StartsWith("ZapfDingbats", StringComparison.Ordinal))
                return false;

            var descriptor = ResolveDict(document, font.GetOptional("FontDescriptor"));
            var embedded = descriptor != null
                && (descriptor.ContainsKey("FontFile") || descriptor.ContainsKey("FontFile2") || descriptor.ContainsKey("FontFile3"));
            // A subset program may not hold the new value's glyphs.
            if (embedded && stripped.Length != baseFont.Length) return false;
            if (descriptor != null && descriptor.GetOptional("Flags") is { } flagsObj
                && document.Resolve(flagsObj).TryGetNumber(out var fontFlags) && ((int)fontFlags & 4) != 0)
                return false; // symbolic: the built-in encoding is not WinAnsi/Standard

            var winAnsi = false;
            var asciiOnly = false;
            var differences = new HashSet<int>();
            switch (document.Resolve(font.GetOptional("Encoding") ?? PdfNull.Instance))
            {
                case PdfName { Value: "WinAnsiEncoding" }:
                    winAnsi = true;
                    break;
                case PdfName { Value: "StandardEncoding" or "MacRomanEncoding" }:
                case PdfNull:
                    asciiOnly = true;
                    break;
                case PdfDictionary encoding:
                    var baseEncoding = encoding.GetNameOrNull("BaseEncoding");
                    if (baseEncoding == "WinAnsiEncoding") winAnsi = true;
                    else if (baseEncoding is null or "StandardEncoding" or "MacRomanEncoding") asciiOnly = true;
                    else return false;
                    if (document.Resolve(encoding.GetOptional("Differences") ?? PdfNull.Instance) is PdfArray diffs)
                    {
                        var code = -1;
                        foreach (var item in diffs)
                        {
                            var resolved = document.Resolve(item);
                            if (resolved.TryGetNumber(out var n)) code = (int)n;
                            else if (resolved is PdfName && code >= 0) differences.Add(code++);
                        }
                    }
                    break;
                default:
                    return false;
            }

            var firstChar = font.GetOptional("FirstChar") is { } fc && document.Resolve(fc).TryGetNumber(out var fcv) ? (int)fcv : 0;
            var widths = document.Resolve(font.GetOptional("Widths") ?? PdfNull.Instance) as PdfArray;
            double? missing = descriptor?.GetOptional("MissingWidth") is { } mw && document.Resolve(mw).TryGetNumber(out var mwv) && mwv > 0
                ? mwv : null;

            double ascent = 0, descent = 0;
            if (descriptor != null)
            {
                if (descriptor.GetOptional("Ascent") is { } a && document.Resolve(a).TryGetNumber(out var av)) ascent = av;
                if (descriptor.GetOptional("Descent") is { } d && document.Resolve(d).TryGetNumber(out var dv)) descent = dv;
            }
            if (ascent <= 0) ascent = stripped.StartsWith("Courier", StringComparison.Ordinal) ? 629 : stripped.StartsWith("Times", StringComparison.Ordinal) ? 683 : 718;
            if (descent >= 0) descent = stripped.StartsWith("Courier", StringComparison.Ordinal) ? -157 : stripped.StartsWith("Times", StringComparison.Ordinal) ? -217 : -207;

            metrics = new SimpleFontMetrics(document, baseFont, firstChar, widths, missing, winAnsi, asciiOnly, differences, ascent, descent);
            return true;
        }

        /// <summary>Encode <paramref name="text"/>; false when any character has no code, or no known width, in this font.</summary>
        public bool TryEncode(string text, out byte[] codes)
        {
            codes = new byte[text.Length];
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c is '\r' or '\n') { codes[i] = (byte)c; continue; }
                byte code;
                if (_winAnsi)
                {
                    if (!Fonts.WinAnsiEncoding.TryMap(c, out code)) return false;
                }
                else if (_asciiOnlyStandard)
                {
                    // StandardEncoding differs from ASCII at 0x27 and 0x60, and every
                    // base encoding differs above 0x7E.
                    if (c < 0x20 || c > 0x7E || c == '\'' || c == '`') return false;
                    code = (byte)c;
                }
                else
                {
                    return false;
                }
                if (_differences.Contains(code)) return false;
                if (!TryWidth(code, out _)) return false;
                codes[i] = code;
            }
            return true;
        }

        public double Width(byte code) => TryWidth(code, out var w) ? w : 0;

        public double Width(IEnumerable<byte> codes)
        {
            double sum = 0;
            foreach (var c in codes) sum += Width(c);
            return sum;
        }

        private bool TryWidth(byte code, out double width)
        {
            width = 0;
            if (code is (byte)'\r' or (byte)'\n') return true;
            if (_widths != null)
            {
                var index = code - _firstChar;
                if (index >= 0 && index < _widths.Count && _document.Resolve(_widths[index]).TryGetNumber(out width))
                    return true;
                if (_missingWidth is { } missing) { width = missing; return true; }
                return false;
            }

            // No /Widths: only a standard-14 font has metrics a viewer agrees on.
            if (_winAnsi && code is 39 or 96
                && Fonts.StandardFontMetrics.TryGetWidthByGlyphName(_baseFont, code == 39 ? "quotesingle" : "grave", out width))
                return true;
            return Fonts.StandardFontMetrics.TryGetWidth(_baseFont, code, out width);
        }

        private static string StripSubset(string baseFont)
            => baseFont.Length > 7 && baseFont[6] == '+' && baseFont[..6].All(char.IsAsciiLetterUpper)
                ? baseFont[7..]
                : baseFont;
    }
}
