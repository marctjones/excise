using System.IO;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Xunit;

namespace Excise.Core.Tests.Document;

/// <summary>
/// #356: PdfPage.ToContentStreamCoordinates must map a visual-space rectangle
/// (top-left origin, y-down, rotated dimensions) into content-stream space
/// (MediaBox bottom-left origin, y-up) correctly for every /Rotate value.
/// A plain Y-flip is only right at 0°; at 90/180/270 it redacts the wrong
/// region. Examples use MediaBox [0 0 100 200] and a 10×20 box at the visual
/// top-left corner so each rotation lands in a distinct, hand-checkable place.
/// </summary>
public class PageRotationCoordinateTests
{
    // Visual box: x ∈ [0,10], y ∈ [0,20] (y measured downward from the top).
    private static PdfRectangle VisualTopLeftBox() => new(0, 0, 10, 20);

    [Fact]
    public void Rotate0_IsPlainYFlip()
    {
        var page = PageWith(rotate: 0);
        var c = page.ToContentStreamCoordinates(VisualTopLeftBox()).Normalize();

        // Visual top-left → content top-left (y near the top = 200).
        c.Left.Should().Be(0);
        c.Right.Should().Be(10);
        c.Bottom.Should().Be(180);
        c.Top.Should().Be(200);
    }

    [Fact]
    public void Rotate90_SwapsAxesToBottomLeft()
    {
        var page = PageWith(rotate: 90);
        var c = page.ToContentStreamCoordinates(VisualTopLeftBox()).Normalize();

        // Clockwise 90°: visual top-left → content bottom-left; dims swap.
        c.Left.Should().Be(0);
        c.Bottom.Should().Be(0);
        c.Right.Should().Be(20);
        c.Top.Should().Be(10);
    }

    [Fact]
    public void Rotate180_MapsToBottomRight()
    {
        var page = PageWith(rotate: 180);
        var c = page.ToContentStreamCoordinates(VisualTopLeftBox()).Normalize();

        c.Left.Should().Be(90);
        c.Right.Should().Be(100);
        c.Bottom.Should().Be(0);
        c.Top.Should().Be(20);
    }

    [Fact]
    public void Rotate270_MapsToTopRight()
    {
        var page = PageWith(rotate: 270);
        var c = page.ToContentStreamCoordinates(VisualTopLeftBox()).Normalize();

        c.Left.Should().Be(80);
        c.Right.Should().Be(100);
        c.Bottom.Should().Be(190);
        c.Top.Should().Be(200);
    }

    [Theory]
    [InlineData(0, 100, 200)]
    [InlineData(90, 200, 100)]
    [InlineData(180, 100, 200)]
    [InlineData(270, 200, 100)]
    public void VisualDimensions_SwapForQuarterTurns(int rotate, double vw, double vh)
    {
        var page = PageWith(rotate);
        page.VisualWidth.Should().Be(vw);
        page.VisualHeight.Should().Be(vh);
    }

    [Fact]
    public void ContentRect_StaysWithinMediaBox_ForAllRotations()
    {
        // A visual rect that fits within the visual bounds of every rotation
        // (the 90/270 visual page is 200×100, so keep both axes ≤ 100) must
        // map inside the MediaBox regardless of rotation — never off-page.
        var visual = new PdfRectangle(30, 40, 60, 90);
        foreach (var rotate in new[] { 0, 90, 180, 270 })
        {
            var c = PageWith(rotate).ToContentStreamCoordinates(visual).Normalize();
            c.Left.Should().BeGreaterThanOrEqualTo(0);
            c.Bottom.Should().BeGreaterThanOrEqualTo(0);
            c.Right.Should().BeLessThanOrEqualTo(100);
            c.Top.Should().BeLessThanOrEqualTo(200);
        }
    }

    [Fact]
    public void NonZeroMediaBoxOrigin_IsOffsetCorrectly()
    {
        // MediaBox [10 20 110 220]: same 100×200 extent shifted by (10,20).
        var page = PageWith(rotate: 0, mediaBox: "[10 20 110 220]");
        var c = page.ToContentStreamCoordinates(new PdfRectangle(0, 0, 10, 20)).Normalize();

        c.Left.Should().Be(10);
        c.Right.Should().Be(20);
        c.Top.Should().Be(220);     // 20 (bottom) + 200 (height)
        c.Bottom.Should().Be(200);  // 220 - 20
    }

    private static PdfPage PageWith(int rotate, string mediaBox = "[0 0 100 200]")
    {
        var rotateEntry = rotate == 0 ? "" : $" /Rotate {rotate}";
        var pdf = BuildOnePager(
            $"<< /Type /Page /Parent 2 0 R /MediaBox {mediaBox}{rotateEntry} /Contents 4 0 R >>",
            "");
        return PdfDocument.Open(pdf).GetPage(1);
    }

    private static byte[] BuildOnePager(string pageDict, string content)
    {
        using var ms = new MemoryStream();
        void W(string s) { var b = Encoding.Latin1.GetBytes(s); ms.Write(b, 0, b.Length); }

        var bodies = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            pageDict,
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
        };

        W("%PDF-1.4\n");
        var off = new long[bodies.Length + 1];
        for (int i = 0; i < bodies.Length; i++)
        {
            off[i + 1] = ms.Position;
            W($"{i + 1} 0 obj\n{bodies[i]}\nendobj\n");
        }
        long xref = ms.Position;
        W($"xref\n0 {bodies.Length + 1}\n0000000000 65535 f \n");
        for (int i = 1; i <= bodies.Length; i++) W($"{off[i]:D10} 00000 n \n");
        W($"trailer\n<< /Root 1 0 R /Size {bodies.Length + 1} >>\nstartxref\n{xref}\n%%EOF");
        return ms.ToArray();
    }
}
