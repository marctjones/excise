using System.IO;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1022 — <see cref="RenderOptions.RenderAnnotations"/> actually controls
/// whether annotations are drawn, and defaults to ON.
///
/// ON is the correct default and is not a preference: annotations are part of
/// what a conforming viewer shows (§12.5), and five of the six reference
/// renderers draw them by default. OFF answers a different question — what is
/// IN the page content stream versus what is overlaid on it — which is the
/// distinction that matters for a redaction tool, because a FreeText annotation
/// looks like page content and is not.
///
/// These need no oracle. Whether excise draws the SAME annotation as mutool is
/// #1010's question and the spec declines to answer it; whether excise draws
/// one at all when asked is checkable from excise's own output.
/// </summary>
public class AnnotationVisibilityToggleTests
{
    private static byte[] PageWithASquareAnnotation()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();
        doc.AddSquareAnnotation(1, new PdfRectangle(72, 600, 300, 660), "toggle fixture");
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static long InkedPixels(SKBitmap bitmap)
    {
        long inked = 0;
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
            {
                var c = bitmap.GetPixel(x, y);
                // Anything meaningfully darker than the white background.
                if (c.Red < 240 || c.Green < 240 || c.Blue < 240) inked++;
            }
        return inked;
    }

    private static long RenderAndCountInk(byte[] pdf, bool renderAnnotations)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf), ownsStream: true);
        var page = doc.GetPage(1);
        using var bitmap = new SkiaRenderer().RenderPage(page, new RenderOptions
        {
            Dpi = 72,
            RenderAnnotations = renderAnnotations
        });
        bitmap.Should().NotBeNull("the page must render either way");
        return InkedPixels(bitmap!);
    }

    [Fact]
    public void RenderAnnotations_DefaultsToOn()
    {
        new RenderOptions().RenderAnnotations.Should().BeTrue(
            "annotations are part of what a conforming viewer shows (§12.5); " +
            "hiding them must be something the caller asks for, never the default");
    }

    [Fact]
    public void TurningAnnotationsOff_RemovesTheirInk()
    {
        var pdf = PageWithASquareAnnotation();

        var withInk = RenderAndCountInk(pdf, renderAnnotations: true);
        var withoutInk = RenderAndCountInk(pdf, renderAnnotations: false);

        // The page content stream is empty, so the annotation is the ONLY ink.
        // Asserting both directions matters: "with > without" alone would pass
        // if the annotation were drawn faintly in both, and "without == 0"
        // alone would pass if nothing ever drew anything.
        withInk.Should().BeGreaterThan(0,
            "the square annotation is the page's only content, so it must ink something when enabled");
        withoutInk.Should().Be(0,
            "with the annotation suppressed and an empty content stream, the page must be blank");
    }
}
