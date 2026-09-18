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
/// #1573 — paper is not the screen. §12.5.3 gives printing its own rule for an
/// annotation's <c>/F</c> flags: it prints only when the <b>Print</b> flag
/// (bit 3) is set, <b>NoView</b> says nothing about paper, and <b>Hidden</b>
/// suppresses both. Windows printing rasterises with <c>SkiaRenderer</c>, which
/// had only the viewer's rule, so it got paper wrong in BOTH directions —
/// printing review markup that Acrobat and PDFKit leave off, and dropping
/// print-only watermarks.
///
/// <para>The fixture puts one annotation in each quadrant with a distinct
/// colour, so "was this one drawn" is a pixel count and not a whole-page
/// difference: no <c>/F</c> at all (the common review-markup case — note
/// excise's own authoring stamps <c>/F Print</c>, so the fixture is hand-built
/// rather than authored), NoView|Print, Print, Hidden|Print.</para>
///
/// <para><b>The oracle is Ghostscript, and it is not excise</b> (CLAUDE.md: a
/// tool must not be its own oracle). gs with a file output device already
/// renders for paper — measured, both directions — so gs's default IS the print
/// oracle and <c>-dPrinted=false</c> is the view one. mutool draw and
/// pdftocairo have no print mode at all, which is why this file uses one
/// reference renderer rather than the usual several.</para>
/// </summary>
public class AnnotationPrintIntentTests
{
    private const int Dpi = 150;

    // Quadrant colours, one per annotation. Kept fully saturated and distinct
    // so a pixel votes for exactly one annotation.
    private static readonly (string Name, string Annot, Func<SKColor, bool> IsInk)[] Annotations =
    [
        ("no /F at all", "/Subtype /Square /Rect [20 20 90 90] /C [1 0 0] /IC [1 0 0] /BS << /W 3 >>",
            c => c.Red > 150 && c.Green < 100 && c.Blue < 100),
        ("NoView|Print", "/Subtype /Square /F 36 /Rect [110 110 180 180] /C [0 0 1] /IC [0 0 1] /BS << /W 3 >>",
            c => c.Blue > 150 && c.Red < 100 && c.Green < 100),
        ("Print", "/Subtype /Square /F 4 /Rect [20 110 90 180] /C [0 1 0] /IC [0 1 0] /BS << /W 3 >>",
            c => c.Green > 150 && c.Red < 100 && c.Blue < 100),
        ("Hidden|Print", "/Subtype /Square /F 6 /Rect [110 20 180 90] /C [1 0 1] /IC [1 0 1] /BS << /W 3 >>",
            c => c.Red > 150 && c.Blue > 150 && c.Green < 100),
    ];

    /// <summary>Which annotations §12.5.3 puts on PAPER: the Print flag, minus Hidden.</summary>
    private static readonly bool[] OnPaper = [false, true, true, false];

    /// <summary>Which ones a VIEWER shows: everything but Hidden and NoView.</summary>
    private static readonly bool[] OnScreen = [true, false, true, false];

    [Fact]
    public void PrintIntent_DrawsExactlyWhatSection1253PutsOnPaper()
    {
        var counts = ExciseInk(new RenderOptions { Dpi = Dpi, PrintIntent = true });

        for (var i = 0; i < Annotations.Length; i++)
        {
            if (OnPaper[i])
            {
                counts[i].Should().BeGreaterThan(1000,
                    $"§12.5.3 prints '{Annotations[i].Name}'");
            }
            else
            {
                counts[i].Should().Be(0,
                    $"§12.5.3 keeps '{Annotations[i].Name}' off paper");
            }
        }
    }

    [Fact]
    public void WithoutPrintIntent_TheViewerRuleIsUnchanged()
    {
        var counts = ExciseInk(new RenderOptions { Dpi = Dpi });

        for (var i = 0; i < Annotations.Length; i++)
        {
            if (OnScreen[i])
                counts[i].Should().BeGreaterThan(1000, $"a viewer shows '{Annotations[i].Name}'");
            else
                counts[i].Should().Be(0, $"a viewer hides '{Annotations[i].Name}'");
        }
    }

    /// <summary>
    /// The two rules must actually DIFFER on this fixture. Without this, both
    /// tests above could be passing on a renderer that ignores the option
    /// entirely and happens to agree — the shape of a check that cannot fail.
    /// </summary>
    [Fact]
    public void ThePrintAndViewRulesDisagreeOnThisFixture()
    {
        var print = ExciseInk(new RenderOptions { Dpi = Dpi, PrintIntent = true });
        var view = ExciseInk(new RenderOptions { Dpi = Dpi });

        print.Should().NotEqual(view,
            "the fixture exists to separate the print rule from the view rule");
        (print[0] == 0 && view[0] > 0).Should().BeTrue(
            "the no-/F annotation is the case that reaches paper wrongly");
        (print[1] > 0 && view[1] == 0).Should().BeTrue(
            "the NoView|Print annotation is the print-only watermark case");
    }

