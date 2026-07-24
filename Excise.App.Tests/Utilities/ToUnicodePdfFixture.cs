using System.IO;
using System.Text;

namespace Excise.App.Tests.Utilities;

/// <summary>
/// Minimal single-page PDF whose text is one Tj of character codes
/// 0x41.. ('A'..) with a /ToUnicode CMap mapping each code to an arbitrary
/// Unicode scalar. Lets search tests store EXACT code points — ligatures,
/// combining marks, harakat, zero-width characters, fullwidth forms — and
/// prove what extraction yields versus what a typed needle must match.
/// Same shape as <c>Excise.Core.Tests.Text.RtlPdfFixtures.SingleTj</c>.
/// </summary>
internal static class ToUnicodePdfFixture
{
    /// <summary>
    /// Build the PDF bytes for a page storing <paramref name="scalars"/> in
    /// logical order (codes 'A', 'B', … map to the scalars one-to-one).
    /// </summary>
    public static byte[] Build(int[] scalars)
    {
        var codes = new char[scalars.Length];
        for (int i = 0; i < scalars.Length; i++) codes[i] = (char)(0x41 + i);
        var content = $"BT /F1 24 Tf 100 700 Td ({new string(codes)}) Tj ET";

        var entries = new StringBuilder();
        for (int i = 0; i < scalars.Length; i++)
            entries.Append($"<{0x41 + i:X2}> <{scalars[i]:X4}>\n");

        var cmap =
            "/CIDInit /ProcSet findresource begin\n" +
            "12 dict begin\n" +
            "begincmap\n" +
            "/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n" +
            "/CMapName /Adobe-Identity-UCS def\n" +
            "/CMapType 2 def\n" +
            "1 begincodespacerange\n<00> <FF>\nendcodespacerange\n" +
            $"{scalars.Length} beginbfchar\n{entries}endbfchar\n" +
            "endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend";

        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, Encoding.Latin1, leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.7");
        writer.Flush();

        var offsets = new long[7];

        offsets[1] = Flush(writer, ms);
        writer.WriteLine("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj");

        offsets[2] = Flush(writer, ms);
        writer.WriteLine("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj");

        offsets[3] = Flush(writer, ms);
        writer.WriteLine("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                         "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>\nendobj");

        offsets[4] = Flush(writer, ms);
        writer.WriteLine($"4 0 obj\n<< /Length {content.Length} >>\nstream");
        writer.WriteLine(content);
        writer.WriteLine("endstream\nendobj");

        offsets[5] = Flush(writer, ms);
        writer.WriteLine("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica " +
                         "/FirstChar 32 /LastChar 127 /ToUnicode 6 0 R >>\nendobj");

        offsets[6] = Flush(writer, ms);
        writer.WriteLine($"6 0 obj\n<< /Length {cmap.Length} >>\nstream");
        writer.WriteLine(cmap);
        writer.WriteLine("endstream\nendobj");

        long xrefPos = Flush(writer, ms);
        writer.WriteLine("xref");
        writer.WriteLine("0 7");
        writer.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 6; i++)
            writer.WriteLine($"{offsets[i]:D10} 00000 n ");
        writer.WriteLine("trailer\n<< /Root 1 0 R /Size 7 >>\nstartxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    private static long Flush(StreamWriter writer, MemoryStream ms)
    {
        writer.Flush();
        return ms.Position;
    }
}
