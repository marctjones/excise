using AwesomeAssertions;
using Pdfe.Core.Document;
using Pdfe.Core.Primitives;
using Xunit;

namespace Pdfe.Core.Tests.Writing;

/// <summary>
/// TDD tests for PDF document writing - tests define expected behavior before implementation.
/// </summary>
public class PdfDocumentWriterTests
{
    #region Save API Tests

    [Fact]
    public void Save_ToStream_ProducesValidPdf()
    {
        // Arrange - Open a PDF
        var originalData = CreateSimplePdf("Hello World");
        using var doc = PdfDocument.Open(originalData);

        // Act - Save to a new stream
        using var outputStream = new MemoryStream();
        doc.Save(outputStream);
        var savedData = outputStream.ToArray();

        // Assert - Should produce valid PDF structure
        savedData.Should().NotBeEmpty();
        var header = System.Text.Encoding.ASCII.GetString(savedData, 0, 8);
        header.Should().StartWith("%PDF-");
    }

    [Fact]
    public void Save_ToBytes_ProducesValidPdf()
    {
        // Arrange
        var originalData = CreateSimplePdf("Test");
        using var doc = PdfDocument.Open(originalData);

        // Act
        var savedData = doc.SaveToBytes();

        // Assert
        savedData.Should().NotBeEmpty();
        savedData.Should().StartWith(new byte[] { 0x25, 0x50, 0x44, 0x46 }); // %PDF
    }

    [Fact]
    public void Save_PreservesPageCount()
    {
        // Arrange
        var originalData = CreateSimplePdf("Content");
        using var doc = PdfDocument.Open(originalData);
        var originalPageCount = doc.PageCount;

        // Act
        var savedData = doc.SaveToBytes();
        using var reopened = PdfDocument.Open(savedData);

        // Assert
        reopened.PageCount.Should().Be(originalPageCount);
    }

    [Fact]
    public void Save_PreservesTextContent()
    {
        // Arrange
        var originalData = CreateSimplePdf("Preserved Text");
        using var doc = PdfDocument.Open(originalData);
        var originalText = doc.GetPage(1).Text;

        // Act
        var savedData = doc.SaveToBytes();
        using var reopened = PdfDocument.Open(savedData);

        // Assert
        reopened.GetPage(1).Text.Should().Contain("Preserved");
    }

    #endregion

    #region Object Serialization Tests

    [Fact]
    public void Serialize_PdfNull_WritesCorrectly()
    {
        // Act
        var result = SerializeObject(PdfNull.Instance);

        // Assert
        result.Should().Be("null");
    }

    [Fact]
    public void Serialize_PdfBoolean_WritesCorrectly()
    {
        // Act & Assert
        SerializeObject(PdfBoolean.True).Should().Be("true");
        SerializeObject(PdfBoolean.False).Should().Be("false");
    }

    [Fact]
    public void Serialize_PdfInteger_WritesCorrectly()
    {
        // Act & Assert
        SerializeObject(new PdfInteger(42)).Should().Be("42");
        SerializeObject(new PdfInteger(-100)).Should().Be("-100");
        SerializeObject(new PdfInteger(0)).Should().Be("0");
    }

    [Fact]
    public void Serialize_PdfReal_WritesCorrectly()
    {
        // Act & Assert
        SerializeObject(new PdfReal(3.14)).Should().Be("3.14");
        SerializeObject(new PdfReal(-0.5)).Should().Be("-0.5");
        SerializeObject(new PdfReal(100.0)).Should().Be("100");
    }

    [Fact]
    public void Serialize_PdfName_WritesCorrectly()
    {
        // Act & Assert
        SerializeObject(new PdfName("Type")).Should().Be("/Type");
        SerializeObject(new PdfName("Page")).Should().Be("/Page");
    }

    [Fact]
    public void Serialize_PdfName_EscapesSpecialChars()
    {
        // Names with special characters need #XX encoding
        var name = new PdfName("Name With Space");
        var result = SerializeObject(name);
        result.Should().Contain("#20"); // Space is #20
    }

    [Fact]
    public void Serialize_PdfString_WritesLiteralCorrectly()
    {
        // Act
        var result = SerializeObject(PdfString.FromText("Hello"));

        // Assert - Should be literal string with parentheses
        result.Should().Be("(Hello)");
    }

    [Fact]
    public void Serialize_PdfString_EscapesParentheses()
    {
        // Act
        var result = SerializeObject(PdfString.FromText("(nested)"));

        // Assert - Parentheses should be escaped
        result.Should().Contain("\\(").And.Contain("\\)");
    }

    [Fact]
    public void Serialize_PdfArray_WritesCorrectly()
    {
        // Arrange
        var array = new PdfArray();
        array.Add((PdfObject)new PdfInteger(1));
        array.Add((PdfObject)new PdfInteger(2));
        array.Add((PdfObject)new PdfInteger(3));

        // Act
        var result = SerializeObject(array);

        // Assert
        result.Should().Be("[1 2 3]");
    }

