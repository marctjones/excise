using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// Independent-oracle verification of page ORGANIZATION — add / insert /
/// remove / move.
///
/// Why this file exists: page manipulation had ~49 tests across four layers
/// (PageCollectionTests, PageOrganizationCommandTests, PageOrganizationWorkflow*
/// and the undo/redo pair) and every single assertion was excise reading back
/// its own output. CLAUDE.md's rule is explicit:
///
///     A tool must not be its own oracle for the property it exists to
///     guarantee.
///
/// A page tree that excise's own parser happens to tolerate — a stale
/// /Count, a Kids entry excise resolves by luck, a content stream still
/// wired to the wrong page dict — would pass the whole suite. Nothing
/// confirmed with an outside reader that a reordered file has the pages
/// excise thinks it has, in the order excise thinks they are in.
///
/// HOW IDENTITY IS ESTABLISHED, without fonts
/// ------------------------------------------
/// Each fixture page carries TWO independent identity signals:
///
///   * its MediaBox width (distinct per page; height is fixed), recovered
///     from the rendered bitmap's aspect ratio — this lives in the page
///     DICTIONARY, and
///   * an ink fraction (a filled rectangle covering a distinct fraction of
///     the page), recovered by counting dark pixels — this lives in the
///     page's CONTENT STREAM, a separate indirect object.
///
/// The assertions check the PAIR against the same expected source page, not
/// the two facts separately. That distinction is the point: checking "all
/// expected widths are present" and "all expected ink fractions are present"
/// independently would also pass a document in which page dicts and content
/// streams had each been permuted correctly but relative to each other were
/// mis-wired — precisely the failure a self-oracle cannot see.
///
/// Ink fractions are deliberately spaced ~0.15 apart rather than forming a
/// tight ramp, so a real ordering bug fails loudly instead of turning into an
/// ambiguous float comparison under antialiasing and DPI rounding.
/// </summary>
public class PageOrganizationDifferentialTests
{
    private const double PageHeightPoints = 400;
    private const int Dpi = 72;

    /// Distinct MediaBox width for source page i — the page-dictionary signal.
    private static double WidthFor(int i) => 200 + 40 * i;

    /// Distinct ink coverage for source page i — the content-stream signal.
    private static double InkFor(int i) => 0.10 + 0.15 * i;

    // ---------------------------------------------------------------- move --

    [Fact]
    public void Move_ReordersPagesAndKeepsContentWithItsPage_PerIndependentRenderer()
    {
        RequireRendererAndCounter();

        var path = BuildFixture(5, out var temp);
        try
        {
            using (var doc = PdfDocument.Open(path))
            {
                // Move(from, to) removes at `from` then inserts at `to` in the
                // shortened list, so 0 -> 3 on [0,1,2,3,4] yields [1,2,3,0,4].
                doc.Pages.Move(0, 3);
                Save(doc, path);
            }

            AssertPageIdentities(path, expectedSourceOrder: new[] { 1, 2, 3, 0, 4 });
        }
        finally { Cleanup(temp); }
    }

    // -------------------------------------------------------------- remove --

    [Fact]
    public void RemoveAt_DropsTheRequestedPageAndNoOther_PerIndependentRenderer()
    {
        RequireRendererAndCounter();

        var path = BuildFixture(5, out var temp);
        try
        {
            using (var doc = PdfDocument.Open(path))
            {
                doc.Pages.RemoveAt(2);
                Save(doc, path);
            }

            AssertPageIdentities(path, expectedSourceOrder: new[] { 0, 1, 3, 4 });
        }
        finally { Cleanup(temp); }
    }

    // -------------------------------------------------------------- insert --

