using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1395 — Ghent GWG168/GWG169 (vector soft masks on a DeviceCMYK page),
/// judged PER CELL against MuPDF and Ghostscript.
///
/// <para><b>Why per cell.</b> The corpus scan's 0.10 page diff fraction cannot
/// see a patch-sized failure: GWG169 sat at PASS with the same visible defect
/// GWG168 shows, and GWG168's five wrong cells (a black square with an inverted
/// gradient for a cyan drop shadow, a white square for a yellow one, a glow
/// missing, a glow nearly blank, a grey gradient for a green bevel) are each a
/// few percent of the page.</para>
///
/// <para><b>What is compared.</b> Each cell of the "Actual test objects" row is
/// split into a 4x4 grid; each block is reduced to two numbers — luminance and
/// chroma (max − min channel). Not RGB: these pages carry a PDF/X-4 output
/// intent and the three engines preview CMYK differently (the gwg160–162
/// contracts already record the references disagreeing on exact pixels). The
/// failure signatures above are large in one of the two numbers — black vs
/// cyan in luminance, white vs yellow and grey vs green in chroma. A block is
/// only a target where MuPDF and Ghostscript agree on it; at least half the
/// blocks of a cell must be judgeable, or the test fails rather than passing
/// on nothing.</para>
///
/// <para>Cell rectangles are PDF points read from the decompressed content
/// streams: the object squares sit at y 90.78..113.45; the row band is widened
/// to take in shadows and glows and stops below the "Actual test objects"
/// label.</para>
/// </summary>
public class GhentSoftMaskVectorCellTests
{
    private const int Dpi = 144;
    private const double PageHeightPt = 141.732;
    private const int Grid = 4;
    private const int LuminanceTolerance = 40;
    private const int ChromaTolerance = 60;

    private const string Gwg168 = "GWG168_Softmasks_Vector_part1_X4.pdf";
    private const string Gwg169 = "GWG169_Softmasks_Vector_part2_X4.pdf";

    private static readonly object RenderLock = new();
    private static readonly Dictionary<string, SKBitmap?> Renders = new();

    [Theory]
    [InlineData(Gwg168, "Drop Shadow", 8.0, 50.0, 81.0, 115.0)]
    [InlineData(Gwg168, "Inner Shadow", 58.0, 100.0, 81.0, 115.0)]
    [InlineData(Gwg168, "Outer Glow", 108.0, 150.0, 81.0, 115.0)]
    [InlineData(Gwg168, "Inner Glow", 158.0, 200.0, 81.0, 115.0)]
    [InlineData(Gwg168, "Bevel and Emboss", 208.0, 250.0, 81.0, 115.0)]
    [InlineData(Gwg169, "Satin", 31.0, 74.0, 79.5, 115.0)]
    [InlineData(Gwg169, "Basic Feather", 81.0, 124.0, 79.5, 115.0)]
    [InlineData(Gwg169, "Directional Feather", 131.0, 174.0, 79.5, 115.0)]
    [InlineData(Gwg169, "Gradient Feather", 181.0, 224.0, 79.5, 115.0)]
    public void Cell_MatchesIndependentRenderers(
        string file, string cell, double leftPt, double rightPt, double bottomPt, double topPt)
    {
        var path = FindGhentPatch(file);
        Assert.SkipWhen(path == null,
            $"Ghent {file} is not present locally (run scripts/download-test-pdfs.sh).");
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        Assert.SkipUnless(GhostscriptReferenceRenderer.IsAvailable, "ghostscript not installed");

        var excise = Render("excise", path!);
        var mutool = Render("mutool", path!);
        var gs = Render("gs", path!);
        excise.Should().NotBeNull();
        mutool.Should().NotBeNull("mutool rendered {0}", file);
        gs.Should().NotBeNull("ghostscript rendered {0}", file);

        var e = Blocks(excise!, leftPt, rightPt, bottomPt, topPt);
        var m = Blocks(mutool!, leftPt, rightPt, bottomPt, topPt);
        var g = Blocks(gs!, leftPt, rightPt, bottomPt, topPt);

        var judged = 0;
        var failures = new List<string>();
        for (var i = 0; i < e.Length; i++)
        {
            var oraclesAgree =
                Math.Abs(m[i].Luminance - g[i].Luminance) <= LuminanceTolerance &&
                Math.Abs(m[i].Chroma - g[i].Chroma) <= ChromaTolerance;
            if (!oraclesAgree)
                continue;

            judged++;
            var targetLuminance = (m[i].Luminance + g[i].Luminance) / 2;
            var targetChroma = (m[i].Chroma + g[i].Chroma) / 2;
            if (Math.Abs(e[i].Luminance - targetLuminance) > LuminanceTolerance ||
                Math.Abs(e[i].Chroma - targetChroma) > ChromaTolerance)
            {
                failures.Add(
                    $"block ({i % Grid},{i / Grid}): excise L{e[i].Luminance:F0}/C{e[i].Chroma:F0} "
                    + $"vs mutool L{m[i].Luminance:F0}/C{m[i].Chroma:F0}, gs L{g[i].Luminance:F0}/C{g[i].Chroma:F0}");
            }
        }

        judged.Should().BeGreaterThanOrEqualTo(Grid * Grid / 2,
            $"{file} '{cell}': MuPDF and Ghostscript must agree on at least half the blocks "
            + "for their answer to be a target");
        failures.Should().BeEmpty(
            $"{file} '{cell}' must look the way MuPDF and Ghostscript render it (#1395). "
            + $"Whole cell (excise {excise!.Width}x{excise.Height}, mutool {mutool!.Width}x{mutool.Height}, "
            + $"gs {gs!.Width}x{gs.Height}):\n{Dump(e, m, g)}");
    }

