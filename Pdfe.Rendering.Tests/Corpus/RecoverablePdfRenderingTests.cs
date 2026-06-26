using AwesomeAssertions;
using Pdfe.Core.Document;
using Xunit;

namespace Pdfe.Rendering.Tests.Corpus;

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
    }
}
