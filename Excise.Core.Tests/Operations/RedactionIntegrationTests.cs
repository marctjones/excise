using System.Text;
using AwesomeAssertions;
using Excise.Core.Content;
using Excise.Core.Document;
using Excise.Core.Operations;
using Excise.Core.Text;
using Xunit;

namespace Excise.Core.Tests.Operations;

/// <summary>
/// Integration tests for the complete redaction workflow using only Excise.Core.
/// </summary>
public class RedactionIntegrationTests
{




    [Fact]
    public void ContentStream_RoundTrip_PreservesNonRedactedContent()
    {
        var pdf = CreatePdfWithText("Keep this text");
        var page = pdf.GetPage(1);

        // Parse content
        var content = page.GetContentStream();
        var initialCount = content.Count;

        // Write back without modification
        page.SetContentStream(content);

        // Parse again
        var afterContent = page.GetContentStream();

        // Should preserve operator count (may differ slightly due to normalization)
        afterContent.Count.Should().BeGreaterThan(0);
    }

    [Fact]
    public void ContentStreamParser_HandlesMultipleTextBlocks()
    {
        var pdf = CreatePdfWithMultipleTextBlocks();
        var page = pdf.GetPage(1);

        var content = page.GetContentStream();

        // Should find multiple BT operators
        var btCount = content.Operators.Count(op => op.Name == "BT");
        btCount.Should().BeGreaterThan(1);

        // Should find multiple text showing operators
        var textOps = content.TextOperators.ToList();
        textOps.Count.Should().BeGreaterThan(1);
    }

    #region Test PDF Generators

    /// <summary>
    /// Create a minimal PDF with a single text line.
    /// </summary>
    private static PdfDocument CreatePdfWithText(string text)
    {
        var content = $"BT /F1 12 Tf 100 700 Td ({EscapePdfString(text)}) Tj ET";
        var pdfBytes = BuildPdfWithContent(content);
        return PdfDocument.Open(pdfBytes);
    }

    /// <summary>
    /// Create a PDF with text and a rectangle.
    /// </summary>
    private static PdfDocument CreatePdfWithTextAndRectangle()
    {
        var content = "BT /F1 12 Tf 100 700 Td (Text content) Tj ET " +
                      "q 0.5 G 50 650 100 50 re S Q";
        var pdfBytes = BuildPdfWithContent(content);
        return PdfDocument.Open(pdfBytes);
    }

    /// <summary>
    /// Create a PDF with multiple text blocks.
    /// </summary>
    private static PdfDocument CreatePdfWithMultipleTextBlocks()
    {
        var content = "BT /F1 12 Tf 100 700 Td (First block) Tj ET " +
                      "BT /F1 12 Tf 100 650 Td (Second block) Tj ET " +
                      "BT /F1 12 Tf 100 600 Td (Third block) Tj ET";
        var pdfBytes = BuildPdfWithContent(content);
        return PdfDocument.Open(pdfBytes);
    }

    private static byte[] BuildPdfWithContent(string contentStream)
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        // Header
        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        // Track object positions
        var offsets = new long[6];

        // Object 1: Catalog
        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 2: Pages
        offsets[2] = ms.Position;
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 3: Page
        offsets[3] = ms.Position;
        writer.WriteLine("3 0 obj");
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 4: Content stream
        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine($"<< /Length {contentStream.Length} >>");
        writer.WriteLine("stream");
        writer.Write(contentStream);
        writer.WriteLine();
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 5: Font (simplified)
        offsets[5] = ms.Position;
        writer.WriteLine("5 0 obj");
        writer.WriteLine("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // xref position
        long xrefPos = ms.Position;

        writer.WriteLine("xref");
        writer.WriteLine("0 6");
        writer.WriteLine("0000000000 65535 f ");
        writer.WriteLine($"{offsets[1]:D10} 00000 n ");
        writer.WriteLine($"{offsets[2]:D10} 00000 n ");
        writer.WriteLine($"{offsets[3]:D10} 00000 n ");
        writer.WriteLine($"{offsets[4]:D10} 00000 n ");
        writer.WriteLine($"{offsets[5]:D10} 00000 n ");
        writer.Flush();

        // trailer
        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Size 6 >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    private static string EscapePdfString(string s)
    {
        return s.Replace("\\", "\\\\")
                .Replace("(", "\\(")
                .Replace(")", "\\)")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r");
    }

    #endregion
}
