using AwesomeAssertions;
using Excise.Rendering.Fonts;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Fonts;

/// <summary>
/// #1363: the complex-script FreeText typesetter, tested against properties
/// the Unicode Standard fixes, not against earlier excise output.
/// <list type="bullet">
///   <item>Arabic letters take positional joining forms (Unicode ArabicShaping).
///     Unjoined isolated forms are the plausible-wrong output this typesetter
///     exists to prevent.</item>
///   <item>A right-to-left paragraph runs right to left, with left-to-right
///     runs and digits keeping their own order inside it (UBA P2/P3, L2).</item>
///   <item>With no covering font, nothing is typeset at all.</item>
/// </list>
/// Every call that touches SkiaSharp's font manager holds
/// <see cref="FontManagerLock.Instance"/>, as the renderer does.
/// </summary>
public class AnnotationTypesetterTests
{
    private const float Size = 20f;
    private const string Beh = "ب";
    private const string Teh = "ت";

    [Fact]
    public void SplitParagraphs_TreatsCrLfAsOneBreakAndKeepsBlankLines()
    {
        AnnotationTypesetter.SplitParagraphs("a\r\nb\rc\nd\n\ne")
            .Should().Equal("a", "b", "c", "d", "", "e");
    }

    [Fact]
    public void ParagraphDirection_ComesFromTheFirstStrongCharacter()
    {
        AnnotationTypesetter.IsRightToLeftParagraph("123 " + Beh + " abc").Should().BeTrue(
            "digits are not strong (UBA P2), so the first strong character is the Arabic beh");
        AnnotationTypesetter.IsRightToLeftParagraph("(abc " + Beh + ")").Should().BeFalse();
        AnnotationTypesetter.IsRightToLeftParagraph("123 !?").Should().BeFalse(
            "a paragraph with no strong character defaults to left-to-right (UBA P3)");
    }

    [Fact]
    public void ArabicLetters_AreShapedIntoTheirJoiningForms()
    {
        var (primary, fallback) = RequireArabicFallback();

        var isolated = SingleRun(Typeset(Beh, primary, fallback)).Glyphs;
        var joined = SingleRun(Typeset(Beh + Beh + Beh, primary, fallback)).Glyphs;

        joined.Should().NotEqual(isolated.Concat(isolated).Concat(isolated),
            "three joined behs take initial, medial and final forms; three copies of the " +
            "isolated glyph is exactly the unshaped output #1363 must never draw");
        joined.Distinct().Count().Should().BeGreaterThan(isolated.Distinct().Count(),
            "positional forms are distinct glyphs");
    }

    [Fact]
    public void RightToLeftParagraph_PlacesTheFirstLogicalWordRightmost()
    {
        var (primary, fallback) = RequireArabicFallback();
        var beh = SingleRun(Typeset(Beh, primary, fallback)).Glyphs;
        var teh = SingleRun(Typeset(Teh, primary, fallback)).Glyphs;

        var line = Typeset(Beh + " " + Teh, primary, fallback).Should().ContainSingle().Subject;

        line.RightToLeft.Should().BeTrue();
        line.Runs.Should().HaveCount(2);
        line.Runs[0].Glyphs.Should().Equal(teh, "the SECOND logical word is leftmost in an RTL line");
        line.Runs[1].Glyphs.Should().Equal(beh, "the FIRST logical word is rightmost");
        line.Runs[1].Positions[0].X.Should().BeGreaterThan(line.Runs[0].Positions[0].X);
    }

    [Fact]
    public void LatinWordsInsideAnRtlParagraph_KeepTheirLogicalOrder()
    {
        var (primary, fallback) = RequireArabicFallback();
        Assert.SkipWhen(primary.GetGlyph('a') == 0, "the primary typeface has no Latin glyphs");

        var line = Typeset(Beh + " abc def", primary, fallback).Should().ContainSingle().Subject;

        // UBA L2 with the Latin run at level 2 inside a level-1 paragraph:
        // visually "abc def" then the beh, reading right to left as
        // beh, "abc def".
        line.Runs.Should().HaveCount(3);
        line.Runs[0].Glyphs.Should().Equal(primary.GetGlyphs("abc"));
        line.Runs[1].Glyphs.Should().Equal(primary.GetGlyphs("def"));
        line.Runs[2].Glyphs.Should().Equal(SingleRun(Typeset(Beh, primary, fallback)).Glyphs);
        line.Runs[0].Positions[0].X.Should().BeLessThan(line.Runs[1].Positions[0].X);
        line.Runs[1].Positions[0].X.Should().BeLessThan(line.Runs[2].Positions[0].X);
    }

