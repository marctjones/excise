using System.Globalization;
using System.Text;
using Excise.Core.Graphics;
using Excise.Core.Primitives;

namespace Excise.Core.Document;

/// <summary>
/// Bakes AcroForm field values into static page content streams and removes
/// widget annotations. Used by <see cref="PdfDocument.FlattenAcroForm"/>.
///
/// Render rules (MVP):
///   • Text and Choice fields: drawn as a single line of Helvetica at /DA-derived
///     size when parseable, else 10 pt. Multiline strings get one line per /n.
///   • Button fields: when value is anything other than "Off"/null, draws an
///     "X" centred in the rect using ZapfDingbats-equivalent glyph (a Helvetica
///     "X" — close enough for MVP and avoids an extra font dependency).
///   • Signature fields: skipped (the visible signature appearance, if any,
///     is left in /AP and the widget annotation is preserved).
///
/// What's intentionally not supported in this MVP:
///   • Full /DA parsing (font, color, size beyond the size token)
///   • Word wrapping inside the rect
///   • Right/centre alignment (/Q)
///   • Combo-box dropdowns / multi-select list-boxes
///
/// Anything unsupported falls back to "draw the value as left-aligned plain
/// text", which is the de-facto behaviour Acrobat shows when /NeedAppearances
/// is true.
/// </summary>
internal static class AcroFormFlattener
{
    private sealed record FieldDrawTarget(PdfField Field, PdfRectangle Rect, string? ExportValue, PdfDictionary? Widget);

    public static void Flatten(PdfDocument document, PdfAcroForm form)
    {
        // Group fields by host page so we append once per page rather than
        // rewriting the same content stream many times.
        var byPage = new Dictionary<int, List<FieldDrawTarget>>();
        foreach (var field in form.Fields)
        {
            if (field.FieldType == PdfFieldType.Signature) continue;

            if (field.FieldType == PdfFieldType.Button && field.Widgets.Count > 0)
            {
                for (var i = 0; i < field.Widgets.Count; i++)
                {
                    var widget = field.Widgets[i];
                    if (widget.PageNumber is not int widgetPageNumber) continue;
                    var widgetDict = i < field.WidgetDictionaries.Count ? field.WidgetDictionaries[i] : null;
                    AddTarget(byPage, widgetPageNumber, new FieldDrawTarget(field, widget.Rect, widget.ExportValue, widgetDict));
                }
                continue;
            }

            if (field.PageNumber is int pageNumber && field.Rect is PdfRectangle rect)
                AddTarget(byPage, pageNumber, new FieldDrawTarget(field, rect, ExportValue: null, Widget: null));
        }

        foreach (var (pageNumber, targets) in byPage)
        {
            var page = document.GetPage(pageNumber);
            AppendFieldDrawing(document, page, targets);
            RemoveWidgetAnnotations(document, page, targets.Select(t => t.Field));
        }

        // Drop catalog-level orphaned widgets that may not have been on any
        // page (defensive — most PDFs don't do this).
    }

    private static void AddTarget(
        Dictionary<int, List<FieldDrawTarget>> byPage,
        int pageNumber,
        FieldDrawTarget target)
    {
        if (!byPage.TryGetValue(pageNumber, out var list))
            byPage[pageNumber] = list = new List<FieldDrawTarget>();
        list.Add(target);
    }

    private static void AppendFieldDrawing(PdfDocument document, PdfPage page, List<FieldDrawTarget> targets)
    {
        // We need a Helvetica entry in the page's font resources to draw the
        // field text. Reuse if present; add a fresh /F-Flat entry otherwise.
        var fontResourceName = EnsureHelveticaResource(page);

        var existing = page.GetContentStreamBytes();
        var sb = new StringBuilder();

        // Wrap original page content in q…Q so any graphics state our
        // appended draws make doesn't leak. (PDF readers tolerate q without
        // a balanced Q, but adding our own balanced pair is cleaner.)
        sb.Append("q\n");
        sb.Append(Encoding.Latin1.GetString(existing));
        if (existing.Length > 0 && existing[^1] != (byte)'\n') sb.Append('\n');
        sb.Append("Q\n");

        foreach (var target in targets)
        {
            // A pushbutton has no /V to draw — its entire visible content is
            // the widget's /AP /N appearance stream (e.g. the "Clear Form"
            // label). Removing the widget without stamping that appearance
            // erased visible ink (#962, found by the #945 conservation
            // gates). Stamp it into the page before the widget goes.
            if (target.Field.IsPushButton && target.Widget != null)
            {
                StampWidgetAppearance(document, page, sb, target.Widget, target.Rect);
                continue;
            }

            DrawField(sb, target, fontResourceName);
        }

        var bytes = Encoding.Latin1.GetBytes(sb.ToString());
        page.SetContentStreamBytes(bytes);
    }

