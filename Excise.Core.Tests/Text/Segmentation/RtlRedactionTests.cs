using System.IO;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Tests.Text;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// Redacting an RTL (Arabic/Hebrew) word given in LOGICAL order must remove
/// it regardless of how the content stream ordered the glyphs (#632).
///
/// Before the bidi reorder in <c>TextExtractor</c>, the visual-order fixtures
/// here reproduced a silent redaction failure: <c>RedactText("سلام")</c>
/// returned 0, reported success, and mutool still read the full word out of
/// the "redacted" file — CLAUDE.md limitation #1 (excise cannot redact what
/// excise cannot read, and it reports success anyway) in its RTL form.
///
/// Assertions follow the redaction test rules: the saved BYTES are searched
/// (ASCII and UTF-16BE), in BOTH logical and visual orders, plus the raw
/// character-code carrier the fixture is known to use — so a leak in any
/// text carrier of the written file fails the test, not just one that
/// excise's own extractor can see.
/// </summary>
public class RtlRedactionTests
{
    private const string ArabicWord = "سلام";
    private const string HebrewWord = "שלום";

    /// <summary>
    /// Pin the trailer /ID so SaveToBytes is deterministic. Without an
    /// existing /ID the writer mints a random 16-byte one serialized as
    /// UPPERCASE HEX — random A–F runs that can coincidentally contain the
    /// short letter needles these byte-checks assert against ("DEF",
    /// "ABCD", "DCBA"), failing the suite spuriously (~1% of runs). Same
    /// byte-check-collision family as the float-noise coordinates of #762.
    /// All-zero ID bytes serialize as "000…0", which contains no letters.
    /// </summary>
    internal static void PinDeterministicId(PdfDocument doc)
    {
        var zeroId = new Excise.Core.Primitives.PdfString(new byte[16], isHex: true);
        doc.Trailer["ID"] = new Excise.Core.Primitives.PdfArray(zeroId, zeroId);
    }

    private static readonly int[] ArabicScalars = { 0x0633, 0x0644, 0x0627, 0x0645 };
    private static readonly int[] HebrewScalars = { 0x05E9, 0x05DC, 0x05D5, 0x05DD };

    [Fact]
    public void RedactText_LogicalNeedle_RemovesVisualOrderArabicWord()
    {
        var pdf = RtlPdfFixtures.SingleTj(ArabicScalars, visualOrder: true);
        using var doc = PdfDocument.Open(pdf);
        RtlRedactionTests.PinDeterministicId(doc);

        // Sanity: the unredacted document must carry the word's glyph codes in
        // the saved bytes, or the "gone afterwards" assertions below prove
        // nothing. The fixture encodes the word as codes 'DCBA' (visual order).
        SearchableTextOf(doc.SaveToBytes()).Should().Contain("DCBA",
            "sanity: the carrier must be present before redaction for its absence after to mean anything");

        var removed = doc.RedactText(ArabicWord);

        removed.Should().BeGreaterThan(0,
            "a logical-order needle must match a visual-order glyph run; " +
            "0 matches is the silent-failure mode this test exists to prevent");

        var saved = doc.SaveToBytes();
        var searchable = SearchableTextOf(saved);
        searchable.Should().NotContain(ArabicWord, "the word must not survive in logical order");
        searchable.Should().NotContain(Reverse(ArabicWord), "nor in visual order");
        searchable.Should().NotContain("DCBA", "nor as its raw character codes");
        searchable.Should().NotContain("ABCD", "nor as reversed character codes");

        using var reopened = PdfDocument.Open(saved);
        reopened.GetPage(1).Text.Should().NotContainAny(ArabicWord, Reverse(ArabicWord));
    }

    [Fact]
    public void RedactText_LogicalNeedle_RemovesVisualOrderHebrewWord()
    {
        var pdf = RtlPdfFixtures.SingleTj(HebrewScalars, visualOrder: true);
        using var doc = PdfDocument.Open(pdf);
        RtlRedactionTests.PinDeterministicId(doc);

        var removed = doc.RedactText(HebrewWord);

        removed.Should().BeGreaterThan(0);

        var searchable = SearchableTextOf(doc.SaveToBytes());
        searchable.Should().NotContain(HebrewWord);
        searchable.Should().NotContain(Reverse(HebrewWord));
        searchable.Should().NotContain("DCBA");
        searchable.Should().NotContain("ABCD");
    }

    [Fact]
    public void RedactText_LogicalNeedle_RemovesLogicalOrderDecreasingXWord()
    {
        // The other producer encoding: logical-order codes positioned at
        // decreasing X. Extraction never reverses these, so this guards
        // against the fix breaking the already-correct path.
        var pdf = RtlPdfFixtures.PerGlyphDecreasingX(ArabicScalars);
        using var doc = PdfDocument.Open(pdf);
        RtlRedactionTests.PinDeterministicId(doc);

        var removed = doc.RedactText(ArabicWord);

        removed.Should().BeGreaterThan(0);

        var searchable = SearchableTextOf(doc.SaveToBytes());
        searchable.Should().NotContain(ArabicWord);
        searchable.Should().NotContain(Reverse(ArabicWord));
    }

    /// <summary>
    /// Carrier-agnostic view of the saved file, per the redaction test rules:
    /// ASCII (name-tree strings, raw codes, hex-encoded carriers stay visible)
    /// concatenated with UTF-16BE (how PDF text strings carry Unicode) and
    /// UTF-8 (metadata streams).
    /// </summary>
    private static string SearchableTextOf(byte[] saved) =>
        // #1049: the shared scanner also searches INSIDE /FlateDecode streams.
        // The hand-rolled encoding concatenation this replaced could not, and
        // excise compresses on save — that blindness declared #1040's leaking
        // output clean. SavedPdfLeakScannerTests proves it.
        Excise.Core.Tests.Text.Segmentation.SavedPdfLeakScanner.AllCarriersText(saved);

