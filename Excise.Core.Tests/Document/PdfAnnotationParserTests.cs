using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Core.Tests.Document;

/// <summary>
/// Comprehensive tests for <see cref="PdfAnnotationParser"/> covering all annotation subtypes,
/// color formats, quad points parsing, date parsing, and edge cases.
/// Tests are synthetic in-memory PDFs so no fixture files are required.
/// </summary>
public class PdfAnnotationParserTests
{
    // ─── PDF builders ───────────────────────────────────────────────────────────

    /// <summary>Build a minimal single-page PDF with the given /Annots array.</summary>
    private static byte[] MakePdfWithAnnots(string annotsDef)
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long obj1Pos, obj2Pos, obj3Pos, obj4Pos, obj5Pos;

        // obj 1 — catalog
        obj1Pos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine("<< /Type /Catalog /Pages 2 0 R >>");
        sb.AppendLine("endobj");

        // obj 2 — pages node
        obj2Pos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        // obj 3 — page
        obj3Pos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots {annotsDef} >>");
        sb.AppendLine("endobj");

        // obj 4 — content stream (empty)
        obj4Pos = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Length 0 >>");
        sb.AppendLine("stream");
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");

        // obj 5 — unused placeholder
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

    // ─── Parser method tests ─────────────────────────────────────────────────