    /// <summary>
    /// Stamp a widget's /AP /N form XObject into the page content, mapped
    /// onto the widget rectangle by the §12.5.5 algorithm: transform the
    /// appearance /BBox through its /Matrix, then scale/translate that
    /// bounding box onto /Rect. The raw /N entry (normally an indirect
    /// reference) goes into the page's XObject resources unchanged, so the
    /// stream is shared, not copied.
    /// </summary>
    private static void StampWidgetAppearance(
        PdfDocument document, PdfPage page, StringBuilder sb, PdfDictionary widget, PdfRectangle rect)
    {
        var rawAp = widget.GetOptional("AP");
        if (rawAp == null || document.Resolve(rawAp) is not PdfDictionary ap)
            return;

        var rawAppearance = ap.GetOptional("N");
        var resolved = rawAppearance == null ? null : document.Resolve(rawAppearance);

        // /N may be a state subdictionary instead of a stream (checkbox-style
        // appearances). Pushbuttons normally carry a single stream; when a
        // state dict shows up anyway, follow /AS, else take the first entry.
        if (resolved is PdfDictionary states && resolved is not PdfStream)
        {
            var stateName = widget.GetNameOrNull("AS");
            rawAppearance = (stateName != null ? states.GetOptional(stateName) : null)
                ?? states.Values.FirstOrDefault();
            resolved = rawAppearance == null ? null : document.Resolve(rawAppearance);
        }

        if (resolved is not PdfStream appearance || rawAppearance == null)
            return;

        if (!TryGetNumbers(document, appearance.GetArrayOrNull("BBox"), 4, out var bbox))
            return;

        double[] matrix = { 1, 0, 0, 1, 0, 0 };
        if (TryGetNumbers(document, appearance.GetArrayOrNull("Matrix"), 6, out var m))
            matrix = m;

        // Transform the four BBox corners through /Matrix and take bounds.
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (x, y) in new[] { (bbox[0], bbox[1]), (bbox[2], bbox[1]), (bbox[2], bbox[3]), (bbox[0], bbox[3]) })
        {
            var tx = matrix[0] * x + matrix[2] * y + matrix[4];
            var ty = matrix[1] * x + matrix[3] * y + matrix[5];
            minX = Math.Min(minX, tx); maxX = Math.Max(maxX, tx);
            minY = Math.Min(minY, ty); maxY = Math.Max(maxY, ty);
        }

        var bw = maxX - minX;
        var bh = maxY - minY;
        if (bw < 1e-6 || bh < 1e-6 || rect.Width < 1e-6 || rect.Height < 1e-6)
            return;

        var sx = rect.Width / bw;
        var sy = rect.Height / bh;
        var ox = rect.Left - minX * sx;
        var oy = rect.Bottom - minY * sy;

        var name = AddXObjectResource(document, page, rawAppearance);

