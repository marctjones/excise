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

    private static readonly int[] ArabicScalars = { 0x0633, 0x0644, 0x0627, 0x0645 };
    private static readonly int[] HebrewScalars = { 0x05E9, 0x05DC, 0x05D5, 0x05DD };

    [Fact]
    public void RedactText_LogicalNeedle_RemovesVisualOrderArabicWord()
    {
        var pdf = RtlPdfFixtures.SingleTj(ArabicScalars, visualOrder: true);
        using var doc = PdfDocument.Open(pdf);

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

        var removed = doc.RedactText(ArabicWord);

        removed.Should().BeGreaterThan(0);

        var searchable = SearchableTextOf(doc.SaveToBytes());
        searchable.Should().NotContain(ArabicWord);
        searchable.Should().NotContain(Reverse(ArabicWord));
    }

    /// <summary>
    /// Carrier-agnostic view of the saved file, per the redaction test rules:
    /// ASCII (name-tree strings, raw codes, hex-encoded carriers stay visible)
    /// concatenated with UTF-16BE (how PDF text strings carry Unicode).
    /// </summary>
    private static string SearchableTextOf(byte[] saved) =>
        Encoding.ASCII.GetString(saved) + Encoding.BigEndianUnicode.GetString(saved);

    private static string Reverse(string s)
    {
        var chars = s.ToCharArray();
        System.Array.Reverse(chars);
        return new string(chars);
    }
}
