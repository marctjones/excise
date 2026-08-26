using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// CarrierTextRecovery is the RC18 unredact CERTAIN-channel reader for text that
/// is physically present in a document carrier the visible page never shows —
/// the leak a redaction leaves when it rewrites page content but misses a
/// carrier. These pin that it recovers the carriers the scrubbers clear, and
/// does NOT invent findings on a clean document.
/// </summary>
public class CarrierTextRecoveryTests
{
    private static byte[] Pdf(string body) => Encoding.Latin1.GetBytes(body);

    private const string WithCarriers =
        "%PDF-1.7\n" +
        "1 0 obj\n<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R >>\nendobj\n" +
        "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n" +
        "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
        "/Resources << /Font << /F1 5 0 R >> >> /Annots [7 0 R] >>\nendobj\n" +
        "4 0 obj\n<< /Length 40 >>\nstream\nBT /F1 14 Tf 72 700 Td (Public) Tj ET\nendstream\nendobj\n" +
        "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n" +
        "6 0 obj\n<< /Type /StructTreeRoot /K 8 0 R >>\nendobj\n" +
        "7 0 obj\n<< /Type /Annot /Subtype /Text /Rect [72 700 92 720] /Contents (SECRET-BETA) >>\nendobj\n" +
        "8 0 obj\n<< /Type /StructElem /S /P /ActualText (SECRET-ALPHA) >>\nendobj\n" +
        "trailer\n<< /Root 1 0 R /Size 9 >>\n%%EOF\n";

    [Fact]
    public void Scan_RecoversStructureTreeActualText()
    {
        using var doc = PdfDocument.Open(Pdf(WithCarriers));
        var hits = CarrierTextRecovery.Scan(doc);

        hits.Should().Contain(h => h.Carrier == "structure-tree /ActualText" && h.Text == "SECRET-ALPHA",
            "text in /ActualText is physically present and readable — a certain recovery");
    }

    [Fact]
    public void Scan_RecoversAnnotationContents()
    {
        using var doc = PdfDocument.Open(Pdf(WithCarriers));
        var hits = CarrierTextRecovery.Scan(doc);

        hits.Should().Contain(h => h.Carrier == "annotation /Contents" && h.Text == "SECRET-BETA",
            "an annotation's /Contents note is recoverable text the visible page never shows");
    }

    [Fact]
    public void Scan_CleanDocument_FindsNothing()
    {
        // No structure tree, no annotations — the certain channel must not
        // manufacture findings from ordinary page content.
        var clean =
            "%PDF-1.7\n" +
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" +
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n" +
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
            "/Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n" +
            "4 0 obj\n<< /Length 40 >>\nstream\nBT /F1 14 Tf 72 700 Td (Public) Tj ET\nendstream\nendobj\n" +
            "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n" +
            "trailer\n<< /Root 1 0 R /Size 6 >>\n%%EOF\n";
        using var doc = PdfDocument.Open(Pdf(clean));

        CarrierTextRecovery.Scan(doc).Should().BeEmpty();
    }
}
