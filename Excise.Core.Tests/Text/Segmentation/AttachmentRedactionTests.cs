using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Core.Text.Segmentation;
using Excise.TestSupport;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// #1572 and the 2026-09-17 decision that redacted output carries no
/// attachments unless the caller keeps them.
/// </summary>
/// <remarks>
/// <para>The fixture attaches files by six routes at once: the catalog name
/// tree, the catalog <c>/AF</c>, a page <c>/AF</c>, a <c>/FileAttachment</c>
/// annotation (secret in the payload, <c>/Desc</c> and <c>/Contents</c>), a
/// Sound annotation, and a link whose <c>/GoToE</c> action names an embedded
/// file specification that nothing else lists. The annotation is also held by
/// a structure element's <c>/OBJR</c>, which keeps it reachable after it
/// leaves <c>/Annots</c>. None of the payloads contains the page text.</para>
/// <para>Every removal claim is checked on the SAVED BYTES, compressed
/// streams inflated, by <see cref="SavedPdfLeakScanner"/> — not by excise's
/// own attachment parser. The qpdf/pdfdetach cross-check lives in
/// Excise.Rendering.Tests (<c>AttachmentRemovalOracleTests</c>).</para>
/// </remarks>
public class AttachmentRedactionTests
{
    internal const string PageText = "Public page text";
    internal const string DocSecret = "DOCPAYLOAD-7Q2";
    internal const string CatalogAfSecret = "CATAFPAYLOAD-7Q2";
    internal const string PageAfSecret = "PAGEAFPAYLOAD-7Q2";
    internal const string AnnotSecret = "ANNOTPAYLOAD-7Q2";
    internal const string AnnotDescSecret = "ANNOTDESC-7Q2";
    internal const string AnnotContentsSecret = "ANNOTNOTE-7Q2";
    internal const string SoundSecret = "SOUNDPAYLOAD-7Q2";
    internal const string GoToESecret = "GOTOEPAYLOAD-7Q2";

    internal static readonly string[] PayloadSecrets =
    {
        DocSecret, CatalogAfSecret, PageAfSecret, AnnotSecret, AnnotDescSecret, SoundSecret, GoToESecret,
    };

    /// <summary>The six-route fixture described on the class.</summary>
    internal static byte[] BuildAllRoutesPdf()
    {
        var content = $"BT /F1 12 Tf 72 700 Td ({PageText}) Tj ET";
        return RawPdf(
            // 1 catalog
            "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles << /Names [(doc.txt) 10 0 R] >> >> " +
                "/AF [12 0 R] /StructTreeRoot 20 0 R /MarkInfo << /Marked true >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                "/Resources << /Font << /F1 5 0 R >> >> /Annots [6 0 R 16 0 R 17 0 R] /AF [14 0 R] /StructParents 0 >>",
            Stream("", content),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            // 6 file attachment annotation
            $"<< /Type /Annot /Subtype /FileAttachment /Rect [72 600 92 620] /Contents ({AnnotContentsSecret}) " +
                "/FS 7 0 R /Name /PushPin /StructParent 1 >>",
            $"<< /Type /Filespec /F (annot.txt) /UF (annot.txt) /Desc ({AnnotDescSecret}) /EF << /F 8 0 R >> >>",
            Stream("/Type /EmbeddedFile /Subtype /text#2Fplain /Params << /Size 24 >>", $"ANNOT: {AnnotSecret} ..."),
            "<< >>",
            // 10 document-level file
            "<< /Type /Filespec /F (doc.txt) /UF (doc.txt) /EF << /F 11 0 R >> >>",
            Stream("/Type /EmbeddedFile", $"DOC: {DocSecret}"),
            // 12 catalog /AF file (not in the name tree)
            "<< /Type /Filespec /F (catalog-af.xml) /UF (catalog-af.xml) /AFRelationship /Data /EF << /F 13 0 R >> >>",
            Stream("/Type /EmbeddedFile", $"<x>{CatalogAfSecret}</x>"),
            // 14 page /AF file
            "<< /Type /Filespec /F (page-af.csv) /UF (page-af.csv) /AFRelationship /Supplement /EF << /F 15 0 R >> >>",
            Stream("/Type /EmbeddedFile", $"a,b\n{PageAfSecret},2\n"),
            // 16 sound annotation, 17 link with /GoToE into an unlisted embedded file
            "<< /Type /Annot /Subtype /Sound /Rect [100 600 120 620] /Sound 18 0 R >>",
            "<< /Type /Annot /Subtype /Link /Rect [200 600 300 620] /A << /S /GoToE /T << /R /C /N (x) >> " +
                "/F 19 0 R >> >>",
            Stream("/Type /Sound /R 8000", $"RIFF {SoundSecret}"),
            "<< /Type /Filespec /F (gotoe.bin) /UF (gotoe.bin) /EF << /F 21 0 R >> >>",
            // 20 structure tree holding the annotation through /OBJR
            "<< /Type /StructTreeRoot /K 22 0 R >>",
            Stream("/Type /EmbeddedFile", $"opaque bytes {GoToESecret}"),
            "<< /Type /StructElem /S /Annot /P 20 0 R /K << /Type /OBJR /Obj 6 0 R /Pg 3 0 R >> >>");
    }

