using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Core.Text.Segmentation;
using Excise.TestSupport;
using Xunit;

namespace Excise.Core.Tests.Operations;

/// <summary>
/// #1507 — an area redaction must not cost a PDF/A file its identity.
///
/// <para><b>The defect.</b> <c>RedactArea</c>'s default is the WHOLESALE
/// document-carrier strip (#897), which removed the catalog <c>/Metadata</c>
/// stream — on a PDF/A input, the XMP packet carrying <c>pdfaid:part</c>. Every
/// PDF/A part requires that stream and that identification (veraPDF
/// PDFA-1B/2B/4: <c>containsMetadata</c>, <c>containsPDFAIdentification</c>), so
/// drawing one box on one page turned an archival document into a file
/// conforming to nothing — with no error, no warning, and nothing in the output
/// to say so.</para>
///
/// <para><b>What these tests are and are not.</b> They pin excise's BEHAVIOUR:
/// what survives the strip, what does not, and that the reinstated packet cannot
/// carry text out of the old one. The CONFORMANCE VERDICT is veraPDF's and lives
/// in <c>PdfATests</c> and
/// <c>Excise.Rendering.Tests/Differential/PdfAConformanceConservationTests</c> —
/// a writer must not grade its own output. The fixtures here are hand-built PDFs
/// that no one claims conform (no OutputIntent, base-14 font); they exist so the
/// behaviour is readable without a JVM on the machine.</para>
///
/// <para><b>Every test also asserts the leak.</b> The strip's whole job is that
/// a term excise cannot NAME — an area redaction has no term — does not survive
/// in a positionless carrier. So each case checks the canary is gone from the
/// SAVED BYTES with <see cref="SavedPdfLeakScanner"/> (which searches inside
/// compressed streams), not just that the identification came back. A fix that
/// kept the packet by keeping its contents would pass a conformance test and
/// fail here, which is the correct order of priorities.</para>
/// </summary>
public class RedactAreaPdfAIdentityTests
{
    /// <summary>
    /// The canary. It is in the page text inside the box, in <c>/Info /Title</c>,
    /// in the XMP <c>dc:title</c>, and in a custom XMP schema — four carriers,
    /// one string, so one assertion covers them all.
    /// </summary>
    private const string Canary = "CANARYNAME";

    private const string PdfA2Identity =
        "<pdfaid:part>2</pdfaid:part><pdfaid:conformance>B</pdfaid:conformance>";

    /// <summary>The box, in content-stream coordinates, over the page-1 text run.</summary>
    private static PdfRectangle Box => new PdfRectangle(60, 690, 400, 730);

    [Fact]
    public void AreaRedaction_OnAPdfADocument_KeepsTheIdentification_AndStillStripsEveryCarrier()
    {
        using var doc = PdfDocument.Open(BuildFixture(PdfA2Identity));
        doc.TargetsPdfA.Should().BeTrue("the fixture's XMP carries pdfaid:part");
        doc.Title.Should().Contain(Canary, "a green run must not come from an empty fixture");

        doc.GetPage(1).RedactArea(Box);   // default: scrubDocumentCarriers = true

        // Asserted BEFORE the save, because this is what #1499's per-widget
        // appearance decision reads mid-pipeline. If the strip leaves the
        // document anonymous in memory, that fix is unreachable from RedactArea
        // no matter what the saved file looks like.
        doc.TargetsPdfA.Should().BeTrue(
            "the identification must be back in place by the time the strip returns");

        var saved = SaveToBytes(doc);

        SavedPdfLeakScanner.FindTerm(saved, Canary).Should().BeEmpty(
            "the strip's reason for existing: page glyphs, /Info /Title, XMP dc:title and the " +
            "custom XMP schema all named the same string, and an area redaction cannot name it " +
            "to scrub selectively — so all of them must be gone from the saved bytes");

        using var after = PdfDocument.Open(saved);
        after.TargetsPdfA.Should().BeTrue();

        var xmp = Encoding.UTF8.GetString(after.GetXmpMetadata()!);
        xmp.Should().Contain("<pdfaid:part>2</pdfaid:part>",
            "PDF/A requires the identification, and PdfDocumentWriter reads this exact element " +
            "form back to decide whether object streams are allowed");
        xmp.Should().Contain("<pdfaid:conformance>B</pdfaid:conformance>",
            "the conformance LEVEL is part of the claim — 2b is not 2a");

        var metadata = after.Resolve(after.Catalog.GetOptional("Metadata")!) as PdfStream;
        metadata.Should().NotBeNull("PDF/A: the catalog shall contain a /Metadata stream");
        metadata!.GetOptional("Filter").Should().BeNull(
            "PDF/A-1 forbids a /Filter on the catalog metadata stream (veraPDF PDFA-1B, " +
            "PDMetadata: isCatalogMetadata == false || Filter == null)");
    }

