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
    }

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

    [Fact]
    public void ANameContinuedAtTheTopOfTheNextColumn_IsReported_AndLeftInPlace()
    {
        RequireOracles();
        const string name = "Quentin Barnaby Holloway";
        // The first column ends with "Quentin Barnaby"; the second column's
        // first line, above it and to the right, begins "Holloway". A reader
        // may well read that as the name, but the geometry is not a line wrap
        // excise can confirm, so it must be REPORTED and nothing removed.
        var (report, saved, output) = Redact(BuildPdf(Shape.Tj,
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

    [Fact]
    public void AContinuationBelowAndToTheRight_IsNotJoined()
    {
        RequireOracles();
        const string name = "Quentin Barnaby Holloway";
        // Drawn one after the other, one line pitch apart, but the second
        // starts in another column to the RIGHT of where the first ends: a
        // table row, not a wrap. Joining it would redact across columns.
        var (report, saved, output) = Redact(BuildPdf(Shape.Tj,
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
        foreach (var word in name.Split(' '))
            SavedPdfLeakScanner.FindTerm(saved, word).Should().BeEmpty($"'{word}' of the name must leave the file");

        foreach (var (tool, text) in Readings(output))
        {
            text.Should().NotContain(name, $"{tool} must not read the name across the break");
            foreach (var word in name.Split(' '))
                text.Should().NotContain(word, $"{tool} must not read '{word}'");
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

    private static byte[] BuildPdf(Shape shape, params (double X, double Y, string Text)[] lines)
    {
        string Num(double v) => v.ToString(CultureInfo.InvariantCulture);
        var content = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            var (x, y, text) = lines[i];
            if (shape == Shape.TrailingSpace && i < lines.Length - 1)
                text += " ";
            var show = shape == Shape.KernedTj
                ? "[" + string.Join(" -400 ", text.Split(' ').Select(w => $"({w})")) + "] TJ"
                : $"({text}) Tj";
            var position = shape == Shape.UnitFontTm
                ? $"/F1 1 Tf 12 0 0 12 {Num(x)} {Num(y)} Tm"
                : $"/F1 12 Tf {Num(x)} {Num(y)} Td";
            content.Append($"BT {position} {show} ET\n");
        }

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
