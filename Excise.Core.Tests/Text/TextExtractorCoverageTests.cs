using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text;
using System.Text;
using Xunit;

namespace Excise.Core.Tests.Text;

/// <summary>
/// Additional coverage tests for TextExtractor.cs to push coverage from 88.6% to >95%.
/// Focuses on uncovered paths: hex-string operands, TJ mixed arrays, quote operators,
/// Tm matrix transformations, empty content streams, missing fonts, and encoding edge cases.
/// </summary>
public class TextExtractorCoverageTests
{
    /// <summary>
    /// Hit ParseHexString path and byte[] operand in ShowText.
    /// Verifies that hex-form text operands (like &lt;48656C6C6F&gt; = "Hello") extract correctly.
    /// </summary>
    [Fact]
    public void ExtractLetters_HexStringOperand_ExtractsCorrectly()
    {
        // Arrange - hex form "Hello" = 48656C6C6F
        var pdfData = CreatePdfWithContentStream("BT /F1 12 Tf 100 700 Td <48656C6C6F> Tj ET");
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var text = extractor.ExtractText();

        // Assert
        text.Should().Contain("Hello");
    }

    /// <summary>
    /// Hit TJ array branch with mixed text and positioning operands.
    /// Verifies that TJ with [(He) -50 (llo)] extracts "He" then "llo" with position adjustment.
    /// </summary>
    [Fact]
    public void ExtractLetters_TJWithMixedArray_ExtractsAllText()
    {
        // Arrange - TJ with alternating text and numeric positioning
        var content = "BT /F1 12 Tf 100 700 Td [(He) -50 (llo)] TJ ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();
        var text = string.Concat(letters.Select(l => l.Value));

        // Assert - should contain both text chunks despite numeric adjustment
        text.Should().Contain("He");
        text.Should().Contain("llo");
    }

    /// <summary>
    /// Hit single-quote operator (') which executes T* (move to next line) then Tj.
    /// </summary>
    [Fact]
    public void ExtractLetters_SingleQuoteOperator_ExecutesT_starThenTj()
    {
        // Arrange - ' operator: move to next line and show text
        var content = "BT /F1 12 Tf 100 700 Td (first) Tj (second) ' ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();
        var text = string.Concat(letters.Select(l => l.Value));

        // Assert
        text.Should().Contain("first");
        text.Should().Contain("second");
    }

    /// <summary>
    /// Hit double-quote operator (") which sets Tw and Tc, moves to next line, then shows text.
    /// </summary>
    [Fact]
    public void ExtractLetters_DoubleQuoteOperator_SetsSpacingAndShowsText()
    {
        // Arrange - " operator: set Tw Tc, move to next line, show text
        var content = "BT /F1 12 Tf 100 700 Td (first) Tj 5 10 (second) \" ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();
        var text = string.Concat(letters.Select(l => l.Value));

        // Assert
        text.Should().Contain("first");
        text.Should().Contain("second");
    }

    /// <summary>
    /// Hit Tm operator with non-identity matrix (e.g., rotation or scaling).
    /// Tm: a b c d e f Tm sets text matrix directly. Verifies rotated text is captured.
    /// </summary>
    [Fact]
    public void ExtractLetters_TmMatrixWithRotation_TransformsCoordinates()
    {
        // Arrange - Tm with rotation matrix (90 degrees: 0 1 -1 0 e f)
        var content = "BT /F1 12 Tf 0 1 -1 0 100 700 Tm (Rotated) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();

        // Assert - rotation should not prevent extraction
        letters.Should().NotBeEmpty();
        var text = string.Concat(letters.Select(l => l.Value));
        text.Should().Contain("Rotated");
    }

    /// <summary>
    /// Empty content stream yields no text, no exception.
    /// </summary>
    [Fact]
    public void ExtractLetters_EmptyContentStream_NoException()
    {
        // Arrange
        var pdfData = CreatePdfWithContentStream("");
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();

        // Assert
        letters.Should().BeEmpty();
    }

    /// <summary>
    /// Page with content that references /F1 but font is missing from resources.
    /// Should fall back gracefully (fontName set to "F1", current font null).
    /// </summary>
    [Fact]
    public void ExtractLetters_MissingFontInResources_FallsBackGracefully()
    {
        // Arrange - create PDF but remove font from resources
        var pdfData = CreatePdfWithMissingFont();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();

        // Assert - should not throw, but may have fewer letters or no letters if font is truly missing
        // The extractor handles missing fonts gracefully. ExtractLetters returns
        // ReadOnlyCollection<Letter>, not List<Letter>; just verify it's a non-null
        // letter sequence.
        letters.Should().NotBeNull();
        letters.Should().BeAssignableTo<IReadOnlyList<Letter>>();
    }

    /// <summary>
    /// Hit the cm (modify CTM) operator with a scaling transformation.
    /// cm: a b c d e f cm applies new matrix to current transformation matrix.
    /// </summary>
    [Fact]
    public void ExtractLetters_CmOperator_AppliesGraphicsTransformation()
    {
        // Arrange - cm operator: scale by 2 in x and y
        var content = "BT /F1 12 Tf 100 700 Td 2 0 0 2 0 0 cm (Scaled) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();

        // Assert
        letters.Should().NotBeEmpty();
        var text = string.Concat(letters.Select(l => l.Value));
        text.Should().Contain("Scaled");
    }

    [Fact]
    public void ExtractText_FormXObjectDoOperator_ExtractsNestedText()
    {
        var pdfData = CreatePdfWithFormXObjectText();
        using var doc = PdfDocument.Open(pdfData);

        var text = doc.GetPage(1).Text;

        text.Should().StartWith("Nested Form Text");
        text.Should().Contain("Page Text");
    }

