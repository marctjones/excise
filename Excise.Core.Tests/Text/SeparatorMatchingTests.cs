using System;
using AwesomeAssertions;
using Excise.Core.Text;
using Xunit;

namespace Excise.Core.Tests.Text;

/// <summary>
/// The one separator rule search, annotation search and term redaction share
/// (<see cref="SeparatorMatching"/> through <see cref="TermMatch"/>): a hyphen and
/// a space in the term match each other by default; matching no separator at all
/// is opt-in; a term with no separator matches literally under every setting.
/// </summary>
public class SeparatorMatchingTests
{
    private static readonly SeparatorMatching Closed = new(HyphenAsSpace: true, AllowNoSeparator: true);

    /// <summary>Every spelling the default must find, as the GUI search reads it (folded).</summary>
    public static TheoryData<string> Variants => new()
    {
        "open source",
        "open-source",
        "open–source",     // en dash
        "open—source",     // em dash
        "open−source",     // minus sign
        "open‐source",     // Unicode hyphen
        "open- source",         // a hyphenated wrap, words joined by a space (#924)
        "open source",     // no-break space
        "open  source",
    };

    [Theory]
    [MemberData(nameof(Variants))]
    public void BothSpellingsOfTheTerm_FindEveryVariant_ByDefault(string variant)
    {
        foreach (var term in new[] { "open source", "open-source", "open–source" })
        {
            var text = MatchingNormalization.Fold($"an {variant} tool");
            var at = TermMatch.IndexOf(text, MatchingNormalization.Fold(term).Trim(), caseSensitive: false,
                wholeWord: false, 0, SeparatorMatching.Default, out var length);
            at.Should().Be(3, $"'{term}' must find '{variant}'");
            text.Substring(at, length).Should().StartWith("open").And.EndWith("source",
                "the match covers the separator run and both words, nothing more");
        }
    }

    [Theory]
    [InlineData("open source")]
    [InlineData("open-source")]
    public void TheClosedCompound_IsFoundOnlyWhenAllowed(string term)
    {
        const string text = "an opensource tool";
        TermMatch.IndexOf(text, term, false, false, 0, SeparatorMatching.Default).Should().Be(-1,
            "matching no separator is opt-in: it would let 'pen nest' match 'pennest'");
        TermMatch.IndexOf(text, term, false, false, 0, Closed, out var length).Should().Be(3);
        length.Should().Be("opensource".Length);
    }

    [Fact]
    public void ExactSeparators_KeepTheOldLiteralRule()
    {
        TermMatch.IndexOf("an open-source tool", "open source", false, false, 0, SeparatorMatching.Exact)
            .Should().Be(-1);
        TermMatch.IndexOf("an open source tool", "open-source", false, false, 0, SeparatorMatching.Exact)
            .Should().Be(-1);
        TermMatch.IndexOf("an open source tool", "open source", false, false, 0, SeparatorMatching.Exact)
            .Should().Be(3);
        TermMatch.IndexOf("an open source tool", "open source", false, false, 0)
            .Should().Be(3, "the parameter's default is exact, so an unthreaded caller is unchanged");
    }

    [Fact]
    public void NoSeparatorWithoutHyphenAsSpace_StillMatchesTheSameSeparatorOrNothing()
    {
        var noSeparatorOnly = new SeparatorMatching(HyphenAsSpace: false, AllowNoSeparator: true);
        TermMatch.IndexOf("opensource", "open source", false, false, 0, noSeparatorOnly).Should().Be(0);
        TermMatch.IndexOf("open source", "open source", false, false, 0, noSeparatorOnly).Should().Be(0);
        TermMatch.IndexOf("open-source", "open source", false, false, 0, noSeparatorOnly).Should().Be(-1);
    }

    [Theory]
    [InlineData("opensource", "open source")]
    [InlineData("opensource", "open-source")]
    [InlineData("Anderson", "Ander-son")]
    public void ATermWithoutASeparator_IsLiteralUnderEverySetting(string term, string text)
    {
        TermMatch.IndexOf(text, term, false, false, 0, Closed).Should().Be(-1,
            "only a separator IN the term becomes flexible; the rule is one-way");
    }

    [Theory]
    [InlineData("co-", "co op")]
    [InlineData("co-", "coop")]
    [InlineData("-5", " 5")]
    [InlineData("x ", "x-")]
    public void ALeadingOrTrailingSeparator_StaysLiteral(string term, string text)
    {
        TermMatch.IndexOf(text, term, false, false, 0, Closed).Should().Be(-1,
            "only a separator between two other characters is flexible");
    }

