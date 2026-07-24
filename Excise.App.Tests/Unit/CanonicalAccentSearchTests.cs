using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Excise.App.Services;
using Excise.App.Tests.Utilities;
using Excise.Core.Document;
using Xunit;

namespace Excise.App.Tests.Unit;

/// <summary>
/// Search must treat the two canonically EQUIVALENT Unicode spellings of
/// accented text as the same (#724): precomposed ("café") and
/// decomposed ("cafe" + combining acute U+0301). The fixture's /ToUnicode
/// maps character codes to c,a,f,e,U+0301 so extraction yields the
/// decomposed spelling, and only canonical (NFC) folding can bridge it to
/// the typed precomposed needle. Canonical sibling of
/// LatinLigatureSearchTests (#722) / ArabicPresentationFormSearchTests
/// (#632).
/// </summary>
public class CanonicalAccentSearchTests
{
    /// <summary>What a user types: precomposed é (U+00E9).</summary>
    private const string PrecomposedWord = "caf\u00E9";

    /// <summary>What the page stores/extracts: e + combining acute.</summary>
    private const string DecomposedWord = "cafe\u0301";

    private static readonly int[] DecomposedScalars = { 'c', 'a', 'f', 'e', 0x0301 };

    private static PdfSearchService NewService() =>
        new(NullLogger<PdfSearchService>.Instance);

    [Fact]
    public void SearchInPage_PrecomposedNeedle_FindsDecomposedWord()
    {
        using var doc = PdfDocument.Open(ToUnicodePdfFixture.Build(DecomposedScalars));
        var page = doc.GetPage(1);

        // Anti-vacuity: the page really carries the decomposed spelling.
        page.Text.Should().Contain(DecomposedWord);
        page.Text.Should().NotContain(PrecomposedWord);

        var matches = NewService().SearchInPage(page, PrecomposedWord, pageIndex: 0);

        matches.Should().NotBeEmpty(
            "a precomposed needle must find canonically equivalent decomposed text");
        var match = matches[0];
        match.Width.Should().BeGreaterThan(0, "the match must map back to word bounds");
        match.MatchedText.Should().Be(PrecomposedWord,
            "matched text is reported in the folded (NFC) space the search ran in");
    }

    [Fact]
    public void SearchInPage_WholeWord_PrecomposedNeedle_FindsDecomposedWord()
    {
        using var doc = PdfDocument.Open(ToUnicodePdfFixture.Build(DecomposedScalars));
        var page = doc.GetPage(1);

        var matches = NewService().SearchInPage(page, PrecomposedWord, wholeWordsOnly: true);

        matches.Should().NotBeEmpty(
            "whole-word comparison must fold both the word and the needle to NFC");
    }

    [Fact]
    public void SearchInPage_Regex_PrecomposedPattern_FindsDecomposedWord()
    {
        using var doc = PdfDocument.Open(ToUnicodePdfFixture.Build(DecomposedScalars));
        var page = doc.GetPage(1);

        var matches = NewService().SearchInPage(page, "caf\u00E9?", useRegex: true);

        matches.Should().NotBeEmpty(
            "the regex path folds the page text so precomposed patterns match decomposed text");
    }

    [Fact]
    public void SearchInPage_DecomposedNeedle_AlsoFindsDecomposedWord()
    {
        using var doc = PdfDocument.Open(ToUnicodePdfFixture.Build(DecomposedScalars));
        var page = doc.GetPage(1);

        var matches = NewService().SearchInPage(page, DecomposedWord, pageIndex: 0);

        matches.Should().NotBeEmpty("both sides fold, so a decomposed needle matches too");
    }

    [Fact]
    public void SearchInPage_UnaccentedNeedle_DoesNotFindPrecomposedWord()
    {
        // Scope guard: canonical folding must not become accent-insensitive
        // matching — "cafe" is not canonically equivalent to "café".
        using var doc = PdfDocument.Open(
            ToUnicodePdfFixture.Build(new[] { 'c', 'a', 'f', 0x00E9 }));
        var page = doc.GetPage(1);

        var matches = NewService().SearchInPage(page, "cafe", pageIndex: 0);

        matches.Should().BeEmpty(
            "the accent is canonically meaningful; accent-INSENSITIVE search is out of scope");
    }
}
