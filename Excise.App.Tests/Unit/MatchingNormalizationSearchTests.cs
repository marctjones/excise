using System;
using System.Linq;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Excise.App.Services;
using Excise.App.Tests.Utilities;
using Excise.Core.Document;
using Xunit;

namespace Excise.App.Tests.Unit;

/// <summary>
/// Search must fold six distinct Unicode carriers of "the same word" onto
/// what a user types on a plain keyboard: Arabic PRESENTATION FORMS (#632),
/// canonically EQUIVALENT accent spellings (#724), FULLWIDTH/halfwidth forms
/// (#727), Arabic harakat / Hebrew niqqud vocalization marks (#725), invisible
/// optional separators (#726), and Latin LIGATURES (#722). Every family shares
/// the same fixture shape (<see cref="ToUnicodePdfFixture"/>: a page storing
/// exact code points via <c>/ToUnicode</c>) and the same three-part contract —
/// a plain needle finds the stored spelling, whole-word comparison folds too,
/// and the stored spelling itself also matches (both sides fold) — so this
/// class merges what were six near-identical files into one parameterized
/// suite. Regex folding and the scope-guard negatives (which MUST NOT match)
/// are family-specific and stay as individual facts below the shared theories.
/// </summary>
public class MatchingNormalizationSearchTests
{
    private static PdfSearchService NewService() =>
        new(NullLogger<PdfSearchService>.Instance);

    /// <summary>One row per normalization family's "typed → stored" pair.</summary>
    public static TheoryData<string, int[], string, string> FoldedWordCases()
    {
        var data = new TheoryData<string, int[], string, string>();

        // Arabic presentation forms (#632): seen-initial, lam-alef-final
        // ligature (folds to TWO base letters), meem-isolated.
        data.Add("Arabic presentation forms",
            new[] { 0xFEE1, 0xFEFC, 0xFEB3 }, "سلام", "سلام");

        // Canonical accent equivalence (#724): precomposed é vs e + combining acute.
        data.Add("Canonical accent (NFC)",
            new[] { 'c', 'a', 'f', 'e', 0x0301 }, "café", "café");

        // Fullwidth ASCII / digits, halfwidth katakana (#727).
        data.Add("Fullwidth ASCII letters", new[] { 0xFF21, 0xFF22, 0xFF23 }, "ABC", null!);
        data.Add("Fullwidth digits", new[] { 0xFF11, 0xFF12, 0xFF13 }, "123", null!);
        data.Add("Halfwidth katakana",
            new[] { 0xFF76, 0xFF80, 0xFF76, 0xFF85 }, "カタカナ", null!);

        // Harakat / niqqud vocalization marks (#725), stored visual-order RTL.
        data.Add("Arabic harakat",
            new[] { 0x064E, 0x0628, 0x064E, 0x062A, 0x064E, 0x0643 },
            "كتب", null!);
        data.Add("Hebrew niqqud",
            new[] { 0x05DD, 0x05B9, 0x05D5, 0x05DC, 0x05B8, 0x05C1, 0x05E9 },
            "שלום", null!);

        // Invisible optional separators (#726): soft hyphen, ZW space/non-joiner/joiner, ZWNBSP.
        foreach (var separator in new[] { 0x00AD, 0x200B, 0x200C, 0x200D, 0xFEFF })
        {
            data.Add($"Invisible separator U+{separator:X4}",
                new[] { 's', 'e', separator, 'c', 'r', 'e', 't' }, "secret", null!);
        }

        // Latin ligatures (#722): "o" + ﬃ (folds to THREE letters) + "ce".
        data.Add("Latin ligature", new[] { 0x006F, 0xFB03, 0x0063, 0x0065 }, "office", "office");

        return data;
    }

