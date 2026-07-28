using System.Collections.Generic;
using AwesomeAssertions;
using Excise.Avalonia.Services;
using Excise.Core.Document;
using Excise.Core.Text;
using Xunit;

namespace Excise.Avalonia.Tests;

/// <summary>
/// #836 — Smart mode rejoins soft (line-break) hyphens on copy, like pdftotext
/// and most readers; LineFaithful stays verbatim.
/// </summary>
public class DehyphenationTests
{
    private const double FontSize = 10;

    // Letters advance by 10 and are ~9 wide, so consecutive glyphs of a word
    // nearly abut (intra-letter gap ~1pt ≪ a word space) — realistic spacing.
    private static Letter G(string v, double x, double baseline) =>
        new(v, new PdfRectangle(x, baseline, x + 9, baseline + FontSize), FontSize,
            "F", x, baseline, 9, v.Length > 0 ? v[0] : ' ');

    // Two visual lines: "unfamil-" then "iar" one line below.
    private static List<Letter> BrokenWord(char continuationFirst = 'i')
    {
        var l = new List<Letter>();
        double y1 = 100, y2 = 86;
        var top = "unfamil-";
        double x = 100;
        foreach (var c in top) { l.Add(G(c.ToString(), x, y1)); x += 10; }
        x = 100;
        foreach (var c in new[] { continuationFirst, 'a', 'r' }) { l.Add(G(c.ToString(), x, y2)); x += 10; }
        return l;
    }

    [Fact]
    public void Smart_RejoinsSoftHyphen()
    {
        TextSelectionEngine.JoinText(BrokenWord(), WhitespaceMode.Smart)
            .Should().Be("unfamiliar", "a line-end hyphen + lowercase continuation is a soft hyphen");
    }

    [Fact]
    public void LineFaithful_KeepsHyphenAndBreak()
    {
        TextSelectionEngine.JoinText(BrokenWord(), WhitespaceMode.LineFaithful)
            .Should().Be("unfamil-\niar", "LineFaithful is verbatim — no dehyphenation");
    }

    [Fact]
    public void Smart_DoesNotJoinBeforeCapitalContinuation()
    {
        // "...-\nApple" must stay hyphenated (likely a real hyphen, not a soft break).
        TextSelectionEngine.JoinText(BrokenWord('A'), WhitespaceMode.Smart)
            .Should().Contain("-\n", "a capitalised continuation is not treated as a soft hyphen");
    }
}