    private readonly record struct Block(double Luminance, double Chroma);

    /// <summary>
    /// Every block's luminance/chroma for all three renderers, so a failure
    /// says where in the cell excise departs rather than naming one block.
    /// </summary>
    private static string Dump(Block[] e, Block[] m, Block[] g)
        => string.Join("\n", Enumerable.Range(0, Grid).Select(by =>
            string.Join("   ", Enumerable.Range(0, Grid).Select(bx =>
            {
                var i = (by * Grid) + bx;
                return $"({bx},{by}) e={e[i].Luminance:F0}/{e[i].Chroma:F0}"
                     + $" m={m[i].Luminance:F0}/{m[i].Chroma:F0}"
                     + $" g={g[i].Luminance:F0}/{g[i].Chroma:F0}";
            }))));

    private static Block[] Blocks(SKBitmap bitmap, double leftPt, double rightPt, double bottomPt, double topPt)
    {
        var scale = Dpi / 72.0;
        var x0 = Math.Clamp((int)Math.Floor(leftPt * scale), 0, bitmap.Width);
        var x1 = Math.Clamp((int)Math.Ceiling(rightPt * scale), 0, bitmap.Width);
        var y0 = Math.Clamp((int)Math.Floor((PageHeightPt - topPt) * scale), 0, bitmap.Height);
        var y1 = Math.Clamp((int)Math.Ceiling((PageHeightPt - bottomPt) * scale), 0, bitmap.Height);

        var blocks = new Block[Grid * Grid];
        for (var by = 0; by < Grid; by++)
        {
            for (var bx = 0; bx < Grid; bx++)
            {
                var bx0 = x0 + ((x1 - x0) * bx / Grid);
                var bx1 = x0 + ((x1 - x0) * (bx + 1) / Grid);
                var by0 = y0 + ((y1 - y0) * by / Grid);
                var by1 = y0 + ((y1 - y0) * (by + 1) / Grid);

                double r = 0, g = 0, b = 0;
                var n = 0;
                for (var y = by0; y < by1; y++)
                {
                    for (var x = bx0; x < bx1; x++)
                    {
                        var c = bitmap.GetPixel(x, y);
                        r += c.Red;
                        g += c.Green;
                        b += c.Blue;
                        n++;
                    }
                }

                if (n > 0)
                {
                    r /= n;
                    g /= n;
                    b /= n;
                }

                blocks[(by * Grid) + bx] = new Block(
                    (0.299 * r) + (0.587 * g) + (0.114 * b),
                    Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)));
            }
        }

        return blocks;
    }

    private static SKBitmap? Render(string engine, string path)
    {
        var key = engine + "|" + path;
        lock (RenderLock)
        {
            if (Renders.TryGetValue(key, out var cached))
                return cached;

            SKBitmap? bitmap = engine switch
            {
                "mutool" => MutoolReferenceRenderer.RenderPage(path, 1, Dpi),
                "gs" => GhostscriptReferenceRenderer.RenderPage(path, 1, Dpi),
                _ => RenderWithExcise(path),
            };
            Renders[key] = bitmap;
            return bitmap;
        }
    }

    private static SKBitmap RenderWithExcise(string path)
    {
        using var doc = PdfDocument.Open(path);
        return new SkiaRenderer().RenderPage(doc.GetPage(1),
            new RenderOptions { Dpi = Dpi, BackgroundColor = SKColors.White });
    }

    private static string? FindGhentPatch(string fileName)
        => FindRepoFile(
            "test-pdfs", "ghent", "extracted", "patches",
            "Ghent_PDF_Output_Suite_V50_Patches", "Categories", "1-CMYK", "Patches",
            fileName);

    private static string? FindRepoFile(params string[] segments)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(segments).ToArray());
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        return null;
    }
}
