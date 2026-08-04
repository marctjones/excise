using System;
using System.IO;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Core.Text;
using Xunit;

namespace Excise.Core.Tests.Parsing;

/// <summary>
/// Five corpus pages that mutool and/or pdftocairo open and excise refused
/// (#884), plus the one document three independent readers open and excise alone
/// could not (#869). Each mechanism is pinned twice: once on a hand-built
/// fixture that reproduces it in a few hundred bytes, and once end-to-end on the
/// corpus file it was found in.
///
/// The redaction half of these changes — "having opened more, does redaction
/// still reach the text" — lives in
/// <c>Text/Segmentation/TolerantParsePathRedactionTests</c>. Both halves are the
/// gate; opening more files is worthless if redaction silently reaches less.
/// </summary>
public class XRefRecoveryRegressionTests
{
    private const string Bug1978317 = "../../../../test-pdfs/pdfjs/bug1978317.pdf";
    private const string EmbeddedImages = "../../../../test-pdfs/pdfium/embedded_images.pdf";
    private const string Bug452455 = "../../../../test-pdfs/pdfium/bug_452455.pdf";

    // ── #869: type-2 xref entries must resolve BY OBJECT NUMBER ──────────────

    [Fact]
    public void CompressedObject_XRefIndexTruncatedByNarrowW_ResolvesTheRequestedObject()
    {
        // /W [1 3 1] gives the index-in-stream field one byte, so the catalog's
        // real slot (260) is recorded as 4 — and slot 4 holds a link annotation.
        // Positional resolution returns that annotation AS THE CATALOG with no
        // error at all, which is the whole point: this failure is silent.
        var pdf = MalformedXRefFixtures.BuildNarrowXRefWidthObjStmPdf();

        using var doc = PdfDocument.Open(new MemoryStream(pdf));

        doc.Catalog.GetNameOrNull("Type").Should().Be("Catalog",
            "the type-2 entry must resolve to object 1, not to whatever occupies " +
            $"slot {MalformedXRefFixtures.DecoySlot}");
        doc.Catalog.GetNameOrNull("Subtype").Should().BeNull(
            "a /Subtype /Link on the catalog means the decoy annotation was returned");
        doc.PageCount.Should().Be(1);
    }

    [Fact]
    public void CompressedObject_XRefIndexTruncatedByNarrowW_DecoyStillResolvesToItself()
    {
        // The other side of the same coin: resolving by object number must not
        // break the objects whose narrow index happens to be correct.
        var pdf = MalformedXRefFixtures.BuildNarrowXRefWidthObjStmPdf();

        using var doc = PdfDocument.Open(new MemoryStream(pdf));

        var decoy = doc.GetObject(MalformedXRefFixtures.DecoyObjectNumber)
            .Should().BeOfType<PdfDictionary>().Subject;
        decoy.GetNameOrNull("Subtype").Should().Be("Link");
    }

    [Fact]
    public void CompressedObject_ObjectStreamDoesNotContainIt_ResolvesToNullNotToAnotherObject()
    {
        // An /ObjStm that does not carry the requested object must yield null.
        // Returning the object that happens to sit at the xref's index is the
        // #869 defect in its general form — a confidently wrong object that
        // nothing downstream can detect.
        var pdf = MalformedXRefFixtures.BuildNarrowXRefWidthObjStmPdf();

        using var doc = PdfDocument.Open(new MemoryStream(pdf));

        // Object 266's xref entry is type 2 and points into the object stream,
        // but the stream's index never names object 266. Positionally that
        // resolves to slot 0's filler dictionary.
        doc.GetObject(266).Should().BeOfType<PdfNull>(
            "an /ObjStm that does not carry the requested object must yield null, " +
            "not whatever sits at the index the xref guessed");
    }

    [Fact]
    public void Open_Bug1978317_ReadsOnePage_AsQpdfMutoolAndPdftocairoDo()
    {
        // The corpus file the fixture above is modelled on: /W [1 3 2] over
        // 65,564 objects, catalog at slot 65541 of /ObjStm 65547, recorded as 5.
        // qpdf --show-npages, mutool info and pdftocairo all report one page.
        Assert.SkipWhen(!File.Exists(Bug1978317), "pdf.js corpus fixture not available");

        using var doc = PdfDocument.Open(Bug1978317);

        doc.Catalog.GetNameOrNull("Type").Should().Be("Catalog");
        doc.PageCount.Should().Be(1);
        new TextExtractor(doc.GetPage(1)).ExtractText().Length.Should().BeGreaterThan(1000,
            "the page's text must come back too — resolving the catalog is only " +
            "worth something if the page tree under it is the real one");
    }

