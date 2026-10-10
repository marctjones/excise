using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1791 — a name of any length that wraps between two of its words is
/// REDACTED from both lines: every glyph of it leaves the content stream, and
/// nothing else on either line does. #1750 only reported the two-word straddle;
/// a three-word name wrapping after its second word was neither removed nor
/// reported, so the redaction said "0 occurrences" over a readable name.
///
/// <para>The fixtures are built here, one PDF per shape, because the wrap
/// geometry is the whole subject: plain <c>Tj</c> lines, <c>TJ</c> arrays
/// whose word gaps are kerning (no space glyph at all), a trailing space
/// glyph at the break, a unit font scaled up by <c>Tm</c>, a
/// <c>/Rotate 90</c> page and a three-line wrap. Removal is judged by three
/// readers that are not excise: the saved bytes inflated
/// (<see cref="SavedPdfLeakScanner"/>), MuPDF and Poppler, the last two
/// reading the name in reading order across the break.</para>
///
/// <para>A wrap is only joined when the next line starts back at the left of
/// the block, one line pitch below. A continuation that jumps UP (the next
/// column's first line) or to the RIGHT (another column) is not a wrap excise
/// can confirm: it is left in place and reported, never removed silently and
/// never passed off as clean.</para>
/// </summary>
public class WordWrapRedactionTests
{
    private const string Lead = "This agreement was signed by";
    private const string Tail = "on behalf of the company.";

    public enum Shape
    {
        /// <summary>One <c>Tj</c> per line; space glyphs between words, none at the break.</summary>
        Tj,
        /// <summary>One <c>TJ</c> per line; every word gap is a kerning number, no space glyph.
        /// No word of a name ends in a narrow glyph: a gap after one is not read as a space (#1879).</summary>
        KernedTj,
        /// <summary>As <see cref="Tj"/>, with the space glyph left at the end of the first line.</summary>
        TrailingSpace,
        /// <summary><c>1 Tf</c> scaled to 12 pt by <c>Tm</c>: the font size a glyph reports is 1.</summary>
        UnitFontTm,
        /// <summary>As <see cref="Tj"/> on a <c>/Rotate 90</c> page.</summary>
        Rotate90,
        /// <summary>One <c>Tj</c> per word and one per space: a one-letter word is a string
        /// of one glyph, whose own origins give no writing direction (#1882).</summary>
        TjPerWord,
        /// <summary>One <c>Tj</c> per glyph: no string has a direction of its own (#1882).</summary>
        TjPerGlyph,
    }

    /// <summary>#1882: the matrix that turns the fixture's upright coordinates
    /// onto the portrait page, by degrees counterclockwise.</summary>
    private static readonly Dictionary<int, double[]> Rotations = new()
    {
        [90] = [0, 1, -1, 0, 612, 0],
        [180] = [-1, 0, 0, -1, 612, 792],
        [270] = [0, -1, 1, 0, 0, 792],
    };

    public static TheoryData<string, int, Shape> WrappedNames()
    {
        var data = new TheoryData<string, int, Shape>();
        var names = new[]
        {
            "Cordelia Whitcombe",
            "Quentin Barnaby Holloway",
            "Ignatius Rowena Mahoney Quill",
            "Octavia Lucinda Pemberton Vasquez Thornbury",
        };
        foreach (var name in names)
        {
            var words = name.Split(' ').Length;
            for (var wrapAfter = 1; wrapAfter <= Math.Min(3, words - 1); wrapAfter++)
                foreach (var shape in Enum.GetValues<Shape>())
                    data.Add(name, wrapAfter, shape);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(WrappedNames))]
    public void ANameWrappedBetweenItsWords_IsRemovedFromBothLines(string name, int wrapAfter, Shape shape)
    {
        RequireOracles();
        var words = name.Split(' ');
        var line1 = $"{Lead} {string.Join(' ', words.Take(wrapAfter))}";
        var line2 = $"{string.Join(' ', words.Skip(wrapAfter))} {Tail}";

        var (report, saved, output) = Redact(
            BuildPdf(shape, (72, 700, line1), (72, 686, line2)), name);
        try
        {
            report.MatchesLocated.Should().Be(1, $"the one wrapped occurrence is matched ({report})");
            report.VerifiedRemovals.Should().Be(1, report.ToString());
            report.WordWrapCandidates.Should().BeEmpty("a confirmed wrap is removed, not merely reported");
            report.IsCleanSuccess.Should().BeTrue(report.ToString());

            AssertNameGone(saved, output, name, "signed by", "on behalf of the company");
        }
        finally
        {
            File.Delete(output);
        }
    }

    public static TheoryData<string, string, string, RedactionProfile> TypographicWrappedNames()
    {
        // (typed term, first line, second line). \222 is a right single quote
        // and \226 an en dash in WinAnsiEncoding: the page prints what a
        // typesetter prints, the user types the keyboard's apostrophe and hyphen.
        var cases = new[]
        {
            ("Siobhan O'Rourke Brannigan", $"{Lead} Siobhan O\\222Rourke", $"Brannigan {Tail}"),
            ("Siobhan O'Rourke Brannigan", $"{Lead} Siobhan", $"O\\222Rourke Brannigan {Tail}"),
            ("Annemarie-Castellano Whitby Ferreira", $"{Lead} Annemarie\\226Castellano Whitby", $"Ferreira {Tail}"),
            // A doubled space inside the name and two trailing spaces before the wrap.
            ("Annemarie-Castellano Whitby Ferreira", $"{Lead} Annemarie\\226Castellano  Whitby  ", $"Ferreira {Tail}"),
        };
        var data = new TheoryData<string, string, string, RedactionProfile>();
        foreach (var (term, line1, line2) in cases)
            foreach (var profile in new[] { RedactionProfile.Standard, RedactionProfile.Maximum })
                data.Add(term, line1, line2, profile);
        return data;
    }

    public static TheoryData<int, Shape, bool, string, int> RotatedWraps()
    {
        var data = new TheoryData<int, Shape, bool, string, int>();
        foreach (var degrees in Rotations.Keys)
            foreach (var shape in new[] { Shape.Tj, Shape.KernedTj, Shape.TrailingSpace, Shape.TjPerWord, Shape.TjPerGlyph })
                foreach (var viaTm in new[] { false, true })
                {
                    data.Add(degrees, shape, viaTm, "Quentin Barnaby Holloway", 2);
                    data.Add(degrees, shape, viaTm, "Quentin A Holloway", 2);
                    data.Add(degrees, shape, viaTm, "Quentin A Holloway", 1);
                }
        return data;
    }

