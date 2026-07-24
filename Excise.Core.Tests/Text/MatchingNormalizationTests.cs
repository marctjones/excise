using System;
using AwesomeAssertions;
using Excise.Core.Text;
using Xunit;

namespace Excise.Core.Tests.Text;

/// <summary>
/// Direct unit coverage of the composite matching fold
/// (<see cref="MatchingNormalization"/>): stage behavior, composition
/// order, the per-letter <see cref="MatchingNormalization.FoldAll"/>
/// cluster contract, identity fast paths, and tolerance of the malformed
/// sequences PDF /ToUnicode maps can produce. Ground truth throughout is
/// deterministic Unicode data (canonical/compatibility decompositions and
/// the code points themselves), not a tool's opinion.
/// Stages: presentation forms/ligatures (#632/#722) → canonical NFC
/// (#724) → harakat/niqqud strip (#725) → invisible separators (#726) →
/// halfwidth/fullwidth forms (#727).
/// </summary>
public class MatchingNormalizationTests
{
    // ---- Stage 2: canonical NFC (#724) ----

    [Fact]
    public void Fold_ComposesDecomposedAccents()
    {
        MatchingNormalization.Fold("cafe\u0301").Should().Be("caf\u00E9");
        MatchingNormalization.Fold("caf\u00E9").Should().Be("caf\u00E9");
    }

    [Fact]
    public void Fold_IsCanonicalOnly_NoNfkcCreep()
    {
        // Compatibility characters outside the explicitly folded blocks
        // keep their identity: superscript two, circled one, ℡.
        MatchingNormalization.Fold("\u00B2").Should().Be("\u00B2");
        MatchingNormalization.Fold("\u2460").Should().Be("\u2460");
        MatchingNormalization.Fold("\u2121").Should().Be("\u2121");
    }

    // ---- Stage 3: harakat/niqqud strip (#725) ----

    [Fact]
    public void Fold_StripsArabicHarakat()
    {
        MatchingNormalization.Fold("\u0643\u064E\u062A\u064E\u0628\u064E")
            .Should().Be("\u0643\u062A\u0628");
    }

    [Fact]
    public void Fold_StripsHebrewNiqqud()
    {
        MatchingNormalization.Fold("\u05E9\u05C1\u05B8\u05DC\u05D5\u05B9\u05DD")
            .Should().Be("\u05E9\u05DC\u05D5\u05DD");
    }

    [Fact]
    public void Fold_KeepsHebrewPunctuationInTheBlock()
    {
        // Maqaf, paseq, sof pasuq, nun hafukha are punctuation, not points.
        MatchingNormalization.Fold("\u05D0\u05BE\u05D1").Should().Be("\u05D0\u05BE\u05D1");
        MatchingNormalization.Fold("\u05C0\u05C3\u05C6").Should().Be("\u05C0\u05C3\u05C6");
    }

    [Fact]
    public void Fold_NfcRunsBeforeTheStrip_SoComposingHamzaSurvives()
    {
        // alef + hamza-above (U+0654, inside the stripped range) composes
        // to U+0623 under NFC FIRST — the hamza is preserved inside the
        // composed letter, not stripped away.
        MatchingNormalization.Fold("\u0627\u0654").Should().Be("\u0623");
    }

    [Fact]
    public void Fold_KeepsLatinCombiningAccents()
    {
        // The strip is Arabic/Hebrew only; a Latin accent survives (as its
        // NFC composition).
        MatchingNormalization.Fold("n\u0303").Should().Be("\u00F1");
    }

    // ---- Stage 4: invisible separators (#726) ----

    [Theory]
    [InlineData("\u00AD")]
    [InlineData("\u200B")]
    [InlineData("\u200C")]
    [InlineData("\u200D")]
    [InlineData("\uFEFF")]
    public void Fold_RemovesInvisibleSeparators(string separator)
    {
        MatchingNormalization.Fold("se" + separator + "cret").Should().Be("secret");
    }

    [Fact]
    public void Fold_MapsNonBreakingSpaceToSpace()
    {
        MatchingNormalization.Fold("top\u00A0secret").Should().Be("top secret");
    }

    [Fact]
    public void Fold_KeepsRealHyphen()
    {
        MatchingNormalization.Fold("se-cret").Should().Be("se-cret");
    }

    // ---- Stage 5: halfwidth/fullwidth (#727) ----

    [Fact]
    public void Fold_FoldsFullwidthAsciiToAscii()
    {
        MatchingNormalization.Fold("\uFF21\uFF22\uFF23").Should().Be("ABC");
        MatchingNormalization.Fold("\uFF11\uFF12\uFF13").Should().Be("123");
    }

    [Fact]
    public void Fold_FoldsHalfwidthKatakana_AndComposesVoicedPairs()
    {
        MatchingNormalization.Fold("\uFF76\uFF80").Should().Be("\u30AB\u30BF");
        // ｶ + halfwidth voiced mark ﾞ → カ + U+3099 → composes to ガ.
        MatchingNormalization.Fold("\uFF76\uFF9E").Should().Be("\u30AC");
    }

    // ---- Earlier stages still compose (#632/#722 regression) ----

    [Fact]
    public void Fold_StillFoldsLigaturesAndArabicPresentationForms()
    {
        MatchingNormalization.Fold("o\uFB03ce").Should().Be("office");
        MatchingNormalization.Fold("\uFEB3").Should().Be("\u0633");
    }

    // ---- Contracts ----

    [Fact]
    public void Fold_ReturnsSameInstance_WhenNothingChanges()
    {
        var s = "plain ASCII text 123";
        MatchingNormalization.Fold(s).Should().BeSameAs(s,
            "the identity fast path keeps unaffected haystacks allocation-free");
    }

    [Fact]
    public void Fold_ToleratesUnpairedSurrogates()
    {
        // Broken /ToUnicode maps can yield lone surrogates; folding must
        // not throw, and matching degrades to exact comparison.
        var broken = "abc" + (char)0xD800 + "def";
        MatchingNormalization.Fold(broken).Should().Be(broken);
    }

    [Fact]
    public void FoldAll_ConcatenationEqualsWholeStringFold()
    {
        // The per-letter contract: concat(FoldAll(values)) == Fold(concat).
        var values = new[] { "c", "a", "f", "e", "\u0301", " ", "\uFF76", "\uFF9E" };
        var folded = MatchingNormalization.FoldAll(values);

        string.Concat(folded).Should().Be(
            MatchingNormalization.Fold(string.Concat(values)));
    }

    [Fact]
    public void FoldAll_MergesCombiningMarkIntoPrecedingLetter()
    {
        var folded = MatchingNormalization.FoldAll(new[] { "e", "\u0301" });

        folded[0].Should().Be("\u00E9", "the accent composes into the base letter's cluster");
        folded[1].Should().BeEmpty("the mark letter contributes zero folded length");
    }

    [Fact]
    public void FoldAll_StrippedMarkLettersFoldToEmpty()
    {
        var folded = MatchingNormalization.FoldAll(new[] { "\u0643", "\u064E" });

        folded[0].Should().Be("\u0643");
        folded[1].Should().BeEmpty("harakat letters vanish in folded space");
    }
}
