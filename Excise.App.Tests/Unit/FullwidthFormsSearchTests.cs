using System.Linq;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Excise.App.Services;
using Excise.App.Tests.Utilities;
using Excise.Core.Document;
using Xunit;

namespace Excise.App.Tests.Unit;

/// <summary>
/// Search must find fullwidth ASCII ("ＡＢＣ", "１２３") and halfwidth
/// katakana ("ｶﾀｶﾅ") from a needle typed on a regular keyboard (#727). The
/// fixture's /ToUnicode maps character codes to the Halfwidth and Fullwidth
/// Forms code points so extraction yields them; only the width fold in
/// MatchingNormalization can bridge that to the typed needle. Scoped to
/// U+FF00–U+FFEF only — compatibility characters outside the block keep
/// their identity (pinned). Sibling of CanonicalAccentSearchTests (#724) /
/// InvisibleSeparatorSearchTests (#726).
/// </summary>
public class FullwidthFormsSearchTests
{
    private static PdfSearchService NewService() =>
        new(NullLogger<PdfSearchService>.Instance);

    [Fact]
    public void SearchInPage_AsciiNeedle_FindsFullwidthText()
    {
        var scalars = new[] { 0xFF21, 0xFF22, 0xFF23 }; // fullwidth ABC
        using var doc = PdfDocument.Open(ToUnicodePdfFixture.Build(scalars));
        var page = doc.GetPage(1);

        // Anti-vacuity: the page carries fullwidth code points, not ASCII.
        page.Text.Should().Contain("\uFF21");
        page.Text.Should().NotContain("ABC");

        var matches = NewService().SearchInPage(page, "ABC", pageIndex: 0);

        matches.Should().NotBeEmpty(
            "an ASCII needle must find fullwidth text via width folding");
        matches[0].Width.Should().BeGreaterThan(0, "the match must map back to word bounds");
    }

    [Fact]
    public void SearchInPage_AsciiDigits_FindFullwidthDigits()
    {
        var scalars = new[] { 0xFF11, 0xFF12, 0xFF13 }; // fullwidth 123
        using var doc = PdfDocument.Open(ToUnicodePdfFixture.Build(scalars));
        var page = doc.GetPage(1);

        var matches = NewService().SearchInPage(page, "123", pageIndex: 0);

        matches.Should().NotBeEmpty(
            "digits are how account numbers hide in fullwidth text");
    }

    [Fact]
    public void SearchInPage_KatakanaNeedle_FindsHalfwidthKatakana()
    {
        var scalars = new[] { 0xFF76, 0xFF80, 0xFF76, 0xFF85 }; // halfwidth katakana
        using var doc = PdfDocument.Open(ToUnicodePdfFixture.Build(scalars));
        var page = doc.GetPage(1);

        var matches = NewService().SearchInPage(
            page, "\u30AB\u30BF\u30AB\u30CA", pageIndex: 0); // regular katakana

        matches.Should().NotBeEmpty(
            "a regular-katakana needle must find halfwidth katakana");
    }

    [Fact]
    public void SearchInPage_WholeWord_AsciiNeedle_FindsFullwidthWord()
    {
        var scalars = new[] { 0xFF21, 0xFF22, 0xFF23 };
        using var doc = PdfDocument.Open(ToUnicodePdfFixture.Build(scalars));
        var page = doc.GetPage(1);

        var matches = NewService().SearchInPage(page, "ABC", wholeWordsOnly: true);

        matches.Should().NotBeEmpty(
            "whole-word comparison must width-fold both the word and the needle");
    }

    [Fact]
    public void SearchInPage_FullwidthNeedle_AlsoFindsAsciiText()
    {
        var scalars = "ABC".Select(c => (int)c).ToArray();
        using var doc = PdfDocument.Open(ToUnicodePdfFixture.Build(scalars));
        var page = doc.GetPage(1);

        var matches = NewService().SearchInPage(
            page, "\uFF21\uFF22\uFF23", pageIndex: 0);

        matches.Should().NotBeEmpty("both sides fold, so a fullwidth needle matches too");
    }

    [Fact]
    public void SearchInPage_CompatibilityCharsOutsideTheBlock_AreNotFolded()
    {
        // Scope guard: no whole-string NFKC creep — "2" must not find
        // superscript two (U+00B2).
        using var doc = PdfDocument.Open(ToUnicodePdfFixture.Build(new[] { 0x00B2 }));
        var page = doc.GetPage(1);

        var matches = NewService().SearchInPage(page, "2", pageIndex: 0);

        matches.Should().BeEmpty(
            "the width fold is scoped to U+FF00-U+FFEF; other compatibility characters keep their identity");
    }
}
