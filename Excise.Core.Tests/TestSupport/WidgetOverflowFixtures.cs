using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Excise.TestSupport;

/// <summary>
/// #2041: a scrolled multiline text field. Its appearance draws more lines than its <c>/Rect</c>
/// holds: the last two land below the widget, outside the <c>/BBox</c> and the <c>re W n</c> clip,
/// so no reader paints them, yet the file holds them and search finds them. The term is ONLY in
/// those clipped lines (and in <c>/V</c>, which restates the whole text). A second text field sits
/// exactly where the clipped lines land, so a scrub that picks fields by rectangle selects the
/// wrong one. One page, Helvetica, uncompressed streams, no <c>/NeedAppearances</c>.
/// </summary>
internal static class WidgetOverflowFixtures
{
    public const string Term = "Zanzibar";

    /// <summary>The clipped lines hold the term this many times.</summary>
    public const int TermOccurrences = 2;

    /// <summary>Lines the multiline field shows (inside its <c>/BBox</c>): they must survive.</summary>
    public const string VisibleLine = "Visible first line";
    public const string SecondLine = "Visible second line";

    /// <summary>The neighbouring field's value, drawn by its own appearance: it must survive.</summary>
    public const string NeighbourValue = "Keep this value";

    public static readonly double[] NotesRect = [100, 600, 400, 640];

    /// <summary>
    /// Where the clipped lines land: baselines at form y -12 and -28, page y 588 and 572 under the
    /// identity mapping (<c>/BBox</c> [0 0 300 40] onto the 300 x 40 <c>/Rect</c>). Their 10 pt
    /// glyph boxes reach at most page y 598, below the field's <c>/Rect</c> (bottom 600), and
    /// overlap this neighbour.
    /// </summary>
    public static readonly double[] NeighbourRect = [100, 566, 400, 597];

    public enum TermEncoding
    {
        /// <summary>Each line a literal string, <c>(…) Tj</c>.</summary>
        LiteralTj,

        /// <summary>The term split across a kerned <c>TJ</c> array.</summary>
        KernedTJ,

        /// <summary>The term as a hex string, <c>&lt;…&gt; Tj</c>.</summary>
        HexTj,
    }

    public static string NotesValue =>
        $"{VisibleLine}\r{SecondLine}\rThird line half shown\rOverflow {Term} one\r{Term} again two";

    public static byte[] Build(TermEncoding encoding = TermEncoding.LiteralTj)
    {
        string Show(string before, string after) => encoding switch
        {
            TermEncoding.LiteralTj => $"({before}{Term}{after}) Tj",
            TermEncoding.KernedTJ => $"[({before}Zan) -15 (zi) 20 (bar{after})] TJ",
            _ => $"<{Hex(before + Term + after)}> Tj",
        };

        var notes =
            "/Tx BMC q 1 1 298 38 re W n BT /Helv 10 Tf 0 g 2 28 Td " +
            $"({VisibleLine}) Tj 0 -12 Td ({SecondLine}) Tj 0 -12 Td (Third line half shown) Tj " +
            $"0 -16 Td {Show("Overflow ", " one")} 0 -16 Td {Show("", " again two")} ET Q EMC";

        return Assemble((appearance, widget) =>
        {
            widget($"/FT /Tx /T (Notes) /Ff 4096 /V ({NotesValue.Replace("\r", "\\r")}) /DA (/Helv 10 Tf 0 g)",
                NotesRect, appearance("[0 0 300 40]", notes));
            widget($"/FT /Tx /T (Keep) /V ({NeighbourValue}) /DA (/Helv 10 Tf 0 g)",
                NeighbourRect, appearance("[0 0 300 24]", $"/Tx BMC BT /Helv 10 Tf 0 g 2 8 Td ({NeighbourValue}) Tj ET EMC"));
        });
    }

    /// <summary>
    /// Two text fields whose widgets share ONE appearance stream (each widget has its own
    /// <c>/AP</c> dictionary, both <c>/N</c> point at the same object), drawing the term, and a
    /// third field with its own appearance that does not.
    /// </summary>
    public const string SharedValue = $"Shared {Term} note";
    public const string SharedSurvivor = "Shared";
    public static readonly double[] AlphaRect = [100, 700, 300, 720];
    public static readonly double[] BetaRect = [100, 650, 300, 670];

