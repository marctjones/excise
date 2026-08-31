using System;
using System.Collections.Generic;
using System.Text;
using Excise.Core.Graphics;
using Excise.Core.Primitives;

namespace Excise.Core.Document;

/// <summary>
/// Bakes a visible normal appearance (<c>/AP /N</c>) onto a signature
/// widget so an applied digital signature actually shows on the page
/// (ISO 32000-2:2020 §12.7.4.5, Table 252). This is the last bullet of
/// issue #623 — the crypto (CMS/ByteRange) is unaffected; this only adds
/// a Form XObject the widget already had room for via its <c>/Rect</c>.
///
/// <para>Mirrors the baked-appearance pattern already used for markup
/// annotations in <see cref="PdfAnnotationAuthoring"/> (#626,
/// <c>BuildFreeTextAppearanceStream</c>): a self-contained Form XObject —
/// own <c>/Resources /Font</c>, stroked border, text drawn with
/// BT/Tf/Td/Tj — clipped to its <c>/BBox</c> by the viewer per spec
/// §8.10.2. No new appearance-stream mechanism is introduced.</para>
/// </summary>
public static class SignatureAppearanceAuthoring
{
    private const double BorderWidth = 1.0;
    private const double MinFontSize = 5;
    private const double MaxFontSize = 9;

    /// <summary>
    /// Bake a visible normal appearance onto <paramref name="widget"/> — the
    /// signature field/widget merged dictionary — if its <c>/Rect</c> has a
    /// non-zero area. A zero-size (or missing) <c>/Rect</c> is the
    /// deliberate invisible-signature case (still a fully valid signature,
    /// per #623's acceptance criteria) and is left untouched: no
    /// <c>/AP</c> is added, matching prior behavior for invisible fields.
    /// </summary>
    /// <param name="document">Document the widget belongs to (for interning
    /// the appearance stream and its font as indirect objects).</param>
    /// <param name="widget">The signature field/widget dictionary, already
    /// carrying its final <c>/Rect</c>.</param>
    /// <param name="lines">Text lines to draw, in order (e.g. "Digitally
    /// signed by X", "Date: ...", "Reason: ..."). Lines that don't fit the
    /// box are not drawn (clipped by <c>/BBox</c>); a single line wider
    /// than the box is truncated with an ellipsis.</param>
    public static void ApplyVisibleAppearance(
        PdfDocument document,
        PdfDictionary widget,
        IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(widget);
        ArgumentNullException.ThrowIfNull(lines);

        if (lines.Count == 0)
            return;

        var rectObj = document.Resolve(widget.GetOptional("Rect") ?? PdfNull.Instance);
        if (rectObj is not PdfArray rectArr)
            return;

        var rect = PdfRectangle.FromArray(rectArr).Normalize();
        if (rect.Width <= 0 || rect.Height <= 0)
            return; // deliberate invisible signature — leave AP-less.

        var apStream = BuildAppearanceStream(document, rect, lines);
        var ap = new PdfDictionary();
        ap["N"] = document.AddIndirectObject(apStream);
        widget["AP"] = ap;
    }

    private static PdfStream BuildAppearanceStream(
        PdfDocument document,
        PdfRectangle rect,
        IReadOnlyList<string> lines)
    {
        double w = rect.Width;
        double h = rect.Height;

        var sb = new StringBuilder();

        double inset = Math.Min(BorderWidth / 2, Math.Min(w, h) / 2 * 0.999);
        sb.Append("0 0 0 RG\n");
        sb.Append($"{Num(BorderWidth)} w\n");
        sb.Append($"{Num(inset)} {Num(inset)} {Num(w - 2 * inset)} {Num(h - 2 * inset)} re S\n");

        double pad = BorderWidth + 2;
        double availableWidth = Math.Max(1, w - 2 * pad);

        double fontSize = Math.Clamp(h / (lines.Count * 1.4), MinFontSize, MaxFontSize);
        var font = PdfFont.Helvetica(fontSize);
        double leading = fontSize * 1.2;

        sb.Append("BT\n");
        sb.Append($"/Helv {Num(fontSize)} Tf\n");
        sb.Append("0 0 0 rg\n");

        double prevX = 0, prevY = 0;
        double baseline = h - pad - fontSize * 0.8;
        foreach (var rawLine in lines)
        {
            if (baseline < -fontSize)
                break; // fully below the BBox — clipped anyway, stop emitting

            var line = TruncateToWidth(rawLine, font, availableWidth);
            sb.Append($"{Num(pad - prevX)} {Num(baseline - prevY)} Td\n");
            sb.Append('(').Append(EscapePdfTextString(line)).Append(") Tj\n");
            prevX = pad;
            prevY = baseline;
            baseline -= leading;
        }

        sb.Append("ET\n");

        var stream = new PdfStream(Encoding.ASCII.GetBytes(sb.ToString()));
        stream.SetName("Type", "XObject");
        stream.SetName("Subtype", "Form");
        stream.SetInt("FormType", 1);
        stream["BBox"] = PdfArray.FromRectangle(0, 0, w, h);

        // Self-contained resources, matching PdfAnnotationAuthoring's
        // baked-appearance streams: the /Helv the Tf refers to.
        var helv = new PdfDictionary();
        helv.SetName("Type", "Font");
        helv.SetName("Subtype", "Type1");
        helv.SetName("BaseFont", "Helvetica");
        helv.SetName("Encoding", "WinAnsiEncoding");

        var fonts = new PdfDictionary();
        fonts["Helv"] = document.AddIndirectObject(helv);
        var resources = new PdfDictionary();
        resources["Font"] = fonts;
        stream["Resources"] = resources;

        return stream;
    }

    /// <summary>Truncate to fit, appending an ellipsis, using real advance widths.</summary>
    private static string TruncateToWidth(string text, PdfFont font, double maxWidth)
    {
        if (font.MeasureWidth(text) <= maxWidth)
            return text;

        const string ellipsis = "...";
        var kept = new StringBuilder();
        foreach (var ch in text)
        {
            if (font.MeasureWidth(kept.ToString() + ch + ellipsis) > maxWidth)
                break;
            kept.Append(ch);
        }

        return kept.Length > 0 ? kept.Append(ellipsis).ToString() : text;
    }

    /// <summary>
    /// Escape a line for a PDF literal string. Printable-ASCII MVP: anything
    /// outside 0x20-0x7E draws as '?' (mirrors
    /// <c>PdfAnnotationAuthoring.EscapePdfTextString</c>).
    /// </summary>
    private static string EscapePdfTextString(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '(': sb.Append("\\("); break;
                case ')': sb.Append("\\)"); break;
                default:
                    sb.Append(ch is < ' ' or > '~' ? '?' : ch);
                    break;
            }
        }
        return sb.ToString();
    }

    private static string Num(double value) => PdfNumberFormatter.Format(value);
}
