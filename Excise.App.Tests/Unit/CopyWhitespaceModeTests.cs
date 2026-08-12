using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text;
using Xunit;

namespace Excise.App.Tests.Unit;

/// <summary>
/// Construction-known fixtures for <see cref="WhitespaceMode.Smart"/> copy
/// output. These synthetic layouts own their ground truth (we place every
/// glyph), so they are the structural oracle for paragraph/list detection —
/// pdftotext is not, because it emits no paragraph blank lines and does not
/// normalise list indentation (see docs/copy-whitespace-reliability.md). The
/// corpus-vs-pdftotext measurement lives in scripts/copy-whitespace-parity.sh,
/// not here, so the serial GUI suite stays fast.
/// </summary>
public class CopyWhitespaceModeTests
{
    /// <summary>One glyph. PDF coords: <paramref name="bottom"/> is the baseline
    /// (Y-up), height fixed at 10 so word-gap threshold is 5.</summary>
    private static Letter G(string v, double left, double bottom)
    {
        var rect = new PdfRectangle(left, bottom, left + 6, bottom + 10);
        return new Letter(v, rect, fontSize: 10, fontName: "F",
            startX: left, startY: bottom, width: 6, characterCode: v.Length > 0 ? v[0] : 0);
    }

    /// <summary>Lay a run of space-separated words on one baseline. Adjacent
    /// glyphs abut (no word space); a single space between words leaves an
    /// 8-unit gap (&gt; the 5-unit threshold) so a space is emitted.</summary>
    private static List<Letter> Line(string text, double left, double bottom)
    {
        var letters = new List<Letter>();
        double x = left;
        foreach (var word in text.Split(' '))
        {
            foreach (var ch in word)
            {
                letters.Add(G(ch.ToString(), x, bottom));
                x += 6;
            }
            x += 8; // inter-word gap
        }
        return letters;
    }

    private static string Smart(IEnumerable<Letter> letters)
        => TextSelectionEngine.JoinText(letters.ToList(), WhitespaceMode.Smart);

    [Fact]
    public void WordSpacing_PreservedInSmartMode()
    {
        // Same-line word gap must still become a single space (no regression).
        var text = Smart(Line("hello world", 0, 100));
        text.Should().Be("hello world");
    }

    [Fact]
    public void SingleLine_NoTrailingWhitespace()
    {
        Smart(Line("solo", 0, 100)).Should().Be("solo");
    }

    [Fact]
    public void TightLines_StayHardBroken_NoFalseParagraph()
    {
        // Three lines at the typical leading (12) — no gap is large enough to be
        // a paragraph, so we keep single line breaks (line-faithful within a
        // paragraph; no reflow, no blank line).
        var letters = new List<Letter>();
        letters.AddRange(Line("alpha", 0, 100));
        letters.AddRange(Line("beta", 0, 88));
        letters.AddRange(Line("gamma", 0, 76));
        Smart(letters).Should().Be("alpha\nbeta\ngamma");
    }

    [Fact]
    public void ParagraphBreak_EmitsBlankLine_OnLargeGap()
    {
        // Two 2-line paragraphs. Intra-paragraph leading 12; the gap to the
        // second paragraph is 26 (> 12 * 1.6) → blank line.
        var letters = new List<Letter>();
        letters.AddRange(Line("first para one", 0, 200));
        letters.AddRange(Line("first para two", 0, 188));
        letters.AddRange(Line("second para one", 0, 162)); // gap 26
        letters.AddRange(Line("second para two", 0, 150));
        Smart(letters).Should().Be(
            "first para one\nfirst para two\n\nsecond para one\nsecond para two");
    }

    [Fact]
    public void BulletList_ItemsStayTight_MarkerPreserved()
    {
        // A bullet glyph decodes inline; each item is its own line, no blank
        // lines between items even though a list often has looser leading.
        var letters = new List<Letter>();
        letters.AddRange(Line("Intro line here", 0, 200));
        letters.AddRange(Line("• first item", 0, 180));  // gap 20 (would be para)
        letters.AddRange(Line("• second item", 0, 160));
        letters.AddRange(Line("• third item", 0, 140));
        var text = Smart(letters);
        text.Should().Contain("• first item\n• second item\n• third item");
        // Items are tight — no blank line splits the list.
        text.Should().NotContain("• first item\n\n");
    }

    [Fact]
    public void NumberedList_Detected()
    {
        var letters = new List<Letter>();
        letters.AddRange(Line("1. one", 0, 200));
        letters.AddRange(Line("2. two", 0, 180));
        letters.AddRange(Line("3. three", 0, 160));
        var text = Smart(letters);
        text.Should().Be("1. one\n2. two\n3. three");
    }

    [Fact]
    public void NestedList_IndentPreservedAsLeadingSpaces()
    {
        // A child item is indented to the right of the block margin; Smart mode
        // reproduces that as leading spaces so the nesting survives copy.
        var letters = new List<Letter>();
        letters.AddRange(Line("• parent", 0, 200));
        letters.AddRange(Line("• child", 20, 180));   // indented 20 units
        var text = Smart(letters);
        text.Split('\n').Should().HaveCount(2);
        text.Split('\n')[1].Should().StartWith(" ").And.Contain("• child");
    }

    [Fact]
    public void LineFaithful_IsUnchanged_NoParagraphOrListLogic()
    {
        // The parameterless overload and explicit LineFaithful must both give
        // exactly one \n per line change, never a blank line.
        var letters = new List<Letter>();
        letters.AddRange(Line("first para one", 0, 200));
        letters.AddRange(Line("first para two", 0, 188));
        letters.AddRange(Line("second para one", 0, 162));
        var faithful = TextSelectionEngine.JoinText(letters);
        faithful.Should().Be("first para one\nfirst para two\nsecond para one");
        TextSelectionEngine.JoinText(letters, WhitespaceMode.LineFaithful)
            .Should().Be(faithful);
    }

    [Theory]
    [InlineData("• item", true)]
    [InlineData("- item", true)]
    [InlineData("– item", true)]
    [InlineData("* item", true)]
    [InlineData("1. item", true)]
    [InlineData("12) item", true)]
    [InlineData("a. item", true)]
    [InlineData("hello world", false)]
    [InlineData("state-of-the-art", false)] // hyphen mid-word, not a marker
    [InlineData("1234 Main St", false)]     // number without . or )
    [InlineData("", false)]
    public void ListMarkerDetection(string text, bool expected)
    {
        TextSelectionEngine.TryGetListMarker(text, out _).Should().Be(expected);
    }
}
