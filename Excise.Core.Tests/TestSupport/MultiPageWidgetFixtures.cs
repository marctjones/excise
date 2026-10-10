using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Excise.TestSupport;

/// <summary>
/// #2040: one AcroForm text field with a widget on each of several pages, as a form repeats a
/// name or case number in every page header. The field is a parent dictionary whose <c>/Kids</c>
/// are the widgets; each widget sits in its own page's <c>/Annots</c> and draws its own
/// appearance text (Helvetica, uncompressed, no <c>/NeedAppearances</c>, so every reader paints
/// the appearance as written).
/// </summary>
internal static class MultiPageWidgetFixtures
{
    public const string FieldName = "CaseNo";
    public static readonly double[] WidgetRect = [100, 700, 400, 720];

    /// <summary>The glyph origin of each appearance's first glyph on its page: /Rect origin + (2, 6).</summary>
    public const double GlyphX = 102, GlyphY = 706;

    /// <param name="drawn">What the widget on page i+1 draws; null draws no text. One widget per entry.</param>
    /// <param name="value">The field's <c>/V</c>; null for none.</param>
    /// <param name="omitP">Pages (1-based) whose widget carries no <c>/P</c>: its page is the
    /// one whose <c>/Annots</c> lists it (ISO 32000-2 12.5.2).</param>
    /// <param name="hiddenPage">The page (1-based) whose widget is flagged Hidden (<c>/F 2</c>), if any.</param>
    /// <param name="emptyPages">Pages with no widget at all, after the widget pages.</param>
    /// <param name="shareAppearance">#2059: page (1-based) to an earlier page whose <c>/AP /N</c>
    /// stream object its widget references instead of its own (one stream, two widgets); that
    /// page's <paramref name="drawn"/> entry is then unused.</param>
    /// <param name="shiftedFontPages">#2059: pages whose appearance draws its text in a simple
    /// font whose <c>/ToUnicode</c> maps every code to the next code (excise reads other letters
    /// while the stream holds the text's codes, the #2043 font).</param>
    /// <param name="formatAction">#2059: give the field a format action (<c>/AA /F</c>).</param>
    public static byte[] Build(
        IReadOnlyList<string?> drawn,
        string? value,
        IReadOnlyCollection<int>? omitP = null,
        int? hiddenPage = null,
        int emptyPages = 0,
        IReadOnlyDictionary<int, int>? shareAppearance = null,
        IReadOnlyCollection<int>? shiftedFontPages = null,
        bool formatAction = false)
    {
        omitP ??= [];
        shareAppearance ??= new Dictionary<int, int>();
        shiftedFontPages ??= [];
        var objects = new List<string>();
        int Add(string body) { objects.Add(body); return objects.Count; }
        int Reserve() { objects.Add(""); return objects.Count; }
        void Set(int number, string body) => objects[number - 1] = body;

        var catalog = Reserve();
        var pagesObj = Reserve();
        var pageCount = drawn.Count + emptyPages;
        var pages = Enumerable.Range(0, pageCount).Select(_ => Reserve()).ToList();
        var field = Reserve();
        var helv = Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        var fonts = $"/Resources << /Font << /Helv {helv} 0 R >> >>";

        var widgets = new List<int>();
        var appearances = new Dictionary<int, int>();
        for (var i = 0; i < drawn.Count; i++)
        {
            var pageNumber = i + 1;
            int ap;
            if (shareAppearance.TryGetValue(pageNumber, out var source))
            {
                ap = appearances[source];
            }
            else
            {
                var shifted = shiftedFontPages.Contains(pageNumber);
                var font = shifted ? "/Odd" : "/Helv";
                var body = drawn[i] is { } text
                    ? $"/Tx BMC BT {font} 12 Tf 0 g 2 6 Td ({text}) Tj ET EMC"
                    : "/Tx BMC EMC";
                var resources = shifted ? $"/Resources << /Font << /Odd {ShiftedFont(Add)} >> >>" : fonts;
                ap = Add(RawStream($"/Type /XObject /Subtype /Form /BBox [0 0 300 20] {resources}", body));
            }
            appearances[pageNumber] = ap;
            var flags = hiddenPage == pageNumber ? 2 : 4;
            var p = omitP.Contains(pageNumber) ? "" : $"/P {pages[i]} 0 R ";
            widgets.Add(Add($"<< /Type /Annot /Subtype /Widget /F {flags} /Rect {RectOf(WidgetRect)} {p}" +
                            $"/Parent {field} 0 R /AP << /N {ap} 0 R >> >>"));
        }

        var v = value != null ? $"/V ({value}) " : "";
        if (formatAction)
            v += "/AA << /F << /S /JavaScript /JS (AFNumber_Format\\(2, 0, 0, 0, \"$\", true\\);) >> >> ";
        Set(field, $"<< /FT /Tx /T ({FieldName}) {v}/DA (/Helv 12 Tf 0 g) " +
                   $"/Kids [{string.Join(' ', widgets.Select(n => $"{n} 0 R"))}] >>");

        for (var i = 0; i < pageCount; i++)
        {
            var contents = Add(RawStream("", $"BT /Helv 10 Tf 20 760 Td (Page {i + 1} body) Tj ET"));
            var annots = i < widgets.Count ? $"/Annots [{widgets[i]} 0 R] " : "";
            Set(pages[i], $"<< /Type /Page /Parent {pagesObj} 0 R /MediaBox [0 0 612 792] /Contents {contents} 0 R " +
                          $"{fonts} {annots}>>");
        }

        var acroForm = Add($"<< /Fields [{field} 0 R] /DA (/Helv 0 Tf 0 g) /DR << /Font << /Helv {helv} 0 R >> >> >>");
        Set(catalog, $"<< /Type /Catalog /Pages {pagesObj} 0 R /AcroForm {acroForm} 0 R >>");
        Set(pagesObj, $"<< /Type /Pages /Kids [{string.Join(' ', pages.Select(n => $"{n} 0 R"))}] /Count {pageCount} >>");

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(sb.ToString()));
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        var xref = Encoding.Latin1.GetByteCount(sb.ToString());
        sb.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            sb.Append(offset.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Count + 1)
          .Append($" /Root {catalog} 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>The #2043 shifted font: Helvetica codes, a <c>/ToUnicode</c> reading each as the next code.</summary>
    private static string ShiftedFont(System.Func<string, int> add)
    {
        const int first = 32, last = 126;
        var widths = string.Join(' ', Enumerable.Repeat("500", last - first + 1));
        var cmap = "/CIDInit /ProcSet findresource begin 12 dict begin begincmap " +
                   "/CMapName /Odd def /CMapType 2 def 1 begincodespacerange <00> <FF> endcodespacerange " +
                   $"1 beginbfrange <{first:X2}> <{last:X2}> <{first + 1:X4}> endbfrange " +
                   "endcmap CMapName currentdict /CMap defineresource pop end end";
        var toUnicode = add(RawStream("", cmap));
        return $"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /FirstChar {first} /LastChar {last} " +
               $"/Widths [{widths}] /ToUnicode {toUnicode} 0 R >>";
    }

    private static string RectOf(double[] r) => $"[{F(r[0])} {F(r[1])} {F(r[2])} {F(r[3])}]";

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static string RawStream(string dict, string body)
        => $"<< {dict} /Length {Encoding.Latin1.GetByteCount(body)} >>\nstream\n{body}\nendstream";
}