    [Fact]
    public void Insert_PlacesTheNewPageAtTheRequestedIndex_PerIndependentRenderer()
    {
        RequireRendererAndCounter();

        var path = BuildFixture(3, out var temp);
        try
        {
            // Source page for the insert: signature 5, distinct from anything
            // already in the fixture (0..2) and still inside the ink-signature
            // range (InkFor(5) = 0.85; see the guard in BuildFixture).
            var donorPath = BuildFixture(1, out var donorTemp, startIndex: 5);
            try
            {
                using (var doc = PdfDocument.Open(path))
                using (var donor = PdfDocument.Open(donorPath))
                {
                    doc.Pages.Insert(1, donor.Pages[0]);
                    Save(doc, path);
                }

                AssertPageIdentities(path, expectedSourceOrder: new[] { 0, 5, 1, 2 });
            }
            finally { Cleanup(donorTemp); }
        }
        finally { Cleanup(temp); }
    }

    // ------------------------------------------------------------ addblank --

    [Fact]
    public void AddBlank_AppendsAPageOfTheRequestedSize_PerIndependentRenderer()
    {
        RequireRendererAndCounter();

        var path = BuildFixture(2, out var temp);
        try
        {
            using (var doc = PdfDocument.Open(path))
            {
                // No content stream: this page's identity is its size alone.
                doc.Pages.AddBlank(WidthFor(9), PageHeightPoints);
                Save(doc, path);
            }

            QpdfReferenceTool.PageCount(path).Should().Be(3,
                "AddBlank must add exactly one page an outside parser can see");

            var pages = RenderAll(path, 3);

            AssertIdentity(pages[0], 0, position: 0);
            AssertIdentity(pages[1], 1, position: 1);

            AspectOf(pages[2]).Should().BeApproximately(AspectFor(9), 0.02,
                "the appended page must carry the size AddBlank was asked for");
            InkFraction(pages[2]).Should().BeLessThan(0.01,
                "a blank page must contain no ink");
        }
        finally { Cleanup(temp); }
    }

    // ------------------------------------------------- structural validity --

    [Fact]
    public void PageMutations_LeaveAStructurallyValidPdf_PerQpdf()
    {
        // Gated on qpdf ALONE: this test calls no renderer, so gating it on
        // mutool too would make the [requires: ...] allowlist marker disagree
        // with the real guard.
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");

        var path = BuildFixture(5, out var temp);
        try
        {
            using (var doc = PdfDocument.Open(path))
            {
                doc.Pages.Move(0, 3);
                doc.Pages.RemoveAt(1);
                doc.Pages.AddBlank();
                Save(doc, path);
            }

            var check = QpdfReferenceTool.Check(path);
            check.Should().NotBeNull("qpdf reported IsAvailable but produced no result");
            check!.Value.Success.Should().BeTrue(
                "an independent tool must consider the page tree valid after move/remove/add. " +
                "excise's own parser tolerating its output proves only that its bugs are " +
                $"self-consistent. qpdf said:\n{check.Value.Output}");
        }
        finally { Cleanup(temp); }
    }

    // ----------------------------------------------------------- internals --

