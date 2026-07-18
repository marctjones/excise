using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text;
using Excise.Avalonia.Services;
using Xunit;

namespace Excise.App.Tests.Unit;

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
}
