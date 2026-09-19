using System.IO.Compression;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// Sibling of <see cref="RefusedImageDiagnosticTests"/> for #1468, which moved
/// image decoding from resolve time to first read.
///
/// <para>The #1396 diagnostic is emitted from a check of
/// <c>DecodeFailureReason</c> that runs BEFORE the renderer reads
/// <c>DecodedData</c>, and that read sits inside a catch-all that returns null.
/// With decoding deferred, the reason does not exist until something attempts
/// the decode — so without care a refused image would vanish silently, which is
/// the exact failure #1396 was filed to end. These fixtures are synthetic, so
/// unlike the corpus-backed siblings they never skip.</para>
/// </summary>
public class DeferredImageDecodeRenderingTests
{
    [Fact]
    public void ARefusedImageWhoseDecodeWasDeferred_StillReportsTheRefusal()
    {
        using var doc = PdfDocument.Open(Jbig2RefusalDocument());
        var image = doc.GetPage(1).GetXObject("Im0").Should().BeOfType<PdfStream>().Subject;
        image.IsDecoded.Should().BeFalse("precondition: the refusal must happen on the deferred path");
        image.DecodeFailureReason.Should().BeNull("precondition: nothing has attempted the decode yet");

        var diagnostics = new List<string>();
        using var _ = new SkiaRenderer().RenderPage(doc.GetPage(1),
            new RenderOptions { Dpi = 72, AntiAlias = false, BackgroundColor = SKColors.White, Diagnostics = diagnostics });

        var message = diagnostics.FirstOrDefault(d => d.Contains("(#1396)"));
        message.Should().NotBeNull(
            "a refused image must say why it is missing even when its decode was deferred to render time; " +
            $"diagnostics were: [{string.Join(" | ", diagnostics)}]");
        message.Should().Contain("recovered malformation");
        message.Should().Contain("JBIG2Decode", "the report has to name which filter refused");
        message.Should().Contain("8x8", "the geometry locates the image");
    }

    [Fact]
    public void AFlateImage_RendersIdentically_WhetherOrNotItsSamplesWereReadFirst()
    {
        var bytes = FlateImageDocument();

        using var cold = PdfDocument.Open(bytes);
        var coldImage = cold.GetPage(1).GetXObject("Im0").Should().BeOfType<PdfStream>().Subject;
        coldImage.IsDecoded.Should().BeFalse("precondition: the render must drive the deferred decode");
        using var coldBitmap = Render(cold);

        using var warm = PdfDocument.Open(bytes);
        var warmImage = warm.GetPage(1).GetXObject("Im0").Should().BeOfType<PdfStream>().Subject;
        warmImage.DecodedData.Length.Should().Be(4 * 4 * 3);
        using var warmBitmap = Render(warm);

        coldImage.IsDecoded.Should().BeTrue("rendering the raw-sample path reads the samples");
        coldBitmap.Bytes.Should().Equal(warmBitmap.Bytes,
            "deferring the decode must not change a single rendered pixel");

        // The image actually drew: not a blank white page on both sides.
        coldBitmap.Bytes.Should().Contain(b => b != 0xFF);
    }

    private static SKBitmap Render(PdfDocument doc)
        => new SkiaRenderer().RenderPage(doc.GetPage(1),
            new RenderOptions { Dpi = 72, AntiAlias = false, BackgroundColor = SKColors.White,
                // #1468: releasing is now the DEFAULT, so a render that says nothing
                // drops the samples once the bitmap exists and IsDecoded reads false
                // afterwards. This class is about the deferred DECODE path — that the
                // render drives it and the pixels match a warm decode — not about the
                // release policy, so it opts out to keep "the samples were read" observable.
                ReleaseDecodedImageSamples = false,
            });

    private static byte[] Jbig2RefusalDocument()
    {
        // An end-of-file segment and nothing else: the JBIG2 decoder attempts it
        // and refuses with a PdfFilterDecodeException.
        var segment = new byte[] { 0, 0, 0, 1, 63, 0, 1, 0, 0, 0, 0 };
        return SingleImageDocument(ImageDictionary("JBIG2Decode", 8, 8, 1, "DeviceGray"), segment);
    }

    private static byte[] FlateImageDocument()
    {
        var samples = Enumerable.Range(0, 4 * 4 * 3).Select(i => (byte)((i * 37) % 200)).ToArray();
        return SingleImageDocument(ImageDictionary("FlateDecode", 4, 4, 8, "DeviceRGB"), Flate(samples));
    }

    private static PdfDictionary ImageDictionary(string filter, int width, int height, int bpc, string colorSpace)
    {
        var dict = new PdfDictionary();
        dict.SetName("Type", "XObject");
        dict.SetName("Subtype", "Image");
        dict.SetInt("Width", width);
        dict.SetInt("Height", height);
        dict.SetInt("BitsPerComponent", bpc);
        dict.SetName("ColorSpace", colorSpace);
        dict.SetName("Filter", filter);
        return dict;
    }

    private static byte[] SingleImageDocument(PdfDictionary imageDict, byte[] data)
    {
        using var doc = PdfDocument.CreateNew();
        var imageRef = doc.AddIndirectObject(new PdfStream(imageDict, data));
        var page = doc.Pages.AddBlank(100, 100);
        var xobjects = new PdfDictionary();
        xobjects["Im0"] = imageRef;
        var resources = new PdfDictionary();
        resources["XObject"] = xobjects;
        page.Dictionary["Resources"] = resources;
        page.SetContentStreamBytes(Encoding.ASCII.GetBytes("q 80 0 0 80 10 10 cm /Im0 Do Q"));
        return doc.SaveToBytes();
    }

    private static byte[] Flate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(data);
        return output.ToArray();
    }
}
