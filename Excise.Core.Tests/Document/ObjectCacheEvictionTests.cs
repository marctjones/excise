using System.IO.Compression;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Core.Tests.Document;

/// <summary>
/// F3 (#1207): the object store may forget a cached object so its encoded
/// bytes can be collected, because the next resolve re-parses it from the
/// file. That is safe ONLY when the re-parse gives back exactly what is
/// forgotten. The cache is the only place an edit lives before a save —
/// AddIndirectObject and ReplaceIndirectObject write there and nowhere else,
/// and an in-place edit mutates the cached instance — so an eviction of any
/// edited slot silently restores the file's original at the save. Measured
/// before the guard: an image dictionary edited in place, rendered once with
/// releasing options and saved, came back unedited. These pin the contract
/// case by case; the renderer-level version is
/// Excise.Rendering.Tests.EvictionEditSafetyTests.
/// </summary>
public class ObjectCacheEvictionTests
{
    [Fact]
    public void APristineParsedObject_IsEvicted_AndTheNextResolveReparsesIt()
    {
        using var doc = PdfDocument.Open(OneIndirectFlateImageDocument());
        var image = Image(doc);
        image.IsPristine.Should().BeTrue("the store marks what it parsed from the file");

        doc.TryEvictFromCache(image).Should().BeTrue();

        var again = Image(doc);
        again.Should().NotBeSameAs(image, "the slot was empty, so this resolve re-parsed the file");
        again.DecodedData.Should().Equal(image.DecodedData, "the re-parse is exact");
    }

    [Fact]
    public void TheStoresOwnDeferredDecodeAndRelease_LeaveTheObjectPristine()
    {
        using var doc = PdfDocument.Open(OneIndirectFlateImageDocument());
        var image = Image(doc);
        image.IsDecoded.Should().BeFalse("images decode on first read (#1468)");

        image.TryEnsureDecoded().Should().BeTrue();
        image.IsPristine.Should().BeTrue("the decoder wrote what the file says; that is not an edit");
        image.TryReleaseDecodedWithoutWaiting(out _).Should().Be(DecodedReleaseOutcome.Released);
        image.IsPristine.Should().BeTrue();

        doc.TryEvictFromCache(image).Should().BeTrue();
    }

    [Fact]
    public void AnObjectEditedInPlace_IsNotEvicted()
    {
        using var doc = PdfDocument.Open(OneIndirectFlateImageDocument());
        var image = Image(doc);
        image.SetName("Intent", "Perceptual");

        image.IsPristine.Should().BeFalse();
        doc.TryEvictFromCache(image).Should().BeFalse("the file does not have this edit; forgetting the object would drop it");
        Image(doc).Should().BeSameAs(image);

        using var saved = PdfDocument.Open(doc.SaveToBytes());
        Image(saved).GetNameOrNull("Intent").Should().Be("Perceptual");
    }

    [Fact]
    public void AnObjectWhoseBytesWereRewritten_IsNotEvicted()
    {
        using var doc = PdfDocument.Open(OneIndirectFlateImageDocument());
        var image = Image(doc);
        image.SetDecodedData(new byte[8 * 8 * 3]);

        doc.TryEvictFromCache(image).Should().BeFalse();
        Image(doc).Should().BeSameAs(image);
    }

    [Fact]
    public void AnAddedObject_IsNotEvicted()
    {
        using var doc = PdfDocument.Open(OneIndirectFlateImageDocument());
        var added = ImageStream(Samples(5));
        var reference = doc.AddIndirectObject(added);

        added.IsPristine.Should().BeFalse("a constructed object is never what the file says");
        doc.TryEvictFromCache(added).Should().BeFalse();
        doc.Resolve(reference).Should().BeSameAs(added);
    }

    [Fact]
    public void AReplacedSlot_IsNotEvicted_InEitherDirection()
    {
        using var doc = PdfDocument.Open(OneIndirectFlateImageDocument());
        var original = Image(doc);
        var number = original.ObjectNumber!.Value;
        var replacement = ImageStream(Samples(5));
        replacement.MarkPristine(); // even a caller lying about pristineness cannot evict a replaced slot
        doc.ReplaceIndirectObject(number, replacement);

        doc.TryEvictFromCache(original).Should().BeFalse("it is no longer the slot's object");
        doc.TryEvictFromCache(replacement).Should().BeFalse("the file holds the predecessor, not this");
        Image(doc).Should().BeSameAs(replacement);
    }

    [Fact]
    public void AStaleReference_CannotEvictTheReparsedObject()
    {
        using var doc = PdfDocument.Open(OneIndirectFlateImageDocument());
        var first = Image(doc);
        doc.TryEvictFromCache(first).Should().BeTrue();
        var second = Image(doc);
        second.SetName("Intent", "Perceptual");

        doc.TryEvictFromCache(first).Should().BeFalse("the slot holds a different (edited) instance now");
        Image(doc).Should().BeSameAs(second);
    }

    [Fact]
    public void AConstructedDictionary_IsNeverPristine_AndAPristineOneStopsBeingSoOnAnyMutation()
    {
        new PdfDictionary().IsPristine.Should().BeFalse();
        foreach (var mutate in new Action<PdfDictionary>[]
        {
            d => d.Set("A", new PdfInteger(1)),
            d => d["A"] = new PdfInteger(1),
            d => d[new PdfName("A")] = new PdfInteger(1),
            d => d.Add("A", new PdfInteger(1)),
            d => d.SetName("A", "x"),
            d => d.SetInt("A", 1),
            d => d.Remove("Missing"),
            d => d.Clear(),
        })
        {
            var dict = new PdfDictionary();
            dict.MarkPristine();
            mutate(dict);
            dict.IsPristine.Should().BeFalse();
        }
    }

    private static PdfStream Image(PdfDocument doc)
    {
        var page = doc.GetPage(1);
        var resources = doc.Resolve(page.Dictionary.GetOptional("Resources")!).Should().BeOfType<PdfDictionary>().Subject;
        var xobjects = doc.Resolve(resources.GetOptional("XObject")!).Should().BeOfType<PdfDictionary>().Subject;
        return doc.Resolve(xobjects.GetOptional("Im0")!).Should().BeOfType<PdfStream>().Subject;
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

    private static byte[] OneIndirectFlateImageDocument()
    {
        using var doc = PdfDocument.CreateNew();
        var imageRef = doc.AddIndirectObject(ImageStream(Samples(3)));
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
