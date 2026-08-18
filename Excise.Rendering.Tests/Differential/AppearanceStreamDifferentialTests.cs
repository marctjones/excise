using System;
using System.Collections.Generic;
using System.Globalization;
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
/// GROUP A (#1053): an annotation that CARRIES an <c>/AP</c>. §12.5.5 says the
/// appearance stream <b>shall</b> be used — it is an ordinary content stream, so
/// every conforming renderer executes the same operators and any disagreement
/// is a real defect in somebody's code, not a matter of taste.
///
/// <para><b>This is the case nothing measured.</b> All 45 rows of
/// <c>tests/annotation-synthesis-policy.json</c> are <c>/AP</c>-ABSENT — the
/// case where the spec declines to specify a look and pixel agreement is
/// therefore the wrong instrument. The annotation work had studied the
/// undetermined case in detail and never measured the determined one.</para>
///
/// <para>What this asks is narrow and answerable: <b>does excise put the
/// appearance in the same place, at the same size, as the independent
/// renderers?</b> The fixtures exercise §12.5.5's appearance-to-<c>/Rect</c>
/// mapping — the algorithm that transforms <c>/BBox</c> by <c>/Matrix</c>,
/// takes the bounding box of the result, and fits that to <c>/Rect</c>. Every
/// step of it is a place to be wrong, and being wrong there misplaces or
/// mis-scales every <c>/AP</c> annotation in every document.</para>
///
/// <para>Comparison is by INKED TILE, not by pixel: excise rasterises through
/// Skia by choice and Skia's scan conversion differs from Cairo's and MuPDF's.
/// Antialiasing must not register as a defect, while a wrong position, a
/// missing scale or a dropped appearance must. A 10pt tile is far coarser than
/// any AA fringe and far finer than any of those errors.</para>
///
/// <para>Distinct from <c>AnnotationAppearanceDrawnTests</c> (#888), which pins
/// three specific past defects — indirect <c>/BBox</c>, absent <c>/BBox</c>, an
/// unbalanced CTM in page content. That file answers "did we re-break these
/// three?"; this one answers "is the mapping right in general?".</para>
/// </summary>
public class AppearanceStreamDifferentialTests
{
    private const int Dpi = 72;        // 1 PDF point == 1 pixel
    private const int PageSize = 200;
    private const int Tile = 10;       // px

    /// <summary>
    /// An <c>/AP</c> case. <paramref name="ApContent"/> always paints a filled
    /// rectangle over the LEFT HALF of its own <c>/BBox</c> — asymmetric on
    /// purpose, so that a mapping which loses the translation, drops the scale
    /// or flips an axis moves the ink somewhere a symmetric shape would hide.
    /// </summary>
    private sealed record ApCase(
        string Id, string Why, string BBox, string? Matrix, string Rect, string ApContent);

    public static TheoryData<string> Cases()
    {
        var d = new TheoryData<string>();
        foreach (var c in AllCases) d.Add(c.Id);
        return d;
    }

