using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// End-to-end coverage for #354: inline images (BI…ID…EI) must be removed
/// from the content stream when they overlap a redaction area — the embedded
/// pixel bytes are the leak, not just the visible rendering. Also verifies the
/// audit path (<see cref="HiddenTextDetector"/>) flags text hidden underneath
/// an inline image.
/// </summary>
public class InlineImageRedactionEndToEndTests
{
    // 0xDEADBEEF — a 4-byte marker that won't occur incidentally elsewhere in
    // the saved PDF, so "is it gone?" reduces to a substring search.
    private static readonly byte[] Marker = { 0xDE, 0xAD, 0xBE, 0xEF };
    private const string MarkerStr = "\xDE\xAD\xBE\xEF";

    [Fact]
    public void RedactArea_OverInlineImage_RemovesEmbeddedBytesFromSavedPdf()
    {
        // Image mapped to page-space (100,600)-(200,700) via the cm matrix.
        var pdfBytes = CreatePdfWithInlineImage("100 0 0 100 100 600");

        using var doc = PdfDocument.Open(pdfBytes);
        var page = doc.GetPage(1);

        // Sanity: the marker is present before redaction.
        Encoding.Latin1.GetString(page.GetContentStreamBytes())
            .Should().Contain(MarkerStr, "the inline image bytes start out in the stream");

        page.RedactArea(new PdfRectangle(50, 550, 250, 750)); // fully covers the image

        var saved = doc.SaveToBytes();
        Encoding.Latin1.GetString(saved).Should().NotContain(MarkerStr,
            "the redacted inline image's pixel bytes must be absent from the saved PDF");

        // And it survives reopen as a structurally clean document.
        using var reopened = PdfDocument.Open(saved);
        reopened.GetPage(1).GetContentStream().Operators
            .Should().NotContain(op => op.Name == "BI");
    }

    [Fact]
    public void RedactArea_AwayFromInlineImage_PreservesBytesOnRoundTrip()
    {
        var pdfBytes = CreatePdfWithInlineImage("100 0 0 100 100 600");

        using var doc = PdfDocument.Open(pdfBytes);
        var page = doc.GetPage(1);

        page.RedactArea(new PdfRectangle(0, 0, 40, 40)); // misses the image entirely

        var saved = doc.SaveToBytes();
        Encoding.Latin1.GetString(saved).Should().Contain(MarkerStr,
            "an inline image outside the redaction area must round-trip losslessly");
    }

    [Fact]
    public void HiddenTextDetector_FlagsTextHiddenUnderInlineImage()
    {
        // Text at (100,700); an inline image painted on top covering it.
        var pdfBytes = CreatePdfWithTextThenInlineImage(
            text: "SECRET", fontSize: 12, textX: 100, textY: 700,
            imageCm: "120 0 0 30 95 695");

        using var doc = PdfDocument.Open(pdfBytes);
        var records = HiddenTextDetector.ScanPage(doc.GetPage(1));

        records.Should().ContainSingle(r => r.HiddenBy == "inline image",
            "text covered by a later-drawn inline image is a redaction-by-overlay leak");
    }

    /// <summary>
    /// Minimal one-page PDF whose content stream is
    /// <c>q &lt;cm&gt; cm BI …/L 4 ID &lt;marker&gt; EI Q</c>.
    /// </summary>
    private static byte[] CreatePdfWithInlineImage(string cm)
    {
        var head = Encoding.Latin1.GetBytes(
            $"q\n{cm} cm\nBI\n/W 2\n/H 2\n/BPC 8\n/CS /G\n/L 4\nID ");
        var tail = Encoding.Latin1.GetBytes("\nEI\nQ\n");
        var content = head.Concat(Marker).Concat(tail).ToArray();
        return BuildSinglePagePdf(content, fontResource: false);
    }

    /// <summary>
    /// One-page PDF that draws <paramref name="text"/> and then paints an
    /// inline image over it (so the image appears later in the stream).
    /// </summary>
    private static byte[] CreatePdfWithTextThenInlineImage(
        string text, double fontSize, double textX, double textY, string imageCm)
    {
        var sb = new StringBuilder();
        sb.Append($"BT /F1 {fontSize} Tf {textX} {textY} Td ({text}) Tj ET\n");
        sb.Append($"q\n{imageCm} cm\nBI\n/W 1\n/H 1\n/BPC 8\n/CS /G\n/L 1\nID ");
        var head = Encoding.Latin1.GetBytes(sb.ToString());
        var tail = Encoding.Latin1.GetBytes("\nEI\nQ\n");
        var content = head.Concat(new byte[] { 0xFF }).Concat(tail).ToArray();
        return BuildSinglePagePdf(content, fontResource: true);
    }

    private static byte[] BuildSinglePagePdf(byte[] contentBody, bool fontResource)
    {
        using var ms = new MemoryStream();
        using var w = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\n" };

        w.WriteLine("%PDF-1.4");
        w.Flush();

        int objCount = fontResource ? 5 : 4;
        var offsets = new long[objCount + 1];

        offsets[1] = ms.Position;
        w.WriteLine("1 0 obj");
        w.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        w.WriteLine("endobj");
        w.Flush();

        offsets[2] = ms.Position;
        w.WriteLine("2 0 obj");
        w.WriteLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        w.WriteLine("endobj");
        w.Flush();

        var resources = fontResource
            ? "/Resources << /Font << /F1 5 0 R >> >>"
            : "/Resources << >>";
        offsets[3] = ms.Position;
        w.WriteLine("3 0 obj");
        w.WriteLine($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R {resources} >>");
        w.WriteLine("endobj");
        w.Flush();

        offsets[4] = ms.Position;
        w.WriteLine("4 0 obj");
        w.WriteLine($"<< /Length {contentBody.Length} >>");
        w.WriteLine("stream");
        w.Flush();
        ms.Write(contentBody, 0, contentBody.Length);
        w.WriteLine();
        w.WriteLine("endstream");
        w.WriteLine("endobj");
        w.Flush();

        if (fontResource)
        {
            offsets[5] = ms.Position;
            w.WriteLine("5 0 obj");
            w.WriteLine("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
            w.WriteLine("endobj");
            w.Flush();
        }

        long xrefPos = ms.Position;
        w.WriteLine("xref");
        w.WriteLine($"0 {objCount + 1}");
        w.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= objCount; i++)
            w.WriteLine($"{offsets[i]:D10} 00000 n ");
        w.Flush();
        w.WriteLine("trailer");
        w.WriteLine($"<< /Root 1 0 R /Size {objCount + 1} >>");
        w.WriteLine("startxref");
        w.WriteLine(xrefPos.ToString());
        w.WriteLine("%%EOF");
        w.Flush();

        return ms.ToArray();
    }
}
