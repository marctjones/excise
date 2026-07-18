using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Core.Tests.Document;

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

    [Fact]
    public void ToViewerDips_ReturnsSameInstanceWhenScaleAlreadyMatches()
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(612, 792);
        var rect = PdfPageRect.ViewerDips(page.PageNumber, 10, 20, 30, 40, renderDpi: 144);

        var mapped = PdfCoordinateMapper.ToViewerDips(page, rect, renderDpi: 144);

        mapped.Should().Be(rect);
        mapped.Dpi.Should().BeApproximately(144, 0.0001);
    }

    [Fact]
    public void ContinuousDips_ConvertsFromContentPointsWithExplicitScale()
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(100, 200);
        var content = PdfPageRect.FromContentPoints(
            page.PageNumber,
            new PdfRectangle(10, 150, 30, 180));

        var continuous = PdfCoordinateMapper.ToContinuousDips(page, content, unitsPerPoint: 2.5);

        continuous.Space.Should().Be(PdfCoordinateSpace.ContinuousDips);
        continuous.UnitsPerPoint.Should().Be(2.5);
        continuous.X.Should().BeApproximately(25, 0.001);
        continuous.Y.Should().BeApproximately(50, 0.001);
        continuous.Width.Should().BeApproximately(50, 0.001);
        continuous.Height.Should().BeApproximately(75, 0.001);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void MapperRejectsInvalidScales(double scale)
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(100, 200);
        var rect = PdfPageRect.VisualPoints(page.PageNumber, 1, 2, 3, 4);

        var viewer = () => PdfCoordinateMapper.ToViewerDips(page, rect, scale);
        var continuous = () => PdfCoordinateMapper.ToContinuousDips(page, rect, scale);

        viewer.Should().Throw<ArgumentOutOfRangeException>();
        continuous.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void PdfPageRect_ConstructorRejectsInvalidValues()
    {
        var badPage = () => new PdfPageRect(0, 0, 0, 1, 1, PdfCoordinateSpace.ContentPoints);
        var badCoordinate = () => new PdfPageRect(1, double.NaN, 0, 1, 1, PdfCoordinateSpace.ContentPoints);
        var badWidth = () => new PdfPageRect(1, 0, 0, -1, 1, PdfCoordinateSpace.ContentPoints);
        var badScale = () => new PdfPageRect(1, 0, 0, 1, 1, PdfCoordinateSpace.ContentPoints, 0);

        badPage.Should().Throw<ArgumentOutOfRangeException>();
        badCoordinate.Should().Throw<ArgumentException>();
        badWidth.Should().Throw<ArgumentOutOfRangeException>();
        badScale.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void PdfPageRect_ToStringAndDpiExposeSpaceAndScale()
    {
        var rect = PdfPageRect.ViewerDips(3, 10, 20, 30, 40, renderDpi: 144);

        rect.Dpi.Should().BeApproximately(144, 0.0001);
        rect.ToString().Should().Contain("Page 3")
            .And.Contain("ViewerDips")
            .And.Contain("scale=2.0000");
    }

    [Fact]
    public void PdfField_FlagsAndButtonStatesReflectRawDictionary()
    {
        using var doc = PdfDocument.CreateNew();
        var widget = new PdfDictionary();
        widget["AP"] = new PdfDictionary
        {
            ["N"] = new PdfDictionary
            {
                ["Off"] = new PdfDictionary(),
                ["Yes"] = new PdfDictionary(),
                ["Maybe"] = new PdfDictionary()
            }
        };
        var raw = new PdfDictionary
        {
            ["V"] = new PdfName("Yes")
        };

        var field = new PdfField(
            doc,
            "Agree",
            "Agree",
            PdfFieldType.Button,
            options: null,
            rect: new PdfRectangle(1, 2, 3, 4),
            pageNumber: 1,
            isReadOnly: true,
            isRequired: true,
            isMultiline: false,
            rawDictionary: raw,
            widgetDictionaries: new[] { widget },
            flags: 0x8000 | 0x10000,
            widgets: Array.Empty<PdfFieldWidget>());

        field.Value.Should().Be("Yes");
        field.IsRadioButton.Should().BeTrue();
        field.IsPushButton.Should().BeTrue();
        field.ButtonExportValues.Should().Equal("Yes", "Maybe");
        field.ToString().Should().Contain("Button 'Agree'")
            .And.Contain("= \"Yes\"")
            .And.Contain("on page 1")
            .And.Contain("read-only");
    }

    [Fact]
    public void PdfField_ChoiceComboBoxValidatesOptionsAndClearsValue()
    {
        using var doc = PdfDocument.CreateNew();
        var raw = new PdfDictionary();
        var field = new PdfField(
            doc,
            "Status",
            "Status",
            PdfFieldType.Choice,
            options: new[] { "Open", "Closed" },
            rect: null,
            pageNumber: null,
            isReadOnly: false,
            isRequired: false,
            isMultiline: false,
            rawDictionary: raw,
            widgetDictionaries: Array.Empty<PdfDictionary>(),
            flags: 0x20000,
            widgets: Array.Empty<PdfFieldWidget>());

        field.IsComboBox.Should().BeTrue();
        field.SetValue("Closed");
        field.Value.Should().Be("Closed");

        var invalid = () => field.SetValue("Other");
        invalid.Should().Throw<ArgumentException>()
            .WithMessage("*not one of the choice field options*");

        field.SetValue(null);
        field.Value.Should().BeNull();
    }
}
