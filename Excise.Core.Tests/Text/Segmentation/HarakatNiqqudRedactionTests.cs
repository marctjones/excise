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
/// Arabic harakat / Hebrew niqqud folding in redaction matching (#725).
///
/// Arabic short vowels and annotation signs (harakat — U+064B–U+065F,
/// U+0670, and the Koranic marks U+06D6–U+06ED) and Hebrew points and
/// cantillation (niqqud/teamim — U+0591–U+05C7, combining marks only) are
/// OPTIONAL in normal writing: vocalized text ("كَتَبَ", "שָׁלוֹם") and bare text
/// ("كتب", "שלום") are the same words. Users type bare; religious texts,
/// children's books, dictionaries and legal documents store vocalized.
/// Before the fix these tests cover, <c>RedactText("كتب")</c> returned 0 on
/// a vocalized fixture and reported success while the word stayed in the
/// file — CLAUDE.md limitation #1 (silent redaction failure).
///
/// The ground truth is the Unicode code points themselves (deterministic;
/// not a tool's opinion): the stored form is the bare form with combining
/// marks of the two scripts interleaved. Matching strips exactly those
/// marks from BOTH sides. Scope is tight: Arabic/Hebrew combining marks
/// ONLY — Latin combining accents are canonically meaningful (handled by
/// NFC composition, #724, and kept), and Hebrew PUNCTUATION sharing the
/// block (maqaf U+05BE, sof pasuq U+05C3…) is untouched.
///
/// Saved-bytes assertions follow the redaction test rules: ASCII + UTF-16BE
/// + UTF-8 views of the written file, checked for the word in vocalized and
/// bare form and for the raw character-code carrier, with anti-vacuity
/// checks that the carrier existed before redaction.
/// </summary>
public class HarakatNiqqudRedactionTests
{
    /// <summary>Bare Arabic "kataba" (wrote): kaf, ta, ba.</summary>
    private const string BareArabic = "\u0643\u062A\u0628";

    /// <summary>Vocalized: each letter followed by fatha (U+064E).</summary>
    private const string VocalizedArabic = "\u0643\u064E\u062A\u064E\u0628\u064E";

    private static readonly int[] VocalizedArabicScalars =
        { 0x0643, 0x064E, 0x062A, 0x064E, 0x0628, 0x064E };

    /// <summary>Bare Hebrew "shalom": shin, lamed, vav, final mem.</summary>
    private const string BareHebrew = "\u05E9\u05DC\u05D5\u05DD";

    /// <summary>
    /// Pointed: shin + shin-dot (U+05C1) + qamats (U+05B8), lamed, vav +
    /// holam (U+05B9), final mem.
    /// </summary>
    private const string PointedHebrew = "\u05E9\u05C1\u05B8\u05DC\u05D5\u05B9\u05DD";

    private static readonly int[] PointedHebrewScalars =
        { 0x05E9, 0x05C1, 0x05B8, 0x05DC, 0x05D5, 0x05B9, 0x05DD };

    [Fact]
    public void RedactText_BareArabicNeedle_RemovesVocalizedWord()
    {
        // Stored in visual order (the common producer encoding); extraction
        // reorders RTL runs to logical, keeping the raw harakat code points.
        var pdf = RtlPdfFixtures.SingleTj(VocalizedArabicScalars, visualOrder: true);
        using var doc = PdfDocument.Open(pdf);

        // Anti-vacuity: the vocalized word must be present — extracted with
        // its raw harakat, and carried as raw character codes '(FEDCBA)'.
        doc.GetPage(1).Text.Should().Contain(VocalizedArabic,
            "sanity: the fixture must extract vocalized for this test to prove anything");
        doc.GetPage(1).Text.Should().NotContain(BareArabic,
            "sanity: the bare spelling must NOT be extractable, or matching would succeed without harakat folding");
        SearchableTextOf(doc.SaveToBytes()).Should().Contain("(FEDCBA)",
            "sanity: the glyph-code carrier must be present before redaction");

        var removed = doc.RedactText(BareArabic);

        removed.Should().BeGreaterThan(0,
            "a bare needle must match vocalized text — harakat are optional " +
            "vocalization, not different letters; 0 matches is the " +
            "silent-failure mode this test exists to prevent (#725)");

        var saved = doc.SaveToBytes();
        var searchable = SearchableTextOf(saved);
        searchable.Should().NotContain(VocalizedArabic, "the word must not survive vocalized");
        searchable.Should().NotContain(BareArabic, "nor bare");
        searchable.Should().NotContain("(FEDCBA)", "nor as its raw character codes");

        using var reopened = PdfDocument.Open(saved);
        reopened.GetPage(1).Text.Should().NotContainAny(VocalizedArabic, BareArabic);
    }

