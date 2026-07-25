using System.IO;
using System.Linq;
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
/// Numbers inside RTL lines (#632, digit-island slice). Digits are bidi-WEAK:
/// inside an RTL line they render left-to-right (least-significant digit on
/// the left boundary of the number, "30" still reads "30") while the words
/// around them run right-to-left. A visual-order stream therefore carries the
/// LINE's segments in reverse logical order but each number's digits in
/// logical order.
///
/// Before this slice the reorderer terminated runs at digits, so each RTL
/// word came out logically ordered but the words' order ACROSS a number
/// stayed visual: logical "عمر 30 سنة" extracted as "سنة 30 عمر". Any
/// phrase needle spanning a number — name + ID number, date lines, phone
/// numbers, exactly the redaction-relevant content of Arabic/Hebrew
/// government documents — silently matched nothing, and RedactText
/// reported success (CLAUDE.md limitation #1 in its RTL form).
///
/// Expected strings are hand-derived from the Unicode Bidirectional
/// Algorithm (UAX #9: P2/P3 paragraph level from the first strong character;
/// W2 EN→AN after Arabic letters; W4 single common separator between digits
/// joins the number; I1/I2 number runs at even embedding level; L2 run
/// reversal) — the spec is the oracle, not excise. Corroborated against
/// mutool 1.27 (`mutool draw -F txt`) on these exact fixture bytes.
/// </summary>
public class RtlDigitIslandExtractionTests
{
    // Logical: "عمر 30 سنة" (age 30 years) — word, European number, word.
    private static readonly int[] MixedLogical =
        { 0x0639, 0x0645, 0x0631, 0x0020, '3', '0', 0x0020, 0x0633, 0x0646, 0x0629 };

    // Visual (left→right): [ةنس] [30] [رمع] — the trailing word's glyphs
    // leftmost and reversed, the number's digits in logical order, the
    // leading word's glyphs rightmost and reversed.
    private static readonly int[] MixedVisual =
        { 0x0629, 0x0646, 0x0633, 0x0020, '3', '0', 0x0020, 0x0631, 0x0645, 0x0639 };

    private static string S(int[] scalars) =>
        string.Concat(scalars.Select(char.ConvertFromUtf32));

    [Fact]
    public void ArabicWordsAroundNumber_VisualOrderLine_ExtractsLogicalOrder()
    {
        var pdf = RtlPdfFixtures.SingleTjScalarStream(MixedVisual);
        using var doc = PdfDocument.Open(pdf);

        var text = new TextExtractor(doc.GetPage(1)).ExtractText();

        text.Should().Be(S(MixedLogical),
            "segments of an RTL line reverse to logical order while each number keeps its digits in place");
    }

    [Fact]
    public void TrailingNumber_VisualOrderLine_ExtractsLogicalOrder()
    {
        // Logical "هاتف 123" (phone 123): the number is logically LAST, so in
        // visual order its digits are the FIRST glyphs of the stream — a
        // leading digit island that must join the RTL segment reversal.
        int[] logical = { 0x0647, 0x0627, 0x062A, 0x0641, 0x0020, '1', '2', '3' };
        int[] visual = { '1', '2', '3', 0x0020, 0x0641, 0x062A, 0x0627, 0x0647 };
        var pdf = RtlPdfFixtures.SingleTjScalarStream(visual);
        using var doc = PdfDocument.Open(pdf);

        new TextExtractor(doc.GetPage(1)).ExtractText().Should().Be(S(logical));
    }

    [Fact]
    public void DecimalNumber_VisualOrderLine_KeepsSeparatorInsideNumber()
    {
        // Logical "وزن 2.5" (weight 2.5). UBA W4: a single common separator
        // between two digits is part of the number — "2.5" must survive as
        // "2.5", not "5.2" or "2 . 5".
        int[] logical = { 0x0648, 0x0632, 0x0646, 0x0020, '2', '.', '5' };
        int[] visual = { '2', '.', '5', 0x0020, 0x0646, 0x0632, 0x0648 };
        var pdf = RtlPdfFixtures.SingleTjScalarStream(visual);
        using var doc = PdfDocument.Open(pdf);

        new TextExtractor(doc.GetPage(1)).ExtractText().Should().Be(S(logical));
    }

