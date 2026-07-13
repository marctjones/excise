using System.Text;
using AwesomeAssertions;
using Pdfe.Core.Document;
using Pdfe.Core.Operations;
using Pdfe.Core.Text.Segmentation;
using Xunit;

namespace Pdfe.Core.Tests.Text.Segmentation;

/// <summary>
/// Redaction leaks through text carriers outside the page content stream (#608).
///
/// Glyph removal is only as strong as the weakest carrier holding the same
/// string. A document can restate a redacted name in:
///
///   - the XMP /Metadata stream (title, subject, keywords)
///   - the /Info dictionary
///   - outline (bookmark) titles
///   - named destinations
///   - annotation /Contents
///   - an earlier revision left behind by an incremental save
///
/// None of these are visible to text extraction, so every one of them is a leak
/// that our content-stream assertions would certify as clean. As in the
/// structure-tree tests (#636), the assertions here read the SAVED BYTES, which
/// is the only carrier-agnostic ground truth.
///
/// Boundary being enforced (per #608): page-level RedactArea scrubs PAGE
/// carriers. Document-level carriers (XMP, outlines, named destinations,
/// embedded files) are scrubbed by the document-level redaction/sanitize entry
/// point. These tests pin both halves, so nobody can quietly move a carrier from
/// "scrubbed" to "out of scope" without a test turning red.
/// </summary>
public class NonPageCarrierRedactionLeakTests
{
    private const string Secret = "SECRET";

    [Fact]
    public void FullSave_GarbageCollectsPreviousRevisionOfRedactedContent()
    {
        // The scariest carrier: an incremental update leaves the ORIGINAL page
        // object, with the un-redacted glyphs, physically present in the file.
        // Anyone reading the previous revision recovers the text in full.
        //
        // PdfDocument.ComputeSaveReachableObjects claims full-save drops stale
        // incremental-update objects because it excludes /Prev from the trailer
        // roots. That is a security claim resting on a code comment. This test
        // is what turns it into a fact.
        var original = BuildPdf(secretInContent: true);
        var incremental = AppendIncrementalUpdate(original);

        // Sanity: the un-redacted revision really is in these bytes twice over.
        BytesAsText(incremental).Should().Contain(Secret,
            "fixture sanity — the original revision must still hold the secret");

        var pdf = PdfDocument.Open(incremental);
        var page = pdf.GetPage(1);
        RedactAllText(page);

        var saved = Save(pdf);

        BytesAsText(saved).Should().NotContain(Secret,
            "a full save must garbage-collect the previous revision. If /Prev survives, the " +
            "un-redacted page object is still in the file and the redaction is cosmetic.");
    }

    [Fact]
    public void Redaction_DoesNotLeaveSecretInInfoDictionary()
    {
        var pdf = PdfDocument.Open(BuildPdf(secretInContent: true, secretInInfo: true));
        var page = pdf.GetPage(1);

        RedactAllText(page);
        PdfDocumentSanitizer.ScrubTerms(pdf, new[] { Secret });

        BytesAsText(Save(pdf)).Should().NotContain(Secret,
            "/Info /Title and /Subject restate the redacted string and are trivially readable");
    }

    [Fact]
    public void Redaction_DoesNotLeaveSecretInXmpMetadata()
    {
        var pdf = PdfDocument.Open(BuildPdf(secretInContent: true, secretInXmp: true));
        var page = pdf.GetPage(1);

        RedactAllText(page);
        PdfDocumentSanitizer.ScrubTerms(pdf, new[] { Secret });

        BytesAsText(Save(pdf)).Should().NotContain(Secret,
            "the XMP /Metadata stream is a plain-text XML packet; a redacted name in dc:title " +
            "survives glyph removal and is readable in any text editor");
    }

    [Fact]
    public void Redaction_DoesNotLeaveSecretInOutlineTitles()
    {
        var pdf = PdfDocument.Open(BuildPdf(secretInContent: true, secretInOutline: true));
        var page = pdf.GetPage(1);

        RedactAllText(page);
        PdfDocumentSanitizer.ScrubTerms(pdf, new[] { Secret });

        BytesAsText(Save(pdf)).Should().NotContain(Secret,
            "a bookmark titled after the redacted content leaks it in the navigation pane — " +
            "visible in the reader's sidebar without even opening the page");
    }

    [Fact]
    public void Sanitizer_LeavesUnrelatedMetadataIntact()
    {
        // The inverse guard. A sanitizer that deletes /Info, /Metadata and
        // /Outlines wholesale would pass every leak assertion above while
        // destroying the document. Only the offending values may go.
        var pdf = PdfDocument.Open(BuildPdf(secretInContent: true, secretInInfo: true, secretInOutline: true));

        RedactAllText(pdf.GetPage(1));
        PdfDocumentSanitizer.ScrubTerms(pdf, new[] { Secret });

        var saved = BytesAsText(Save(pdf));
        saved.Should().Contain("Quarterly Report",
            "unrelated /Info values must survive — sanitizing is surgical, not scorched-earth");
        saved.Should().Contain("Chapter One",
            "unrelated outline titles must survive, or we destroy the document's navigation");
    }

