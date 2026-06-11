using AwesomeAssertions;
using Pdfe.Core.Document;
using Xunit;

namespace Pdfe.Core.Tests.Document;

public class PdfCoordinateMapperTests
{
    [Fact]
    public void ViewerDips_ToContentPoints_UsesTaggedRenderScale()
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(612, 792);

        var viewerRect = PdfPageRect.ViewerDips(
            page.PageNumber,
            x: 100 * 120.0 / 72.0,
            y: 72 * 120.0 / 72.0,
            width: 60 * 120.0 / 72.0,
            height: 20 * 120.0 / 72.0,
            renderDpi: 120);

        var content = PdfCoordinateMapper.ToContentPoints(page, viewerRect).ToPdfRectangle().Normalize();

        content.Left.Should().BeApproximately(100, 0.001);
        content.Bottom.Should().BeApproximately(700, 0.001);
        content.Right.Should().BeApproximately(160, 0.001);
        content.Top.Should().BeApproximately(720, 0.001);
    }

    [Fact]
    public void ContentPoints_ToViewerDips_UsesRequestedViewerDpi()
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(612, 792);
        var content = PdfPageRect.FromContentPoints(
            page.PageNumber,
            new PdfRectangle(100, 700, 160, 720));

        var viewer120 = PdfCoordinateMapper.ToViewerDips(page, content, renderDpi: 120);
        var viewer150 = PdfCoordinateMapper.ToViewerDips(page, content, renderDpi: 150);

        viewer120.Space.Should().Be(PdfCoordinateSpace.ViewerDips);
        viewer120.X.Should().BeApproximately(166.6667, 0.001);
        viewer120.Y.Should().BeApproximately(120, 0.001);
        viewer120.Width.Should().BeApproximately(100, 0.001);
        viewer120.Height.Should().BeApproximately(33.3333, 0.001);

        viewer150.X.Should().BeApproximately(208.3333, 0.001);
        viewer150.Width.Should().BeApproximately(125, 0.001);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void ContentPoints_RoundTripThroughVisualPoints_ForEveryPageRotation(int rotation)
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(100, 200);
        page.Rotation = rotation;
        var content = PdfPageRect.FromContentPoints(
            page.PageNumber,
            new PdfRectangle(20, 40, 60, 90));

        var visual = PdfCoordinateMapper.ToVisualPoints(page, content);
        var roundTrip = PdfCoordinateMapper.ToContentPoints(page, visual).ToPdfRectangle().Normalize();

        roundTrip.Left.Should().BeApproximately(20, 0.001);
        roundTrip.Bottom.Should().BeApproximately(40, 0.001);
        roundTrip.Right.Should().BeApproximately(60, 0.001);
        roundTrip.Top.Should().BeApproximately(90, 0.001);
    }

    [Fact]
    public void ToContentPoints_RejectsRectFromDifferentPage()
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(612, 792);
        var rect = PdfPageRect.VisualPoints(2, 0, 0, 10, 10);

        var act = () => PdfCoordinateMapper.ToContentPoints(page, rect);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*page 2*mapper page is 1*");
    }
}
