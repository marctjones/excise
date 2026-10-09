using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Excise.TestSupport;

/// <summary>
/// #2039: AcroForm widgets whose appearance DRAWS text that differs from, or is placed differently
/// from, the field's <c>/V</c>, as Acrobat (and excise's own XFA S1 writer) leaves them. One page,
/// Helvetica, uncompressed streams, no <c>/NeedAppearances</c> (so every reader draws the
/// appearance as written).
/// </summary>
internal static class WidgetDisplayTextFixtures
{
    /// <summary>A combo box: <c>/V</c> is the save value, the appearance draws the display text.</summary>
    public const string ChoiceSave = "CAN";
    public const string ChoiceDisplay = "Canada";
    public const string OtherSave = "FRA";
    public const string OtherDisplay = "France";
    public static readonly double[] ChoiceRect = [100, 600, 300, 620];

    /// <summary>
    /// Where the appearance's first glyph lands on the page by ISO 32000-2 12.5.5 Algorithm 8.1:
    /// form point (2, 1.5) through <c>/Matrix [2 0 0 2 0 0]</c> is (4, 3); the transformed
    /// <c>/BBox</c> [0 0 100 10] maps onto the 200 x 20 <c>/Rect</c> by scale 2 and translate
    /// (100, 600): (108, 606). The order matters: A x Matrix would put it at (208, 1206).
    /// </summary>
    public const double ChoiceGlyphX = 108, ChoiceGlyphY = 606;

    /// <summary>The effective size: 3 Tf through both scales of 2.</summary>
    public const double ChoiceFontSize = 12;

    /// <summary>A text field whose appearance draws its <c>/V</c> centred, not at the left edge.</summary>
    public const string TextValue = "Ada Lovelace";
    public static readonly double[] TextRect = [100, 700, 400, 720];
    public const double TextGlyphX = 210, TextGlyphY = 706;

    /// <summary>A comb field: one glyph per cell, 50 pt apart.</summary>
    public const string CombValue = "AB12";
    public static readonly double[] CombRect = [100, 500, 300, 520];

    /// <summary>A text field whose appearance draws nothing: the value is read from <c>/V</c>.</summary>
    public const string FallbackValue = "Fallback Value";
    public static readonly double[] FallbackRect = [100, 400, 300, 420];

    /// <summary>
    /// The page content ends with <c>3 Tr 50 Tz</c> outside any <c>q … Q</c>: text state the
    /// appearance streams do not set. An appearance starts from the default state (ISO 32000-2
    /// 12.5.5, 8.4.1 Table 51 and 9.3.1 Table 102: Tr 0, Tz 100), not from what the page left.
    /// </summary>
    public const string PageText = "Widget display fixture";

    public static byte[] Build()
    {
        var objects = new List<string>();
        int Add(string body) { objects.Add(body); return objects.Count; }
        int Reserve() { objects.Add(""); return objects.Count; }
        void Set(int number, string body) => objects[number - 1] = body;

        var catalog = Reserve();
        var pages = Reserve();
        var page = Reserve();
        var helv = Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        var contents = Add(RawStream("", $"BT /Helv 10 Tf 20 760 Td ({PageText}) Tj ET 3 Tr 50 Tz"));
        var fonts = $"/Resources << /Font << /Helv {helv} 0 R >> >>";

        int Appearance(string bbox, string matrix, string body)
            => Add(RawStream($"/Type /XObject /Subtype /Form /BBox {bbox} {matrix} {fonts}", body));

        var annots = new List<int>();
        int Widget(string extra, double[] rect, int ap)
        {
            var n = Add($"<< /Type /Annot /Subtype /Widget /F 4 /Rect {RectOf(rect)} /P {page} 0 R {extra} /AP << /N {ap} 0 R >> >>");
            annots.Add(n);
            return n;
        }

        Widget($"/FT /Ch /T (Country) /Ff 131072 /Opt [[({ChoiceSave}) ({ChoiceDisplay})] [({OtherSave}) ({OtherDisplay})]] /V ({ChoiceSave}) /DA (/Helv 0 Tf 0 g)",
            ChoiceRect,
            Appearance("[0 0 50 5]", "/Matrix [2 0 0 2 0 0]", $"/Tx BMC BT /Helv 3 Tf 0 g 2 1.5 Td ({ChoiceDisplay}) Tj ET EMC"));
        Widget($"/FT /Tx /T (Name) /Q 1 /V ({TextValue}) /DA (/Helv 12 Tf 0 g)",
            TextRect,
            Appearance("[0 0 300 20]", "", $"/Tx BMC BT /Helv 12 Tf 0 g 110 6 Td ({TextValue}) Tj ET EMC"));
        var comb = new StringBuilder("/Tx BMC BT /Helv 12 Tf 0 g 20 6 Td ");
        for (int i = 0; i < CombValue.Length; i++)
            comb.Append(i == 0 ? "" : "50 0 Td ").Append('(').Append(CombValue[i]).Append(") Tj ");
        comb.Append("ET EMC");
        Widget($"/FT /Tx /T (Code) /Ff 16777216 /MaxLen 4 /V ({CombValue}) /DA (/Helv 12 Tf 0 g)",
            CombRect,
            Appearance("[0 0 200 20]", "", comb.ToString()));
        Widget($"/FT /Tx /T (Fallback) /V ({FallbackValue}) /DA (/Helv 12 Tf 0 g)",
            FallbackRect,
            Appearance("[0 0 200 20]", "", "/Tx BMC EMC"));

        var acroForm = Add($"<< /Fields [{string.Join(' ', annots.Select(n => $"{n} 0 R"))}] /DA (/Helv 0 Tf 0 g) " +
                           $"/DR << /Font << /Helv {helv} 0 R >> >> >>");
        Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R /AcroForm {acroForm} 0 R >>");
        Set(pages, $"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
        Set(page, $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] /Contents {contents} 0 R " +
                  $"{fonts} /Annots [{string.Join(' ', annots.Select(n => $"{n} 0 R"))}] >>");

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

    private static string RectOf(double[] r) => $"[{F(r[0])} {F(r[1])} {F(r[2])} {F(r[3])}]";

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static string RawStream(string dict, string body)
        => $"<< {dict} /Length {Encoding.Latin1.GetByteCount(body)} >>\nstream\n{body}\nendstream";
}
