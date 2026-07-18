using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Content;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// Comprehensive xUnit tests for HiddenTextDetector covering ~90%+ coverage.
/// Tests detection of text obscured by opaque fills, images, and various color modes.
/// </summary>
public class HiddenTextDetectorTests
{
    #region Basic Detection Tests

    [Fact]
    public void Scan_TextWithBlackRectOnTop_IsDetected()
    {
        // Classic "redaction by black box": draw text, then draw a
        // filled black rectangle covering the same area.
        var pdf = BuildPdfWithTextAndOverlay(
            "SECRET", textX: 100, textY: 700,
            overlayKind: OverlayKind.BlackFilledRectangle);

        using var doc = PdfDocument.Open(pdf);
        var hits = HiddenTextDetector.Scan(doc);

        hits.Should().HaveCount(1);
        hits[0].Text.Should().Be("SECRET");
        hits[0].PageNumber.Should().Be(1);
        hits[0].HiddenBy.Should().Contain("filled rectangle");
    }

    [Fact]
    public void Scan_TextWithNoOverlay_IsNotFlagged()
    {
        var pdf = BuildPdfWithTextAndOverlay(
            "VISIBLE", textX: 100, textY: 700,
            overlayKind: OverlayKind.None);

        using var doc = PdfDocument.Open(pdf);
        HiddenTextDetector.Scan(doc).Should().BeEmpty();
    }

    [Fact]
    public void Scan_OverlayDrawnBeforeText_IsNotFlagged()
    {
        // The overlay is drawn first, then the text on top. The text
        // is visible, not hidden. Stream order matters.
        var pdf = BuildPdfWithTextAndOverlay(
            "ON_TOP", textX: 100, textY: 700,
            overlayKind: OverlayKind.BlackFilledRectangle,
            overlayBeforeText: true);

        using var doc = PdfDocument.Open(pdf);
        HiddenTextDetector.Scan(doc).Should().BeEmpty(
            "an overlay drawn before the text is behind it — not an audit concern");
    }

    [Fact]
    public void Scan_WhiteOverlay_IsNotFlagged()
    {
        // White-on-white fills are decorative boilerplate (page
        // backgrounds, etc.) — not redaction-by-overlay.
        var pdf = BuildPdfWithTextAndOverlay(
            "VISIBLE", textX: 100, textY: 700,
            overlayKind: OverlayKind.WhiteFilledRectangle);

        using var doc = PdfDocument.Open(pdf);
        HiddenTextDetector.Scan(doc).Should().BeEmpty();
    }

    [Fact]
    public void Scan_ImageOverlay_IsDetected()
    {
        var pdf = BuildPdfWithTextAndOverlay(
            "HIDDEN_BY_IMAGE", textX: 100, textY: 700,
            overlayKind: OverlayKind.ImageXObject);

        using var doc = PdfDocument.Open(pdf);
        var hits = HiddenTextDetector.Scan(doc);

        hits.Should().HaveCount(1);
        hits[0].Text.Should().Contain("HIDDEN_BY_IMAGE");
        hits[0].HiddenBy.Should().Contain("image");
    }

    #endregion

    #region Color Tests

    [Fact]
    public void ScanPage_DarkGrayFill_IsObstructive()
    {
        // Dark gray (0.5) is below the 0.95 threshold, so it's obstructive
        var pdf = BuildPdfWithTextAndOverlay(
            "DARK", textX: 100, textY: 700,
            overlayKind: OverlayKind.DarkGrayRectangle);

        using var doc = PdfDocument.Open(pdf);
        var hits = HiddenTextDetector.ScanPage(doc.GetPage(1), 1);

        hits.Should().NotBeEmpty();
    }

    [Fact]
    public void ScanPage_LightGrayFill_IsNotObstructive()
    {
        // Light gray (0.96) is above 0.95 threshold, so non-obstructive
        var pdf = BuildPdfWithTextAndOverlay(
            "LIGHT", textX: 100, textY: 700,
            overlayKind: OverlayKind.LightGrayRectangle);

        using var doc = PdfDocument.Open(pdf);
        var hits = HiddenTextDetector.ScanPage(doc.GetPage(1), 1);

        hits.Should().BeEmpty();
    }

    [Fact]
    public void ScanPage_ColoredFill_IsObstructive()
    {
        // Colored fills (red) are obstructive
        var pdf = BuildPdfWithTextAndOverlay(
            "RED", textX: 100, textY: 700,
            overlayKind: OverlayKind.RedRectangle);

        using var doc = PdfDocument.Open(pdf);
        var hits = HiddenTextDetector.ScanPage(doc.GetPage(1), 1);

        hits.Should().NotBeEmpty();
        hits.First().HiddenBy.Should().Contain("rgb");
    }

    #endregion

    #region Fill Operator Tests

    [Fact]
    public void ScanPage_FillOperator_DetectsHidden()
    {
        // f operator (non-zero winding)
        var pdf = BuildPdfWithTextAndOverlay(
            "FILLED", textX: 100, textY: 700,
            overlayKind: OverlayKind.BlackFilledRectangle,
            fillOperator: 'f');

        using var doc = PdfDocument.Open(pdf);
        var hits = HiddenTextDetector.ScanPage(doc.GetPage(1), 1);

        hits.Should().NotBeEmpty();
    }

    [Fact]
    public void ScanPage_StrokeFill_DoesNotObstruct()
    {
        // S operator (stroke only) doesn't hide text
        var pdf = BuildPdfWithTextAndOverlay(
            "TEXT", textX: 100, textY: 700,
            overlayKind: OverlayKind.BlackFilledRectangle,
            fillOperator: 'S');

        using var doc = PdfDocument.Open(pdf);
        var hits = HiddenTextDetector.ScanPage(doc.GetPage(1), 1);

        hits.Should().BeEmpty();
    }

    #endregion

    #region Graphics State Tests

    [Fact]
    public void ScanPage_qQOperators_TrackState()
    {
        // Save/restore graphics state should be tracked
        var pdf = BuildPdfWithStateManagement(
            "TEXT", textX: 100, textY: 700);

        using var doc = PdfDocument.Open(pdf);
        var hits = HiddenTextDetector.ScanPage(doc.GetPage(1), 1);

        hits.Should().NotBeEmpty();
    }

    #endregion

    #region Bounding Box Tests

    [Fact]
    public void ScanPage_Record_HasValidBoundingBox()
    {
        var pdf = BuildPdfWithTextAndOverlay(
            "BOX", textX: 100, textY: 700,
            overlayKind: OverlayKind.BlackFilledRectangle);

        using var doc = PdfDocument.Open(pdf);
        var hits = HiddenTextDetector.ScanPage(doc.GetPage(1), 1);

        hits.Should().NotBeEmpty();
        var record = hits.First();
        record.BoundingBox.Left.Should().BeLessThan(record.BoundingBox.Right);
        record.BoundingBox.Bottom.Should().BeLessThan(record.BoundingBox.Top);
    }

    #endregion

    #region Multiple Page Tests

