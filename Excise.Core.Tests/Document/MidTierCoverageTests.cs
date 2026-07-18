using Xunit;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Content;
using Excise.Core.Primitives;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Excise.Core.Tests.Document;

/// <summary>
/// Coverage tests for four mid-tier classes: PdfPage, PdfAnnotationParser,
/// PdfOutlineParser, and ContentStreamParser.
/// Targets uncovered branches: inheritance paths, edge cases, error conditions.
/// </summary>
public class MidTierCoverageTests
{
    #region PDF Builders

    /// <summary>Build a minimal single-page PDF with optional rotation on parent.</summary>
    private static byte[] MakePdfWithPageRotation(int? pageRotate = null, int? parentRotate = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long obj1Pos, obj2Pos, obj3Pos, obj4Pos;

        // obj 1 — catalog
        obj1Pos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine("<< /Type /Catalog /Pages 2 0 R >>");
        sb.AppendLine("endobj");

        // obj 2 — pages node
        obj2Pos = sb.Length;
        sb.AppendLine("2 0 obj");
        var pagesDict = "<< /Type /Pages /Kids [3 0 R] /Count 1";
        if (parentRotate.HasValue)
            pagesDict += $" /Rotate {parentRotate.Value}";
        pagesDict += " >>";
        sb.AppendLine(pagesDict);
        sb.AppendLine("endobj");

        // obj 3 — page
        obj3Pos = sb.Length;
        sb.AppendLine("3 0 obj");
        var pageDict = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R";
        if (pageRotate.HasValue)
            pageDict += $" /Rotate {pageRotate.Value}";
        pageDict += " >>";
        sb.AppendLine(pageDict);
        sb.AppendLine("endobj");

        // obj 4 — content stream
        obj4Pos = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Length 0 >>");
        sb.AppendLine("stream");
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 5");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{obj1Pos:D10} 00000 n ");
        sb.AppendLine($"{obj2Pos:D10} 00000 n ");
        sb.AppendLine($"{obj3Pos:D10} 00000 n ");
        sb.AppendLine($"{obj4Pos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 5 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>Build minimal PDF with annotations.</summary>
    private static byte[] MakePdfWithAnnots(string annotsDef)
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long obj1Pos, obj2Pos, obj3Pos, obj4Pos, obj5Pos;

        obj1Pos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine("<< /Type /Catalog /Pages 2 0 R >>");
        sb.AppendLine("endobj");

        obj2Pos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        obj3Pos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots {annotsDef} >>");
        sb.AppendLine("endobj");

        obj4Pos = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Length 0 >>");
        sb.AppendLine("stream");
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");

        obj5Pos = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine("<< >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 6");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{obj1Pos:D10} 00000 n ");
        sb.AppendLine($"{obj2Pos:D10} 00000 n ");
        sb.AppendLine($"{obj3Pos:D10} 00000 n ");
        sb.AppendLine($"{obj4Pos:D10} 00000 n ");
        sb.AppendLine($"{obj5Pos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 6 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    #endregion

    #region PdfPage Tests

    /// <summary>
    /// Test: Rotation setter with normalized angle (covers the angle calculation branch).
    /// PdfPage.Rotation normalizes angles and rejects non-multiple-of-90 degrees.
    /// </summary>
    [Fact]
    public void PdfPage_SetRotation_NormalizesAngle()
    {
        var pdf = MakePdfWithPageRotation();
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var page = doc.Pages[0];

        // Set rotation to 450 degrees (normalize to 90)
        page.Rotation = 450;
        page.Rotation.Should().Be(90);

        // Set rotation to -90 (normalize to 270)
        page.Rotation = -90;
        page.Rotation.Should().Be(270);

        // Set rotation to 0 should remove the Rotate key
        page.Rotation = 0;
        page.Rotation.Should().Be(0);
        page.Dictionary.ContainsKey("Rotate").Should().BeFalse();
    }

    /// <summary>
    /// Test: Rotation setter rejects invalid angles (not multiple of 90).
    /// Covers the ArgumentException branch.
    /// </summary>
    [Fact]
    public void PdfPage_SetRotation_RejectsInvalidAngle()
    {
        var pdf = MakePdfWithPageRotation();
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var page = doc.Pages[0];

        Action act = () => page.Rotation = 45;
        act.Should().Throw<ArgumentException>().WithMessage("*Rotation must be 0, 90, 180, or 270*");
    }

    /// <summary>
    /// Test: Page rotation inherited from parent /Pages node (not on page itself).
    /// Covers the GetInheritedInt inheritance walk path.
    /// </summary>
    [Fact]
    public void PdfPage_GetRotation_InheritedFromParent()
    {
        var pdf = MakePdfWithPageRotation(pageRotate: null, parentRotate: 90);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var page = doc.Pages[0];

        // Page should inherit the rotation from parent
        page.Rotation.Should().Be(90);
    }

    /// <summary>
    /// Test: ContentStreamBytes with no Contents key (covers early return path).
    /// </summary>
    [Fact]
    public void PdfPage_GetContentStreamBytes_WithNoContents()
    {
        var pdf = MakePdfWithPageRotation();
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var page = doc.Pages[0];

        // Remove the Contents key entirely
        page.Dictionary.Remove("Contents");

        var bytes = page.GetContentStreamBytes();
        bytes.Should().BeEmpty();
    }

    /// <summary>
    /// Test: CropBox falls back to MediaBox when not specified (covers fallback path).
    /// </summary>
    [Fact]
    public void PdfPage_CropBox_FallsBackToMediaBox()
    {
        var pdf = MakePdfWithPageRotation();
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var page = doc.Pages[0];

        var cropBox = page.CropBox;
        var mediaBox = page.MediaBox;
        cropBox.Should().Be(mediaBox);
    }

    #endregion

    #region PdfAnnotationParser Tests

    /// <summary>
    /// Test: ParseDate with no timezone offset (covers the simple date path without offset).
    /// Example: D:20250101000000 (no timezone)
    /// </summary>
    [Fact]
    public void PdfAnnotationParser_ParseDate_NoTimezone()
    {
        var annotsDef = @"[<< /Type /Annot /Subtype /Text /Rect [100 100 150 150] /M (D:20250228143022) >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var parsed = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        parsed.Should().HaveCount(1);
        parsed[0].ModDate.Should().NotBeNull();
        parsed[0].ModDate!.Value.Year.Should().Be(2025);
        parsed[0].ModDate!.Value.Month.Should().Be(2);
        parsed[0].ModDate!.Value.Day.Should().Be(28);
    }

    /// <summary>
    /// Test: ParseDate with Z (UTC) timezone suffix.
    /// Covers the 'Z' branch in the date parsing logic.
    /// </summary>
    [Fact]
    public void PdfAnnotationParser_ParseDate_UtcTimezone()
    {
        var annotsDef = @"[<< /Type /Annot /Subtype /Text /Rect [100 100 150 150] /M (D:20250228143022Z) >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var parsed = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        parsed.Should().HaveCount(1);
        parsed[0].ModDate.Should().NotBeNull();
        parsed[0].ModDate!.Value.Offset.Should().Be(TimeSpan.Zero);
    }

    /// <summary>
    /// Test: Link annotation with neither /A nor /Dest (covers unresolvable link).
    /// Should still be parsed but with null destination and URI.
    /// </summary>
    [Fact]
    public void PdfAnnotationParser_Link_NeitherActionNorDest()
    {
        var annotsDef = @"[<< /Type /Annot /Subtype /Link /Rect [100 100 200 120] >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var parsed = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        parsed.Should().HaveCount(1);
        parsed[0].DestinationPage.Should().BeNull();
        parsed[0].Uri.Should().BeNull();
    }

    /// <summary>
    /// Test: Link with non-URI/non-GoTo action type (e.g., Launch).
    /// Covers the "return (null, null)" branch when action type is unknown.
    /// </summary>
    [Fact]
    public void PdfAnnotationParser_Link_NonUriNonGoToAction()
    {
        var annotsDef = @"[<< /Type /Annot /Subtype /Link /Rect [100 100 200 120] /A << /S /Launch /F (app.exe) >> >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var parsed = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        parsed.Should().HaveCount(1);
        parsed[0].DestinationPage.Should().BeNull();
        parsed[0].Uri.Should().BeNull();
    }

    /// <summary>
    /// Test: Color parsing with CMYK (all zeros) edge case.
    /// Covers the CMYK → RGB conversion with boundary values.
    /// </summary>
    [Fact]
    public void PdfAnnotationParser_Color_CmykAllZeros()
    {
        var annotsDef = @"[<< /Type /Annot /Subtype /Highlight /Rect [100 100 200 120] /C [0 0 0 0] >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var parsed = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        parsed.Should().HaveCount(1);
        // CMYK (0,0,0,0) converts to white: R=1, G=1, B=1
        var color = parsed[0].Color;
        color.Should().NotBeNull();
        color!.Value.R.Should().Be(1.0);
        color!.Value.G.Should().Be(1.0);
        color!.Value.B.Should().Be(1.0);
    }

    /// <summary>
    /// Test: QuadPoints with non-multiple-of-8 length (covers rejection path).
    /// Per spec, QuadPoints must be array of 8n coordinates.
    /// </summary>
    [Fact]
    public void PdfAnnotationParser_QuadPoints_InvalidLength()
    {
        var annotsDef = @"[<< /Type /Annot /Subtype /Highlight /Rect [100 100 200 120] /QuadPoints [100 100 150 100 150 150 100 150 175] >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var parsed = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        parsed.Should().HaveCount(1);
        parsed[0].QuadPoints.Should().BeNull(); // Should be rejected
    }

    #endregion

    #region PdfOutlineParser Tests

    /// <summary>
    /// Test: Outline tree with missing /First key at root.
    /// Covers the "firstObj == null" return empty branch.
    /// </summary>
    [Fact]
    public void PdfOutlineParser_Parse_NoFirstKey()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long obj1Pos, obj2Pos, obj3Pos, obj4Pos;

        obj1Pos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine("<< /Type /Catalog /Pages 2 0 R /Outlines 4 0 R >>");
        sb.AppendLine("endobj");

        obj2Pos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        obj3Pos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        obj4Pos = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Type /Outlines >>");  // Missing /First key
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 5");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{obj1Pos:D10} 00000 n ");
        sb.AppendLine($"{obj2Pos:D10} 00000 n ");
        sb.AppendLine($"{obj3Pos:D10} 00000 n ");
        sb.AppendLine($"{obj4Pos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 5 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        var pdf = Encoding.Latin1.GetBytes(sb.ToString());
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);

        var parsed = PdfOutlineParser.Parse(doc);
        parsed.Should().BeEmpty();
    }

