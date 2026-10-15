using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Excise.Core.Text;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Text;

/// <summary>
/// The overprint rule (<see cref="OverprintedGlyphs"/>, #1047) at its edges. The
/// end-to-end tests in <see cref="OverprintedTextExtractionTests"/> prove a stacked
/// word reads once and a genuine double letter does not collapse; these pin the
/// geometry that decides it, and the two ways it can go wrong with security weight:
/// a stack read as ONE letter whose other stamps are then left in the file, and two
/// genuine letters read as one so that a search misses a word it should find.
/// </summary>
public class OverprintedGlyphsTests
{
    private const double Size = 12;
    private const double Advance = 6;   // glyph width; tolerance is a quarter of width and height

    private static Letter L(string value, double x, double y = 700, double width = Advance, double height = Size) =>
        new(value, new PdfRectangle(x, y, x + width, y + height), Size, "F1", x, y, width,
            value.Length > 0 ? value[0] : ' ');

    private static string Text(IEnumerable<Letter> letters) => string.Concat(letters.Select(l => l.Value));

    /// <summary>"word" with every letter stamped <paramref name="copies"/> times, <paramref name="offset"/> apart.</summary>
    private static List<Letter> Stacked(string word, int copies, double offset, double origin = 60, double advance = Advance)
    {
        var letters = new List<Letter>();
        var x = origin;
        foreach (var c in word)
        {
            for (var k = 0; k < copies; k++) letters.Add(L(c.ToString(), x + k * offset));
            x += advance;
        }
        return letters;
    }

    [Theory]
    [InlineData(0.0, 0.0, true)]
    [InlineData(0.3, 0.0, true)]      // observed faux-bold: sub-point offsets
    [InlineData(1.4, 0.0, true)]      // just inside a quarter of the 6 pt width
    [InlineData(1.6, 0.0, false)]     // just outside
    [InlineData(0.0, 2.9, true)]      // just inside a quarter of the 12 pt height
    [InlineData(0.0, 3.1, false)]
    [InlineData(-1.4, -2.9, true)]    // either direction
    [InlineData(-1.6, 0.0, false)]
    [InlineData(1.4, 3.1, false)]     // inside in x, outside in y: both must hold
    [InlineData(6.0, 0.0, false)]     // a whole advance apart is a genuine neighbour
    public void IsOverprintOf_HoldsTheQuarterGlyphTolerance(double dx, double dy, bool expected)
    {
        OverprintedGlyphs.IsOverprintOf(L("t", 100), L("t", 100 + dx, 700 + dy)).Should().Be(expected);
    }

    [Fact]
    public void TheToleranceScalesWithTheType()
    {
        // The same 0.6 pt offset is a stack at 24 pt and two letters at 1 pt.
        OverprintedGlyphs.IsOverprintOf(L("t", 100, width: 12, height: 24), L("t", 100.6, width: 12, height: 24))
            .Should().BeTrue();
        OverprintedGlyphs.IsOverprintOf(L("t", 100, width: 1, height: 1), L("t", 100.6, width: 1, height: 1))
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("t", "T")]
    [InlineData("e", "é")]
    [InlineData("e", "e\u0301")]
    [InlineData("s", "ſ")]
    public void DifferentValues_AreNeverAStack_EvenWhenTheyFoldTogether(string a, string b)
    {
        // Overprint is exact-value and geometric. Folding case or accents here would
        // let "Té" stacked on "te" read as one letter and drop a glyph from the view.
        OverprintedGlyphs.IsOverprintOf(L(a, 100), L(b, 100)).Should().BeFalse();
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("")]
    public void SpacesAndEmptyValues_AreNeverAStack(string value)
    {
        OverprintedGlyphs.IsOverprintOf(L(value, 100), L(value, 100)).Should().BeFalse(
            "two spaces at one spot are two characters; collapsing them would glue words");
    }

