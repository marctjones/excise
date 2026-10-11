using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Excise.Core.Text;
using Excise.Core.Text.Segmentation;
using Excise.TestSupport;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// Term redaction removes every spelling the GUI search finds for the term
/// (<see cref="RedactionOptions.MatchHyphenAsSpace"/>, default on): what search
/// shows, redaction must remove. Each test asserts the removal with oracles excise
/// is not (CLAUDE.md rule 4): the saved bytes, decompressed
/// (<see cref="SavedPdfLeakScanner"/>), and MuPDF's text and glyph positions.
/// </summary>
public class SeparatorEquivalentRedactionTests
{
    private const double PageHeight = 792;

    /// <summary>One line per spelling, plus neighbours that must survive untouched.</summary>
    private static readonly string[] Lines =
    [
        "Line A open source here",
        "Line B open-source here",
        "Line C open–source here",
        "Line D open—source here",
        "Line E resource stays",
        "Line F opensource stays",
    ];

    private static byte[] Fixture(string? title = null, IReadOnlyList<string>? lines = null)
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(612, PageHeight);
        using (var g = page.GetGraphics())
        {
            var font = PdfFont.Helvetica(12);
            var y = 700.0;
            foreach (var line in lines ?? Lines)
            {
                g.DrawString(line, font, PdfBrush.Black, 72, y);
                y -= 30;
            }
            g.Flush();
        }
        if (title != null) doc.SetTitle(title);
        return doc.SaveToBytes();
    }

    private static (byte[] Saved, RedactionReport Report) Redact(byte[] pdf, string term, RedactionOptions options)
    {
        using var doc = PdfDocument.Open(pdf);
        var report = doc.RedactText(term, options);
        return (doc.SaveToBytes(), report);
    }

    private static void SkipWithoutMutool() =>
        Assert.SkipUnless(MutoolTextOracle.IsAvailable && MutoolStextOracle.IsAvailable,
            "mutool not installed [requires: tool:mutool]");

    [Theory]
    [InlineData("open source")]
    [InlineData("open-source")]
    public void RedactingEitherSpelling_RemovesEverySpacedHyphenatedAndDashedOccurrence_AndMovesNothing(string term)
    {
        SkipWithoutMutool();
        var original = Fixture(title: "An open-source title");

        var (saved, report) = Redact(original, term, RedactionOptions.Default);

        report.VerifiedRemovals.Should().Be(4, "open source, open-source, open–source and open—source");
        report.Survived.Should().Be(0);
        report.MatchHyphenAsSpace.Should().BeTrue("the default rule is part of the result");

        // Independent oracle 1: the bytes, every stream decompressed.
        foreach (var spelling in new[] { "open source", "open-source", "open–source", "open—source" })
            SavedPdfLeakScanner.FindTerm(saved, spelling).Should().BeEmpty($"'{spelling}' must be gone from the file");

        // Independent oracle 2: MuPDF's text. Every spelling gone, every neighbour kept.
        var mutool = MutoolTextOracle.ExtractAllPages(saved);
        foreach (var spelling in new[] { "open source", "open-source", "open–source", "open—source" })
            mutool.Should().NotContain(spelling);
        mutool.Should().Contain("resource stays").And.Contain("opensource stays",
            "the closed compound is not the term unless closed compounds are asked for");

        // Independent oracle 3: MuPDF's glyph positions. The default width policy
        // keeps the layout, so every surviving word sits exactly where it was.
        var before = MutoolStextOracle.Words(original, 1, PageHeight);
        var after = MutoolStextOracle.Words(saved, 1, PageHeight);
        foreach (var word in before.Where(w => !(w.Text.Contains("open") || w.Text.Contains("source")) || w.Text is "resource" or "opensource"))
            after.Should().Contain(w => w.Text == word.Text
                    && System.Math.Abs(w.Left - word.Left) < 0.01 && System.Math.Abs(w.Bottom - word.Bottom) < 0.01,
                $"'{word.Text}' must survive in place");
    }

    [Fact]
    public void ExactSeparators_LeaveTheOtherSpellings_SoTheOracleCanSeeALeak()
    {
        // The counterfactual: with the equivalence off, the same oracles report the
        // hyphenated and dashed spellings as still in the file. A leak test that
        // could not fail would prove nothing.
        SkipWithoutMutool();
        var (saved, report) = Redact(Fixture(), "open source",
            RedactionOptions.Default with { MatchHyphenAsSpace = false });

        report.VerifiedRemovals.Should().Be(1);
        report.ToString().Should().Contain("exact separators");
        SavedPdfLeakScanner.FindTerm(saved, "open-source").Should().NotBeEmpty();
        MutoolTextOracle.ExtractAllPages(saved).Should().Contain("open–source");
    }

    [Fact]
    public void ClosedCompounds_AlsoRemoveTheClosedSpelling_AndStillNotResource()
    {
        SkipWithoutMutool();
        var (saved, report) = Redact(Fixture(), "open source",
            RedactionOptions.Default with { MatchClosedCompounds = true });

        report.VerifiedRemovals.Should().Be(5);
        report.ToString().Should().Contain("closed compounds matched");
        SavedPdfLeakScanner.FindTerm(saved, "opensource").Should().BeEmpty();
        var mutool = MutoolTextOracle.ExtractAllPages(saved);
        mutool.Should().NotContain("opensource").And.Contain("resource stays");
    }

    [Fact]
    public void TheTermLeavesTheDocumentTitle_InEverySpelling()
    {
        // #896: the rule reaches the carriers with the page, never one without the other.
        // Metadata kept, so the term scrub (Strip: cut the term out) is what runs.
        var (saved, _) = Redact(Fixture(title: "An open–source title"), "open source",
            RedactionOptions.Default with { StripDocumentMetadata = false });
        using var doc = PdfDocument.Open(saved);
        doc.Title.Should().NotContain("open").And.Contain("title");
        SavedPdfLeakScanner.FindTerm(saved, "open–source").Should().BeEmpty();
    }

    [Fact]
    public void AHyphenatedCompoundWrappedAtItsHyphen_IsRemovedAsItsHyphenatedSpellingAlwaysWas()
    {
        // "open-" ending one line and "source" starting the next reads as
        // "open-source" to the matcher (a line-end hyphen stays at a wrap, #1791),
        // so the term "open-source" has always removed it. "open source" now does
        // too, because it is the same term.
        SkipWithoutMutool();
        var pdf = Fixture(lines: ["Wrapped open-", "source text"]);

        var (saved, report) = Redact(pdf, "open source", RedactionOptions.Default);

        report.VerifiedRemovals.Should().Be(1);
        report.HyphenatedCandidates.Should().BeEmpty();
        var mutool = MutoolTextOracle.ExtractAllPages(saved);
        mutool.Should().NotContain("open").And.Contain("Wrapped").And.Contain("text");
    }

    private static Letter L(string v, double x, double y, double w) =>
        new(v, new PdfRectangle(x, y, x + w, y + 12), 12, "F1", x, y, w, v.Length > 0 ? v[0] : ' ');

    [Fact]
    public void AWordSplitByALineEndHyphen_IsStillReportedNotJoined_WithBothOptionsOn()
    {
        // #1372 is unchanged: a term with no separator matches literally, so
        // "Ander-" / "son" is still a reported candidate, never a match.
        Letter[] letters =
        [
            L("A", 100, 700, 8), L("n", 108, 700, 7), L("d", 115, 700, 7),
            L("e", 122, 700, 7), L("r", 129, 700, 5), L("-", 134, 700, 4),
            L("s", 100, 686, 6), L("o", 106, 686, 7), L("n", 113, 686, 7),
        ];
        var both = new SeparatorMatching(HyphenAsSpace: true, AllowNoSeparator: true);

        PdfDocumentRedactionExtensions.FindTextMatches(letters, "Anderson", caseSensitive: false, separators: both)
            .Should().BeEmpty();
        var hyphenated = new List<HyphenatedTermCandidate>();
        PdfDocumentRedactionExtensions.FindWrappedCandidates(
            letters, "Anderson", caseSensitive: false, wholeWord: false, 1, hyphenated, [], both);
        hyphenated.Should().ContainSingle().Which.ToString().Should().Be("\"Ander-\" / \"son\"");
    }
}
