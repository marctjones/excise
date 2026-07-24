using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text;
using Xunit;

namespace Excise.Core.Tests.Text;

/// <summary>
/// Unit tests for <see cref="LatinLigatures"/> and
/// <see cref="PresentationFormFolding"/> (#722): folding the Latin ligature
/// code points U+FB00–U+FB06 to their plain-letter compatibility
/// decompositions, and NOTHING else.
///
/// The expectations are the Unicode compatibility decompositions themselves —
/// deterministic ground truth, not any tool's reading.
/// </summary>
public class LatinLigatureFoldingTests
{
    [Theory]
    [InlineData(0xFB00, "ff")]
    [InlineData(0xFB01, "fi")]
    [InlineData(0xFB02, "fl")]
    [InlineData(0xFB03, "ffi")]
    [InlineData(0xFB04, "ffl")]
    [InlineData(0xFB05, "st")] // ﬅ (long s-t): NFKC folds ſ → s too
    [InlineData(0xFB06, "st")] // ﬆ
    public void Fold_MapsLigatureToPlainLetters(int ligatureScalar, string expected)
    {
        var raw = ((char)ligatureScalar).ToString();

        LatinLigatures.Fold(raw).Should().Be(expected);

        // Cross-check against the deterministic Unicode ground truth: the
        // per-character NFKC decomposition itself.
        LatinLigatures.Fold(raw).Should().Be(raw.Normalize(NormalizationForm.FormKC));
    }

    [Fact]
    public void Fold_LigatedWord_YieldsTypedPlainWord()
    {
        // "office" as PDFs commonly store it: o + ﬃ + c + e (4 chars) → the
        // 6 plain letters a user types. A 1 → 3 expansion.
        var ligated = "o" + (char)0xFB03 + "ce";
        LatinLigatures.Fold(ligated).Should().Be("office");
    }

    [Fact]
    public void Fold_PlainText_IsUntouchedAndSameInstance()
    {
        const string plain = "office fluff";
        LatinLigatures.Fold(plain).Should().BeSameAs(plain,
            "text without ligature code points must pass through without allocation");
    }

    [Theory]
    [InlineData(new[] { (char)0xFB13, (char)0xFB17 })]      // Armenian ligatures (same block, out of scope)
    [InlineData(new[] { (char)0xFB1D, (char)0xFB2A, (char)0xFB4F })] // Hebrew presentation forms
    [InlineData(new[] { (char)0xFB50, (char)0xFEB3, (char)0xFEFC })] // Arabic presentation forms (Arabic helper's job, not this one's)
    [InlineData(new[] { (char)0xFF21, (char)0xFF10 })]      // fullwidth A, 0
    [InlineData(new[] { (char)0x2460, (char)0x00BD, (char)0x00B2 })] // circled one, vulgar half, superscript two
    [InlineData(new[] { (char)0x017F })]                    // bare long s (ſ) — only ligatures fold, not ſ itself
    public void Fold_LeavesOtherCompatibilityCharactersAlone(char[] chars)
    {
        var text = new string(chars);

        // Scope guard: this is NOT whole-string NFKC. Only U+FB00–U+FB06
        // fold; every other compatibility character keeps its identity so
        // unrelated matching behavior cannot change.
        LatinLigatures.Fold(text).Should().BeSameAs(text);
    }

    [Fact]
    public void Fold_MixedText_FoldsOnlyTheLigatures()
    {
        var input = "an o" + (char)0xFB03 + "ce ｆull " + (char)0xFB2A + " day";
        LatinLigatures.Fold(input)
            .Should().Be("an office ｆull " + (char)0xFB2A + " day",
                "the fullwidth ｆ and Hebrew presentation form must survive untouched");
    }

    [Fact]
    public void ContainsLigatures_DetectsTheBlock()
    {
        LatinLigatures.ContainsLigatures(((char)0xFB00).ToString()).Should().BeTrue();
        LatinLigatures.ContainsLigatures(((char)0xFB06).ToString()).Should().BeTrue();
        LatinLigatures.ContainsLigatures(((char)0xFB07).ToString()).Should().BeFalse();
        LatinLigatures.ContainsLigatures("office").Should().BeFalse();
        LatinLigatures.ContainsLigatures(null).Should().BeFalse();
        LatinLigatures.ContainsLigatures("").Should().BeFalse();
    }

    [Fact]
    public void CombinedFold_FoldsBothLatinAndArabicInOnePass()
    {
        // One string carrying both a Latin ligature and shaped Arabic: the
        // combined helper folds both; everything else keeps identity.
        var input = "o" + (char)0xFB03 + "ce " +
            new string(new[] { (char)0xFEB3, (char)0xFEFC, (char)0xFEE1 }) +
            " " + (char)0xFF21;

        PresentationFormFolding.Fold(input)
            .Should().Be("office سلام " + (char)0xFF21);
    }

    [Fact]
    public void CombinedFold_UnaffectedText_IsSameInstance()
    {
        const string plain = "hello سلام world";
        PresentationFormFolding.Fold(plain).Should().BeSameAs(plain);
    }

    [Fact]
    public void CombinedContainsFoldable_DetectsBothScopes()
    {
        PresentationFormFolding.ContainsFoldable(((char)0xFB01).ToString()).Should().BeTrue();
        PresentationFormFolding.ContainsFoldable(((char)0xFEB3).ToString()).Should().BeTrue();
        PresentationFormFolding.ContainsFoldable(((char)0xFB2A).ToString()).Should().BeFalse("Hebrew presentation forms are out of scope");
        PresentationFormFolding.ContainsFoldable("office").Should().BeFalse();
        PresentationFormFolding.ContainsFoldable(null).Should().BeFalse();
    }

    [Fact]
    public void Extraction_PreservesRawLigatures()
    {
        // Folding is for MATCHING only. page.Text must keep the raw ligature
        // code points: glyph-level removal pairs operator chars to letters
        // one-to-one on raw values, and extraction parity is measured against
        // independent extractors that report the raw /ToUnicode mapping.
        var pdf = RtlPdfFixtures.SingleTj(new[] { 'o', 0xFB03, 'c', 'e' }, visualOrder: false);
        using var doc = PdfDocument.Open(pdf);

        doc.GetPage(1).Text.Should().Be("o" + (char)0xFB03 + "ce",
            "extraction must return the ligature code point as stored, unfolded");
    }
}
