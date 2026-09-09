using System.IO;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.TestSupport;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// #1093, end to end: redacting one word must not rewrite the rest of the page.
///
/// <para>Until the write-back opted into byte splicing, removing a single glyph
/// run put EVERY operator on the page back through
/// <c>ContentStreamWriter</c> — its string escaping, its number formatting, its
/// inline-image reconstruction. Three shipped corruptions came from that
/// (#354, #762, PDFDocEncoding octal escapes), each found by a leak rather than
/// by a gate, and each one damaged content the redaction never targeted.</para>
///
/// <para>The operators here are deliberately spelled the way a real producer
/// spells them and the writer does NOT: <c>0.500</c> rather than <c>0.5</c>,
/// run-together spacing, a comment. If any of those come back normalised, the
/// page was re-serialized.</para>
/// </summary>
public class RedactionSourceBytePreservationTests
{
    private const string AwkwardContent =
        "% producer comment\n"
        + "q\n"
        + "0.500  0.250 0.1250 rg\n"
        + "BT\n"
        + "  /F1   12.00 Tf\n"
        + "  72 720 Td\n"
        + "  (KEEPME) Tj\n"
        + "  0 -20 Td\n"
        + "  (SECRET) Tj\n"
        + "ET\n"
        + "Q\n";

    [Fact]
    public void RedactingAWord_LeavesUntouchedOperatorsBytewiseAlone()
    {
        using var doc = PdfDocument.Open(BuildPdfWithContent(AwkwardContent));

        doc.RedactText("SECRET").MatchesLocated.Should().BeGreaterThan(0);

        var after = Encoding.Latin1.GetString(doc.GetPage(1).GetContentStreamBytes());

        after.Should().NotContain("SECRET", "the point of the exercise");
        after.Should().Contain("0.500  0.250 0.1250 rg",
            "an operator the redaction never touched must keep its source bytes — "
            + "number formatting and spacing included (#1093)");
        after.Should().Contain("% producer comment",
            "comments are content-stream bytes too; re-serialization drops them");
    }

    /// <summary>
    /// The other half: what the redaction DID touch is gone from the saved file,
    /// carrier and all. Splicing must not become a way for original bytes to
    /// come back.
    /// </summary>
    [Fact]
    public void RedactingAWord_RemovesItFromTheSavedBytes()
    {
        using var doc = PdfDocument.Open(BuildPdfWithContent(AwkwardContent));
        doc.RedactText("SECRET");

        var saved = doc.SaveToBytes();
        SavedPdfLeakScanner.FindTerm(saved, "SECRET").Should().BeEmpty(
            "carrier-agnostic scan of the saved file, compressed streams included");
    }

    private static byte[] BuildPdfWithContent(string contentStream)
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[6];

        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[2] = ms.Position;
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[3] = ms.Position;
        writer.WriteLine("3 0 obj");
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
            + "/Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine($"<< /Length {contentStream.Length} >>");
        writer.WriteLine("stream");
        writer.Write(contentStream);
        writer.WriteLine();
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[5] = ms.Position;
        writer.WriteLine("5 0 obj");
        writer.WriteLine("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        writer.WriteLine("endobj");
        writer.Flush();

        long xrefPos = ms.Position;
        writer.WriteLine("xref");
        writer.WriteLine("0 6");
        writer.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 5; i++)
            writer.WriteLine($"{offsets[i]:D10} 00000 n ");
        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Size 6 >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }
}
