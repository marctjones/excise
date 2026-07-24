using System;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Tests.Text;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// Fullwidth/halfwidth form folding in redaction matching (#727).
///
/// CJK documents write embedded Latin letters and digits as FULLWIDTH
/// forms (U+FF01–U+FF5E: "ＡＢＣ", "１２３") and katakana as HALFWIDTH
/// forms (U+FF61–U+FF9F: "ｶﾀｶﾅ"). A user's needle is typed with a regular
/// keyboard: "ABC", "123", "カタカナ". Before the fix these tests cover,
/// <c>RedactText("ABC")</c> returned 0 against stored "ＡＢＣ" and
/// reported success while the text stayed in the file — CLAUDE.md
/// limitation #1 (silent redaction failure) for names, account numbers
/// and dates in Japanese/Chinese/Korean documents.
///
/// The ground truth is the Unicode compatibility decomposition (NFKC) of
/// the Halfwidth and Fullwidth Forms block U+FF00–U+FFEF (deterministic;
/// not a tool's opinion): U+FF21 → "A", U+FF76 → "カ", and the halfwidth
/// voiced pair U+FF76 U+FF9E composes to "ガ". The fold is scoped to THAT
/// BLOCK ONLY — deliberately not whole-string NFKC, which would also
/// rewrite superscripts, circled numbers, and other compatibility
/// characters and loosen matching (pinned by scope guards).
///
/// Saved-bytes assertions follow the redaction test rules: ASCII +
/// UTF-16BE + UTF-8 views of the written file, checked for the text in
/// both widths and for the raw character-code carrier, with anti-vacuity
/// checks that the carrier existed before redaction.
/// </summary>
public class FullwidthFormsRedactionTests
{
    [Theory]
    [InlineData("ABC", new[] { 0xFF21, 0xFF22, 0xFF23 })]  // ＡＢＣ
    [InlineData("123", new[] { 0xFF11, 0xFF12, 0xFF13 })]  // １２３
    [InlineData("abc", new[] { 0xFF41, 0xFF42, 0xFF43 })]  // ａｂｃ
    public void RedactText_AsciiNeedle_RemovesFullwidthText(
        string plainNeedle, int[] fullwidthScalars)
    {
        var pdf = RtlPdfFixtures.SingleTj(fullwidthScalars, visualOrder: false);
        using var doc = PdfDocument.Open(pdf);

        var storedText = string.Concat(fullwidthScalars.Select(char.ConvertFromUtf32));

        // Anti-vacuity: the fullwidth text must be present — extracted with
        // its raw fullwidth code points, carried as raw character codes.
        doc.GetPage(1).Text.Should().Contain(storedText,
            "sanity: the fixture must extract fullwidth for this test to prove anything");
        doc.GetPage(1).Text.Should().NotContain(plainNeedle,
            "sanity: the ASCII spelling must NOT be extractable, or matching would succeed without width folding");
        SearchableTextOf(doc.SaveToBytes()).Should().Contain("(ABC)",
            "sanity: the glyph-code carrier must be present before redaction");

        var removed = doc.RedactText(plainNeedle);

        removed.Should().BeGreaterThan(0,
            $"an ASCII needle '{plainNeedle}' must match fullwidth '{storedText}'; " +
            "0 matches is the silent-failure mode this test exists to prevent (#727)");

        var saved = doc.SaveToBytes();
        var searchable = SearchableTextOf(saved);
        searchable.Should().NotContain(plainNeedle, "the text must not survive in ASCII");
        searchable.Should().NotContain(storedText, "nor fullwidth");
        searchable.Should().NotContain("(ABC)", "nor as its raw character codes");

        using var reopened = PdfDocument.Open(saved);
        reopened.GetPage(1).Text.Should().NotContainAny(plainNeedle, storedText);
    }

    [Fact]
    public void RedactText_KatakanaNeedle_RemovesHalfwidthKatakana()
    {
        // Stored halfwidth ｶﾀｶﾅ; typed regular katakana カタカナ.
        var scalars = new[] { 0xFF76, 0xFF80, 0xFF76, 0xFF85 };
        var storedText = "\uFF76\uFF80\uFF76\uFF85";  // halfwidth katakana
        var typedNeedle = "\u30AB\u30BF\u30AB\u30CA";  // regular katakana
        var pdf = RtlPdfFixtures.SingleTj(scalars, visualOrder: false);
        using var doc = PdfDocument.Open(pdf);

        doc.GetPage(1).Text.Should().Contain(storedText,
            "sanity: the fixture must extract halfwidth");
        doc.GetPage(1).Text.Should().NotContain(typedNeedle);

        var removed = doc.RedactText(typedNeedle);

        removed.Should().BeGreaterThan(0,
            "a regular-katakana needle must match halfwidth katakana (#727)");

        var searchable = SearchableTextOf(doc.SaveToBytes());
        searchable.Should().NotContain(storedText);
        searchable.Should().NotContain(typedNeedle);
        searchable.Should().NotContain("(ABCD)");
    }

