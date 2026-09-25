using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using Excise.Core.Content;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Core.Text.Segmentation;
using Excise.TestSupport;
using Xunit;
using static Excise.Core.Tests.Text.Segmentation.FormXObjectRedactionTests;

namespace Excise.Core.Tests.Redaction;

/// <summary>
/// #1830: every op-list consumer places images, forms and paths through the
/// CTM the walker stamped on each operator. One page exercises nested
/// <c>q</c>/<c>cm</c>/<c>Q</c>, an unbalanced <c>Q</c> (a no-op, §8.4.2), a
/// <c>cm</c> before an image <c>Do</c>, a form drawn under <c>cm</c> and an
/// inline image under <c>cm</c>; each consumer must land every one of them at
/// the page-space rectangle the spec gives, worked out by hand below.
/// </summary>
public class StampedCtmConsumerTests
{
    // Page-space placements of the fixture's content (see Content).
    private static readonly PdfRectangle ImageXObject = new(20, 20, 120, 120);
    private static readonly PdfRectangle FilledBar = new(200, 200, 240, 220);
    private static readonly PdfRectangle Checkbox = new(20, 600, 32, 612);
    private static readonly PdfRectangle InlineImage = new(300, 300, 340, 340);

    // Where a consumer that lost a q/Q or reset the CTM on the unbalanced Q
    // would put the images; nothing is actually drawn here.
    private static readonly PdfRectangle NearOrigin = new(0, 0, 10, 10);

    private const string Content =
        "BT /F1 12 Tf 30 60 Td (HID) Tj ET\n" +
        "BT /F1 12 Tf 205 205 Td (REC) Tj ET\n" +
        "BT /F1 12 Tf 305 320 Td (INL) Tj ET\n" +
        "q 2 0 0 2 0 0 cm\n" +
        "q 1 0 0 1 10 10 cm 50 0 0 50 0 0 cm /Im0 Do Q\n" +      // (20,20)-(120,120)
        "100 100 20 10 re f\n" +                                  // (200,200)-(240,220)
        "q 1 0 0 1 10 300 cm 0 0 6 6 re S Q\n" +                  // (20,600)-(32,612)
        "Q\n" +
        "q 1 0 0 1 400 0 cm /Fm0 Do Q\n" +                        // BBox -> (400,0)-(500,100)
        "1 0 0 1 300 300 cm\n" +
        "Q\n" +                                                   // unbalanced: CTM unchanged
        "0 100 m 100 100 l S\n" +                                 // (300,400)-(400,400)
        "40 0 0 40 0 0 cm\n" +
        "BI /W 9 /H 1 /BPC 8 /CS /G /L 9 ID INLSECRET EI\n";      // (300,300)-(340,340)

