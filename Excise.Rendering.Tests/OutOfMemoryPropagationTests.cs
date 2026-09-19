using System.IO.Compression;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Rendering;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests;

/// <summary>
/// #1679: an <see cref="OutOfMemoryException"/> inside an image decode must
/// reach the caller. It used to be swallowed by the bare <c>catch</c> blocks
/// under <c>DecodeImageBitmap</c> and <c>RawSampleImageDecoder</c>, which turned
/// "ran out of memory" into "no image drawn" — a WRONG PICTURE with a success
/// exit code. Measured on the Altona technical2 page: every heap cap below the
/// render's need produced exit 0, "Saved to:", and a PNG that differed from the
/// unconstrained render. Nothing said so.
///
/// <para>The second test is the other half of the contract and is why the fix
/// is a <c>when</c> filter rather than deleting the catch: #878's "draw
/// nothing for an image that cannot be decoded" must survive. A decoder that
/// refuses is a note and a blank, not a crash — pdf.js issue18042 relies on it.</para>
/// </summary>
public class OutOfMemoryPropagationTests
{
    [Fact]
    public void OutOfMemoryDuringImageDecode_PropagatesOutOfRenderPage()
    {
        using var doc = PdfDocument.Open(OneFlateImageDocument());
        var page = doc.GetPage(1);
        var image = ImageXObject(doc, page);
        image.IsDecoded.Should().BeFalse("the object store defers image decode (#1468), so the throw below is what the render hits");

        image.DeferDecode(_ => throw new OutOfMemoryException("injected"));

        var act = () => new SkiaRenderer().RenderPage(page, new RenderOptions { Dpi = 72 });

        act.Should().Throw<OutOfMemoryException>(
            "an out-of-memory render must fail loudly; swallowing it draws a wrong page and reports success");
    }

    [Fact]
    public void OrdinaryDecodeFailure_StillDrawsNothingAndDoesNotThrow()
    {
        using var doc = PdfDocument.Open(OneFlateImageDocument());
        var page = doc.GetPage(1);
        var image = ImageXObject(doc, page);

        image.DeferDecode(_ => throw new InvalidOperationException("injected refusal"));

        var diagnostics = new List<string>();
        using var bitmap = new SkiaRenderer().RenderPage(page, new RenderOptions { Dpi = 72, Diagnostics = diagnostics });

        bitmap.Should().NotBeNull("#878: an undecodable image is a blank, not a failed render");
        diagnostics.Should().NotBeEmpty("the refusal must be recorded somewhere a caller can see it");
    }

    // ---- helpers -------------------------------------------------------

    private const int Width = 4;
    private const int Height = 4;

    private static PdfStream ImageXObject(PdfDocument doc, PdfPage page)
    {
        var resources = doc.Resolve(page.Dictionary.GetOptional("Resources")!).Should().BeOfType<PdfDictionary>().Subject;
        var xobjects = doc.Resolve(resources.GetOptional("XObject")!).Should().BeOfType<PdfDictionary>().Subject;
        return doc.Resolve(xobjects.GetOptional("Im0")!).Should().BeOfType<PdfStream>().Subject;
    }

    private static byte[] OneFlateImageDocument()
    {
        var samples = new byte[Width * Height * 3];
        for (var i = 0; i < samples.Length; i++) samples[i] = (byte)(i * 7);

        var dict = new PdfDictionary();
        dict.SetName("Type", "XObject");
        dict.SetName("Subtype", "Image");
        dict.SetInt("Width", Width);
        dict.SetInt("Height", Height);
        dict.SetInt("BitsPerComponent", 8);
        dict.SetName("ColorSpace", "DeviceRGB");
        dict.SetName("Filter", "FlateDecode");

        using var doc = PdfDocument.CreateNew();
        var imageRef = doc.AddIndirectObject(new PdfStream(dict, Flate(samples)));
        var page = doc.Pages.AddBlank(100, 100);
        var xobjects = new PdfDictionary();
        xobjects["Im0"] = imageRef;
        var resources = new PdfDictionary();
        resources["XObject"] = xobjects;
        page.Dictionary["Resources"] = resources;
        page.SetContentStreamBytes(Encoding.ASCII.GetBytes("q 40 0 0 40 10 10 cm /Im0 Do Q"));
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
