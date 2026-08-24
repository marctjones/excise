using System.IO;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Operations;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// #1129 — a redacted term surviving in a PAGE-level XMP packet.
///
/// <para>§14.3.2 permits a <c>/Metadata</c> stream on any object, not only the
/// catalog. <c>ScrubXmpMetadata</c> used to read only
/// <c>Catalog["Metadata"]</c>, so on a real CDC PDF the term was scrubbed from
/// the catalog packet and left intact in a page packet — mutool read clean,
/// the bytes did not. Found by the redaction benchmark.</para>
/// </summary>
public class MultiPacketXmpRedactionTests
{
    private const string Secret = "Farrar";

    /// <summary>
    /// One page carrying its OWN <c>/Metadata</c> packet with the secret in
    /// <c>dc:title</c>. The catalog has no metadata at all, so a catalog-only
    /// scrub would miss this entirely.
    /// </summary>
    private static byte[] BuildPageLevelXmpPdf()
    {
        var xmp =
            "<?xpacket begin='' id='W5M0MpCehiHzreSzNTczkc9d'?>" +
            "<x:xmpmeta xmlns:x='adobe:ns:meta/'><rdf:RDF " +
            "xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'>" +
            $"<rdf:Description dc:title='{Secret} report' " +
            "xmlns:dc='http://purl.org/dc/elements/1.1/'/></rdf:RDF>" +
            "</x:xmpmeta><?xpacket end='w'?>";

        var sb = new StringBuilder();
        var offsets = new System.Collections.Generic.List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

        sb.Append("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        // The page carries /Metadata 6 0 R -- NOT the catalog.
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
            "/Metadata 6 0 R /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>\nendobj\n");
        Obj("4 0 obj\n<< /Length 40 >>\nstream\nBT /F1 24 Tf 100 700 Td (public) Tj ET\nendstream\nendobj\n");
        Obj("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        Obj($"6 0 obj\n<< /Type /Metadata /Subtype /XML /Length {xmp.Length} >>\nstream\n{xmp}\nendstream\nendobj\n");

        var xref = sb.Length;
        sb.Append("xref\n0 7\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size 7 /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static byte[] Save(PdfDocument d)
    {
        using var ms = new MemoryStream();
        d.Save(ms);
        return ms.ToArray();
    }

    [Fact]
    public void Guard_TheSecretIsInThePageLevelPacketToBeginWith()
    {
        var saved = BuildPageLevelXmpPdf();
        SavedPdfLeakScanner.FindTerm(saved, Secret).Should().NotBeEmpty(
            "the fixture must actually place the secret in a page-level packet, " +
            "or this test proves nothing");
    }

    [Fact]
    public void PageLevelXmpPacket_IsScrubbed()
    {
        using var doc = PdfDocument.Open(BuildPageLevelXmpPdf());
        PdfDocumentSanitizer.ScrubTerms(doc, new[] { Secret });

        // Carrier-agnostic, decompressing: if the term survives in ANY packet,
        // catalog or page, this fails.
        SavedPdfLeakScanner.FindTerm(Save(doc), Secret).Should().BeEmpty(
            "§14.3.2 allows /Metadata on any object; the scrub must reach page- " +
            "and XObject-level packets, not just the catalog's (#1129)");
    }
}
