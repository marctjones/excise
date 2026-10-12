using AwesomeAssertions;
using Excise.Core.Text;
using Xunit;

namespace Excise.Core.Tests.Text;

/// <summary>
/// Ignore accents (<see cref="SeparatorMatching.IgnoreAccents"/>, default on): the
/// combining diacritical marks of Latin, Greek and Cyrillic letters are ignored in the
/// term AND the text, so both directions match, whether the accent is precomposed or a
/// separate mark. Marks that are part of a letter in other scripts are never dropped.
/// </summary>
public class AccentMatchingTests
{
    private static readonly SeparatorMatching Exact = SeparatorMatching.Default with { IgnoreAccents = false };

    private static int Find(string text, string term, bool caseSensitive = false, SeparatorMatching? rule = null) =>
        TermMatch.IndexOf(MatchingNormalization.Fold(text), MatchingNormalization.Fold(term).Trim(),
            caseSensitive, wholeWord: true, 0, rule ?? SeparatorMatching.Default);

    [Theory]
    [InlineData("le café noir", "cafe")]
    [InlineData("le cafe noir", "café")]
    [InlineData("le CAFÉ noir", "cafe")]
    [InlineData("le café noir", "cafe")]        // combining acute stored as its own code point
    [InlineData("le cafe noir", "café")]
    [InlineData("mon résumé", "resume")]
    [InlineData("mon resume", "résumé")]
    [InlineData("naïve", "naive")]
    [InlineData("Ångström", "angstrom")]
    [InlineData("Dvořák", "dvorak")]
    [InlineData("Phở", "pho")]                        // two marks on one letter (Vietnamese)
    [InlineData("ἀλήθεια", "αληθεια")]                // Greek
    [InlineData("Ёлка", "елка")]                      // Cyrillic ё
    public void AccentedAndPlainSpellings_FindEachOther(string text, string term)
    {
        Find(text, term).Should().BeGreaterThanOrEqualTo(0, $"'{term}' must find '{text}'");
        Find(text, term, rule: Exact).Should().Be(-1, "with exact accents an accented letter matches only itself");
    }

    [Fact]
    public void CaseSensitiveStillIgnoresAccents_ButNotCase()
    {
        Find("le Café noir", "Cafe", caseSensitive: true).Should().Be(3);
        Find("le Café noir", "cafe", caseSensitive: true).Should().Be(-1);
    }

    [Theory]
    [InlineData("क़ानून", "कानून")]   // Devanagari nukta: qanun is not kanun
    [InlineData("ก่า", "กา")]                        // Thai tone mark
    [InlineData("が", "か")]                          // Japanese voiced mark
    [InlineData("각", "가")]                          // Hangul syllable with a final consonant
    [InlineData("søster", "soster")]                  // ø is a letter, not an accented o
    [InlineData("łódź", "lodz")]                      // ł likewise (the accents are ignored, ł is not)
    public void MarksThatArePartOfTheLetter_AndDistinctLetters_StayDistinct(string text, string term)
    {
        Find(text, term).Should().Be(-1, $"'{term}' must not find '{text}'");
    }

    [Fact]
    public void HebrewPointsAndArabicHarakat_AreIgnoredAsBefore_InBothModes()
    {
        Find("שָׁלוֹם", "שלום").Should().Be(0);
        Find("שָׁלוֹם", "שלום", rule: Exact).Should().Be(0, "#725 folds them whatever the accent rule");
        Find("كَتَبَ", "كتب", rule: Exact).Should().Be(0);
    }

    [Fact]
    public void TheMatch_CoversTheAccentsOfItsLastLetter_AndNeverStartsOnAnAccent()
    {
        const string raw = "le café noir";
        TermMatch.IndexOf(raw, "cafe", false, false, 0, SeparatorMatching.Default, out var length).Should().Be(3);
        length.Should().Be(5, "the separate accent goes with its letter");

        var needle = TermMatch.Needle("cafe", false, SeparatorMatching.Default);
        TermMatch.MatchEnd(raw, 3, needle, false, SeparatorMatching.Default).Should().Be(7);
        TermMatch.MatchEnd("éx", 1, TermMatch.Needle("x", false, SeparatorMatching.Default), false,
            SeparatorMatching.Default).Should().Be(-1, "a match starting on the accent would take the é's accent");
    }

    [Fact]
    public void Mask_CutsEveryAccentSpelling()
    {
        TermMatch.Mask("Café, cafe et CAFÉ.", ["cafe"], caseSensitive: false, wholeWord: true,
            SeparatorMatching.Default).Should().Be(",  et .");
        TermMatch.Mask("Café, cafe et CAFÉ.", ["cafe"], caseSensitive: false, wholeWord: true,
            Exact).Should().Be("Café,  et CAFÉ.", "with exact accents only the plain spelling goes");
    }
}
