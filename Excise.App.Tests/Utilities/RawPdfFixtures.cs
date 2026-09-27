using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Excise.App.Tests.Utilities;

/// <summary>
/// Minimal raw-PDF builders for GUI copy-quality tests (#1203, #1204) that need
/// real, positioned glyphs for non-Latin scripts. <c>PdfFont.Helvetica</c> +
/// <c>DrawString</c> (<see cref="TestPdfGenerator"/>) only handles Latin text,
/// so these write the content stream directly — the same technique already
/// used by <c>Excise.Avalonia.Tests.RtlFixtures</c> (#373) and
/// <c>VerticalWritingMetricsTests.BuildType0Pdf</c> (#515): a Type1/Type0 font
/// with no embedded outlines, a <c>/ToUnicode</c> CMap driving extraction, and
/// widths taken from the Standard-14 Helvetica metrics (real outlines are
/// irrelevant — these tests assert selection/copy geometry and logical text,
/// not glyph rendering). Each helper here is a per-project copy rather than a
/// shared reference because the existing builders are <c>internal</c>/private
/// to test assemblies that do not expose them to <c>Excise.App.Tests</c>.
/// </summary>
internal static class RawPdfFixtures
{
    /// <summary>
    /// One line of text, placed at PDF content-space <paramref name="X"/>/<paramref name="Y"/>
    /// (bottom-left origin, matching <c>graphics.DrawString</c>). <paramref name="Text"/> is
    /// LOGICAL — the order a human reads it, exactly what the test's expected string should
    /// be. When <paramref name="Rtl"/> is true the WHOLE line's codes are placed in mirrored
    /// (visual) order, matching how a real RTL producer paints a purely-RTL run as it advances
    /// left-to-right in the content stream while script direction runs right-to-left — this is
    /// what excise's extractor expects to see and un-mirror (#632).
    ///
    /// <paramref name="RtlRunStart"/> is for a mixed line: characters before this index are an
    /// LTR prefix placed as-is (already visual = logical), and characters from this index to
    /// the end are a trailing RTL run mirrored independently — exactly
    /// <c>Excise.Avalonia.Tests.RtlFixtures.SingleTjWithLatinPrefix</c>'s shape. Mirroring the
    /// WHOLE line here (as <paramref name="Rtl"/> does) would be wrong for a mixed line: a real
    /// producer places each direction run in its own visual order, not the paragraph's logical
    /// order reversed end-to-end.
    /// </summary>
    internal readonly record struct Line(string Text, double X, double Y, bool Rtl = false, int? RtlRunStart = null);

    /// <summary>
    /// Single-page PDF, one non-embedded Type1 font (Helvetica metrics) shared
    /// by every line, one page-global <c>/ToUnicode</c> CMap. Every DISTINCT
    /// character used anywhere on the page gets one stable code in
    /// <c>0x20..0xFE</c> (0x20 is always plain space); a repeated character
    /// reuses its code, so multiple lines can share vocabulary.
    /// </summary>
    internal static string WriteSimpleFontPdf(string outputPath, params Line[] lines)
    {
        var codeOf = new Dictionary<char, byte>();
        byte next = 0x21; // keep 0x20 reserved for plain space
        byte CodeFor(char ch)
        {
            if (ch == ' ') return 0x20;
            if (codeOf.TryGetValue(ch, out var c)) return c;
            if (next == 0x7F) next = 0xA1; // skip DEL..0xA0 (kept clear of control/NBSP quirks)
            var code = next++;
            codeOf[ch] = code;
            return code;
        }

        var content = new StringBuilder();
        foreach (var line in lines)
        {
            var chars = line.Text.ToCharArray();
            var codes = chars.Select(CodeFor).ToArray();
            if (line.Rtl)
            {
                Array.Reverse(codes);
            }
            else if (line.RtlRunStart is int cut)
            {
                Array.Reverse(codes, cut, codes.Length - cut);
            }
            var hex = string.Concat(codes.Select(c => c.ToString("X2")));
            content.Append($"BT /F1 24 Tf {line.X.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
                            $"{line.Y.ToString(System.Globalization.CultureInfo.InvariantCulture)} Td <{hex}> Tj ET\n");
        }

        var bfchar = new StringBuilder();
        foreach (var kv in codeOf)
            bfchar.Append($"<{kv.Value:X2}> <{(int)kv.Key:X4}>\n");
        bfchar.Append("<20> <0020>\n");

        var pdf = BuildSimpleFontPdf(content.ToString(), bfchar.ToString(), codeOf.Count + 1);
        File.WriteAllBytes(outputPath, pdf);
        return outputPath;
    }

