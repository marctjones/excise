using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Core.Tests.Document;

/// <summary>
/// Tests for document-metadata scrubbing.
///
/// Why this matters: content-level redaction removes glyphs from the body of
/// the PDF, but a redacted title or author still surfaces the secret in the
/// file-properties dialog and tools like pdfinfo. The scrubbing API closes
/// that gap.
/// </summary>
public class MetadataScrubTests
{
    [Fact]
    public void ScrubMetadata_RemovesAllInfoKeys()
    {
        var pdf = BuildPdfWithMetadata(
            title: "Secret Title",
            author: "Mr Smith",
            subject: "Confidential",
            keywords: "redact-me",
            creator: "Internal Tool 1.0",
            producer: "ToolBackend");

        using var doc = PdfDocument.Open(pdf);
        doc.Title.Should().Be("Secret Title"); // sanity

        doc.ScrubMetadata();

        doc.Title.Should().BeNull();
        doc.Author.Should().BeNull();
        doc.Subject.Should().BeNull();
        doc.Keywords.Should().BeNull();
        doc.Creator.Should().BeNull();
        doc.Producer.Should().BeNull();
    }

    [Fact]
    public void ScrubMetadata_DropsXmpMetadataStream()
    {
        var pdf = BuildPdfWithMetadata(
            title: "T",
            xmpBody: "<?xml version=\"1.0\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">SECRET</rdf:li></rdf:Alt></dc:title></rdf:Description></rdf:RDF></x:xmpmeta>");

        using var doc = PdfDocument.Open(pdf);
        doc.GetXmpMetadata().Should().NotBeNull(); // sanity

        doc.ScrubMetadata();

        doc.GetXmpMetadata().Should().BeNull();
    }

    [Fact]
    public void GetXmpMetadata_NoMetadataStream_ReturnsNull()
    {
        var pdf = BuildPdfWithMetadata(title: "T"); // no XMP
        using var doc = PdfDocument.Open(pdf);
        doc.GetXmpMetadata().Should().BeNull();
    }

    [Fact]
    public void GetXmpMetadata_ReturnsDecodedXmpBody()
    {
        var xmp = "<?xml version=\"1.0\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\"></x:xmpmeta>";
        var pdf = BuildPdfWithMetadata(title: "T", xmpBody: xmp);
        using var doc = PdfDocument.Open(pdf);

        var bytes = doc.GetXmpMetadata();
        bytes.Should().NotBeNull();
        Encoding.UTF8.GetString(bytes!).Should().Contain("xmpmeta");
    }

    [Fact]
    public void ScrubInfoKeys_TargetedRemoval_LeavesOthersAlone()
    {
        var pdf = BuildPdfWithMetadata(title: "T", author: "A", creator: "C");
        using var doc = PdfDocument.Open(pdf);

        doc.ScrubInfoKeys("Title", "Author");

        doc.Title.Should().BeNull();
        doc.Author.Should().BeNull();
        doc.Creator.Should().Be("C");
    }

    [Fact]
    public void ScrubMetadata_AfterScrub_OperationIsIdempotent()
    {
        var pdf = BuildPdfWithMetadata(title: "T");
        using var doc = PdfDocument.Open(pdf);

        doc.ScrubMetadata();
        doc.ScrubMetadata(); // second call must not throw

        doc.Title.Should().BeNull();
    }

    [Fact]
    public void ScrubMetadata_DocumentWithoutInfoDict_DoesNotThrow()
    {
        var pdf = BuildPdfWithoutInfoDict();
        using var doc = PdfDocument.Open(pdf);

        // No-op essentially, but the API should be safe to call regardless.
        var act = () => doc.ScrubMetadata();
        act.Should().NotThrow();
    }

    [Fact]
    public void ScrubInfoKeys_NullArgument_DoesNotThrow()
    {
        var pdf = BuildPdfWithMetadata(title: "T");
        using var doc = PdfDocument.Open(pdf);

        var act = () => doc.ScrubInfoKeys(null!);
        act.Should().NotThrow();
    }

    [Fact]
    public void ScrubMetadata_PreservesNonMetadataInfoEntries()
    {
        // Custom Info-dict keys (e.g. /exciseAppVersion) should be left alone —
        // we only scrub the standard PDF spec keys.
        var pdf = BuildPdfWithMetadata(title: "T", customInfoKey: "exciseAppVersion", customInfoValue: "1.0");
        using var doc = PdfDocument.Open(pdf);

        doc.ScrubMetadata();

        doc.Info.Should().NotBeNull();
        doc.Info!.GetStringOrNull("exciseAppVersion").Should().Be("1.0");
    }

    [Fact]
    public void ScrubMetadata_RoundTrip_SaveAndReload_StaysScrubbed()
    {
        var pdf = BuildPdfWithMetadata(
            title: "Secret",
            author: "X",
            xmpBody: "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"></x:xmpmeta>");

        byte[] scrubbed;
        using (var doc = PdfDocument.Open(pdf))
        {
            doc.ScrubMetadata();
            using var ms = new System.IO.MemoryStream();
            doc.Save(ms);
            scrubbed = ms.ToArray();
        }

        using var reopened = PdfDocument.Open(scrubbed);
        reopened.Title.Should().BeNull();
        reopened.Author.Should().BeNull();
        reopened.GetXmpMetadata().Should().BeNull();
    }

    // ─── PDF builders ────────────────────────────────────────────────────────

    private static byte[] BuildPdfWithMetadata(
        string? title = null,
        string? author = null,
        string? subject = null,
        string? keywords = null,
        string? creator = null,
        string? producer = null,
        string? xmpBody = null,
        string? customInfoKey = null,
        string? customInfoValue = null)
    {
        var sb = new StringBuilder();
        var offsets = new long[8];
        void Mark(int n) => offsets[n] = sb.Length;

        sb.Append("%PDF-1.7\n");

        // Catalog: optionally references /Metadata 6
        Mark(1);
        sb.Append("1 0 obj <</Type/Catalog/Pages 2 0 R");
        if (xmpBody != null) sb.Append("/Metadata 6 0 R");
        sb.Append(">> endobj\n");

        Mark(2); sb.Append("2 0 obj <</Type/Pages/Count 1/Kids[3 0 R]>> endobj\n");
        Mark(3); sb.Append("3 0 obj <</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<<>>>> endobj\n");

        // Info dict (5)
        Mark(5);
        sb.Append("5 0 obj <<");
        if (title    != null) sb.Append($"/Title({title})");
        if (author   != null) sb.Append($"/Author({author})");
        if (subject  != null) sb.Append($"/Subject({subject})");
        if (keywords != null) sb.Append($"/Keywords({keywords})");
        if (creator  != null) sb.Append($"/Creator({creator})");
        if (producer != null) sb.Append($"/Producer({producer})");
        if (customInfoKey != null) sb.Append($"/{customInfoKey}({customInfoValue})");
        sb.Append(">> endobj\n");

        // Optional Metadata stream (6)
        if (xmpBody != null)
        {
            Mark(6);
            var xmpBytes = Encoding.UTF8.GetBytes(xmpBody);
            sb.Append($"6 0 obj <</Type/Metadata/Subtype/XML/Length {xmpBytes.Length}>>\nstream\n");
            sb.Append(xmpBody);
            sb.Append("\nendstream endobj\n");
        }

        var xrefPos = sb.Length;
        int objCount = xmpBody != null ? 7 : 6;
        sb.Append("xref\n0 ").Append(objCount).Append("\n0000000000 65535 f \n");
        for (int i = 1; i < objCount; i++)
        {
            if (i == 4) { sb.Append("0000000000 65535 f \n"); continue; }
            sb.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");
        }
        sb.Append("trailer <</Size ").Append(objCount).Append("/Root 1 0 R/Info 5 0 R>>\nstartxref\n")
          .Append(xrefPos).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] BuildPdfWithoutInfoDict()
    {
        var sb = new StringBuilder();
        var offsets = new long[5];
        void Mark(int n) => offsets[n] = sb.Length;
        sb.Append("%PDF-1.7\n");
        Mark(1); sb.Append("1 0 obj <</Type/Catalog/Pages 2 0 R>> endobj\n");
        Mark(2); sb.Append("2 0 obj <</Type/Pages/Count 1/Kids[3 0 R]>> endobj\n");
        Mark(3); sb.Append("3 0 obj <</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<<>>>> endobj\n");
        var xrefPos = sb.Length;
        sb.Append("xref\n0 4\n0000000000 65535 f \n");
        for (int i = 1; i <= 3; i++) sb.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer <</Size 4/Root 1 0 R>>\nstartxref\n").Append(xrefPos).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