    [Theory]
    [MemberData(nameof(FoldedWordCases))]
    public void SearchInPage_PlainNeedle_FindsFoldedWord(
        string family, int[] storedScalars, string typedNeedle, string? expectedMatchedText)
    {
        using var doc = PdfDocument.Open(ToUnicodePdfFixture.Build(storedScalars));
        var page = doc.GetPage(1);

        // Anti-vacuity: the page really carries the folded spelling, not the typed one.
        page.Text.Should().NotContain(typedNeedle, $"{family}: fixture must not already store the plain needle");

        var matches = NewService().SearchInPage(page, typedNeedle, pageIndex: 0);

        matches.Should().NotBeEmpty($"{family}: a plain needle must find the folded word");
        matches[0].Width.Should().BeGreaterThan(0, $"{family}: the match must map back to word bounds");
        if (expectedMatchedText is not null)
        {
            matches[0].MatchedText.Should().Be(expectedMatchedText,
                $"{family}: matched text is reported in the folded space the search ran in");
        }
    }

    public static TheoryData<string, int[], string> WholeWordCases()
    {
        var data = new TheoryData<string, int[], string>();
        data.Add("Arabic presentation forms",
            new[] { 0xFEE1, 0xFEFC, 0xFEB3 }, "سلام");
        data.Add("Canonical accent (NFC)", new[] { 'c', 'a', 'f', 'e', 0x0301 }, "café");
        data.Add("Fullwidth ASCII letters", new[] { 0xFF21, 0xFF22, 0xFF23 }, "ABC");
        data.Add("Arabic harakat",
            new[] { 0x064E, 0x0628, 0x064E, 0x062A, 0x064E, 0x0643 }, "كتب");
        data.Add("Invisible separator (soft hyphen)",
            new[] { 's', 'e', 0x00AD, 'c', 'r', 'e', 't' }, "secret");
        data.Add("Latin ligature", new[] { 0x006F, 0xFB03, 0x0063, 0x0065 }, "office");
        return data;
    }

    [Theory]
    [MemberData(nameof(WholeWordCases))]
    public void SearchInPage_WholeWord_PlainNeedle_FindsFoldedWord(
        string family, int[] storedScalars, string typedNeedle)
    {
        using var doc = PdfDocument.Open(ToUnicodePdfFixture.Build(storedScalars));
        var page = doc.GetPage(1);

        var matches = NewService().SearchInPage(page, typedNeedle, wholeWordsOnly: true);

        matches.Should().NotBeEmpty(
            $"{family}: whole-word comparison must fold both the word and the needle");
    }

    public static TheoryData<string, int[], string> ReverseDirectionCases()
    {
        var data = new TheoryData<string, int[], string>();
        data.Add("Arabic presentation forms",
            new[] { 0xFEE1, 0xFEFC, 0xFEB3 },
            new string(new[] { (char)0xFEB3, (char)0xFEFC, (char)0xFEE1 }));
        data.Add("Canonical accent (NFC)", new[] { 'c', 'a', 'f', 'e', 0x0301 }, "café");
        data.Add("Fullwidth ASCII letters", new[] { (int)'A', (int)'B', (int)'C' }, "ＡＢＣ");
        data.Add("Arabic harakat",
            new[] { 0x064E, 0x0628, 0x064E, 0x062A, 0x064E, 0x0643 },
            "كَتَبَ");
        data.Add("Latin ligature", new[] { 0x006F, 0xFB03, 0x0063, 0x0065 },
            "o" + (char)0xFB03 + "ce");
        return data;
    }

    [Theory]
    [MemberData(nameof(ReverseDirectionCases))]
    public void SearchInPage_FoldedNeedle_AlsoFindsFoldedWord(
        string family, int[] storedScalars, string foldedNeedle)
    {
        using var doc = PdfDocument.Open(ToUnicodePdfFixture.Build(storedScalars));
        var page = doc.GetPage(1);

        var matches = NewService().SearchInPage(page, foldedNeedle, pageIndex: 0);

        matches.Should().NotBeEmpty($"{family}: both sides fold, so the folded spelling matches too");
    }

    // ── Family-specific extras: not shaped like the shared theories above ──