    /// <summary>
    /// Test: Outline item with negative /Count (indicates collapsed tree in PDF).
    /// Still parses children normally, but /Count being negative is allowed.
    /// </summary>
    [Fact]
    public void PdfOutlineParser_Parse_NegativeCount()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long obj1Pos, obj2Pos, obj3Pos, obj4Pos, obj5Pos;

        obj1Pos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine("<< /Type /Catalog /Pages 2 0 R /Outlines 4 0 R >>");
        sb.AppendLine("endobj");

        obj2Pos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        obj3Pos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        obj4Pos = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Type /Outlines /First 5 0 R /Last 5 0 R /Count 1 >>");
        sb.AppendLine("endobj");

        obj5Pos = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine("<< /Title (Chapter 1) /Parent 4 0 R /Count -2 >>");  // Negative count
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 6");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{obj1Pos:D10} 00000 n ");
        sb.AppendLine($"{obj2Pos:D10} 00000 n ");
        sb.AppendLine($"{obj3Pos:D10} 00000 n ");
        sb.AppendLine($"{obj4Pos:D10} 00000 n ");
        sb.AppendLine($"{obj5Pos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 6 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        var pdf = Encoding.Latin1.GetBytes(sb.ToString());
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);

        var parsed = PdfOutlineParser.Parse(doc);
        parsed.Should().HaveCount(1);
        parsed[0].Title.Should().Be("Chapter 1");
    }

    #endregion

    #region ContentStreamParser Tests

    /// <summary>
    /// Test: Comment in middle of stream (should be skipped).
    /// Covers the SkipWhitespaceAndComments logic.
    /// </summary>
    [Fact]
    public void ContentStreamParser_Parse_IgnoresComments()
    {
        var content = Encoding.UTF8.GetBytes(
            "BT\n" +
            "% This is a comment\n" +
            "/F1 12 Tf\n" +
            "(Hello) Tj\n" +
            "ET"
        );

        var parser = new ContentStreamParser(content);
        var stream = parser.Parse();

        // Should parse operators, skipping the comment
        stream.Operators.Select(op => op.Name).Should().Contain(new[] { "BT", "Tf", "Tj", "ET" });
    }

    /// <summary>
    /// Test: Q (restore graphics state) without matching q (stack underflow).
    /// Parser should handle gracefully, not crash.
    /// </summary>
    [Fact]
    public void ContentStreamParser_Parse_QWithoutMatchingQ()
    {
        var content = Encoding.UTF8.GetBytes(
            "1 0 0 1 10 20 cm\n" +
            "Q\n" +  // Q without matching q — stack underflow
            "BT (text) Tj ET"
        );

        var parser = new ContentStreamParser(content);
        var stream = parser.Parse();

        // Should not crash, parser should continue
        stream.Operators.Should().NotBeEmpty();
        stream.Operators.Select(op => op.Name).Should().Contain("Q");
    }

    /// <summary>
    /// Test: Unknown operator name with valid operands (should not crash).
    /// Covers the default case in operator parsing.
    /// </summary>
    [Fact]
    public void ContentStreamParser_Parse_UnknownOperator()
    {
        var content = Encoding.UTF8.GetBytes("1 2 3 UnknownOp");

        var parser = new ContentStreamParser(content);
        var stream = parser.Parse();

        // Unknown operators should be skipped or stored, but not crash
        stream.Operators.Should().NotBeNull();
    }

    #endregion
}
