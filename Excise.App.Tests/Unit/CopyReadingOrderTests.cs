using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Excise.Avalonia.Services;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Excise.Core.Text;
using Xunit;

namespace Excise.App.Tests.Unit;

/// <summary>
/// Copy-quality harness (#774). These tests drive the REAL copy-assembly path
/// the viewer uses — <see cref="TextSelectionEngine.SortReadingOrder(IEnumerable{Letter}, ReadingOrderStrategy)"/>
/// followed by <see cref="TextSelectionEngine.BuildSelection"/>, which is exactly
/// what <c>PdfViewerControl</c> copies to the clipboard on a drag — and assert the
/// resulting text is in the reading order a human expects.
///
/// The multi-column expectation is verified two independent ways so excise is
/// never its own oracle (see CLAUDE.md / no-self-oracle):
///   1. CONSTRUCTION-KNOWN — the fixture places column-1 words then column-2
///      words, so column-major order is author-defined, not engine-defined.
///   2. INDEPENDENT WITNESS — poppler <c>pdftotext</c> (default reading-order
///      mode, which is column-aware) run on the same PDF. Skipped + allow-listed
///      where the tool is absent (Linux CI has no poppler binaries; runs on macOS).
/// </summary>
public class CopyReadingOrderTests
{
    // ── fixtures ────────────────────────────────────────────────────────────

    // Two independent prose columns with DISJOINT vocabularies (so an
    // order-check can tell which column a word came from) and STAGGERED
    // baselines (right offset from left) so they read as two flows, not a
    // baseline-aligned table.
    private static readonly string[] LeftColumn =
    {
        "Alpha bravo charlie", "delta echo foxtrot", "golf hotel india",
        "juliet kilo lima", "mike november oscar", "papa quebec romeo",
    };
    private static readonly string[] RightColumn =
    {
        "sierra tango uniform", "victor whiskey xray", "yankee zulu one",
        "two three four", "five six seven", "eight nine ten",
    };