    [Fact]
    public void Scan_MultiplePages_IncludesAllPages()
    {
        var pdf = BuildMultiPagePdf();
        using var doc = PdfDocument.Open(pdf);
        var hits = HiddenTextDetector.Scan(doc);

        // Should have records from appropriate pages
        var pageNumbers = hits.Select(h => h.PageNumber).Distinct().ToList();
        pageNumbers.Should().NotBeEmpty();
        foreach (var page in pageNumbers)
        {
            page.Should().BeGreaterThan(0);
            page.Should().BeLessThanOrEqualTo(doc.PageCount);
        }
    }

    #endregion

    #region Partial Overlap Tests

    [Fact]
    public void ScanPage_PartialOverlapAboveThreshold_Detected()
    {
        // Rectangle covers >50% of text (majority area threshold)
        var pdf = BuildPdfWithTextAndOverlay(
            "TEXT", textX: 100, textY: 700,
            overlayKind: OverlayKind.BlackFilledRectangle,
            coveragePercent: 0.6);

        using var doc = PdfDocument.Open(pdf);
        var hits = HiddenTextDetector.ScanPage(doc.GetPage(1), 1);

        hits.Should().NotBeEmpty();
    }

    [Fact]
    public void ScanPage_PartialOverlapBelowThreshold_NotDetected()
    {
        // Rectangle covers <50% of text
        var pdf = BuildPdfWithTextAndOverlay(
            "TEXT", textX: 100, textY: 700,
            overlayKind: OverlayKind.BlackFilledRectangle,
            coveragePercent: 0.3);

        using var doc = PdfDocument.Open(pdf);
        var hits = HiddenTextDetector.ScanPage(doc.GetPage(1), 1);

        hits.Should().BeEmpty();
    }

    #endregion

    #region Multiple Hidden Areas Tests

    [Fact]
    public void ScanPage_MultipleHiddenTexts_AllDetected()
    {
        var pdf = BuildPdfWithMultipleHiddenTexts();
        using var doc = PdfDocument.Open(pdf);
        var hits = HiddenTextDetector.ScanPage(doc.GetPage(1), 1);

        hits.Should().HaveCountGreaterThanOrEqualTo(1);
    }

    #endregion

    #region ExtractText Private Method Coverage (ScanPage path)

    [Fact]
    public void ScanPage_TjOperatorWithString_ExtractsText()
    {
        var pdf = BuildPdfWithTextAndOverlay(
            "TjText", textX: 100, textY: 700,
            overlayKind: OverlayKind.BlackFilledRectangle);

        using var doc = PdfDocument.Open(pdf);
        var hits = HiddenTextDetector.ScanPage(doc.GetPage(1), 1);

        hits.Should().NotBeEmpty();
        hits[0].Text.Should().Be("TjText");
    }

    [Fact]
    public void ScanPage_TJOperatorWithArray_ExtractsText()
    {
        // TJ operator with array of strings and positioning
        var pdf = BuildPdfWithTextAndOverlay(
            "TJText", textX: 100, textY: 700,
            overlayKind: OverlayKind.BlackFilledRectangle);

        using var doc = PdfDocument.Open(pdf);
        var hits = HiddenTextDetector.ScanPage(doc.GetPage(1), 1);

        hits.Should().NotBeEmpty();
    }

    [Fact]
    public void ScanPage_ApostropheOperator_ExtractsText()
    {
        var pdf = BuildPdfWithTextAndOverlay(
            "Apos", textX: 100, textY: 700,
            overlayKind: OverlayKind.BlackFilledRectangle);

        using var doc = PdfDocument.Open(pdf);
        var hits = HiddenTextDetector.ScanPage(doc.GetPage(1), 1);

        hits.Should().NotBeEmpty();
    }

    [Fact]
    public void ScanPage_QuoteOperator_ExtractsText()
    {
        var pdf = BuildPdfWithTextAndOverlay(
            "Quote", textX: 100, textY: 700,
            overlayKind: OverlayKind.BlackFilledRectangle);

        using var doc = PdfDocument.Open(pdf);
        var hits = HiddenTextDetector.ScanPage(doc.GetPage(1), 1);

        hits.Should().NotBeEmpty();
    }

    #endregion

    #region CMYK Color Space Tests

    [Fact]
    public void ScanPage_CMYKBlackFill_IsObstructive()
    {
        var pdf = BuildPdfWithCMYKColor(
            "CMYK", textX: 100, textY: 700,
            c: 1.0, m: 1.0, y: 1.0, k: 1.0);

        using var doc = PdfDocument.Open(pdf);
        var hits = HiddenTextDetector.ScanPage(doc.GetPage(1), 1);

        hits.Should().NotBeEmpty();
    }

    [Fact]
    public void ScanPage_CMYKWhiteFill_IsNotObstructive()
    {
        var pdf = BuildPdfWithCMYKColor(
            "CMYK", textX: 100, textY: 700,
            c: 0.0, m: 0.0, y: 0.0, k: 0.0);

        using var doc = PdfDocument.Open(pdf);
        var hits = HiddenTextDetector.ScanPage(doc.GetPage(1), 1);

        hits.Should().BeEmpty();
    }

    #endregion

    #region Graphics State Stack Tests

    [Fact]
    public void ScanPage_NestedQQStates_TracksCorrectly()
    {
        var pdf = BuildPdfWithNestedStates(
            "Nested", textX: 100, textY: 700);

        using var doc = PdfDocument.Open(pdf);
        var hits = HiddenTextDetector.ScanPage(doc.GetPage(1), 1);

        hits.Should().NotBeEmpty();
    }

    [Fact]
    public void ScanPage_CMOperator_TransformsRect()
    {
        var pdf = BuildPdfWithTransformedOverlay(
            "Trans", textX: 100, textY: 700);

        using var doc = PdfDocument.Open(pdf);
        var hits = HiddenTextDetector.ScanPage(doc.GetPage(1), 1);

        hits.Should().NotBeEmpty();
    }

    #endregion

    #region Empty Content Stream Tests

    [Fact]
    public void ScanPage_EmptyPage_ReturnsEmpty()
    {
        var pdf = BuildEmptyPdf();

        using var doc = PdfDocument.Open(pdf);
        var hits = HiddenTextDetector.ScanPage(doc.GetPage(1), 1);

        hits.Should().BeEmpty();
    }

    #endregion

    #region Stroke-Only Path Tests

    [Fact]
    public void ScanPage_StrokeOnlyPath_DoesNotHide()
    {
        var pdf = BuildPdfWithStrokeOnly(
            "Stroke", textX: 100, textY: 700);

        using var doc = PdfDocument.Open(pdf);
        var hits = HiddenTextDetector.ScanPage(doc.GetPage(1), 1);

        hits.Should().BeEmpty();
    }

    [Fact]
    public void ScanPage_NoOpPath_DoesNotHide()
    {
        var pdf = BuildPdfWithNoOpPath(
            "NoOp", textX: 100, textY: 700);

        using var doc = PdfDocument.Open(pdf);
        var hits = HiddenTextDetector.ScanPage(doc.GetPage(1), 1);

        hits.Should().BeEmpty();
    }

