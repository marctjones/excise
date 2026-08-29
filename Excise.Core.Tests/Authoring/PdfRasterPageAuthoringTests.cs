using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Core.Tests.Authoring;

public class PdfRasterPageAuthoringTests
{
    [Fact]
    public void AddRgbRasterPage_WritesOnlyAnImageXObjectAndNoTextCarrier()
    {
        var pixels = new byte[]
        {
            255, 0, 0, 0, 255, 0,
            0, 0, 255, 255, 255, 255,
        };

        using var document = PdfDocument.CreateNew();
        document.AddRgbRasterPage(pixels, pixelWidth: 2, pixelHeight: 2, widthPoints: 144, heightPoints: 72);
        var bytes = document.SaveToBytes();

        using var reopened = PdfDocument.Open(bytes);
        var page = reopened.GetPage(1);
        page.Text.Should().BeEmpty();
        Encoding.ASCII.GetString(page.GetContentStreamBytes()).Should().Be("q\n144 0 0 72 0 0 cm\n/Im0 Do\nQ\n");
        var image = page.GetXObject("Im0").Should().BeOfType<PdfStream>().Subject;
        image.GetName("Subtype").Should().Be("Image");
        image.GetInt("Width").Should().Be(2);
        image.GetInt("Height").Should().Be(2);
        image.GetName("ColorSpace").Should().Be("DeviceRGB");
        image.DecodedData.Should().Equal(pixels);
        page.Dictionary.ContainsKey("Annots").Should().BeFalse();
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(1, 0, 1)]
    [InlineData(1, 1, 0)]
    public void AddRgbRasterPage_RejectsInvalidDimensions(int width, int height, int points)
    {
        using var document = PdfDocument.CreateNew();
        var act = () => document.AddRgbRasterPage(new byte[3], width, height, points, points);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
