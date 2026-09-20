using System.Collections.Generic;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1091 — proves the operand TJ-split IMPROVED redaction, not just that it did
/// not regress. Two things a ratchet cannot show and a leak scanner cannot show:
/// (1) the split path actually FIRES on real content (not a blamed path that
/// never runs), and (2) it destroys NO MORE untargeted text than the
/// reconstruction it replaced — measured as a DELTA against the old path via the
/// GlyphRemover.DisableOperandSplit A/B toggle, graded by the INDEPENDENT
/// extractor (mutool) on both sides.
/// </summary>
public class OperandSplitImprovementTests
{
    private readonly Xunit.ITestOutputHelper _out;
    public OperandSplitImprovementTests(Xunit.ITestOutputHelper o) => _out = o;

    // Real fixtures whose text redaction rewrites content (not just carriers),
    // with a term the oracle sees on them.
    private static readonly (string Rel, string Term)[] Cases =
    {
        ("test-pdfs/pdfjs/foss-primer.pdf", "Every"),
        ("test-pdfs/pdfjs/TAMReview.pdf", "University"),
        ("test-pdfs/pdfjs/issue1350.pdf", "your"),
        ("test-pdfs/pdfjs/issue14821.pdf", "text"),
        ("test-pdfs/smoke/irs-w4.pdf", "your"),
    };

    private static int Alnum(string s) => s.Count(char.IsLetterOrDigit);

    private static (int Collateral, long Splits) RedactAndMeasure(string path, string term, bool disableSplit)
    {
        OperandGlyphSplitter.ResetCounters();
        GlyphRemover.DisableOperandSplit = disableSplit;
        try
        {
            int pageCount;
            using (var probe = PdfDocument.Open(File.ReadAllBytes(path))) pageCount = probe.PageCount;
            var beforePages = MutoolTextExtractor.ExtractAllPages(path, pageCount);
            var before = beforePages == null ? "" : string.Join("\n", beforePages);

            var outPath = Path.Combine(Path.GetTempPath(), $"split-ab-{Guid.NewGuid():N}.pdf");
            using (var doc = PdfDocument.Open(File.ReadAllBytes(path)))
            {
                doc.RedactText(term, drawBlackRect: false);
                doc.Save(outPath);
            }
            try
            {
                var afterPages = MutoolTextExtractor.ExtractAllPages(outPath, pageCount);
                var after = afterPages == null ? "" : string.Join("\n", afterPages);
                var termCost = term.Length * System.Text.RegularExpressions.Regex.Matches(before, System.Text.RegularExpressions.Regex.Escape(term)).Count;
                var collateral = Math.Max(0, Alnum(before) - Alnum(after) - termCost);
                return (collateral, OperandGlyphSplitter.Splits);
            }
            finally { try { File.Delete(outPath); } catch { } }
        }
        finally { GlyphRemover.DisableOperandSplit = false; }
    }

    [Fact]
    public void OperandSplit_Fires_AndDestroysNoMoreThanReconstruction()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        // #1706 — the shared locator, so a worktree's .git FILE does not stop
        // the search short of the main checkout, where these corpora live.
        var present = Cases.Where(c => TestRepoLayout.FindFile(c.Rel) != null).ToList();
        Assert.SkipUnless(present.Count > 0, TestRepoLayout.AbsenceReason(
            "fixtures", Cases.Select(c => c.Rel).ToArray()));

        long totalSplits = 0;
        int worseCases = 0, improvedCases = 0;
        var lines = new List<string>();

        foreach (var (rel, term) in present)
        {
            var path = TestRepoLayout.FindFile(rel)!;
            var on = RedactAndMeasure(path, term, disableSplit: false);
            var off = RedactAndMeasure(path, term, disableSplit: true);
            totalSplits += on.Splits;
            if (on.Collateral > off.Collateral) worseCases++;
            if (on.Collateral < off.Collateral) improvedCases++;
            lines.Add($"{Path.GetFileName(rel),-22} '{term}': split fired {on.Splits}x  " +
                      $"collateral split-on {on.Collateral} vs reconstruct {off.Collateral}");
        }

        foreach (var l in lines) _out.WriteLine(l);
        _out.WriteLine($"TOTAL splits fired={totalSplits}, improved={improvedCases}, worse={worseCases}");

        // (1) The primary path actually runs on real content.
        totalSplits.Should().BeGreaterThan(0,
            "the operand split must FIRE on real documents, not silently fall back to reconstruction");

        // (2) It never destroys MORE untargeted text than the reconstruction it
        // replaced — the whole reason it is the primary path (#1038's collateral
        // lived in reconstruction). Graded by mutool on both sides.
        worseCases.Should().Be(0,
            "the split must not increase collateral over the reconstruction path on any case");
    }
}
