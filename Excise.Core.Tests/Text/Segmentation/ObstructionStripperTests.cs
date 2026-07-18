using AwesomeAssertions;
using Excise.Core.Content;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Core.Text.Segmentation;
using System.IO;
using System.Text;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

public class ObstructionStripperTests
{
    /// <summary>
    /// Create a minimal valid PDF for testing.
    /// </summary>
    private static PdfPage GetTestPage()
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        long o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        long o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        long o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");
        long xrefPos = sb.Length;
        sb.Append("xref\n0 4\n");
        sb.Append("0000000000 65535 f \n");
        sb.Append($"{o1:D10} 00000 n \n");
        sb.Append($"{o2:D10} 00000 n \n");
        sb.Append($"{o3:D10} 00000 n \n");
        sb.Append("trailer\n<< /Size 4 /Root 1 0 R >>\n");
        sb.Append($"startxref\n{xrefPos}\n%%EOF\n");
        return PdfDocument.Open(
            new MemoryStream(Encoding.Latin1.GetBytes(sb.ToString())),
            ownsStream: false).Pages[0];
    }

    [Fact]
    public void StripObstructions_WithNullPage_ThrowsArgumentNullException()
    {
        var action = () => ObstructionStripper.StripObstructions(null!);

        action.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void StripObstructions_WithEmptyContentStream_ReturnsWithoutError()
    {
        var page = GetTestPage();
        page.SetContentStream(new ContentStream());

        var action = () => ObstructionStripper.StripObstructions(page);

        action.Should().NotThrow();
        page.GetContentStream().Count.Should().Be(0);
    }

    [Fact]
    public void StripObstructions_BlackFillRectangle_RemovesPathAndFill()
    {
        var page = GetTestPage();
        var ops = new List<ContentOperator>
        {
            ContentOperator.SaveState(),
            ContentOperator.SetFillRgb(0, 0, 0),  // Black fill
            ContentOperator.Rectangle(10, 10, 100, 100),
            ContentOperator.Fill(),
            ContentOperator.RestoreState()
        };
        page.SetContentStream(new ContentStream(ops));

        ObstructionStripper.StripObstructions(page);

        var result = page.GetContentStream();
        // Should remove: "re" and "f"
        result.Operators.Any(op => op.Name == "re").Should().BeFalse();
        result.Operators.Any(op => op.Name == "f").Should().BeFalse();
        // Should keep: "q", "Q", and color operator
        result.Operators.Any(op => op.Name == "q").Should().BeTrue();
        result.Operators.Any(op => op.Name == "Q").Should().BeTrue();
        result.Operators.Any(op => op.Name == "rg").Should().BeTrue();
    }

    [Fact]
    public void StripObstructions_WhiteFillRectangle_KeepsPathAndFill()
    {
        var page = GetTestPage();
        var ops = new List<ContentOperator>
        {
            ContentOperator.SaveState(),
            ContentOperator.SetFillRgb(1, 1, 1),  // White fill (not obstructive)
            ContentOperator.Rectangle(10, 10, 100, 100),
            ContentOperator.Fill(),
            ContentOperator.RestoreState()
        };
        page.SetContentStream(new ContentStream(ops));

        ObstructionStripper.StripObstructions(page);

        var result = page.GetContentStream();
        // Should keep all operators
        result.Operators.Any(op => op.Name == "re").Should().BeTrue();
        result.Operators.Any(op => op.Name == "f").Should().BeTrue();
    }

    [Fact]
    public void StripObstructions_NearWhiteFillRectangle_KeepsPathAndFill()
    {
        var page = GetTestPage();
        var ops = new List<ContentOperator>
        {
            ContentOperator.SaveState(),
            ContentOperator.SetFillRgb(0.95, 0.95, 0.95),  // Near-white (not obstructive)
            ContentOperator.Rectangle(10, 10, 100, 100),
            ContentOperator.Fill(),
            ContentOperator.RestoreState()
        };
        page.SetContentStream(new ContentStream(ops));

        ObstructionStripper.StripObstructions(page);

        var result = page.GetContentStream();
        // Should keep all operators
        result.Operators.Any(op => op.Name == "re").Should().BeTrue();
        result.Operators.Any(op => op.Name == "f").Should().BeTrue();
    }

    [Fact]
    public void StripObstructions_GrayFillRectangle_RemovesPathAndFill()
    {
        var page = GetTestPage();
        var ops = new List<ContentOperator>
        {
            ContentOperator.SaveState(),
            ContentOperator.SetFillGray(0.5),  // Medium gray (obstructive)
            ContentOperator.Rectangle(10, 10, 100, 100),
            ContentOperator.Fill(),
            ContentOperator.RestoreState()
        };
        page.SetContentStream(new ContentStream(ops));

        ObstructionStripper.StripObstructions(page);

        var result = page.GetContentStream();
        result.Operators.Any(op => op.Name == "re").Should().BeFalse();
        result.Operators.Any(op => op.Name == "f").Should().BeFalse();
    }

    [Fact]
    public void StripObstructions_StrokeOnlyPath_KeepsPathAndStroke()
    {
        var page = GetTestPage();
        var ops = new List<ContentOperator>
        {
            ContentOperator.SaveState(),
            ContentOperator.SetFillRgb(0, 0, 0),  // Dark fill color
            ContentOperator.Rectangle(10, 10, 100, 100),
            ContentOperator.Stroke(),  // Stroke only, not fill
            ContentOperator.RestoreState()
        };
        page.SetContentStream(new ContentStream(ops));

        ObstructionStripper.StripObstructions(page);

        var result = page.GetContentStream();
        // Stroke operations should be kept
        result.Operators.Any(op => op.Name == "re").Should().BeTrue();
        result.Operators.Any(op => op.Name == "S").Should().BeTrue();
    }

    [Fact]
    public void StripObstructions_FillEvenOddWithDarkColor_RemovesPathAndFill()
    {
        var page = GetTestPage();
        var ops = new List<ContentOperator>
        {
            ContentOperator.SaveState(),
            ContentOperator.SetFillRgb(0.1, 0.1, 0.1),
            ContentOperator.Rectangle(10, 10, 100, 100),
            ContentOperator.FillEvenOdd(),  // f*
            ContentOperator.RestoreState()
        };
        page.SetContentStream(new ContentStream(ops));

        ObstructionStripper.StripObstructions(page);

        var result = page.GetContentStream();
        result.Operators.Any(op => op.Name == "f*").Should().BeFalse();
        result.Operators.Any(op => op.Name == "re").Should().BeFalse();
    }

    [Fact]
    public void StripObstructions_FillAndStrokeWithDarkColor_RemovesPathAndOp()
    {
        var page = GetTestPage();
        var ops = new List<ContentOperator>
        {
            ContentOperator.SaveState(),
            ContentOperator.SetFillRgb(0, 0, 0),
            ContentOperator.Rectangle(10, 10, 100, 100),
            ContentOperator.FillAndStroke(),  // B
            ContentOperator.RestoreState()
        };
        page.SetContentStream(new ContentStream(ops));

        ObstructionStripper.StripObstructions(page);

        var result = page.GetContentStream();
        result.Operators.Any(op => op.Name == "B").Should().BeFalse();
        result.Operators.Any(op => op.Name == "re").Should().BeFalse();
    }

    [Fact]
    public void StripObstructions_TextOperators_AlwaysKept()
    {
        var page = GetTestPage();
        var ops = new List<ContentOperator>
        {
            ContentOperator.BeginText(),
            ContentOperator.ShowText("Important Text"),
            ContentOperator.EndText(),
            ContentOperator.SaveState(),
            ContentOperator.SetFillRgb(0, 0, 0),
            ContentOperator.Rectangle(10, 10, 100, 100),
            ContentOperator.Fill(),
            ContentOperator.RestoreState()
        };
        page.SetContentStream(new ContentStream(ops));

        ObstructionStripper.StripObstructions(page);

        var result = page.GetContentStream();
        // Text operators should always be kept
        result.Operators.Any(op => op.Name == "BT").Should().BeTrue();
        result.Operators.Any(op => op.Name == "Tj").Should().BeTrue();
        result.Operators.Any(op => op.Name == "ET").Should().BeTrue();
    }

    [Fact]
    public void StripObstructions_MultiplePathConstructionOps_AllRemoved()
    {
        var page = GetTestPage();
        var ops = new List<ContentOperator>
        {
            ContentOperator.SaveState(),
            ContentOperator.SetFillRgb(0.1, 0, 0),
            ContentOperator.MoveTo(10, 10),      // m
            ContentOperator.LineTo(100, 10),     // l
            ContentOperator.LineTo(100, 100),    // l
            ContentOperator.ClosePath(),         // h
            ContentOperator.Fill(),
            ContentOperator.RestoreState()
        };
        page.SetContentStream(new ContentStream(ops));

        ObstructionStripper.StripObstructions(page);

        var result = page.GetContentStream();
        // All path ops should be removed
        result.Operators.Any(op => op.Name == "m").Should().BeFalse();
        result.Operators.Any(op => op.Name == "l").Should().BeFalse();
        result.Operators.Any(op => op.Name == "h").Should().BeFalse();
        result.Operators.Any(op => op.Name == "f").Should().BeFalse();
    }

    [Fact]
    public void StripObstructions_NoPathOperator_FillNotRemoved()
    {
        var page = GetTestPage();
        var ops = new List<ContentOperator>
        {
            ContentOperator.SaveState(),
            ContentOperator.SetFillRgb(0, 0, 0),
            ContentOperator.Fill(),  // Fill without path construction
            ContentOperator.RestoreState()
        };
        page.SetContentStream(new ContentStream(ops));

        ObstructionStripper.StripObstructions(page);

        var result = page.GetContentStream();
        // Fill with no preceding path should be kept (pendingPath is empty)
        result.Operators.Any(op => op.Name == "f").Should().BeTrue();
    }

    [Fact]
    public void StripObstructions_CMYKBlackColor_RemovesPathAndFill()
    {
        var page = GetTestPage();
        var ops = new List<ContentOperator>
        {
            ContentOperator.SaveState(),
            // CMYK black: C=0, M=0, Y=0, K=1 (results in RGB=0,0,0)
            new ContentOperator("k", new PdfObject[] {
                new PdfInteger(0),
                new PdfInteger(0),
                new PdfInteger(0),
                new PdfInteger(1)
            }),
            ContentOperator.Rectangle(10, 10, 100, 100),
            ContentOperator.Fill(),
            ContentOperator.RestoreState()
        };
        page.SetContentStream(new ContentStream(ops));

        ObstructionStripper.StripObstructions(page);

        var result = page.GetContentStream();
        // CMYK black should be obstructive
        result.Operators.Any(op => op.Name == "re").Should().BeFalse();
        result.Operators.Any(op => op.Name == "f").Should().BeFalse();
    }

    [Fact]
    public void StripObstructions_EndPathNoOp_KeepsPathOps()
    {
        var page = GetTestPage();
        var ops = new List<ContentOperator>
        {
            ContentOperator.SaveState(),
            ContentOperator.SetFillRgb(0.1, 0.1, 0.1),
            ContentOperator.Rectangle(10, 10, 100, 100),
            ContentOperator.EndPath(),  // n (no-op paint)
            ContentOperator.RestoreState()
        };
        page.SetContentStream(new ContentStream(ops));

        ObstructionStripper.StripObstructions(page);

        var result = page.GetContentStream();
        // EndPath is not a fill, so path should be kept
        result.Operators.Any(op => op.Name == "re").Should().BeTrue();
        result.Operators.Any(op => op.Name == "n").Should().BeTrue();
    }

    [Fact]
    public void StripObstructions_CloseAndStroke_KeepsPathOps()
    {
        var page = GetTestPage();
        var ops = new List<ContentOperator>
        {
            ContentOperator.SaveState(),
            ContentOperator.SetFillRgb(0.1, 0.1, 0.1),
            ContentOperator.Rectangle(10, 10, 100, 100),
            ContentOperator.CloseAndStroke(),  // s (stroke, not fill)
            ContentOperator.RestoreState()
        };
        page.SetContentStream(new ContentStream(ops));

        ObstructionStripper.StripObstructions(page);

        var result = page.GetContentStream();
        // Stroke is not fill, so path should be kept
        result.Operators.Any(op => op.Name == "re").Should().BeTrue();
        result.Operators.Any(op => op.Name == "s").Should().BeTrue();
    }

    [Fact]
    public void StripObstructions_MultipleObstructiveRectangles_AllRemoved()
    {
        var page = GetTestPage();
        var ops = new List<ContentOperator>
        {
            // First obstructive rectangle
            ContentOperator.SaveState(),
            ContentOperator.SetFillRgb(0, 0, 0),
            ContentOperator.Rectangle(10, 10, 100, 100),
            ContentOperator.Fill(),
            ContentOperator.RestoreState(),
            // Second obstructive rectangle
            ContentOperator.SaveState(),
            ContentOperator.SetFillGray(0.2),
            ContentOperator.MoveTo(150, 150),
            ContentOperator.LineTo(250, 150),
            ContentOperator.LineTo(250, 250),
            ContentOperator.ClosePath(),
            ContentOperator.Fill(),
            ContentOperator.RestoreState()
        };
        page.SetContentStream(new ContentStream(ops));

        ObstructionStripper.StripObstructions(page);

        var result = page.GetContentStream();
        // Both rectangles should be removed
        result.Operators.Where(op => op.Name == "f").Should().BeEmpty();
        result.Operators.Where(op => op.Name == "re").Should().BeEmpty();
        result.Operators.Where(op => op.Name == "m").Should().BeEmpty();
    }

    [Fact]
    public void StripObstructions_MixedObstructiveAndNonObstructive_RemovesOnlyObstructive()
    {
        var page = GetTestPage();
        var ops = new List<ContentOperator>
        {
            // Non-obstructive white rectangle
            ContentOperator.SaveState(),
            ContentOperator.SetFillRgb(1, 1, 1),
            ContentOperator.Rectangle(10, 10, 50, 50),
            ContentOperator.Fill(),
            ContentOperator.RestoreState(),
            // Obstructive black rectangle
            ContentOperator.SaveState(),
            ContentOperator.SetFillRgb(0, 0, 0),
            ContentOperator.Rectangle(100, 100, 150, 150),
            ContentOperator.Fill(),
            ContentOperator.RestoreState()
        };
        page.SetContentStream(new ContentStream(ops));

        ObstructionStripper.StripObstructions(page);

        var result = page.GetContentStream();
        // Should have kept first rectangle and removed second
        result.Count.Should().BeGreaterThan(0);
        // Total fill operators: should have 1 (the white one)
        var fillOps = result.Operators.Where(op => op.Name == "f").ToList();
        fillOps.Should().HaveCount(1);
    }

    [Fact]
    public void StripObstructions_ClosePathFillAndStrokeBOperator_RemovesPathAndOp()
    {
        var page = GetTestPage();
        var ops = new List<ContentOperator>
        {
            ContentOperator.SaveState(),
            ContentOperator.SetFillRgb(0, 0, 0),
            ContentOperator.Rectangle(10, 10, 100, 100),
            new ContentOperator("b", Array.Empty<PdfObject>()),  // Close path + fill + stroke
            ContentOperator.RestoreState()
        };
        page.SetContentStream(new ContentStream(ops));

        ObstructionStripper.StripObstructions(page);

        var result = page.GetContentStream();
        result.Operators.Any(op => op.Name == "b").Should().BeFalse();
        result.Operators.Any(op => op.Name == "re").Should().BeFalse();
    }

    [Fact]
    public void StripObstructions_ClosePathFillAndStrokeEvenOddBStarOperator_RemovesPathAndOp()
    {
        var page = GetTestPage();
        var ops = new List<ContentOperator>
        {
            ContentOperator.SaveState(),
            ContentOperator.SetFillRgb(0, 0, 0),
            ContentOperator.Rectangle(10, 10, 100, 100),
            new ContentOperator("b*", Array.Empty<PdfObject>()),  // Close path + fill even-odd + stroke
            ContentOperator.RestoreState()
        };
        page.SetContentStream(new ContentStream(ops));

        ObstructionStripper.StripObstructions(page);

        var result = page.GetContentStream();
        result.Operators.Any(op => op.Name == "b*").Should().BeFalse();
        result.Operators.Any(op => op.Name == "re").Should().BeFalse();
    }

    [Fact]
    public void StripObstructions_FillAndStrokeEvenOddBStarOperator_RemovesPathAndOp()
    {
        var page = GetTestPage();
        var ops = new List<ContentOperator>
        {
            ContentOperator.SaveState(),
            ContentOperator.SetFillRgb(0, 0, 0),
            ContentOperator.Rectangle(10, 10, 100, 100),
            new ContentOperator("B*", Array.Empty<PdfObject>()),  // Fill even-odd + stroke
            ContentOperator.RestoreState()
        };
        page.SetContentStream(new ContentStream(ops));

        ObstructionStripper.StripObstructions(page);

        var result = page.GetContentStream();
        result.Operators.Any(op => op.Name == "B*").Should().BeFalse();
        result.Operators.Any(op => op.Name == "re").Should().BeFalse();
    }

    [Fact]
    public void StripObstructions_CurveToCubicBezier_RemovesCurveAndFill()
    {
        var page = GetTestPage();
        var ops = new List<ContentOperator>
        {
            ContentOperator.SaveState(),
            ContentOperator.SetFillRgb(0, 0, 0),
            ContentOperator.MoveTo(10, 10),
            new ContentOperator("c", new PdfObject[] {
                new PdfReal(20), new PdfReal(20),
                new PdfReal(30), new PdfReal(30),
                new PdfReal(40), new PdfReal(40)
            }),  // Cubic bezier curve
            ContentOperator.Fill(),
            ContentOperator.RestoreState()
        };
        page.SetContentStream(new ContentStream(ops));

        ObstructionStripper.StripObstructions(page);

        var result = page.GetContentStream();
        result.Operators.Any(op => op.Name == "c").Should().BeFalse();
        result.Operators.Any(op => op.Name == "f").Should().BeFalse();
    }

    [Fact]
    public void StripObstructions_CurveToNoFirstControlPoint_RemovesCurveAndFill()
    {
        var page = GetTestPage();
        var ops = new List<ContentOperator>
        {
            ContentOperator.SaveState(),
            ContentOperator.SetFillRgb(0, 0, 0),
            ContentOperator.MoveTo(10, 10),
            new ContentOperator("v", new PdfObject[] {
                new PdfReal(30), new PdfReal(30),
                new PdfReal(40), new PdfReal(40)
            }),  // Bezier with no first control point
            ContentOperator.Fill(),
            ContentOperator.RestoreState()
        };
        page.SetContentStream(new ContentStream(ops));

        ObstructionStripper.StripObstructions(page);

        var result = page.GetContentStream();
        result.Operators.Any(op => op.Name == "v").Should().BeFalse();
        result.Operators.Any(op => op.Name == "f").Should().BeFalse();
    }

    [Fact]
    public void StripObstructions_CurveToNoLastControlPoint_RemovesCurveAndFill()
    {
        var page = GetTestPage();
        var ops = new List<ContentOperator>
        {
            ContentOperator.SaveState(),
            ContentOperator.SetFillRgb(0, 0, 0),
            ContentOperator.MoveTo(10, 10),
            new ContentOperator("y", new PdfObject[] {
                new PdfReal(20), new PdfReal(20),
                new PdfReal(40), new PdfReal(40)
            }),  // Bezier with no last control point
            ContentOperator.Fill(),
            ContentOperator.RestoreState()
        };
        page.SetContentStream(new ContentStream(ops));

        ObstructionStripper.StripObstructions(page);

        var result = page.GetContentStream();
        result.Operators.Any(op => op.Name == "y").Should().BeFalse();
        result.Operators.Any(op => op.Name == "f").Should().BeFalse();
    }

    [Fact]
    public void StripObstructions_CMYKNearWhiteColor_KeepsPathAndFill()
    {
        var page = GetTestPage();
        var ops = new List<ContentOperator>
        {
            ContentOperator.SaveState(),
            // CMYK near-white: C=0, M=0, Y=0, K=0.02 (results in near-white)
            new ContentOperator("k", new PdfObject[] {
                new PdfReal(0),
                new PdfReal(0),
                new PdfReal(0),
                new PdfReal(0.02)
            }),
            ContentOperator.Rectangle(10, 10, 100, 100),
            ContentOperator.Fill(),
            ContentOperator.RestoreState()
        };
        page.SetContentStream(new ContentStream(ops));

        ObstructionStripper.StripObstructions(page);

        var result = page.GetContentStream();
        // Near-white CMYK should NOT be obstructive
        result.Operators.Any(op => op.Name == "re").Should().BeTrue();
        result.Operators.Any(op => op.Name == "f").Should().BeTrue();
    }

    [Fact]
    public void StripObstructions_GrayscaleExactly0_95_NotObstructive()
    {
        var page = GetTestPage();
        var ops = new List<ContentOperator>
        {
            ContentOperator.SaveState(),
            new ContentOperator("g", new PdfObject[] { new PdfReal(0.95) }),
            ContentOperator.Rectangle(10, 10, 100, 100),
            ContentOperator.Fill(),
            ContentOperator.RestoreState()
        };
        page.SetContentStream(new ContentStream(ops));

        ObstructionStripper.StripObstructions(page);

        var result = page.GetContentStream();
        // g=0.95 is exactly at threshold (>= 0.95 is not obstructive)
        result.Operators.Any(op => op.Name == "re").Should().BeTrue();
        result.Operators.Any(op => op.Name == "f").Should().BeTrue();
    }

    [Fact]
    public void StripObstructions_Grayscale0_94_IsObstructive()
    {
        var page = GetTestPage();
        var ops = new List<ContentOperator>
        {
            ContentOperator.SaveState(),
            new ContentOperator("g", new PdfObject[] { new PdfReal(0.94) }),
            ContentOperator.Rectangle(10, 10, 100, 100),
            ContentOperator.Fill(),
            ContentOperator.RestoreState()
        };
        page.SetContentStream(new ContentStream(ops));

        ObstructionStripper.StripObstructions(page);

        var result = page.GetContentStream();
        // g=0.94 is below threshold, should be obstructive
        result.Operators.Any(op => op.Name == "re").Should().BeFalse();
        result.Operators.Any(op => op.Name == "f").Should().BeFalse();
    }
}