    [Theory]
    [MemberData(nameof(RotatedWraps))]
    public void ANameWrappedInTextRotatedByItsMatrix_IsRemovedFromBothLines(
        int degrees, Shape shape, bool viaTm, string name, int wrapAfter)
    {
        // #1882: landscape content on a portrait page, turned by the CTM or by
        // the text matrix itself. Every line is vertical or upside down in user
        // space, and a y-only line model read each glyph as a line of its own:
        // the wrapped name was neither matched nor reported.
        RequireOracles();
        var words = name.Split(' ');
        var pdf = BuildPdf(shape, Rotations[degrees], viaTm,
            (72, 500, $"{Lead} {string.Join(' ', words.Take(wrapAfter))}"),
            (72, 486, $"{string.Join(' ', words.Skip(wrapAfter))} {Tail}"));
        AssertReadersRead(pdf, name);

        var (report, saved, output) = Redact(pdf, name);
        try
        {
            report.MatchesLocated.Should().Be(1, $"the one wrapped occurrence is matched ({report})");
            report.VerifiedRemovals.Should().Be(1, report.ToString());
            report.WordWrapCandidates.Should().BeEmpty("a confirmed wrap is removed, not merely reported");
            report.IsCleanSuccess.Should().BeTrue(report.ToString());

            AssertNameGone(saved, output, name, "signed by", "on behalf of the company");
        }
        finally
        {
            File.Delete(output);
        }
    }

    /// <summary>#1891: the turn of a block of text by an angle that is not a
    /// multiple of 90 degrees, about the block's middle so every glyph stays on
    /// the page.</summary>
    private static double[] TurnedAboutTheBlock(int degrees)
    {
        var r = degrees * Math.PI / 180;
        var (c, s) = (Math.Round(Math.Cos(r), 6), Math.Round(Math.Sin(r), 6));
        const double cx = 200, cy = 493, px = 306, py = 396;
        return [c, s, -s, c, Math.Round(px - (c * cx - s * cy), 4), Math.Round(py - (s * cx + c * cy), 4)];
    }

    public static TheoryData<int, Shape, bool, string, int> ObliqueWraps()
    {
        var data = new TheoryData<int, Shape, bool, string, int>();
        foreach (var degrees in new[] { 2, 30, 45, 135, 315 })
            foreach (var shape in new[] { Shape.Tj, Shape.KernedTj, Shape.TrailingSpace, Shape.TjPerWord, Shape.TjPerGlyph })
                foreach (var viaTm in new[] { false, true })
                {
                    data.Add(degrees, shape, viaTm, "Quentin Barnaby Holloway", 2);
                    data.Add(degrees, shape, viaTm, "Quentin A Holloway", 2);
                    data.Add(degrees, shape, viaTm, "Quentin A Holloway", 1);
                }
        return data;
    }