    /// <summary>
    /// Builds a PDF whose page i has MediaBox width <see cref="WidthFor"/>(i)
    /// and ink coverage <see cref="InkFor"/>(i). startIndex shifts the
    /// signature space so a donor page cannot collide with the main fixture.
    /// </summary>
    private static string BuildFixture(int pageCount, out string tempDir, int startIndex = 0)
    {
        tempDir = Path.Combine(Path.GetTempPath(), "excise-pageorg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var path = Path.Combine(tempDir, "fixture.pdf");

        using var doc = PdfDocument.CreateNew();
        for (int i = 0; i < pageCount; i++)
        {
            int sig = startIndex + i;
            // InkFor(sig) is a FRACTION of the page. Past ~0.95 the rectangle
            // covers the whole page, every such page measures 1.0, and the
            // content-stream signal silently stops distinguishing anything —
            // which looks exactly like a mis-wiring failure. Fail loudly here
            // instead (the first draft of this file used sig=7 => 1.15 and lost
            // an hour to a "bug" that was only a bad fixture).
            if (InkFor(sig) > 0.95)
                throw new ArgumentOutOfRangeException(nameof(startIndex),
                    $"ink signature for page {sig} is {InkFor(sig):0.##}; must stay <= 0.95 to remain distinguishable");
            var page = doc.Pages.AddBlank(WidthFor(sig), PageHeightPoints);
            // Filled rectangle across the full width, covering InkFor(sig) of
            // the height — so the painted fraction of the page equals InkFor(sig).
            var content = $"0 0 0 rg 0 0 {WidthFor(sig):0.###} {PageHeightPoints * InkFor(sig):0.###} re f";
            page.SetContentStreamBytes(Encoding.ASCII.GetBytes(content));
        }
        Save(doc, path);
        return path;
    }

    private static void Save(PdfDocument doc, string path)
    {
        var bytes = doc.SaveToBytes();
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>
    /// Identity comes from mutool (render) and the page COUNT from qpdf
    /// (--show-npages), so these tests need both. Kept as one helper so the
    /// guard and the allowlist's [requires: tool:mutool tool:qpdf] marker
    /// cannot drift apart.
    /// </summary>
    private static void RequireRendererAndCounter()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");
    }

    private static List<SKBitmap> RenderAll(string path, int expectedPageCount)
    {
        var bitmaps = new List<SKBitmap>();
        for (int p = 1; p <= expectedPageCount; p++)
        {
            var bmp = MutoolReferenceRenderer.RenderPage(path, p, Dpi);
            if (bmp == null) break;
            bitmaps.Add(bmp);
        }
        return bitmaps;
    }

    /// <summary>
    /// Asserts that, per mutool, the saved document's pages are exactly
    /// expectedSourceOrder — checking each page's dictionary signal (size) and
    /// content-stream signal (ink) against the SAME source index.
    /// </summary>
    private static void AssertPageIdentities(string path, int[] expectedSourceOrder)
    {
        // Page count from qpdf, NOT from rendering: mutool draw clamps an
        // out-of-range page to the last page and exits 0, so "render until it
        // fails" over-counts every time (this test file's first draft did
        // exactly that and reported 6 pages for a 5-page document).
        QpdfReferenceTool.PageCount(path).Should().Be(expectedSourceOrder.Length,
            "an independent parser must see exactly the pages excise says it wrote");

        var pages = RenderAll(path, expectedSourceOrder.Length);

        for (int position = 0; position < expectedSourceOrder.Length; position++)
            AssertIdentity(pages[position], expectedSourceOrder[position], position);
    }

    private static void AssertIdentity(SKBitmap bmp, int expectedSource, int position)
    {
        AspectOf(bmp).Should().BeApproximately(AspectFor(expectedSource), 0.02,
            $"page at position {position} should be source page {expectedSource} " +
            "(page-dictionary signal: MediaBox width)");

        InkFraction(bmp).Should().BeApproximately(InkFor(expectedSource), 0.03,
            $"page at position {position} should still carry source page {expectedSource}'s " +
            "content stream (content-stream signal: ink coverage). A mismatch here with a " +
            "matching size means the page dict and its content stream were mis-wired.");
    }

    private static double AspectFor(int sourceIndex) => WidthFor(sourceIndex) / PageHeightPoints;

    private static double AspectOf(SKBitmap bmp) => (double)bmp.Width / bmp.Height;

    private static double InkFraction(SKBitmap bmp)
    {
        long dark = 0;
        for (int y = 0; y < bmp.Height; y++)
        for (int x = 0; x < bmp.Width; x++)
        {
            var c = bmp.GetPixel(x, y);
            if (c.Alpha > 8 && (c.Red + c.Green + c.Blue) / 3 < 128) dark++;
        }
        return (double)dark / (bmp.Width * bmp.Height);
    }

    private static void Cleanup(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (IOException) { /* best effort */ }
    }
}