    /// <summary>
    /// The question a reviewer should ask about any fix that puts a carrier
    /// BACK: can redacted text ride in with it? The reinstated packet is built
    /// from at most three validated tokens, so nothing else from the original —
    /// not the title, not an unknown schema, not a date — is copied.
    /// </summary>
    [Fact]
    public void TheReinstatedPacket_CarriesTheIdentificationAndNothingElse()
    {
        using var doc = PdfDocument.Open(BuildFixture(PdfA2Identity));
        doc.GetPage(1).RedactArea(Box);

        var xmp = Encoding.UTF8.GetString(doc.GetXmpMetadata()!);

        foreach (var gone in new[] { "dc:title", "rdf:Alt", "acme", "example.invalid", Canary })
            xmp.Should().NotContain(gone,
                $"'{gone}' comes from the original packet; the reinstated one is built from the " +
                "validated pdfaid tokens alone, never copied");

        xmp.Should().Contain("dc:format",
            "the only other property emitted is the constant application/pdf");
        xmp.Should().NotContain("bytes=").And.NotContain("encoding=",
            "veraPDF XMPPackage rules: the bytes and encoding attributes shall not be used in " +
            "the header of an XMP packet");
    }

    /// <summary>
    /// #897 unchanged for everyone else: a document that never claimed PDF/A
    /// loses its whole XMP packet, exactly as before. The fix is an exception for
    /// an identification, not a general softening of the strip.
    /// </summary>
    [Fact]
    public void AreaRedaction_OnANonPdfADocument_StillDropsTheWholeXmpPacket()
    {
        using var doc = PdfDocument.Open(BuildFixture(pdfaIdentity: ""));
        doc.TargetsPdfA.Should().BeFalse();
        doc.GetXmpMetadata().Should().NotBeNull("sanity: there is a packet to drop");

        doc.GetPage(1).RedactArea(Box);

        doc.GetXmpMetadata().Should().BeNull(
            "with no identification to preserve there is nothing to write back, and the packet " +
            "is a carrier like any other");

        var saved = SaveToBytes(doc);
        SavedPdfLeakScanner.FindTerm(saved, Canary).Should().BeEmpty();
        using var after = PdfDocument.Open(saved);
        after.GetXmpMetadata().Should().BeNull();
    }

    /// <summary>
    /// PDF/A-4 differs in three ways that a "just keep pdfaid:part" fix would
    /// get wrong, so they are pinned together: <c>pdfaid:rev</c> is REQUIRED
    /// (veraPDF PDFA-4: <c>rev == "2020"</c>), <c>pdfaid:conformance</c> must be
    /// ABSENT (<c>conformance == null</c>), and the Info dictionary "shall not be
    /// present ... unless there exists a PieceInfo entry" — while if present it
    /// "shall only contain a ModDate entry". The strip empties the Info
    /// dictionary and keeps the object, which satisfies neither rule, so on the
    /// PDF/A path an emptied dictionary is dropped.
    /// </summary>
    [Fact]
    public void AreaRedaction_OnAPdfA4Document_KeepsRev_AddsNoConformance_AndDropsTheEmptiedInfoDict()
    {
        using var doc = PdfDocument.Open(BuildFixture(
            "<pdfaid:part>4</pdfaid:part><pdfaid:rev>2020</pdfaid:rev>",
            infoExtra: " /ModDate (D:20260101000000Z)"));

        doc.GetPage(1).RedactArea(Box);
        var saved = SaveToBytes(doc);

        using var after = PdfDocument.Open(saved);
        var xmp = Encoding.UTF8.GetString(after.GetXmpMetadata()!);
        xmp.Should().Contain("<pdfaid:part>4</pdfaid:part>");
        xmp.Should().Contain("<pdfaid:rev>2020</pdfaid:rev>",
            "PDF/A-4 requires the revision; dropping it withdraws the claim just as surely as " +
            "dropping the part");
        xmp.Should().NotContain("pdfaid:conformance",
            "PDF/A-4 forbids a conformance value — the identification is COPIED as found, never " +
            "completed with a level excise picked");

        after.Trailer.GetOptional("Info").Should().BeNull(
            "the strip emptied the Info dictionary; an empty one present in the trailer fails " +
            "ISO 19005-4 6.1.3, and an object with no entries carries nothing worth keeping");
        after.Info.Should().BeNull(
            "asserted both ways: the trailer key and the document's own view of it, since an " +
            "xref-stream trailer is surfaced by a different path than a classic one");
        SavedPdfLeakScanner.FindTerm(saved, Canary).Should().BeEmpty();
    }