    private static string Reverse(string s)
    {
        var chars = s.ToCharArray();
        System.Array.Reverse(chars);
        return new string(chars);
    }
}

/// <summary>
/// RedactText on phrases that SPAN A NUMBER inside an RTL line (#632,
/// digit-island slice) — the ID-line / date-line / phone-number case that is
/// the redaction-relevant content of Arabic and Hebrew government documents.
///
/// Before the digit-island reorder, each RTL word extracted logically but the
/// word order ACROSS the number stayed visual ("عمر 30 سنة" extracted as
/// "سنة 30 عمر"), so a logical-order phrase needle matched nothing and
/// RedactText returned 0 while reporting success — the silent-failure mode
/// this suite exists to prevent.
///
/// Assertions follow the redaction test rules: the saved BYTES are searched
/// (ASCII, UTF-16BE and UTF-8) for the phrase in logical order, fully
/// reversed visual order, per-run reversed order, and the fixture's known
/// raw character-code carrier.
/// </summary>
public class RtlDigitIslandRedactionTests
{
    // Logical "عمر 30 سنة"; the fixture stores it in visual order (see
    // RtlDigitIslandExtractionTests for the UBA derivation). Stream codes are
    // 'A'..'J' assigned positionally, so the raw-code carrier of the full
    // line is "ABCDEFGHIJ".
    private static readonly int[] MixedVisual =
        { 0x0629, 0x0646, 0x0633, 0x0020, '3', '0', 0x0020, 0x0631, 0x0645, 0x0639 };

    private const string LogicalPhrase = "عمر 30 سنة";

    [Fact]
    public void RedactText_LogicalPhraseSpanningNumber_RemovesVisualOrderLine()
    {
        var pdf = RtlPdfFixtures.SingleTjScalarStream(MixedVisual);
        using var doc = PdfDocument.Open(pdf);
        RtlRedactionTests.PinDeterministicId(doc);

        // Sanity: extraction must read the phrase logically, and the raw-code
        // carrier must be present, or the absence assertions prove nothing.
        doc.GetPage(1).Text.Should().Contain(LogicalPhrase);
        SearchableTextOf(doc.SaveToBytes()).Should().Contain("ABCDEFGHIJ");

        var removed = doc.RedactText(LogicalPhrase);

        removed.Should().BeGreaterThan(0,
            "a logical-order phrase spanning a number must match the visual-order line; " +
            "0 matches is the silent-failure mode this test exists to prevent");

        var searchable = SearchableTextOf(doc.SaveToBytes());
        searchable.Should().NotContain(LogicalPhrase, "the phrase must not survive in logical order");
        searchable.Should().NotContain(ReverseString(LogicalPhrase), "nor fully reversed");
        searchable.Should().NotContainAny("سنة", "عمر", "ةنس", "رمع");
        searchable.Should().NotContain("ABCDEFGHIJ", "nor as its raw character codes");
        searchable.Should().NotContain("JIHGFEDCBA");

        using var reopened = PdfDocument.Open(doc.SaveToBytes());
        reopened.GetPage(1).Text.Should().NotContainAny(LogicalPhrase, ReverseString(LogicalPhrase));
    }

    [Fact]
    public void RedactText_NumberOnly_RemovesTheNumberAndKeepsTheWords()
    {
        var pdf = RtlPdfFixtures.SingleTjScalarStream(MixedVisual);
        using var doc = PdfDocument.Open(pdf);
        RtlRedactionTests.PinDeterministicId(doc);

        // The number's stream codes are 'E' ('3') and 'F' ('0').
        SearchableTextOf(doc.SaveToBytes()).Should().Contain("DEF");

        var removed = doc.RedactText("30");

        removed.Should().BeGreaterThan(0);

        var searchable = SearchableTextOf(doc.SaveToBytes());
        searchable.Should().NotContain("DEF",
            "the number's raw character codes must be gone from the saved bytes");

        using var reopened = PdfDocument.Open(doc.SaveToBytes());
        reopened.GetPage(1).Text.Should().NotContain("30");
    }

    [Fact]
    public void RedactText_HebrewPhraseWithTrailingNumber_RemovesVisualOrderLine()
    {
        // Logical "טלפון 123" (phone 123): trailing number ⇒ the digits are
        // the FIRST glyphs of the visual stream (leading digit island).
        int[] visual = { '1', '2', '3', 0x0020, 0x05DF, 0x05D5, 0x05E4, 0x05DC, 0x05D8 };
        const string logicalPhrase = "טלפון 123";
        var pdf = RtlPdfFixtures.SingleTjScalarStream(visual);
        using var doc = PdfDocument.Open(pdf);
        RtlRedactionTests.PinDeterministicId(doc);

        doc.GetPage(1).Text.Should().Contain(logicalPhrase);

        var removed = doc.RedactText(logicalPhrase);

        removed.Should().BeGreaterThan(0);

        var searchable = SearchableTextOf(doc.SaveToBytes());
        searchable.Should().NotContain(logicalPhrase);
        searchable.Should().NotContain(ReverseString(logicalPhrase));
        searchable.Should().NotContain("טלפון");
        searchable.Should().NotContain("ABCDEFGHI", "nor as its raw character codes");
    }

    private static string SearchableTextOf(byte[] saved) =>
        Encoding.ASCII.GetString(saved) +
        Encoding.BigEndianUnicode.GetString(saved) +
        Encoding.UTF8.GetString(saved);

    private static string ReverseString(string s)
    {
        var chars = s.ToCharArray();
        System.Array.Reverse(chars);
        return new string(chars);
    }
}