    [Fact]
    public void PenNest_MatchesPennest_OnlyWithClosedCompounds()
    {
        TermMatch.Holds("the pennest", ["pen nest"], false, false, SeparatorMatching.Default).Should().BeFalse();
        TermMatch.Holds("the pennest", ["pen nest"], false, false, Closed).Should().BeTrue();
    }

    [Theory]
    [InlineData("reopen-sourcery", false)]
    [InlineData("reopen source", false)]
    [InlineData("open-sourcery", false)]
    [InlineData("(open–source)", true)]
    [InlineData("open- source.", true)]
    public void WholeWord_IsJudgedAtTheEndsOfTheMatchInTheText(string text, bool expected)
    {
        (TermMatch.IndexOf(text, "open-source", false, wholeWord: true, 0, SeparatorMatching.Default) >= 0)
            .Should().Be(expected);
        TermMatch.IndexOf("reopen-sourcery", "open-source", false, wholeWord: false, 0, SeparatorMatching.Default)
            .Should().Be(2, "substring mode still finds it inside a longer word");
    }

    [Fact]
    public void CaseSensitivity_IsUnchanged()
    {
        TermMatch.IndexOf("Open-Source", "open source", caseSensitive: true, false, 0, SeparatorMatching.Default)
            .Should().Be(-1);
        TermMatch.IndexOf("Open-Source", "open source", caseSensitive: false, false, 0, SeparatorMatching.Default)
            .Should().Be(0);
        TermMatch.IndexOf("Open-Source", "Open Source", caseSensitive: true, false, 0, SeparatorMatching.Default)
            .Should().Be(0);
    }

    [Fact]
    public void Mask_CutsEveryVariant_AndLeavesTheNeighbours()
    {
        TermMatch.Mask("open source, open-source, open–source and resource", ["open source"],
                false, false, SeparatorMatching.Default)
            .Should().Be(", ,  and resource");
        TermMatch.Mask("open-source", ["open source"], false, false, SeparatorMatching.Exact)
            .Should().BeNull("exact separators do not see the hyphenated spelling");
    }

    [Fact]
    public void TheFoldStillApplies_LigaturesFullwidthAndSoftHyphens()
    {
        // A ligature and fullwidth forms reach the match through the fold, as before.
        TermMatch.Mask("the ﬁle-name field", ["file name"], false, false, SeparatorMatching.Default)
            .Should().Be("the  field");
        TermMatch.Holds("ＡＢ－ＣＤ", ["AB CD"], false, false, SeparatorMatching.Default)
            .Should().BeTrue("a fullwidth hyphen-minus folds to '-'");
        // A soft hyphen is invisible: open&shy;source reads as the closed compound.
        TermMatch.Holds("open­source", ["open source"], false, false, SeparatorMatching.Default)
            .Should().BeFalse();
        TermMatch.Holds("open­source", ["open source"], false, false, Closed).Should().BeTrue();
    }

    [Fact]
    public void RightToLeftText_WithAHyphen_MatchesItsSpacedSpelling()
    {
        // Hebrew maqaf (U+05BE) is punctuation, not a separator here: it keeps its identity.
        TermMatch.Holds("בית-ספר", ["בית ספר"],
            false, false, SeparatorMatching.Default).Should().BeTrue();
        TermMatch.Holds("בית־ספר", ["בית ספר"],
            false, false, SeparatorMatching.Default).Should().BeFalse();
    }

    [Theory]
    [InlineData("open source")]
    [InlineData("open-source")]
    public void MatchEnd_MapsAFoldedVariantBackToTheRawSpan(string needle)
    {
        const string raw = "an open–source tool";
        TermMatch.MatchEnd(raw, 3, needle, StringComparison.OrdinalIgnoreCase, SeparatorMatching.Default)
            .Should().Be(3 + "open–source".Length - 1);
        TermMatch.MatchEnd(raw, 3, needle, StringComparison.OrdinalIgnoreCase, SeparatorMatching.Exact)
            .Should().Be(needle == "open-source" ? 3 + "open-source".Length - 1 : -1,
                "exact: the en dash folds to '-', so only the hyphenated term matches, as before");
        TermMatch.MatchEnd("open  -  source", 0, needle, StringComparison.OrdinalIgnoreCase, SeparatorMatching.Default)
            .Should().Be("open  -  source".Length - 1, "a raw separator run is matched whole");
    }
}
