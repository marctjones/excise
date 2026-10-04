using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Text;

/// <summary>
/// Pure-logic tests for the text-selection engine — hit-testing,
/// reading-order sort, and range-between. Synthetic Letter inputs only
/// (no PDF needed) so these are fast and deterministic.
/// </summary>
public class TextSelectionEngineTests
{
    /// <summary>Build a Letter with synthetic geometry. Y is bottom-left origin (PDF).</summary>
    private static Letter L(string value, double left, double bottom, double width, double height)
    {
        var rect = new PdfRectangle(left, bottom, left + width, bottom + height);
        return new Letter(value, rect, fontSize: height,
            fontName: "Helvetica", startX: left, startY: bottom,
            width: width, characterCode: value[0]);
    }

    [Fact]
    public void HitTest_PointInsideGlyph_ReturnsThatLetter()
    {
        var letters = new[] { L("A", 10, 100, 8, 12), L("B", 18, 100, 8, 12) };
        var hit = TextSelectionEngine.HitTest(letters, 14, 106);
        hit!.Value.Should().Be("A");
    }

    [Fact]
    public void HitTest_PointBetweenGlyphs_ReturnsNearestOnSameLine()
    {
        // Two glyphs on the same line, pointer in the gap between them
        // closer to the right one.
        var letters = new[] { L("A", 10, 100, 8, 12), L("B", 30, 100, 8, 12) };
        var hit = TextSelectionEngine.HitTest(letters, 27, 106);
        hit!.Value.Should().Be("B");
    }

    [Fact]
    public void HitTest_PointFarFromLine_PrefersSameLine()
    {
        // Two lines vertically separated. Pointer on the *upper* line
        // X-position, slightly off horizontally — must NOT pick the
        // lower line just because that line happens to have a closer X.
        var letters = new[]
        {
            L("U1", 10, 100, 8, 12), L("U2", 18, 100, 8, 12),  // upper line baseline 100
            L("D1", 10, 60,  8, 12), L("D2", 18, 60,  8, 12),  // lower line baseline 60
        };
        // Pointer above the upper line slightly, X aligned with U1.
        var hit = TextSelectionEngine.HitTest(letters, 14, 109);
        hit!.Value.Should().Be("U1");
    }

    [Fact]
    public void SortReadingOrder_TopToBottomLeftToRight()
    {
        // Two lines, glyphs arrived in a non-reading order.
        var letters = new[]
        {
            L("D2", 18, 60, 8, 12), L("U1", 10, 100, 8, 12),
            L("D1", 10, 60, 8, 12), L("U2", 18, 100, 8, 12),
        };
        var ordered = TextSelectionEngine.SortReadingOrder(letters);
        string.Join("", ordered.Select(l => l.Value))
            .Should().Be("U1U2D1D2");
    }

    [Fact]
    public void RangeBetween_InclusiveOfBothEndpoints()
    {
        var letters = new[]
        {
            L("a", 10, 100, 8, 12),
            L("b", 18, 100, 8, 12),
            L("c", 26, 100, 8, 12),
            L("d", 34, 100, 8, 12),
        };
        var ordered = TextSelectionEngine.SortReadingOrder(letters);
        var range = TextSelectionEngine.RangeBetween(ordered, ordered[0], ordered[2]);
        string.Join("", range.Select(l => l.Value)).Should().Be("abc");

        // Reverse direction (focus before anchor) — same range.
        var reverse = TextSelectionEngine.RangeBetween(ordered, ordered[2], ordered[0]);
        string.Join("", reverse.Select(l => l.Value)).Should().Be("abc");
    }

    [Fact]
    public void SortReadingOrder_StackedHorizontalDigits_DoesNotGuessVerticalWriting()
    {
        // #1915: four one-glyph horizontal lines, painted bottom-to-top.
        var letters = new[]
        {
            L("1", 10, 60, 8, 12), L("2", 10, 80, 8, 12),
            L("3", 10, 100, 8, 12), L("4", 10, 120, 8, 12),
        };

        string.Concat(TextSelectionEngine.SortReadingOrder(letters).Select(l => l.Value))
            .Should().Be("4321", "horizontal writing is not a vertical run merely because X stays constant");
    }

    [Fact]
    public void SortReadingOrder_ShortVerticalColumns_KeepsProducerOrder()
    {
        // #1915: two glyphs per column cannot satisfy the old three-pair guess.
        var letters = new[]
        {
            new Letter("日", new PdfRectangle(388, 276, 412, 300), 24, "F", 400, 300, 24, 1) { IsVerticalWriting = true },
            new Letter("本", new PdfRectangle(388, 252, 412, 276), 24, "F", 400, 276, 24, 2) { IsVerticalWriting = true },
            new Letter("語", new PdfRectangle(338, 276, 362, 300), 24, "F", 350, 300, 24, 3) { IsVerticalWriting = true },
            new Letter("文", new PdfRectangle(338, 252, 362, 276), 24, "F", 350, 276, 24, 4) { IsVerticalWriting = true },
        };

        TextSelectionEngine.SortReadingOrder(letters).Should().Equal(letters,
            "WMode, not run length or page geometry, defines vertical writing");
    }

