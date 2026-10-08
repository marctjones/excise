using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Core.Text;
using Excise.TestSupport;
using Xunit;

namespace Excise.Core.Tests.Document;

/// <summary>
/// #2009: a selection that spans lines is written as one /QuadPoints quad per line and a
/// /Rect that is their union, so a viewer does not paint the gap between the lines. The
/// independent render and qpdf checks live in RotatedMarkupPlacementTests; these pin the
/// authoring contract and the line grouping the viewer feeds it.
/// </summary>
public class MultiLineTextMarkupTests
{
    private static readonly PdfRectangle Line1 = new(100, 700, 300, 718);
    private static readonly PdfRectangle Line2 = new(100, 660, 220, 678);

    private static PdfDocument NewPage()
    {
        var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();
        return doc;
    }

    private static PdfAnnotation Add(PdfDocument doc, string kind, params PdfRectangle[] lines) => kind switch
    {
        "Highlight" => doc.AddHighlightAnnotation(1, lines),
        "Underline" => doc.AddUnderlineAnnotation(1, lines),
        "StrikeOut" => doc.AddStrikeOutAnnotation(1, lines),
        _ => doc.AddSquigglyAnnotation(1, lines),
    };

    [Theory]
    [InlineData("Highlight")]
    [InlineData("Underline")]
    [InlineData("StrikeOut")]
    [InlineData("Squiggly")]
    public void TwoLines_AreTwoQuads_AndTheRectIsTheirUnion(string kind)
    {
        using var doc = NewPage();

        var annotation = Add(doc, kind, Line1, Line2);

        annotation.QuadPoints.Should().HaveCount(2, "one quad per line");
        annotation.QuadPoints![0].Should().Be(Line1);
        annotation.QuadPoints![1].Should().Be(Line2);
        var raw = (PdfArray)doc.Resolve(annotation.RawDictionary.GetOptional("QuadPoints")!);
        raw.Count.Should().Be(16);

        // No quad covers the band between the lines (y 678..700).
        annotation.QuadPoints!.Any(q => q.Bottom < 690 && q.Top > 690).Should().BeFalse();

        var rect = annotation.Rect;
        rect.Left.Should().BeLessThanOrEqualTo(100);
        rect.Right.Should().BeGreaterThanOrEqualTo(300);
        rect.Top.Should().BeGreaterThanOrEqualTo(718);
        rect.Bottom.Should().BeLessThanOrEqualTo(660);
    }

    [Theory]
    [InlineData("Underline")]
    [InlineData("StrikeOut")]
    public void TwoLines_DrawOneStrokePerLine_NotOneAcrossTheBox(string kind)
    {
        using var doc = NewPage();

        var annotation = Add(doc, kind, Line1, Line2);

        var ap = (PdfDictionary)doc.Resolve(annotation.RawDictionary.GetOptional("AP")!);
        var ops = ((PdfStream)doc.Resolve(ap.GetOptional("N")!)).GetDecodedString();
        ops.Split(" m\n").Length.Should().Be(3, "two moveto operations");
        ops.Split("S\n").Length.Should().Be(3, "two strokes");
    }

    [Theory]
    [InlineData("Highlight")]
    [InlineData("Underline")]
    public void OneLine_ListOverload_IsTheSingleRectOverload(string kind)
    {
        using var doc = NewPage();
        var single = kind == "Highlight" ? doc.AddHighlightAnnotation(1, Line1) : doc.AddUnderlineAnnotation(1, Line1);
        var list = Add(doc, kind, Line1);

        string Numbers(PdfAnnotation a) => string.Join(",",
            ((PdfArray)doc.Resolve(a.RawDictionary.GetOptional("QuadPoints")!)).Select(o => o.ToString()));
        Numbers(list).Should().Be(Numbers(single));
        list.Rect.Should().Be(single.Rect);
    }

    [Fact]
    public void NoLines_IsRejected()
    {
        using var doc = NewPage();

        var act = () => doc.AddHighlightAnnotation(1, System.Array.Empty<PdfRectangle>());

        act.Should().Throw<System.ArgumentException>();
    }

    // ── The line grouping the viewer uses to build those rectangles ──────────

    private static System.Collections.Generic.IReadOnlyList<Letter> LettersOf(string probeId)
    {
        using var doc = PdfDocument.Open(RotationProbes.Build(probeId));
        return doc.GetPage(1).Letters.Where(l => !char.IsWhiteSpace(l.Value[0])).ToList();
    }

    [Fact]
    public void LineRectangles_OfAWholePage_AreOnePerLine()
    {
        var letters = LettersOf("probe-r0");

        var lines = TextSelectionEngine.LineRectangles(letters);

        lines.Should().HaveCount(2);
        lines[0].Bottom.Should().BeGreaterThan(lines[1].Top, "line 1 sits above line 2, with a gap between");
    }

    [Fact]
    public void LineRectangles_OfAMidLineToMidLineSelection_CoverOnlyTheSelectedGlyphs()
    {
        // "ALPHA PROBE ONE" / "BRAVO PROBE TWO" without spaces: 13 letters on line 1.
        var letters = LettersOf("probe-r0");
        string.Concat(letters.Take(18).Select(l => l.Value)).Should().Be("ALPHAPROBEONEBRAVO");
        var slice = letters.Skip(5).Take(13).ToList(); // PROBE ONE | BRAVO

        var lines = TextSelectionEngine.LineRectangles(slice);

        lines.Should().HaveCount(2);
        lines[0].Left.Should().BeGreaterThan(letters[4].GlyphRectangle.Right - 0.01, "line 1 starts at PROBE, not at ALPHA");
        lines[0].Right.Should().BeApproximately(letters[12].GlyphRectangle.Right, 0.01, "line 1 ends after ONE");
        lines[1].Left.Should().BeLessThan(lines[0].Left, "line 2 starts at BRAVO, at the page margin");
        lines[1].Right.Should().BeApproximately(letters[17].GlyphRectangle.Right, 0.01, "line 2 ends after BRAVO, before PROBE and TWO");
    }

    [Fact]
    public void LineRectangles_OfTextTurnedInUserSpace_AreStillOneLine()
    {
        // The text runs along user-space y; grouping by page y would split it per glyph.
        var letters = LettersOf("probe-r90-textccw");

        TextSelectionEngine.LineRectangles(letters).Should().HaveCount(1);
    }

    [Fact]
    public void LineRectangles_OfNothing_IsEmpty()
    {
        TextSelectionEngine.LineRectangles(System.Array.Empty<Letter>()).Should().BeEmpty();
    }
}