    [Fact]
    public void RedactText_VocalizedArabicNeedle_AlsoRemovesVocalizedWord()
    {
        // A user may paste vocalized text copied from a viewer. Folding is
        // applied to BOTH sides, so a vocalized needle matches too.
        var pdf = RtlPdfFixtures.SingleTj(VocalizedArabicScalars, visualOrder: true);
        using var doc = PdfDocument.Open(pdf);

        var removed = doc.RedactText(VocalizedArabic);

        removed.Should().BeGreaterThan(0);

        var searchable = SearchableTextOf(doc.SaveToBytes());
        searchable.Should().NotContain(VocalizedArabic);
        searchable.Should().NotContain(BareArabic);
        searchable.Should().NotContain("(FEDCBA)");
    }

    [Fact]
    public void RedactText_BareHebrewNeedle_RemovesPointedWord()
    {
        var pdf = RtlPdfFixtures.SingleTj(PointedHebrewScalars, visualOrder: true);
        using var doc = PdfDocument.Open(pdf);

        doc.GetPage(1).Text.Should().Contain(PointedHebrew,
            "sanity: the fixture must extract pointed");
        doc.GetPage(1).Text.Should().NotContain(BareHebrew,
            "sanity: the bare spelling must NOT be extractable");
        SearchableTextOf(doc.SaveToBytes()).Should().Contain("(GFEDCBA)",
            "sanity: the glyph-code carrier must be present before redaction");

        var removed = doc.RedactText(BareHebrew);

        removed.Should().BeGreaterThan(0,
            "a bare needle must match pointed (niqqud) text (#725)");

        var searchable = SearchableTextOf(doc.SaveToBytes());
        searchable.Should().NotContain(PointedHebrew);
        searchable.Should().NotContain(BareHebrew);
        searchable.Should().NotContain("(GFEDCBA)");
    }

    [Fact]
    public void RedactText_BareArabicNeedle_DoesNotTouchUnrelatedText()
    {
        // Scope guard: redacting the vocalized word must leave Latin text on
        // the same line intact. Latin prefix + visual-order RTL word in one
        // Tj — the common producer encoding.
        var pdf = RtlPdfFixtures.SingleTjWithLatinPrefix("xyz ", VocalizedArabicScalars);
        using var doc = PdfDocument.Open(pdf);

        var removed = doc.RedactText(BareArabic);

        removed.Should().BeGreaterThan(0);

        using var reopened = PdfDocument.Open(doc.SaveToBytes());
        var text = reopened.GetPage(1).Text;
        text.Should().Contain("xyz", "unrelated text on the same line must survive");
        text.Should().NotContainAny(VocalizedArabic, BareArabic);
    }

    [Fact]
    public void RedactText_LatinCombiningAccents_AreNotStripped()
    {
        // Scope guard: the strip is Arabic/Hebrew marks ONLY. A Latin
        // combining accent is canonically meaningful — "cafe" must still not
        // match "café" stored decomposed-then-composed (see #724).
        var pdf = RtlPdfFixtures.SingleTj(
            new[] { 'c', 'a', 'f', 0x00E9 }, visualOrder: false);
        using var doc = PdfDocument.Open(pdf);

        var removed = doc.RedactText("cafe");

        removed.Should().Be(0,
            "stripping must not extend to Latin combining accents (accent-insensitivity is out of scope)");
    }

    [Fact]
    public void RedactText_HebrewPunctuationInBlock_IsNotStripped()
    {
        // Scope guard: U+05BE (maqaf, the Hebrew hyphen) shares the
        // U+0591–U+05C7 neighborhood but is PUNCTUATION, not a combining
        // mark. A needle without it must not match text with it.
        var withMaqaf = new[] { 0x05D0, 0x05D1, 0x05BE, 0x05D2, 0x05D3 }; // אב־גד
        var pdf = RtlPdfFixtures.SingleTj(withMaqaf, visualOrder: true);
        using var doc = PdfDocument.Open(pdf);

        var removed = doc.RedactText("\u05D0\u05D1\u05D2\u05D3"); // alef-bet-gimel-dalet, no maqaf

        removed.Should().Be(0,
            "maqaf is punctuation carrying real content structure, not optional pointing");
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
