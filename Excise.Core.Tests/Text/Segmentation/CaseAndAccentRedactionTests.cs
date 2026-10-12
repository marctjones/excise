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
/// Term redaction removes every spelling a case-insensitive search finds under Unicode
/// full case folding: what search shows, redaction must remove. The fold changes length
/// (ß → ss), so the glyphs removed must be exactly the matched ones. Each test asserts
/// the removal with oracles excise is not (CLAUDE.md rule 4): the saved bytes,
/// decompressed (<see cref="SavedPdfLeakScanner"/>), and MuPDF's text and glyph positions.
/// </summary>
public class CaseAndAccentRedactionTests
{
    private const double PageHeight = 792;

    private static byte[] Fixture(IReadOnlyList<string> lines, string? title = null)
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(612, PageHeight);
        using (var g = page.GetGraphics())
        {
            var font = PdfFont.Helvetica(12);
            var y = 700.0;
            foreach (var line in lines)
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

    /// <summary>Every surviving MuPDF word of <paramref name="original"/> that <paramref name="keep"/> selects sits where it was.</summary>
    private static void SurvivorsUnmoved(byte[] original, byte[] saved, System.Func<string, bool> keep)
    {
        var before = MutoolStextOracle.Words(original, 1, PageHeight);
        var after = MutoolStextOracle.Words(saved, 1, PageHeight);
        foreach (var word in before.Where(w => keep(w.Text)))
            after.Should().Contain(w => w.Text == word.Text
                    && System.Math.Abs(w.Left - word.Left) < 0.01 && System.Math.Abs(w.Bottom - word.Bottom) < 0.01,
                $"'{word.Text}' must survive in place");
    }

    [Theory]
    [InlineData("strasse")]
    [InlineData("STRASSE")]
    [InlineData("Straße")]
    public void RedactingStrasse_RemovesStraßeAndSTRASSE_AndMovesNothing(string term)
    {
        SkipWithoutMutool();
        var original = Fixture(["Line A Straße here", "Line B STRASSE here", "Line C Strass stays"],
            title: "Die Straße");

        var (saved, report) = Redact(original, term, RedactionOptions.Default with { StripDocumentMetadata = false });

        report.VerifiedRemovals.Should().Be(2, "Straße and STRASSE");
        report.Survived.Should().Be(0);

        foreach (var spelling in new[] { "Straße", "STRASSE", "Stra" + "ß" })
            SavedPdfLeakScanner.FindTerm(saved, spelling).Should().BeEmpty($"'{spelling}' must be gone from the file");
        var mutool = MutoolTextOracle.ExtractAllPages(saved);
        mutool.Should().NotContain("Straße").And.NotContain("STRASSE").And.NotContain("ße");
        mutool.Should().Contain("Strass stays", "a shorter word is not the term").And.Contain("Line A").And.Contain("here");
        SurvivorsUnmoved(original, saved, t => !t.StartsWith("Stra") && !t.StartsWith("STRA") || t == "Strass");

        using var doc = PdfDocument.Open(saved);
        doc.Title.Should().NotContain("Stra").And.Contain("Die", "the carrier scrub uses the same fold");
    }

    [Fact]
    public void MatchCase_LeavesTheOtherSpelling_SoTheOracleCanSeeALeak()
    {
        // The counterfactual: under Match case the fold does not apply, and the same
        // oracles see the other spelling still in the file.
        SkipWithoutMutool();
        var (saved, report) = Redact(Fixture(["Line A Straße here", "Line B STRASSE here"]), "STRASSE",
            RedactionOptions.Default with { CaseSensitive = true });

        report.VerifiedRemovals.Should().Be(1);
        MutoolTextOracle.ExtractAllPages(saved).Should().Contain("Straße");
    }

    [Fact]
    public void TheMatcher_RemovesExactlyTheGlyphsOfTheFoldedMatch()
    {
        // ß is one glyph that folds to two letters: the slice must be its glyph, no
        // more, no less, and the word around a partial match keeps its other glyphs.
        var letters = new List<Letter>();
        var x = 60.0;
        foreach (var c in "Hauptstraße 1") { letters.Add(L(c.ToString(), x)); x += 6; }

        var matches = PdfDocumentRedactionExtensions.FindTextMatches(letters, "STRASSE", caseSensitive: false,
            separators: SeparatorMatching.Default);
        matches.Should().ContainSingle();
        string.Concat(matches[0].Select(l => l.Value)).Should().Be("straße");

        matches = PdfDocumentRedactionExtensions.FindTextMatches(letters, "sse", caseSensitive: false,
            separators: SeparatorMatching.Default);
        string.Concat(matches.Single().Select(l => l.Value)).Should().Be("ße", "a match inside the ß takes the ß");
    }

    [Theory]
    [InlineData("cafe")]
    [InlineData("café")]
    [InlineData("CAFE")]
    public void RedactingCafe_RemovesCafeCaféAndCAFÉ_AndMovesNothing(string term)
    {
        SkipWithoutMutool();
        var original = Fixture(["Line A café here", "Line B CAFÉ here", "Line C cafe here", "Line D cafeteria stays"],
            title: "Le café du coin");

        var (saved, report) = Redact(original, term,
            RedactionOptions.Default with { WholeWord = true, StripDocumentMetadata = false });

        report.VerifiedRemovals.Should().Be(3, "café, CAFÉ and cafe");
        report.Survived.Should().Be(0);
        report.ToString().Should().NotContain("exact accents", "the default goes unsaid");

        foreach (var spelling in new[] { "café", "CAFÉ", "cafe here" })
            SavedPdfLeakScanner.FindTerm(saved, spelling).Should().BeEmpty($"'{spelling}' must be gone from the file");
        var mutool = MutoolText(saved);
        mutool.Should().NotContain("café").And.NotContain("CAFÉ").And.NotContain("cafe here");
        mutool.Should().Contain("cafeteria stays", "whole-word: a longer word is not the term");
        SurvivorsUnmoved(original, saved, t => !t.StartsWith("caf", System.StringComparison.OrdinalIgnoreCase)
            || t == "cafeteria");

        using var doc = PdfDocument.Open(saved);
        doc.Title.Should().NotContain("caf").And.Contain("coin", "the carrier scrub uses the same rule");
    }

    [Fact]
    public void ExactAccents_LeaveTheAccentedSpellings_SoTheOracleCanSeeALeak()
    {
        SkipWithoutMutool();
        var (saved, report) = Redact(Fixture(["Line A café here", "Line C cafe here"]), "cafe",
            RedactionOptions.Default with { IgnoreAccents = false });

        report.VerifiedRemovals.Should().Be(1);
        report.ToString().Should().Contain("exact accents");
        SavedPdfLeakScanner.FindTerm(saved, "café").Should().NotBeEmpty();
        MutoolText(saved).Should().Contain("café");
    }

    private static string MutoolText(byte[] pdf) => MutoolTextOracle.ExtractAllPages(pdf);

    private static Letter L(string v, double x) =>
        new(v, new PdfRectangle(x, 700, x + 6, 712), 12, "F1", x, 700, 6, v.Length > 0 ? v[0] : ' ');
}