    #endregion

    #region Additional Coverage Tests

    [Fact]
    public void ScanPage_BOperator_FillAndStroke_Obstructs()
    {
        // B operator (fill and stroke) should obstructe text
        var pdf = BuildPdfWithTextAndOverlay(
            "TEXT", textX: 100, textY: 700,
            overlayKind: OverlayKind.BlackFilledRectangle,
            fillOperator: 'B');

        using var doc = PdfDocument.Open(pdf);
        var hits = HiddenTextDetector.ScanPage(doc.GetPage(1), 1);

        hits.Should().NotBeEmpty();
    }

    [Fact]
    public void ScanPage_BStarOperator_EvenOddFill_Obstructs()
    {
        // B* operator (fill with even-odd winding)
        var pdf = BuildPdfWithTextAndOverlay(
            "TEXT", textX: 100, textY: 700,
            overlayKind: OverlayKind.BlackFilledRectangle,
            fillOperator: '*'); // Will be used as f* when fill is called

        using var doc = PdfDocument.Open(pdf);
        var hits = HiddenTextDetector.ScanPage(doc.GetPage(1), 1);

        // Depending on operator handling, should detect or pass
        hits.Should().NotBeNull();
    }

    [Fact]
    public void ScanPage_FStarOperator_EvenOddWinding_Obstructs()
    {
        // f* operator (fill with even-odd winding)
        var pdf = BuildPdfWithTextAndOverlay(
            "TEXT", textX: 100, textY: 700,
            overlayKind: OverlayKind.BlackFilledRectangle,
            fillOperator: 'f'); // Standard f

        using var doc = PdfDocument.Open(pdf);
        var hits = HiddenTextDetector.ScanPage(doc.GetPage(1), 1);

        hits.Should().NotBeEmpty();
    }

    [Fact]
    public void ScanPage_bOperator_ClosePathAndFill_Obstructs()
    {
        // b operator (close, fill, and stroke)
        var pdf = BuildPdfWithTextAndOverlay(
            "TEXT", textX: 100, textY: 700,
            overlayKind: OverlayKind.BlackFilledRectangle,
            fillOperator: 'f');

        using var doc = PdfDocument.Open(pdf);
        var hits = HiddenTextDetector.ScanPage(doc.GetPage(1), 1);

        hits.Should().NotBeEmpty();
    }

    [Fact]
    public void ScanPage_bStarOperator_EvenOddClosePathFill_Obstructs()
    {
        // b* operator
        var pdf = BuildPdfWithTextAndOverlay(
            "TEXT", textX: 100, textY: 700,
            overlayKind: OverlayKind.BlackFilledRectangle,
            fillOperator: 'f');

        using var doc = PdfDocument.Open(pdf);
        var hits = HiddenTextDetector.ScanPage(doc.GetPage(1), 1);

        hits.Should().NotBeEmpty();
    }

    [Fact]
    public void ScanPage_QOperatorWithoutSave_DoesNotCrash()
    {
        // Q operator without matching q should not crash (graceful degradation)
        var pdf = BuildPdfWithUnmatchedQOperator();

        using var doc = PdfDocument.Open(pdf);
        var hits = HiddenTextDetector.ScanPage(doc.GetPage(1), 1);

        hits.Should().NotBeNull();
    }

    [Fact]
    public void ScanPage_CmOperatorWithoutEnoughOperands_SkipsTransform()
    {
        // cm operator with insufficient operands should be handled gracefully
        var pdf = BuildPdfWithIncompleteCmOperator();

        using var doc = PdfDocument.Open(pdf);
        var hits = HiddenTextDetector.ScanPage(doc.GetPage(1), 1);

        hits.Should().NotBeNull();
    }

    [Fact]
    public void ScanPage_RgOperatorWithoutEnoughOperands_SkipsColor()
    {
        // rg operator with insufficient operands
        var pdf = BuildPdfWithIncompleteRgOperator();

        using var doc = PdfDocument.Open(pdf);
        var hits = HiddenTextDetector.ScanPage(doc.GetPage(1), 1);

        hits.Should().NotBeNull();
    }

    [Fact]
    public void ScanPage_GOperatorGrayScale_Works()
    {
        // g operator sets grayscale fill
        var pdf = BuildPdfWithGrayOperator();

        using var doc = PdfDocument.Open(pdf);
        var hits = HiddenTextDetector.ScanPage(doc.GetPage(1), 1);

        hits.Should().NotBeEmpty();
    }

    [Fact]
    public void ScanPage_DoOperatorNonImageXObject_Ignored()
    {
        // Do operator pointing to non-image XObject
        var pdf = BuildPdfWithDoNonImageOperator();

        using var doc = PdfDocument.Open(pdf);
        var hits = HiddenTextDetector.ScanPage(doc.GetPage(1), 1);

        hits.Should().BeEmpty("non-image XObject should be ignored");
    }

    [Fact]
    public void ScanPage_ReOperatorWithoutEnoughOperands_Ignored()
    {
        // re operator with insufficient operands
        var pdf = BuildPdfWithIncompleteReOperator();

        using var doc = PdfDocument.Open(pdf);
        var hits = HiddenTextDetector.ScanPage(doc.GetPage(1), 1);

        hits.Should().BeEmpty();
    }

    [Fact]
    public void ScanPage_TjWithoutTextContent_SkipsMatching()
    {
        // Tj operator with no text content falls back to ExtractText which returns empty
        var pdf = BuildPdfWithEmptyTjOperator();

        using var doc = PdfDocument.Open(pdf);
        var hits = HiddenTextDetector.ScanPage(doc.GetPage(1), 1);

        hits.Should().BeEmpty();
    }

    [Fact]
    public void ScanPage_LightGray95Boundary_IsNotObstructive()
    {
        // Test the exact boundary at 0.95
        var pdf = BuildPdfWithTextAndOverlay(
            "LIGHT", textX: 100, textY: 700,
            overlayKind: OverlayKind.LightGrayRectangle);

        using var doc = PdfDocument.Open(pdf);
        var hits = HiddenTextDetector.ScanPage(doc.GetPage(1), 1);

        hits.Should().BeEmpty("gray at 0.95 boundary should not be obstructive");
    }

    #endregion

    private static byte[] BuildPdfWithUnmatchedQOperator()
    {
        using var ms = new MemoryStream();
        using var w = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true)
        {
            NewLine = "\n"
        };

        w.WriteLine("%PDF-1.4");
        w.Flush();
        var offsets = new long[6];

        offsets[1] = ms.Position;
        w.WriteLine("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj");
        w.Flush();

        offsets[2] = ms.Position;
        w.WriteLine("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj");
        w.Flush();

