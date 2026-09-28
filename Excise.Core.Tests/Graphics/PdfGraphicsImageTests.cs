using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Core.Tests.Graphics;

/// <summary>
/// #1908: DrawImage places a <see cref="PdfImage"/> (RGB pixels or a JPEG) as an image
/// XObject in the page resources, one entry per distinct image, painted by
/// <c>q w 0 0 h x y cm /ImN Do Q</c>.
/// </summary>
public class PdfGraphicsImageTests
{
    /// <summary>The start of a JPEG: SOI, an APP0 segment, then a frame header. Enough for DrawImage, which reads only the header.</summary>
    private static byte[] JpegHeader(byte sof, int width, int height, int components, int precision = 8)
    {
        var bytes = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x4A, 0x46 };
        bytes.AddRange([0xFF, sof, 0x00, (byte)(8 + 3 * components), (byte)precision,
            (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, (byte)components]);
        for (int c = 0; c < components; c++)
            bytes.AddRange([(byte)(c + 1), 0x11, 0x00]);
        bytes.AddRange([0xFF, 0xD9]);
        return bytes.ToArray();
    }

    private static PdfImage Rgba() => PdfImage.FromRgb(2, 2, [255, 0, 0, 0, 255, 0, 0, 0, 255, 9, 9, 9], [255, 128, 0, 255]);

    private static PdfImage Opaque() => PdfImage.FromRgb(1, 1, [1, 2, 3]);

    private static PdfStream Image(PdfPage page, string name) => page.GetXObject(name).Should().BeOfType<PdfStream>().Subject;

    [Fact]
    public void DrawImage_PaintsTheUnitSquareMappedOntoTheBox()
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(200, 200);
        using var g = page.GetGraphics();

        g.DrawImage(Rgba(), 10, 20, 100, 50);

