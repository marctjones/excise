using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text;
using Xunit;

namespace Excise.Core.Tests.Text;

/// <summary>
/// Whole-page structure-gate coverage for #938/#947. Selection sorting can
/// accept ambiguous geometry because the user bounded the range; page.Text
/// must retain producer order unless the complete page proves interleaved
/// two-column prose.
/// </summary>
public class PageTextOrderTests
{
    [Fact]
    public void DeterminePageTextOrder_RowInterleavedPairedProse_UsesColumns()
    {
        var letters = CreateTwoColumnRows(proseRows: 8, tableRows: 0, rowMajor: true);

        TextSelectionEngine.DeterminePageTextOrder(letters)
            .Should().Be(TextSelectionEngine.PageTextOrderStrategy.ColumnAware);
    }

    [Fact]
    public void SortPageTextOrder_RowInterleavedPairedProse_OrdersColumnsWithoutLosingGlyphs()
    {
        var letters = CreateTwoColumnRows(proseRows: 8, tableRows: 0, rowMajor: true);

        var ordered = TextSelectionEngine.SortPageTextOrder(letters);
        var text = string.Concat(ordered.Select(letter => letter.Value));

        text.IndexOf("Left prose row 08", StringComparison.Ordinal).Should().BeLessThan(
            text.IndexOf("Right prose row 01", StringComparison.Ordinal));
        ordered.Should().HaveCount(letters.Count);
        ordered.Distinct().Should().HaveCount(letters.Count,
            "whole-page sorting must emit every original glyph exactly once");
        new HashSet<Letter>(ordered).SetEquals(letters).Should().BeTrue(
            "whole-page sorting must return the original glyph objects, not reconstructed substitutes");
    }

    [Fact]
    public void DeterminePageTextOrder_AlreadyColumnMajorProse_KeepsProducerOrder()
    {
        var letters = CreateTwoColumnRows(proseRows: 8, tableRows: 0, rowMajor: false);

        TextSelectionEngine.DeterminePageTextOrder(letters)
            .Should().Be(TextSelectionEngine.PageTextOrderStrategy.RawStream,
                "a producer that already emits complete columns has no repeated backtracks to repair");
        TextSelectionEngine.SortPageTextOrder(letters).Should().Equal(letters);
    }

    [Fact]
    public void DeterminePageTextOrder_SevenPairedRows_KeepsProducerOrder()
    {
        var letters = CreateTwoColumnRows(proseRows: 7, tableRows: 0, rowMajor: true);

        TextSelectionEngine.DeterminePageTextOrder(letters)
            .Should().Be(TextSelectionEngine.PageTextOrderStrategy.RawStream,
                "#947 pins the lower side of the bounded eight-line structure threshold");
    }

    [Fact]
    public void DeterminePageTextOrder_SparseFormGrid_KeepsProducerOrder()
    {
        var letters = new List<Letter>();
        for (var row = 1; row <= 12; row++)
        {
            var y = 740 - row * 24;
            letters.AddRange(CreateRun(row % 2 == 0 ? "Name" : "Date", 40, y));
            letters.AddRange(CreateRun(row % 2 == 0 ? "Value" : "Code", 360, y));
        }

        TextSelectionEngine.DeterminePageTextOrder(letters)
            .Should().Be(TextSelectionEngine.PageTextOrderStrategy.RawStream,
                "short field labels are a form grid, not two parallel prose columns");
    }

    [Fact]
    public void DeterminePageTextOrder_NumericTable_KeepsRowsTogether()
    {
        var letters = CreateTwoColumnRows(proseRows: 0, tableRows: 12, rowMajor: true);

        TextSelectionEngine.DeterminePageTextOrder(letters)
            .Should().Be(TextSelectionEngine.PageTextOrderStrategy.RawStream);
        string.Concat(TextSelectionEngine.SortPageTextOrder(letters).Select(letter => letter.Value))
            .Should().Contain("Country table row 011001 51%Country table row 021002 52%",
                "table values must stay attached to their row labels");
    }

    [Fact]
    public void DeterminePageTextOrder_HalfProseHalfTable_KeepsProducerOrder()
    {
        var letters = CreateTwoColumnRows(proseRows: 8, tableRows: 8, rowMajor: true);

        TextSelectionEngine.DeterminePageTextOrder(letters)
            .Should().Be(TextSelectionEngine.PageTextOrderStrategy.RawStream,
                "paired prose must be a strict majority so mixed report/table pages remain row-oriented");
    }