    // ── #884: an assembled xref that cannot reach /Root must be rebuilt ───────

    [Fact]
    public void Open_TerminalEmptyXRefAndPrevPastEof_ReconstructsFromIndirectObjects()
    {
        // Truncated file: startxref and /Prev both point past EOF and the only
        // section present is an empty "xref 0 0". The repair path parses that
        // happily and "succeeds" with zero entries plus a healthy-looking /Root,
        // so reconstruction was never reached — although the objects are all
        // intact and reconstruction works.
        var pdf = MalformedXRefFixtures.BuildTruncatedTerminalXRefPdf();

        using var doc = PdfDocument.Open(new MemoryStream(pdf));

        doc.PageCount.Should().Be(1);
        new TextExtractor(doc.GetPage(1)).ExtractText().Should().Contain("SECRET");
    }

    [Fact]
    public void Open_HealthyIncrementalUpdate_DoesNotTriggerReconstruction()
    {
        // The reason the /Root check lives after the /Prev walk and not inside
        // XRefParser: one xref SECTION of an incrementally-updated file
        // legitimately omits the catalog. Asking a single section would condemn
        // — or needlessly rebuild — a large class of healthy files.
        var pdf = BuildIncrementallyUpdatedPdf();

        using var doc = PdfDocument.Open(new MemoryStream(pdf));

        doc.PageCount.Should().Be(1);
        doc.Catalog.GetNameOrNull("Type").Should().Be("Catalog");
        doc.Info!.GetStringOrNull("Title").Should().Be("second revision",
            "the update's own objects must win — a rebuild that merged over them " +
            "would rewind the document to a superseded revision");

        doc.GetObject(7).Should().BeOfType<PdfNull>(
            "object 7 exists in the file's bytes but the update marks it FREE. " +
            "Header-scanning reconstruction cannot see that — so it must not run " +
            "here, and where it does run it must only fill gaps, never override " +
            "what the real xref decided");
    }

    [Fact]
    public void Open_EmbeddedImages_ReadsOnePage_AsMutoolAndQpdfDo()
    {
        // 34,279 bytes of a file whose tail was cut off: startxref 124724,
        // /Prev 123786, /XRefStm 123449 — all past EOF. mutool reports
        // "repairing PDF document" and Pages: 1; qpdf reconstructs and reports 1.
        Assert.SkipWhen(!File.Exists(EmbeddedImages), "PDFium corpus fixture not available");

        using var doc = PdfDocument.Open(EmbeddedImages);

        doc.PageCount.Should().Be(1);
        new TextExtractor(doc.GetPage(1)).ExtractText().Trim().Should().NotBeEmpty();
    }

    // ── #884: unterminated streams ───────────────────────────────────────────

    [Fact]
    public void GetObject_UnterminatedStream_RecoversItsBodyInsteadOfFailing()
    {
        var pdf = MalformedXRefFixtures.BuildUnterminatedHugeLengthStreamPdf();

        using var doc = PdfDocument.Open(new MemoryStream(pdf));

        var stream = doc.GetObject(6).Should().BeOfType<PdfStream>().Subject;
        var recovered = Encoding.Latin1.GetString(stream.DecodedData);

        recovered.Should().Contain(MalformedXRefFixtures.UnterminatedStreamBody,
            "the stream's own body is what the recovery is for");
        recovered.Should().NotContain(MalformedXRefFixtures.TailMarker,
            "scanning to EOF would fold every FOLLOWING object into this stream. " +
            "For a redaction tool that is a leak: the swallowed copy belongs to a " +
            "different object that no glyph pass ever looks at");
    }

    [Fact]
    public void GetObject_HugeDeclaredLength_DoesNotAllocateTheDeclaredLength()
    {
        // /Length 536870911 in a file of about a kilobyte. Allocating the
        // declared length is a memory-exhaustion lever handed to whoever wrote
        // the file, and it bought nothing: the read is truncated at EOF anyway.
        var pdf = MalformedXRefFixtures.BuildUnterminatedHugeLengthStreamPdf();
        pdf.Length.Should().BeLessThan(4096, "the fixture must stay tiny for this to mean anything");

        using var doc = PdfDocument.Open(new MemoryStream(pdf));

        var before = GC.GetAllocatedBytesForCurrentThread();
        doc.GetObject(6).Should().BeOfType<PdfStream>();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.Should().BeLessThan(16L * 1024 * 1024,
            $"a {pdf.Length}-byte file must not be able to demand a 512 MB buffer; " +
            $"actually allocated {allocated / 1024} KB");
    }

