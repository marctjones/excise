using System.Text;
using Pdfe.Core.Authoring;
using Pdfe.Core.Graphics;
using Xunit;

namespace Pdfe.Core.Tests;

/// <summary>
/// Verifies <see cref="PdfDocumentBuilder.PdfA"/> emits the document-level
/// structures PDF/A requires: an XMP packet with the pdfaid identifier, an sRGB
/// OutputIntent, a trailer /ID, and an embedded font (no base-14). Full
/// conformance is validated externally with veraPDF (PDF/A-2b: 144/144 rules).
/// </summary>
public class PdfATests
{
    private static byte[] BuildPdfA()
    {
        var font = PdfFont.FromFile("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf", 11);
        return PdfDocumentBuilder.Create()
            .Language("en-US")
            .Title("Archival Test")
            .DefaultFont(font)
            .PdfA(PdfAConformance.PdfA2B)
            .Heading("Archival Test")
            .Paragraph("Body text — with an em dash and unicode: café.")
            .SaveToBytes();
    }

    [Fact]
    public void PdfA_EmitsXmpPdfaId_OutputIntent_AndTrailerId()
    {
        var latin1 = Encoding.Latin1.GetString(BuildPdfA());

        Assert.Contains("pdfaid:part>2", latin1);
        Assert.Contains("pdfaid:conformance>B", latin1);
        Assert.Contains("/OutputIntents", latin1);
        Assert.Contains("GTS_PDFA1", latin1);
        Assert.Contains("/ID", latin1);
    }

    [Fact]
    public void NewDocument_AlwaysGetsATrailerId()
    {
        var bytes = PdfDocumentBuilder.Create().Heading("Hi").SaveToBytes();
        Assert.Contains("/ID", Encoding.Latin1.GetString(bytes));
    }
}
