using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text;
using Xunit;

namespace Excise.Core.Tests.Text;

/// <summary>
/// Unit tests for <see cref="ArabicPresentationForms"/> (#632): folding the
/// Arabic presentation-form blocks (U+FB50–U+FDFF, U+FE70–U+FEFF) to their
/// base-letter compatibility decompositions, and NOTHING else.
///
/// The expectations are the Unicode compatibility decompositions themselves —
/// deterministic ground truth, not any tool's reading.
/// </summary>
public class ArabicPresentationFormFoldingTests
{
    [Theory]
    [InlineData(0xFEB3, "س")] // seen initial → seen
    [InlineData(0xFEE1, "م")] // meem isolated → meem
    [InlineData(0xFE8D, "ا")] // alef isolated → alef
    [InlineData(0xFB50, "ٱ")] // alef wasla isolated → alef wasla (Forms-A)
    [InlineData(0xFE81, "آ")] // alef-with-madda isolated → composed U+0622, not U+0627+U+0653
    public void Fold_MapsPresentationFormToBaseLetter(int presentationScalar, string expected)
    {
        ArabicPresentationForms.Fold(((char)presentationScalar).ToString())
            .Should().Be(expected);
    }

    [Theory]
    [InlineData(0xFEFB)] // lam-alef isolated
    [InlineData(0xFEFC)] // lam-alef final
    public void Fold_ExpandsLamAlefLigatureToTwoLetters(int ligatureScalar)
    {
        ArabicPresentationForms.Fold(((char)ligatureScalar).ToString())
            .Should().Be("لا", "lam-alef is a 1 → 2 character expansion");
    }

    [Fact]
    public void Fold_ShapedWord_YieldsTypedBaseWord()
    {
        // "سلام" as a shaping engine stores it: seen-initial U+FEB3,
        // lam-alef-final U+FEFC, meem-isolated U+FEE1 (3 chars) → the 4 base
        // letters a user types.
        var shaped = new string(new[] { (char)0xFEB3, (char)0xFEFC, (char)0xFEE1 });
        ArabicPresentationForms.Fold(shaped).Should().Be("سلام");
    }

    [Fact]
    public void Fold_BaseArabic_IsUntouchedAndSameInstance()
    {
        const string baseWord = "سلام";
        ArabicPresentationForms.Fold(baseWord).Should().BeSameAs(baseWord,
            "text without presentation forms must pass through without allocation");
    }

    [Theory]
    [InlineData(new[] { 'h', 'i', ' ', '!' })]              // Latin
    [InlineData(new[] { (char)0xFB01, (char)0xFB02 })]      // fi/fl ligatures (Alphabetic Presentation Forms)
    [InlineData(new[] { (char)0xFB2A, (char)0xFB4F })]      // Hebrew presentation forms (out of scope: Arabic-only)
    [InlineData(new[] { (char)0xFF21, (char)0xFF10 })]      // fullwidth A, 0
    [InlineData(new[] { (char)0x2460, (char)0x00BD })]      // circled one, vulgar half
    public void Fold_LeavesNonArabicCompatibilityCharactersAlone(char[] chars)
    {
        var text = new string(chars);

        // Scope guard: this is NOT whole-string NFKC. Only the two Arabic
        // presentation blocks fold; every other compatibility character keeps
        // its identity so unrelated matching behavior cannot change.
        ArabicPresentationForms.Fold(text).Should().BeSameAs(text);
    }

    [Fact]
    public void Fold_MixedText_FoldsOnlyThePresentationForms()
    {
        // "abc " + shaped سلام + " " + fi-ligature + "xyz": only the shaped
        // Arabic folds; the Latin text and the fi ligature survive untouched.
        var input = "abc " +
            new string(new[] { (char)0xFEB3, (char)0xFEFC, (char)0xFEE1 }) +
            " " + (char)0xFB01 + "xyz";

        ArabicPresentationForms.Fold(input)
            .Should().Be("abc سلام " + (char)0xFB01 + "xyz");
    }

    [Fact]
    public void Fold_NoncharactersInsideBlockA_DoNotThrow()
    {
        // U+FDD0–U+FDEF are Unicode noncharacters inside Forms-A; they must
        // fold to themselves rather than crash string.Normalize.
        var noncharacter = ((char)0xFDD0).ToString();
        ArabicPresentationForms.Fold(noncharacter).Should().Be(noncharacter);
    }

    [Fact]
    public void ContainsPresentationForms_DetectsBothBlocks()
    {
        ArabicPresentationForms.ContainsPresentationForms(((char)0xFB50).ToString()).Should().BeTrue();
        ArabicPresentationForms.ContainsPresentationForms(((char)0xFEFC).ToString()).Should().BeTrue();
        ArabicPresentationForms.ContainsPresentationForms("سلام").Should().BeFalse();
        ArabicPresentationForms.ContainsPresentationForms("abc").Should().BeFalse();
        ArabicPresentationForms.ContainsPresentationForms(null).Should().BeFalse();
        ArabicPresentationForms.ContainsPresentationForms("").Should().BeFalse();
    }

    [Fact]
    public void Extraction_PreservesRawPresentationForms()
    {
        // Folding is for MATCHING only. page.Text must keep the raw shaped
        // code points: glyph-level removal pairs operator chars to letters
        // one-to-one on raw values, and extraction parity is measured against
        // independent extractors that report the raw /ToUnicode mapping.
        var pdf = RtlPdfFixtures.SingleTj(new[] { 0xFEB3, 0xFEFC, 0xFEE1 }, visualOrder: true);
        using var doc = PdfDocument.Open(pdf);

        var shaped = new string(new[] { (char)0xFEB3, (char)0xFEFC, (char)0xFEE1 });
        doc.GetPage(1).Text.Should().Be(shaped,
            "extraction must return the shaped characters as stored (in logical order), unfolded");
    }
}