    [Fact]
    public void ADegenerateGlyphBox_DoesNotThrowOrDivide()
    {
        var zero = L("t", 100, width: 0, height: 0);
        var negative = L("t", 100, width: -6, height: -12);
        var act = () =>
        {
            OverprintedGlyphs.IsOverprintOf(zero, zero);
            OverprintedGlyphs.IsOverprintOf(negative, negative);
            OverprintedGlyphs.IsOverprintOf(zero, L("t", 100.5, width: 0, height: 0));
        };
        act.Should().NotThrow("a zero-width glyph (a zero-advance font entry) is real input");
        OverprintedGlyphs.IsOverprintOf(zero, L("t", 100.5, width: 0, height: 0)).Should().BeFalse(
            "the tolerance floor keeps a degenerate box from absorbing a distant letter");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public void AStackOfAnyDepth_ReadsOnce_WithItsSpanCoveringEveryStamp(int copies)
    {
        var letters = Stacked("Test", copies, offset: 0.3);
        var (view, start, end) = OverprintedGlyphs.Collapse(letters);

        Text(view).Should().Be("Test");
        start.Should().Equal(Enumerable.Range(0, 4).Select(i => i * copies));
        end.Should().Equal(Enumerable.Range(0, 4).Select(i => i * copies + copies - 1),
            "redaction restores every stamp between SpanStart and SpanEnd; a short span leaves ink behind");
        Text(OverprintedGlyphs.Representatives(letters)).Should().Be("Test");
    }

    [Fact]
    public void TheSpans_TileTheLettersExactly()
    {
        var letters = Stacked("Mississippi", 3, 0.3);
        var (view, start, end) = OverprintedGlyphs.Collapse(letters);
        view.Count.Should().Be(start.Count).And.Be(end.Count);
        start[0].Should().Be(0);
        end[^1].Should().Be(letters.Count - 1);
        for (var i = 1; i < start.Count; i++)
            start[i].Should().Be(end[i - 1] + 1, "no letter is in two spans and none is in neither");
        Text(view).Should().Be("Mississippi", "the genuine ss and pp survive inside the stacks");
    }

    [Fact]
    public void AGenuineDoubleLetter_WhoseCopiesAreEachStacked_ReadsAsTwoLetters()
    {
        // "ll" drawn faux-bold: l l (an advance apart), each stamped twice.
        var letters = Stacked("ll", 2, offset: 0.3);
        Text(letters).Should().Be("llll");
        var (view, start, end) = OverprintedGlyphs.Collapse(letters);
        Text(view).Should().Be("ll");
        start.Should().Equal(0, 2);
        end.Should().Equal(1, 3);
    }

    [Fact]
    public void AGenuineDoubleLetter_TightlyKerned_IsNotCollapsed()
    {
        // 6 pt glyph, copies 2.0 pt apart: more than a quarter, so two letters.
        var letters = new List<Letter> { L("l", 100), L("l", 102.0) };
        Text(OverprintedGlyphs.Collapse(letters).View).Should().Be("ll");
    }

    [Fact]
    public void StampsThatAreNotNeighboursInTheLetterOrder_AreNotMerged()
    {
        // T e T e: drawn alternately. Only neighbours merge (a pinned, documented limit:
        // the matcher reads what the stream order says, as MuPDF does).
        var letters = new List<Letter> { L("T", 60), L("e", 66), L("T", 60.3), L("e", 66.3) };
        var (view, _, _) = OverprintedGlyphs.Collapse(letters);
        Text(view).Should().Be("TeTe");
        ReferenceEquals(OverprintedGlyphs.Representatives(letters), letters).Should().BeTrue(
            "no neighbouring stamps: the very same list, nothing allocated");
    }

    [Fact]
    public void AStackedLetterOnAnotherLine_IsNotMerged()
    {
        var letters = new List<Letter> { L("a", 100, y: 700), L("a", 100.1, y: 700 - 14) };
        Text(OverprintedGlyphs.Collapse(letters).View).Should().Be("aa");
    }

    [Fact]
    public void EmptyAndSingleLetterLists_AreSafe()
    {
        OverprintedGlyphs.Collapse([]).View.Should().BeEmpty();
        OverprintedGlyphs.Representatives([]).Should().BeEmpty();
        var one = new List<Letter> { L("x", 1) };
        Text(OverprintedGlyphs.Collapse(one).View).Should().Be("x");
        ReferenceEquals(OverprintedGlyphs.Representatives(one), one).Should().BeTrue();
    }

    [Fact]
    public void TheStackDepth_IsUnbounded_NotSilentlyCapped()
    {
        // A hostile or buggy producer stamps a letter hundreds of times. Every stamp
        // must be in the span, or the ones past a cap stay in the file after redaction.
        var letters = Enumerable.Range(0, 500).Select(_ => L("x", 100)).ToList();
        var (view, start, end) = OverprintedGlyphs.Collapse(letters);
        view.Should().ContainSingle();
        (start[0], end[0]).Should().Be((0, 499));
    }

    // -- The redaction matcher over stacks, with the accent rule ------------------

    [Theory]
    [InlineData("CAFE")]
    [InlineData("café")]
    [InlineData("Cafe")]
    public void TheMatcher_TakesEveryStampOfAMatchedStack_WhateverTheSpelling(string term)
    {
        var letters = Stacked("café", 3, offset: 0.3);
        var matches = PdfDocumentRedactionExtensions.FindTextMatches(letters, term, caseSensitive: false,
            wholeWord: true, separators: SeparatorMatching.Default);

        matches.Should().ContainSingle();
        matches[0].Count.Should().Be(12, "3 stamps of 4 letters: leaving one stamp leaves the word drawn");
        Text(matches[0]).Should().Be(Text(letters), "the match is the whole stack, in order");
    }

    [Fact]
    public void TheMatcher_DoesNotTakeNeighbouringLettersOfAStackedWord()
    {
        var letters = Stacked("a cafe b", 2, offset: 0.3);
        var matches = PdfDocumentRedactionExtensions.FindTextMatches(letters, "cafe", caseSensitive: false,
            wholeWord: true, separators: SeparatorMatching.Default);
        matches.Should().ContainSingle();
        Text(matches[0]).Should().Be("ccaaffee");
    }

    [Fact]
    public void ADoubleLetterTermInAStackedWord_MatchesOnceAndTakesAllFourStamps()
    {
        var letters = Stacked("hello", 2, offset: 0.3);
        var matches = PdfDocumentRedactionExtensions.FindTextMatches(letters, "ll", caseSensitive: false,
            separators: SeparatorMatching.Default);
        matches.Should().ContainSingle();
        Text(matches[0]).Should().Be("llll");
    }
}
