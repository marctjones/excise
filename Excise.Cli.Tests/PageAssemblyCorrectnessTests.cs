using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Excise.Core.Operations;
using Excise.Rendering.Differential;
using Xunit;

namespace Excise.Cli.Tests;

/// <summary>
/// Correctness of the CLI's page-assembly commands, verified by tools that are
/// not excise.
///
/// Two gaps this closes:
///
///  1. `merge` and `split` had NO correctness tests at the CLI layer at all.
///     The only two tests touching Program.RunMerge/RunSplit were
///     PermissionEnforcementTests' #677 pair, which assert that /P bit 11
///     blocks the operation — nothing asserted that a merge actually
///     concatenates, or that a split actually divides. The underlying
///     PdfDocumentMerger/PdfDocumentSplitter have 13 tests between them in
///     Excise.Core.Tests, but nothing covered the CLI wiring that reaches them.
///
///  2. None of those 13 use an independent oracle — Excise.Core.Tests only
///     references Excise.Core, so it structurally cannot. Every assertion was
///     excise reading back its own output, which per CLAUDE.md proves only that
///     its bugs are self-consistent.
///
/// Excise.Cli.Tests reaches Excise.Rendering.Differential transitively (via
/// Excise.RenderTools), so this is the natural home for both.
///
/// Page COUNT comes from qpdf --show-npages and page CONTENT from mutool's text
/// extractor. Note that page count deliberately does NOT come from "render
/// pages until one fails": mutool draw clamps an out-of-range page to the last
/// page and exits 0, so that approach silently over-counts.
/// </summary>
public class PageAssemblyCorrectnessTests : IDisposable
{
    private readonly List<string> _temps = new();

    private string TempPath(string suffix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-assembly-{Guid.NewGuid():N}{suffix}");
        _temps.Add(path);
        return path;
    }

    private string TempDir()
    {
        var dir = TempPath("");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// A single-page PDF whose only text is <paramref name="text"/>.
    private string PageWithText(string text)
    {
        var path = TempPath(".pdf");
        File.WriteAllBytes(path, TestPdfBuilder.SinglePage(text));
        return path;
    }

    private static void RequireOracles()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
    }

    // ----------------------------------------------------------------- merge --

    [Fact]
    public void RunMerge_ConcatenatesEveryInputPageInOrder_PerIndependentOracle()
    {
        RequireOracles();

        var a = PageWithText("ALPHAPAGE");
        var b = PageWithText("BRAVOPAGE");
        var c = PageWithText("CHARLIEPAGE");
        var output = TempPath(".pdf");

        Program.RunMerge([a, b, c], output);

        QpdfReferenceTool.PageCount(output).Should().Be(3,
            "merging three single-page inputs must yield three pages according to an " +
            "independent parser, not just to excise's own reopen");

        var expected = new[] { "ALPHAPAGE", "BRAVOPAGE", "CHARLIEPAGE" };
        for (int i = 0; i < expected.Length; i++)
        {
            var text = MutoolTextExtractor.ExtractPage(output, i + 1) ?? string.Empty;
            Strip(text).Should().Contain(expected[i],
                $"page {i + 1} of the merged document must carry input {i + 1}'s content. " +
                "Order matters: a merge that produces the right page COUNT with the pages " +
                "permuted is still wrong, and only a per-page content check catches it.");
        }
    }

    [Fact]
    public void RunMerge_ProducesAStructurallyValidPdf_PerQpdf()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");

        var output = TempPath(".pdf");
        Program.RunMerge([PageWithText("ONE"), PageWithText("TWO")], output);

        var check = QpdfReferenceTool.Check(output);
        check.Should().NotBeNull("qpdf reported IsAvailable but produced no result");
        check!.Value.Success.Should().BeTrue(
            $"a merged document must satisfy an independent structural check. qpdf said:\n{check.Value.Output}");
    }

    // ----------------------------------------------------------------- split --

    [Fact]
    public void RunSplit_ToSinglePages_BurstsOnePdfPerPageWithTheRightContent_PerIndependentOracle()
    {
        RequireOracles();

        // Build the multi-page input through RunMerge. That couples this test to
        // merge, which is deliberate and safe: merge is verified independently
        // by RunMerge_ConcatenatesEveryInputPageInOrder_PerIndependentOracle
        // above, so a merge regression shows up there first rather than
        // silently corrupting this test's premise.
        var merged = TempPath(".pdf");
        Program.RunMerge([PageWithText("SPLITONE"), PageWithText("SPLITTWO"), PageWithText("SPLITTHREE")], merged);

        var outDir = TempDir();
        var written = Program.RunSplit(merged, outDir, PdfDocumentSplitter.SplitToSinglePages);

        written.Should().HaveCount(3, "a single-page burst of a 3-page document must write 3 files");

        var expected = new[] { "SPLITONE", "SPLITTWO", "SPLITTHREE" };
        for (int i = 0; i < written.Count; i++)
        {
            File.Exists(written[i]).Should().BeTrue($"fragment {i + 1} should exist on disk");

            QpdfReferenceTool.PageCount(written[i]).Should().Be(1,
                $"fragment {i + 1} of a single-page burst must contain exactly one page " +
                "according to an independent parser");

            var text = MutoolTextExtractor.ExtractPage(written[i], 1) ?? string.Empty;
            Strip(text).Should().Contain(expected[i],
                $"fragment {i + 1} must carry source page {i + 1}'s content — a burst that " +
                "produces the right number of one-page files with the wrong content in them " +
                "is the failure a page-count-only assertion misses");
        }
    }

    [Fact]
    public void RunSplit_Fragments_AreStructurallyValid_PerQpdf()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");

        var merged = TempPath(".pdf");
        Program.RunMerge([PageWithText("FRAGONE"), PageWithText("FRAGTWO")], merged);

        var outDir = TempDir();
        var written = Program.RunSplit(merged, outDir, PdfDocumentSplitter.SplitToSinglePages);

        foreach (var fragment in written)
        {
            var check = QpdfReferenceTool.Check(fragment);
            check.Should().NotBeNull();
            check!.Value.Success.Should().BeTrue(
                $"split fragment {Path.GetFileName(fragment)} must satisfy an independent " +
                $"structural check. qpdf said:\n{check.Value.Output}");
        }
    }

    // ------------------------------------------------------------- internals --

    /// mutool's text output carries layout whitespace and newlines; the fixture
    /// words are single tokens, so compare against a whitespace-stripped form.
    private static string Strip(string text) =>
        new string(text.Where(ch => !char.IsWhiteSpace(ch)).ToArray());

    public void Dispose()
    {
        foreach (var t in _temps)
        {
            try
            {
                if (Directory.Exists(t)) Directory.Delete(t, recursive: true);
                else if (File.Exists(t)) File.Delete(t);
            }
            catch (IOException) { /* best effort */ }
        }
    }
}
