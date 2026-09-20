using System.IO.Compression;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests;

/// <summary>
/// F3 (#1207): a one-shot render forgets an image object once its bitmap is
/// cached, so the ENCODED bytes the object store pinned can be collected. After
/// F2 those bytes were everything left on the managed heap at the x4 peak.
///
/// <para>Proved by identity, not by memory: with releasing on, resolving the
/// image after a render yields a DIFFERENT instance (evicted, re-parsed from the
/// file), and a second render is byte-identical — the re-parse is exact. With
/// releasing off (the viewer), the SAME instance: its pages stay warm.</para>
/// </summary>
public class EncodedBytesEvictionTests
{
    [Fact]
    public void AReleasingRender_ForgetsTheImageObject_AndARerenderIsIdentical()
    {
        using var doc = PdfDocument.Open(OneIndirectFlateImageDocument());
        var page = doc.GetPage(1);
        var before = ImageXObject(doc, page);
        before.ObjectNumber.Should().NotBeNull("the fixture's image must be indirect, or there is nothing to evict");

        using var first = new SkiaRenderer().RenderPage(page, new RenderOptions { Dpi = 72 });
        var after = ImageXObject(doc, page);

        after.Should().NotBeSameAs(before,
            "after a releasing render the object store must have forgotten the image; a second resolve re-parses it");
        after.IsDecoded.Should().BeFalse("the re-parsed object starts deferred, like the first one did");

        using var second = new SkiaRenderer().RenderPage(page, new RenderOptions { Dpi = 72 });
        second.Bytes.Should().Equal(first.Bytes, "re-parsing from the file is exact");
    }

    [Fact]
    public void ANonReleasingRender_KeepsTheImageObject()
    {
        using var doc = PdfDocument.Open(OneIndirectFlateImageDocument());
        var page = doc.GetPage(1);
        var before = ImageXObject(doc, page);

        using var _ = new SkiaRenderer().RenderPage(page, new RenderOptions { Dpi = 72, ReleaseDecodedImageSamples = false });

        ImageXObject(doc, page).Should().BeSameAs(before,
            "the viewer keeps its pages warm; forgetting objects under it would re-read every image per band");
    }

    private static PdfStream ImageXObject(PdfDocument doc, PdfPage page)
    {
        var resources = doc.Resolve(page.Dictionary.GetOptional("Resources")!).Should().BeOfType<PdfDictionary>().Subject;
        var xobjects = doc.Resolve(resources.GetOptional("XObject")!).Should().BeOfType<PdfDictionary>().Subject;
        return doc.Resolve(xobjects.GetOptional("Im0")!).Should().BeOfType<PdfStream>().Subject;
    }

    private static byte[] OneIndirectFlateImageDocument()
    {
        var samples = new byte[8 * 8 * 3];
        for (var i = 0; i < samples.Length; i++) samples[i] = (byte)(i * 11);
        using var z = new MemoryStream();
        using (var d = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) d.Write(samples);
        var flate = z.ToArray();

        var dict = new PdfDictionary();
        dict.SetName("Type", "XObject");
        dict.SetName("Subtype", "Image");
        dict.SetInt("Width", 8);
        dict.SetInt("Height", 8);
        dict.SetInt("BitsPerComponent", 8);
        dict.SetName("ColorSpace", "DeviceRGB");
        dict.SetName("Filter", "FlateDecode");

        using var doc = PdfDocument.CreateNew();
        var imageRef = doc.AddIndirectObject(new PdfStream(dict, flate));
        var page = doc.Pages.AddBlank(100, 100);
        var xobjects = new PdfDictionary();
        xobjects["Im0"] = imageRef;
        var resources = new PdfDictionary();
        resources["XObject"] = xobjects;
        page.Dictionary["Resources"] = resources;
        page.SetContentStreamBytes(Encoding.ASCII.GetBytes("q 40 0 0 40 10 10 cm /Im0 Do Q"));
        return doc.SaveToBytes();
    }
}
