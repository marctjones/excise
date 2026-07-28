using System.Collections.Generic;
using AwesomeAssertions;
using Excise.Avalonia.Services;
using Excise.Core.Document;
using Excise.Core.Text;
using Xunit;

namespace Excise.Avalonia.Tests;

/// <summary>
/// #833 — some fonts (e.g. TT0 in scotus-trump-v-us.pdf) report a near-zero
/// glyph advance width, even though glyph POSITIONS are correct. That broke two
/// GUI things, both fixed here without touching Core width resolution:
///   1. Copy inserted a space between every letter ("w o r r y").
///   2. Selection highlights were ~0-wide slivers (invisible).
/// These build synthetic zero-width glyphs (widths 0.3, advances ~10) to lock in
/// the width-independent behaviour.
/// </summary>
public class DegenerateGlyphWidthTests
{
    private const double FontSize = 10;
    private const double Baseline = 100;

    private static Letter Glyph(string v, double x, double width = 0.3) =>
        new(
            v,
            new PdfRectangle(x, Baseline, x + width, Baseline + FontSize),
            FontSize,
            "TT0",
            x,
            Baseline,
            width,
            v.Length > 0 ? v[0] : ' ');

    /// <summary>"worry as" laid out with real space glyph + ~0 glyph widths.</summary>
    private static List<Letter> WorryAs()
    {
        var xs = new (string v, double x)[]
        {
            ("w", 100), ("o", 110), ("r", 120), ("r", 130), ("y", 140),
            (" ", 150), ("a", 160), ("s", 170),
        };
        var list = new List<Letter>();
        foreach (var (v, x) in xs) list.Add(Glyph(v, x));
        return list;
    }

    [Fact]
    public void JoinText_DoesNotSpaceEveryLetter_WhenGlyphWidthsAreZero()
    {
        var letters = WorryAs();

        TextSelectionEngine.JoinText(letters, WhitespaceMode.LineFaithful)
            .Should().Be("worry as", "a real space glyph separates the words; zero glyph widths must NOT add a space between every letter");

        TextSelectionEngine.JoinText(letters, WhitespaceMode.Smart)
            .Should().Be("worry as");
    }

    [Fact]
    public void GapPositionedWords_StillGetSpaces_WhenNoSpaceGlyphPresent()
    {
        // No whitespace glyph: two words separated only by a wide gap. The
        // advance heuristic must still recover the break.
        var letters = new List<Letter>
        {
            Glyph("h", 100), Glyph("i", 110),      // advances ~10
            Glyph("y", 145), Glyph("o", 155),      // 35pt jump = word gap (3.5x median)
        };
        TextSelectionEngine.JoinText(letters, WhitespaceMode.LineFaithful)
            .Should().Be("hi yo");
    }

    [Fact]
    public void EffectiveHighlightRect_WidensDegenerateWidthToAdvance()
    {
        var letters = WorryAs();

        // 'w' has width 0.3 but the next glyph 'o' is 10pt away → highlight must
        // span ~the advance, not the invisible 0.3 sliver.
        var w = TextSelectionEngine.EffectiveHighlightRect(letters, 0);
        w.Width.Should().BeGreaterThan(0.5 * 10,
            "a degenerate ~0-width glyph must be widened to (about) its advance so the highlight is visible");
        w.Width.Should().BeLessThanOrEqualTo(FontSize + 0.001, "widening is capped at the font size");
    }

    [Fact]
    public void EffectiveHighlightRect_LeavesRealWidthsUnchanged()
    {
        var normal = new List<Letter> { Glyph("A", 100, width: 6), Glyph("B", 110, width: 6) };
        var r = TextSelectionEngine.EffectiveHighlightRect(normal, 0);
        r.Width.Should().BeApproximately(6, 0.001, "a glyph with a real width must pass through unchanged");
    }
}