    [Fact]
    public void DigitsInsideAnRtlParagraph_AreNotReversed()
    {
        var (primary, fallback) = RequireArabicFallback();
        Assert.SkipWhen(primary.GetGlyph('1') == 0, "the primary typeface has no digit glyphs");

        var line = Typeset(Beh + " 123", primary, fallback).Should().ContainSingle().Subject;

        line.Runs.Should().HaveCount(2);
        line.Runs[0].Glyphs.Should().Equal(primary.GetGlyphs("123"),
            "a number reads left to right even in an Arabic sentence. Reversed digits " +
            "state a different number");
    }

    [Fact]
    public void ArabicWordsInsideAnLtrParagraph_RunRightToLeftAsAGroup()
    {
        var (primary, fallback) = RequireArabicFallback();
        Assert.SkipWhen(primary.GetGlyph('a') == 0, "the primary typeface has no Latin glyphs");

        var line = Typeset("abc " + Beh + " " + Teh, primary, fallback).Should().ContainSingle().Subject;

        line.RightToLeft.Should().BeFalse();
        line.Runs.Should().HaveCount(3);
        line.Runs[0].Glyphs.Should().Equal(primary.GetGlyphs("abc"));
        line.Runs[1].Glyphs.Should().Equal(SingleRun(Typeset(Teh, primary, fallback)).Glyphs,
            "the two Arabic words form one RTL group, so the later word is to the left");
        line.Runs[2].Glyphs.Should().Equal(SingleRun(Typeset(Beh, primary, fallback)).Glyphs);
    }

    [Fact]
    public void EveryParagraph_StartsANewLine()
    {
        var (primary, fallback) = RequireArabicFallback();

        Typeset(Beh + "\n" + Teh + "\r\n" + Beh, primary, fallback).Should().HaveCount(3);
    }

    [Fact]
    public void ALongParagraph_WrapsWithinTheWidth()
    {
        var (primary, fallback) = RequireArabicFallback();
        var text = string.Join(" ", Enumerable.Repeat(Beh + Teh + Beh, 20));

        var lines = Typeset(text, primary, fallback, maxWidth: 120f);

        lines.Count.Should().BeGreaterThan(1);
        lines.Should().OnlyContain(l => l.Width <= 120f + 0.01f,
            "no line of several short words may exceed the wrap width");
        lines.Sum(l => l.Runs.Count).Should().Be(20, "wrapping must not drop or duplicate a word");
    }

    [Fact]
    public void NoFontCoversTheScript_TypesetsNothing()
    {
        lock (FontManagerLock.Instance)
        {
            var primary = SKTypeface.FromFamilyName("Helvetica") ?? SKTypeface.Default;
            Assert.SkipWhen(primary.GetGlyph(0x0627) != 0,
                $"the primary typeface ({primary.FamilyName}) covers Arabic, so it cannot stand in for an uncovered script");

            var lines = AnnotationTypesetter.Typeset(
                "العربية", primary, Size, 500f, _ => null, out var failure);

            lines.Should().BeNull("with no covering font, the typesetter fails closed rather than laying out .notdef");
            failure.Should().Contain("U+0627");
        }
    }

    private static (SKTypeface Primary, Func<int, SKTypeface?> Fallback) RequireArabicFallback()
    {
        lock (FontManagerLock.Instance)
        {
            var primary = SKTypeface.FromFamilyName("Helvetica") ?? SKTypeface.Default;
            var arabic = SKFontManager.Default.MatchCharacter(0x0628);
            Assert.SkipWhen(arabic == null,
                "No system font covers Arabic (U+0628), so there is nothing to shape with. " +
                "NoFontCoversTheScript_TypesetsNothing covers the fail-closed branch.");
            return (primary, cp => SKFontManager.Default.MatchCharacter(cp));
        }
    }

    private static IReadOnlyList<AnnotationTypesetter.TypesetLine> Typeset(
        string text, SKTypeface primary, Func<int, SKTypeface?> fallback, float maxWidth = 10_000f)
    {
        lock (FontManagerLock.Instance)
        {
            var lines = AnnotationTypesetter.Typeset(text, primary, Size, maxWidth, fallback, out var failure);
            lines.Should().NotBeNull($"typesetting \"{text}\" failed: {failure}");
            return lines!;
        }
    }

    private static AnnotationTypesetter.GlyphRun SingleRun(IReadOnlyList<AnnotationTypesetter.TypesetLine> lines)
    {
        var line = lines.Should().ContainSingle().Subject;
        return line.Runs.Should().ContainSingle().Subject;
    }
}