    [Theory]
    [MemberData(nameof(ObliqueWraps))]
    public void ANameWrappedInTextTurnedByANonRightAngle_IsRemovedFromBothLines(
        int degrees, Shape shape, bool viaTm, string name, int wrapAfter)
    {
        // #1891 / #2011: the line model read a turned line in the quarter turn
        // nearest its direction, so at 30 or 45 degrees consecutive glyphs of
        // one line sat on different "lines" and a wrapped name was reported,
        // not removed. The direction is the walker's, exact at any angle.
        // 2 degrees is the OCR layer of a skewed scan (Tesseract writes one
        // turned Tm per block), which the quarter-turn snap read as upright.
        RequireOracles();
        Assert.SkipUnless(MutoolStextGeometry.IsAvailable, "mutool not installed [requires: tool:mutool]");
        var words = name.Split(' ');
        var pdf = ObliqueWrap(degrees, shape, viaTm, name, wrapAfter);

        // The count to meet is MuPDF's, from the input; Poppler reads text off a
        // quarter turn in fragments (TurnedTextFindAndRedactTests), so it only
        // judges the output.
        var input = Path.Combine(Path.GetTempPath(), $"excise-1891-in-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(input, pdf);
        var outputs = new List<string> { input };
        try
        {
            var mutoolInput = Regex.Replace(MutoolTextExtractor.ExtractPage(input, 1) ?? "", @"\s+", " ");
            var occurrences = Regex.Matches(mutoolInput, Regex.Escape(name)).Count;
            occurrences.Should().Be(1, $"fixture sanity: MuPDF reads the name once across the break:\n{mutoolInput}");

            var stext = MutoolStextGeometry.Read(pdf, 1);
            var quads = words.Where(w => w.Length >= 3).SelectMany(w =>
            {
                var hits = stext.FindChars(w);
                hits.Should().HaveCount(1, $"fixture sanity: MuPDF places '{w}' once");
                return hits[0];
            }).ToList();
            quads.Should().OnlyContain(q => q.Left >= 0 && q.Top >= 0 && q.Right <= stext.Width && q.Bottom <= stext.Height,
                "fixture sanity: the turned block lies on the page");
            using (var before = MutoolReferenceRenderer.RenderPage(input, 1, 72))
                TurnedTextFindAndRedactTests.InkIn(before!, quads).Should().BeGreaterThan(0.05, "fixture sanity: the name is drawn");
            foreach (var word in words.Where(w => w.Length >= 3))
                SavedPdfLeakScanner.FindTerm(pdf, word).Should().NotBeEmpty($"fixture sanity: the scanner finds '{word}' in the input");

            var (report, saved, output) = Redact(pdf, name);
            outputs.Add(output);
            report.MatchesLocated.Should().Be(occurrences, $"every occurrence MuPDF reads is matched ({report})");
            report.VerifiedRemovals.Should().Be(occurrences, report.ToString());
            report.WordWrapCandidates.Should().BeEmpty("a confirmed wrap is removed, not merely reported");
            report.IsCleanSuccess.Should().BeTrue(report.ToString());

            foreach (var word in words.Where(w => w.Length >= 3))
                SavedPdfLeakScanner.FindTerm(saved, word).Should().BeEmpty($"'{word}' of the name must leave the file");
            foreach (var (tool, text) in Readings(output))
            {
                text.Should().NotContain(name, $"{tool} must not read the name across the break");
                foreach (var word in words.Where(w => w.Length >= 3))
                    text.Should().NotMatchRegex($@"\b{Regex.Escape(word)}\b", $"{tool} must not read '{word}'");
            }

            // No covering box and no width closing: the pixels inside the name's
            // own glyph quads show what glyph removal alone left there.
            using (var doc = PdfDocument.Open(pdf))
            {
                doc.RedactText(name, RedactionOptions.Default with { DrawBox = false, Width = WidthPolicy.CollapsePreserveLayout });
                var pixelOutput = Path.Combine(Path.GetTempPath(), $"excise-1891-px-{Guid.NewGuid():N}.pdf");
                doc.Save(pixelOutput);
                outputs.Add(pixelOutput);
                using var after = MutoolReferenceRenderer.RenderPage(pixelOutput, 1, 72);
                TurnedTextFindAndRedactTests.InkIn(after!, quads).Should().BeLessThan(0.001, "no ink is left inside the name's glyph quads");
            }
        }
        finally
        {
            foreach (var path in outputs)
                File.Delete(path);
        }
    }

    public static TheoryData<int, Shape, bool, string, int> TurnedWraps()
    {
        // #2055: the oblique angles of ObliqueWraps, and the right angles and
        // upright text through the same turn about the block, which must not move.
        var data = ObliqueWraps();
        foreach (var degrees in new[] { 0, 90, 180, 270 })
            foreach (var shape in new[] { Shape.Tj, Shape.KernedTj, Shape.TrailingSpace, Shape.TjPerWord, Shape.TjPerGlyph })
                foreach (var viaTm in new[] { false, true })
                {
                    data.Add(degrees, shape, viaTm, "Quentin Barnaby Holloway", 2);
                    data.Add(degrees, shape, viaTm, "Quentin A Holloway", 2);
                    data.Add(degrees, shape, viaTm, "Quentin A Holloway", 1);
                }
        return data;
    }

    [Theory]
    [MemberData(nameof(TurnedWraps))]
    public void ANameWrappedInTextTurnedByANonRightAngle_KeepsTheNeighbouringLines(
        int degrees, Shape shape, bool viaTm, string name, int wrapAfter)
    {
        // #2055: each line of a match was removed through one axis-aligned box
        // around its glyph centres. Around a diagonal run that box is a square,
        // and it took glyphs of the lines above and below the match ("his ag"
        // and "ny." at 45 degrees) while the report said clean success. Judged
        // by the delta: MuPDF reads the input's glyphs minus the name's, no more.
        RequireOracles();
        var pdf = ObliqueWrap(degrees, shape, viaTm, name, wrapAfter);
        var input = Path.Combine(Path.GetTempPath(), $"excise-2055-in-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(input, pdf);
        var (report, saved, output) = Redact(pdf, name);
        try
        {
            report.VerifiedRemovals.Should().Be(1, report.ToString());
            report.IsCleanSuccess.Should().BeTrue(report.ToString());
            foreach (var word in name.Split(' ').Where(w => w.Length >= 3))
                SavedPdfLeakScanner.FindTerm(saved, word).Should().BeEmpty($"'{word}' of the name must leave the file");

            AssertOnlyRemoved(input, output, name);

            var mutool = Regex.Replace(MutoolTextExtractor.ExtractPage(output, 1) ?? "", @"\s+", " ");
            foreach (var neighbour in new[] { Lead, Tail })
                mutool.Should().Contain(neighbour, $"MuPDF must still read the neighbouring text '{neighbour}'");
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }

    /// <summary>#2055: the issue's own case, a phrase inside one line of a
    /// turned block, and phrases in a block turned through a mirror or a skew.</summary>
    public static TheoryData<string, int, string> TurnedPhrases()
    {
        var data = new TheoryData<string, int, string>();
        foreach (var degrees in new[] { 30, 45, 135, 315 })
            data.Add("turned", degrees, "signed by");
        foreach (var degrees in new[] { 0, 30, 45, 135 })
        {
            data.Add("mirrored", degrees, "signed by");
            data.Add("skewed", degrees, "signed by");
            // A mirror puts the second line above the first, which is not a wrap
            // the matcher joins (it is reported); the skew keeps the line order.
            data.Add("skewed", degrees, "Quentin Barnaby Holloway");
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(TurnedPhrases))]
    public void APhraseInTextTurnedByANonRightAngle_KeepsTheNeighbouringLines(string kind, int degrees, string term)
    {
        RequireOracles();
        var r = degrees * Math.PI / 180;
        var (c, s) = (Math.Cos(r), Math.Sin(r));
        double[] linear = kind switch
        {
            "mirrored" => [c, s, s, -c],
            "skewed" => [c, s, 0.3 * c - s, 0.3 * s + c],
            _ => [c, s, -s, c],
        };
        var pdf = BuildPdf(Shape.Tj, AboutTheBlock(linear), viaTm: false,
            (72, 500, $"{Lead} Quentin Barnaby"),
            (72, 486, $"Holloway {Tail}"));
        var input = Path.Combine(Path.GetTempPath(), $"excise-2055-in-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(input, pdf);
        var (report, saved, output) = Redact(pdf, term);
        try
        {
            report.VerifiedRemovals.Should().Be(1, report.ToString());
            report.IsCleanSuccess.Should().BeTrue(report.ToString());
            foreach (var word in term.Split(' ').Where(w => w.Length >= 3))
                SavedPdfLeakScanner.FindTerm(saved, word).Should().BeEmpty($"'{word}' must leave the file");
            AssertOnlyRemoved(input, output, term);
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }

    [Theory]
    [InlineData(30)]
    [InlineData(45)]
    [InlineData(135)]
    [InlineData(315)]
    public void TextTurnedByANonRightAngle_TwoSequentialRedactions_WithACurlyQuoteADollarAndParentheses(int degrees)
    {
        // CLAUDE.md rule 9 at an oblique angle (#2055): the second redaction runs
        // on the stream the first rebuilt, and neither takes a neighbour's glyph.
        RequireOracles();
        var pdf = BuildPdf(Shape.Tj, TurnedAboutTheBlock(degrees), viaTm: false,
            (72, 500, "Paid $1,250 \\(net\\) to Siobhan O\\222Rourke"),
            (72, 486, "Brannigan on behalf of the company."));
        var input = Path.Combine(Path.GetTempPath(), $"excise-2055-in-{Guid.NewGuid():N}.pdf");
        var output = Path.Combine(Path.GetTempPath(), $"excise-2055-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(input, pdf);
        try
        {
            using (var doc = PdfDocument.Open(pdf))
            {
                var first = doc.RedactText("Siobhan O'Rourke Brannigan", RedactionOptions.Default);
                first.VerifiedRemovals.Should().Be(1, first.ToString());
                first.IsCleanSuccess.Should().BeTrue(first.ToString());
                var second = doc.RedactText("$1,250 (net)", RedactionOptions.Default);
                second.VerifiedRemovals.Should().Be(1, second.ToString());
                second.IsCleanSuccess.Should().BeTrue(second.ToString());
                doc.Save(output);
            }

            var saved = File.ReadAllBytes(output);
            foreach (var fragment in new[] { "Siobhan", "Rourke", "Brannigan", "1,250" })
                SavedPdfLeakScanner.FindTerm(saved, fragment).Should().BeEmpty($"'{fragment}' must leave the file");
            AssertOnlyRemoved(input, output, "Siobhan O’Rourke Brannigan $1,250 (net)");
            var mutool = Regex.Replace(MutoolTextExtractor.ExtractPage(output, 1) ?? "", @"\s+", " ");
            mutool.Should().Contain("Paid", "MuPDF must still read the text before the first term");
            mutool.Should().Contain("on behalf of the company.", "MuPDF must still read the second line");
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }

    [Theory]
    [InlineData(30)]
    [InlineData(45)]
    [InlineData(135)]
    public void ADrawnAreaOverTextTurnedByANonRightAngle_RemovesEveryGlyphItsBoxOverlaps(int degrees)
    {
        // #2055 changed how a matched line of turned text selects its glyphs,
        // not how a drawn area does: a rectangle still takes every glyph whose
        // page-space box it touches, the leak-safe reading of AnyOverlap.
        RequireOracles();
        var pdf = BuildPdf(Shape.Tj, TurnedAboutTheBlock(degrees), viaTm: false,
            (72, 500, $"{Lead} Quentin Barnaby"),
            (72, 486, $"Holloway {Tail}"));
        var area = new PdfRectangle(296, 386, 316, 406);
        var input = Path.Combine(Path.GetTempPath(), $"excise-2055-in-{Guid.NewGuid():N}.pdf");
        var output = Path.Combine(Path.GetTempPath(), $"excise-2055-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(input, pdf);
        try
        {
            string overlapped;
            using (var doc = PdfDocument.Open(pdf))
            {
                var page = doc.GetPage(1);
                overlapped = string.Concat(page.Letters
                    .Where(l => l.GlyphRectangle.Normalize().IntersectsWith(area))
                    .Select(l => l.Value));
                overlapped.Count(c => !char.IsWhiteSpace(c)).Should().BeGreaterThan(4,
                    "fixture sanity: the area lies across both lines");
                page.RedactArea(area, RedactionOptions.Default with { DrawBox = false });
                doc.Save(output);
            }
            AssertOnlyRemoved(input, output, overlapped);
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }

    /// <summary>
    /// #2055, judged by the delta: MuPDF reads the input's glyphs minus those
    /// of <paramref name="removed"/>, no more and no fewer.
    /// </summary>
    private static void AssertOnlyRemoved(string input, string output, string removed)
    {
        var before = Glyphs(MutoolTextExtractor.ExtractPage(input, 1));
        var after = Glyphs(MutoolTextExtractor.ExtractPage(output, 1));
        foreach (var c in removed.Where(c => !char.IsWhiteSpace(c)))
            before[c] = before.GetValueOrDefault(c) - 1;
        var lost = before.Where(kv => kv.Value > after.GetValueOrDefault(kv.Key))
            .Select(kv => $"{kv.Key}x{kv.Value - after.GetValueOrDefault(kv.Key)}").ToList();
        var left = after.Where(kv => kv.Value > before.GetValueOrDefault(kv.Key))
            .Select(kv => $"{kv.Key}x{kv.Value - before.GetValueOrDefault(kv.Key)}").ToList();
        var reading = Regex.Replace(MutoolTextExtractor.ExtractPage(output, 1) ?? "", @"\s+", " ");
        lost.Should().BeEmpty($"only the requested glyphs leave (lost: {string.Join(", ", lost)}); MuPDF reads {reading}");
        left.Should().BeEmpty($"every requested glyph leaves (left: {string.Join(", ", left)}); MuPDF reads {reading}");

        static Dictionary<char, int> Glyphs(string? text) =>
            (text ?? "").Where(c => !char.IsWhiteSpace(c)).GroupBy(c => c).ToDictionary(g => g.Key, g => g.Count());
    }

    /// <summary>#2055: <paramref name="linear"/> (a b c d) applied about the
    /// block's middle, so every glyph stays on the page.</summary>
    private static double[] AboutTheBlock(double[] linear)
    {
        var (a, b, c, d) = (Math.Round(linear[0], 6), Math.Round(linear[1], 6), Math.Round(linear[2], 6), Math.Round(linear[3], 6));
        const double cx = 200, cy = 493, px = 306, py = 396;
        return [a, b, c, d, Math.Round(px - (a * cx + c * cy), 4), Math.Round(py - (b * cx + d * cy), 4)];
    }

    /// <summary>#1891: <paramref name="name"/> wrapped after <paramref name="wrapAfter"/>
    /// words in a block turned <paramref name="degrees"/> about its middle.</summary>
    private static byte[] ObliqueWrap(int degrees, Shape shape, bool viaTm, string name, int wrapAfter)
    {
        var words = name.Split(' ');
        return BuildPdf(shape, TurnedAboutTheBlock(degrees), viaTm,
            (72, 500, $"{Lead} {string.Join(' ', words.Take(wrapAfter))}"),
            (72, 486, $"{string.Join(' ', words.Skip(wrapAfter))} {Tail}"));
    }

    [Fact]
    public void ATermInVerticalWriting_WrappedOntoTheNextColumn_IsRemovedFromBothColumns()
    {
        // #1902 / #2011: Identity-V writing runs down a column and wraps onto
        // the next column to the left. The line model reads it in the vertical
        // frame, so the column change is a wrap like a line change in upright
        // text, and a term across it is removed from both columns.
        Assert.SkipUnless(PdftotextTextExtractor.IsAvailable, "pdftotext not installed [requires: tool:pdftotext]");
        const string term = "日本語 漢字";
        var pdf = VerticalWritingTextOracleTests.TwoColumnIdentityVPdf(secondColumnX: 270);
        var input = Path.Combine(Path.GetTempPath(), $"excise-2011-v-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(input, pdf);
        string? output = null;
        try
        {
            // Poppler reads a column as a line: the independent count of the term.
            var popplerInput = Regex.Replace(PdftotextTextExtractor.ExtractPage(input, 1) ?? "", @"\s+", " ");
            var occurrences = Regex.Matches(popplerInput, Regex.Escape(term)).Count;
            occurrences.Should().Be(1, $"fixture sanity: pdftotext reads the term across the column change:\n{popplerInput}");

            // The scanner reads these Identity codes as UTF-16BE strings.
            foreach (var part in new[] { "日本語", "漢字" })
                SavedPdfLeakScanner.FindTerm(pdf, part).Should().NotBeEmpty($"fixture sanity: the scanner finds '{part}' in the input");

            RedactionReport report;
            byte[] saved;
            (report, saved, output) = Redact(pdf, term);
            report.MatchesLocated.Should().Be(occurrences, report.ToString());
            report.VerifiedRemovals.Should().Be(occurrences, report.ToString());
            report.WordWrapCandidates.Should().BeEmpty("a confirmed column wrap is removed, not merely reported");
            report.IsCleanSuccess.Should().BeTrue(report.ToString());

            foreach (var part in new[] { "日本語", "漢字" })
                SavedPdfLeakScanner.FindTerm(saved, part).Should().BeEmpty($"'{part}' must leave the file");
            var popplerOutput = PdftotextTextExtractor.ExtractPage(output, 1);
            popplerOutput.Should().NotBeNull();
            popplerOutput.Should().NotContain("日").And.NotContain("本").And.NotContain("語")
                .And.NotContain("漢").And.NotContain("字");
            popplerOutput.Should().Contain("、", "the glyph after the term in the second column stays");
        }
        finally
        {
            File.Delete(input);
            if (output != null) File.Delete(output);
        }
    }

    [Fact]
    public void RotatedText_TwoSequentialRedactions_WithACurlyQuoteADollarAndParentheses()
    {
        // CLAUDE.md rule 9 at 90 degrees: the second redaction runs on the
        // content stream the first rebuilt, and both terms carry characters
        // that are escaped or folded (a right single quote the user types as
        // an apostrophe, $, parentheses).
        RequireOracles();
        var pdf = BuildPdf(Shape.Tj, Rotations[90], viaTm: false,
            (72, 500, "Paid $1,250 \\(net\\) to Siobhan O\\222Rourke"),
            (72, 486, "Brannigan on behalf of the company."));
        var output = Path.Combine(Path.GetTempPath(), $"excise-1882-{Guid.NewGuid():N}.pdf");
        try
        {
            using (var doc = PdfDocument.Open(pdf))
            {
                var first = doc.RedactText("Siobhan O'Rourke Brannigan", RedactionOptions.Default);
                first.VerifiedRemovals.Should().Be(1, first.ToString());
                first.IsCleanSuccess.Should().BeTrue(first.ToString());
                var second = doc.RedactText("$1,250 (net)", RedactionOptions.Default);
                second.VerifiedRemovals.Should().Be(1, second.ToString());
                second.IsCleanSuccess.Should().BeTrue(second.ToString());
                doc.Save(output);
            }

            var saved = File.ReadAllBytes(output);
            foreach (var fragment in new[] { "Siobhan", "Rourke", "Brannigan", "1,250" })
                SavedPdfLeakScanner.FindTerm(saved, fragment).Should().BeEmpty($"'{fragment}' must leave the file");
            foreach (var (tool, text) in Readings(output))
            {
                foreach (var fragment in new[] { "Siobhan", "Rourke", "Brannigan", "1,250", "(net)" })
                    text.Should().NotContain(fragment, $"{tool} must not read '{fragment}'");
                text.Should().Contain("Paid", $"{tool} must still read the text before the first term");
                text.Should().Contain("on behalf of the company", $"{tool} must still read the second line");
            }
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Theory]
    [MemberData(nameof(TypographicWrappedNames))]
    public void AWrappedNameWithACurlyQuoteOrEnDash_IsRemovedFromBothLines(
        string term, string line1, string line2, RedactionProfile profile)
    {
        // #1791 with #1871: the wrap stands in for a space AND the page's
        // typographic quote, dash and doubled space fold to what was typed.
        RequireOracles();
        var (report, saved, output) = Redact(BuildPdf(Shape.Tj, (72, 700, line1), (72, 686, line2)), term, profile);
        try
        {
            report.VerifiedRemovals.Should().Be(1, report.ToString());
            report.IsCleanSuccess.Should().BeTrue(report.ToString());

            SavedPdfLeakScanner.FindTerm(saved, term).Should().BeEmpty();
            // The printed name's letters, split where the page has a quote,
            // a dash or a space: what a reader of the bytes would find.
            var fragments = Regex.Split(term, "[^A-Za-z]+").Where(f => f.Length >= 3).ToList();
            fragments.Should().HaveCountGreaterThan(2);
            foreach (var fragment in fragments)
                SavedPdfLeakScanner.FindTerm(saved, fragment).Should().BeEmpty($"'{fragment}' must leave the file");
            foreach (var (tool, text) in Readings(output))
            {
                foreach (var fragment in fragments)
                    text.Should().NotContain(fragment, $"{tool} must not read '{fragment}'");
                text.Should().Contain("signed by", $"{tool} must still read the first line's text");
                text.Should().Contain("on behalf of the company", $"{tool} must still read the second line's text");
            }
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public void ANameWrappedOverThreeLines_IsRemovedFromEveryLine()
    {
        RequireOracles();
        const string name = "Octavia Lucinda Pemberton Vasquez Thornbury";
        var (report, saved, output) = Redact(BuildPdf(Shape.Tj,
            (72, 700, $"{Lead} Octavia Lucinda"),
            (72, 686, "Pemberton Vasquez"),
            (72, 672, $"Thornbury {Tail}")), name);
        try
        {
            report.VerifiedRemovals.Should().Be(1, report.ToString());
            report.IsCleanSuccess.Should().BeTrue(report.ToString());
            AssertNameGone(saved, output, name, "signed by", "on behalf of the company");
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public void AWrapInsideTheRightColumn_IsRemoved_AndTheLeftColumnOnTheSameBaselinesIsKept()
    {
        RequireOracles();
        const string name = "Ignatius Rowena Mahoney Quill";
        // The left column is drawn first, the right column after it: each
        // column is one block in reading order, and the left column's lines
        // sit on the very baselines the name wraps across.
        var (report, saved, output) = Redact(BuildPdf(Shape.Tj,
            (72, 700, "Left column opening line"),
            (72, 686, "Left column closing line"),
            (320, 700, "Signed by Ignatius Rowena"),
            (320, 686, "Mahoney Quill today")), name);
        try
        {
            report.VerifiedRemovals.Should().Be(1, report.ToString());
            report.IsCleanSuccess.Should().BeTrue(report.ToString());
            AssertNameGone(saved, output, name,
                "Left column opening line", "Left column closing line", "Signed by", "today");
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Theory]
    [InlineData(Shape.Tj)]
    [InlineData(Shape.TrailingSpace)]
    [InlineData(Shape.KernedTj)]
    public void ANameContinuedAtTheTopOfTheNextColumn_IsReported_AndLeftInPlace(Shape shape)
    {
        RequireOracles();
        const string name = "Quentin Barnaby Holloway";
        // The first column ends with "Quentin Barnaby"; the second column's
        // first line, above it and to the right, begins "Holloway". A reader
        // may well read that as the name, but the geometry is not a line wrap
        // excise can confirm, so it must be REPORTED and nothing removed.
        // #1884: whether or not the producer left a space glyph at the foot of
        // the first column, which used to let the match jump anywhere.
        var (report, saved, output) = Redact(BuildPdf(shape,
            (72, 700, "First column text"),
            (72, 686, "ends with Quentin Barnaby"),
            (320, 700, "Holloway opens the second"),
            (320, 686, "column of the page")), name);
        try
        {
            report.MatchesLocated.Should().Be(0, "an unconfirmed continuation is not joined");
            report.WordWrapCandidates.Should().ContainSingle(report.ToString());
            report.WordWrapCandidates[0].BeforeBreak.Should().Be("Quentin Barnaby");
            report.WordWrapCandidates[0].AfterBreak.Should().Be("Holloway");
            report.IsCleanSuccess.Should().BeFalse("a readable occurrence is still on the page");

            AssertAllPresent(saved, output, "Quentin", "Barnaby", "Holloway", "First column text", "second");
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Theory]
    [InlineData(Shape.Tj)]
    [InlineData(Shape.TrailingSpace)]
    [InlineData(Shape.KernedTj)]
    [InlineData(Shape.UnitFontTm)]
    public void ANameWrappedInAColumnDrawnRowByRow_IsRemoved_AndTheOtherColumnIsKept(Shape shape)
    {
        // #1883: the page draws its two columns row by row, so the right
        // column's first line sits in the stream between the two lines the
        // name wraps across. Poppler reads the column as a block; MuPDF reads
        // stream order and never forms the name, so a check by MuPDF alone
        // called the page clean while "Redacted 0" left it readable.
        RequireOracles();
        const string name = "Quentin Barnaby Holloway";
        var pdf = BuildPdf(shape,
            (72, 700, "Signed by Quentin Barnaby"),
            (320, 700, "Right column first line"),
            (72, 686, "Holloway on behalf"),
            (320, 686, "Right column second line"));
        AssertPopplerReads(pdf, name);

        var (report, saved, output) = Redact(pdf, name);
        try
        {
            report.MatchesLocated.Should().Be(1, report.ToString());
            report.VerifiedRemovals.Should().Be(1, report.ToString());
            report.IsCleanSuccess.Should().BeTrue(report.ToString());

            AssertNameGone(saved, output, name, "Signed by", "on behalf");
            // The match is removed line by line, never as one stream range: the
            // right column's first line lies between its halves (#942).
            AssertAllPresent(saved, output, "Right column first line", "Right column second line",
                "Right", "column", "first", "second", "Signed", "behalf");
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public void ANameWrappedOverThreeRowsOfAColumnDrawnRowByRow_IsRemovedFromEveryLine()
    {
        RequireOracles();
        const string name = "Octavia Lucinda Pemberton Vasquez Thornbury";
        var pdf = BuildPdf(Shape.Tj,
            (72, 700, "Signed by Octavia Lucinda"),
            (320, 700, "Right column first line"),
            (72, 686, "Pemberton Vasquez"),
            (320, 686, "Right column second line"),
            (72, 672, "Thornbury on behalf"),
            (320, 672, "Right column third line"));
        AssertPopplerReads(pdf, name);

        var (report, saved, output) = Redact(pdf, name);
        try
        {
            report.VerifiedRemovals.Should().Be(1, report.ToString());
            report.IsCleanSuccess.Should().BeTrue(report.ToString());
            AssertNameGone(saved, output, name, "Signed by", "on behalf");
            AssertAllPresent(saved, output, "Right column first line", "Right column second line",
                "Right column third line");
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public void ANameWrappedInTheRightColumnOfARowByRowPage_IsRemoved_AndTheLeftColumnIsKept()
    {
        RequireOracles();
        const string name = "Quentin Barnaby Holloway";
        var pdf = BuildPdf(Shape.Tj,
            (72, 700, "Left column first line"),
            (320, 700, "Witnessed by Quentin Barnaby"),
            (72, 686, "Left column second line"),
            (320, 686, "Holloway in person"));
        AssertPopplerReads(pdf, name);

        var (report, saved, output) = Redact(pdf, name);
        try
        {
            report.VerifiedRemovals.Should().Be(1, report.ToString());
            report.IsCleanSuccess.Should().BeTrue(report.ToString());
            AssertNameGone(saved, output, name, "Witnessed by", "in person");
            AssertAllPresent(saved, output, "Left column first line", "Left column second line");
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public void AWideGapInsideALine_DoesNotSplitIt_AndAWrapAfterItIsRemoved()
    {
        // #1883: a line drawn in two pieces with a gap of four ems between
        // them (a justified or tabbed line). The second piece heads no column,
        // so it is still the end of the line that wraps onto the next: were it
        // a line of its own, the first piece would take the wrap and the name
        // would be left in place.
        RequireOracles();
        const string name = "Quentin Barnaby Holloway";
        var (report, saved, output) = Redact(BuildPdf(Shape.Tj,
            (72, 700, "This agreement was"),
            (220, 700, "signed by Quentin Barnaby"),
            (72, 686, $"Holloway {Tail}")), name);
        try
        {
            report.VerifiedRemovals.Should().Be(1, report.ToString());
            report.IsCleanSuccess.Should().BeTrue(report.ToString());
            AssertNameGone(saved, output, name, "This agreement was", "signed by", "on behalf of the company");
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public void ANameWrappedBesideAOneLineColumn_IsReported_AndLeftInPlace()
    {
        // #1883: the right column has one line, drawn between the two lines
        // the name wraps across. With no line of its own above or below, it
        // cannot be told from the rest of a justified line, so the geometry
        // does not confirm the wrap: the name is reported, never called clean.
        RequireOracles();
        const string name = "Quentin Barnaby Holloway";
        var pdf = BuildPdf(Shape.Tj,
            (72, 700, "Signed by Quentin Barnaby"),
            (320, 700, "Right column only line"),
            (72, 686, "Holloway on behalf"));
        AssertPopplerReads(pdf, name);

        var (report, saved, output) = Redact(pdf, name);
        try
        {
            report.MatchesLocated.Should().Be(0, "an unconfirmed wrap is not joined");
            report.WordWrapCandidates.Should().ContainSingle(report.ToString());
            report.WordWrapCandidates[0].BeforeBreak.Should().Be("Quentin Barnaby");
            report.WordWrapCandidates[0].AfterBreak.Should().Be("Holloway");
            report.IsCleanSuccess.Should().BeFalse("a readable occurrence is still on the page");
            AssertAllPresent(saved, output, "Quentin", "Barnaby", "Holloway", "Right column only line");
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public void AWrapAfterASuperscriptFootnoteMark_IsRemoved()
    {
        // #1883, measured on scotus-trump-v-us.pdf page 64: a raised footnote
        // mark splits its line in the stream, and both the text before it and
        // the text after it lie above the next line's start. The text after
        // it ends the line; the text before it must not take the wrap.
        RequireOracles();
        const string name = "the President";
        var pdf = BuildPdf(Shape.Tj,
            (72, 700, "(2020)."),
            (111, 707, "3"),
            (120, 700, "An important difference is that the"),
            (72, 686, "President is entitled to appeal."));
        AssertReadersRead(pdf, name);

        var (report, saved, output) = Redact(pdf, name);
        try
        {
            report.VerifiedRemovals.Should().Be(1, report.ToString());
            report.IsCleanSuccess.Should().BeTrue(report.ToString());
            AssertNameGone(saved, output, name, "(2020).", "An important difference is that", "is entitled to appeal");
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public void TheEndOfOneColumnsLineAndTheNextLineOfTheOtherColumn_AreNotJoined()
    {
        // #1883: on the same row-by-row page, the right column's first line
        // and the left column's second line are consecutive in the stream,
        // one line pitch apart, the second starting left of where the first
        // ends. Joined by stream order, "line Holloway" matched and was removed.
        RequireOracles();
        var (report, saved, output) = Redact(BuildPdf(Shape.Tj,
            (72, 700, "Signed by Quentin Barnaby"),
            (320, 700, "Right column first line"),
            (72, 686, "Holloway on behalf"),
            (320, 686, "Right column second line")), "line Holloway");
        try
        {
            report.MatchesLocated.Should().Be(0, "the right column's line does not wrap onto the left column's");
            AssertAllPresent(saved, output, "Right column first line", "Holloway on behalf");
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public void ALineInTheRightColumnAndTheNextLineInTheLeft_AreNotJoined_AndAreReported()
    {
        // #1883: a table row whose last cell is in the right column, then the
        // next row's first cell at the left margin. One line pitch down and
        // wholly left of where the first ends, like a wrap, but the two lines
        // share no extent: no block holds both. Stream order joined them.
        RequireOracles();
        const string name = "Quentin Barnaby Holloway";
        var (report, saved, output) = Redact(BuildPdf(Shape.Tj,
            (320, 700, "Name Quentin Barnaby"),
            (72, 686, "Holloway Street office")), name);
        try
        {
            report.MatchesLocated.Should().Be(0, "a line in another column is not the next line of this one");
            report.WordWrapCandidates.Should().ContainSingle("consecutive in the stream, so the net reports them");
            report.IsCleanSuccess.Should().BeFalse(report.ToString());
            AssertAllPresent(saved, output, "Quentin", "Barnaby", "Holloway", "office");
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public void TheSameWordsOnNonAdjacentLines_AreUntouched_AndNotReported()
    {
        RequireOracles();
        const string name = "Quentin Barnaby Holloway";
        var (report, saved, output) = Redact(BuildPdf(Shape.Tj,
            (72, 700, "Witnessed by Quentin Barnaby"),
            (72, 686, "and nobody else at all"),
            (72, 672, "Holloway Street is the venue")), name);
        try
        {
            report.MatchesLocated.Should().Be(0, "the halves are not consecutive lines of one phrase");
            report.WordWrapCandidates.Should().BeEmpty("a line of other text sits between them");
            report.IsCleanSuccess.Should().BeTrue(report.ToString());

            AssertAllPresent(saved, output, "Quentin", "Barnaby", "Holloway", "nobody else");
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Theory]
    [InlineData(Shape.Tj)]
    [InlineData(Shape.TrailingSpace)]
    public void AContinuationBelowAndToTheRight_IsNotJoined(Shape shape)
    {
        RequireOracles();
        const string name = "Quentin Barnaby Holloway";
        // Drawn one after the other, one line pitch apart, but the second
        // starts in another column to the RIGHT of where the first ends: a
        // table row, not a wrap. Joining it would redact across columns.
        var (report, saved, output) = Redact(BuildPdf(shape,
            (72, 700, "Name Quentin Barnaby"),
            (320, 686, "Holloway Street office")), name);
        try
        {
            report.MatchesLocated.Should().Be(0, "text in another column is not the next line");
            report.IsCleanSuccess.Should().BeFalse(
                "the halves are consecutive in reading order, so the net reports them");

            AssertAllPresent(saved, output, "Quentin", "Barnaby", "Holloway", "office");
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public void AMultiWordNameHyphenatedAcrossTheBreak_IsReported()
    {
        RequireOracles();
        const string name = "Quentin Barnaby Holloway";
        // #1372's hyphen policy stands: a line-end hyphen splits a word, not a
        // phrase, and is reported rather than joined. Before #1791 the
        // hyphen net only looked at the one word either side of the hyphen,
        // so "Barn-" / "aby" never contained the three-word name and the
        // report was clean over a readable name.
        var (report, saved, output) = Redact(BuildPdf(Shape.Tj,
            (72, 700, $"{Lead} Quentin Barn-"),
            (72, 686, $"aby Holloway {Tail}")), name);
        try
        {
            report.HyphenatedCandidates.Should().ContainSingle(report.ToString());
            report.HyphenatedCandidates[0].BeforeBreak.Should().Be("Quentin Barn");
            report.HyphenatedCandidates[0].AfterBreak.Should().Be("aby Holloway");
            report.IsCleanSuccess.Should().BeFalse("a readable occurrence is still on the page");

            AssertAllPresent(saved, output, "Quentin", "Holloway");
        }
        finally
        {
            File.Delete(output);
        }
    }

    private static void RequireOracles()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed [requires: tool:mutool]");
        Assert.SkipUnless(PdftotextTextExtractor.IsAvailable, "pdftotext not installed [requires: tool:pdftotext]");
    }

    private static (RedactionReport Report, byte[] Saved, string Output) Redact(
        byte[] pdf, string term, RedactionProfile profile = RedactionProfile.Standard)
    {
        var output = Path.Combine(Path.GetTempPath(), $"excise-1791-{Guid.NewGuid():N}.pdf");
        using var doc = PdfDocument.Open(pdf);
        var report = doc.RedactText(term, RedactionOptions.ForProfile(profile));
        doc.Save(output);
        return (report, File.ReadAllBytes(output), output);
    }

    /// <summary>
    /// Every word of <paramref name="name"/> is gone from the saved bytes, and
    /// neither MuPDF nor Poppler reads the name or any word of it; both still
    /// read every one of <paramref name="kept"/>.
    /// </summary>
    private static void AssertNameGone(byte[] saved, string output, string name, params string[] kept)
    {
        // A one-letter word is a byte any file holds; the readers judge it as a word.
        foreach (var word in name.Split(' ').Where(w => w.Length >= 3))
            SavedPdfLeakScanner.FindTerm(saved, word).Should().BeEmpty($"'{word}' of the name must leave the file");

        foreach (var (tool, text) in Readings(output))
        {
            text.Should().NotContain(name, $"{tool} must not read the name across the break");
            foreach (var word in name.Split(' '))
                text.Should().NotMatchRegex($@"\b{Regex.Escape(word)}\b", $"{tool} must not read '{word}'");
            foreach (var neighbour in kept)
                text.Should().Contain(neighbour, $"{tool} must still read the neighbouring text '{neighbour}'");
        }
    }

    private static void AssertAllPresent(byte[] saved, string output, params string[] kept)
    {
        foreach (var word in kept.Where(k => !k.Contains(' ')))
            SavedPdfLeakScanner.FindTerm(saved, word).Should().NotBeEmpty($"'{word}' was not asked for and stays");
        foreach (var (tool, text) in Readings(output))
            foreach (var word in kept)
                text.Should().Contain(word, $"{tool} must still read '{word}'");
    }

    /// <summary>Both independent readers' page text, whitespace collapsed so a
    /// line break reads as the space it stands for.</summary>
    private static IEnumerable<(string Tool, string Text)> Readings(string output)
    {
        foreach (var (tool, raw) in new[]
                 {
                     ("mutool", MutoolTextExtractor.ExtractPage(output, 1)),
                     ("pdftotext", PdftotextTextExtractor.ExtractPage(output, 1)),
                 })
        {
            raw.Should().NotBeNull($"{tool} must read the saved file");
            yield return (tool, Regex.Replace(raw!, @"\s+", " "));
        }
    }

    /// <summary>
    /// A readable occurrence is the fixture's whole premise: MuPDF and Poppler
    /// both read <paramref name="name"/> across the break in the input.
    /// </summary>
    private static void AssertReadersRead(byte[] pdf, string name)
    {
        var input = Path.Combine(Path.GetTempPath(), $"excise-wrap-in-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(input, pdf);
        try
        {
            foreach (var (tool, text) in Readings(input))
                text.Should().Contain(name, $"{tool} must read the name in the fixture");
        }
        finally
        {
            File.Delete(input);
        }
    }

    /// <summary>#1883: Poppler, which reads a column as a block, reads
    /// <paramref name="name"/> in the fixture. MuPDF reads stream order and may not.</summary>
    private static void AssertPopplerReads(byte[] pdf, string name)
    {
        var input = Path.Combine(Path.GetTempPath(), $"excise-wrap-in-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(input, pdf);
        try
        {
            var text = PdftotextTextExtractor.ExtractPage(input, 1);
            text.Should().NotBeNull();
            Regex.Replace(text!, @"\s+", " ").Should().Contain(name, "pdftotext must read the name in the fixture");
        }
        finally
        {
            File.Delete(input);
        }
    }

    private static byte[] BuildPdf(Shape shape, params (double X, double Y, string Text)[] lines) =>
        BuildPdf(shape, matrix: null, viaTm: shape == Shape.UnitFontTm, lines);

    /// <summary>
    /// One page, one text object per line. <paramref name="matrix"/> turns the
    /// whole page: by <c>cm</c>, or with <paramref name="viaTm"/> folded into
    /// each line's text matrix over a unit font.
    /// </summary>
    private static byte[] BuildPdf(
        Shape shape, double[]? matrix, bool viaTm, params (double X, double Y, string Text)[] lines)
    {
        string Num(double v) => v.ToString(CultureInfo.InvariantCulture);
        var m = matrix ?? [1, 0, 0, 1, 0, 0];
        var content = new StringBuilder();
        if (matrix != null && !viaTm)
            content.Append($"q {string.Join(' ', m.Select(Num))} cm\n");
        for (var i = 0; i < lines.Length; i++)
        {
            var (x, y, text) = lines[i];
            if (shape == Shape.TrailingSpace && i < lines.Length - 1)
                text += " ";
            var show = shape switch
            {
                Shape.KernedTj => "[" + string.Join(" -400 ", text.Split(' ').Select(w => $"({w})")) + "] TJ",
                Shape.TjPerWord => string.Join(" ( ) Tj ", text.Split(' ').Select(w => $"({w}) Tj")),
                Shape.TjPerGlyph => string.Join(" ", text.Select(c => $"({c}) Tj")),
                _ => $"({text}) Tj",
            };
            var position = viaTm
                ? $"/F1 1 Tf {Num(12 * m[0])} {Num(12 * m[1])} {Num(12 * m[2])} {Num(12 * m[3])} "
                    + $"{Num(m[0] * x + m[2] * y + m[4])} {Num(m[1] * x + m[3] * y + m[5])} Tm"
                : $"/F1 12 Tf {Num(x)} {Num(y)} Td";
            content.Append($"BT {position} {show} ET\n");
        }
        if (matrix != null && !viaTm)
            content.Append("Q\n");

        var rotate = shape == Shape.Rotate90 ? " /Rotate 90" : "";
        var body = content.ToString();
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]{rotate} "
                + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {body.Length} >>\nstream\n{body}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        };

        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[objects.Length];
        for (var i = 0; i < objects.Length; i++)
        {
            offsets[i] = pdf.Length;
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