    [Fact]
    public void SearchInPage_Regex_PrecomposedPattern_FindsDecomposedWord()
    {
        using var doc = PdfDocument.Open(
            ToUnicodePdfFixture.Build(new[] { 'c', 'a', 'f', 'e', 0x0301 }));
        var page = doc.GetPage(1);

        var matches = NewService().SearchInPage(page, "café?", useRegex: true);

        matches.Should().NotBeEmpty(
            "the regex path folds the page text so precomposed patterns match decomposed text");
    }

    [Fact]
    public void SearchInPage_Regex_PlainLetterPattern_FindsLigatedWord()
    {
        using var doc = PdfDocument.Open(
            ToUnicodePdfFixture.Build(new[] { 0x006F, 0xFB03, 0x0063, 0x0065 }));
        var page = doc.GetPage(1);

        var matches = NewService().SearchInPage(page, "off?ice", useRegex: true);

        matches.Should().NotBeEmpty(
            "the regex path folds the page text so plain-letter patterns match ligated text");
    }

    [Fact]
    public void SearchInPage_PlainSpaceNeedle_FindsNonBreakingSpaceText()
    {
        var scalars = "top".Select(c => (int)c)
            .Append(0x00A0)
            .Concat("secret".Select(c => (int)c)).ToArray();
        using var doc = PdfDocument.Open(ToUnicodePdfFixture.Build(scalars));
        var page = doc.GetPage(1);

        page.Text.Should().Contain(" ");

        var matches = NewService().SearchInPage(page, "top secret", pageIndex: 0);

        matches.Should().NotBeEmpty(
            "a needle typed with a plain space must find text stored with U+00A0");
    }

    // ── Scope guards: every fold is bounded, and these prove the bound ──

    [Fact]
    public void SearchInPage_UnaccentedNeedle_DoesNotFindPrecomposedWord()
    {
        // Canonical folding must not become accent-insensitive matching —
        // "cafe" is not canonically equivalent to "café".
        using var doc = PdfDocument.Open(
            ToUnicodePdfFixture.Build(new[] { 'c', 'a', 'f', 0x00E9 }));
        var page = doc.GetPage(1);

        var matches = NewService().SearchInPage(page, "cafe", pageIndex: 0);

        matches.Should().BeEmpty(
            "the accent is canonically meaningful; accent-INSENSITIVE search is out of scope");
    }

    [Fact]
    public void SearchInPage_CompatibilityCharsOutsideTheBlock_AreNotFolded()
    {
        // No whole-string NFKC creep — "2" must not find superscript two (U+00B2).
        using var doc = PdfDocument.Open(ToUnicodePdfFixture.Build(new[] { 0x00B2 }));
        var page = doc.GetPage(1);

        var matches = NewService().SearchInPage(page, "2", pageIndex: 0);

        matches.Should().BeEmpty(
            "the width fold is scoped to U+FF00-U+FFEF; other compatibility characters keep their identity");
    }

    [Fact]
    public void SearchInPage_LatinAccents_AreNotStrippedByTheMarkFold()
    {
        // The Arabic/Hebrew mark strip must not also eat Latin combining accents.
        using var doc = PdfDocument.Open(
            ToUnicodePdfFixture.Build(new[] { 'c', 'a', 'f', 0x00E9 }));
        var page = doc.GetPage(1);

        var matches = NewService().SearchInPage(page, "cafe", pageIndex: 0);

        matches.Should().BeEmpty(
            "Latin combining accents are canonically meaningful and must not be stripped");
    }

    [Fact]
    public void SearchInPage_RealHyphen_IsNotFoldedAsASeparator()
    {
        // "secret" must not find "se-cret" — a real hyphen is visible content.
        using var doc = PdfDocument.Open(
            ToUnicodePdfFixture.Build(new[] { 's', 'e', (int)'-', 'c', 'r', 'e', 't' }));
        var page = doc.GetPage(1);

        var matches = NewService().SearchInPage(page, "secret", pageIndex: 0);

        matches.Should().BeEmpty(
            "only the soft hyphen is an optional separator; U+002D keeps its identity");
    }
}
