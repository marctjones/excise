using System.Globalization;
using System.Text;
using Pdfe.Core.Graphics;
using Pdfe.Core.Primitives;

namespace Pdfe.Core.Document;

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
    private sealed record FieldDrawTarget(PdfField Field, PdfRectangle Rect, string? ExportValue);

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
                foreach (var widget in field.Widgets)
                {
                    if (widget.PageNumber is not int widgetPageNumber) continue;
                    AddTarget(byPage, widgetPageNumber, new FieldDrawTarget(field, widget.Rect, widget.ExportValue));
                }
                continue;
            }

            if (field.PageNumber is int pageNumber && field.Rect is PdfRectangle rect)
                AddTarget(byPage, pageNumber, new FieldDrawTarget(field, rect, ExportValue: null));
        }

        foreach (var (pageNumber, targets) in byPage)
        {
            var page = document.GetPage(pageNumber);
            AppendFieldDrawing(page, targets);
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

    private static void AppendFieldDrawing(PdfPage page, List<FieldDrawTarget> targets)
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
            DrawField(sb, target, fontResourceName);

        var bytes = Encoding.Latin1.GetBytes(sb.ToString());
        page.SetContentStreamBytes(bytes);
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