    private static void RedactAllText(PdfPage page)
    {
        page.RedactArea(new PdfRectangle(0, 0, 612, 792), GlyphRemovalStrategy.AnyOverlap);
    }

    private static byte[] Save(PdfDocument pdf)
    {
        using var ms = new MemoryStream();
        pdf.Save(ms);
        return ms.ToArray();
    }

    private static string BytesAsText(byte[] saved) =>
        Encoding.ASCII.GetString(saved) + "\n" + Encoding.BigEndianUnicode.GetString(saved);

    private static byte[] BuildPdf(
        bool secretInContent = false,
        bool secretInInfo = false,
        bool secretInXmp = false,
        bool secretInOutline = false)
    {
        var content = secretInContent
            ? $"BT /F1 24 Tf 100 700 Td ({Secret}) Tj ET"
            : "BT /F1 24 Tf 100 700 Td (public) Tj ET";

        var sb = new StringBuilder();
        var offsets = new List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

        sb.Append("%PDF-1.7\n");

        var catalogExtras = new StringBuilder();
        if (secretInXmp) catalogExtras.Append(" /Metadata 8 0 R");
        if (secretInOutline) catalogExtras.Append(" /Outlines 6 0 R");

        Obj($"1 0 obj\n<< /Type /Catalog /Pages 2 0 R{catalogExtras} >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
            "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        Obj("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        // Outlines: one offending title, one innocent, to pin the inverse guard.
        var outlineTitle = secretInOutline ? Secret : "Chapter Two";
        Obj($"6 0 obj\n<< /Type /Outlines /First 7 0 R /Last 9 0 R /Count 2 >>\nendobj\n");
        Obj($"7 0 obj\n<< /Title ({outlineTitle}) /Parent 6 0 R /Next 9 0 R >>\nendobj\n");

        var xmp = secretInXmp
            ? $"<?xpacket begin='' id='W5M0MpCehiHzreSzNTczkc9d'?><x:xmpmeta xmlns:x='adobe:ns:meta/'>" +
              $"<rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'>" +
              $"<rdf:Description dc:title='{Secret}' xmlns:dc='http://purl.org/dc/elements/1.1/'/>" +
              $"</rdf:RDF></x:xmpmeta><?xpacket end='w'?>"
            : "<?xpacket begin='' id='W5M0MpCehiHzreSzNTczkc9d'?><?xpacket end='w'?>";
        Obj($"8 0 obj\n<< /Type /Metadata /Subtype /XML /Length {xmp.Length} >>\nstream\n{xmp}\nendstream\nendobj\n");

        Obj("9 0 obj\n<< /Title (Chapter One) /Parent 6 0 R /Prev 7 0 R >>\nendobj\n");

        var title = secretInInfo ? Secret : "Quarterly Report";
        Obj($"10 0 obj\n<< /Title ({title}) /Subject (Quarterly Report) >>\nendobj\n");

        int xrefPos = sb.Length;
        sb.Append("xref\n0 11\n0000000000 65535 f \n");
        foreach (var off in offsets)
            sb.Append(off.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size 11 /Root 1 0 R /Info 10 0 R >>\nstartxref\n").Append(xrefPos).Append("\n%%EOF");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Append an incremental update that rewrites the page's content stream,
    /// leaving the ORIGINAL (secret-bearing) content object physically present
    /// in the file and reachable only through /Prev.
    /// </summary>
    private static byte[] AppendIncrementalUpdate(byte[] original)
    {
        var text = Encoding.ASCII.GetString(original);

        // The offset of the original xref TABLE. Searching for "xref\n" would
        // match the tail of "startxref\n" and point /Prev at nonsense.
        int prevXref = text.LastIndexOf("\nxref\n", StringComparison.Ordinal) + 1;

        var sb = new StringBuilder(text);
        const string newContent = "BT /F1 24 Tf 100 700 Td (revised) Tj ET";

        int objOffset = sb.Length;
        sb.Append($"4 0 obj\n<< /Length {newContent.Length} >>\nstream\n{newContent}\nendstream\nendobj\n");

        int xrefPos = sb.Length;
        sb.Append("xref\n0 1\n0000000000 65535 f \n4 1\n")
          .Append(objOffset.ToString("D10")).Append(" 00000 n \n")
          .Append($"trailer\n<< /Size 11 /Root 1 0 R /Info 10 0 R /Prev {prevXref} >>\nstartxref\n")
          .Append(xrefPos).Append("\n%%EOF");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