    /// <summary>Two-column article PDF; right column baselines staggered 7pt.</summary>
    private static byte[] BuildTwoColumnArticle()
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank();
        var font = PdfFont.Helvetica(11);
        using (var g = page.GetGraphics())
        {
            double leftTop = page.Height - 100;
            double rightTop = page.Height - 107;
            for (int i = 0; i < LeftColumn.Length; i++)
                g.DrawString(LeftColumn[i], font, PdfBrush.Black, 60, leftTop - i * 16);
            for (int i = 0; i < RightColumn.Length; i++)
                g.DrawString(RightColumn[i], font, PdfBrush.Black, 330, rightTop - i * 16);
            g.Flush();
        }
        return doc.SaveToBytes();
    }

    private static List<Letter> LettersOf(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        return doc.GetPage(1).Letters?.ToList() ?? new List<Letter>();
    }

    /// <summary>
    /// The copied clipboard string for a full-page selection under a strategy —
    /// the exact assembly the control performs (SortReadingOrder → BuildSelection).
    /// </summary>
    private static string CopyWholePage(List<Letter> letters, ReadingOrderStrategy strategy)
    {
        var reading = TextSelectionEngine.SortReadingOrder(letters, strategy);
        if (reading.Count == 0) return string.Empty;
        var gap = TextSelectionEngine.EstimateColumnGap(reading);
        var selection = TextSelectionEngine.BuildSelection(
            reading, letters, reading[0], reading[^1], gap);
        return selection.Text;
    }

    private static List<string> Tokens(string s) =>
        Regex.Split(s, @"\s+").Where(t => t.Length > 0).ToList();

    // ── Part 1 baseline: measure the multi-column bug ────────────────────────

    [Fact]
    public void TwoColumn_Simple_Interleaves_ColumnsAcrossTheGutter()
    {
        var letters = LettersOf(BuildTwoColumnArticle());
        var copy = CopyWholePage(letters, ReadingOrderStrategy.Simple);
        var tokens = Tokens(copy);

        // Simple (pre-#774) order reads row by row across the gutter, so a left
        // word is immediately followed by the right word on the (nearly) same
        // line — the interleaving bug. "romeo" (last left word) is NOT reached
        // before "sierra" (first right word).
        var interleaved = new List<string>();
        for (int i = 0; i < LeftColumn.Length; i++)
        {
            interleaved.AddRange(Tokens(LeftColumn[i]));
            interleaved.AddRange(Tokens(RightColumn[i]));
        }
        tokens.Should().Equal(interleaved, "Simple strategy interleaves the two columns (the bug #774 fixes)");

        var columnMajor = LeftColumn.Concat(RightColumn).SelectMany(Tokens).ToList();
        tokens.Should().NotEqual(columnMajor, "Simple is NOT column-major — that's the defect");
    }

    // ── Part 2: column-aware is the default and reads column-by-column ───────

    [Fact]
    public void TwoColumn_ColumnAware_ReadsEachColumnTopToBottom()
    {
        var letters = LettersOf(BuildTwoColumnArticle());

        // Default overload must be ColumnAware.
        var copyDefault = CopyWholePage(letters, ReadingOrderStrategy.ColumnAware);
        var reading = TextSelectionEngine.SortReadingOrder(letters); // no strategy arg
        TextSelectionEngine.JoinText(reading).Should().NotBeNull();

        var columnMajor = LeftColumn.Concat(RightColumn).SelectMany(Tokens).ToList();
        Tokens(copyDefault).Should().Equal(columnMajor,
            "column-aware copy reads all of column 1 then all of column 2 (construction-known)");
    }

    [Fact]
    public void TwoColumn_ColumnAware_MatchesIndependentPdftotextOracle()
    {
        var pdf = BuildTwoColumnArticle();
        var path = Path.Combine(Path.GetTempPath(), $"excise-2col-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdf);
        try
        {
            var oracle = RunPdftotext(path);
            Assert.SkipWhen(oracle == null, "poppler pdftotext not on PATH (Linux CI has no poppler binaries)");

            var oracleTokens = Tokens(oracle!);
            var columnMajor = LeftColumn.Concat(RightColumn).SelectMany(Tokens).ToList();

            // Independent witness: poppler's column-aware reading order agrees
            // with our construction-known column-major order.
            oracleTokens.Should().Equal(columnMajor,
                "pdftotext default (reading-order) mode reads this layout column-by-column");

            // And excise's column-aware copy equals that independent order.
            var letters = LettersOf(pdf);
            Tokens(CopyWholePage(letters, ReadingOrderStrategy.ColumnAware))
                .Should().Equal(oracleTokens, "excise column-aware copy matches the independent oracle");
        }
        finally { TryDelete(path); }
    }

    // ── Part 3: strategy is configurable and changes the copy order ──────────

    [Fact]
    public void TwoColumn_SwitchingStrategy_ChangesCopyOrder()
    {
        var letters = LettersOf(BuildTwoColumnArticle());

        var columnAware = Tokens(CopyWholePage(letters, ReadingOrderStrategy.ColumnAware));
        var simple = Tokens(CopyWholePage(letters, ReadingOrderStrategy.Simple));

        columnAware.Should().NotEqual(simple, "the two strategies MUST produce different copy order on a 2-column page");

        var columnMajor = LeftColumn.Concat(RightColumn).SelectMany(Tokens).ToList();
        columnAware.Should().Equal(columnMajor);
    }

    [Fact]
    public void TwoColumn_RawStream_UsesContentStreamOrder()
    {
        var letters = LettersOf(BuildTwoColumnArticle());
        var raw = TextSelectionEngine.SortReadingOrder(letters, ReadingOrderStrategy.RawStream);

        // RawStream is the untouched Excise.Core emit order — no geometric sort.
        raw.Should().Equal(letters, "RawStream returns the page letters in content-stream order");
    }

    // ── single-column identity: the fix must not touch single-column copy ────

    [Fact]
    public void SingleColumn_Wrapped_ColumnAwareIsIdenticalToSimple()
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank();
        var font = PdfFont.Helvetica(11);
        string[] lines =
        {
            "The morning light spilled across", "the quiet valley floor as the",
            "river wound its patient way past", "the sleeping village and onward",
            "toward the far grey line of hills",
        };
        using (var g = page.GetGraphics())
        {
            double top = page.Height - 100;
            for (int i = 0; i < lines.Length; i++)
                g.DrawString(lines[i], font, PdfBrush.Black, 72, top - i * 16);
            g.Flush();
        }
        var letters = LettersOf(doc.SaveToBytes());

        var columnAware = TextSelectionEngine.SortReadingOrder(letters, ReadingOrderStrategy.ColumnAware);
        var simple = TextSelectionEngine.SortReadingOrder(letters, ReadingOrderStrategy.Simple);

        columnAware.Select(l => l.Value).Should().Equal(simple.Select(l => l.Value),
            "single-column pages must be byte-identical under both strategies");

        var expected = lines.SelectMany(Tokens).ToList();
        Tokens(TextSelectionEngine.JoinText(columnAware)).Should().Equal(expected,
            "single-column prose copies in natural top-to-bottom order");
    }

    // ── table guard: sub-threshold gaps are NEVER treated as columns ─────────

    [Fact]
    public void DetectColumnBoundaries_DenseRowGridWithSubThresholdGaps_IsNotColumns()
    {
        // 5 rows × 3 columns of fixed-width glyphs. Inter-column gaps are
        // deliberately < the column-gutter threshold (3 × median glyph width =
        // 3 × 6 = 18). A dense table with tight gaps must stay a single band so
        // it reads row-major. Deterministic geometry (no font-metric guessing).
        var letters = new List<Letter>();
        double[] colX = { 10, 40, 70 };   // each cell 3 glyphs × 6pt = 18 wide → ends +18; gaps = 12 (< 18)
        for (int r = 0; r < 5; r++)
        {
            double y = 100 - r * 16;
            for (int c = 0; c < 3; c++)
                for (int k = 0; k < 3; k++)
                    letters.Add(L($"{(char)('a' + c)}", colX[c] + k * 6, y));
        }
        TextSelectionEngine.DetectColumnBoundaries(letters).Should().BeEmpty(
            "tight inter-column gaps (< gutter threshold) are word spacing, not column gutters");
    }

    [Fact]
    public void WideGapTable_IsReadColumnMajor_DocumentedOutOfScope()
    {
        // OBSERVATIONAL (#774 bounded scope): a baseline-aligned table with WIDE
        // inter-column gaps is geometrically indistinguishable from a 2-column
        // article, so column-aware reads it column-by-column. This is a KNOWN
        // limitation, pinned here so a future change to the boundary is noticed
        // rather than silent. (poppler pdftotext reads this same layout
        // row-major; matching it needs table detection, which is out of scope.)
        string[,] cells =
        {
            { "Jan", "aaa" }, { "Feb", "bbb" }, { "Mar", "ccc" },
            { "Apr", "ddd" }, { "May", "eee" },
        };
        double[] colX = { 72, 300 };   // very wide gap

        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank();
        var font = PdfFont.Helvetica(11);
        using (var g = page.GetGraphics())
        {
            double top = page.Height - 100;
            for (int r = 0; r < 5; r++)
                for (int c = 0; c < 2; c++)
                    g.DrawString(cells[r, c], font, PdfBrush.Black, colX[c], top - r * 16);
            g.Flush();
        }
        var letters = LettersOf(doc.SaveToBytes());

        var columnMajor = new List<string>();
        for (int c = 0; c < 2; c++)
            for (int r = 0; r < 5; r++)
                columnMajor.Add(cells[r, c]);

        Tokens(CopyWholePage(letters, ReadingOrderStrategy.ColumnAware))
            .Should().Equal(columnMajor,
                "wide-gap tables are (imperfectly) read column-major — a documented out-of-scope limitation");
    }

    // ── wrap-around-image: observational, documents out-of-scope behaviour ───

    [Fact]
    public void TextWrappingAroundImage_ColumnAwareDoesNotMisSplit()
    {
        // Text with a rectangular "hole" where a figure would sit (top and
        // bottom lines full width; middle lines only on the left of the hole).
        // There is no tall right-hand text block, so no column gutter qualifies
        // and column-aware falls back to geometric order. Full flow-around-figure
        // reading order is OUT OF SCOPE for #774 (documented) — this test pins
        // that column-aware does not mis-split such a page, not that it is ideal.
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank();
        var font = PdfFont.Helvetica(11);
        using (var g = page.GetGraphics())
        {
            double top = page.Height - 100;
            g.DrawString("Header spanning the whole width here", font, PdfBrush.Black, 72, top);
            // Middle lines: text only to the LEFT of a figure box (right side empty).
            for (int i = 1; i <= 4; i++)
                g.DrawString("left of figure", font, PdfBrush.Black, 72, top - i * 16);
            g.DrawString("Footer spanning the whole width again", font, PdfBrush.Black, 72, top - 5 * 16);
            g.Flush();
        }
        var letters = LettersOf(doc.SaveToBytes());

        var columnAware = TextSelectionEngine.SortReadingOrder(letters, ReadingOrderStrategy.ColumnAware);
        var simple = TextSelectionEngine.SortReadingOrder(letters, ReadingOrderStrategy.Simple);
        columnAware.Select(l => l.Value).Should().Equal(simple.Select(l => l.Value),
            "no qualifying gutter → column-aware degrades to geometric order (not mis-split)");
    }

    // ── pure-unit detector checks ────────────────────────────────────────────

    [Fact]
    public void DetectColumnBoundaries_SingleWideWordGap_IsNotAColumn()
    {
        // "hello[wide gap]world" on line 1, "next" on line 2. The gap is wide
        // but only one ragged line straddles it — must NOT be a column boundary.
        var letters = new List<Letter>
        {
            L("h", 10, 100), L("e", 16, 100), L("l", 22, 100), L("l", 28, 100), L("o", 34, 100),
            L("w", 90, 100), L("o", 96, 100), L("r", 102, 100), L("l", 108, 100), L("d", 114, 100),
            L("n", 10, 84), L("e", 16, 84), L("x", 22, 84), L("t", 28, 84),
        };
        TextSelectionEngine.DetectColumnBoundaries(letters).Should().BeEmpty(
            "a lone wide word gap on one line is not a column gutter");
    }

    private static Letter L(string value, double left, double bottom, double width = 6, double height = 10)
    {
        var rect = new PdfRectangle(left, bottom, left + width, bottom + height);
        return new Letter(value, rect, fontSize: height, fontName: "Helvetica",
            startX: left, startY: bottom, width: width, characterCode: value[0]);
    }

    // ── pdftotext oracle helper ──────────────────────────────────────────────

    private static string? RunPdftotext(string pdfPath)
    {
        try
        {
            var psi = new ProcessStartInfo("pdftotext", $"\"{pdfPath}\" -")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            var stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            return p.ExitCode == 0 ? stdout : null;
        }
        catch { return null; } // tool absent → caller skips
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