    [Fact]
    public void Fixture_CarriesEverySecret_BeforeAnything()
    {
        var input = BuildAllRoutesPdf();
        foreach (var secret in PayloadSecrets.Append(AnnotContentsSecret))
            SavedPdfLeakScanner.FindTerm(input, secret).Should().NotBeEmpty($"guard: {secret} must be in the input");
    }

    [Fact]
    public void ScrubEmbeddedFiles_RemovesEveryRoute_FromTheSavedBytes()
    {
        using var doc = PdfDocument.Open(BuildAllRoutesPdf());

        doc.ScrubEmbeddedFiles();
        var saved = doc.SaveToBytes();

        foreach (var secret in PayloadSecrets)
            SavedPdfLeakScanner.FindTerm(saved, secret).Should().BeEmpty(
                $"{secret}: ScrubEmbeddedFiles must leave no attachment payload or description (#1572)");
        using var reopened = PdfDocument.Open(saved);
        reopened.GetEmbeddedFiles().Should().BeEmpty();
        reopened.GetPage(1).GetAnnotations().Select(a => a.Subtype)
            .Should().Equal(new[] { PdfAnnotationSubtype.Link }, "only the link annotation remains");
        SavedPdfLeakScanner.FindTerm(saved, PageText).Should().NotBeEmpty("the page itself is untouched");
    }

    [Fact]
    public void RemoveAllAttachments_NamesEachFile_WithSizeAndLocation()
    {
        using var doc = PdfDocument.Open(BuildAllRoutesPdf());

        var removed = doc.RemoveAllAttachments();

        removed.Select(r => r.Name).Should().BeEquivalentTo(
            new[] { "doc.txt", "catalog-af.xml", "page-af.csv", "annot.txt", "(sound clip)", "gotoe.bin" });
        removed.Should().OnlyContain(r => r.Disposition == AttachmentDisposition.Removed);
        removed.Single(r => r.Name == "annot.txt").SizeBytes.Should().Be(24, "/Params /Size is the declared size");
        removed.Single(r => r.Name == "doc.txt").SizeBytes.Should().Be(Encoding.ASCII.GetByteCount($"DOC: {DocSecret}"));
        removed.Single(r => r.Name == "annot.txt").Location.Should().Be("page 1 (file attachment annotation)");
        removed.Single(r => r.Name == "gotoe.bin").Location.Should().StartWith("elsewhere in the document");
        doc.RemoveAllAttachments().Should().BeEmpty("the scrub is idempotent");
    }

    [Fact]
    public void ScrubEmbeddedFilesReversibly_RestoresEveryRoute()
    {
        var input = BuildAllRoutesPdf();
        using var doc = PdfDocument.Open(input);

        var (removed, restore) = doc.ScrubEmbeddedFilesReversibly();
        removed.Should().HaveCount(6);
        restore();

        var saved = doc.SaveToBytes();
        foreach (var secret in PayloadSecrets)
            SavedPdfLeakScanner.FindTerm(saved, secret).Should().NotBeEmpty($"{secret}: undo must restore it");
        doc.GetPage(1).GetAnnotations().Should().HaveCount(3, "the removed annotations are back in /Annots");
    }

    // ── redaction entry points: removal is the default ─────────────────────