    [Fact]
    public void Serialize_PdfDictionary_WritesCorrectly()
    {
        // Arrange
        var dict = new PdfDictionary
        {
            ["Type"] = new PdfName("Page"),
            ["Count"] = new PdfInteger(1)
        };

        // Act
        var result = SerializeObject(dict);

        // Assert
        result.Should().Contain("<<");
        result.Should().Contain("/Type /Page");
        result.Should().Contain("/Count 1");
        result.Should().Contain(">>");
    }

    [Fact]
    public void Serialize_PdfReference_WritesCorrectly()
    {
        // Act
        var result = SerializeObject(new PdfReference(5, 0));

        // Assert
        result.Should().Be("5 0 R");
    }

    #endregion

    #region XRef Table Tests

    [Fact]
    public void XRefTable_HasCorrectFormat()
    {
        // Arrange
        var originalData = CreateSimplePdf("Test");
        using var doc = PdfDocument.Open(originalData);

        // Act
        var savedData = doc.SaveToBytes();
        var content = System.Text.Encoding.ASCII.GetString(savedData);

        // Assert - Should have xref table
        content.Should().Contain("xref");
        content.Should().Contain("startxref");
        content.Should().Contain("%%EOF");
    }

    [Fact]
    public void Trailer_HasRequiredKeys()
    {
        // Arrange
        var originalData = CreateSimplePdf("Test");
        using var doc = PdfDocument.Open(originalData);

        // Act
        var savedData = doc.SaveToBytes();
        var content = System.Text.Encoding.ASCII.GetString(savedData);

        // Assert - Trailer must have /Root and /Size
        content.Should().Contain("trailer");
        content.Should().Contain("/Root");
        content.Should().Contain("/Size");
    }

    #endregion

    #region Stream Tests

    [Fact]
    public void Stream_PreservesData()
    {
        // Arrange
        var originalData = CreateSimplePdf("Stream Test");
        using var doc = PdfDocument.Open(originalData);

        // Act
        var savedData = doc.SaveToBytes();
        using var reopened = PdfDocument.Open(savedData);

        // Assert - Content stream should be preserved
        var page = reopened.GetPage(1);
        var contentBytes = page.GetContentStreamBytes();
        contentBytes.Should().NotBeEmpty();
    }

    #endregion

    #region Round-Trip Tests

    [Fact]
    public void RoundTrip_SimplePdf_PreservesStructure()
    {
        // Arrange
        var originalData = CreateSimplePdf("Round Trip Test");

        // Act - Multiple round trips
        byte[] currentData = originalData;
        for (int i = 0; i < 3; i++)
        {
            using var doc = PdfDocument.Open(currentData);
            currentData = doc.SaveToBytes();
        }

        // Assert - Should still be valid after 3 round trips
        using var final = PdfDocument.Open(currentData);
        final.PageCount.Should().Be(1);
        final.GetPage(1).Text.Should().Contain("Round Trip");
    }

    #endregion

    #region Helper Methods

    private static byte[] CreateSimplePdf(string text)
    {
        var content = $"BT /F1 12 Tf 100 700 Td ({text}) Tj ET";

        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true);
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
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine($"<< /Length {content.Length} >>");
        writer.WriteLine("stream");
        writer.Write(content);
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

    private static string SerializeObject(PdfObject obj)
    {
        // This will call the PdfObjectWriter.Serialize method when implemented
        return Pdfe.Core.Writing.PdfObjectWriter.Serialize(obj);
    }

    #endregion

    #region Cross-reference plumbing not re-emitted (#359)

    [Fact]
    public void Save_DropsUnreachableObjectsAndXRefPlumbing_SoFreedObjectsCannotLeak()
    {
        // The writer emits a classic xref table and writes object-stream members
        // as standalone objects. Re-emitting the /ObjStm (or /XRef) container is
        // redundant AND a leak path: a redacted Form XObject that was inlined and
        // freed (RemoveObject) would still ship inside the container's bytes. (#359)
        // It also garbage-collects normal unreachable objects so stale previous
        // revisions and scrubbed attachment/metadata streams do not survive a save.
        using var doc = PdfDocument.Open(CreateSimplePdf("Hi"));

        var keep = new PdfStream(System.Text.Encoding.ASCII.GetBytes("KEEPME_DATA"));
        doc.AddIndirectObject(keep);

        var objStm = new PdfStream(System.Text.Encoding.ASCII.GetBytes("SECRET_IN_OBJSTM"));
        objStm["Type"] = new PdfName("ObjStm");
        doc.AddIndirectObject(objStm);

        var xref = new PdfStream(System.Text.Encoding.ASCII.GetBytes("SECRET_IN_XREF"));
        xref["Type"] = new PdfName("XRef");
        doc.AddIndirectObject(xref);

        var saved = System.Text.Encoding.Latin1.GetString(doc.SaveToBytes());

        saved.Should().NotContain("KEEPME_DATA", "unreachable normal objects must not be re-emitted");
        saved.Should().NotContain("SECRET_IN_OBJSTM", "/ObjStm containers must not be re-emitted");
        saved.Should().NotContain("SECRET_IN_XREF", "/XRef streams must not be re-emitted");
    }

    #endregion
}