    private static byte[] BuildSimpleFontPdf(string content, string bfcharEntries, int bfcharCount)
    {
        var cmap =
            "/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n" +
            "/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n" +
            "/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n" +
            "1 begincodespacerange\n<00> <FF>\nendcodespacerange\n" +
            $"{bfcharCount} beginbfchar\n{bfcharEntries}endbfchar\n" +
            "endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend";

        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, Encoding.Latin1, leaveOpen: true) { NewLine = "\n" };
        writer.WriteLine("%PDF-1.7");
        var offsets = new long[7];
        long Flush() { writer.Flush(); return ms.Position; }

        offsets[1] = Flush();
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        writer.WriteLine("endobj");

        offsets[2] = Flush();
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        writer.WriteLine("endobj");

        offsets[3] = Flush();
        writer.WriteLine("3 0 obj");
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                          "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>");
        writer.WriteLine("endobj");

        offsets[4] = Flush();
        writer.WriteLine("4 0 obj");
        writer.WriteLine($"<< /Length {content.Length} >>");
        writer.WriteLine("stream");
        writer.WriteLine(content);
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");

        offsets[5] = Flush();
        writer.WriteLine("5 0 obj");
        writer.WriteLine("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica " +
                          "/FirstChar 32 /LastChar 254 /ToUnicode 6 0 R >>");
        writer.WriteLine("endobj");

        offsets[6] = Flush();
        writer.WriteLine("6 0 obj");
        writer.WriteLine($"<< /Length {cmap.Length} >>");
        writer.WriteLine("stream");
        writer.WriteLine(cmap);
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");

        long xrefPos = Flush();
        writer.WriteLine("xref");
        writer.WriteLine("0 7");
        writer.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 6; i++)
            writer.WriteLine($"{offsets[i]:D10} 00000 n ");
        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Size 7 >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// One vertical (Identity-V) column of CJK characters, painted top-to-bottom
    /// starting at <paramref name="x"/>/<paramref name="y"/> with no <c>/W2</c>
    /// override — the spec default <c>/DW2 [880 -1000]</c> advances one em down
    /// per glyph (ISO 32000-2 §9.7.4.3, pinned by
    /// <c>VerticalWritingMetricsTests.IdentityV_DefaultMetrics_AdvanceOneEmDown</c>).
    /// <paramref name="text"/> is LOGICAL (top-to-bottom reading order); codes are
    /// each character's UTF-16BE code unit (BMP-only, which covers every CJK
    /// Unified Ideograph and Hangul syllable used here), decoded back to the same
    /// characters via <c>/ToUnicode /Identity-H</c> (#716) so extraction does not
    /// depend on the non-embedded-font Unicode heuristic.
    /// </summary>
    internal readonly record struct VerticalColumn(string Text, double X, double Y);

    internal static string WriteIdentityVPdf(string outputPath, params VerticalColumn[] columns)
    {
        var content = new StringBuilder();
        foreach (var col in columns)
        {
            var hex = string.Concat(col.Text.Select(c => ((int)c).ToString("X4")));
            content.Append($"BT /F0 24 Tf {col.X.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
                            $"{col.Y.ToString(System.Globalization.CultureInfo.InvariantCulture)} Td <{hex}> Tj ET\n");
        }

        var pdf = BuildType0Pdf(content.ToString());
        File.WriteAllBytes(outputPath, pdf);
        return outputPath;
    }

    private static byte[] BuildType0Pdf(string content)
    {
        var sb = new StringBuilder();
        var offsets = new long[6];
        void Obj(int n) => offsets[n] = sb.Length;

        sb.Append("%PDF-1.7\n");
        Obj(1); sb.Append("1 0 obj <</Type/Catalog/Pages 2 0 R>> endobj\n");
        Obj(2); sb.Append("2 0 obj <</Type/Pages/Count 1/Kids[3 0 R]>> endobj\n");
        Obj(3); sb.Append("3 0 obj <</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]" +
                          "/Resources<</Font<</F0 4 0 R>>>>/Contents 5 0 R>> endobj\n");
        Obj(4); sb.Append("4 0 obj <</Type/Font/Subtype/Type0/BaseFont/Test" +
                          "/Encoding/Identity-V/ToUnicode/Identity-H" +
                          "/DescendantFonts[<</Type/Font/Subtype/CIDFontType2/BaseFont/Test" +
                          "/CIDSystemInfo<</Registry(Adobe)/Ordering(Identity)/Supplement 0>>" +
                          "/DW 1000>>]>> endobj\n");
        Obj(5); sb.Append($"5 0 obj <</Length {content.Length}>>\nstream\n{content}\nendstream endobj\n");

        var xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        for (int i = 1; i <= 5; i++)
            sb.Append(offsets[i].ToString("D10") + " 00000 n \n");
        sb.Append($"trailer <</Size 6/Root 1 0 R>>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
