using System.IO.Compression;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests;

/// <summary>
/// #1468 increment 2: <see cref="RenderOptions.ReleaseDecodedImageSamples"/>.
///
/// <para>A deferred image decode (increment 1) still pins the inflated samples
/// on the stream object once anything reads them, for as long as the document
/// is open. The option lets a render that will not need them again — thumbnail
/// pre-warm, a one-page CLI export — give back what IT decoded. It is opt-in
/// because the interactive viewer renders the same page as several bands, and
/// releasing after each band would inflate a large image once per band.</para>
///
/// <para>What these pin: the flag releases the image, its <c>/SMask</c> and its
/// explicit <c>/Mask</c>; no flag releases nothing; a stream decoded before
/// the render, or rewritten by someone other than the decoder, is left alone;
/// and not one output pixel changes. Synthetic fixtures, so they never skip.</para>
/// </summary>
public class DecodedImageSampleReleaseTests
{
    private const int Size = 8;

    [Fact]
    public void AFlaggedRender_ReleasesTheImageAndMaskSamplesItDecoded_AndDrawsTheSamePixels()
    {
        var bytes = MaskedImagesDocument();

        using var kept = PdfDocument.Open(bytes);
        var keptStreams = Streams(kept);
        keptStreams.Should().OnlyContain(s => !s.IsDecoded, "precondition: the render must drive every decode");
        using var keptBitmap = Render(kept, release: false);

        keptStreams.Should().OnlyContain(s => s.IsDecoded,
            "without the flag a render keeps what it decoded, exactly as before #1468 increment 2");

        using var released = PdfDocument.Open(bytes);
        var releasedStreams = Streams(released);
        using var releasedBitmap = Render(released, release: true);

        releasedStreams.Should().OnlyContain(s => !s.IsDecoded,
            "the flag gives back the samples of every image, /SMask and /Mask stream this render decoded");
        releasedBitmap.Bytes.Should().Equal(keptBitmap.Bytes,
            "releasing decoded samples must not change a single rendered pixel");
        releasedBitmap.Bytes.Should().Contain(b => b != 0xFF, "the images actually drew");

        // And the released streams still read back exactly what the decoder produced.
        for (var i = 0; i < releasedStreams.Length; i++)
            releasedStreams[i].DecodedData.Should().Equal(keptStreams[i].DecodedData);
    }

    [Fact]
    public void AFlaggedRenderedTwice_DrawsIdentically_BothTimes()
    {
        using var doc = PdfDocument.Open(MaskedImagesDocument());

        using var first = Render(doc, release: true);
        using var second = Render(doc, release: true);

        second.Bytes.Should().Equal(first.Bytes,
            "a render after a release decodes again from the unchanged encoded bytes");
        Streams(doc).Should().OnlyContain(s => !s.IsDecoded);
    }

    [Fact]
    public void AFlaggedRender_LeavesSamplesItDidNotDecode()
    {
        using var doc = PdfDocument.Open(MaskedImagesDocument());
        var streams = Streams(doc);
        var before = streams.Select(s => s.DecodedData).ToArray();

        using var _ = Render(doc, release: true);

        for (var i = 0; i < streams.Length; i++)
        {
            streams[i].IsDecoded.Should().BeTrue(
                "these samples were decoded before the render started — by a viewer band, say — and a " +
                "thumbnail render must not make that renderer decode them again");
            streams[i].DecodedData.Should().BeSameAs(before[i]);
        }
    }

    [Fact]
    public void AFlaggedRender_NeverReleasesSamplesWrittenByAnythingButTheDecoder()
    {
        using var doc = PdfDocument.Open(MaskedImagesDocument());
        var image = Streams(doc)[0];

        // What a redaction does to an image it keeps: new samples through
        // SetDecodedData. Here, simply the original samples inverted.
        var rewritten = image.DecodedData.Select(b => (byte)~b).ToArray();
        image.SetDecodedData(rewritten);

        using var _ = Render(doc, release: true);

        image.IsDecoded.Should().BeTrue();
        image.DecodedData.Should().BeSameAs(rewritten,
            "releasing a rewritten stream would re-decode the ORIGINAL encoded bytes and silently undo the rewrite");
    }

