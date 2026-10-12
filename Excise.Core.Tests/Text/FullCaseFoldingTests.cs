using System;
using AwesomeAssertions;
using Excise.Core.Text;
using Xunit;

namespace Excise.Core.Tests.Text;

/// <summary>
/// Case-insensitive matching is Unicode FULL case folding (CaseFolding.txt C + F),
/// one rule for search, annotation search and term redaction (<see cref="TermMatch"/>).
/// A fold can be longer than its source (ß → ss), so every match must map back to
/// the caller's own characters.
/// </summary>
public class FullCaseFoldingTests
{
    [Fact]
    public void TheTable_IsUnicode16_CAndF()
    {
        UnicodeCaseFolding.UnicodeVersion.Should().Be("16.0.0");
        UnicodeCaseFolding.EntryCount.Should().Be(1557);
        UnicodeCaseFolding.Fold("ß").Should().Be("ss");
        UnicodeCaseFolding.Fold("ẞ").Should().Be("ss");
        UnicodeCaseFolding.Fold("SS").Should().Be("ss");
        UnicodeCaseFolding.Fold("ŉ").Should().Be("ʼn");
        UnicodeCaseFolding.Fold("ǰ").Should().Be("ǰ");
        UnicodeCaseFolding.Fold("ᾀ").Should().Be("ἀι");
        UnicodeCaseFolding.Fold("ﬃ").Should().Be("ffi");
        UnicodeCaseFolding.Fold("\U00010400").Should().Be("\U00010428", "Deseret folds through a surrogate pair");
    }

    [Fact]
    public void TurkishI_IsNotCultureFolded()
    {
        UnicodeCaseFolding.Fold("I").Should().Be("i");
        UnicodeCaseFolding.Fold("İ").Should().Be("i̇", "the F entry, not the Turkish T entry");
        UnicodeCaseFolding.Fold("ı").Should().Be("ı", "dotless i has no C or F entry: it folds to itself");
        TermMatch.IndexOf("ıstanbul", "istanbul", caseSensitive: false, wholeWord: false, 0).Should().Be(-1);
    }

    [Theory]
    [InlineData("Straße", "strasse")]
    [InlineData("STRASSE", "straße")]
    [InlineData("Straße", "STRASSE")]
    [InlineData("STRAẞE", "strasse")]
    [InlineData("strasse", "STRAẞE")]
    [InlineData("Straße", "STRAẞE")]
    public void SharpS_AndDoubleS_FindEachOther(string text, string term)
    {
        var at = TermMatch.IndexOf($"in der {text} 5", term, caseSensitive: false, wholeWord: true, 0,
            SeparatorMatching.Default, out var length);
        at.Should().Be(7);
        $"in der {text} 5".Substring(at, length).Should().Be(text, "the match maps back to the text's own characters");
    }

    [Theory]
    [InlineData("ŉ", "ʼN")]
    [InlineData("ǰ", "J̌")]
    [InlineData("ᾳ", "ΑΙ")]
    [InlineData("ﬀ", "FF")]
    [InlineData("K", "k")]   // Kelvin sign
    [InlineData("ſ", "S")]        // long s
    public void OtherOneToManyAndSpecialFoldings_MatchBothWays(string a, string b)
    {
        TermMatch.IndexOf($"x {a} y", b, false, false, 0).Should().Be(2);
        TermMatch.IndexOf($"x {b} y", a, false, false, 0).Should().Be(2);
    }

    [Fact]
    public void MatchCase_StaysExactOrdinal()
    {
        TermMatch.IndexOf("Straße", "strasse", caseSensitive: true, false, 0).Should().Be(-1);
        TermMatch.IndexOf("Straße", "Strasse", caseSensitive: true, false, 0).Should().Be(-1);
        TermMatch.IndexOf("Straße", "Straße", caseSensitive: true, false, 0).Should().Be(0);
    }

    [Fact]
    public void AMatchInsideAnExpansion_TakesTheWholeCharacter()
    {
        // 'Straße' folds to 'strasse': 'ss' and 'se' both touch the ß.
        TermMatch.IndexOf("Straße", "ss", false, false, 0, SeparatorMatching.Default, out var length).Should().Be(4);
        length.Should().Be(1);
        TermMatch.IndexOf("Straße", "se", false, false, 0, SeparatorMatching.Default, out length).Should().Be(4);
        length.Should().Be(2, "ß and e");
        TermMatch.IndexOf("Straße", "stras", false, false, 0, SeparatorMatching.Default, out length).Should().Be(0);
        length.Should().Be(5, "the ß it ends inside is taken whole");
    }

    [Fact]
    public void Offsets_AfterAnExpansion_StayInTheCallersText()
    {
        const string text = "ßßß Weiß weiss";
        var hits = new System.Collections.Generic.List<(int, int)>();
        for (var at = 0; (at = TermMatch.IndexOf(text, "WEISS", false, true, at, SeparatorMatching.Default, out var len)) >= 0; at += len)
            hits.Add((at, len));
        hits.Should().Equal((4, 4), (9, 5));
    }

    [Theory]
    [InlineData("Straße", "strasse", 0, 5)]
    [InlineData("Straße", "se", 4, 5)]
    [InlineData("STRASSE", "straße", 0, 6)]
    [InlineData("oﬃce", "fice", 1, 3)]
    public void MatchEnd_MapsTheFoldedMatchBackToRawCharacters(string raw, string term, int start, int end)
    {
        var needle = TermMatch.Needle(term, caseSensitive: false, SeparatorMatching.Default);
        TermMatch.MatchEnd(raw, start, needle, caseSensitive: false, SeparatorMatching.Default).Should().Be(end);
    }

    [Fact]
    public void Mask_CutsTheRawSpanOfAFoldedMatch()
    {
        TermMatch.Mask("Sitz: Hauptstraße 1", ["STRASSE"], caseSensitive: false, wholeWord: false)
            .Should().Be("Sitz: Haupt 1");
        TermMatch.Mask("Sitz: HAUPTSTRASSE 1", ["straße"], caseSensitive: false, wholeWord: false)
            .Should().Be("Sitz: HAUPT 1");
        TermMatch.Mask("Sitz: Hauptstraße 1", ["STRASSE"], caseSensitive: true, wholeWord: false)
            .Should().BeNull("Match case keeps the exact ordinal rule");
    }
}
