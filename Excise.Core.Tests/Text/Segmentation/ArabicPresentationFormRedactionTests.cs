using System.IO;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Tests.Text;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// Arabic presentation-form folding in redaction matching (#632).
///
/// Arabic text can be stored in a PDF either as base letters (U+0621–U+064A)
/// or as PRESENTATION FORMS — the shaped positional glyphs of U+FB50–U+FDFF
/// (Forms-A, incl. lam-alef ligatures) and U+FE70–U+FEFF (Forms-B). A user's
/// redaction search string is typed in base letters. Before the fix these
/// tests cover, a presentation-form-encoded word never matched a base-letter
/// needle: <c>RedactText("سلام")</c> returned 0 on the fixtures below and
/// reported success while the word stayed in the file — CLAUDE.md
/// limitation #1 in its presentation-form shape.
///
/// The ground truth for the equivalence is the Unicode compatibility
/// decomposition of the two Arabic presentation blocks (deterministic; not a
/// tool's opinion): U+FEB3→U+0633, U+FEFC→U+0644 U+0627 (a 1→2 lam-alef
/// expansion), U+FEE1→U+0645. Folding is applied to MATCHING only — the
/// letters, operator text, and removed bytes keep their raw presentation-form
/// code points, so glyph-level removal still maps char-to-glyph exactly.
///
/// Saved-bytes assertions follow the redaction test rules: ASCII + UTF-16BE
/// (+ UTF-8) views of the written file, checked for the word in base form,
/// shaped form, both orders, and the raw character-code carrier the fixture
/// is known to use — with an anti-vacuity check that the carrier existed
/// before redaction.
/// </summary>
public class ArabicPresentationFormRedactionTests
{
    /// <summary>What a user types: base letters, logical order.</summary>
    private const string BaseWord = "سلام"; // U+0633 U+0644 U+0627 U+0645

    /// <summary>
    /// The same word as a shaping engine stores it: seen-initial U+FEB3,
    /// lam-alef-final ligature U+FEFC, meem-isolated U+FEE1.
    /// </summary>
    private const string ShapedWord = "ﺳﻼﻡ";

    private static readonly int[] ShapedScalars = { 0xFEB3, 0xFEFC, 0xFEE1 };

    [Fact]
    public void RedactText_BaseLetterNeedle_RemovesPresentationFormWord()
    {
        // Visual-order stream (codes reversed, painted left-to-right) — the
        // common producer encoding, mapped to presentation-form scalars.
        var pdf = RtlPdfFixtures.SingleTj(ShapedScalars, visualOrder: true);
        using var doc = PdfDocument.Open(pdf);

        // Anti-vacuity: before redaction the word must be present — extracted
        // as presentation forms, and carried in the saved bytes as its raw
        // character codes 'CBA' (visual order).
        doc.GetPage(1).Text.Should().Contain(ShapedWord,
            "sanity: the fixture must extract as presentation forms for this test to prove anything");
        SearchableTextOf(doc.SaveToBytes()).Should().Contain("(CBA)",
            "sanity: the glyph-code carrier must be present before redaction");

        var removed = doc.RedactText(BaseWord);

        removed.Should().BeGreaterThan(0,
            "a base-letter needle must match a presentation-form glyph run via " +
            "compatibility-decomposition folding; 0 matches is the silent-failure " +
            "mode this test exists to prevent");

        var saved = doc.SaveToBytes();
        var searchable = SearchableTextOf(saved);
        searchable.Should().NotContain(BaseWord, "the word must not survive in base letters");
        searchable.Should().NotContain(ShapedWord, "nor in its shaped (presentation-form) encoding");
        searchable.Should().NotContain(Reverse(ShapedWord), "nor in visual (reversed) order");
        searchable.Should().NotContain("(CBA)", "nor as its raw character codes");
        searchable.Should().NotContain("(ABC)", "nor as reversed character codes");

        using var reopened = PdfDocument.Open(saved);
        reopened.GetPage(1).Text.Should().NotContainAny(
            BaseWord, ShapedWord, Reverse(ShapedWord));
    }

    [Fact]
    public void RedactText_ShapedNeedle_AlsoRemovesPresentationFormWord()
    {
        // A user may also paste shaped text copied from a viewer. Folding is
        // applied to BOTH sides, so a shaped needle matches too.
        var pdf = RtlPdfFixtures.SingleTj(ShapedScalars, visualOrder: true);
        using var doc = PdfDocument.Open(pdf);

        var removed = doc.RedactText(ShapedWord);

        removed.Should().BeGreaterThan(0);

        var searchable = SearchableTextOf(doc.SaveToBytes());
        searchable.Should().NotContain(BaseWord);
        searchable.Should().NotContain(ShapedWord);
        searchable.Should().NotContain("(CBA)");
    }

    [Fact]
    public void RedactText_BaseLetterNeedle_RemovesLogicalOrderPresentationFormWord()
    {
        // The other producer encoding: logical-order shaped codes positioned
        // at decreasing X. Extraction never reverses these; folding alone
        // must bridge shaped → base.
        var pdf = RtlPdfFixtures.PerGlyphDecreasingX(ShapedScalars);
        using var doc = PdfDocument.Open(pdf);

        var removed = doc.RedactText(BaseWord);

        removed.Should().BeGreaterThan(0);

        var searchable = SearchableTextOf(doc.SaveToBytes());
        searchable.Should().NotContain(BaseWord);
        searchable.Should().NotContain(ShapedWord);
        searchable.Should().NotContain(Reverse(ShapedWord));
    }

    [Fact]
    public void RedactText_BaseLetterNeedle_DoesNotTouchUnrelatedLatinText()
    {
        // Scope guard: folding must not affect non-Arabic content. A Latin
        // prefix shares the page; redacting the Arabic word must leave it.
        var pdf = RtlPdfFixtures.SingleTjWithLatinPrefix("abc", ShapedScalars);
        using var doc = PdfDocument.Open(pdf);

        var removed = doc.RedactText(BaseWord);

        removed.Should().BeGreaterThan(0);

        using var reopened = PdfDocument.Open(doc.SaveToBytes());
        var text = reopened.GetPage(1).Text;
        text.Should().Contain("abc", "unrelated Latin text on the same line must survive");
        text.Should().NotContainAny(BaseWord, ShapedWord, Reverse(ShapedWord));
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

    private static string Reverse(string s)
    {
        var chars = s.ToCharArray();
        System.Array.Reverse(chars);
        return new string(chars);
    }
}