    [Fact]
    public void Parse_NoAnnots_ReturnsEmpty()
    {
        var pdf = MakePdfWithAnnots("[]");
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Parse_NullAnnotsObject_ReturnsEmpty()
    {
        var pdf = MakePdfWithAnnots("null");
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Parse_AnnotsNotArray_ReturnsEmpty()
    {
        var pdf = MakePdfWithAnnots("<< /Name (NotAnArray) >>");
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Parse_AnnotWithMissingRect_SkipsIt()
    {
        var annotsDef = @"[<< /Type /Annot /Subtype /Text /Contents (No rect) >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Parse_RectWithFewerThan4Elements_SkipsIt()
    {
        var annotsDef = @"[<< /Type /Annot /Subtype /Text /Rect [0 0] >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Parse_RectAsNull_SkipsIt()
    {
        var annotsDef = @"[<< /Type /Annot /Subtype /Text /Rect null >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Parse_SingleTextAnnotation_ParsesAllFields()
    {
        var annotsDef = @"[<<
            /Type /Annot
            /Subtype /Text
            /Rect [72 720 108 756]
            /Contents (Hello World)
            /T (Marc Jones)
            /NM (unique-id)
            /Name /Note
            /F 68
        >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        result.Should().HaveCount(1);
        var a = result[0];
        a.Subtype.Should().Be(PdfAnnotationSubtype.Text);
        a.Contents.Should().Be("Hello World");
        a.Author.Should().Be("Marc Jones");
        a.Name.Should().Be("unique-id");
        a.IconName.Should().Be("Note");
        a.Rect.Left.Should().Be(72);
        a.Rect.Bottom.Should().Be(720);
        a.Rect.Right.Should().Be(108);
        a.Rect.Top.Should().Be(756);
        a.Flags.Should().HaveFlag(PdfAnnotationFlags.Print);
        a.Flags.Should().HaveFlag(PdfAnnotationFlags.ReadOnly);
    }

    // ─── Subtype parsing tests ───────────────────────────────────────────────

    [Theory]
    [InlineData("Text", PdfAnnotationSubtype.Text)]
    [InlineData("Link", PdfAnnotationSubtype.Link)]
    [InlineData("FreeText", PdfAnnotationSubtype.FreeText)]
    [InlineData("Line", PdfAnnotationSubtype.Line)]
    [InlineData("Square", PdfAnnotationSubtype.Square)]
    [InlineData("Circle", PdfAnnotationSubtype.Circle)]
    [InlineData("Polygon", PdfAnnotationSubtype.Polygon)]
    [InlineData("PolyLine", PdfAnnotationSubtype.PolyLine)]
    [InlineData("Highlight", PdfAnnotationSubtype.Highlight)]
    [InlineData("Underline", PdfAnnotationSubtype.Underline)]
    [InlineData("Squiggly", PdfAnnotationSubtype.Squiggly)]
    [InlineData("StrikeOut", PdfAnnotationSubtype.StrikeOut)]
    [InlineData("Stamp", PdfAnnotationSubtype.Stamp)]
    [InlineData("Caret", PdfAnnotationSubtype.Caret)]
    [InlineData("Ink", PdfAnnotationSubtype.Ink)]
    [InlineData("Popup", PdfAnnotationSubtype.Popup)]
    [InlineData("FileAttachment", PdfAnnotationSubtype.FileAttachment)]
    [InlineData("Sound", PdfAnnotationSubtype.Sound)]
    [InlineData("Movie", PdfAnnotationSubtype.Movie)]
    [InlineData("Widget", PdfAnnotationSubtype.Widget)]
    [InlineData("Screen", PdfAnnotationSubtype.Screen)]
    [InlineData("Watermark", PdfAnnotationSubtype.Watermark)]
    [InlineData("Redact", PdfAnnotationSubtype.Redact)]
    public void Parse_AllAnnotationSubtypes(string subtypeName, PdfAnnotationSubtype expectedSubtype)
    {
        var annotsDef = $@"[<< /Type /Annot /Subtype /{subtypeName} /Rect [0 0 100 20] >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        result.Should().HaveCount(1);
        result[0].Subtype.Should().Be(expectedSubtype);
    }

    [Fact]
    public void Parse_UnknownSubtype_ParsedAsUnknown()
    {
        var annotsDef = @"[<< /Type /Annot /Subtype /FutureType /Rect [0 0 10 10] >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        result.Should().HaveCount(1);
        result[0].Subtype.Should().Be(PdfAnnotationSubtype.Unknown);
    }

    // ─── Color parsing tests ─────────────────────────────────────────────────

    [Fact]
    public void Parse_GrayColor_ConvertedToRgb()
    {
        var annotsDef = @"[<< /Type /Annot /Subtype /Text /Rect [0 0 10 10] /C [0.5] >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        result.Should().HaveCount(1);
        var color = result[0].Color;
        color.Should().NotBeNull();
        color!.Value.R.Should().BeApproximately(0.5, 0.01);
        color!.Value.G.Should().BeApproximately(0.5, 0.01);
        color!.Value.B.Should().BeApproximately(0.5, 0.01);
    }

    [Fact]
    public void Parse_RgbColor_Parsed()
    {
        var annotsDef = @"[<< /Type /Annot /Subtype /Text /Rect [0 0 10 10] /C [1.0 0.5 0.0] >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        var color = result[0].Color;
        color!.Value.R.Should().BeApproximately(1.0, 0.01);
        color!.Value.G.Should().BeApproximately(0.5, 0.01);
        color!.Value.B.Should().BeApproximately(0.0, 0.01);
    }

    [Fact]
    public void Parse_CmykColor_ConvertedToRgb()
    {
        // CMYK [0 0 0 0] = full white in CMYK
        var annotsDef = @"[<< /Type /Annot /Subtype /Text /Rect [0 0 10 10] /C [0 0 0 0] >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        var color = result[0].Color;
        color.Should().NotBeNull();
        color!.Value.R.Should().BeApproximately(1.0, 0.01);
        color!.Value.G.Should().BeApproximately(1.0, 0.01);
        color!.Value.B.Should().BeApproximately(1.0, 0.01);
    }

    [Fact]
    public void Parse_CmykColor_BlackInk_ConvertsCorrectly()
    {
        // CMYK [0 0 0 1] renders as process black in the shared DeviceCMYK
        // screen preview, not pure DeviceRGB black.
        var annotsDef = @"[<< /Type /Annot /Subtype /Text /Rect [0 0 10 10] /C [0 0 0 1] >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        var color = result[0].Color;
        color!.Value.R.Should().BeApproximately(35.0 / 255.0, 0.01);
        color!.Value.G.Should().BeApproximately(31.0 / 255.0, 0.01);
        color!.Value.B.Should().BeApproximately(32.0 / 255.0, 0.01);
    }

    [Fact]
    public void Parse_InvalidColorArray_NoColor()
    {
        // Invalid array length (2 elements)
        var annotsDef = @"[<< /Type /Annot /Subtype /Text /Rect [0 0 10 10] /C [0.5 0.5] >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        result[0].Color.Should().BeNull();
    }

    [Fact]
    public void Parse_ColorNotArray_NoColor()
    {
        var annotsDef = @"[<< /Type /Annot /Subtype /Text /Rect [0 0 10 10] /C (NotArray) >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        result[0].Color.Should().BeNull();
    }

    // ─── QuadPoints parsing tests ────────────────────────────────────────────

    [Fact]
    public void Parse_QuadPoints_SingleQuad_Parsed()
    {
        var annotsDef = @"[<<
            /Type /Annot
            /Subtype /Highlight
            /Rect [100 700 300 720]
            /QuadPoints [100 720 300 720 100 700 300 700]
        >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        result[0].QuadPoints.Should().NotBeNull().And.HaveCount(1);
        var quad = result[0].QuadPoints![0];
        quad.Left.Should().Be(100);
        quad.Right.Should().Be(300);
        quad.Bottom.Should().Be(700);
        quad.Top.Should().Be(720);
    }

    [Fact]
    public void Parse_QuadPoints_MultipleQuads_Parsed()
    {
        // Two quads (16 numbers total)
        var annotsDef = @"[<<
            /Type /Annot
            /Subtype /Highlight
            /Rect [0 0 400 800]
            /QuadPoints [100 720 300 720 100 700 300 700 400 600 500 600 400 580 500 580]
        >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        result[0].QuadPoints.Should().NotBeNull().And.HaveCount(2);
    }

    [Fact]
    public void Parse_QuadPoints_InvalidLength_NoQuadPoints()
    {
        // 7 elements (not multiple of 8)
        var annotsDef = @"[<<
            /Type /Annot
            /Subtype /Highlight
            /Rect [0 0 100 100]
            /QuadPoints [1 2 3 4 5 6 7]
        >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        result[0].QuadPoints.Should().BeNull();
    }

    [Fact]
    public void Parse_QuadPoints_TooFewElements_NoQuadPoints()
    {
        // Less than 8 elements (one quad minimum)
        var annotsDef = @"[<<
            /Type /Annot
            /Subtype /Highlight
            /Rect [0 0 100 100]
            /QuadPoints [1 2 3 4]
        >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        result[0].QuadPoints.Should().BeNull();
    }

    [Fact]
    public void Parse_QuadPoints_NotArray_NoQuadPoints()
    {
        var annotsDef = @"[<<
            /Type /Annot
            /Subtype /Highlight
            /Rect [0 0 100 100]
            /QuadPoints (NotArray)
        >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        result[0].QuadPoints.Should().BeNull();
    }

    [Fact]
    public void Parse_QuadPoints_CalculatesBoundingBox()
    {
        // Four corners: (10, 10), (90, 10), (10, 90), (90, 90)
        // Expected bounding box: [10, 10, 90, 90]
        var annotsDef = @"[<<
            /Type /Annot
            /Subtype /Highlight
            /Rect [10 10 90 90]
            /QuadPoints [10 90 90 90 10 10 90 10]
        >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        var quad = result[0].QuadPoints![0];
        quad.Left.Should().Be(10);
        quad.Right.Should().Be(90);
        quad.Bottom.Should().Be(10);
        quad.Top.Should().Be(90);
    }

    // ─── Date parsing tests ──────────────────────────────────────────────────

    [Fact]
    public void Parse_DateWithDPrefix_Parsed()
    {
        var annotsDef = @"[<<
            /Type /Annot
            /Subtype /Text
            /Rect [0 0 100 100]
            /M (D:20250101120000Z)
        >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        var date = result[0].ModDate;
        date.Should().NotBeNull();
        date!.Value.Year.Should().Be(2025);
        date!.Value.Month.Should().Be(1);
        date!.Value.Day.Should().Be(1);
    }

    [Fact]
    public void Parse_DateWithoutDPrefix_Parsed()
    {
        var annotsDef = @"[<<
            /Type /Annot
            /Subtype /Text
            /Rect [0 0 100 100]
            /M (20250315)
        >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        var date = result[0].ModDate;
        date!.Value.Year.Should().Be(2025);
        date!.Value.Month.Should().Be(3);
        date!.Value.Day.Should().Be(15);
    }

    [Fact]
    public void Parse_DateWithPositiveOffset_Parsed()
    {
        // D:20250101120000+05'00'
        var annotsDef = @"[<<
            /Type /Annot
            /Subtype /Text
            /Rect [0 0 100 100]
            /M (D:20250101120000+05'00')
        >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        var date = result[0].ModDate;
        date.Should().NotBeNull();
        date!.Value.Offset.Should().Be(new TimeSpan(5, 0, 0));
    }

    [Fact]
    public void Parse_DateWithNegativeOffset_Parsed()
    {
        var annotsDef = @"[<<
            /Type /Annot
            /Subtype /Text
            /Rect [0 0 100 100]
            /M (D:20250101120000-08'30')
        >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        var date = result[0].ModDate;
        date!.Value.Offset.Should().Be(new TimeSpan(-8, -30, 0));
    }

    [Fact]
    public void Parse_InvalidDate_NoDate()
    {
        var annotsDef = @"[<<
            /Type /Annot
            /Subtype /Text
            /Rect [0 0 100 100]
            /M (InvalidDateString)
        >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        result[0].ModDate.Should().BeNull();
    }

    [Fact]
    public void Parse_CreationDateField_Parsed()
    {
        var annotsDef = @"[<<
            /Type /Annot
            /Subtype /Text
            /Rect [0 0 100 100]
            /CreationDate (D:20240101)
        >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        result[0].CreationDate.Should().NotBeNull();
        result[0].CreationDate!.Value.Year.Should().Be(2024);
    }

    // ─── Link resolution tests ───────────────────────────────────────────────

    [Fact]
    public void Parse_LinkAnnotationWithGoToAction_ResolvesPage()
    {
        var pdf = MakePdfWithMultiPageAnnot();
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);

        // Build page ref map (would be done by caller)
        var pageRefMap = new Dictionary<(int, int), int> { { (3, 0), 1 }, { (4, 0), 2 } };
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, pageRefMap, null);

        var link = result.FirstOrDefault(a => a.Subtype == PdfAnnotationSubtype.Link);
        link.Should().NotBeNull();
        link!.DestinationPage.Should().Be(2);
    }

    [Fact]
    public void Parse_LinkAnnotationWithUriAction_ExposesUri()
    {
        var annotsDef = @"[<<
            /Type /Annot
            /Subtype /Link
            /Rect [0 0 100 20]
            /A << /S /URI /URI (https://example.com) >>
        >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        var link = result[0];
        link.Uri.Should().Be("https://example.com");
        link.DestinationPage.Should().BeNull();
    }

    [Fact]
    public void Parse_LinkWithoutPageInMap_NoDestinationPage()
    {
        var pdf = MakePdfWithMultiPageAnnot();
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);

        // Empty page ref map
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        var link = result.FirstOrDefault(a => a.Subtype == PdfAnnotationSubtype.Link);
        link!.DestinationPage.Should().BeNull();
    }

    // ─── Multiple annotations tests ──────────────────────────────────────────

    [Fact]
    public void Parse_MultipleAnnotations_AllParsed()
    {
        var annotsDef = @"[
            << /Type /Annot /Subtype /Text /Rect [10 10 50 50] /Contents (note) >>
            << /Type /Annot /Subtype /Highlight /Rect [60 60 200 80] >>
            << /Type /Annot /Subtype /Stamp /Rect [100 500 300 600] >>
        ]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        result.Should().HaveCount(3);
        result.Select(a => a.Subtype).Should().BeEquivalentTo(new[]
        {
            PdfAnnotationSubtype.Text, PdfAnnotationSubtype.Highlight, PdfAnnotationSubtype.Stamp
        });
    }

    [Fact]
    public void Parse_MixedValidAndInvalidAnnotations_OnlyValidParsed()
    {
        var annotsDef = @"[
            << /Type /Annot /Subtype /Text /Rect [10 10 50 50] >>
            << /Type /Annot /Subtype /Link >>
            << /Type /Annot /Subtype /Stamp /Rect [100 500 300 600] >>
        ]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        result.Should().HaveCount(2);
        result.Select(a => a.Subtype).Should().BeEquivalentTo(new[]
        {
            PdfAnnotationSubtype.Text, PdfAnnotationSubtype.Stamp
        });
    }

    [Fact]
    public void Parse_AnnotationReferenceInArray_Resolved()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long obj1Pos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine("<< /Type /Catalog /Pages 2 0 R >>");
        sb.AppendLine("endobj");

        long obj2Pos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        long obj3Pos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [5 0 R] >>");
        sb.AppendLine("endobj");

        long obj4Pos = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Length 0 >>");
        sb.AppendLine("stream");
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");

        long obj5Pos = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine("<< /Type /Annot /Subtype /Text /Rect [0 0 100 100] /Contents (From Ref) >>");
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
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        result.Should().HaveCount(1);
        result[0].Contents.Should().Be("From Ref");
    }

    // ─── Flag parsing tests ──────────────────────────────────────────────────

    [Fact]
    public void Parse_AnnotationFlags_AllFlagsRecognized()
    {
        // F = 1 + 2 + 4 + 8 + 16 + 32 + 64 + 128 + 256 + 512 = 1023
        var annotsDef = @"[<< /Type /Annot /Subtype /Text /Rect [0 0 10 10] /F 1023 >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        var flags = result[0].Flags;
        flags.Should().HaveFlag(PdfAnnotationFlags.Invisible);
        flags.Should().HaveFlag(PdfAnnotationFlags.Hidden);
        flags.Should().HaveFlag(PdfAnnotationFlags.Print);
        flags.Should().HaveFlag(PdfAnnotationFlags.NoZoom);
        flags.Should().HaveFlag(PdfAnnotationFlags.NoRotate);
        flags.Should().HaveFlag(PdfAnnotationFlags.NoView);
        flags.Should().HaveFlag(PdfAnnotationFlags.ReadOnly);
        flags.Should().HaveFlag(PdfAnnotationFlags.Locked);
        flags.Should().HaveFlag(PdfAnnotationFlags.ToggleNoView);
        flags.Should().HaveFlag(PdfAnnotationFlags.LockedContents);
    }

    [Fact]
    public void Parse_NoFlags_DefaultsToZero()
    {
        var annotsDef = @"[<< /Type /Annot /Subtype /Text /Rect [0 0 10 10] >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        result[0].Flags.Should().Be(PdfAnnotationFlags.None);
    }

    // ─── Text-markup classification tests ────────────────────────────────────

    [Theory]
    [InlineData("Highlight")]
    [InlineData("Underline")]
    [InlineData("Squiggly")]
    [InlineData("StrikeOut")]
    public void Parse_TextMarkupAnnotations_IsTextMarkupTrue(string subtypeName)
    {
        var annotsDef = $@"[<< /Type /Annot /Subtype /{subtypeName} /Rect [0 0 100 20] >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        result[0].IsTextMarkup.Should().BeTrue();
    }

    [Fact]
    public void Parse_NonTextMarkup_IsTextMarkupFalse()
    {
        var annotsDef = @"[<< /Type /Annot /Subtype /Text /Rect [0 0 100 100] >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        result[0].IsTextMarkup.Should().BeFalse();
    }

    // ─── Open and IconName tests ────────────────────────────────────────────

    [Fact]
    public void Parse_OpenFlagTrue_Parsed()
    {
        var annotsDef = @"[<< /Type /Annot /Subtype /Text /Rect [0 0 10 10] /Open true >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        result[0].IsOpen.Should().BeTrue();
    }

    [Fact]
    public void Parse_OpenFlagFalse_Parsed()
    {
        var annotsDef = @"[<< /Type /Annot /Subtype /Text /Rect [0 0 10 10] /Open false >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        result[0].IsOpen.Should().BeFalse();
    }

    [Fact]
    public void Parse_NoOpenFlag_DefaultsFalse()
    {
        var annotsDef = @"[<< /Type /Annot /Subtype /Text /Rect [0 0 10 10] >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        result[0].IsOpen.Should().BeFalse();
    }

    [Fact]
    public void Parse_IconNameParsed()
    {
        var annotsDef = @"[<< /Type /Annot /Subtype /Text /Rect [0 0 10 10] /Name /Comment >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        result[0].IconName.Should().Be("Comment");
    }

    // ─── RawDictionary always available ─────────────────────────────────────

    [Fact]
    public void Parse_RawDictionary_AlwaysAvailable()
    {
        var annotsDef = @"[<< /Type /Annot /Subtype /Text /Rect [0 0 10 10] >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        result[0].RawDictionary.Should().NotBeNull();
        result[0].RawDictionary.GetNameOrNull("Subtype").Should().Be("Text");
    }

    // ─── Phase 10 subtype-specific data ──────────────────────────────────────

    [Fact]
    public void Parse_LineAnnotation_LEndpoints_Captured()
    {
        var annotsDef = @"[<<
            /Type /Annot /Subtype /Line /Rect [0 0 200 200]
            /L [10 20 110 120]
        >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        result[0].LineEndpoints.Should().NotBeNull();
        var (x1, y1, x2, y2) = result[0].LineEndpoints!.Value;
        (x1, y1, x2, y2).Should().Be((10.0, 20.0, 110.0, 120.0));
    }

    [Fact]
    public void Parse_LineAnnotation_NoLArray_LineEndpointsNull()
    {
        var annotsDef = @"[<< /Type /Annot /Subtype /Line /Rect [0 0 200 200] >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        result[0].LineEndpoints.Should().BeNull();
    }

    [Fact]
    public void Parse_NonLineAnnotation_LineEndpoints_AreNull()
    {
        // /L is set but subtype is Square — only Line populates LineEndpoints.
        var annotsDef = @"[<< /Type /Annot /Subtype /Square /Rect [0 0 200 200] /L [10 20 110 120] >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        result[0].LineEndpoints.Should().BeNull();
    }

    [Fact]
    public void Parse_PolygonAnnotation_VerticesCaptured()
    {
        var annotsDef = @"[<<
            /Type /Annot /Subtype /Polygon /Rect [0 0 300 300]
            /Vertices [10 20 110 30 60 200]
        >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        result[0].Vertices.Should().NotBeNull();
        result[0].Vertices.Should().HaveCount(3);
        result[0].Vertices![0].Should().Be((10.0, 20.0));
        result[0].Vertices![2].Should().Be((60.0, 200.0));
    }

    [Fact]
    public void Parse_PolyLineAnnotation_VerticesCaptured()
    {
        var annotsDef = @"[<<
            /Type /Annot /Subtype /PolyLine /Rect [0 0 300 300]
            /Vertices [0 0 50 50 100 0]
        >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        result[0].Vertices.Should().HaveCount(3);
    }

    [Fact]
    public void Parse_InkAnnotation_StrokesCaptured()
    {
        var annotsDef = @"[<<
            /Type /Annot /Subtype /Ink /Rect [0 0 300 300]
            /InkList [
                [10 20 30 40 50 60]
                [70 80 90 100]
            ]
        >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        var ink = result[0].InkStrokes!;
        ink.Should().HaveCount(2);
        ink[0].Should().HaveCount(3);
        ink[1].Should().HaveCount(2);
        ink[0][0].Should().Be((10.0, 20.0));
        ink[1][1].Should().Be((90.0, 100.0));
    }

    [Fact]
    public void Parse_InkAnnotation_NoInkList_StrokesNull()
    {
        var annotsDef = @"[<< /Type /Annot /Subtype /Ink /Rect [0 0 300 300] >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        result[0].InkStrokes.Should().BeNull();
    }

    [Fact]
    public void Parse_FileAttachment_ReadsName_FromUF()
    {
        var annotsDef = @"[<<
            /Type /Annot /Subtype /FileAttachment /Rect [0 0 20 20]
            /FS << /Type /Filespec /UF (data.csv) /F (data.csv) >>
        >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        result[0].AttachmentFileName.Should().Be("data.csv");
        // No /EF embedded stream → bytes null
        result[0].AttachmentBytes.Should().BeNull();
    }

    [Fact]
    public void Parse_FileAttachment_FallsBackTo_F_WhenNoUF()
    {
        var annotsDef = @"[<<
            /Type /Annot /Subtype /FileAttachment /Rect [0 0 20 20]
            /FS << /Type /Filespec /F (legacy.txt) >>
        >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        result[0].AttachmentFileName.Should().Be("legacy.txt");
    }

    [Fact]
    public void Parse_FileAttachment_NoFS_AllAttachmentFieldsNull()
    {
        var annotsDef = @"[<< /Type /Annot /Subtype /FileAttachment /Rect [0 0 20 20] >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        result[0].AttachmentFileName.Should().BeNull();
        result[0].AttachmentBytes.Should().BeNull();
        result[0].AttachmentMimeType.Should().BeNull();
    }

    [Fact]
    public void Parse_BorderArray_WidthAndDashExtracted()
    {
        var annotsDef = @"[<<
            /Type /Annot /Subtype /Square /Rect [0 0 100 100]
            /Border [0 0 2.5 [3 1]]
        >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        result[0].BorderWidth.Should().Be(2.5);
        result[0].BorderDashPattern.Should().NotBeNull();
        result[0].BorderDashPattern.Should().Equal(3.0, 1.0);
    }

    [Fact]
    public void Parse_BSDictionary_OverridesBorderArrayWidth()
    {
        var annotsDef = @"[<<
            /Type /Annot /Subtype /Square /Rect [0 0 100 100]
            /Border [0 0 1]
            /BS << /Type /Border /W 4 /S /D /D [5 2] >>
        >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        result[0].BorderWidth.Should().Be(4.0);
        result[0].BorderStyle.Should().Be("D");
        result[0].BorderDashPattern.Should().Equal(5.0, 2.0);
    }

    [Fact]
    public void Parse_NoBorder_BorderFieldsNull()
    {
        var annotsDef = @"[<< /Type /Annot /Subtype /Square /Rect [0 0 100 100] >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        result[0].BorderWidth.Should().BeNull();
        result[0].BorderStyle.Should().BeNull();
        result[0].BorderDashPattern.Should().BeNull();
    }

    [Fact]
    public void Parse_AppearancePresent_HasAppearanceTrue()
    {
        var annotsDef = @"[<<
            /Type /Annot /Subtype /Stamp /Rect [0 0 100 100]
            /AP << /N << >> >>
        >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        result[0].HasAppearance.Should().BeTrue();
    }

    [Fact]
    public void Parse_NoAppearance_HasAppearanceFalse()
    {
        var annotsDef = @"[<< /Type /Annot /Subtype /Stamp /Rect [0 0 100 100] >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        result[0].HasAppearance.Should().BeFalse();
    }

    [Fact]
    public void Parse_VerticesArray_OddNumberOfCoords_TruncatesUnpaired()
    {
        // 5 numbers — last one has no Y partner; should yield 2 complete points.
        var annotsDef = @"[<<
            /Type /Annot /Subtype /Polygon /Rect [0 0 100 100]
            /Vertices [0 0 50 50 100]
        >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        result[0].Vertices.Should().HaveCount(2);
    }

    [Fact]
    public void Parse_LineEndpoints_TooFewElements_LineEndpointsNull()
    {
        var annotsDef = @"[<<
            /Type /Annot /Subtype /Line /Rect [0 0 100 100]
            /L [10 20 110]
        >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        result[0].LineEndpoints.Should().BeNull();
    }

    // ─── FileAttachment edge cases (covers lines 180-184) ─────────────────────

    [Fact]
    public void Parse_FileAttachment_NoEFDict_ReturnsFileNameOnly()
    {
        // /FS present but /EF missing — should get filename but no bytes
        var annotsDef = @"[<<
            /Type /Annot /Subtype /FileAttachment /Rect [0 0 20 20]
            /FS << /Type /Filespec /UF (missing.txt) >>
        >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        result[0].AttachmentFileName.Should().Be("missing.txt");
        result[0].AttachmentBytes.Should().BeNull();
        result[0].AttachmentMimeType.Should().BeNull();
    }

    [Fact]
    public void Parse_FileAttachment_EFDictNotDictionary_ReturnsFileNameOnly()
    {
        // /EF is present but not a dictionary
        var annotsDef = @"[<<
            /Type /Annot /Subtype /FileAttachment /Rect [0 0 20 20]
            /FS << /Type /Filespec /UF (test.txt) /EF (invalid) >>
        >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        result[0].AttachmentFileName.Should().Be("test.txt");
        result[0].AttachmentBytes.Should().BeNull();
    }

    [Fact]
    public void Parse_FileAttachment_NoFileStreamInEF_ReturnsFileNameOnly()
    {
        // /EF dict is present but /F and /UF both missing
        var annotsDef = @"[<<
            /Type /Annot /Subtype /FileAttachment /Rect [0 0 20 20]
            /FS << /Type /Filespec /UF (test.txt) /EF << >> >>
        >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        result[0].AttachmentFileName.Should().Be("test.txt");
        result[0].AttachmentBytes.Should().BeNull();
    }

    [Fact]
    public void Parse_FileAttachment_FileStreamNotStream_ReturnsFileNameOnly()
    {
        // /F in /EF is present but not a stream object
        var annotsDef = @"[<<
            /Type /Annot /Subtype /FileAttachment /Rect [0 0 20 20]
            /FS << /Type /Filespec /UF (test.txt) /EF << /F (notstream) >> >>
        >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        result[0].AttachmentFileName.Should().Be("test.txt");
        result[0].AttachmentBytes.Should().BeNull();
    }

    [Fact]
    public void Parse_FileAttachment_FSNotDictionary_AllFieldsNull()
    {
        // /FS present but not a dictionary
        var annotsDef = @"[<<
            /Type /Annot /Subtype /FileAttachment /Rect [0 0 20 20]
            /FS (notdict)
        >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        result[0].AttachmentFileName.Should().BeNull();
        result[0].AttachmentBytes.Should().BeNull();
        result[0].AttachmentMimeType.Should().BeNull();
    }

    // ─── BorderStyle dictionary tests ─────────────────────────────────────────

    [Fact]
    public void Parse_BSDictionary_AllFields_Parsed()
    {
        // /BS with all optional fields: /W, /S, /D
        var annotsDef = @"[<<
            /Type /Annot /Subtype /Square /Rect [0 0 100 100]
            /BS << /W 3 /S /S /D [2 3] >>
        >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        result[0].BorderWidth.Should().Be(3.0);
        result[0].BorderStyle.Should().Be("S");
        result[0].BorderDashPattern.Should().NotBeNull();
        result[0].BorderDashPattern.Should().Equal(2.0, 3.0);
    }

    [Fact]
    public void Parse_BSDictionary_PartialFields_OnlySetPresent()
    {
        // /BS with only /S (no /W or /D)
        var annotsDef = @"[<<
            /Type /Annot /Subtype /Square /Rect [0 0 100 100]
            /Border [0 0 1]
            /BS << /S /D >>
        >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        result[0].BorderWidth.Should().Be(1.0);
        result[0].BorderStyle.Should().Be("D");
        result[0].BorderDashPattern.Should().BeNull();
    }

    [Fact]
    public void Parse_BSDictionary_RealNumber_W_Parsed()
    {
        // /BS with /W as floating-point number (not integer)
        var annotsDef = @"[<<
            /Type /Annot /Subtype /Square /Rect [0 0 100 100]
            /BS << /W 2.75 >>
        >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        result[0].BorderWidth.Should().BeApproximately(2.75, 0.01);
    }

    [Fact]
    public void Parse_BSNotDictionary_IgnoredGracefully()
    {
        // /BS is present but not a dictionary (e.g., array)
        var annotsDef = @"[<<
            /Type /Annot /Subtype /Square /Rect [0 0 100 100]
            /Border [0 0 2]
            /BS [1 2 3]
        >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        result[0].BorderWidth.Should().Be(2.0);
        result[0].BorderStyle.Should().BeNull();
    }

    // ─── Link action parsing edge cases ────────────────────────────────────────

    [Fact]
    public void Parse_LinkWithGoToRAction_IgnoredAsUnsupported()
    {
        // /A with /S /GoToR (external link) — should return (null, null)
        var annotsDef = @"[<<
            /Type /Annot /Subtype /Link /Rect [0 0 100 20]
            /A << /S /GoToR /F (external.pdf) >>
        >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        var link = result[0];
        link.DestinationPage.Should().BeNull();
        link.Uri.Should().BeNull();
    }

    [Fact]
    public void Parse_LinkActionNotDictionary_IgnoredGracefully()
    {
        // /A exists but is not a dictionary
        var annotsDef = @"[<<
            /Type /Annot /Subtype /Link /Rect [0 0 100 20]
            /A (notdict)
        >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var pageDict = doc.GetPage(1).Dictionary;

        var result = PdfAnnotationParser.Parse(doc, pageDict, new(), null);

        result[0].DestinationPage.Should().BeNull();
        result[0].Uri.Should().BeNull();
    }

    // ─── Helper methods ─────────────────────────────────────────────────────

    private static byte[] MakePdfWithMultiPageAnnot()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long obj1Pos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine("<< /Type /Catalog /Pages 2 0 R >>");
        sb.AppendLine("endobj");

        long obj2Pos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>");
        sb.AppendLine("endobj");

        long obj3Pos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [6 0 R] >>");
        sb.AppendLine("endobj");

        long obj4Pos = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        long obj5Pos = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine("<< /Length 0 >>");
        sb.AppendLine("stream");
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");

        long obj6Pos = sb.Length;
        sb.AppendLine("6 0 obj");
        sb.AppendLine("<< /Type /Annot /Subtype /Link /Rect [0 0 100 100] /Dest [4 0 R /XYZ 0 0 0] >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 7");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{obj1Pos:D10} 00000 n ");
        sb.AppendLine($"{obj2Pos:D10} 00000 n ");
        sb.AppendLine($"{obj3Pos:D10} 00000 n ");
        sb.AppendLine($"{obj4Pos:D10} 00000 n ");
        sb.AppendLine($"{obj5Pos:D10} 00000 n ");
        sb.AppendLine($"{obj6Pos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 7 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
