using System.IO;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text;
using Xunit;

namespace Excise.Core.Tests.Text;

/// <summary>
/// RTL (Arabic/Hebrew) extraction must produce LOGICAL character order (#632).
///
/// PDF content streams usually carry RTL text in VISUAL order — glyphs emitted
/// left-to-right with positive advances, i.e. the byte sequence is the reverse
/// of the logical character order. Raw stream-order extraction therefore
/// yields reversed text, a user's logical-order search string never matches,
/// and <c>RedactText</c> silently removes nothing (verified: before the fix,
/// <c>excise redact</c> reported "Redacted 0 occurrence(s)" on the visual-order
/// fixture below while mutool still read the full word out of the output).
///
/// Oracle: mutool 1.27 (`mutool draw -F txt`) applied to these exact fixture
/// bytes returns the logical-order strings asserted here — for BOTH stream
/// orders — so these expectations are an independent tool's reading, not
/// excise checking its own homework.
/// </summary>
public class RtlTextExtractionTests
{
    // Logical order (first character = first letter a reader pronounces).
    private const string ArabicWord = "سلام"; // سلام
    private const string HebrewWord = "שלום"; // שלום

    private static readonly int[] ArabicScalars = { 0x0633, 0x0644, 0x0627, 0x0645 };
    private static readonly int[] HebrewScalars = { 0x05E9, 0x05DC, 0x05D5, 0x05DD };

    [Fact]
    public void VisualOrderStream_Arabic_ExtractsLogicalOrder()
    {
        // Codes reversed in the stream, painted left-to-right — how virtually
        // every producer encodes Arabic that displays correctly.
        var pdf = RtlPdfFixtures.SingleTj(ArabicScalars, visualOrder: true);
        using var doc = PdfDocument.Open(pdf);

        var text = new TextExtractor(doc.GetPage(1)).ExtractText();

        text.Should().Be(ArabicWord,
            "visual-order glyph runs must be reordered to logical order, matching mutool");
    }

    [Fact]
    public void VisualOrderStream_Hebrew_ExtractsLogicalOrder()
    {
        var pdf = RtlPdfFixtures.SingleTj(HebrewScalars, visualOrder: true);
        using var doc = PdfDocument.Open(pdf);

        var text = new TextExtractor(doc.GetPage(1)).ExtractText();

        text.Should().Be(HebrewWord);
    }

    [Fact]
    public void LogicalOrderStream_DecreasingX_Arabic_IsNotDisturbed()
    {
        // The other real-world encoding: logical-order codes, each glyph
        // positioned explicitly at DECREASING X. Already logical — the
        // reorderer must leave it alone.
        var pdf = RtlPdfFixtures.PerGlyphDecreasingX(ArabicScalars);
        using var doc = PdfDocument.Open(pdf);

        var text = new TextExtractor(doc.GetPage(1)).ExtractText();

        text.Should().Be(ArabicWord,
            "descending-X runs are already in logical order and must not be reversed");
    }

    [Fact]
    public void VisualOrderStream_Arabic_WordsComeOutLogical()
    {
        var pdf = RtlPdfFixtures.SingleTj(ArabicScalars, visualOrder: true);
        using var doc = PdfDocument.Open(pdf);

        var words = new TextExtractor(doc.GetPage(1)).ExtractWords();

        words.Should().ContainSingle().Which.Text.Should().Be(ArabicWord);
    }

    [Fact]
    public void MixedLatinAndRtl_OnlyTheRtlRunIsReordered()
    {
        // "abc" followed by the visual-order Arabic word in one Tj. The Latin
        // prefix must stay untouched; only the RTL run flips to logical.
        var pdf = RtlPdfFixtures.SingleTjWithLatinPrefix("abc", ArabicScalars);
        using var doc = PdfDocument.Open(pdf);

        var text = new TextExtractor(doc.GetPage(1)).ExtractText();

        text.Should().Be("abc" + ArabicWord);
    }
}

/// <summary>
/// Minimal RTL fixture PDFs with a deterministic /ToUnicode CMap: character
/// codes 0x41... ('A', 'B', ...) map to the given Unicode scalars in LOGICAL
/// order, so the stream's byte order fully controls extraction order and the
/// expected text is known exactly.
/// </summary>
internal static class RtlPdfFixtures
{
    /// <summary>
    /// One Tj painting the word left-to-right with positive advances.
    /// <paramref name="visualOrder"/> true = codes reversed (leftmost glyph is
    /// the LAST logical character — the common producer encoding);
    /// false = codes in logical order (renders mirrored; pathological).
    /// </summary>
    public static byte[] SingleTj(int[] logicalScalars, bool visualOrder)
    {
        var codes = Codes(logicalScalars.Length);
        if (visualOrder) System.Array.Reverse(codes);
        var content = $"BT /F1 24 Tf 100 700 Td ({new string(codes)}) Tj ET";
        return Build(content, logicalScalars);
    }

