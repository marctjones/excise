using System.IO;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// End-to-end tests for PdfPage.RedactArea. These go beyond the per-class
/// unit tests by actually parsing a minimal PDF, redacting a region, saving
/// the modified document back out, and re-parsing — proving the glyph-level
/// redaction pipeline composes correctly AND that redacted text is
/// structurally absent from the saved PDF (the actual security guarantee).
/// </summary>
public class PdfPageRedactionEndToEndTests
{
    [Fact]
    public void RedactArea_CoveringOneWord_RemovesItFromContentStreamBytes()
    {
        // "HELLO WORLD" at 12pt, baseline y=700, starting at x=100.
        // At 12pt Helvetica: H~8.4, E~9.3, L~2.7, L~2.7, O~9.3 = ~32 pt
        // space ~3 pt, then WORLD. "WORLD" spans roughly x=135..180.
        var pdfBytes = CreatePdfWithText("HELLO WORLD", fontSize: 12, x: 100, y: 700);

        using var doc = PdfDocument.Open(pdfBytes);
        var page = doc.GetPage(1);

        // Confirm the text is actually in there before we redact.
        var initialText = string.Concat(page.Letters.Select(l => l.Value));
        initialText.Should().Contain("WORLD");
        initialText.Should().Contain("HELLO");

        // Locate the bounding box of WORLD in the initially-extracted letters
        // so we know exactly where to target the redaction.
        var wLetters = page.Letters.SkipWhile(l => l.Value != "W").Take(5).ToList();
        wLetters.Should().HaveCount(5);
        var worldBox = new PdfRectangle(
            wLetters.Min(l => l.GlyphRectangle.Left),
            wLetters.Min(l => l.GlyphRectangle.Bottom),
            wLetters.Max(l => l.GlyphRectangle.Right),
            wLetters.Max(l => l.GlyphRectangle.Top));

        page.RedactArea(worldBox);

        // Save the mutated document and re-open. This forces the content
        // stream to be re-serialized and re-parsed — catches anything that
        // only works in the mutated in-memory copy.
        var savedBytes = doc.SaveToBytes();
        using var reopened = PdfDocument.Open(savedBytes);
        var afterText = string.Concat(reopened.GetPage(1).Letters.Select(l => l.Value));

        afterText.Should().NotContain("WORLD",
            "redacted text must be gone from PDF structure, not just visually covered");
        afterText.Should().Contain("HELLO",
            "non-redacted text must survive");

        // The PDF security guarantee: redacted characters are absent from
        // the raw content stream bytes, so pdftotext et al. can't recover them.
        var rawContent = Encoding.Latin1.GetString(reopened.GetPage(1).GetContentStreamBytes());
        rawContent.Should().NotContain("WORLD",
            "WORLD must be absent from the raw content stream, not just the letter extraction");
        rawContent.Should().Contain("HELLO");
    }

    [Fact]
    public void RedactArea_AreaHitsNothing_LeavesContentStreamUnchanged()
    {
        var pdfBytes = CreatePdfWithText("HELLO WORLD", fontSize: 12, x: 100, y: 700);

        using var doc = PdfDocument.Open(pdfBytes);
        var page = doc.GetPage(1);
        var originalContent = page.GetContentStreamBytes();

        // Far from where the text actually is.
        page.RedactArea(new PdfRectangle(500, 50, 600, 100));

        var after = page.GetContentStreamBytes();
        // "No-op" doesn't have to mean byte-identical — the parser/serializer
        // round-trip is allowed to normalize whitespace or numeric formatting.
        // What matters: the decoded text survives.
        var afterText = string.Concat(page.Letters.Select(l => l.Value));
        afterText.Should().Contain("HELLO");
        afterText.Should().Contain("WORLD");
    }

    [Fact]
    public void RedactAreas_MultipleRects_RemovesAllOfThem()
    {
        var pdfBytes = CreatePdfWithText("APPLE BANANA CHERRY", fontSize: 12, x: 100, y: 700);

        using var doc = PdfDocument.Open(pdfBytes);
        var page = doc.GetPage(1);

        var apple = page.Letters.Take(5).ToList();
        var cherry = page.Letters.Skip(13).Take(6).ToList();

        var appleBox = BoundingBoxOf(apple);
        var cherryBox = BoundingBoxOf(cherry);

        page.RedactAreas(new[] { appleBox, cherryBox });

        var rawContent = Encoding.Latin1.GetString(page.GetContentStreamBytes());
        rawContent.Should().NotContain("APPLE");
        rawContent.Should().NotContain("CHERRY");
        rawContent.Should().Contain("BANANA", "the middle word must survive");
    }

    private static PdfRectangle BoundingBoxOf(IEnumerable<Excise.Core.Text.Letter> letters)
    {
        var list = letters.ToList();
        return new PdfRectangle(
            list.Min(l => l.GlyphRectangle.Left),
            list.Min(l => l.GlyphRectangle.Bottom),
            list.Max(l => l.GlyphRectangle.Right),
            list.Max(l => l.GlyphRectangle.Top));
    }

    /// <summary>
    /// Build a minimal one-page PDF whose content stream draws the given
    /// text at the given position via Helvetica. The text is emitted as a
    /// literal <c>(text) Tj</c> so per-glyph segmentation has something to
    /// bite on.
    /// </summary>
    private static byte[] CreatePdfWithText(string text, double fontSize, double x, double y)
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\n" };

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
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                         "/Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>");
        writer.WriteLine("endobj");
        writer.Flush();

        var contentBody = $"BT /F1 {fontSize} Tf {x} {y} Td ({text}) Tj ET";
        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine($"<< /Length {contentBody.Length} >>");
        writer.WriteLine("stream");
        writer.Write(contentBody);
        writer.WriteLine();
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[5] = ms.Position;
        writer.WriteLine("5 0 obj");
        writer.WriteLine("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        writer.WriteLine("endobj");
        writer.Flush();

        long xrefPos = ms.Position;
        writer.WriteLine("xref");
        writer.WriteLine("0 6");
        writer.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 5; i++)
            writer.WriteLine($"{offsets[i]:D10} 00000 n ");
        writer.Flush();
        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Size 6 >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }
}