    [Fact]
    public void RangeBetween_AcrossLines_FollowsReadingOrder()
    {
        var letters = new[]
        {
            L("U1", 10, 100, 8, 12), L("U2", 18, 100, 8, 12), L("U3", 26, 100, 8, 12),
            L("D1", 10,  60, 8, 12), L("D2", 18,  60, 8, 12), L("D3", 26,  60, 8, 12),
        };
        var ordered = TextSelectionEngine.SortReadingOrder(letters);
        // Anchor at U2, focus at D2 — selection should include U2,U3,D1,D2.
        var anchor = ordered.First(l => l.Value == "U2");
        var focus = ordered.First(l => l.Value == "D2");
        var range = TextSelectionEngine.RangeBetween(ordered, anchor, focus);
        string.Join("", range.Select(l => l.Value)).Should().Be("U2U3D1D2");
    }

    [Fact]
    public void JoinText_InsertsWordSpacesAndLineBreaks()
    {
        // "h e l l o[gap]w o r l d" — single big gap should produce a space.
        var line1 = new[]
        {
            L("h", 10, 100, 6, 10), L("e", 16, 100, 6, 10),
            L("l", 22, 100, 6, 10), L("l", 28, 100, 6, 10), L("o", 34, 100, 6, 10),
            // gap > half line height
            L("w", 50, 100, 6, 10), L("o", 56, 100, 6, 10),
            L("r", 62, 100, 6, 10), L("l", 68, 100, 6, 10), L("d", 74, 100, 6, 10),
        };
        // Second line.
        var line2 = new[]
        {
            L("n", 10, 80, 6, 10), L("e", 16, 80, 6, 10),
            L("x", 22, 80, 6, 10), L("t", 28, 80, 6, 10),
        };
        var ordered = TextSelectionEngine.SortReadingOrder(line1.Concat(line2));
        var text = TextSelectionEngine.JoinText(ordered);
        text.Should().Be("hello world\nnext");
    }

    [Theory] // #1902: a horizontal line and a vertical column never share a line
    [InlineData(WhitespaceMode.LineFaithful)]
    [InlineData(WhitespaceMode.Smart)]
    public void JoinText_HorizontalThenVerticalColumn_BreaksAtTheWritingModeChange(WhitespaceMode mode)
    {
        // The header's Y-centre (400) equals the column's X-centre (400), so the
        // two axes coincide: only the writing-mode change separates them.
        var letters = new[]
        {
            L("A", 100, 394, 8, 12), L("B", 108, 394, 8, 12),
            new Letter("日", new PdfRectangle(388, 276, 412, 300), 24, "F", 400, 300, 24, 0x65E5) { IsVerticalWriting = true },
            new Letter("本", new PdfRectangle(388, 252, 412, 276), 24, "F", 400, 276, 24, 0x672C) { IsVerticalWriting = true },
        };

        TextSelectionEngine.JoinText(letters, mode).Should().Be("AB\n日本");
    }

    /// <summary>
    /// #1834: an area reads like a drag across it (logical order for a line
    /// painted right to left), and takes its letters by the removal strategy:
    /// the area overlaps 1 pt of the leftmost glyph and stops short of "x".
    /// </summary>
    [Fact]
    public void SelectInRectangle_ReadsLikeADragAcrossTheArea_ByTheRemovalStrategy()
    {
        var letters = new[]
        {
            L("س", 40, 100, 8, 12), L("ل", 30, 100, 8, 12),
            L("ا", 20, 100, 8, 12), L("م", 10, 100, 8, 12),
            L("x", 60, 100, 8, 12),
        };
        var area = new PdfRectangle(17, 95, 50, 115);
        var reading = TextSelectionEngine.SortReadingOrder(letters);
        var drag = TextSelectionEngine.ToLogicalOrder(
            TextSelectionEngine.ColumnAwareRange(
                reading, reading[0], reading[3], TextSelectionEngine.EstimateColumnGap(reading)),
            letters);

        Text(TextSelectionEngine.SelectInRectangle(letters, area, GlyphRemovalStrategy.AnyOverlap))
            .Should().Be(Text(drag)).And.Be("سلام");
        Text(TextSelectionEngine.SelectInRectangle(letters, area, GlyphRemovalStrategy.FullyContained))
            .Should().Be("سلا");
    }

    private static string Text(System.Collections.Generic.IEnumerable<Letter> letters) =>
        string.Concat(letters.Select(l => l.Value));
}