    [Fact]
    public void RedactText_RemovesEveryAttachment_ByDefault_EvenWithoutATermInThem()
    {
        using var doc = PdfDocument.Open(BuildAllRoutesPdf());

        var report = doc.RedactText("Public");
        var saved = doc.SaveToBytes();

        foreach (var secret in PayloadSecrets.Append(AnnotContentsSecret))
            SavedPdfLeakScanner.FindTerm(saved, secret).Should().BeEmpty(secret);
        report.Attachments.Should().HaveCount(6).And.OnlyContain(a => a.Disposition == AttachmentDisposition.Removed);
        report.IsCleanSuccess.Should().BeTrue("removed attachments are a clean outcome");
        report.ToString().Should().Contain("6 attachment(s) removed");
    }

    [Fact]
    public void RedactArea_RemovesEveryAttachment_ByDefault_AndRecordsThemForTheSafetyReport()
    {
        using var doc = PdfDocument.Open(BuildAllRoutesPdf());

        doc.GetPage(1).RedactArea(new PdfRectangle(60, 690, 300, 720));
        var report = RedactedCopySafetyPolicy.Evaluate(doc, RedactedCopySafetyRequest.ForAreas(
            new[] { new RedactedCopySafetyArea(1, PdfPageRect.FromContentPoints(1, new PdfRectangle(60, 690, 300, 720))) }));

        foreach (var secret in PayloadSecrets.Append(AnnotContentsSecret))
            SavedPdfLeakScanner.FindTerm(doc.SaveToBytes(), secret).Should().BeEmpty(secret);
        report.AttachmentResults.Should().HaveCount(6,
            "the area pass removed them before the report ran; the report must still name them (#1572)");
        report.EmbeddedFileCountBefore.Should().Be(6);
    }

    [Fact]
    public void RedactArea_BoolOverloadWithCarriersOff_KeepsAttachments()
    {
        using var doc = PdfDocument.Open(BuildAllRoutesPdf());

        doc.GetPage(1).RedactArea(new PdfRectangle(60, 690, 300, 720), scrubDocumentCarriers: false);

        SavedPdfLeakScanner.FindTerm(doc.SaveToBytes(), DocSecret).Should().NotBeEmpty(
            "the caller opted out of every document-level strip");
    }

    // ── portfolios ────────────────────────────────────────────────────────

    private static byte[] PortfolioPdf()
    {
        using var doc = PdfDocument.Open(BuildAllRoutesPdf());
        doc.Catalog["Collection"] = new PdfDictionary { ["View"] = new PdfName("D") };
        return doc.SaveToBytes();
    }

    [Fact]
    public void Portfolio_IsRefused_BeforeAnythingChanges()
    {
        var input = PortfolioPdf();
        using var doc = PdfDocument.Open(input);

        var text = () => doc.RedactText("Public");
        text.Should().Throw<PdfPortfolioRedactionException>().WithMessage("*portfolio*");
        var area = () => doc.GetPage(1).RedactArea(new PdfRectangle(60, 690, 300, 720));
        area.Should().Throw<PdfPortfolioRedactionException>();
        var copy = () => RedactedCopySafetyPolicy.Evaluate(doc, RedactedCopySafetyRequest.ForTerms(new[] { "Public" }));
        copy.Should().Throw<PdfPortfolioRedactionException>();

        var saved = doc.SaveToBytes();
        SavedPdfLeakScanner.FindTerm(saved, PageText).Should().NotBeEmpty("the refused redaction changed nothing");
        SavedPdfLeakScanner.FindTerm(saved, DocSecret).Should().NotBeEmpty();
    }

    [Fact]
    public void Portfolio_WithKeepAttachments_IsRedactedMemberByMember()
    {
        using var doc = PdfDocument.Open(PortfolioPdf());

        var report = doc.RedactText("Public", new RedactionOptions { KeepAttachments = true });

        report.VerifiedRemovals.Should().Be(1);
        report.Attachments.Should().NotBeEmpty();
    }

    // ── kept attachments ──────────────────────────────────────────────────

    private const string Term = "Quillfeather";

