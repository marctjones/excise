using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Core.Tests.Document;

/// <summary>
/// Tests for the form-field auto-detector. Builds tiny PDFs whose content
/// streams contain the shapes the detector is looking for, then asserts
/// the suggestions match.
/// </summary>
public class PdfFormAutoDetectorTests
{
    private static byte[] BuildPdf(string contentStream)
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");
        long o1 = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine("<< /Type /Catalog /Pages 2 0 R >>");
        sb.AppendLine("endobj");
        long o2 = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");
        long o3 = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>");
        sb.AppendLine("endobj");
        long o4 = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine($"<< /Length {contentStream.Length} >>");
        sb.AppendLine("stream");
        sb.AppendLine(contentStream);
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");
        long xref = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 5");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{o1:D10} 00000 n ");
        sb.AppendLine($"{o2:D10} 00000 n ");
        sb.AppendLine($"{o3:D10} 00000 n ");
        sb.AppendLine($"{o4:D10} 00000 n ");
        sb.AppendLine("trailer << /Size 5 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xref.ToString());
        sb.AppendLine("%%EOF");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    [Fact]
    public void DetectsHorizontalUnderline_AsTextField()
    {
        // 200pt-long horizontal line at y=700. Classic Word-form underline.
        using var doc = PdfDocument.Open(BuildPdf("100 700 m 300 700 l S"));

        var suggestions = PdfFormAutoDetector.ScanPage(doc.GetPage(1));

        suggestions.Should().HaveCount(1);
        suggestions[0].FieldType.Should().Be(PdfFieldType.Text);
        suggestions[0].Rect.Left.Should().Be(100);
        suggestions[0].Rect.Right.Should().Be(300);
        suggestions[0].Rect.Bottom.Should().BeGreaterThan(700,
            "the suggested input rect must sit ABOVE the line so users type into the blank space");
        suggestions[0].SuggestedName.Should().StartWith("Text");
    }

    [Fact]
    public void IgnoresShortLines_LessThanMinLength()
    {
        // 20pt line — too short to be a fillable underline.
        using var doc = PdfDocument.Open(BuildPdf("100 700 m 120 700 l S"));
        PdfFormAutoDetector.ScanPage(doc.GetPage(1)).Should().BeEmpty();
    }

    [Fact]
    public void IgnoresVerticalLines()
    {
        // Long vertical line — table border, not a field.
        using var doc = PdfDocument.Open(BuildPdf("100 600 m 100 760 l S"));
        PdfFormAutoDetector.ScanPage(doc.GetPage(1)).Should().BeEmpty();
    }

    [Fact]
    public void IgnoresFilledRectangles()
    {
        // Filled rectangle = opaque content, not a checkbox outline.
        using var doc = PdfDocument.Open(BuildPdf("100 700 12 12 re f"));
        PdfFormAutoDetector.ScanPage(doc.GetPage(1)).Should().BeEmpty();
    }

    [Fact]
    public void DetectsSmallSquareOutline_AsCheckbox()
    {
        // 12x12pt outline = checkbox.
        using var doc = PdfDocument.Open(BuildPdf("100 700 12 12 re S"));
        var sugg = PdfFormAutoDetector.ScanPage(doc.GetPage(1));

        sugg.Should().HaveCount(1);
        sugg[0].FieldType.Should().Be(PdfFieldType.Button);
        sugg[0].Rect.Width.Should().BeApproximately(12, 0.01);
        sugg[0].Rect.Height.Should().BeApproximately(12, 0.01);
        sugg[0].SuggestedName.Should().StartWith("Checkbox");
    }

    [Fact]
    public void IgnoresLargeOutlines_NotCheckboxes()
    {
        // 400x400pt outline — page border, not a checkbox.
        using var doc = PdfDocument.Open(BuildPdf("50 50 400 400 re S"));
        PdfFormAutoDetector.ScanPage(doc.GetPage(1)).Should().BeEmpty();
    }

    [Fact]
    public void IgnoresWideRectangles_TooFarFromSquare()
    {
        // 12pt tall but 200pt wide — text-area placeholder, not a checkbox.
        using var doc = PdfDocument.Open(BuildPdf("100 700 200 12 re S"));
        PdfFormAutoDetector.ScanPage(doc.GetPage(1)).Should().BeEmpty();
    }

    [Fact]
    public void DetectsMultipleFieldsInOneStream_NumbersThemSequentially()
    {
        var content = string.Join("\n",
            "100 700 m 300 700 l S",   // Text1
            "100 650 m 300 650 l S",   // Text2
            "320 700 12 12 re S");     // Checkbox1
        using var doc = PdfDocument.Open(BuildPdf(content));

        var sugg = PdfFormAutoDetector.ScanPage(doc.GetPage(1));
        sugg.Select(s => s.SuggestedName).Should().BeEquivalentTo(new[] {
            "Text1", "Text2", "Checkbox1"
        });
    }

    [Fact]
    public void Apply_MaterializesSuggestionsAsRealFields()
    {
        var content = string.Join("\n",
            "100 700 m 300 700 l S",
            "320 700 12 12 re S");
        using var doc = PdfDocument.Open(BuildPdf(content));

        var sugg = PdfFormAutoDetector.ScanPage(doc.GetPage(1));
        var created = PdfFormAutoDetector.Apply(doc, sugg);

        created.Should().Be(2);
        var form = doc.GetAcroForm()!;
        form.Fields.Should().HaveCount(2);
        form.GetTextFields().Should().HaveCount(1);
        form.GetButtonFields().Should().HaveCount(1);
    }

    [Fact]
    public void GraphicsStateTracking_SavesAndRestoresTransformationMatrix()
    {
        // Test q/Q graphics state stack: save/restore CTM
        var content = string.Join("\n",
            "q",              // Save state
            "2 0 0 2 10 10 cm", // Scale 2x, translate to (10,10)
            "100 700 m 300 700 l S",  // Line at scaled position
            "Q",              // Restore state (back to identity)
            "100 700 m 300 700 l S"); // Line at original position
        using var doc = PdfDocument.Open(BuildPdf(content));

        var suggestions = PdfFormAutoDetector.ScanPage(doc.GetPage(1));

        // Should find both lines as text fields; scaled position differs from unscaled
        suggestions.Should().HaveCount(2);
        suggestions[0].FieldType.Should().Be(PdfFieldType.Text);
        suggestions[1].FieldType.Should().Be(PdfFieldType.Text);
    }

    [Fact]
    public void GraphicsStateTracking_IncompleteMatrixOperator_IgnoresTransform()
    {
        // Test cm operator with insufficient operands (< 6) - should not crash, should skip transform
        var content = "2 0 0 cm\n100 700 m 300 700 l S";  // Only 4 operands, not 6
        using var doc = PdfDocument.Open(BuildPdf(content));

        // Should parse without error; the incomplete cm is skipped, line detected normally
        var suggestions = PdfFormAutoDetector.ScanPage(doc.GetPage(1));

        suggestions.Should().HaveCount(1);
        suggestions[0].FieldType.Should().Be(PdfFieldType.Text);
        // Line should be at untransformed position since cm was invalid
        suggestions[0].Rect.Left.Should().BeApproximately(100, 0.1);
    }

    [Fact]
    public void MatrixMultiply_ComposesTransformationsCorrectly()
    {
        // Test matrix composition: scale then translate
        // Create two consecutive cm operators that compose
        var content = string.Join("\n",
            "2 0 0 2 0 0 cm",  // Scale 2x
            "3 0 0 3 0 0 cm",  // Scale 3x (total 6x)
            "50 350 m 150 350 l S");  // Line at (50,350), should scale to (300,2100) and (900,2100)
        using var doc = PdfDocument.Open(BuildPdf(content));

        var suggestions = PdfFormAutoDetector.ScanPage(doc.GetPage(1));

        suggestions.Should().HaveCount(1);
        suggestions[0].FieldType.Should().Be(PdfFieldType.Text);
        // With 6x scale, the 100pt line becomes 600pt
        suggestions[0].Rect.Width.Should().BeGreaterThan(500);
    }

    [Fact]
    public void Scan_MultiplePages_AggregatesSuggestionsWithPageNumbers()
    {
        // Build a multi-page PDF
        var sb = new StringBuilder();
        var offsets = new long[8];
        void Mark(int n) => offsets[n] = sb.Length;

        sb.AppendLine("%PDF-1.7");
        Mark(1);
        sb.AppendLine("1 0 obj");
        sb.AppendLine("<< /Type /Catalog /Pages 2 0 R >>");
        sb.AppendLine("endobj");
        Mark(2);
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>");
        sb.AppendLine("endobj");

        // Page 1: Text field
        Mark(3);
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 5 0 R >>");
        sb.AppendLine("endobj");
        Mark(5);
        sb.AppendLine("5 0 obj");
        var contentStream1 = "100 700 m 300 700 l S";
        sb.AppendLine($"<< /Length {contentStream1.Length + 1} >>");
        sb.AppendLine("stream");
        sb.AppendLine(contentStream1);
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");

        // Page 2: Checkbox field
        Mark(4);
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R >>");
        sb.AppendLine("endobj");
        Mark(6);
        sb.AppendLine("6 0 obj");
        var contentStream2 = "100 700 12 12 re S";
        sb.AppendLine($"<< /Length {contentStream2.Length + 1} >>");
        sb.AppendLine("stream");
        sb.AppendLine(contentStream2);
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");

        long xref = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 7");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{offsets[1]:D10} 00000 n ");
        sb.AppendLine($"{offsets[2]:D10} 00000 n ");
        sb.AppendLine($"{offsets[3]:D10} 00000 n ");
        sb.AppendLine($"{offsets[4]:D10} 00000 n ");
        sb.AppendLine($"{offsets[5]:D10} 00000 n ");
        sb.AppendLine($"{offsets[6]:D10} 00000 n ");
        sb.AppendLine("trailer << /Size 7 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xref.ToString());
        sb.AppendLine("%%EOF");
        var pdfBytes = Encoding.Latin1.GetBytes(sb.ToString());

        using var doc = PdfDocument.Open(pdfBytes);

        var allSuggestions = PdfFormAutoDetector.Scan(doc);

        // Should find 1 text field on page 1, 1 checkbox on page 2
        allSuggestions.Should().HaveCount(2);
        allSuggestions[0].PageNumber.Should().Be(1);
        allSuggestions[0].FieldType.Should().Be(PdfFieldType.Text);
        allSuggestions[1].PageNumber.Should().Be(2);
        allSuggestions[1].FieldType.Should().Be(PdfFieldType.Button);
    }

    [Fact]
    public void GraphicsStateTracking_UnmatchedRestore_StaysAtCurrentState()
    {
        // Test unmatched Q (restore without save) - should not crash
        var content = string.Join("\n",
            "Q",              // Restore when stack is empty - should be safe
            "100 700 m 300 700 l S");
        using var doc = PdfDocument.Open(BuildPdf(content));

        // Should handle gracefully
        var suggestions = PdfFormAutoDetector.ScanPage(doc.GetPage(1));

        suggestions.Should().HaveCount(1);
        suggestions[0].FieldType.Should().Be(PdfFieldType.Text);
    }
}
