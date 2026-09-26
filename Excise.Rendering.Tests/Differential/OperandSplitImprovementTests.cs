using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using Xunit;
using RecoveryFixtureBuilder = Excise.Core.Tests.Redaction.Recovery.RecoveryFixtureBuilder;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1091: a term inside one Tj/TJ operand is cut out of that operand and
/// nothing else is. The neighbours in the same operand keep their glyphs and
/// their positions. Graded by tools that share no code with excise: the saved
/// bytes decompressed by <see cref="SavedPdfLeakScanner"/>, and mutool's text
/// and per-glyph positions.
/// </summary>
public sealed class OperandSplitImprovementTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"operand-split-{Guid.NewGuid():N}");

    public OperandSplitImprovementTests(ITestOutputHelper output)
    {
        _out = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>The term mid-string in a Tj, and mid-element in a kerned TJ.</summary>
    public static TheoryData<string> Shows() => new()
    {
        "(Louise Anne Farrar) Tj",
        "[(Lou) -30 (ise Anne Farrar) 50 (.)] TJ",
    };

    [Theory]
    [MemberData(nameof(Shows))]
    public void TermInsideOneOperand_IsRemoved_AndTheNeighboursKeepTheirGlyphsAndPositions(string show)
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        var input = RecoveryFixtureBuilder.Build($"BT /F1 24 Tf 72 700 Td {show} ET\n");
        var before = Path.Combine(_dir, "before.pdf");
        var after = Path.Combine(_dir, "after.pdf");
        File.WriteAllBytes(before, input);
        using (var doc = PdfDocument.Open(input))
        {
            doc.RedactText("Anne", RedactionOptions.Default);
            doc.Save(after);
        }

        SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(after), "Anne").Should().BeEmpty();
        MutoolTextExtractor.ExtractPage(after, 1).Should()
            .Contain("Louise").And.Contain("Farrar").And.NotContain("Anne");

        // Every glyph but the term's is where mutool put it before the
        // redaction. Spaces are left out: mutool may synthesise one across the
        // gap the term leaves.
        var glyphsBefore = Letters(before);
        var glyphsAfter = Letters(after);
        var start = string.Concat(glyphsBefore.Select(g => g.Char)).IndexOf("Anne", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, "mutool reads the term on the input");
        var expected = glyphsBefore.Take(start).Concat(glyphsBefore.Skip(start + 4)).ToList();
        glyphsAfter.Select(g => g.Char).Should().Equal(expected.Select(g => g.Char));
        for (var i = 0; i < expected.Count; i++)
        {
            glyphsAfter[i].X.Should().BeApproximately(expected[i].X, 0.05, $"'{expected[i].Char}' keeps its x");
            glyphsAfter[i].Y.Should().BeApproximately(expected[i].Y, 0.05, $"'{expected[i].Char}' keeps its y");
        }
    }

    private static List<MutoolGlyphPositions.Glyph> Letters(string path)
    {
        var glyphs = MutoolGlyphPositions.ExtractPage(path, 1);
        glyphs.Should().NotBeNull("mutool must read the page");
        return glyphs!.Where(g => !string.IsNullOrWhiteSpace(g.Char)).ToList();
    }

    // Real documents whose text redaction rewrites operands, with a term mutool
    // reads on them. issue14821 is pdf.js's overlapping-runs fixture: "text90"
    // and "text91" share a line and the second run starts inside the "90", so
    // AnyOverlap takes those digits with the term. That is the strategy, not
    // the split; the file still checks that every "text" goes.
    private static readonly (string Rel, string Term, bool RunsOverlap)[] Cases =
    {
        ("test-pdfs/local-real-world/foss-primer.pdf", "Every", false),
        ("test-pdfs/pdfjs/TAMReview.pdf", "University", false),
        ("test-pdfs/pdfjs/issue1350.pdf", "your", false),
        ("test-pdfs/pdfjs/issue14821.pdf", "text", true),
        ("test-pdfs/smoke/irs-w4.pdf", "your", false),
    };

    private static int Alnum(string s) => s.Count(char.IsLetterOrDigit);

    [Fact]
    public void RealDocuments_TheTermGoes_AndNoOtherCharacterDoes()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        // #1706 — the shared locator, so a worktree's .git FILE does not stop
        // the search short of the main checkout, where these corpora live.
        var present = Cases.Where(c => TestRepoLayout.FindFile(c.Rel) != null).ToList();
        Assert.SkipUnless(present.Count > 0, TestRepoLayout.AbsenceReason(
            "fixtures", Cases.Select(c => c.Rel).ToArray()));

        foreach (var (rel, term, runsOverlap) in present)
        {
            var path = TestRepoLayout.FindFile(rel)!;
            var output = Path.Combine(_dir, Path.GetFileName(rel));
            int pageCount;
            using (var doc = PdfDocument.Open(File.ReadAllBytes(path)))
            {
                pageCount = doc.PageCount;
                doc.RedactText(term, RedactionOptions.Default with { DrawBox = false });
                doc.Save(output);
            }

            var before = MutoolTextExtractor.ExtractAllPages(path, pageCount);
            var after = MutoolTextExtractor.ExtractAllPages(output, pageCount);
            before.Should().NotBeNull($"mutool must read {rel}");
            after.Should().NotBeNull($"mutool must read the redacted {rel}");
            var textBefore = string.Join("\n", before!);
            var textAfter = string.Join("\n", after!);
            int Count(string text) => Regex.Matches(text, Regex.Escape(term), RegexOptions.IgnoreCase).Count;
            var occurrences = Count(textBefore);
            _out.WriteLine($"{Path.GetFileName(rel),-22} '{term}': {occurrences} before, {Count(textAfter)} after, " +
                           $"{Alnum(textBefore) - Alnum(textAfter)} letters and digits lost for {term.Length * occurrences} in the term");

            occurrences.Should().BeGreaterThan(0, $"mutool reads '{term}' on {rel}");
            Count(textAfter).Should().Be(0, $"every '{term}' is removed from {rel}");
            SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(output), term).Should().BeEmpty();
            if (!runsOverlap)
                (Alnum(textBefore) - Alnum(textAfter)).Should().Be(term.Length * occurrences,
                    $"only the term's characters leave {rel}: the neighbours in each operand are kept");
        }
    }
}