    private static byte[] KeptFixture(PdfDocument? nested = null)
    {
        using var doc = PdfDocument.Open(BuildAllRoutesPdf());
        var names = new PdfArray();
        void Add(string name, byte[] bytes, string? mime = null, string? desc = null)
        {
            var stream = new PdfStream(bytes);
            stream.SetName("Type", "EmbeddedFile");
            if (mime != null) stream.SetName("Subtype", mime);
            var parameters = new PdfDictionary();
            parameters.SetInt("Size", bytes.Length);
            parameters.SetString("CheckSum", "0123456789abcdef");
            stream["Params"] = parameters;
            var fs = new PdfDictionary
            {
                ["Type"] = new PdfName("Filespec"),
                ["F"] = new PdfString(name),
                ["UF"] = new PdfString(name),
                ["EF"] = new PdfDictionary { ["F"] = doc.AddIndirectObject(stream) },
            };
            if (desc != null) fs["Desc"] = new PdfString(desc);
            names.Add((PdfObject)new PdfString(name));
            names.Add(doc.AddIndirectObject(fs));
        }

        Add("notes.txt", Encoding.UTF8.GetBytes($"Call Zanzibar {Term} tomorrow"));
        Add("clean.csv", Encoding.UTF8.GetBytes("a,b\n1,2\n"));
        Add("utf16.xml", Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes($"<n>{Term}</n>")).ToArray());
        Add("photo.png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 }, mime: "image/png");
        Add($"{Term}-named.bin", new byte[] { 1, 2, 3 });
        if (nested != null)
            Add("inner.pdf", nested.SaveToBytes(), mime: "application/pdf");

        var catalogNames = (PdfDictionary)doc.Resolve(doc.Catalog.GetOptional("Names")!);
        catalogNames["EmbeddedFiles"] = new PdfDictionary { ["Names"] = names };
        return doc.SaveToBytes();
    }

    private static byte[] InnerPdfBytes()
    {
        var content = $"BT /F1 12 Tf 72 700 Td (Inner {Term} page) Tj ET";
        return RawPdf(
            "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles << /Names [(deep.txt) 6 0 R] >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                "/Resources << /Font << /F1 5 0 R >> >> >>",
            Stream("", content),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /Filespec /F (deep.txt) /UF (deep.txt) /EF << /F 7 0 R >> >>",
            Stream("/Type /EmbeddedFile", $"deep {Term} text"));
    }

    [Fact]
    public void KeepAttachments_TextFilesAreRedacted_OthersAreReported_AndTheResultIsNotClean()
    {
        using var inner = PdfDocument.Open(InnerPdfBytes());
        var input = KeptFixture(inner);
        SavedPdfLeakScanner.FindTerm(input, Term).Should().NotBeEmpty("guard");

        using var doc = PdfDocument.Open(input);
        var report = doc.RedactText(Term, new RedactionOptions { KeepAttachments = true });
        var saved = doc.SaveToBytes();

        SavedPdfLeakScanner.FindTerm(saved, Term).Should().BeEmpty(
            "text attachments are cut, the nested PDF is redacted, and the file NAMED after the term is removed");
        SavedPdfLeakScanner.FindTerm(saved, "Call Zanzibar").Should().NotBeEmpty("only the term is cut from notes.txt");
        SavedPdfLeakScanner.FindTerm(saved, "0123456789abcdef").Should().NotBeEmpty(
            "an untouched file keeps its checksum");

        var byName = report.Attachments.ToDictionary(a => a.Name);
        byName["notes.txt"].Disposition.Should().Be(AttachmentDisposition.KeptTermRemoved);
        byName["notes.txt"].SizeBytes.Should().Be(Encoding.UTF8.GetByteCount("Call Zanzibar  tomorrow"),
            "/Params /Size follows the rewritten content");
        byName["clean.csv"].Disposition.Should().Be(AttachmentDisposition.KeptTermNotFound);
        byName["utf16.xml"].Disposition.Should().Be(AttachmentDisposition.KeptTermRemoved, "BOM-marked UTF-16 is decoded");
        byName["photo.png"].Disposition.Should().Be(AttachmentDisposition.KeptNotChecked);
        byName[$"{Term}-named.bin"].Disposition.Should().Be(AttachmentDisposition.Removed,
            "the term-based carrier scrub drops a file whose name holds the term, and the report says so");
        byName["inner.pdf"].Disposition.Should().Be(AttachmentDisposition.KeptTermRemoved);
        byName["annot.txt"].Disposition.Should().Be(AttachmentDisposition.KeptTermNotFound);

        report.IsCleanSuccess.Should().BeFalse("photo.png and the sound clip were not checked");
        report.ToString().Should().Contain("photo.png").And.Contain("NOT checked");

        using var reopened = PdfDocument.Open(saved);
        var innerAfter = reopened.GetEmbeddedFiles().Single(f => f.FileName == "inner.pdf");
        using var innerDoc = PdfDocument.Open(innerAfter.Bytes!);
        innerDoc.GetPage(1).Text.Should().NotContain(Term).And.Contain("Inner");
        innerDoc.GetEmbeddedFiles().Should().ContainSingle("the nested PDF keeps ITS attachments too")
            .Which.Bytes.Should().NotBeNull();
        SavedPdfLeakScanner.FindTerm(innerAfter.Bytes!, Term).Should().BeEmpty("the nested PDF's own attachment is redacted");
    }