    public static byte[] BuildShared()
        => Assemble((appearance, widget) =>
        {
            var shared = appearance("[0 0 200 20]", $"/Tx BMC BT /Helv 10 Tf 0 g 2 6 Td ({SharedValue}) Tj ET EMC");
            widget($"/FT /Tx /T (Alpha) /V ({SharedValue}) /DA (/Helv 10 Tf 0 g)", AlphaRect, shared);
            widget($"/FT /Tx /T (Beta) /V ({SharedValue}) /DA (/Helv 10 Tf 0 g)", BetaRect, shared);
            widget($"/FT /Tx /T (Keep) /V ({NeighbourValue}) /DA (/Helv 10 Tf 0 g)",
                NeighbourRect, appearance("[0 0 300 24]", $"/Tx BMC BT /Helv 10 Tf 0 g 2 8 Td ({NeighbourValue}) Tj ET EMC"));
        });

    /// <summary>
    /// A text field whose normal appearance draws <see cref="SharedValue"/> and whose DOWN
    /// appearance (<c>/AP /D</c>, drawn while the widget is pressed) draws <see cref="DownText"/>:
    /// the term is in both streams.
    /// </summary>
    public const string DownText = $"Down {Term} state";

    public static byte[] BuildWithDownAppearance()
        => Assemble((appearance, widget) =>
        {
            var normal = appearance("[0 0 200 20]", $"/Tx BMC BT /Helv 10 Tf 0 g 2 6 Td ({SharedValue}) Tj ET EMC");
            var down = appearance("[0 0 200 20]", $"/Tx BMC BT /Helv 10 Tf 0 g 2 6 Td ({DownText}) Tj ET EMC");
            widget($"/FT /Tx /T (Alpha) /V ({SharedValue}) /DA (/Helv 10 Tf 0 g)", AlphaRect, normal, $"/D {down} 0 R ");
        });

    private delegate int AppearanceFactory(string bbox, string body);

    private delegate int WidgetFactory(string extra, double[] rect, int appearance, string apExtra = "");

    private static byte[] Assemble(System.Action<AppearanceFactory, WidgetFactory> build)
    {
        var objects = new List<string>();
        int Add(string body) { objects.Add(body); return objects.Count; }
        int Reserve() { objects.Add(""); return objects.Count; }
        void Set(int number, string body) => objects[number - 1] = body;

        var catalog = Reserve();
        var pages = Reserve();
        var page = Reserve();
        var helv = Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        var contents = Add(RawStream("", "BT /Helv 12 Tf 20 760 Td (Widget overflow fixture) Tj ET"));
        var fonts = $"/Resources << /Font << /Helv {helv} 0 R >> >>";

        var annots = new List<int>();
        build(
            (bbox, body) => Add(RawStream($"/Type /XObject /Subtype /Form /BBox {bbox} {fonts}", body)),
            (extra, rect, ap, apExtra) =>
            {
                var n = Add($"<< /Type /Annot /Subtype /Widget /F 4 /Rect {RectOf(rect)} /P {page} 0 R {extra} /AP << /N {ap} 0 R {apExtra}>> >>");
                annots.Add(n);
                return n;
            });

        var refs = string.Join(' ', annots.Select(n => $"{n} 0 R"));
        var acroForm = Add($"<< /Fields [{refs}] /DA (/Helv 0 Tf 0 g) /DR << /Font << /Helv {helv} 0 R >> >> >>");
        Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R /AcroForm {acroForm} 0 R >>");
        Set(pages, $"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
        Set(page, $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] /Contents {contents} 0 R {fonts} /Annots [{refs}] >>");

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(System.Text.Encoding.Latin1.GetByteCount(sb.ToString()));
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        var xref = System.Text.Encoding.Latin1.GetByteCount(sb.ToString());
        sb.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            sb.Append(offset.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Count + 1)
          .Append($" /Root {catalog} 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return System.Text.Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static string Hex(string text)
        => string.Concat(System.Text.Encoding.Latin1.GetBytes(text).Select(b => b.ToString("X2")));

    private static string RectOf(double[] r) => $"[{F(r[0])} {F(r[1])} {F(r[2])} {F(r[3])}]";

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static string RawStream(string dict, string body)
        => $"<< {dict} /Length {System.Text.Encoding.Latin1.GetByteCount(body)} >>\nstream\n{body}\nendstream";
}
