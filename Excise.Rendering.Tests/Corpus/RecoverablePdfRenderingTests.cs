using AwesomeAssertions;
using Excise.Core.Document;
using Xunit;

namespace Excise.Rendering.Tests.Corpus;

public class RecoverablePdfRenderingTests
{
    private const string Issue19484_1 = "../../../../test-pdfs/pdfjs/issue19484_1.pdf";
    private const string Issue19484_2 = "../../../../test-pdfs/pdfjs/issue19484_2.pdf";

    [Theory]
    [InlineData(Issue19484_1)]
    [InlineData(Issue19484_2)]
    public void Render_AcrobatCompatibleV4R4ShortKeyPadding_RendersFirstPage(string path)
    {
        Assert.SkipWhen(!File.Exists(path), "pdf.js regression fixture not available");

        using var doc = PdfDocument.Open(path);
        using var bitmap = new SkiaRenderer().RenderPage(doc.GetPage(1), new RenderOptions { Dpi = 72 });

        bitmap.Width.Should().BeGreaterThan(0);
        bitmap.Height.Should().BeGreaterThan(0);
        // #1399: width/height alone pass on a fully blank page (both fixtures
        // are body-text pages, so a wrong short-key derivation renders
        // nothing rather than throwing). Ink is the companion assertion to
        // RecoverablePdfRegressionTests' text-content check.
        InkFraction(bitmap).Should().BeGreaterThan(0.001,
            "the page carries body text; a short-key regression decrypts to garbage/emptiness, not a throw");
    }

    private static double InkFraction(SkiaSharp.SKBitmap bmp)
    {
        if (bmp.Width == 0 || bmp.Height == 0) return 0;
        int ink = 0;
        for (int y = 0; y < bmp.Height; y++)
        for (int x = 0; x < bmp.Width; x++)
        {
            var c = bmp.GetPixel(x, y);
            if (c.Red + c.Green + c.Blue < 384) ink++;
        }
        return (double)ink / (bmp.Width * bmp.Height);
    }
}
