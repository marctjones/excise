using System;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Excise.Rendering.Differential;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1372 — the redaction bench's security verdict is "can any independent tool
/// still read the term". Until this pair of oracles existed, that question had
/// exactly one answer: mutool's. mutool is MuPDF, and PyMuPDF — one of the
/// redactors the bench measures — is the same C library through a Python
/// binding, so MuPDF was grading MuPDF's own redaction and would share its
/// blind spots. That is the self-oracle failure the project's rule exists to
/// prevent, one step removed.
///
/// <para>These tests pin the property the bench now relies on: two extractors,
/// two engines, agreeing on ordinary text. They do not assert that the two are
/// character-identical — Poppler and MuPDF differ on layout, ligatures and
/// whitespace by design — only that both read the same words, which is what a
/// leak check asks.</para>
/// </summary>
public class SecondTextOracleTests
{
    [Fact]
    public void PdftotextIsPresent_SoTheBenchHasTwoEngines()
    {
        Assert.SkipUnless(File.Exists("/opt/homebrew/bin/pdftotext") || File.Exists("/usr/bin/pdftotext")
                          || File.Exists("/usr/local/bin/pdftotext"),
            "poppler's pdftotext not installed — the bench falls back to one text engine");
        PdftotextTextExtractor.IsAvailable.Should().BeTrue(
            "the adapter must detect a pdftotext that is on PATH");
    }

    [Theory]
    [InlineData("irs-w9.pdf")]
    [InlineData("cdc-vis-covid-19.pdf")]
    public void BothEnginesReadTheSameWords(string fileName)
    {
        Assert.SkipUnless(PdftotextTextExtractor.IsAvailable, "pdftotext not present");
        var path = FindRepoFile("test-pdfs", "smoke", fileName);
        Assert.SkipWhen(path == null, "smoke corpus not present");

        var mutool = MutoolTextExtractor.ExtractPage(path!, 1);
        var poppler = PdftotextTextExtractor.ExtractPage(path!, 1);
        mutool.Should().NotBeNullOrWhiteSpace("mutool must read a government form");
        poppler.Should().NotBeNullOrWhiteSpace("poppler must read a government form");

        var a = Words(mutool!);
        var b = Words(poppler!);
        a.Count.Should().BeGreaterThan(20, "a near-empty extraction would make this vacuous");

        // Agreement on the WORD SET, not on layout. Two independent engines
        // differ on spacing and line order; they must not differ on what words
        // are on the page.
        var shared = a.Intersect(b).Count();
        var overlap = (double)shared / Math.Max(a.Count, b.Count);
        overlap.Should().BeGreaterThan(0.85,
            $"mutool and pdftotext should read substantially the same words from {fileName} " +
            $"page 1; overlap={overlap:P1}, mutool={a.Count} words, poppler={b.Count}");
    }

    /// <summary>
    /// The reason the second engine is worth its cost: it must be able to
    /// answer when asked about a term, so a "no leak" verdict rests on two
    /// readers rather than one. A null answer is data, not a pass.
    /// </summary>
    [Fact]
    public void PopplerAnswersTheLeakQuestionItIsAskedInTheBench()
    {
        Assert.SkipUnless(PdftotextTextExtractor.IsAvailable, "pdftotext not present");
        var path = FindRepoFile("test-pdfs", "smoke", "irs-w9.pdf");
        Assert.SkipWhen(path == null, "smoke corpus not present");

        var pages = PdftotextTextExtractor.ExtractAllPages(path!, 1);
        pages.Should().NotBeNull("a null answer means the bench silently drops to one engine");
        pages!.Length.Should().Be(1);
        string.Join("\n", pages).Should().Contain("Request for Taxpayer",
            "the second oracle must actually read the page it is asked about");
    }

    private static System.Collections.Generic.HashSet<string> Words(string text)
        => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
               .Select(w => new string(w.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant())
               .Where(w => w.Length >= 3)
               .ToHashSet(StringComparer.Ordinal);

    private static string? FindRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var c = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(c)) return c;
            dir = dir.Parent;
        }
        return null;
    }
}