    private static byte[] Fixture() => Build(
        Obj("<< /Type /Catalog /Pages 2 0 R >>"),
        Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
            "/Resources << /Font << /F1 5 0 R >> /XObject << /Im0 6 0 R /Fm0 7 0 R >> >> >>"),
        Stream("", Content),
        Obj(HelveticaFont),
        Stream("/Type /XObject /Subtype /Image /Width 9 /Height 1 /BitsPerComponent 8 " +
               "/ColorSpace /DeviceGray", "IMGSECRET"),
        Stream("/Type /XObject /Subtype /Form /BBox [0 0 100 100] /Resources << /Font << /F1 5 0 R >> >>",
               "BT /F1 12 Tf 10 10 Td (FORMTEXT) Tj ET"));

    // ---- ImageRedactor ----

    [Fact]
    public void ImageRedactor_DropsTheImageXObjectAtItsNestedCmPlacement()
    {
        using var doc = PdfDocument.Open(Fixture());
        var page = doc.GetPage(1);

        var output = ImageRedactor.ProcessOperations(page.GetContentStream().Operators, page,
            new PdfRectangle(15, 15, 125, 125), GlyphRemovalStrategy.AnyOverlap, out var removed, out var edited);

        removed.Should().Be(1);
        edited.Should().Be(0);
        output.Where(o => o.Name == "Do").Select(o => o.GetName(0)).Should().Equal("Fm0");
        output.Should().ContainSingle(o => o.Name == "BI");
    }

    [Fact]
    public void ImageRedactor_DropsTheInlineImagePlacedAfterAnUnbalancedQ()
    {
        using var doc = PdfDocument.Open(Fixture());
        var page = doc.GetPage(1);

        var output = ImageRedactor.ProcessOperations(page.GetContentStream().Operators, page,
            new PdfRectangle(295, 295, 345, 345), GlyphRemovalStrategy.AnyOverlap, out var removed);

        removed.Should().Be(1);
        output.Should().NotContain(o => o.Name == "BI");
        output.Where(o => o.Name == "Do").Select(o => o.GetName(0)).Should().Equal("Im0", "Fm0");
    }

    [Fact]
    public void ImageRedactor_KeepsEveryImageWhenTheAreaIsWhereAWrongCtmWouldPutThem()
    {
        using var doc = PdfDocument.Open(Fixture());
        var page = doc.GetPage(1);
        var ops = page.GetContentStream().Operators;

        var output = ImageRedactor.ProcessOperations(ops, page, NearOrigin,
            GlyphRemovalStrategy.AnyOverlap, out var removed, out var edited);

        removed.Should().Be(0);
        edited.Should().Be(0);
        output.Should().Equal(ops);
    }

    [Fact]
    public void ImageRedactor_DropsAndCountsAnImageWithNoStampedPlacement()
    {
        using var doc = PdfDocument.Open(Fixture());
        var page = doc.GetPage(1);
        var bi = page.GetContentStream().Operators.Single(o => o.Name == "BI");
        var unstamped = new List<ContentOperator>
        {
            new("Do", new PdfObject[] { new PdfName("Im0") }),
            new("BI", bi.Operands) { InlineImageData = bi.InlineImageData },
        };
        var touched = new List<PdfStream>();

        var output = ImageRedactor.ProcessOperations(unstamped, page, new PdfRectangle(500, 700, 510, 710),
            GlyphRemovalStrategy.AnyOverlap, out var removed, out var edited, touched);

        output.Should().BeEmpty("an image that cannot be placed may lie inside the area, so it is not kept");
        removed.Should().Be(2, "the drop is reported through the removal count");
        edited.Should().Be(0);
        touched.Should().ContainSingle();
    }

    [Fact]
    public void RedactArea_OverBothImages_RemovesTheirBytesFromTheSavedFile()
    {
        using var doc = PdfDocument.Open(Fixture());
        SavedPdfLeakScanner.FindTerm(doc.SaveToBytes(), "IMGSECRET").Should().NotBeEmpty("fixture sanity");
        SavedPdfLeakScanner.FindTerm(doc.SaveToBytes(), "INLSECRET").Should().NotBeEmpty("fixture sanity");

        var page = doc.GetPage(1);
        page.RedactArea(new PdfRectangle(15, 15, 125, 125));
        page.RedactArea(new PdfRectangle(295, 295, 345, 345));
        var saved = doc.SaveToBytes();

        SavedPdfLeakScanner.FindTerm(saved, "IMGSECRET").Should().BeEmpty(
            "the image XObject drawn under nested cm lies inside the first area");
        SavedPdfLeakScanner.FindTerm(saved, "INLSECRET").Should().BeEmpty(
            "the inline image drawn after the unbalanced Q lies inside the second area");
    }

    [Fact]
    public void RedactArea_WhereAWrongCtmWouldPutTheImages_LeavesTheirBytes()
    {
        using var doc = PdfDocument.Open(Fixture());
        doc.GetPage(1).RedactArea(NearOrigin);
        var saved = doc.SaveToBytes();

        SavedPdfLeakScanner.FindTerm(saved, "IMGSECRET").Should().NotBeEmpty();
        SavedPdfLeakScanner.FindTerm(saved, "INLSECRET").Should().NotBeEmpty();
    }

    // ---- RedactedCopySafetyPolicy raster audit ----

    [Theory]
    [InlineData(15, 15, 125, 125, 1)]      // the image XObject under nested cm
    [InlineData(295, 295, 345, 345, 1)]    // the inline image after the unbalanced Q
    [InlineData(0, 0, 10, 10, 0)]          // where a wrong CTM would put them
    public void RasterAudit_CountsTheImagesAtTheirStampedPlacement(
        double left, double bottom, double right, double top, int expected)
    {
        using var doc = PdfDocument.Open(Fixture());
        var rasterOnly = RedactedCopySafetyOptions.Default with
        {
            ScrubMetadata = false,
            ScrubAttachments = false,
            ScrubRequestedTerms = false,
            RunCarrierAudit = false,
            VerifyRequestedTerms = false,
            RunHiddenTextAudit = false,
            RunRasterRedactionAudit = true,
        };

        var report = RedactedCopySafetyPolicy.Evaluate(doc, RedactedCopySafetyRequest.ForAreas(
            new[] { new RedactedCopySafetyArea(1,
                PdfPageRect.FromContentPoints(1, new PdfRectangle(left, bottom, right, top))) },
            options: rasterOnly));

        report.RemainingRasterOverlapCount.Should().Be(expected);
        report.RasterRedactionAuditStatus.Should().Be(expected > 0
            ? RedactedContentVerificationStatus.Warning
            : RedactedContentVerificationStatus.Verified);
    }

    // ---- HiddenTextDetector ----

    [Fact]
    public void HiddenTextDetector_PlacesEachObstructionThroughItsStampedCtm()
    {
        using var doc = PdfDocument.Open(Fixture());
        var page = doc.GetPage(1);

        var hits = HiddenTextDetector.ScanPage(page);

        hits.Select(h => (h.Text, h.HiddenBy, h.ObstructionBox)).Should().BeEquivalentTo(new[]
        {
            ("HID", "image /Im0", (PdfRectangle?)ImageXObject),
            ("REC", "black filled rectangle", (PdfRectangle?)FilledBar),
            ("INL", "inline image", (PdfRectangle?)InlineImage),
        });
        HiddenTextDetector.DarkFilledBoxes(page).Should().Equal(FilledBar);
    }

    // ---- FormXObjectFlattener ----

    [Fact]
    public void FormXObjectFlattener_InlinesTheFormWhenTheAreaMeetsItsPageSpaceBBox()
    {
        using var doc = PdfDocument.Open(Fixture());
        var page = doc.GetPage(1);

        FormXObjectFlattener.FlattenOverlapping(page, page.GetContentStream().Operators,
            new PdfRectangle(450, 50, 460, 60), out _, out var inlined).Should().BeTrue();
        inlined.Should().Equal(7);
    }

    [Fact]
    public void FormXObjectFlattener_LeavesTheFormWhenOnlyItsUntransformedBBoxMeetsTheArea()
    {
        using var doc = PdfDocument.Open(Fixture());
        var page = doc.GetPage(1);

        FormXObjectFlattener.FlattenOverlapping(page, page.GetContentStream().Operators,
            new PdfRectangle(10, 10, 50, 50), out _, out var inlined).Should().BeFalse();
        inlined.Should().BeEmpty();
    }

    [Fact]
    public void FormXObjectFlattener_InlinesAFormWithNoStampedPlacement()
    {
        using var doc = PdfDocument.Open(Fixture());
        var page = doc.GetPage(1);

        FormXObjectFlattener.FlattenOverlapping(page,
            new[] { new ContentOperator("Do", new PdfObject[] { new PdfName("Fm0") }) },
            new PdfRectangle(500, 700, 510, 710), out _, out var inlined).Should().BeTrue();
        inlined.Should().Equal(7);
    }

    // ---- PdfFormAutoDetector ----

    [Fact]
    public void PdfFormAutoDetector_PlacesTheCheckboxAndUnderlineThroughTheirStampedCtm()
    {
        using var doc = PdfDocument.Open(Fixture());

        var suggestions = PdfFormAutoDetector.ScanPage(doc.GetPage(1));

        suggestions.Select(s => (s.FieldType, s.Rect)).Should().Equal(
            (PdfFieldType.Button, Checkbox),
            (PdfFieldType.Text, new PdfRectangle(300, 401, 400, 417)));
    }
}
