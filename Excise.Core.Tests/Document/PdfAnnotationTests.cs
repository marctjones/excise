using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Xunit;
namespace Excise.Core.Tests.Document;

/// <summary>
/// Tests for PDF annotation parsing (§12.5).
/// Synthetic in-memory PDFs are used so no fixture files are required.
/// </summary>
public class PdfAnnotationTests
{
    private readonly ITestOutputHelper _out;
    public PdfAnnotationTests(ITestOutputHelper o) => _out = o;

    // ─── helpers ────────────────────────────────────────────────────────────

    /// <summary>Build a minimal single-page PDF with the given /Annots array.</summary>
    private static byte[] MakePdfWithAnnots(string annotsDef)
    {
        // We write a valid cross-reference table so PdfDocument.Open can parse it.
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

        // obj 5 — unused placeholder so annotation objects can be refs
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

    // ─── basic parsing ────────────────────────────────────────────────────────

    [Fact]
    public void GetAnnotations_PageWithNoAnnots_ReturnsEmpty()
    {
        var pdf = MakePdfWithAnnots("[]");
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);

        doc.GetPage(1).GetAnnotations().Should().BeEmpty();
    }

    [Fact]
    public void GetAnnotations_TextAnnotation_ParsesSubtypeAndContents()
    {
        var annotsDef = @"[<<
            /Type /Annot
            /Subtype /Text
            /Rect [72 720 108 756]
            /Contents (Hello World)
            /T (Marc Jones)
        >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);

        var annots = doc.GetPage(1).GetAnnotations();