    /// <summary>
    /// Logical-order codes, one Tj per glyph, positioned at decreasing X so
    /// the word displays correctly right-to-left.
    /// </summary>
    public static byte[] PerGlyphDecreasingX(int[] logicalScalars)
    {
        var codes = Codes(logicalScalars.Length);
        var sb = new StringBuilder("BT /F1 24 Tf");
        for (int i = 0; i < codes.Length; i++)
            sb.Append($" 1 0 0 1 {200 - i * 12} 700 Tm ({codes[i]}) Tj");
        sb.Append(" ET");
        return Build(sb.ToString(), logicalScalars);
    }

    /// <summary>
    /// A Latin prefix followed by the visual-order RTL word, all in one Tj.
    /// Latin letters keep their 1:1 identity mapping in the CMap.
    /// </summary>
    public static byte[] SingleTjWithLatinPrefix(string latinPrefix, int[] logicalScalars)
    {
        var codes = Codes(logicalScalars.Length);
        System.Array.Reverse(codes);
        var content = $"BT /F1 24 Tf 100 700 Td ({latinPrefix}{new string(codes)}) Tj ET";

        // Extend the mapping so the prefix maps to itself. Prefix must not
        // collide with the 0x41.. code range used for the RTL scalars.
        var mapping = new StringBuilder();
        foreach (var ch in latinPrefix)
            mapping.Append($"<{(int)ch:X2}> <{(int)ch:X4}>\n");
        for (int i = 0; i < logicalScalars.Length; i++)
            mapping.Append($"<{0x41 + i:X2}> <{logicalScalars[i]:X4}>\n");
        return Build(content, logicalScalars, mapping.ToString(),
            latinPrefix.Length + logicalScalars.Length);
    }

    private static char[] Codes(int count)
    {
        var codes = new char[count];
        for (int i = 0; i < count; i++) codes[i] = (char)(0x41 + i);
        return codes;
    }

    private static byte[] Build(
        string content, int[] scalars, string? bfcharEntries = null, int? bfcharCount = null)
    {
        var entries = bfcharEntries;
        if (entries == null)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < scalars.Length; i++)
                sb.Append($"<{0x41 + i:X2}> <{scalars[i]:X4}>\n");
            entries = sb.ToString();
        }

        var cmap =
            "/CIDInit /ProcSet findresource begin\n" +
            "12 dict begin\n" +
            "begincmap\n" +
            "/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n" +
            "/CMapName /Adobe-Identity-UCS def\n" +
            "/CMapType 2 def\n" +
            "1 begincodespacerange\n<00> <FF>\nendcodespacerange\n" +
            $"{bfcharCount ?? scalars.Length} beginbfchar\n{entries}endbfchar\n" +
            "endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend";

        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, Encoding.Latin1, leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.7");
        writer.Flush();

        var offsets = new long[7];

        offsets[1] = Flush(writer, ms);
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        writer.WriteLine("endobj");

        offsets[2] = Flush(writer, ms);
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        writer.WriteLine("endobj");

        offsets[3] = Flush(writer, ms);
        writer.WriteLine("3 0 obj");
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                         "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>");
        writer.WriteLine("endobj");

        offsets[4] = Flush(writer, ms);
        writer.WriteLine("4 0 obj");
        writer.WriteLine($"<< /Length {content.Length} >>");
        writer.WriteLine("stream");
        writer.WriteLine(content);
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");

        offsets[5] = Flush(writer, ms);
        writer.WriteLine("5 0 obj");
        writer.WriteLine("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica " +
                         "/FirstChar 32 /LastChar 127 /ToUnicode 6 0 R >>");
        writer.WriteLine("endobj");

        offsets[6] = Flush(writer, ms);
        writer.WriteLine("6 0 obj");
        writer.WriteLine($"<< /Length {cmap.Length} >>");
        writer.WriteLine("stream");
        writer.WriteLine(cmap);
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");

        long xrefPos = Flush(writer, ms);
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

    private static long Flush(StreamWriter writer, MemoryStream ms)
    {
        writer.Flush();
        return ms.Position;
    }
}
