using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Tests.Content;
using Excise.Core.Text;
using Xunit;

namespace Excise.Core.Tests.Text;

/// <summary>
/// #2008: a line turned by its text matrix (or by <c>cm</c>) is one line to
/// page text, to the words search reads, and to the selection order. Before
/// the fix every glyph of it was a line of its own, because the line model
/// compared user-space y. The independent-oracle half (MuPDF, Poppler, pixels)
/// is <c>TurnedTextFindAndRedactTests</c> in Excise.Rendering.Tests.
///
/// <para>The walker reports each glyph's advance direction from Tm × CTM and
/// the sign of Th. Its initial values are the spec's: Tm is the identity at
/// <c>BT</c> (§9.4.1), Th is 100 (§9.3.4), the CTM is the identity
/// (§8.4.2); the fixtures that omit <c>Tm</c>, <c>Tz</c> and <c>cm</c> pin
/// that an unturned show reads as upright.</para>
/// </summary>
public class TurnedLineGroupingTests
{
    private const string Line = "REFERENCE Parklands Phase 14B";

    private static IReadOnlyList<Letter> Letters(string content)
    {
        using var doc = PdfDocument.Open(ContentStreamFixture.Build(content));
        return ContentStreamFixture.ExtractLetters(doc.GetPage(1));
    }

    private static string PageText(string content)
    {
        using var doc = PdfDocument.Open(ContentStreamFixture.Build(content));
        return doc.GetPage(1).Text;
    }

    [Fact]
    public void AShowWithNoTmTzOrCm_IsUpright()
    {
        var letters = Letters("BT /F1 12 Tf 72 700 Td (Upright) Tj ET");
        letters.Should().NotBeEmpty();
        letters.Should().AllSatisfy(l =>
        {
            l.BaselineAngle.Should().Be(0);
            TextSelectionEngine.IsTurned(l).Should().BeFalse();
            TextSelectionEngine.LineFrame(l).Box.Should().Be(l.GlyphRectangle, "an upright glyph's frame is its box");
        });
    }

    [Theory]
    [InlineData("BT /F1 12 Tf 0 1 -1 0 300 200 Tm (X) Tj ET", 90)]
    [InlineData("BT /F1 12 Tf -1 0 0 -1 300 600 Tm (X) Tj ET", 180)]
    [InlineData("BT /F1 12 Tf 0 -1 1 0 300 600 Tm (X) Tj ET", -90)]
    [InlineData("q 0 1 -1 0 612 0 cm BT /F1 12 Tf 100 100 Td (X) Tj ET Q", 90)]
    [InlineData("BT /F1 12 Tf -100 Tz 300 400 Td (X) Tj ET", 180)]
    [InlineData("BT /F1 12 Tf 0.7071068 0.7071068 -0.7071068 0.7071068 300 400 Tm (X) Tj ET", 45)]
    public void TheWalker_ReportsTheAdvanceDirection_FromTmCtmAndTh(string content, double degrees)
    {
        var letter = Letters(content).Single();
        (letter.BaselineAngle * 180 / Math.PI).Should().BeApproximately(degrees, 1e-4);
        TextSelectionEngine.IsTurned(letter).Should().BeTrue();
    }

    public static TheoryData<string> TurnedLines() => new()
    {
        $"BT /F1 12 Tf 0 1 -1 0 300 200 Tm ({Line}) Tj ET",
        $"BT /F1 12 Tf 0 -1 1 0 300 600 Tm ({Line}) Tj ET",
        $"BT /F1 12 Tf -1 0 0 -1 500 400 Tm ({Line}) Tj ET",
        $"BT /F1 12 Tf 0.5 0.8660254 -0.8660254 0.5 200 200 Tm ({Line}) Tj ET",
        $"q 0 1 -1 0 612 0 cm BT /F1 12 Tf 100 100 Td ({Line}) Tj ET Q",
        // Kerned, with no space glyph: the word gaps are TJ numbers.
        "BT /F1 12 Tf 0 1 -1 0 300 200 Tm [(REFERENCE) -400 (Parklands) -400 (Phase) -400 (14B)] TJ ET",
        // One Tj per glyph: every glyph is its own string.
        "BT /F1 12 Tf 0 1 -1 0 300 200 Tm " + string.Join(" ", Line.Select(c => $"({c}) Tj")) + " ET",
    };

    [Theory]
    [MemberData(nameof(TurnedLines))]
    public void PageText_ReadsATurnedLine_AsOneLine(string content)
    {
        var text = PageText(content + "\nBT /F1 12 Tf 72 72 Td (KEEPME) Tj ET");
        text.Should().Contain(Line).And.Contain("KEEPME");
    }

    [Theory]
    [MemberData(nameof(TurnedLines))]
    public void Words_OfATurnedLine_AreItsWords(string content)
    {
        var words = TextExtractor.BuildWords(Letters(content)).Select(w => w.Text).ToList();
        words.Should().Equal("REFERENCE", "Parklands", "Phase", "14B");
    }

    [Fact]
    public void Words_DoNotRunAcrossADirectionChange()
    {
        // The upright word ends where the turned one starts; without the
        // direction check they would join as one word.
        var words = TextExtractor.BuildWords(Letters(
            "BT /F1 12 Tf 100 300 Td (AB) Tj ET BT /F1 12 Tf 0 1 -1 0 114 300 Tm (CD) Tj ET"))
            .Select(w => w.Text).ToList();
        words.Should().Equal("AB", "CD");
    }

    [Fact]
    public void SelectionOrder_ReadsATurnedLineInItsWritingOrder_AfterTheUprightText()
    {
        var letters = Letters(
            "BT /F1 12 Tf 72 700 Td (Upright one) Tj 0 -14 Td (Upright two) Tj ET\n" +
            $"BT /F1 12 Tf 0 1 -1 0 300 200 Tm ({Line}) Tj ET");
        var ordered = TextSelectionEngine.SortReadingOrder(letters);
        var text = TextSelectionEngine.JoinText(ordered);
        text.Should().Be("Upright one\nUpright two\n" + Line);

        // A drag from the word's first glyph to its last copies exactly the word.
        var p = ordered.FindIndex(l => l.Value == "P");
        TextSelectionEngine.JoinText(TextSelectionEngine.RangeBetween(ordered, ordered[p], ordered[p + 8]))
            .Should().Be("Parklands");
    }

    [Fact]
    public void SelectionOrder_OfAnUprightPage_IsUnchanged()
    {
        var letters = Letters("BT /F1 12 Tf 72 700 Td (Upright one) Tj 0 -14 Td (Upright two) Tj ET");
        TextSelectionEngine.JoinText(TextSelectionEngine.SortReadingOrder(letters))
            .Should().Be("Upright one\nUpright two");
    }

    [Fact]
    public void APageOfOnlyTurnedText_SortsWithoutUprightGlyphs()
    {
        var letters = Letters($"BT /F1 12 Tf 0 1 -1 0 300 200 Tm ({Line}) Tj ET");
        foreach (var strategy in new[] { ReadingOrderStrategy.Simple, ReadingOrderStrategy.ColumnAware })
            TextSelectionEngine.JoinText(TextSelectionEngine.SortReadingOrder(letters, strategy)).Should().Be(Line);
    }
}
