using System.IO;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// Redaction must still remove text from documents that only open because the
/// parser became MORE tolerant (#884).
///
/// WHY THIS EXISTS
/// ---------------
/// Three parser changes shipped together, each turning a hard failure into a
/// recovered read:
///
///   1. a missing 'endobj' no longer discards the parsed object
///   2. an undefined object number resolves to null (PDF 32000-1 §7.3.10)
///      instead of throwing
///   3. a stream whose /Length overruns the file returns the bytes that exist
///
/// Every one of those makes excise open a document it previously refused. For a
/// viewer that is strictly an improvement. For a REDACTION tool it is a new
/// risk with a specific shape: the document now opens, redaction runs, and
/// redaction reports success — but if the tolerant path left part of the object
/// graph unreachable, the text was never reached and therefore never removed.
///
/// That is the failure CLAUDE.md is built around: excise cannot redact what
/// excise cannot read, and it will report success anyway. Widening what excise
/// reads without checking redaction still reaches it would be exactly the wrong
/// trade.
///
/// WHAT IS ASSERTED
/// ----------------
/// The carrier-agnostic check on the SAVED BYTES, in both ASCII and UTF-16BE.
/// Deliberately NOT ExtractAllText, which reads only the content stream and has
/// passed on leaking documents three separate times (#636, #608, #637).
/// </summary>
public class TolerantParsePathRedactionTests
{
    private const string Secret = "SECRET";
    private const string Keep = "KEEPME";

    /// <summary>
    /// A document referencing an object number that the xref does not define.
    /// Before #884 this threw at open; now it resolves to null per §7.3.10.
    /// </summary>
    [Fact]
    public void UndefinedObjectReference_DocumentOpens_AndRedactionStillRemovesTheText()
    {
        var pdf = BuildPdf(extraPageEntry: "  /Metadata 99 0 R\n");   // object 99 never defined

        using var doc = PdfDocument.Open(pdf);
        doc.PageCount.Should().Be(1, "the undefined reference must not prevent the document opening");

        var removed = doc.RedactText(Secret).VerifiedRemovals;
        removed.Should().BeGreaterThan(0,
            "the text is reachable in the content stream — a null sibling object must not " +
            "make redaction skip it");

        var saved = Utf16AndAsciiOf(SaveToBytes(doc));
        saved.Should().NotContain(Secret,
            "a document that opens only because of the §7.3.10 tolerance must still redact " +
            "completely — opening more files is worthless if redaction silently reaches less");
        saved.Should().Contain(Keep, "unrelated text must survive");
    }

    /// <summary>
    /// A page whose object is not terminated by 'endobj'. Before #884 this threw
    /// at open; now the already-parsed value is kept.
    /// </summary>
    [Fact]
    public void MissingEndobj_DocumentOpens_AndRedactionStillRemovesTheText()
    {
        var pdf = BuildPdf(omitEndobjOnContentStream: true);

        using var doc = PdfDocument.Open(pdf);
        doc.PageCount.Should().Be(1);

        doc.RedactText(Secret).VerifiedRemovals.Should().BeGreaterThan(0);

        var saved = Utf16AndAsciiOf(SaveToBytes(doc));
        saved.Should().NotContain(Secret);
        saved.Should().Contain(Keep);
    }

    /// <summary>
    /// A page with no /MediaBox anywhere in its inheritance chain. Before #884
    /// this threw; now it defaults to 612x792.
    ///
    /// This one carries the most risk of the three: redaction rectangles are
    /// computed against the page box, so a GUESSED box means guessed geometry.
    /// Text-based redaction should be unaffected because it locates glyphs from
    /// the content stream rather than from the page box — which is precisely
    /// what this pins.
    /// </summary>
    [Fact]
    public void MissingMediaBox_DocumentOpens_AndTextRedactionStillRemovesTheText()
    {
        var pdf = BuildPdf(omitMediaBox: true);

        using var doc = PdfDocument.Open(pdf);
        var page = doc.GetPage(1);
        page.Width.Should().Be(612, "the default page box stands in for the missing /MediaBox");
        page.Height.Should().Be(792);

        doc.RedactText(Secret).VerifiedRemovals.Should().BeGreaterThan(0,
            "text redaction locates glyphs from the content stream, so a defaulted page box " +
            "must not stop it finding them");

        var saved = Utf16AndAsciiOf(SaveToBytes(doc));
        saved.Should().NotContain(Secret);
        saved.Should().Contain(Keep);
    }

    /// <summary>
    /// A document whose catalog is only reachable because compressed objects are
    /// resolved by object number rather than by the xref's index-in-stream
    /// (#869). Before that, the catalog slot came back as a link annotation.
    ///
    /// The risk this pins is specific to resolving objects by a different key:
    /// the document now opens, but if the page tree it opened onto were the
    /// WRONG one, redaction would run over a page whose text is not the text in
    /// the file, and report success.
    /// </summary>
    [Fact]
    public void TruncatedObjStmIndex_DocumentOpens_AndRedactionStillRemovesTheText()
    {
        var pdf = Excise.Core.Tests.Parsing.MalformedXRefFixtures
            .BuildNarrowXRefWidthObjStmPdf(ContentFor(Secret, Keep));

        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        doc.PageCount.Should().Be(1);

        doc.RedactText(Secret).VerifiedRemovals.Should().BeGreaterThan(0,
            "the page reached through the recovered catalog must be the real one, " +
            "with the real text on it");

        var saved = Utf16AndAsciiOf(SaveToBytes(doc));
        saved.Should().NotContain(Secret);
        saved.Should().Contain(Keep, "unrelated text must survive");
    }