    [Fact]
    public void DeterminePageTextOrder_ThreeColumnLayout_KeepsProducerOrder()
    {
        var letters = new List<Letter>();
        for (var row = 1; row <= 8; row++)
        {
            var y = 740 - row * 22;
            letters.AddRange(CreateRun($"Left prose row {row:00}", 30, y));
            letters.AddRange(CreateRun($"Middle prose row {row:00}", 250, y));
            letters.AddRange(CreateRun($"Right prose row {row:00}", 470, y));
        }

        TextSelectionEngine.DeterminePageTextOrder(letters)
            .Should().Be(TextSelectionEngine.PageTextOrderStrategy.RawStream,
                "nested and multi-gutter layouts are intentionally outside the conservative page gate");
    }

    [Fact]
    public void DeterminePageTextOrder_RtlContent_KeepsLogicalProducerOrder()
    {
        var letters = CreateTwoColumnRows(proseRows: 8, tableRows: 0, rowMajor: true);
        var first = letters[0];
        letters[0] = new Letter("א", first.GlyphRectangle, first.FontSize, first.FontName,
            first.StartX, first.StartY, first.Width, first.CharacterCode);

        TextSelectionEngine.DeterminePageTextOrder(letters)
            .Should().Be(TextSelectionEngine.PageTextOrderStrategy.RawStream,
                "visual left-to-right column sorting must not undo logical RTL extraction");
    }

    [Fact]
    public void DeterminePageTextOrder_PredominantlyVerticalText_KeepsProducerOrder()
    {
        var letters = new List<Letter>();
        for (var column = 0; column < 2; column++)
        {
            for (var index = 0; index < 40; index++)
            {
                var x = 100 + column * 300;
                var y = 740 - index * 10;
                letters.Add(CreateLetter("A", x, y));
            }
        }

        TextSelectionEngine.DeterminePageTextOrder(letters)
            .Should().Be(TextSelectionEngine.PageTextOrderStrategy.RawStream);
    }

    [Fact]
    public void SortPageTextOrder_SpanningHeader_RemainsAheadOfBothColumns()
    {
        var letters = CreateRun(
            "A continuous spanning report header that remains intact above both text columns",
            30,
            770,
            glyphWidth: 6);
        letters.AddRange(CreateTwoColumnRows(proseRows: 8, tableRows: 0, rowMajor: true));

        TextSelectionEngine.DeterminePageTextOrder(letters)
            .Should().Be(TextSelectionEngine.PageTextOrderStrategy.ColumnAware);
        var text = string.Concat(TextSelectionEngine.SortPageTextOrder(letters)
            .Select(letter => letter.Value));
        text.Should().StartWith("A continuous spanning report header");
        text.IndexOf("both text columns", StringComparison.Ordinal).Should().BeLessThan(
            text.IndexOf("Left prose row 01", StringComparison.Ordinal));
    }

    [Fact]
    public void DeterminePageTextOrder_SingleColumnPage_KeepsProducerOrder()
    {
        var letters = new List<Letter>();
        for (var row = 1; row <= 12; row++)
            letters.AddRange(CreateRun($"A complete single column prose line number {row:00}", 40, 740 - row * 22));

        TextSelectionEngine.DeterminePageTextOrder(letters)
            .Should().Be(TextSelectionEngine.PageTextOrderStrategy.RawStream);
    }

    private static List<Letter> CreateTwoColumnRows(int proseRows, int tableRows, bool rowMajor)
    {
        var left = new List<List<Letter>>();
        var right = new List<List<Letter>>();
        var row = 0;

        for (var index = 1; index <= proseRows; index++)
        {
            row++;
            var y = 740 - row * 22;
            left.Add(CreateRun($"Left prose row {index:00}", 40, y));
            right.Add(CreateRun($"Right prose row {index:00}", 360, y));
        }

        for (var index = 1; index <= tableRows; index++)
        {
            row++;
            var y = 740 - row * 22;
            left.Add(CreateRun($"Country table row {index:00}", 40, y));
            right.Add(CreateRun($"{1000 + index} {50 + index}%", 360, y));
        }

        return rowMajor
            ? Enumerable.Range(0, left.Count)
                .SelectMany(index => left[index].Concat(right[index]))
                .ToList()
            : left.SelectMany(run => run).Concat(right.SelectMany(run => run)).ToList();
    }

    private static List<Letter> CreateRun(
        string value,
        double x,
        double y,
        double glyphWidth = 6)
    {
        var result = new List<Letter>(value.Length);
        foreach (var character in value)
        {
            var width = character == ' ' ? glyphWidth * 0.6 : glyphWidth;
            result.Add(CreateLetter(character.ToString(), x, y, width));
            x += width;
        }
        return result;
    }

    private static Letter CreateLetter(string value, double x, double y, double width = 6) =>
        new(
            value,
            new PdfRectangle(x, y, x + width, y + 12),
            fontSize: 12,
            fontName: "F1",
            startX: x,
            startY: y,
            width,
            characterCode: value[0]);
}
