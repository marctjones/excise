using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using SkiaSharp;

namespace Excise.Rendering.Tests.Visual;

/// <summary>
/// Pins the #1426 skip: a DeviceCMYK transparency-group Form XObject whose
/// content stream cannot mark the page at all (no path-painting, text-
/// showing, shading or XObject operator) must render IDENTICALLY whether
/// invoked or not -- and, separately, a form that DOES paint must never be
/// skipped. Both real fixtures this was found on
/// (test-pdfs/pdfjs/bug1703683_page2_reduced.pdf,
/// test-pdfs/pdfjs/bug1755507.pdf, #1386's Class B) were independently
/// verified byte-identical against develop and against mutool/pdftocairo
/// before this landed; these are the synthetic unit-level pins.
/// </summary>
public sealed class RenderTailEmptyTransparencyGroupTests
{
    [Fact]
    public void RenderPage_EmptyDeviceCmykGroupInvokedRepeatedly_MatchesPageWithoutTheInvocations()
    {
        const string emptyForm = "0 TL\nq\nQ\n"; // the exact real-world degenerate content (#1426)
        var repeatedInvocations = string.Concat(Enumerable.Repeat("q 1 0 0 1 20 20 cm /Fm Do Q\n", 5));
        var withInvocations = BuildPage(
            content:
                "0.1 0.1 0 0 k 0 0 300 300 re f\n" +
                repeatedInvocations +
                "0 0 1 0 k 40 40 60 60 re f\n",
            formBBox: "[32768 32768 -32768 -32768]", // the exact real-world degenerate BBox
            formContent: emptyForm);
        var withoutInvocations = BuildPage(
            content: "0.1 0.1 0 0 k 0 0 300 300 re f\n0 0 1 0 k 40 40 60 60 re f\n",
            formBBox: "[32768 32768 -32768 -32768]",
            formContent: emptyForm);

        using var docWith = PdfDocument.Open(withInvocations);
        using var bitmapWith = new SkiaRenderer().RenderPage(
            docWith.GetPage(1), new RenderOptions { Dpi = 96, BackgroundColor = SKColors.White });
        using var docWithout = PdfDocument.Open(withoutInvocations);
        using var bitmapWithout = new SkiaRenderer().RenderPage(
            docWithout.GetPage(1), new RenderOptions { Dpi = 96, BackgroundColor = SKColors.White });

        bitmapWith.Width.Should().Be(bitmapWithout.Width);
        bitmapWith.Height.Should().Be(bitmapWithout.Height);
        bitmapWith.Bytes.Should().Equal(bitmapWithout.Bytes,
            "invoking a form 5 times whose content stream cannot mark the page must render " +
            "identically to never invoking it at all -- a group that paints nothing must not " +
            "perturb the backdrop it composites against (ISO 32000-1 §11.4/§11.6.6)");
    }

    [Fact]
    public void RenderPage_NonEmptyFormWithDegenerateBBox_StillPaintsItsContent()
    {
        // Regression guard: the #1426 "paints nothing" check must be
        // conservative. A form sharing the exact degenerate BBox shape but
        // whose content stream DOES paint must never be skipped.
        var pdf = BuildPage(
            content: "q 1 0 0 1 20 20 cm /Fm Do Q\n",
            formBBox: "[32768 32768 -32768 -32768]",
            formContent: "1 0 0 0 k 0 0 100 100 re f\n"); // paints cyan

        using var doc = PdfDocument.Open(pdf);
        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1), new RenderOptions { Dpi = 96, BackgroundColor = SKColors.White });

        // The form paints a rect at form-local (0,0)-(100,100), translated by
        // "1 0 0 1 20 20 cm" -> absolute PDF-space (20,20)-(120,120); centre
        // (70,70) in device pixels at 96 DPI (scale 4/3), Y flipped to top-left origin.
        var probe = bitmap.GetPixel(93, 307);
        (probe.Red < 250 || probe.Green < 250 || probe.Blue < 250).Should().BeTrue(
            "a form that actually paints must not be treated as empty just because it shares " +
            $"the degenerate-BBox shape -- expected non-white ink, got {probe}");
    }

    [Fact]
    public void RenderPage_FormWithOnlyGraphicsStateOperators_IsTreatedAsEmpty()
    {
        // Broader than the real-world fixture's degenerate BBox: any form
        // whose content is only q/Q/cm/gs (no PathPainting/TextShowing/
        // Shading/XObject operator) must be treated as empty, with a NORMAL
        // (non-degenerate) BBox too.
        var withInvocation = BuildPage(
            content: "0.2 0.3 0.4 0 k 0 0 300 300 re f\nq 1 0 0 1 20 20 cm /Fm Do Q\n",
            formBBox: "[0 0 50 50]",
            formContent: "q 1 0 0 1 10 10 cm Q\n");
        var withoutInvocation = BuildPage(
            content: "0.2 0.3 0.4 0 k 0 0 300 300 re f\n",
            formBBox: "[0 0 50 50]",
            formContent: "q 1 0 0 1 10 10 cm Q\n");

        using var docWith = PdfDocument.Open(withInvocation);
        using var bitmapWith = new SkiaRenderer().RenderPage(
            docWith.GetPage(1), new RenderOptions { Dpi = 96, BackgroundColor = SKColors.White });
        using var docWithout = PdfDocument.Open(withoutInvocation);
        using var bitmapWithout = new SkiaRenderer().RenderPage(
            docWithout.GetPage(1), new RenderOptions { Dpi = 96, BackgroundColor = SKColors.White });

        bitmapWith.Bytes.Should().Equal(bitmapWithout.Bytes,
            "a normal-BBox form with only graphics-state operators must also be treated as empty");
    }

    private static byte[] BuildPage(string content, string formBBox, string formContent)
    {
        var extraObjects = new (int Number, string Dictionary, string StreamContent)[]
        {
            (5,
             $"/Type /XObject /Subtype /Form /BBox {formBBox} " +
             "/Group << /S /Transparency /CS /DeviceCMYK >>",
             formContent),
        };
        return BuildSinglePagePdf(content, "/XObject << /Fm 5 0 R >>", extraObjects);
    }

    private static byte[] BuildSinglePagePdf(
        string content,
        string resources,
        (int Number, string Dictionary, string StreamContent)[]? extraObjects)
    {
        var sb = new StringBuilder();
        var objectCount = 4 + (extraObjects?.Length ?? 0);
        var offsets = new long[objectCount + 1];
        sb.Append("%PDF-1.7\n");

        offsets[1] = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        offsets[2] = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        offsets[3] = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Contents 4 0 R\n");
        sb.Append("   /Group << /S /Transparency /CS /DeviceCMYK >>\n");
        sb.Append($"   /Resources << {resources} >>\n>>\nendobj\n");

        offsets[4] = sb.Length;
        sb.Append($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");

        if (extraObjects != null)
        {
            foreach (var (number, dictionary, streamContent) in extraObjects)
            {
                offsets[number] = sb.Length;
                sb.Append($"{number} 0 obj\n<< {dictionary} /Length {streamContent.Length} >>\n");
                sb.Append($"stream\n{streamContent}\nendstream\nendobj\n");
            }
        }

        var xref = sb.Length;
        sb.Append($"xref\n0 {objectCount + 1}\n0000000000 65535 f \n");
        for (var i = 1; i <= objectCount; i++)
            sb.Append($"{offsets[i]:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Root 1 0 R /Size {objectCount + 1} >>\nstartxref\n{xref}\n%%EOF\n");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