    [Fact]
    public void RedactText_VoicedKatakanaNeedle_RemovesHalfwidthPair()
    {
        // Halfwidth voiced katakana is TWO code points (ｶ + ﾞ,
        // U+FF76 U+FF9E); the typed needle is ONE (ガ, U+30AC). The width
        // fold produces カ + combining U+3099, which must then canonically
        // compose to match — the width and canonical stages must compose
        // correctly across letter boundaries.
        var scalars = new[] { 0xFF76, 0xFF9E };
        var pdf = RtlPdfFixtures.SingleTj(scalars, visualOrder: false);
        using var doc = PdfDocument.Open(pdf);

        var removed = doc.RedactText("\u30AC");  // ga (voiced ka)

        removed.Should().BeGreaterThan(0,
            "the halfwidth base+voiced-mark pair must fold and compose to ガ (#727)");

        var searchable = SearchableTextOf(doc.SaveToBytes());
        searchable.Should().NotContain("\uFF76\uFF9E");
        searchable.Should().NotContain("\u30AC");
        searchable.Should().NotContain("(AB)");
    }

    [Fact]
    public void RedactText_FullwidthNeedle_AlsoRemovesAsciiText()
    {
        // The mirror image: ASCII stored, fullwidth needle pasted from a
        // CJK document. Folding applies to BOTH sides.
        var scalars = "ABC".Select(c => (int)c).ToArray();
        var pdf = RtlPdfFixtures.SingleTj(scalars, visualOrder: false);
        using var doc = PdfDocument.Open(pdf);

        var removed = doc.RedactText("\uFF21\uFF22\uFF23");  // fullwidth ABC

        removed.Should().BeGreaterThan(0,
            "a fullwidth needle must match ASCII text (#727)");

        var searchable = SearchableTextOf(doc.SaveToBytes());
        searchable.Should().NotContain("\uFF21\uFF22\uFF23");
    }

    [Fact]
    public void RedactText_AsciiNeedle_DoesNotTouchUnrelatedText()
    {
        // Scope guard: redacting the fullwidth text must leave other text on
        // the same line intact.
        var scalars = new[] { 0xFF21, 0xFF22, 0xFF23, (int)' ' }
            .Concat("xyz".Select(c => (int)c)).ToArray();
        var pdf = RtlPdfFixtures.SingleTj(scalars, visualOrder: false);
        using var doc = PdfDocument.Open(pdf);

        var removed = doc.RedactText("ABC");

        removed.Should().BeGreaterThan(0);

        using var reopened = PdfDocument.Open(doc.SaveToBytes());
        var text = reopened.GetPage(1).Text;
        text.Should().Contain("xyz", "unrelated text on the same line must survive");
        text.Should().NotContain("ABC");
    }

    [Theory]
    [InlineData("2", new[] { 0x00B2 })]   // superscript two ²
    [InlineData("1", new[] { 0x2460 })]   // circled digit one ①
    [InlineData("TEL", new[] { 0x2121 })] // telephone sign ℡
    public void RedactText_CompatibilityCharsOutsideTheBlock_AreNotFolded(
        string needle, int[] storedScalars)
    {
        // Scope guard: the fold is the U+FF00–U+FFEF block ONLY. Whole-string
        // NFKC would also fold superscripts, circled numbers and squared
        // signs — that creep must not happen.
        var pdf = RtlPdfFixtures.SingleTj(storedScalars, visualOrder: false);
        using var doc = PdfDocument.Open(pdf);

        var removed = doc.RedactText(needle);

        removed.Should().Be(0,
            "compatibility characters outside the Halfwidth and Fullwidth Forms " +
            "block must keep their identity (no whole-string NFKC creep)");
    }

    /// <summary>
    /// Carrier-agnostic view of the saved file, per the redaction test rules:
    /// ASCII (raw codes, hex carriers) + UTF-16BE (PDF Unicode text strings)
    /// + UTF-8 (XMP metadata).
    /// </summary>
    private static string SearchableTextOf(byte[] saved) =>
        Encoding.ASCII.GetString(saved) +
        Encoding.BigEndianUnicode.GetString(saved) +
        Encoding.UTF8.GetString(saved);
}