        sb.Append("q\n");
        sb.Append(sx.ToString("0.####", CultureInfo.InvariantCulture)).Append(" 0 0 ")
          .Append(sy.ToString("0.####", CultureInfo.InvariantCulture)).Append(' ')
          .Append(ox.ToString("0.###", CultureInfo.InvariantCulture)).Append(' ')
          .Append(oy.ToString("0.###", CultureInfo.InvariantCulture)).Append(" cm\n");
        sb.Append('/').Append(name).Append(" Do\n");
        sb.Append("Q\n");
    }

    private static bool TryGetNumbers(PdfDocument document, PdfArray? array, int count, out double[] values)
    {
        values = new double[count];
        if (array == null || array.Count < count)
            return false;
        for (var i = 0; i < count; i++)
        {
            if (document.Resolve(array[i]) is not PdfObject obj || !obj.TryGetNumber(out var n))
                return false;
            values[i] = n;
        }
        return true;
    }

    private static string AddXObjectResource(PdfDocument document, PdfPage page, PdfObject appearance)
    {
        var resources = page.Resources;
        if (resources == null)
        {
            resources = new PdfDictionary();
            page.Dictionary["Resources"] = resources;
        }

        var rawXObjects = resources.GetOptional("XObject");
        if (rawXObjects == null || document.Resolve(rawXObjects) is not PdfDictionary xobjects)
        {
            xobjects = new PdfDictionary();
            resources["XObject"] = xobjects;
        }

        var counter = 0;
        string name;
        do { name = $"FlatAP{counter++}"; } while (xobjects.ContainsKey(name));

        xobjects[name] = appearance;
        return name;
    }

    private static string EnsureHelveticaResource(PdfPage page)
    {
        // PdfPage.AddFont already de-dupes by base font name, so calling it
        // twice for the same font is a no-op the second time.
        return page.AddFont(PdfFont.Helvetica(10));
    }

    private static void DrawField(StringBuilder sb, FieldDrawTarget target, string fontResourceName)
    {
        var field = target.Field;
        var rect = target.Rect;
        var value = field.Value;
        if (string.IsNullOrEmpty(value)) return;

        switch (field.FieldType)
        {
            case PdfFieldType.Button:
                if (ShouldDrawButtonMark(field, target, value))
                    DrawCheckmark(sb, rect, fontResourceName);
                break;

            case PdfFieldType.Text:
            case PdfFieldType.Choice:
            default:
                DrawText(sb, rect, value!, fontResourceName, ParseFontSize(field));
                break;
        }
    }

    private static bool ShouldDrawButtonMark(PdfField field, FieldDrawTarget target, string value)
    {
        if (string.Equals(value, "Off", StringComparison.OrdinalIgnoreCase))
            return false;

        if (field.ButtonExportValues.Count <= 1)
            return true;

        return string.Equals(target.ExportValue, value, StringComparison.Ordinal);
    }

    private static void DrawText(StringBuilder sb, PdfRectangle rect, string value, string fontResourceName, double fontSize)
    {
        // Split on newline so multiline text fields get one line per row,
        // top-down. PDF y grows upward, so the first line sits highest.
        var lines = WrapLines(value.Replace("\r\n", "\n").Replace('\r', '\n'), rect, fontSize).ToList();
        var leading = fontSize * 1.2;

        var x = rect.Left + 2.0;
        var firstY = rect.Top - fontSize;     // top-aligned baseline
        if (firstY < rect.Bottom + 2.0)
            firstY = rect.Bottom + 2.0;       // single-line: sit just above the bottom edge

        sb.Append("q\n");
        sb.Append(rect.Left.ToString("0.###", CultureInfo.InvariantCulture)).Append(' ')
          .Append(rect.Bottom.ToString("0.###", CultureInfo.InvariantCulture)).Append(' ')
          .Append(rect.Width.ToString("0.###", CultureInfo.InvariantCulture)).Append(' ')
          .Append(rect.Height.ToString("0.###", CultureInfo.InvariantCulture))
          .Append(" re W n\n");
        sb.Append("BT\n");
        sb.Append('/').Append(fontResourceName).Append(' ')
          .Append(fontSize.ToString("0.###", CultureInfo.InvariantCulture))
          .Append(" Tf\n");
        sb.Append("0 g\n");
        sb.Append(x.ToString("0.###", CultureInfo.InvariantCulture)).Append(' ')
          .Append(firstY.ToString("0.###", CultureInfo.InvariantCulture))
          .Append(" Td\n");

        var maxLines = Math.Max(1, (int)Math.Floor(Math.Max(1, rect.Height - 4) / leading));
        for (int i = 0; i < Math.Min(lines.Count, maxLines); i++)
        {
            if (i > 0)
            {
                sb.Append("0 ").Append((-leading).ToString("0.###", CultureInfo.InvariantCulture)).Append(" Td\n");
            }
            sb.Append('(').Append(EscapePdfString(lines[i])).Append(") Tj\n");
        }

        sb.Append("ET\n");
        sb.Append("Q\n");
    }

    private static IEnumerable<string> WrapLines(string value, PdfRectangle rect, double fontSize)
    {
        var maxChars = Math.Max(1, (int)Math.Floor(Math.Max(1, rect.Width - 4) / Math.Max(1, fontSize * 0.5)));
        foreach (var rawLine in value.Split('\n'))
        {
            if (rawLine.Length <= maxChars)
            {
                yield return rawLine;
                continue;
            }

            var line = rawLine;
            while (line.Length > maxChars)
            {
                var breakAt = line.LastIndexOf(' ', Math.Min(maxChars, line.Length - 1));
                if (breakAt <= 0) breakAt = maxChars;
                yield return line[..breakAt].TrimEnd();
                line = line[breakAt..].TrimStart();
            }

            yield return line;
        }
    }

    private static void DrawCheckmark(StringBuilder sb, PdfRectangle rect, string fontResourceName)
    {
        // Draw a black "X" sized to the rect. Keeps things simple and font-
        // independent enough for MVP — Acrobat normally uses ZapfDingbats but
        // most readers will render an "X" in Helvetica fine.
        var size = Math.Max(2.0, Math.Min(rect.Width, rect.Height) * 0.7);
        var x = rect.Left + (rect.Width - size * 0.5) * 0.5;
        var y = rect.Bottom + (rect.Height - size) * 0.5;

        sb.Append("q\n");
        sb.Append("BT\n");
        sb.Append('/').Append(fontResourceName).Append(' ')
          .Append(size.ToString("0.###", CultureInfo.InvariantCulture)).Append(" Tf\n");
        sb.Append("0 g\n");
        sb.Append(x.ToString("0.###", CultureInfo.InvariantCulture)).Append(' ')
          .Append(y.ToString("0.###", CultureInfo.InvariantCulture)).Append(" Td\n");
        sb.Append("(X) Tj\n");
        sb.Append("ET\n");
        sb.Append("Q\n");
    }

    /// <summary>
    /// Parse "(/Helv 10 Tf 0 g)" appearance string for a font size. Returns
    /// 10 when /DA is missing or unparseable. Doesn't try to honor the font
    /// or color — those would require resolving the AcroForm's /DR resources.
    /// </summary>
    private static double ParseFontSize(PdfField field)
    {
        var da = field.RawDictionary.GetStringOrNull("DA");
        if (da == null) return 10.0;

        // Look for "<num> Tf" pattern.
        var tokens = da.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 1; i < tokens.Length; i++)
        {
            if (tokens[i] == "Tf" &&
                double.TryParse(tokens[i - 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var size) &&
                size > 0)
            {
                return size;
            }
        }
        return 10.0;
    }

    private static string EscapePdfString(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '(':  sb.Append("\\(");  break;
                case ')':  sb.Append("\\)");  break;
                case '\n': sb.Append("\\n");  break;
                case '\r': sb.Append("\\r");  break;
                case '\t': sb.Append("\\t");  break;
                default:
                    if (ch < 0x20 || ch > 0x7E) sb.Append('?'); // Latin1-only MVP
                    else sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }

    private static void RemoveWidgetAnnotations(PdfDocument document, PdfPage page, IEnumerable<PdfField> fields)
    {
        var annotsObj = page.Dictionary.GetOptional("Annots");
        if (annotsObj == null) return;
        if (document.Resolve(annotsObj) is not PdfArray annots) return;

        // Collect widget dictionaries we need to drop. Identity comparison via
        // ReferenceEquals is enough — same instance traveled through the
        // parser into the field.
        var widgetSet = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        foreach (var f in fields)
            foreach (var w in f.WidgetDictionaries)
                widgetSet.Add(w);

        for (int i = annots.Count - 1; i >= 0; i--)
        {
            var resolved = document.Resolve(annots[i]);
            if (resolved is PdfDictionary annotDict && widgetSet.Contains(annotDict))
                annots.RemoveAt(i);
        }
    }
}