    [Fact]
    public void Open_Bug452455_ResolvesTheUnterminatedFunctionStream()
    {
        Assert.SkipWhen(!File.Exists(Bug452455), "PDFium corpus fixture not available");

        using var doc = PdfDocument.Open(Bug452455);
        doc.PageCount.Should().Be(1);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var stream = doc.GetObject(17).Should().BeOfType<PdfStream>(
            "the /Length 536870911 function stream has no endstream anywhere; " +
            "recovering it must not throw").Subject;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.Should().BeLessThan(16L * 1024 * 1024);

        // Which bytes come back is the whole point of the shorter-extent rule,
        // so assert on them: "PdfStream, not an exception" would also pass on
        // the over-long extent that swallows the rest of the file.
        var recovered = Encoding.Latin1.GetString(stream.DecodedData);
        recovered.Should().Contain("no end stream keyword",
            "the object's own body must survive");
        recovered.Should().NotContain("Halftone",
            "object 16 follows this one in the file; an unbounded scan would fold " +
            "it into this stream");
    }

    // ── a well-formed stream must still come back byte-identical ─────────────

    [Theory]
    [InlineData(37)]        // shorter than the lexer's 8 KB buffer
    [InlineData(8192)]      // exactly the buffer
    [InlineData(40_000)]    // several buffers
    public void ReadStreamData_WellFormedStream_ReturnsExactlyTheDeclaredBytes(int size)
    {
        // Bounding the stream buffer by "bytes remaining" means computing the
        // remaining distance from the LOGICAL position, which is the underlying
        // stream's cursor minus the unread part of the lexer buffer. Getting
        // that arithmetic wrong by the buffered amount would silently return
        // SHORT data on a perfectly healthy stream — which for a redaction tool
        // is text excise never sees and therefore never removes.
        var payload = new byte[size];
        for (int i = 0; i < size; i++)
            payload[i] = (byte)(i % 251);

        using var ms = new MemoryStream();
        void W(string s) { var b = Encoding.Latin1.GetBytes(s); ms.Write(b, 0, b.Length); }

        // Tokens before the stream so the lexer's buffer is partially consumed
        // at the moment ReadStreamData is called.
        W("9 0 obj\n<< /Length " + size + " >>\nstream\n");
        ms.Write(payload, 0, payload.Length);
        W("\nendstream\nendobj\n");

        ms.Position = 0;
        using var parser = new Excise.Core.Parsing.PdfParser(ms.ToArray());
        var parsed = parser.ParseIndirectObject().Value.Should().BeOfType<PdfStream>().Subject;

        parsed.EncodedData.Should().Equal(payload,
            "a well-formed stream must round-trip byte-identically");
    }

    // ---------------------------------------------------------------- helpers --

    /// <summary>
    /// A two-revision PDF: the second revision's xref section defines only the
    /// /Info object it adds, and reaches the catalog through /Prev.
    /// </summary>
    private static byte[] BuildIncrementallyUpdatedPdf()
    {
        var latin1 = Encoding.Latin1;
        const string content = MalformedXRefFixtures.DefaultContent;
        var sb = new StringBuilder();
        var offsets = new int[9];

        sb.Append("%PDF-1.7\n");
        offsets[1] = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        offsets[2] = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Count 1 /Kids [3 0 R] /MediaBox [0 0 612 792] >>\nendobj\n");
        offsets[3] = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 4 0 R "
                  + "/Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        offsets[4] = sb.Length;
        sb.Append($"4 0 obj\n<< /Length {latin1.GetByteCount(content)} >>\nstream\n{content}\nendstream\nendobj\n");
        offsets[5] = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        offsets[7] = sb.Length;
        sb.Append("7 0 obj\n<< /Ghost (first revision only) >>\nendobj\n");

        int firstXRef = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        for (int i = 1; i <= 5; i++)
            sb.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");
        sb.Append("7 1\n").Append(offsets[7].ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size 8 /Root 1 0 R >>\n");
        sb.Append("startxref\n").Append(firstXRef).Append("\n%%EOF\n");

        // Second revision: adds object 6 (/Info) and DELETES object 7.
        offsets[6] = sb.Length;
        sb.Append("6 0 obj\n<< /Title (second revision) >>\nendobj\n");

        int secondXRef = sb.Length;
        sb.Append("xref\n6 2\n").Append(offsets[6].ToString("D10")).Append(" 00000 n \n");
        sb.Append("0000000000 65535 f \n");
        sb.Append($"trailer\n<< /Size 8 /Root 1 0 R /Info 6 0 R /Prev {firstXRef} >>\n");
        sb.Append("startxref\n").Append(secondXRef).Append("\n%%EOF\n");

        return latin1.GetBytes(sb.ToString());
    }
}