    private static readonly ApCase[] AllCases =
    {
        new("bbox-equals-rect",
            "the baseline: no scaling or translation needed, so any failure here is not about the mapping",
            "[0 0 100 100]", null, "[50 50 150 150]",
            "1 0 0 rg 0 0 50 100 re f"),

        new("bbox-smaller-than-rect",
            "/BBox is 10x10 and /Rect is 100x100 — the appearance must be scaled x10. " +
            "Skipping the scale leaves a tenth of the ink in a corner.",
            "[0 0 10 10]", null, "[50 50 150 150]",
            "1 0 0 rg 0 0 5 10 re f"),

        new("bbox-offset-from-origin",
            "/BBox does not start at the origin, so the mapping must TRANSLATE. " +
            "Ignoring the offset puts the ink outside /Rect entirely.",
            "[100 100 200 200]", null, "[50 50 150 150]",
            "1 0 0 rg 100 100 50 100 re f"),

        new("non-square-rect",
            "square /BBox into a 2:1 /Rect. §12.5.5 fits the box to /Rect, so the " +
            "appearance STRETCHES; preserving aspect ratio is the wrong answer here.",
            "[0 0 100 100]", null, "[20 80 180 130]",
            "1 0 0 rg 0 0 50 100 re f"),

        new("matrix-scale",
            "/Matrix scales the appearance space. The algorithm maps the TRANSFORMED " +
            "box to /Rect, so the result must be identical to the baseline — a renderer " +
            "that applies /Matrix without re-fitting draws it twice as large.",
            "[0 0 50 50]", "[2 0 0 2 0 0]", "[50 50 150 150]",
            "1 0 0 rg 0 0 25 50 re f"),

        new("matrix-translate",
            "/Matrix translates the appearance space. Same reasoning: the transformed " +
            "box is re-fitted to /Rect, so the ink must land exactly where the baseline does.",
            "[0 0 100 100]", "[1 0 0 1 300 300]", "[50 50 150 150]",
            "1 0 0 rg 0 0 50 100 re f"),
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void ExciseDrawsTheAppearanceWhereTheIndependentRenderersDo(string id)
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        Assert.SkipUnless(PdftocairoReferenceRenderer.IsAvailable, "pdftocairo not installed");

        var c = AllCases.Single(x => x.Id == id);
        var path = WriteFixture(c);

        try
        {
            var oracles = new (string Name, SKBitmap? Bmp)[]
            {
                ("mutool",      MutoolReferenceRenderer.RenderPage(path, 1, Dpi)),
                ("pdftocairo",  PdftocairoReferenceRenderer.RenderPage(path, 1, Dpi)),
                ("ghostscript", GhostscriptReferenceRenderer.IsAvailable
                                    ? GhostscriptReferenceRenderer.RenderPage(path, 1, Dpi) : null),
                ("pdfium",      PdfiumNativeReferenceRenderer.IsAvailable
                                    ? PdfiumNativeReferenceRenderer.RenderPage(
                                          path, 1, Dpi, userPassword: null, renderAnnotations: true) : null),
                ("pdfbox",      PdfBoxReferenceRenderer.IsAvailable
                                    ? PdfBoxReferenceRenderer.RenderPage(path, 1, Dpi) : null),
            };

            var voters = oracles.Where(o => o.Bmp != null).ToList();

            // THREE, not two — the lesson of #976. At two voters a majority is
            // unanimity, so one dissenting renderer empties the reference set
            // and the failure surfaces as "no majority inked anything", which
            // reads like a broken fixture rather than a disagreement. Three
            // makes a real majority possible and keeps the diagnosis honest.
            //
            // mutool, pdftocairo and Ghostscript answer on any fully
            // provisioned box; pdfium and PDFBox join when EXCISE_PDFIUM_TEST
            // and EXCISE_PDFBOX_JAR are set, taking the pool to five.
            Assert.SkipWhen(voters.Count < 3,
                $"only {voters.Count} reference renderer(s) available; a majority needs three");

            var oracleTiles = voters.ToDictionary(o => o.Name, o => InkedTiles(o.Bmp!));

            // The reference answer is the tile set a MAJORITY of the answering
            // oracles ink. Scoring against one renderer elects it, which is the
            // defect #932 removed from the corpus gate.
            var majority = MajorityTiles(oracleTiles.Values.ToList());

            using var mine = RenderWithExcise(path);
            var ours = InkedTiles(mine);

            var report = string.Join("\n", oracleTiles.Select(kv =>
                $"      {kv.Key,-12} {kv.Value.Count,4} tiles  {Describe(kv.Value)}"));

            // Guard first: an all-blank page passes every set comparison
            // trivially. The oracles must actually be drawing something.
            majority.Count.Should().BeGreaterThan(0,
                $"{c.Id}: no majority of oracles inked ANYTHING, so this fixture cannot " +
                $"discriminate. {c.Why}\n{report}");

            var missing = majority.Except(ours).ToList();   // they ink, we don't
            var extra = ours.Except(majority).ToList();     // we ink, they don't

            var tolerance = Math.Max(2, majority.Count / 5);

            missing.Count.Should().BeLessThanOrEqualTo(tolerance,
                $"{c.Id}: excise left {missing.Count} of {majority.Count} tiles blank that a " +
                $"majority of renderers inked.\n  WHY THIS CASE EXISTS: {c.Why}\n" +
                $"  excise {ours.Count} tiles {Describe(ours)}\n{report}");

            extra.Count.Should().BeLessThanOrEqualTo(tolerance,
                $"{c.Id}: excise inked {extra.Count} tiles no majority of renderers inked.\n" +
                $"  WHY THIS CASE EXISTS: {c.Why}\n" +
                $"  excise {ours.Count} tiles {Describe(ours)}\n{report}");
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    private static string Describe(HashSet<(int X, int Y)> tiles)
    {
        if (tiles.Count == 0) return "(none)";
        return $"x{tiles.Min(t => t.X) * Tile}..{tiles.Max(t => t.X) * Tile + Tile}" +
               $" y{tiles.Min(t => t.Y) * Tile}..{tiles.Max(t => t.Y) * Tile + Tile}";
    }

    private static HashSet<(int X, int Y)> MajorityTiles(List<HashSet<(int X, int Y)>> sets)
    {
        var counts = new Dictionary<(int, int), int>();
        foreach (var s in sets)
            foreach (var t in s)
                counts[t] = counts.GetValueOrDefault(t) + 1;
        return counts.Where(kv => kv.Value * 2 > sets.Count).Select(kv => kv.Key).ToHashSet();
    }

    /// <summary>
    /// Tiles carrying meaningful ink. Coarse ON PURPOSE — Skia's scan
    /// conversion differs from Cairo's and MuPDF's, and those differences are an
    /// accepted limitation here, not a defect to chase.
    /// </summary>
    private static HashSet<(int X, int Y)> InkedTiles(SKBitmap bmp)
    {
        var tiles = new HashSet<(int, int)>();
        for (var ty = 0; ty * Tile < bmp.Height; ty++)
            for (var tx = 0; tx * Tile < bmp.Width; tx++)
            {
                var inked = 0;
                for (var y = ty * Tile; y < Math.Min((ty + 1) * Tile, bmp.Height); y++)
                    for (var x = tx * Tile; x < Math.Min((tx + 1) * Tile, bmp.Width); x++)
                    {
                        var c = bmp.GetPixel(x, y);
                        if (c.Alpha > 128 && (c.Red < 200 || c.Green < 200 || c.Blue < 200)) inked++;
                    }
                if (inked > Tile * Tile / 20) tiles.Add((tx, ty));
            }
        return tiles;
    }

    private static SKBitmap RenderWithExcise(string path)
    {
        using var doc = PdfDocument.Open(File.ReadAllBytes(path));
        var bmp = new SkiaRenderer().RenderPage(doc.GetPage(1), new RenderOptions
        {
            Dpi = Dpi,
            RenderAnnotations = true,
        });
        bmp.Should().NotBeNull("excise must render the page");
        return bmp!;
    }

    private static string WriteFixture(ApCase c)
    {
        var ap = c.ApContent;
        var apLen = Encoding.Latin1.GetByteCount(ap);
        var matrix = c.Matrix == null ? "" : $" /Matrix {c.Matrix}";

        var objects = new List<string>
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            $"2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 {PageSize} {PageSize}] >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /Annots [4 0 R] >>\nendobj\n",
            // /F 4 (Print) so Ghostscript, which honours the flag, participates.
            $"4 0 obj\n<< /Type /Annot /Subtype /Square /F 4 /Rect {c.Rect} /AP << /N 5 0 R >> >>\nendobj\n",
            $"5 0 obj\n<< /Type /XObject /Subtype /Form /BBox {c.BBox}{matrix} /Length {apLen} >>\n" +
            $"stream\n{ap}\nendstream\nendobj\n",
        };

        var sb = new StringBuilder();
        var offsets = new List<int>();
        sb.Append("%PDF-1.7\n");
        foreach (var o in objects) { offsets.Add(sb.Length); sb.Append(o); }

        var xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offsets)
            sb.Append(o.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Count + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");

        var path = Path.Combine(Path.GetTempPath(), $"excise-ap-{Guid.NewGuid():N}.pdf");
        File.WriteAllText(path, sb.ToString(), Encoding.Latin1);
        return path;
    }
}