        g.GetOperators().Should().Be("q\n100 0 0 50 10 20 cm\n/Im1 Do\nQ\n");
    }

    [Fact]
    public void DrawImage_RgbWithAlpha_IsAnRgbImageWithItsAlphaAsSoftMask()
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(200, 200);
        using (var g = page.GetGraphics())
            g.DrawImage(Rgba(), 0, 0, 10, 10);

        var image = Image(page, "Im1");
        image.GetNameOrNull("Subtype").Should().Be("Image");
        (image.GetInt("Width"), image.GetInt("Height"), image.GetInt("BitsPerComponent")).Should().Be((2, 2, 8));
        image.GetNameOrNull("ColorSpace").Should().Be("DeviceRGB");
        image.DecodedData.Should().Equal(255, 0, 0, 0, 255, 0, 0, 0, 255, 9, 9, 9);

        var mask = doc.Resolve(image.GetOptional("SMask")!).Should().BeOfType<PdfStream>().Subject;
        mask.GetNameOrNull("ColorSpace").Should().Be("DeviceGray");
        mask.DecodedData.Should().Equal(255, 128, 0, 255);
    }

    [Fact]
    public void DrawImage_OpaqueRgb_HasNoSoftMask()
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(200, 200);
        using (var g = page.GetGraphics())
            g.DrawImage(Opaque(), 0, 0, 10, 10);

        Image(page, "Im1").ContainsKey("SMask").Should().BeFalse();
        Image(page, "Im1").DecodedData.Should().Equal(1, 2, 3);
    }

    [Theory]
    [InlineData(0xC0, 3, "DeviceRGB")]
    [InlineData(0xC2, 1, "DeviceGray")]
    public void DrawImage_Jpeg_IsEmbeddedAsIsWithDctDecode(byte sof, int components, string colorSpace)
    {
        var jpeg = JpegHeader(sof, 640, 480, components);
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(200, 200);
        using (var g = page.GetGraphics())
            g.DrawImage(PdfImage.FromJpeg(jpeg), 0, 0, 10, 10);

        var image = Image(page, "Im1");
        image.GetNameOrNull("Filter").Should().Be("DCTDecode");
        (image.GetInt("Width"), image.GetInt("Height")).Should().Be((640, 480));
        image.GetNameOrNull("ColorSpace").Should().Be(colorSpace);
        image.EncodedData.Should().Equal(jpeg);
    }

    [Fact]
    public void DrawImage_EqualImagesTwice_AddOneXObject_EvenAcrossGraphicsContexts()
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(200, 200);
        using (var g = page.GetGraphics())
        {
            g.DrawImage(Rgba(), 0, 0, 10, 10);
            g.DrawImage(Rgba(), 50, 50, 10, 10);
            g.DrawImage(Opaque(), 0, 0, 10, 10);
            g.GetOperators().Should().Contain("/Im1 Do\nQ\nq\n10 0 0 10 50 50 cm\n/Im1 Do").And.Contain("/Im2 Do");
        }
        using (var g = page.GetGraphics())
        {
            g.DrawImage(Rgba(), 0, 0, 10, 10);
            g.GetOperators().Should().Contain("/Im1 Do");
        }

        page.GetXObject("Im3").Should().BeNull();
    }

    [Fact]
    public void DrawImage_SkipsXObjectNamesAlreadyOnThePage()
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(200, 200);
        var xobjects = new PdfDictionary();
        xobjects["Im1"] = new PdfStream();
        var resources = new PdfDictionary();
        resources["XObject"] = xobjects;
        page.Dictionary["Resources"] = resources;

        using var g = page.GetGraphics();
        g.DrawImage(Rgba(), 0, 0, 10, 10);

        g.GetOperators().Should().Contain("/Im2 Do");
        xobjects.Keys.Select(k => k.Value).Should().Equal("Im1", "Im2");
    }

    [Fact]
    public void DrawImage_AfterATranslucentFill_PaintsAtFullOpacity()
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(200, 200);
        using var g = page.GetGraphics();
        g.DrawRectangle(0, 0, 5, 5, new PdfBrush(PdfColor.Red) { Opacity = 0.5 });
        g.DrawImage(Rgba(), 0, 0, 10, 10);

        g.GetOperators().Should().EndWith("f\n/GS2 gs\nq\n10 0 0 10 0 0 cm\n/Im1 Do\nQ\n");
        page.GetExtGState("GS2")!.GetOptional("ca")!.GetInt().Should().Be(1);
    }

    [Fact]
    public void DrawImage_SurvivesSaveAndReopen()
    {
        byte[] saved;
        using (var doc = PdfDocument.CreateNew())
        {
            var page = doc.Pages.AddBlank(200, 200);
            using (var g = page.GetGraphics())
                g.DrawImage(Rgba(), 10, 20, 100, 50);
            saved = doc.SaveToBytes();
        }

        using var reopened = PdfDocument.Open(saved);
        var p = reopened.GetPage(1);
        System.Text.Encoding.Latin1.GetString(p.GetContentStreamBytes()).Should().Contain("q\n100 0 0 50 10 20 cm\n/Im1 Do\nQ\n");
        var image = Image(p, "Im1");
        image.DecodedData.Should().Equal(255, 0, 0, 0, 255, 0, 0, 0, 255, 9, 9, 9);
        reopened.Resolve(image.GetOptional("SMask")!).Should().BeOfType<PdfStream>()
            .Which.DecodedData.Should().Equal(255, 128, 0, 255);
    }

    public static TheoryData<string, byte[]> Unsupported => new()
    {
        { "not an image", [1, 2, 3, 4] },
        { "PNG", [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A] },
        { "CMYK JPEG", JpegHeader(0xC0, 10, 10, 4) },
        { "12-bit JPEG", JpegHeader(0xC1, 10, 10, 3, precision: 12) },
        { "lossless JPEG", JpegHeader(0xC3, 10, 10, 3) },
        { "JPEG with its height in a DNL segment", JpegHeader(0xC0, 10, 0, 3) },
    };

    [Theory]
    [MemberData(nameof(Unsupported))]
    public void FromJpeg_UnsupportedImage_Throws(string what, byte[] bytes) =>
        FluentActions.Invoking(() => PdfImage.FromJpeg(bytes)).Should().Throw<ArgumentException>(what);

    [Theory]
    [InlineData(0, 1, 0, null)]
    [InlineData(2, 2, 11, null)]
    [InlineData(2, 2, 13, null)]
    [InlineData(2, 2, 12, 3)]
    [InlineData(2, 2, 12, 5)]
    public void FromRgb_SizeAndLengthsMustAgree(int width, int height, int rgbLength, int? alphaLength) =>
        FluentActions.Invoking(() => PdfImage.FromRgb(width, height, new byte[rgbLength], alphaLength is { } a ? new byte[a] : null))
            .Should().Throw<ArgumentException>();

    [Fact]
    public void FromRgb_CopiesItsPixels_SoLaterChangesToTheArrayDoNotReachTheImage()
    {
        byte[] rgb = [1, 2, 3];
        var image = PdfImage.FromRgb(1, 1, rgb);
        rgb[0] = 99;
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(200, 200);
        using (var g = page.GetGraphics())
            g.DrawImage(image, 0, 0, 10, 10);

        Image(page, "Im1").DecodedData.Should().Equal(1, 2, 3);
    }

    [Fact]
    public void Identity_DependsOnSizeAndAlpha_NotOnlyOnTheRgbBytes()
    {
        byte[] six = [1, 2, 3, 4, 5, 6];
        var identities = new[]
        {
            PdfImage.FromRgb(2, 1, six).Identity,
            PdfImage.FromRgb(1, 2, six).Identity,
            PdfImage.FromRgb(2, 1, six, [255, 0]).Identity,
            PdfImage.FromRgb(2, 1, six, [255, 1]).Identity,
        };
        identities.Should().OnlyHaveUniqueItems();
        PdfImage.FromRgb(2, 1, (byte[])six.Clone()).Identity.Should().Be(identities[0]);
    }

    [Fact]
    public void MixedDrawingWithImages_EveryPathObjectIsWellFormed()
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(200, 200);
        using var g = page.GetGraphics();
        g.SaveState();
        g.ClipRectangle(0, 0, 100, 100);
        g.DrawImage(Rgba(), 0, 0, 50, 50);
        g.DrawCircle(50, 50, 20, PdfBrush.Blue, PdfPen.Black);
        g.MoveTo(0, 0);
        g.DrawImage(Rgba(), 10, 10, 50, 50);
        g.LineTo(10, 10);
        g.Stroke(PdfPen.Black);
        g.RestoreState();

        PdfGraphicsOperatorOrderTests.AssertPathObjectsWellFormed(g.GetOperators());
    }
}