        annots.Should().HaveCount(1);
        var a = annots[0];
        a.Subtype.Should().Be(PdfAnnotationSubtype.Text);
        a.Contents.Should().Be("Hello World");
        a.Author.Should().Be("Marc Jones");
        a.Rect.Left.Should().BeApproximately(72, 0.01);
        a.Rect.Top.Should().BeApproximately(756, 0.01);
    }

    [Fact]
    public void GetAnnotations_HighlightAnnotation_ParsesQuadPoints()
    {
        var annotsDef = @"[<<
            /Type /Annot
            /Subtype /Highlight
            /Rect [100 700 300 720]
            /QuadPoints [100 720 300 720 100 700 300 700]
            /C [1 1 0]
        >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);

        var annots = doc.GetPage(1).GetAnnotations();

        annots.Should().HaveCount(1);
        var a = annots[0];
        a.Subtype.Should().Be(PdfAnnotationSubtype.Highlight);
        a.IsTextMarkup.Should().BeTrue();
        a.QuadPoints.Should().NotBeNull().And.HaveCount(1);
        a.Color.Should().NotBeNull();
        a.Color!.Value.R.Should().BeApproximately(1, 0.01);
        a.Color!.Value.G.Should().BeApproximately(1, 0.01);
        a.Color!.Value.B.Should().BeApproximately(0, 0.01);
    }

    [Fact]
    public void GetAnnotations_UnderlineSquigglyStrikeout_IsTextMarkupTrue()
    {
        foreach (var subtype in new[] { "Underline", "Squiggly", "StrikeOut" })
        {
            var annotsDef = $@"[<< /Type /Annot /Subtype /{subtype} /Rect [0 0 100 20] >>]";
            var pdf = MakePdfWithAnnots(annotsDef);
            using var doc = PdfDocument.Open(new MemoryStream(pdf), false);

            var annots = doc.GetPage(1).GetAnnotations();
            annots[0].IsTextMarkup.Should().BeTrue(
                $"{subtype} must be classified as text markup");
        }
    }

    [Fact]
    public void GetAnnotations_MultipleAnnotations_ReturnsAll()
    {
        var annotsDef = @"[
            << /Type /Annot /Subtype /Text /Rect [10 10 50 50] /Contents (note) >>
            << /Type /Annot /Subtype /Highlight /Rect [60 60 200 80] >>
            << /Type /Annot /Subtype /Stamp /Rect [100 500 300 600] >>
        ]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);

        var annots = doc.GetPage(1).GetAnnotations();

        annots.Should().HaveCount(3);
        annots.Select(a => a.Subtype).Should().BeEquivalentTo(
            new[] { PdfAnnotationSubtype.Text, PdfAnnotationSubtype.Highlight, PdfAnnotationSubtype.Stamp });
    }

    [Fact]
    public void GetAnnotations_AnnotationFlags_ParsedCorrectly()
    {
        // Flag 4 = Print, flag 64 = ReadOnly  →  combined = 68
        var annotsDef = @"[<< /Type /Annot /Subtype /Text /Rect [0 0 10 10] /F 68 >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);

        var annots = doc.GetPage(1).GetAnnotations();

        var flags = annots[0].Flags;
        flags.HasFlag(PdfAnnotationFlags.Print).Should().BeTrue();
        flags.HasFlag(PdfAnnotationFlags.ReadOnly).Should().BeTrue();
        flags.HasFlag(PdfAnnotationFlags.Hidden).Should().BeFalse();
    }

    [Fact]
    public void GetAnnotations_UriLinkAnnotation_ExposesUri()
    {
        var annotsDef = @"[<<
            /Type /Annot
            /Subtype /Link
            /Rect [0 0 100 20]
            /A << /S /URI /URI (https://example.com) >>
        >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);

        var annots = doc.GetPage(1).GetAnnotations();

        annots.Should().HaveCount(1);
        var a = annots[0];
        a.Subtype.Should().Be(PdfAnnotationSubtype.Link);
        a.Uri.Should().Be("https://example.com");
        a.DestinationPage.Should().BeNull();
    }

    [Fact]
    public void GetAnnotations_GrayColorAnnotation_ParsedAsRgb()
    {
        // /C [0.5] → grayscale 0.5 → R=G=B=0.5
        var annotsDef = @"[<< /Type /Annot /Subtype /Text /Rect [0 0 10 10] /C [0.5] >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);

        var annot = doc.GetPage(1).GetAnnotations()[0];
        annot.Color.Should().NotBeNull();
        annot.Color!.Value.R.Should().BeApproximately(0.5, 0.01);
        annot.Color!.Value.G.Should().BeApproximately(0.5, 0.01);
        annot.Color!.Value.B.Should().BeApproximately(0.5, 0.01);
    }

    [Fact]
    public void GetAnnotations_UnknownSubtype_ParsedAsUnknown()
    {
        var annotsDef = @"[<< /Type /Annot /Subtype /FutureType /Rect [0 0 10 10] >>]";
        var pdf = MakePdfWithAnnots(annotsDef);
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);

        var annot = doc.GetPage(1).GetAnnotations()[0];
        annot.Subtype.Should().Be(PdfAnnotationSubtype.Unknown);
        annot.RawDictionary.Should().NotBeNull("raw dict always available for unknown subtypes");
    }

    // ─── real PDF smoke test ─────────────────────────────────────────────────

    private const string SmokePdf = "../../../../test-pdfs/smoke/irs-w9.pdf";

    [Fact]
    public void GetAnnotations_IrsW9_HasWidgetAnnotations()
    {
        // IRS W-9 is a fillable form — every form field is a Widget annotation.
        if (!File.Exists(SmokePdf)) return;

        using var doc = PdfDocument.Open(SmokePdf);
        var widgetCount = 0;
        for (int p = 1; p <= doc.PageCount; p++)
        {
            var annots = doc.GetPage(p).GetAnnotations();
            widgetCount += annots.Count(a => a.Subtype == PdfAnnotationSubtype.Widget);
            foreach (var a in annots)
                _out.WriteLine($"  p{p} {a}");
        }
        widgetCount.Should().BeGreaterThan(0,
            "IRS W-9 is a fillable form and must contain Widget annotations");
    }
}
