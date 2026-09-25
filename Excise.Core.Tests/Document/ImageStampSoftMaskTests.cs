using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Core.Tests.Document;

/// <summary>
/// An image stamp can carry an alpha channel as a real <c>/SMask</c> (ISO 32000-2 §11.6.5.3), so a PNG
/// with a transparent background sits on the page instead of in a box.
/// </summary>
public class ImageStampSoftMaskTests
{
    private static readonly PdfRectangle Box = new(50, 50, 150, 150);

    private static (PdfStream Image, PdfDocument Doc) ImageOf(byte[] rgb, int w, int h, byte[]? alpha)
    {
        var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank(200, 200);
        var annotation = doc.AddImageStampAnnotation(1, Box, rgb, w, h, alphaPixels: alpha);
        var ap = (PdfDictionary)doc.Resolve(annotation.RawDictionary.GetOptional("AP")!);
        var form = (PdfStream)doc.Resolve(ap.GetOptional("N")!);
        var resources = (PdfDictionary)doc.Resolve(form["Resources"]);
        var xobjects = (PdfDictionary)doc.Resolve(resources["XObject"]);
        return ((PdfStream)doc.Resolve(xobjects["Im0"]), doc);
    }

    [Fact]
    public void WithAlpha_TheImageCarriesAnSMaskOfTheSameSize()
    {
        var rgb = new byte[2 * 2 * 3];
        var alpha = new byte[] { 255, 0, 128, 255 };
        var (image, doc) = ImageOf(rgb, 2, 2, alpha);
        using (doc)
        {
            var mask = (PdfStream)doc.Resolve(image["SMask"]);
            mask.GetInt("Width").Should().Be(2);
            mask.GetInt("Height").Should().Be(2);
            mask.GetNameOrNull("ColorSpace").Should().Be("DeviceGray");
            mask.GetInt("BitsPerComponent").Should().Be(8);
            mask.DecodedData.Should().Equal(alpha);
        }
    }

    [Fact]
    public void WithAnAllOpaqueAlpha_NoSMaskIsWritten()
    {
        var (image, doc) = ImageOf(new byte[2 * 2 * 3], 2, 2, new byte[] { 255, 255, 255, 255 });
        using (doc)
            image.GetOptional("SMask").Should().BeNull("a fully opaque image needs no second image");
    }

    [Fact]
    public void WithoutAlpha_NoSMaskIsWritten_AsBefore()
    {
        var (image, doc) = ImageOf(new byte[2 * 2 * 3], 2, 2, null);
        using (doc)
            image.GetOptional("SMask").Should().BeNull();
    }

    [Fact]
    public void AnAlphaBufferOfTheWrongSize_IsRefused()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank(200, 200);
        var act = () => doc.AddImageStampAnnotation(1, Box, new byte[12], 2, 2, alphaPixels: new byte[3]);
        act.Should().Throw<ArgumentException>().WithMessage("*alphaPixels*");
    }

    [Fact]
    public void TheSMask_SurvivesSaveAndReload()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank(200, 200);
        doc.AddImageStampAnnotation(1, Box, new byte[12], 2, 2, alphaPixels: new byte[] { 0, 255, 255, 0 });
        var saved = doc.SaveToBytes();

        using var reopened = PdfDocument.Open(saved);
        var stamp = reopened.GetPage(1).GetAnnotations().Single(a => a.Subtype == PdfAnnotationSubtype.Stamp);
        var ap = (PdfDictionary)reopened.Resolve(stamp.RawDictionary.GetOptional("AP")!);
        var form = (PdfStream)reopened.Resolve(ap.GetOptional("N")!);
        var xobjects = (PdfDictionary)reopened.Resolve(((PdfDictionary)reopened.Resolve(form["Resources"]))["XObject"]);
        var image = (PdfStream)reopened.Resolve(xobjects["Im0"]);
        ((PdfStream)reopened.Resolve(image["SMask"])).DecodedData.Should().Equal(new byte[] { 0, 255, 255, 0 });
    }
}