    /// <summary>
    /// Ghostscript decides, per annotation, whether excise put the right ink on
    /// paper and on screen — including the direction excise had wrong.
    /// </summary>
    [Fact]
    public void Ghostscript_AgreesWithBothRules()
    {
        Assert.SkipUnless(GhostscriptReferenceRenderer.IsAvailable, "ghostscript not installed");

        var path = Path.Combine(Path.GetTempPath(), $"excise-printintent-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, Fixture());
        try
        {
            var printed = GhostscriptReferenceRenderer.TryRenderPage(path, 1, Dpi);
            Assert.SkipWhen(printed.Bitmap == null,
                $"ghostscript could not render the fixture for print: {printed.ErrorMessage}");
            var viewed = GhostscriptReferenceRenderer.TryRenderPageForViewIntent(path, 1, Dpi);
            Assert.SkipWhen(viewed.Bitmap == null,
                $"ghostscript could not render the fixture for view: {viewed.ErrorMessage}");

            using var printBitmap = printed.Bitmap!;
            using var viewBitmap = viewed.Bitmap!;
            var gsPrint = Ink(printBitmap);
            var gsView = Ink(viewBitmap);

            // The oracle reproduces the rule this fixture is built around; if it
            // does not, it is not an oracle for this question and saying so is
            // more useful than asserting against it.
            Assert.SkipWhen(gsPrint.SequenceEqual(gsView),
                "this ghostscript does not distinguish print from view intent (-dPrinted), " +
                $"print={string.Join(",", gsPrint)} view={string.Join(",", gsView)}");

            for (var i = 0; i < Annotations.Length; i++)
            {
                (gsPrint[i] > 0).Should().Be(OnPaper[i],
                    $"ghostscript's print raster on '{Annotations[i].Name}' " +
                    $"(counts print={string.Join(",", gsPrint)})");
                (gsView[i] > 0).Should().Be(OnScreen[i],
                    $"ghostscript's view raster on '{Annotations[i].Name}' " +
                    $"(counts view={string.Join(",", gsView)})");
            }

            var excisePrint = ExciseInk(new RenderOptions { Dpi = Dpi, PrintIntent = true });
            var exciseView = ExciseInk(new RenderOptions { Dpi = Dpi });
            for (var i = 0; i < Annotations.Length; i++)
            {
                (excisePrint[i] > 0).Should().Be(gsPrint[i] > 0,
                    $"excise's print raster must agree with ghostscript on '{Annotations[i].Name}'");
                (exciseView[i] > 0).Should().Be(gsView[i] > 0,
                    $"excise's view raster must agree with ghostscript on '{Annotations[i].Name}'");
            }
        }
        finally
        {
            try { File.Delete(path); } catch { /* temp file */ }
        }
    }

    private static long[] ExciseInk(RenderOptions options)
    {
        using var document = PdfDocument.Open(Fixture());
        using var bitmap = new SkiaRenderer().RenderPage(document.GetPage(1), options)!;
        return Ink(bitmap);
    }

    private static long[] Ink(SKBitmap bitmap)
    {
        var counts = new long[Annotations.Length];
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Alpha <= 40)
                    continue;
                for (var i = 0; i < Annotations.Length; i++)
                {
                    if (Annotations[i].IsInk(pixel))
                    {
                        counts[i]++;
                        break;
                    }
                }
            }
        }
        return counts;
    }

    /// <summary>
    /// A 200x200 page with the four annotations and no content stream, written
    /// by hand: <c>PdfAnnotationAuthoring</c> stamps <c>/F Print</c> on
    /// everything it creates, which is precisely the flag under test.
    /// </summary>
    private static byte[] Fixture()
    {
        var annots = Annotations.Select(a => a.Annot).ToArray();
        var refs = string.Join(" ", annots.Select((_, i) => $"{4 + i} 0 R"));
        var objects = new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 200 200] >>\nendobj\n",
            $"3 0 obj\n<< /Type /Page /Parent 2 0 R /Annots [{refs}] >>\nendobj\n",
        }.Concat(annots.Select((a, i) => $"{4 + i} 0 obj\n<< /Type /Annot {a} >>\nendobj\n")).ToArray();

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        foreach (var o in objects)
        {
            offsets.Add(sb.Length);
            sb.Append(o);
        }

        var xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            sb.Append(offset.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