    /// <summary>
    /// Fail-secure. <c>PdfDocument.TargetsPdfA</c> answers "does this document
    /// claim PDF/A" by looking for the <c>pdfaid:part</c> property at all — the
    /// right question when deciding not to EMIT something PDF/A forbids.
    /// Reinstating a claim is a stronger requirement: the value has to be one
    /// excise can validate. Where it is not, the claim is withdrawn rather than
    /// repaired, which is the pre-#1507 outcome and never a claim excise made up.
    /// </summary>
    [Fact]
    public void AreaRedaction_WithAnUnreadableIdentification_WithdrawsTheClaim_RatherThanInventOne()
    {
        using var doc = PdfDocument.Open(BuildFixture(
            "<pdfaid:part>9</pdfaid:part><pdfaid:conformance>Z</pdfaid:conformance>"));
        doc.TargetsPdfA.Should().BeTrue("the property is present, which is all TargetsPdfA asks");

        doc.GetPage(1).RedactArea(Box);

        doc.GetXmpMetadata().Should().BeNull(
            "part 9 is not a part of ISO 19005; excise does not know what file this is, so it " +
            "writes no identification at all");
        SavedPdfLeakScanner.FindTerm(SaveToBytes(doc), Canary).Should().BeEmpty();
    }

    /// <summary>
    /// <c>RedactAreas</c> strips once per call and <c>RedactArea</c> may be
    /// called repeatedly on the same document, so the read-strip-rewrite has to
    /// be idempotent: the second pass reads the identification out of the packet
    /// the first pass wrote, and no orphan packet may accumulate in the file.
    /// </summary>
    [Fact]
    public void RepeatedAreaRedactions_KeepOneIdentificationPacket()
    {
        using var doc = PdfDocument.Open(BuildFixture(PdfA2Identity));

        var act = () => doc.GetPage(1).RedactAreas(new[]
        {
            Box,
            new PdfRectangle(60, 600, 400, 640),
            new PdfRectangle(60, 500, 400, 540),
        });
        act.Should().NotThrow();
        doc.GetPage(1).RedactArea(new PdfRectangle(60, 400, 400, 440));

        var saved = SaveToBytes(doc);
        doc.TargetsPdfA.Should().BeTrue();
        SavedPdfLeakScanner.FindTerm(saved, Canary).Should().BeEmpty();

        var text = Encoding.Latin1.GetString(saved);
        CountOccurrences(text, "<pdfaid:part>").Should().Be(1,
            "each pass replaces the packet; a second copy would mean the superseded one is still " +
            "reachable, which for a real document means its dc:title is still in the file");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static byte[] SaveToBytes(PdfDocument doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// One page whose text run sits inside <see cref="Box"/>, plus the three
    /// positionless carriers that name the same string: <c>/Info /Title</c>, the
    /// XMP <c>dc:title</c>, and a custom XMP schema (the case that makes a
    /// selective XMP scrub impossible — excise has never heard of this schema
    /// and, with no term, could not find the string in it anyway).
    /// </summary>
    /// <param name="pdfaIdentity">The pdfaid properties to put in the packet;
    /// empty for a document that makes no PDF/A claim.</param>
    /// <param name="infoExtra">Extra raw entries for the Info dictionary.</param>
    private static byte[] BuildFixture(string pdfaIdentity, string infoExtra = "")
    {
        var content = $"BT /F1 24 Tf 72 700 Td ({Canary} appears here) Tj ET";

        var identityDescription = pdfaIdentity.Length == 0 ? "" :
            "<rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\">"
            + pdfaIdentity + "</rdf:Description>";

        var xmp =
            "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>" +
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF " +
            "xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
            identityDescription +
            "<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\">" +
            $"<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">{Canary} in XMP title</rdf:li>" +
            "</rdf:Alt></dc:title></rdf:Description>" +
            "<rdf:Description rdf:about=\"\" xmlns:acme=\"http://example.invalid/ns/acme/\">" +
            $"<acme:note>{Canary} in a schema excise has never heard of</acme:note>" +
            "</rdf:Description>" +
            "</rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";

        var objects = new List<string>
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R /Metadata 6 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 4 0 R " +
            "/Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n",
            "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n",
            $"6 0 obj\n<< /Type /Metadata /Subtype /XML /Length {xmp.Length} >>\nstream\n{xmp}\nendstream\nendobj\n",
            $"7 0 obj\n<< /Title ({Canary} in Info title){infoExtra} >>\nendobj\n",
        };

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        foreach (var o in objects) { offsets.Add(sb.Length); sb.Append(o); }
        var xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Count + 1)
          .Append(" /Root 1 0 R /Info 7 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