    [Fact]
    public void KeepAttachments_ChecksumOfARewrittenFileIsDropped()
    {
        using var doc = PdfDocument.Open(KeptFixture());
        doc.RedactText(Term, new RedactionOptions { KeepAttachments = true });

        var notes = doc.GetEmbeddedFiles().Single(f => f.FileName == "notes.txt");
        var stream = (PdfStream)doc.Resolve(((PdfDictionary)doc.Resolve(notes.RawDictionary["EF"]))["F"]);
        var parameters = (PdfDictionary)doc.Resolve(stream["Params"]);
        parameters.ContainsKey("CheckSum").Should().BeFalse("an MD5 of the original content would confirm a guess");
    }

    [Fact]
    public void KeepAttachments_APasswordProtectedNestedPdf_IsRefused_AndNothingChanges()
    {
        // excise opens only an empty user password; a member protected by a
        // real one cannot be redacted, so the whole redaction is refused.
        var locked = PdfDocument.CreateNew();
        locked.Pages.AddBlank(612, 792);
        var lockedBytes = locked.SaveToBytes(new Excise.Core.Security.PdfEncryptionOptions
        {
            UserPassword = "member-secret",
            OwnerPassword = "member-owner",
        });
        locked.Dispose();
        var openLocked = () => PdfDocument.Open(lockedBytes);
        openLocked.Should().Throw<Exception>("guard: the member must not open without its password");

        using var doc = PdfDocument.Open(BuildAllRoutesPdf());
        var payload = new PdfStream(lockedBytes);
        var fs = new PdfDictionary
        {
            ["F"] = new PdfString("locked.pdf"),
            ["EF"] = new PdfDictionary { ["F"] = doc.AddIndirectObject(payload) },
        };
        doc.Catalog["AF"] = new PdfArray(doc.AddIndirectObject(fs));

        var act = () => doc.RedactText("Public", new RedactionOptions { KeepAttachments = true });

        act.Should().Throw<AttachmentRedactionRefusedException>().Which.AttachmentName.Should().Be("locked.pdf");
        SavedPdfLeakScanner.FindTerm(doc.SaveToBytes(), PageText).Should().NotBeEmpty(
            "the refusal came before the page was touched");
    }

    [Fact]
    public void KeepAttachments_OnAreaRedaction_ReportsEveryKeptFileAsNotChecked()
    {
        using var doc = PdfDocument.Open(BuildAllRoutesPdf());
        var area = new PdfRectangle(60, 690, 300, 720);

        doc.GetPage(1).RedactArea(area, new RedactionOptions { KeepAttachments = true });
        var report = RedactedCopySafetyPolicy.Evaluate(doc, RedactedCopySafetyRequest.ForAreas(
            new[] { new RedactedCopySafetyArea(1, PdfPageRect.FromContentPoints(1, area)) },
            options: new RedactedCopySafetyOptions { ScrubAttachments = false }));

        SavedPdfLeakScanner.FindTerm(doc.SaveToBytes(), DocSecret).Should().NotBeEmpty("kept");
        report.AttachmentsScrubbed.Should().BeFalse();
        report.AttachmentResults.Should().HaveCount(6)
            .And.OnlyContain(a => a.Disposition == AttachmentDisposition.KeptNotChecked);
        report.HasWarnings.Should().BeTrue("a kept file nobody checked must not pass silently");
        report.Warnings.Should().Contain(w => w.Contains("doc.txt") && w.Contains("NOT checked"));
    }

    // ── fixture plumbing ──────────────────────────────────────────────────

    internal static string Stream(string dictionary, string data)
        => $"<< {dictionary} /Length {Encoding.Latin1.GetByteCount(data)} >>\nstream\n{data}\nendstream";

    internal static byte[] RawPdf(params string[] bodies)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var i = 0; i < bodies.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        var xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            sb.Append(offset.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
