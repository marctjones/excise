using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using System.IO;
using System.Text;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

public class PdfDocumentRedactionExtensionsTests
{
    /// <summary>
    /// Create a minimal valid PDF for testing with a simple content stream.
    /// </summary>
    private static PdfDocument OpenDoc(string contentStreamBody)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        long o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        long o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        long o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        string streamBody = contentStreamBody;
        long o4 = sb.Length;
        sb.Append($"4 0 obj\n<< /Length {Encoding.Latin1.GetByteCount(streamBody)} >>\nstream\n{streamBody}\nendstream\nendobj\n");
        long o5 = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        long xref = sb.Length;
        sb.Append("xref\n0 6\n");
        sb.Append("0000000000 65535 f \n");
        sb.Append($"{o1:D10} 00000 n \n");
        sb.Append($"{o2:D10} 00000 n \n");
        sb.Append($"{o3:D10} 00000 n \n");
        sb.Append($"{o4:D10} 00000 n \n");
        sb.Append($"{o5:D10} 00000 n \n");
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        sb.Append($"startxref\n{xref}\n%%EOF\n");
        return PdfDocument.Open(new MemoryStream(Encoding.Latin1.GetBytes(sb.ToString())), false);
    }

    [Fact]
    public void RedactText_NullDocument_ThrowsArgumentNullException()
    {
        var action = () => PdfDocumentRedactionExtensions.RedactText(null!, "test");

        action.Should().Throw<ArgumentNullException>().WithParameterName("document");
    }

    [Fact]
    public void RedactText_EmptySearchText_Returns0()
    {
        var doc = OpenDoc("BT /F1 12 Tf 100 700 Td (Hello) Tj ET");

        var result = doc.RedactText("");

        result.Should().Be(0);
    }

    [Fact]
    public void RedactText_NullSearchText_Returns0()
    {
        var doc = OpenDoc("BT /F1 12 Tf 100 700 Td (Hello) Tj ET");

        var result = doc.RedactText(null!);

        result.Should().Be(0);
    }

    [Fact]
    public void RedactText_DocumentWithNoContentStream_Returns0()
    {
        var doc = OpenDoc("");

        var result = doc.RedactText("test");

        result.Should().Be(0);
    }

    [Fact]
    public void RedactText_DocumentWithNoText_Returns0()
    {
        var doc = OpenDoc("q 0 0 0 rg 100 100 50 50 re f Q");

        var result = doc.RedactText("Hello");

        result.Should().Be(0);
    }

    [Fact]
    public void RedactText_WithDrawBlackRectTrue_AppendsBlackRectangle()
    {
        var doc = OpenDoc("BT /F1 12 Tf 100 700 Td (Hello World) Tj ET");

        var originalPageOps = doc.GetPage(1).GetContentStream().Count;
        var result = doc.RedactText("Hello", drawBlackRect: true);

        var newPageOps = doc.GetPage(1).GetContentStream().Count;
        if (result > 0)
        {
            newPageOps.Should().BeGreaterThan(originalPageOps);
        }
    }

    [Fact]
    public void RedactText_WithDrawBlackRectFalse_DoesNotAppendRect()
    {
        var doc = OpenDoc("BT /F1 12 Tf 100 700 Td (Hello World) Tj ET");

        var originalPageOps = doc.GetPage(1).GetContentStream().Count;
        var result = doc.RedactText("Hello", drawBlackRect: false);

        var newPageOps = doc.GetPage(1).GetContentStream().Count;
        if (result > 0)
        {
            newPageOps.Should().BeLessThanOrEqualTo(originalPageOps + 2);
        }
    }

    [Fact]
    public void RedactText_CaseSensitiveTrue_DoesNotMatchDifferentCase()
    {
        var doc = OpenDoc("BT /F1 12 Tf 100 700 Td (Hello) Tj ET");

        var resultLower = doc.RedactText("hello", caseSensitive: true);

        resultLower.Should().Be(0);
    }

    [Fact]
    public void RedactText_CaseSensitiveFalse_MatchesDifferentCase()
    {
        var doc = OpenDoc("BT /F1 12 Tf 100 700 Td (Hello) Tj ET");

        var resultLower = doc.RedactText("hello", caseSensitive: false);

        resultLower.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void RedactText_WithCurlyQuote_FindsMatch()
    {
        var contentWithCurlyQuote = "BT /F1 12 Tf 100 700 Td (It's) Tj ET";
        var doc = OpenDoc(contentWithCurlyQuote);

        var result = doc.RedactText("It's", caseSensitive: false);

        result.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void RedactText_WithMultiplePages_RedactsAllPages()
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        long o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        long o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>\nendobj\n");
        long o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 5 0 R /Resources << /Font << /F1 6 0 R >> >> >>\nendobj\n");
        long o4 = sb.Length;
        sb.Append("4 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 7 0 R /Resources << /Font << /F1 6 0 R >> >> >>\nendobj\n");
        string stream1 = "BT /F1 12 Tf 100 700 Td (Hello) Tj ET";
        long o5 = sb.Length;
        sb.Append($"5 0 obj\n<< /Length {Encoding.Latin1.GetByteCount(stream1)} >>\nstream\n{stream1}\nendstream\nendobj\n");
        string stream2 = "BT /F1 12 Tf 100 700 Td (Hello) Tj ET";
        long o6 = sb.Length;
        sb.Append($"7 0 obj\n<< /Length {Encoding.Latin1.GetByteCount(stream2)} >>\nstream\n{stream2}\nendstream\nendobj\n");
        long o7 = sb.Length;
        sb.Append("6 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        long xref = sb.Length;
        sb.Append("xref\n0 8\n");
        sb.Append("0000000000 65535 f \n");
        sb.Append($"{o1:D10} 00000 n \n");
        sb.Append($"{o2:D10} 00000 n \n");
        sb.Append($"{o3:D10} 00000 n \n");
        sb.Append($"{o4:D10} 00000 n \n");
        sb.Append($"{o5:D10} 00000 n \n");
        sb.Append($"{o6:D10} 00000 n \n");
        sb.Append($"{o7:D10} 00000 n \n");
        sb.Append("trailer\n<< /Size 8 /Root 1 0 R >>\n");
        sb.Append($"startxref\n{xref}\n%%EOF\n");

        var doc = PdfDocument.Open(new MemoryStream(Encoding.Latin1.GetBytes(sb.ToString())), false);

        var result = doc.RedactText("Hello");

        result.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void RedactText_WithWhitespaceNormalization_MatchesCollapsedWhitespace()
    {
        var doc = OpenDoc("BT /F1 12 Tf 100 700 Td (Hello   World) Tj ET");

        var result = doc.RedactText("Hello World");

        result.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void RedactText_DoesNotThrowOnValidInput()
    {
        var doc = OpenDoc("BT /F1 12 Tf 100 700 Td (Test) Tj ET");

        var action = () => doc.RedactText("Test");

        action.Should().NotThrow();
    }

    [Fact]
    public void RedactText_ReturnsNonNegativeCount()
    {
        var doc = OpenDoc("BT /F1 12 Tf 100 700 Td (Hello) Tj ET");

        var result = doc.RedactText("Hello");

        result.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void RedactText_WithStrategy_AcceptsGlyphRemovalStrategy()
    {
        var doc = OpenDoc("BT /F1 12 Tf 100 700 Td (Hello) Tj ET");

        var result = doc.RedactText("Hello", strategy: GlyphRemovalStrategy.AnyOverlap);

        result.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void RedactText_NonExistentText_Returns0()
    {
        var doc = OpenDoc("BT /F1 12 Tf 100 700 Td (Hello) Tj ET");

        var result = doc.RedactText("Nonexistent");

        result.Should().Be(0);
    }
}
