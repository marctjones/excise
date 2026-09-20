using System.IO.Compression;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Rendering.Tests;

/// <summary>
/// F3 (#1207) forgets an image object after a releasing render so its encoded
/// bytes can be collected, and the next resolve re-parses it FROM THE FILE.
/// The object store holds every edit in that same cache and nowhere else, so
/// an eviction of an edited slot silently restores the original at save. A
/// GUI document is long-lived and edited between renders (thumbnails re-render
/// a page after a redaction with the default options), which makes this the
/// leak class CLAUDE.md's redaction section exists for: nothing throws, the
/// saved file just carries what the user removed.
///
/// <para>Each test edits, renders with the DEFAULT options (releasing, so F3
/// is live), saves, reopens, and asserts the edit is in the saved file.</para>
/// </summary>
public class EvictionEditSafetyTests
{
    [Fact]
    public void APageAddedFromAnotherDocument_KeepsItsImage_ThroughARenderAndASave()
    {
        using var source = PdfDocument.Open(OneIndirectFlateImageDocument(Samples(11)));
        using var doc = PdfDocument.Open(OneIndirectFlateImageDocument(Samples(3)));
        doc.Pages.Add(source.GetPage(1));
        doc.PageCount.Should().Be(2);

        using var _ = new SkiaRenderer().RenderPage(doc.GetPage(2), new RenderOptions { Dpi = 72 });

        using var saved = PdfDocument.Open(doc.SaveToBytes());
        ImageXObject(saved, saved.GetPage(2)).DecodedData.Should().Equal(Samples(11),
            "the page added from the other document must still carry its image after a releasing render");
    }

    [Fact]
    public void AReplacedImageObject_IsWhatGetsSaved_AfterARender()
    {
        using var doc = PdfDocument.Open(OneIndirectFlateImageDocument(Samples(3)));
        var page = doc.GetPage(1);
        var original = ImageXObject(doc, page);
        var number = original.ObjectNumber!.Value;
        doc.ReplaceIndirectObject(number, ImageStream(Samples(7)));

        using var _ = new SkiaRenderer().RenderPage(page, new RenderOptions { Dpi = 72 });

        using var saved = PdfDocument.Open(doc.SaveToBytes());
        ImageXObject(saved, saved.GetPage(1)).DecodedData.Should().Equal(Samples(7),
            "the replacement, not the file's original, is the document's image now");
    }

    [Fact]
    public void AnImageDictionaryEditedInPlace_KeepsTheEdit_ThroughARenderAndASave()
    {
        using var doc = PdfDocument.Open(OneIndirectFlateImageDocument(Samples(3)));
        var page = doc.GetPage(1);
        ImageXObject(doc, page).SetName("Intent", "Perceptual");

        using var _ = new SkiaRenderer().RenderPage(page, new RenderOptions { Dpi = 72 });

        using var saved = PdfDocument.Open(doc.SaveToBytes());
        ImageXObject(saved, saved.GetPage(1)).GetNameOrNull("Intent").Should().Be("Perceptual",
            "an edit to the cached object must survive a releasing render of the page");
    }

    [Fact]
    public void ARedactedImage_StaysRedacted_ThroughARenderAndASave()
    {
        using var doc = PdfDocument.Open(OneIndirectFlateImageDocument(Samples(3)));
        var page = doc.GetPage(1);
        // The image is drawn at 10..50 x 10..50 (see the content stream).
        page.RedactArea(new PdfRectangle(10, 10, 50, 50));

        using var _ = new SkiaRenderer().RenderPage(page, new RenderOptions { Dpi = 72 });

        using var saved = PdfDocument.Open(doc.SaveToBytes());
        var savedPage = saved.GetPage(1);
        var resources = saved.Resolve(savedPage.Dictionary.GetOptional("Resources")!).Should().BeOfType<PdfDictionary>().Subject;
        var xobjects = saved.Resolve(resources.GetOptional("XObject")!).Should().BeOfType<PdfDictionary>().Subject;
        foreach (var key in xobjects.Keys)
        {
            var image = saved.Resolve(xobjects[key]).Should().BeOfType<PdfStream>().Subject;
            image.DecodedData.Should().NotEqual(Samples(3),
                "the original samples must not come back through a re-parse of the evicted object ({0})", key.Value);
        }
    }

    private static byte[] Samples(int seed)
    {
        var samples = new byte[8 * 8 * 3];
        for (var i = 0; i < samples.Length; i++) samples[i] = (byte)(i * seed + 1);
        return samples;
    }

    private static PdfStream ImageStream(byte[] samples)
    {
        using var z = new MemoryStream();
        using (var d = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) d.Write(samples);
        var dict = new PdfDictionary();
        dict.SetName("Type", "XObject");
        dict.SetName("Subtype", "Image");
        dict.SetInt("Width", 8);
        dict.SetInt("Height", 8);
        dict.SetInt("BitsPerComponent", 8);
        dict.SetName("ColorSpace", "DeviceRGB");
        dict.SetName("Filter", "FlateDecode");
        return new PdfStream(dict, z.ToArray());
    }

    private static byte[] OneIndirectFlateImageDocument(byte[] samples)
    {
        using var doc = PdfDocument.CreateNew();
        var imageRef = doc.AddIndirectObject(ImageStream(samples));
        var page = doc.Pages.AddBlank(100, 100);
        var xobjects = new PdfDictionary();
        xobjects["Im0"] = imageRef;
        var resources = new PdfDictionary();
        resources["XObject"] = xobjects;
        page.Dictionary["Resources"] = resources;
        page.SetContentStreamBytes(Encoding.ASCII.GetBytes("q 40 0 0 40 10 10 cm /Im0 Do Q"));
        return doc.SaveToBytes();
    }

    private static PdfStream ImageXObject(PdfDocument doc, PdfPage page)
    {
        var resources = doc.Resolve(page.Dictionary.GetOptional("Resources")!).Should().BeOfType<PdfDictionary>().Subject;
        var xobjects = doc.Resolve(resources.GetOptional("XObject")!).Should().BeOfType<PdfDictionary>().Subject;
        return doc.Resolve(xobjects.GetOptional("Im0")!).Should().BeOfType<PdfStream>().Subject;
    }
}
