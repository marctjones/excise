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

    /// <summary>
    /// #1492: <c>RenderOptions.ImageSampleStreamSink</c> reports which image and
    /// mask streams a render read. The viewer keeps a realized page's samples
    /// pinned by it, so it must name a stream the page reads even when another
    /// render already decoded it — a filter on "not decoded yet" would let a
    /// realized page's shared image be released under it.
    /// </summary>
    [Fact]
    public void TheImageSampleSink_RecordsEveryImageAndMaskStreamTheRenderRead_DecodedOrNot()
    {
        using var doc = PdfDocument.Open(MaskedImagesDocument());
        var streams = Streams(doc);
        streams.Should().OnlyContain(s => !s.IsDecoded, "precondition: the first render drives every decode");

        var first = new List<PdfStream>();
        using var firstBitmap = Render(doc, release: false, sink: first);
        first.Should().HaveCount(streams.Length, "the image, its /SMask, the second image and its /Mask, once each");
        foreach (var stream in streams)
            first.Should().Contain(s => ReferenceEquals(s, stream));

        streams.Should().OnlyContain(s => s.IsDecoded, "precondition: nothing released them");
        var second = new List<PdfStream>();
        using var secondBitmap = Render(doc, release: false, sink: second);
        second.Should().HaveCount(streams.Length,
            "a stream an earlier render decoded is still a stream this page reads");
        foreach (var stream in streams)
            second.Should().Contain(s => ReferenceEquals(s, stream));

        using var plain = Render(doc, release: false);
        secondBitmap.Bytes.Should().Equal(plain.Bytes, "recording what a render reads changes no pixel");
    }

    [Fact]
    public void TheImageSampleSink_RecordsImagesDrawnInsideAFormAndAnAnnotationAppearance_EvenWhenTheRenderReleasesThem()
    {
        using var doc = PdfDocument.Open(FormAndAnnotationImagesDocument());
        var page = doc.GetPage(1);
        var form = XObjectOf(doc, page.Dictionary, "Fm0");
        var formImage = XObjectOf(doc, form, "ImF");
        var annots = doc.Resolve(page.Dictionary.GetOptional("Annots")!).Should().BeOfType<PdfArray>().Subject;
        var annot = doc.Resolve(annots[0]).Should().BeOfType<PdfDictionary>().Subject;
        var ap = doc.Resolve(annot.GetOptional("AP")!).Should().BeOfType<PdfDictionary>().Subject;
        var appearance = doc.Resolve(ap.GetOptional("N")!).Should().BeOfType<PdfStream>().Subject;
        var appearanceImage = XObjectOf(doc, appearance, "ImA");

        var sink = new List<PdfStream>();
        using var bitmap = Render(doc, release: true, sink: sink);

        sink.Should().HaveCount(2, "the only two images on the page, one per carrier");
        sink.Should().Contain(s => ReferenceEquals(s, formImage), "an image drawn by a form XObject");
        sink.Should().Contain(s => ReferenceEquals(s, appearanceImage), "an image drawn by an annotation's appearance stream");
        formImage.IsDecoded.Should().BeFalse("the release flag still releases what the render decoded");
        appearanceImage.IsDecoded.Should().BeFalse();
        bitmap.Bytes.Should().Contain(b => b != 0xFF, "the images actually drew");
    }

    [Fact]
    public void TheImageSampleSink_StaysEmpty_ForAPageWithNoImages()
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(100, 100);
        page.SetContentStreamBytes(Encoding.ASCII.GetBytes("0 0 1 rg 10 10 50 50 re f"));
        using var reopened = PdfDocument.Open(doc.SaveToBytes());

        var sink = new List<PdfStream>();
        using var _ = Render(reopened, release: false, sink: sink);

        sink.Should().BeEmpty();
    }

    /// <summary>
    /// #1468: releasing is the DEFAULT, and a render that says nothing gets it.
    ///
    /// <para>This exists because the old default was the defect. The option used
    /// to default to false, so a caller had to remember to opt in; three did (the
    /// CLI, the thumbnail cache, the Windows printer) and two did not — the GUI's
    /// image export and the bare <c>RenderPage</c> API both silently held every
    /// decoded image on a page until the document closed.</para>
    ///
    /// <para>⚠️ Nothing else pins this. The theory above passes <c>release</c>
    /// explicitly for both values, so it is green whichever way the default
    /// points, and every other test constructs its own options. Without this
    /// case, flipping the default back is a silent change.</para>
    /// </summary>
    [Fact]
    public void ReleasingDecodedSamples_IsTheDefault()
    {
        new RenderOptions().ReleaseDecodedImageSamples.Should().BeTrue(
            "a caller that says nothing must not hold every decoded image on the "
            + "page until the document closes; the interactive viewer is the one "
            + "caller that opts OUT, and it does so explicitly");
    }

    // ---- helpers -------------------------------------------------------

    private static SKBitmap Render(PdfDocument doc, bool release, ICollection<PdfStream>? sink = null)
        => new SkiaRenderer().RenderPage(doc.GetPage(1), new RenderOptions
        {
            Dpi = 72,
            AntiAlias = false,
            BackgroundColor = SKColors.White,
            ReleaseDecodedImageSamples = release,
            ImageSampleStreamSink = sink,
        });

    private static PdfStream XObjectOf(PdfDocument doc, PdfDictionary owner, string name)
    {
        var resources = doc.Resolve(owner.GetOptional("Resources")!).Should().BeOfType<PdfDictionary>().Subject;
        var xobjects = doc.Resolve(resources.GetOptional("XObject")!).Should().BeOfType<PdfDictionary>().Subject;
        return doc.Resolve(xobjects.GetOptional(name)!).Should().BeOfType<PdfStream>().Subject;
    }

    /// <summary>
    /// A page that draws one image through a form XObject, and a Square
    /// annotation whose normal appearance draws another.
    /// </summary>
    private static byte[] FormAndAnnotationImagesDocument()
    {
        using var doc = PdfDocument.CreateNew();
        var rgb = Enumerable.Range(0, Size * Size * 3).Select(i => (byte)((i * 53) % 180)).ToArray();
        var formImageRef = doc.AddIndirectObject(new PdfStream(
            ImageDictionary(bitsPerComponent: 8, colorSpace: "DeviceRGB"), Flate(rgb)));
        var appearanceImageRef = doc.AddIndirectObject(new PdfStream(
            ImageDictionary(bitsPerComponent: 8, colorSpace: "DeviceRGB"), Flate(rgb.Reverse().ToArray())));
        var formRef = doc.AddIndirectObject(FormDrawing("ImF", formImageRef));
        var appearanceRef = doc.AddIndirectObject(FormDrawing("ImA", appearanceImageRef));

        var page = doc.Pages.AddBlank(100, 100);
        var xobjects = new PdfDictionary();
        xobjects["Fm0"] = formRef;
        var resources = new PdfDictionary();
        resources["XObject"] = xobjects;
        page.Dictionary["Resources"] = resources;
        page.SetContentStreamBytes(Encoding.ASCII.GetBytes("q 1 0 0 1 5 5 cm /Fm0 Do Q"));

        var ap = new PdfDictionary();
        ap["N"] = appearanceRef;
        var annot = new PdfDictionary();
        annot.SetName("Type", "Annot");
        annot.SetName("Subtype", "Square");
        annot["Rect"] = new PdfArray(new PdfInteger(55), new PdfInteger(55), new PdfInteger(95), new PdfInteger(95));
        annot["AP"] = ap;
        page.Dictionary["Annots"] = new PdfArray(doc.AddIndirectObject(annot));
        return doc.SaveToBytes();
    }

    private static PdfStream FormDrawing(string imageName, PdfReference imageRef)
    {
        var xobjects = new PdfDictionary();
        xobjects[imageName] = imageRef;
        var resources = new PdfDictionary();
        resources["XObject"] = xobjects;
        var dict = new PdfDictionary();
        dict.SetName("Type", "XObject");
        dict.SetName("Subtype", "Form");
        dict["BBox"] = new PdfArray(new PdfInteger(0), new PdfInteger(0), new PdfInteger(40), new PdfInteger(40));
        dict["Resources"] = resources;
        var content = Encoding.ASCII.GetBytes($"q 40 0 0 40 0 0 cm /{imageName} Do Q");
        dict.SetInt("Length", content.Length);
        return new PdfStream(dict, content);
    }

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