    /// <summary>
    /// A truncated document that only opens because the assembled xref was
    /// rebuilt by scanning for indirect object headers (#884).
    ///
    /// The specific risk: a reconstructed table can resolve objects the original
    /// xref never pointed at, so "the document opens" is no evidence that the
    /// object graph redaction walks is complete.
    /// </summary>
    [Fact]
    public void ReconstructedXRef_DocumentOpens_AndRedactionStillRemovesTheText()
    {
        var pdf = Excise.Core.Tests.Parsing.MalformedXRefFixtures
            .BuildTruncatedTerminalXRefPdf(ContentFor(Secret, Keep));

        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        doc.PageCount.Should().Be(1);

        doc.RedactText(Secret).VerifiedRemovals.Should().BeGreaterThan(0);

        var saved = Utf16AndAsciiOf(SaveToBytes(doc));
        saved.Should().NotContain(Secret);
        saved.Should().Contain(Keep);
    }

    /// <summary>
    /// A document carrying an unterminated stream, recovered by scanning to EOF
    /// and cutting at the next object boundary (#884).
    ///
    /// This is the tolerant path with the most direct route to a leak, and the
    /// test asserts the leak is closed rather than merely that the file opens: an
    /// unbounded scan would fold the objects that FOLLOW into the recovered
    /// stream, so text redaction removed from its own object would ship again
    /// inside a stream no glyph pass ever examined.
    /// </summary>
    [Fact]
    public void UnterminatedStream_DocumentOpens_AndRedactionStillRemovesTheText()
    {
        var pdf = Excise.Core.Tests.Parsing.MalformedXRefFixtures
            .BuildUnterminatedHugeLengthStreamPdf(ContentFor(Secret, Keep));

        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        doc.PageCount.Should().Be(1);

        doc.RedactText(Secret).VerifiedRemovals.Should().BeGreaterThan(0);

        var saved = Utf16AndAsciiOf(SaveToBytes(doc));
        saved.Should().NotContain(Secret,
            "the recovered stream is referenced from the page's resources, so it is " +
            "written on save — anything it swallowed ships with the document");
        saved.Should().Contain(Keep);
    }

    // ---------------------------------------------------------------- helpers --

    private static string ContentFor(string secret, string keep)
        => $"BT /F1 24 Tf 72 700 Td ({secret}) Tj 0 -30 Td ({keep}) Tj ET";

    /// <summary>
    /// A minimal one-page PDF containing both strings, built longhand so the
    /// xref offsets are real and each defect can be introduced surgically.
    /// </summary>
    private static byte[] BuildPdf(
        string extraPageEntry = "",
        bool omitEndobjOnContentStream = false,
        bool omitMediaBox = false)
    {
        var content = $"BT /F1 24 Tf 72 700 Td ({Secret}) Tj 0 -30 Td ({Keep}) Tj ET";
        var mediaBox = omitMediaBox ? "" : "  /MediaBox [0 0 612 792]\n";
        var contentEnd = omitEndobjOnContentStream ? "endstream\n" : "endstream\nendobj\n";

        var objects = new[]
        {
            "1 0 obj\n<<\n  /Type /Catalog\n  /Pages 2 0 R\n>>\nendobj\n",
            $"2 0 obj\n<<\n  /Type /Pages\n  /Count 1\n  /Kids [3 0 R]\n{mediaBox}>>\nendobj\n",
            "3 0 obj\n<<\n  /Type /Page\n  /Parent 2 0 R\n  /Contents 4 0 R\n" +
                "  /Resources << /Font << /F1 5 0 R >> >>\n" + extraPageEntry + ">>\nendobj\n",
            $"4 0 obj\n<<\n  /Length {content.Length}\n>>\nstream\n{content}\n{contentEnd}",
            "5 0 obj\n<<\n  /Type /Font\n  /Subtype /Type1\n  /BaseFont /Helvetica\n>>\nendobj\n",
        };

        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new int[objects.Length + 1];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i + 1] = sb.Length;
            sb.Append(objects[i]);
        }

        var xrefPos = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append('\n');
        sb.Append("0000000000 65535 f \n");
        for (int i = 1; i <= objects.Length; i++)
            sb.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<<\n  /Root 1 0 R\n  /Size ").Append(objects.Length + 1).Append("\n>>\n");
        sb.Append("startxref\n").Append(xrefPos).Append("\n%%EOF\n");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static byte[] SaveToBytes(PdfDocument pdf)
    {
        using var ms = new MemoryStream();
        pdf.Save(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Saved bytes as searchable text. PDF strings may be literal ASCII or
    /// UTF-16BE, so both are searched — a leak hiding behind an encoding is
    /// still a leak.
    /// </summary>
    private static string Utf16AndAsciiOf(byte[] saved)
        => Encoding.ASCII.GetString(saved) + "\n" + Encoding.BigEndianUnicode.GetString(saved);
}