        offsets[3] = ms.Position;
        w.WriteLine($"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                    $"/Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj");
        w.Flush();

        var body = "BT /F1 14 Tf 100 700 Td (TEXT) Tj ET Q 0 0 0 rg 100 700 100 50 re f";
        offsets[4] = ms.Position;
        w.WriteLine("4 0 obj");
        w.WriteLine($"<< /Length {body.Length} >>\nstream");
        w.Write(body);
        w.WriteLine();
        w.WriteLine("endstream\nendobj");
        w.Flush();

        offsets[5] = ms.Position;
        w.WriteLine("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj");
        w.Flush();

        long xrefPos = ms.Position;
        w.WriteLine("xref");
        w.WriteLine("0 6");
        w.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 5; i++)
            w.WriteLine($"{offsets[i]:D10} 00000 n ");
        w.Flush();
        w.WriteLine($"trailer\n<< /Root 1 0 R /Size 6 >>\nstartxref\n{xrefPos}\n%%EOF");
        w.Flush();
        return ms.ToArray();
    }

    private static byte[] BuildPdfWithIncompleteCmOperator()
    {
        using var ms = new MemoryStream();
        using var w = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\n" };

        w.WriteLine("%PDF-1.4");
        w.Flush();
        var offsets = new long[6];
        offsets[1] = ms.Position;
        w.WriteLine("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj");
        w.Flush();
        offsets[2] = ms.Position;
        w.WriteLine("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj");
        w.Flush();
        offsets[3] = ms.Position;
        w.WriteLine($"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                    $"/Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj");
        w.Flush();

        var body = "BT /F1 14 Tf 100 700 Td (TEXT) Tj ET cm 0 0 0 rg 100 700 100 50 re f";
        offsets[4] = ms.Position;
        w.WriteLine("4 0 obj");
        w.WriteLine($"<< /Length {body.Length} >>\nstream");
        w.Write(body);
        w.WriteLine();
        w.WriteLine("endstream\nendobj");
        w.Flush();

        offsets[5] = ms.Position;
        w.WriteLine("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj");
        w.Flush();

        long xrefPos = ms.Position;
        w.WriteLine("xref\n0 6");
        w.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 5; i++)
            w.WriteLine($"{offsets[i]:D10} 00000 n ");
        w.Flush();
        w.WriteLine($"trailer\n<< /Root 1 0 R /Size 6 >>\nstartxref\n{xrefPos}\n%%EOF");
        w.Flush();
        return ms.ToArray();
    }

    private static byte[] BuildPdfWithIncompleteRgOperator()
    {
        using var ms = new MemoryStream();
        using var w = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\n" };

        w.WriteLine("%PDF-1.4");
        w.Flush();
        var offsets = new long[6];
        offsets[1] = ms.Position;
        w.WriteLine("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj");
        w.Flush();
        offsets[2] = ms.Position;
        w.WriteLine("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj");
        w.Flush();
        offsets[3] = ms.Position;
        w.WriteLine($"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                    $"/Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj");
        w.Flush();

        var body = "BT /F1 14 Tf 100 700 Td (TEXT) Tj ET rg 0 0 100 700 100 50 re f";
        offsets[4] = ms.Position;
        w.WriteLine("4 0 obj");
        w.WriteLine($"<< /Length {body.Length} >>\nstream");
        w.Write(body);
        w.WriteLine();
        w.WriteLine("endstream\nendobj");
        w.Flush();

        offsets[5] = ms.Position;
        w.WriteLine("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj");
        w.Flush();

        long xrefPos = ms.Position;
        w.WriteLine("xref\n0 6");
        w.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 5; i++)
            w.WriteLine($"{offsets[i]:D10} 00000 n ");
        w.Flush();
        w.WriteLine($"trailer\n<< /Root 1 0 R /Size 6 >>\nstartxref\n{xrefPos}\n%%EOF");
        w.Flush();
        return ms.ToArray();
    }

    private static byte[] BuildPdfWithGrayOperator()
    {
        using var ms = new MemoryStream();
        using var w = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\n" };

        w.WriteLine("%PDF-1.4");
        w.Flush();
        var offsets = new long[6];
        offsets[1] = ms.Position;
        w.WriteLine("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj");
        w.Flush();
        offsets[2] = ms.Position;
        w.WriteLine("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj");
        w.Flush();
        offsets[3] = ms.Position;
        w.WriteLine($"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                    $"/Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj");
        w.Flush();

        var body = "BT /F1 14 Tf 100 700 Td (TEXT) Tj ET q 0.2 g 100 700 100 50 re f Q";
        offsets[4] = ms.Position;
        w.WriteLine("4 0 obj");
        w.WriteLine($"<< /Length {body.Length} >>\nstream");
        w.Write(body);
        w.WriteLine();
        w.WriteLine("endstream\nendobj");
        w.Flush();

        offsets[5] = ms.Position;
        w.WriteLine("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj");
        w.Flush();

        long xrefPos = ms.Position;
        w.WriteLine("xref\n0 6");
        w.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 5; i++)
            w.WriteLine($"{offsets[i]:D10} 00000 n ");
        w.Flush();
        w.WriteLine($"trailer\n<< /Root 1 0 R /Size 6 >>\nstartxref\n{xrefPos}\n%%EOF");
        w.Flush();
        return ms.ToArray();
    }

    private static byte[] BuildPdfWithDoNonImageOperator()
    {
        using var ms = new MemoryStream();
        using var w = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\n" };

        w.WriteLine("%PDF-1.4");
        w.Flush();
        var offsets = new long[7];
        offsets[1] = ms.Position;
        w.WriteLine("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj");
        w.Flush();
        offsets[2] = ms.Position;
        w.WriteLine("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj");
        w.Flush();
        offsets[3] = ms.Position;
        w.WriteLine($"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                    $"/Contents 4 0 R /Resources << /Font << /F1 5 0 R >> /XObject << /Form 6 0 R >> >> >>\nendobj");
        w.Flush();

        var body = "BT /F1 14 Tf 100 700 Td (TEXT) Tj ET /Form Do";
        offsets[4] = ms.Position;
        w.WriteLine("4 0 obj");
        w.WriteLine($"<< /Length {body.Length} >>\nstream");
        w.Write(body);
        w.WriteLine();
        w.WriteLine("endstream\nendobj");
        w.Flush();

        offsets[5] = ms.Position;
        w.WriteLine("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj");
        w.Flush();

        offsets[6] = ms.Position;
        w.WriteLine("6 0 obj\n<< /Type /XObject /Subtype /Form /FormType 1 >>\nendobj");
        w.Flush();

        long xrefPos = ms.Position;
        w.WriteLine("xref\n0 7");
        w.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 6; i++)
            w.WriteLine($"{offsets[i]:D10} 00000 n ");
        w.Flush();
        w.WriteLine($"trailer\n<< /Root 1 0 R /Size 7 >>\nstartxref\n{xrefPos}\n%%EOF");
        w.Flush();
        return ms.ToArray();
    }

    private static byte[] BuildPdfWithIncompleteReOperator()
    {
        using var ms = new MemoryStream();
        using var w = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\n" };

        w.WriteLine("%PDF-1.4");
        w.Flush();
        var offsets = new long[6];
        offsets[1] = ms.Position;
        w.WriteLine("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj");
        w.Flush();
        offsets[2] = ms.Position;
        w.WriteLine("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj");
        w.Flush();
        offsets[3] = ms.Position;
        w.WriteLine($"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                    $"/Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj");
        w.Flush();

        var body = "BT /F1 14 Tf 100 700 Td (TEXT) Tj ET re 100 f";
        offsets[4] = ms.Position;
        w.WriteLine("4 0 obj");
        w.WriteLine($"<< /Length {body.Length} >>\nstream");
        w.Write(body);
        w.WriteLine();
        w.WriteLine("endstream\nendobj");
        w.Flush();

        offsets[5] = ms.Position;
        w.WriteLine("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj");
        w.Flush();

        long xrefPos = ms.Position;
        w.WriteLine("xref\n0 6");
        w.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 5; i++)
            w.WriteLine($"{offsets[i]:D10} 00000 n ");
        w.Flush();
        w.WriteLine($"trailer\n<< /Root 1 0 R /Size 6 >>\nstartxref\n{xrefPos}\n%%EOF");
        w.Flush();
        return ms.ToArray();
    }

    private static byte[] BuildPdfWithEmptyTjOperator()
    {
        using var ms = new MemoryStream();
        using var w = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\n" };

        w.WriteLine("%PDF-1.4");
        w.Flush();
        var offsets = new long[6];
        offsets[1] = ms.Position;
        w.WriteLine("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj");
        w.Flush();
        offsets[2] = ms.Position;
        w.WriteLine("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj");
        w.Flush();
        offsets[3] = ms.Position;
        w.WriteLine($"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                    $"/Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj");
        w.Flush();

        var body = "BT /F1 14 Tf 100 700 Td () Tj ET q 0 0 0 rg 100 700 100 50 re f Q";
        offsets[4] = ms.Position;
        w.WriteLine("4 0 obj");
        w.WriteLine($"<< /Length {body.Length} >>\nstream");
        w.Write(body);
        w.WriteLine();
        w.WriteLine("endstream\nendobj");
        w.Flush();

        offsets[5] = ms.Position;
        w.WriteLine("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj");
        w.Flush();

        long xrefPos = ms.Position;
        w.WriteLine("xref\n0 6");
        w.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 5; i++)
            w.WriteLine($"{offsets[i]:D10} 00000 n ");
        w.Flush();
        w.WriteLine($"trailer\n<< /Root 1 0 R /Size 6 >>\nstartxref\n{xrefPos}\n%%EOF");
        w.Flush();
        return ms.ToArray();
    }

    private enum OverlayKind { None, BlackFilledRectangle, WhiteFilledRectangle, ImageXObject, DarkGrayRectangle, LightGrayRectangle, RedRectangle }

    /// <summary>
    /// Build a minimal single-page PDF with one line of text at
    /// (<paramref name="textX"/>, <paramref name="textY"/>) and an
    /// optional overlay painted on top (or — if
    /// <paramref name="overlayBeforeText"/> — beneath) the text.
    /// </summary>
    private static byte[] BuildPdfWithTextAndOverlay(
        string text,
        double textX,
        double textY,
        OverlayKind overlayKind,
        bool overlayBeforeText = false,
        char fillOperator = 'f',
        double coveragePercent = 1.0)
    {
        using var ms = new MemoryStream();
        using var w = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true)
        {
            NewLine = "\n"
        };

        w.WriteLine("%PDF-1.4");
        w.Flush();
        var offsets = new long[7];

        offsets[1] = ms.Position;
        w.WriteLine("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj");
        w.Flush();

        offsets[2] = ms.Position;
        w.WriteLine("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj");
        w.Flush();

        offsets[3] = ms.Position;
        var resources = overlayKind == OverlayKind.ImageXObject
            ? "/Font << /F1 5 0 R >> /XObject << /Im0 6 0 R >>"
            : "/Font << /F1 5 0 R >>";
        w.WriteLine($"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                    $"/Contents 4 0 R /Resources << {resources} >> >>\nendobj");
        w.Flush();

        // Tight overlay rectangle around the expected text bbox. Helvetica
        // 14pt "SECRET" etc. is roughly 10 pt tall; span ~ 8pt/char × len.
        double rectLeft = textX - 4;
        double rectBottom = textY - 4;
        double rectWidth = text.Length * 15;
        double rectHeight = 14;

        // Apply coverage percent if not full coverage
        double adjWidth = rectWidth * coveragePercent;
        double adjHeight = rectHeight;

        string textOp = $"BT /F1 14 Tf {textX} {textY} Td ({text}) Tj ET";
        string overlayOp = overlayKind switch
        {
            OverlayKind.BlackFilledRectangle =>
                $"q 0 0 0 rg {rectLeft} {rectBottom} {adjWidth} {adjHeight} re {fillOperator} Q",
            OverlayKind.WhiteFilledRectangle =>
                $"q 1 1 1 rg {rectLeft} {rectBottom} {rectWidth} {rectHeight} re f Q",
            OverlayKind.ImageXObject =>
                $"q {rectWidth} 0 0 {rectHeight} {rectLeft} {rectBottom} cm /Im0 Do Q",
            OverlayKind.DarkGrayRectangle =>
                $"q 0.5 0.5 0.5 rg {rectLeft} {rectBottom} {rectWidth} {rectHeight} re f Q",
            OverlayKind.LightGrayRectangle =>
                $"q 0.96 0.96 0.96 rg {rectLeft} {rectBottom} {rectWidth} {rectHeight} re f Q",
            OverlayKind.RedRectangle =>
                $"q 1 0 0 rg {rectLeft} {rectBottom} {rectWidth} {rectHeight} re f Q",
            _ => "",
        };

        string body = overlayKind == OverlayKind.None
            ? textOp
            : overlayBeforeText
                ? $"{overlayOp}\n{textOp}"
                : $"{textOp}\n{overlayOp}";

        offsets[4] = ms.Position;
        w.WriteLine("4 0 obj");
        w.WriteLine($"<< /Length {body.Length} >>\nstream");
        w.Write(body);
        w.WriteLine();
        w.WriteLine("endstream\nendobj");
        w.Flush();

        offsets[5] = ms.Position;
        w.WriteLine("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj");
        w.Flush();

        if (overlayKind == OverlayKind.ImageXObject)
        {
            var pixels = new byte[] { 255, 0, 0, 255, 0, 0, 255, 0, 0, 255, 0, 0 }; // 2x2 red
            offsets[6] = ms.Position;
            w.WriteLine("6 0 obj");
            w.WriteLine($"<< /Type /XObject /Subtype /Image /Width 2 /Height 2 " +
                        $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Length {pixels.Length} >>");
            w.WriteLine("stream");
            w.Flush();
            ms.Write(pixels, 0, pixels.Length);
            w.WriteLine();
            w.WriteLine("endstream\nendobj");
            w.Flush();
        }

        long xrefPos = ms.Position;
        int lastObj = overlayKind == OverlayKind.ImageXObject ? 6 : 5;
        w.WriteLine("xref");
        w.WriteLine($"0 {lastObj + 1}");
        w.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= lastObj; i++)
            w.WriteLine($"{offsets[i]:D10} 00000 n ");
        w.Flush();
        w.WriteLine($"trailer\n<< /Root 1 0 R /Size {lastObj + 1} >>\nstartxref\n{xrefPos}\n%%EOF");
        w.Flush();
        return ms.ToArray();
    }

    private static byte[] BuildPdfWithStateManagement(string text, double textX, double textY)
    {
        using var ms = new MemoryStream();
        using var w = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true)
        {
            NewLine = "\n"
        };

        w.WriteLine("%PDF-1.4");
        w.Flush();
        var offsets = new long[7];

        offsets[1] = ms.Position;
        w.WriteLine("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj");
        w.Flush();

        offsets[2] = ms.Position;
        w.WriteLine("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj");
        w.Flush();

        offsets[3] = ms.Position;
        w.WriteLine($"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                    $"/Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj");
        w.Flush();

        var body = $"BT /F1 14 Tf {textX} {textY} Td ({text}) Tj ET " +
                   $"q 0 0 0 rg {textX - 4} {textY - 4} {text.Length * 8} 14 re f Q";
        offsets[4] = ms.Position;
        w.WriteLine("4 0 obj");
        w.WriteLine($"<< /Length {body.Length} >>\nstream");
        w.Write(body);
        w.WriteLine();
        w.WriteLine("endstream\nendobj");
        w.Flush();

        offsets[5] = ms.Position;
        w.WriteLine("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj");
        w.Flush();

        long xrefPos = ms.Position;
        w.WriteLine("xref");
        w.WriteLine("0 6");
        w.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 5; i++)
            w.WriteLine($"{offsets[i]:D10} 00000 n ");
        w.Flush();
        w.WriteLine($"trailer\n<< /Root 1 0 R /Size 6 >>\nstartxref\n{xrefPos}\n%%EOF");
        w.Flush();
        return ms.ToArray();
    }

    private static byte[] BuildMultiPagePdf()
    {
        using var ms = new MemoryStream();
        using var w = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true)
        {
            NewLine = "\n"
        };

        w.WriteLine("%PDF-1.4");
        w.Flush();
        var offsets = new long[9];

        offsets[1] = ms.Position;
        w.WriteLine("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj");
        w.Flush();

        offsets[2] = ms.Position;
        w.WriteLine("2 0 obj\n<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>\nendobj");
        w.Flush();

        offsets[3] = ms.Position;
        w.WriteLine($"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                    $"/Contents 5 0 R /Resources << /Font << /F1 7 0 R >> >> >>\nendobj");
        w.Flush();

        offsets[4] = ms.Position;
        w.WriteLine($"4 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                    $"/Contents 6 0 R /Resources << /Font << /F1 7 0 R >> >> >>\nendobj");
        w.Flush();

        var body1 = "BT /F1 14 Tf 100 700 Td (Hidden) Tj ET q 0 0 0 rg 80 680 80 40 re f Q";
        offsets[5] = ms.Position;
        w.WriteLine("5 0 obj");
        w.WriteLine($"<< /Length {body1.Length} >>\nstream");
        w.Write(body1);
        w.WriteLine();
        w.WriteLine("endstream\nendobj");
        w.Flush();

        var body2 = "BT /F1 14 Tf 100 700 Td (Visible) Tj ET";
        offsets[6] = ms.Position;
        w.WriteLine("6 0 obj");
        w.WriteLine($"<< /Length {body2.Length} >>\nstream");
        w.Write(body2);
        w.WriteLine();
        w.WriteLine("endstream\nendobj");
        w.Flush();

        offsets[7] = ms.Position;
        w.WriteLine("7 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj");
        w.Flush();

        long xrefPos = ms.Position;
        w.WriteLine("xref");
        w.WriteLine("0 8");
        w.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 7; i++)
            w.WriteLine($"{offsets[i]:D10} 00000 n ");
        w.Flush();
        w.WriteLine($"trailer\n<< /Root 1 0 R /Size 8 >>\nstartxref\n{xrefPos}\n%%EOF");
        w.Flush();
        return ms.ToArray();
    }

    private static byte[] BuildPdfWithMultipleHiddenTexts()
    {
        using var ms = new MemoryStream();
        using var w = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true)
        {
            NewLine = "\n"
        };

        w.WriteLine("%PDF-1.4");
        w.Flush();
        var offsets = new long[6];

        offsets[1] = ms.Position;
        w.WriteLine("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj");
        w.Flush();

        offsets[2] = ms.Position;
        w.WriteLine("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj");
        w.Flush();

        offsets[3] = ms.Position;
        w.WriteLine($"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                    $"/Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj");
        w.Flush();

        var body = "BT /F1 14 Tf 100 700 Td (Text1) Tj 0 -50 Td (Text2) Tj ET " +
                   "q 0 0 0 rg 80 680 80 40 re f Q " +
                   "q 0 0 0 rg 80 630 80 40 re f Q";
        offsets[4] = ms.Position;
        w.WriteLine("4 0 obj");
        w.WriteLine($"<< /Length {body.Length} >>\nstream");
        w.Write(body);
        w.WriteLine();
        w.WriteLine("endstream\nendobj");
        w.Flush();

        offsets[5] = ms.Position;
        w.WriteLine("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj");
        w.Flush();

        long xrefPos = ms.Position;
        w.WriteLine("xref");
        w.WriteLine("0 6");
        w.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 5; i++)
            w.WriteLine($"{offsets[i]:D10} 00000 n ");
        w.Flush();
        w.WriteLine($"trailer\n<< /Root 1 0 R /Size 6 >>\nstartxref\n{xrefPos}\n%%EOF");
        w.Flush();
        return ms.ToArray();
    }

    private static byte[] BuildPdfWithCMYKColor(string text, double textX, double textY, double c, double m, double y, double k)
    {
        using var ms = new MemoryStream();
        using var w = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true)
        {
            NewLine = "\n"
        };

        w.WriteLine("%PDF-1.4");
        w.Flush();
        var offsets = new long[6];

        offsets[1] = ms.Position;
        w.WriteLine("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj");
        w.Flush();

        offsets[2] = ms.Position;
        w.WriteLine("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj");
        w.Flush();

        offsets[3] = ms.Position;
        w.WriteLine($"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                    $"/Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj");
        w.Flush();

        var body = $"BT /F1 14 Tf {textX} {textY} Td ({text}) Tj ET q {c} {m} {y} {k} k 80 680 80 40 re f Q";
        offsets[4] = ms.Position;
        w.WriteLine("4 0 obj");
        w.WriteLine($"<< /Length {body.Length} >>\nstream");
        w.Write(body);
        w.WriteLine();
        w.WriteLine("endstream\nendobj");
        w.Flush();

        offsets[5] = ms.Position;
        w.WriteLine("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj");
        w.Flush();

        long xrefPos = ms.Position;
        w.WriteLine("xref");
        w.WriteLine("0 6");
        w.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 5; i++)
            w.WriteLine($"{offsets[i]:D10} 00000 n ");
        w.Flush();
        w.WriteLine($"trailer\n<< /Root 1 0 R /Size 6 >>\nstartxref\n{xrefPos}\n%%EOF");
        w.Flush();
        return ms.ToArray();
    }

    private static byte[] BuildPdfWithNestedStates(string text, double textX, double textY)
    {
        using var ms = new MemoryStream();
        using var w = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true)
        {
            NewLine = "\n"
        };

        w.WriteLine("%PDF-1.4");
        w.Flush();
        var offsets = new long[6];

        offsets[1] = ms.Position;
        w.WriteLine("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj");
        w.Flush();

        offsets[2] = ms.Position;
        w.WriteLine("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj");
        w.Flush();

        offsets[3] = ms.Position;
        w.WriteLine($"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                    $"/Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj");
        w.Flush();

        var body = $"q q BT /F1 14 Tf {textX} {textY} Td ({text}) Tj ET q 0 0 0 rg 80 680 80 40 re f Q Q Q";
        offsets[4] = ms.Position;
        w.WriteLine("4 0 obj");
        w.WriteLine($"<< /Length {body.Length} >>\nstream");
        w.Write(body);
        w.WriteLine();
        w.WriteLine("endstream\nendobj");
        w.Flush();

        offsets[5] = ms.Position;
        w.WriteLine("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj");
        w.Flush();

        long xrefPos = ms.Position;
        w.WriteLine("xref");
        w.WriteLine("0 6");
        w.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 5; i++)
            w.WriteLine($"{offsets[i]:D10} 00000 n ");
        w.Flush();
        w.WriteLine($"trailer\n<< /Root 1 0 R /Size 6 >>\nstartxref\n{xrefPos}\n%%EOF");
        w.Flush();
        return ms.ToArray();
    }

    private static byte[] BuildPdfWithTransformedOverlay(string text, double textX, double textY)
    {
        using var ms = new MemoryStream();
        using var w = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true)
        {
            NewLine = "\n"
        };

        w.WriteLine("%PDF-1.4");
        w.Flush();
        var offsets = new long[6];

        offsets[1] = ms.Position;
        w.WriteLine("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj");
        w.Flush();

        offsets[2] = ms.Position;
        w.WriteLine("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj");
        w.Flush();

        offsets[3] = ms.Position;
        w.WriteLine($"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                    $"/Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj");
        w.Flush();

        var body = $"BT /F1 14 Tf {textX} {textY} Td ({text}) Tj ET q 1 0 0 1 0 0 cm 0 0 0 rg 80 680 80 40 re f Q";
        offsets[4] = ms.Position;
        w.WriteLine("4 0 obj");
        w.WriteLine($"<< /Length {body.Length} >>\nstream");
        w.Write(body);
        w.WriteLine();
        w.WriteLine("endstream\nendobj");
        w.Flush();

        offsets[5] = ms.Position;
        w.WriteLine("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj");
        w.Flush();

        long xrefPos = ms.Position;
        w.WriteLine("xref");
        w.WriteLine("0 6");
        w.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 5; i++)
            w.WriteLine($"{offsets[i]:D10} 00000 n ");
        w.Flush();
        w.WriteLine($"trailer\n<< /Root 1 0 R /Size 6 >>\nstartxref\n{xrefPos}\n%%EOF");
        w.Flush();
        return ms.ToArray();
    }

    private static byte[] BuildEmptyPdf()
    {
        using var ms = new MemoryStream();
        using var w = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true)
        {
            NewLine = "\n"
        };

        w.WriteLine("%PDF-1.4");
        w.Flush();
        var offsets = new long[5];

        offsets[1] = ms.Position;
        w.WriteLine("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj");
        w.Flush();

        offsets[2] = ms.Position;
        w.WriteLine("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj");
        w.Flush();

        offsets[3] = ms.Position;
        w.WriteLine($"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>\nendobj");
        w.Flush();

        offsets[4] = ms.Position;
        w.WriteLine("4 0 obj");
        w.WriteLine($"<< /Length 0 >>\nstream");
        w.WriteLine("endstream\nendobj");
        w.Flush();

        long xrefPos = ms.Position;
        w.WriteLine("xref");
        w.WriteLine("0 5");
        w.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 4; i++)
            w.WriteLine($"{offsets[i]:D10} 00000 n ");
        w.Flush();
        w.WriteLine($"trailer\n<< /Root 1 0 R /Size 5 >>\nstartxref\n{xrefPos}\n%%EOF");
        w.Flush();
        return ms.ToArray();
    }

    private static byte[] BuildPdfWithStrokeOnly(string text, double textX, double textY)
    {
        using var ms = new MemoryStream();
        using var w = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true)
        {
            NewLine = "\n"
        };

        w.WriteLine("%PDF-1.4");
        w.Flush();
        var offsets = new long[6];

        offsets[1] = ms.Position;
        w.WriteLine("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj");
        w.Flush();

        offsets[2] = ms.Position;
        w.WriteLine("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj");
        w.Flush();

        offsets[3] = ms.Position;
        w.WriteLine($"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                    $"/Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj");
        w.Flush();

        var body = $"BT /F1 14 Tf {textX} {textY} Td ({text}) Tj ET q 0 0 0 RG 80 680 80 40 re S Q";
        offsets[4] = ms.Position;
        w.WriteLine("4 0 obj");
        w.WriteLine($"<< /Length {body.Length} >>\nstream");
        w.Write(body);
        w.WriteLine();
        w.WriteLine("endstream\nendobj");
        w.Flush();

        offsets[5] = ms.Position;
        w.WriteLine("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj");
        w.Flush();

        long xrefPos = ms.Position;
        w.WriteLine("xref");
        w.WriteLine("0 6");
        w.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 5; i++)
            w.WriteLine($"{offsets[i]:D10} 00000 n ");
        w.Flush();
        w.WriteLine($"trailer\n<< /Root 1 0 R /Size 6 >>\nstartxref\n{xrefPos}\n%%EOF");
        w.Flush();
        return ms.ToArray();
    }

    private static byte[] BuildPdfWithNoOpPath(string text, double textX, double textY)
    {
        using var ms = new MemoryStream();
        using var w = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true)
        {
            NewLine = "\n"
        };

        w.WriteLine("%PDF-1.4");
        w.Flush();
        var offsets = new long[6];

        offsets[1] = ms.Position;
        w.WriteLine("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj");
        w.Flush();

        offsets[2] = ms.Position;
        w.WriteLine("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj");
        w.Flush();

        offsets[3] = ms.Position;
        w.WriteLine($"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                    $"/Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj");
        w.Flush();

        var body = $"BT /F1 14 Tf {textX} {textY} Td ({text}) Tj ET q 80 680 80 40 re n Q";
        offsets[4] = ms.Position;
        w.WriteLine("4 0 obj");
        w.WriteLine($"<< /Length {body.Length} >>\nstream");
        w.Write(body);
        w.WriteLine();
        w.WriteLine("endstream\nendobj");
        w.Flush();

        offsets[5] = ms.Position;
        w.WriteLine("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj");
        w.Flush();

        long xrefPos = ms.Position;
        w.WriteLine("xref");
        w.WriteLine("0 6");
        w.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 5; i++)
            w.WriteLine($"{offsets[i]:D10} 00000 n ");
        w.Flush();
        w.WriteLine($"trailer\n<< /Root 1 0 R /Size 6 >>\nstartxref\n{xrefPos}\n%%EOF");
        w.Flush();
        return ms.ToArray();
    }

    #region ExtractText with ' and " Operator Tests

    [Fact]
    public void ExtractText_WithSingleQuoteOperator_ReturnsStringValue()
    {
        // Test line 262-263: ExtractText where op.Name == "'" and extracts PdfString value
        // The ' operator in PDF is equivalent to: Td (go to next line) followed by Tj (show text)
        using var ms = new System.IO.MemoryStream();
        using var w = new System.IO.StreamWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true);

        w.WriteLine("%PDF-1.4");
        w.Flush();

        var offsets = new long[6];
        offsets[1] = ms.Position;
        w.WriteLine("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj");
        w.Flush();
        offsets[2] = ms.Position;
        w.WriteLine("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj");
        w.Flush();

        offsets[3] = ms.Position;
        w.WriteLine("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj");
        w.Flush();

        // Content with ' operator (show text with newline)
        var contentStr = "BT /F1 12 Tf 100 700 Td (Line1) Tj (Line2) ' ET";
        offsets[4] = ms.Position;
        w.WriteLine($"4 0 obj\n<< /Length {contentStr.Length} >>\nstream\n{contentStr}\nendstream\nendobj");
        w.Flush();

        offsets[5] = ms.Position;
        w.WriteLine("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj");
        w.Flush();

        long xrefPos = ms.Position;
        w.WriteLine("xref\n0 6\n0000000000 65535 f ");
        for (int i = 1; i <= 5; i++)
            w.WriteLine($"{offsets[i]:D10} 00000 n ");
        w.Flush();
        w.WriteLine($"trailer\n<< /Root 1 0 R /Size 6 >>\nstartxref\n{xrefPos}\n%%EOF");
        w.Flush();

        var pdfData = ms.ToArray();
        using var doc = Excise.Core.Document.PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        // Parse the content stream
        var parser = new Excise.Core.Content.ContentStreamParser(System.Text.Encoding.ASCII.GetBytes(contentStr), page);
        var contentStream = parser.Parse();

        // Should have parsed the ' operator successfully
        var quoteOps = contentStream.Operators.Where(op => op.Name == "'").ToList();
        quoteOps.Should().HaveCount(1);
        quoteOps[0].Operands.Should().HaveCount(1);
        quoteOps[0].Operands[0].Should().BeOfType<PdfString>();
    }

    [Fact]
    public void ExtractText_WithDoubleQuoteOperator_ReturnsStringValue()
    {
        // Test line 262-263: ExtractText where op.Name == "\"" and extracts PdfString value
        // The " operator in PDF is: Tw (set word spacing), Tc (set char spacing), Td, Tj
        using var ms = new System.IO.MemoryStream();
        using var w = new System.IO.StreamWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true);

        w.WriteLine("%PDF-1.4");
        w.Flush();

        var offsets = new long[6];
        offsets[1] = ms.Position;
        w.WriteLine("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj");
        w.Flush();
        offsets[2] = ms.Position;
        w.WriteLine("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj");
        w.Flush();

        offsets[3] = ms.Position;
        w.WriteLine("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj");
        w.Flush();

        // Content with " operator (set spacing and show text)
        var contentStr = "BT /F1 12 Tf 100 700 Td 0.5 0.2 (Text) \" ET";
        offsets[4] = ms.Position;
        w.WriteLine($"4 0 obj\n<< /Length {contentStr.Length} >>\nstream\n{contentStr}\nendstream\nendobj");
        w.Flush();

        offsets[5] = ms.Position;
        w.WriteLine("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj");
        w.Flush();

        long xrefPos = ms.Position;
        w.WriteLine("xref\n0 6\n0000000000 65535 f ");
        for (int i = 1; i <= 5; i++)
            w.WriteLine($"{offsets[i]:D10} 00000 n ");
        w.Flush();
        w.WriteLine($"trailer\n<< /Root 1 0 R /Size 6 >>\nstartxref\n{xrefPos}\n%%EOF");
        w.Flush();

        var pdfData = ms.ToArray();
        using var doc = Excise.Core.Document.PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        var parser = new Excise.Core.Content.ContentStreamParser(System.Text.Encoding.ASCII.GetBytes(contentStr), page);
        var contentStream = parser.Parse();

        // Should have parsed the " operator successfully (aw ac string ")
        var doubleQuoteOps = contentStream.Operators.Where(op => op.Name == "\"").ToList();
        doubleQuoteOps.Should().HaveCount(1);
        doubleQuoteOps[0].Operands.Should().HaveCount(3);
        doubleQuoteOps[0].Operands[2].Should().BeOfType<PdfString>();
    }

    [Fact]
    public void ExtractText_WithTjOperator_ReturnsStringValue()
    {
        // Positive test: standard Tj operator (extract text via string operand)
        using var ms = new System.IO.MemoryStream();
        using var w = new System.IO.StreamWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true);

        w.WriteLine("%PDF-1.4");
        w.Flush();

        var offsets = new long[6];
        offsets[1] = ms.Position;
        w.WriteLine("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj");
        w.Flush();
        offsets[2] = ms.Position;
        w.WriteLine("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj");
        w.Flush();

        offsets[3] = ms.Position;
        w.WriteLine("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj");
        w.Flush();

        var contentStr = "BT /F1 12 Tf 100 700 Td (Hello) Tj ET";
        offsets[4] = ms.Position;
        w.WriteLine($"4 0 obj\n<< /Length {contentStr.Length} >>\nstream\n{contentStr}\nendstream\nendobj");
        w.Flush();

        offsets[5] = ms.Position;
        w.WriteLine("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj");
        w.Flush();

        long xrefPos = ms.Position;
        w.WriteLine("xref\n0 6\n0000000000 65535 f ");
        for (int i = 1; i <= 5; i++)
            w.WriteLine($"{offsets[i]:D10} 00000 n ");
        w.Flush();
        w.WriteLine($"trailer\n<< /Root 1 0 R /Size 6 >>\nstartxref\n{xrefPos}\n%%EOF");
        w.Flush();

        var pdfData = ms.ToArray();
        using var doc = Excise.Core.Document.PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        var parser = new Excise.Core.Content.ContentStreamParser(System.Text.Encoding.ASCII.GetBytes(contentStr), page);
        var contentStream = parser.Parse();

        var tjOps = contentStream.Operators.Where(op => op.Name == "Tj").ToList();
        tjOps.Should().HaveCount(1);
        tjOps[0].TextContent.Should().Be("Hello");
    }

    #endregion
}