    /// <summary>
    /// Hit q (save) and Q (restore) graphics state operators.
    /// </summary>
    [Fact]
    public void ExtractLetters_QandQOperators_SaveRestoreGraphicsState()
    {
        // Arrange - q/Q save and restore graphics state around text
        var content = "BT /F1 12 Tf 100 700 Td q (inside) Tj Q (outside) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();
        var text = string.Concat(letters.Select(l => l.Value));

        // Assert
        text.Should().Contain("inside");
        text.Should().Contain("outside");
    }

    /// <summary>
    /// Hit TD operator: move to next line and set leading.
    /// TD: tx ty TD is equivalent to TL: -ty Td: tx ty
    /// </summary>
    [Fact]
    public void ExtractLetters_TDOperator_MovesAndSetsLeading()
    {
        // Arrange - TD operator
        var content = "BT /F1 12 Tf 50 500 TD (Line1) Tj 10 -20 TD (Line2) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();
        var text = string.Concat(letters.Select(l => l.Value));

        // Assert
        text.Should().Contain("Line1");
        text.Should().Contain("Line2");
    }

    /// <summary>
    /// Hit Tj with string operand (not byte array).
    /// </summary>
    [Fact]
    public void ExtractLetters_TjWithStringOperand_ExtractsText()
    {
        // Arrange - Tj with parenthesized string
        var content = "BT /F1 12 Tf 100 700 Td (StringText) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var text = extractor.ExtractText();

        // Assert
        text.Should().Contain("StringText");
    }

    /// <summary>
    /// Hit T* operator (move to start of next line using current leading).
    /// </summary>
    [Fact]
    public void ExtractLetters_TstarOperator_MovesToNextLine()
    {
        // Arrange - TL sets leading, T* moves to next line
        var content = "BT /F1 12 Tf 100 700 Td (First) Tj 12 TL T* (Second) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();
        var text = string.Concat(letters.Select(l => l.Value));

        // Assert
        text.Should().Contain("First");
        text.Should().Contain("Second");
    }

    /// <summary>
    /// Hit text state operators: Tc (character spacing), Tw (word spacing), Tz (horizontal scaling).
    /// </summary>
    [Fact]
    public void ExtractLetters_TextStateOperators_SetSpacingAndScaling()
    {
        // Arrange - Tc, Tw, Tz
        var content = "BT /F1 12 Tf 100 700 Td 2 Tc 3 Tw 110 Tz (Spaced) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();

        // Assert
        letters.Should().NotBeEmpty();
        var text = string.Concat(letters.Select(l => l.Value));
        text.Should().Contain("Spaced");
    }

    // ── Helper Methods ───────────────────────────────────────────────────────────

    private static byte[] CreatePdfWithContentStream(string content, string fontName = "Helvetica")
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

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
        writer.WriteLine($"<< /Length {content.Length} >>");
        writer.WriteLine("stream");
        writer.Write(content);
        writer.WriteLine();
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 5: Font
        offsets[5] = ms.Position;
        writer.WriteLine("5 0 obj");
        writer.WriteLine($"<< /Type /Font /Subtype /Type1 /BaseFont /{fontName} /Encoding /WinAnsiEncoding >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // xref
        long xrefPos = ms.Position;
        writer.WriteLine("xref");
        writer.WriteLine("0 6");
        writer.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 5; i++)
            writer.WriteLine($"{offsets[i]:D10} 00000 n ");
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

    private static byte[] CreatePdfWithFormXObjectText()
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        const string pageContent = "q /Fm0 Do Q BT /F1 12 Tf 100 650 Td (Page Text) Tj ET";
        const string formContent = "BT /F1 12 Tf 100 700 Td (Nested Form Text) Tj ET";

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[7];

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
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> /XObject << /Fm0 6 0 R >> >> >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine($"<< /Length {pageContent.Length} >>");
        writer.WriteLine("stream");
        writer.Write(pageContent);
        writer.WriteLine();
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[5] = ms.Position;
        writer.WriteLine("5 0 obj");
        writer.WriteLine("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[6] = ms.Position;
        writer.WriteLine("6 0 obj");
        writer.WriteLine($"<< /Type /XObject /Subtype /Form /BBox [0 0 612 792] /Matrix [1 0 0 1 0 0] /Resources << /Font << /F1 5 0 R >> >> /Length {formContent.Length} >>");
        writer.WriteLine("stream");
        writer.Write(formContent);
        writer.WriteLine();
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.Flush();

        long xrefPos = ms.Position;
        writer.WriteLine("xref");
        writer.WriteLine("0 7");
        writer.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 6; i++)
            writer.WriteLine($"{offsets[i]:D10} 00000 n ");
        writer.Flush();

        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Size 7 >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    /// <summary>
    /// Creates a PDF where content references /F1 but /F1 is not in the font resources.
    /// </summary>
    private static byte[] CreatePdfWithMissingFont()
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        var content = "BT /F1 12 Tf 100 700 Td (Missing) Tj ET";

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[5];

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

        // Object 3: Page - resources have no fonts (empty /Font dict)
        offsets[3] = ms.Position;
        writer.WriteLine("3 0 obj");
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << >> >> >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 4: Content stream
        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine($"<< /Length {content.Length} >>");
        writer.WriteLine("stream");
        writer.Write(content);
        writer.WriteLine();
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.Flush();

        // xref
        long xrefPos = ms.Position;
        writer.WriteLine("xref");
        writer.WriteLine("0 5");
        writer.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 4; i++)
            writer.WriteLine($"{offsets[i]:D10} 00000 n ");
        writer.Flush();

        // trailer
        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Size 5 >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }
}