    [Fact]
    public void ArabicIndicDigits_VisualOrderLine_ExtractsLogicalOrder()
    {
        // Same line with Arabic-Indic digits (U+0660.., bidi class AN):
        // logical "عمر ٣٠ سنة". AN digits also render left-to-right inside
        // the RTL line, so the stream carries them in logical order too.
        int[] logical = { 0x0639, 0x0645, 0x0631, 0x0020, 0x0663, 0x0660, 0x0020, 0x0633, 0x0646, 0x0629 };
        int[] visual = { 0x0629, 0x0646, 0x0633, 0x0020, 0x0663, 0x0660, 0x0020, 0x0631, 0x0645, 0x0639 };
        var pdf = RtlPdfFixtures.SingleTjScalarStream(visual);
        using var doc = PdfDocument.Open(pdf);

        new TextExtractor(doc.GetPage(1)).ExtractText().Should().Be(S(logical));
    }

    [Fact]
    public void StrongLatinOnLine_DigitsStayWithTheLatinContext()
    {
        // Visual stream "abc 123" + reversed Arabic. With a strong-LTR
        // character on the line the paragraph is LTR (UBA P2/P3) and W7 binds
        // the digits to the Latin context: logical is "abc 123 سلام" — the
        // digits must NOT travel with the Arabic word. This is the guard that
        // digit-island joining only applies to RTL-directed lines.
        int[] visual = { 'a', 'b', 'c', 0x0020, '1', '2', '3', 0x0020, 0x0645, 0x0627, 0x0644, 0x0633 };
        var pdf = RtlPdfFixtures.SingleTjScalarStream(visual);
        using var doc = PdfDocument.Open(pdf);

        new TextExtractor(doc.GetPage(1)).ExtractText().Should().Be("abc 123 سلام");
    }

    [Fact]
    public void PureNumberLine_IsNeverTouched()
    {
        int[] visual = { '4', '2', '.', '5', '0' };
        var pdf = RtlPdfFixtures.SingleTjScalarStream(visual);
        using var doc = PdfDocument.Open(pdf);

        new TextExtractor(doc.GetPage(1)).ExtractText().Should().Be("42.50",
            "a line with no strong-RTL letter must never be reordered");
    }

    [Fact]
    public void LogicalOrderStream_DecreasingX_WithNumber_IsNotDisturbed()
    {
        // The other producer encoding: logical-order codes with every glyph
        // positioned explicitly. The Arabic word descends from the right; the
        // number sits to its left with its digits ascending locally (that is
        // how digits render inside an RTL line). Net descending X ⇒ already
        // logical ⇒ untouched.
        int[] logical = { 0x0639, 0x0645, 0x0631, '3', '0' };
        int[] xPositions = { 200, 188, 176, 140, 152 };
        var pdf = RtlPdfFixtures.PerGlyphAtPositions(logical, xPositions);
        using var doc = PdfDocument.Open(pdf);

        new TextExtractor(doc.GetPage(1)).ExtractText().Should().Be(S(logical));
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
    /// One Tj whose glyphs paint the given Unicode scalars left-to-right in
    /// exactly the given STREAM order (positive advances). Character codes
    /// 0x41.. are assigned positionally, so the stream's byte order — and
    /// nothing else — controls extraction order. Limited to 26 glyphs (codes
    /// 'A'..'Z') so the content-stream literal never needs escaping.
    /// </summary>
    public static byte[] SingleTjScalarStream(params int[] scalarsInStreamOrder)
    {
        var codes = Codes(scalarsInStreamOrder.Length);
        var content = $"BT /F1 24 Tf 100 700 Td ({new string(codes)}) Tj ET";
        return Build(content, scalarsInStreamOrder);
    }

    /// <summary>
    /// Logical-order scalars, one Tj per glyph, each positioned explicitly at
    /// the given X (Y fixed at 700). Models producers that write logical-order
    /// codes and place every glyph — including LTR-rendered digits inside an
    /// RTL line — at its visual position.
    /// </summary>
    public static byte[] PerGlyphAtPositions(int[] logicalScalars, int[] xPositions)
    {
        var codes = Codes(logicalScalars.Length);
        var sb = new StringBuilder("BT /F1 24 Tf");
        for (int i = 0; i < codes.Length; i++)
            sb.Append($" 1 0 0 1 {xPositions[i]} 700 Tm ({codes[i]}) Tj");
        sb.Append(" ET");
        return Build(sb.ToString(), logicalScalars);
    }

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
