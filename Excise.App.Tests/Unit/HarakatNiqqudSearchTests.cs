using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Excise.App.Services;
using Excise.App.Tests.Utilities;
using Excise.Core.Document;
using Xunit;

namespace Excise.App.Tests.Unit;

/// <summary>
/// Search must find vocalized Arabic (harakat) and pointed Hebrew (niqqud)
/// text from a bare-letter needle (#725): the marks are optional
/// vocalization, not different letters. The fixture's /ToUnicode maps
/// character codes to the vocalized code points so extraction yields the
/// marks; only the Arabic/Hebrew mark strip in MatchingNormalization can
/// bridge that to a bare typed needle. Sibling of
/// ArabicPresentationFormSearchTests (#632) / CanonicalAccentSearchTests
/// (#724).
/// </summary>
public class HarakatNiqqudSearchTests
{
    /// <summary>Bare Arabic "kataba": kaf, ta, ba.</summary>
    private const string BareArabic = "\u0643\u062A\u0628";

    /// <summary>Vocalized: each letter followed by fatha (U+064E).</summary>
    private const string VocalizedArabic = "\u0643\u064E\u062A\u064E\u0628\u064E";

    /// <summary>
    /// Stored scalars in VISUAL order (the common producer encoding):
    /// logical kaf+fatha, ta+fatha, ba+fatha reversed wholesale. Extraction
    /// reorders the RTL run back to logical, raw marks intact.
    /// </summary>
    private static readonly int[] VocalizedArabicScalars =
        { 0x064E, 0x0628, 0x064E, 0x062A, 0x064E, 0x0643 };

    /// <summary>Bare Hebrew "shalom".</summary>
    private const string BareHebrew = "\u05E9\u05DC\u05D5\u05DD";

    /// <summary>Pointed "shalom" scalars, stored in visual order.</summary>
    private static readonly int[] PointedHebrewScalars =
        { 0x05DD, 0x05B9, 0x05D5, 0x05DC, 0x05B8, 0x05C1, 0x05E9 };

    private static PdfSearchService NewService() =>
        new(NullLogger<PdfSearchService>.Instance);

    [Fact]
    public void SearchInPage_BareArabicNeedle_FindsVocalizedWord()
    {
        using var doc = PdfDocument.Open(ToUnicodePdfFixture.Build(VocalizedArabicScalars));
        var page = doc.GetPage(1);

        // Anti-vacuity: the page carries the marks, not the bare spelling.
        page.Text.Should().Contain("\u064E");
        page.Text.Should().NotContain(BareArabic);

        var matches = NewService().SearchInPage(page, BareArabic, pageIndex: 0);

        matches.Should().NotBeEmpty(
            "a bare needle must find vocalized text — harakat are optional vocalization");
        matches[0].Width.Should().BeGreaterThan(0, "the match must map back to word bounds");
    }

    [Fact]
    public void SearchInPage_BareHebrewNeedle_FindsPointedWord()
    {
        using var doc = PdfDocument.Open(ToUnicodePdfFixture.Build(PointedHebrewScalars));
        var page = doc.GetPage(1);

        page.Text.Should().Contain("\u05B8");
        page.Text.Should().NotContain(BareHebrew);

        var matches = NewService().SearchInPage(page, BareHebrew, pageIndex: 0);

        matches.Should().NotBeEmpty(
            "a bare needle must find pointed (niqqud) text");
    }

    [Fact]
    public void SearchInPage_WholeWord_BareArabicNeedle_FindsVocalizedWord()
    {
        using var doc = PdfDocument.Open(ToUnicodePdfFixture.Build(VocalizedArabicScalars));
        var page = doc.GetPage(1);

        var matches = NewService().SearchInPage(page, BareArabic, wholeWordsOnly: true);

        matches.Should().NotBeEmpty(
            "whole-word comparison must strip the marks from both the word and the needle");
    }

    [Fact]
    public void SearchInPage_VocalizedNeedle_AlsoFindsVocalizedWord()
    {
        using var doc = PdfDocument.Open(ToUnicodePdfFixture.Build(VocalizedArabicScalars));
        var page = doc.GetPage(1);

        var matches = NewService().SearchInPage(page, VocalizedArabic, pageIndex: 0);

        matches.Should().NotBeEmpty("both sides fold, so a vocalized needle matches too");
    }

    [Fact]
    public void SearchInPage_LatinAccents_AreNotStripped()
    {
        // Scope guard: the strip covers Arabic/Hebrew marks only — searching
        // "cafe" must not find precomposed "café".
        using var doc = PdfDocument.Open(
            ToUnicodePdfFixture.Build(new[] { 'c', 'a', 'f', 0x00E9 }));
        var page = doc.GetPage(1);

        var matches = NewService().SearchInPage(page, "cafe", pageIndex: 0);

        matches.Should().BeEmpty(
            "Latin combining accents are canonically meaningful and must not be stripped");
    }
}