    // ---- helpers -------------------------------------------------------

    private static SKBitmap Render(PdfDocument doc, bool release)
        => new SkiaRenderer().RenderPage(doc.GetPage(1), new RenderOptions
        {
            Dpi = 72,
            AntiAlias = false,
            BackgroundColor = SKColors.White,
            ReleaseDecodedImageSamples = release,
        });

    /// <summary>Im0, its /SMask, Im1, its explicit /Mask — in that order.</summary>
    private static PdfStream[] Streams(PdfDocument doc)
    {
        var page = doc.GetPage(1);
        var im0 = page.GetXObject("Im0").Should().BeOfType<PdfStream>().Subject;
        var im1 = page.GetXObject("Im1").Should().BeOfType<PdfStream>().Subject;
        return new[]
        {
            im0,
            doc.Resolve(im0.GetOptional("SMask")!).Should().BeOfType<PdfStream>().Subject,
            im1,
            doc.Resolve(im1.GetOptional("Mask")!).Should().BeOfType<PdfStream>().Subject,
        };
    }

    private static byte[] MaskedImagesDocument()
    {
        using var doc = PdfDocument.CreateNew();

        var rgb = Enumerable.Range(0, Size * Size * 3).Select(i => (byte)((i * 37) % 200)).ToArray();
        var alpha = Enumerable.Range(0, Size * Size).Select(i => (byte)(i % 3 == 0 ? 0 : 255)).ToArray();
        // 1 bpc, one byte per row of 8: a checkerboard stencil.
        var stencil = Enumerable.Range(0, Size).Select(row => (byte)(row % 2 == 0 ? 0xAA : 0x55)).ToArray();

        var smaskRef = doc.AddIndirectObject(new PdfStream(
            ImageDictionary(bitsPerComponent: 8, colorSpace: "DeviceGray"), Flate(alpha)));
        var maskRef = doc.AddIndirectObject(new PdfStream(
            ImageDictionary(bitsPerComponent: 1, colorSpace: "DeviceGray"), Flate(stencil)));

        var im0Dict = ImageDictionary(bitsPerComponent: 8, colorSpace: "DeviceRGB");
        im0Dict["SMask"] = smaskRef;
        var im0Ref = doc.AddIndirectObject(new PdfStream(im0Dict, Flate(rgb)));

        var im1Dict = ImageDictionary(bitsPerComponent: 8, colorSpace: "DeviceRGB");
        im1Dict["Mask"] = maskRef;
        var im1Ref = doc.AddIndirectObject(new PdfStream(im1Dict, Flate(rgb.Reverse().ToArray())));

        var page = doc.Pages.AddBlank(100, 100);
        var xobjects = new PdfDictionary();
        xobjects["Im0"] = im0Ref;
        xobjects["Im1"] = im1Ref;
        var resources = new PdfDictionary();
        resources["XObject"] = xobjects;
        page.Dictionary["Resources"] = resources;
        page.SetContentStreamBytes(Encoding.ASCII.GetBytes(
            "q 40 0 0 40 5 5 cm /Im0 Do Q q 40 0 0 40 55 55 cm /Im1 Do Q"));
        return doc.SaveToBytes();
    }

    private static PdfDictionary ImageDictionary(int bitsPerComponent, string colorSpace)
    {
        var dict = new PdfDictionary();
        dict.SetName("Type", "XObject");
        dict.SetName("Subtype", "Image");
        dict.SetInt("Width", Size);
        dict.SetInt("Height", Size);
        dict.SetInt("BitsPerComponent", bitsPerComponent);
        dict.SetName("ColorSpace", colorSpace);
        dict.SetName("Filter", "FlateDecode");
        return dict;
    }

    private static byte[] Flate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(data);
        return output.ToArray();
    }
}
