using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests;

/// <summary>
/// #1471: PNG output uses the Sub filter at zlib level 6. The encoded BYTES
/// change; the decoded pixels must not. Compared as decoded RGBA, never as
/// file bytes.
///
/// PNG stores unpremultiplied samples and render bitmaps are premultiplied,
/// so a pixel with 0 &lt; alpha &lt; 255 is not expected to survive a
/// premul -> PNG -> premul trip bit-exactly under EITHER encoder. Exact
/// source equality is therefore asserted only for opaque content; partially
/// transparent content is asserted equal to what the previous default encoder
/// produced. Nothing here is evidence that a render is opaque.
/// </summary>
public sealed class PngEncodingRoundTripTests
{
    [Fact]
    public void EncodePng_OpaqueBitmapDecodesToExactlyTheSourcePixels()
    {
        using var source = CreateBitmap(width: 257, height: 131, opaque: true);

        using var encoded = SkiaRenderer.EncodePng(source);
        encoded.Should().NotBeNull();
        using var decoded = DecodeUnpremul(encoded!, source.Width, source.Height);

        decoded.GetPixelSpan().ToArray().Should().Equal(source.GetPixelSpan().ToArray());
    }

    [Fact]
    public void EncodePng_PartialAlphaDecodesIdenticallyToTheDefaultEncoder()
    {
        using var source = CreateBitmap(width: 193, height: 97, opaque: false);

        using var encoded = SkiaRenderer.EncodePng(source);
        using var image = SKImage.FromBitmap(source);
        using var defaultEncoded = image.Encode(SKEncodedImageFormat.Png, 100);
        encoded.Should().NotBeNull();

        using var decoded = DecodeUnpremul(encoded!, source.Width, source.Height);
        using var defaultDecoded = DecodeUnpremul(defaultEncoded, source.Width, source.Height);

        decoded.GetPixelSpan().ToArray().Should().Equal(defaultDecoded.GetPixelSpan().ToArray());
    }

    [Fact]
    public void RenderPageToPng_DecodesToExactlyTheRenderedBitmap()
    {
        using var document = PdfDocument.Open(CreatePdf(
            "1 0 0 rg 36 36 200 120 re f " +
            "0 0.5 1 rg 300 400 180 250 re f " +
            "0.2 0.8 0.3 RG 8 w 50 700 m 560 90 l S"));
        var renderer = new SkiaRenderer();

        using var rendered = renderer.RenderPage(document.GetPage(1), new RenderOptions());
        using var png = new MemoryStream();
        renderer.RenderPageToPng(document.GetPage(1), png, new RenderOptions());

        // Precondition for exact comparison: this full-page render (no
        // ClipRect) has the paper composited under every pixel.
        var renderedBytes = rendered.GetPixelSpan().ToArray();
        for (var i = 3; i < renderedBytes.Length; i += 4)
            renderedBytes[i].Should().Be(255);

        png.Position = 0;
        using var data = SKData.Create(png);
        using var decoded = DecodeUnpremul(data, rendered.Width, rendered.Height);
        decoded.GetPixelSpan().ToArray().Should().Equal(renderedBytes);
    }

    private static SKBitmap DecodeUnpremul(SKData data, int width, int height)
    {
        var decoded = SKBitmap.Decode(
            data,
            new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        decoded.Should().NotBeNull();
        return decoded!;
    }

    /// <summary>
    /// Deterministic content that exercises every PNG row filter's choice:
    /// smooth gradients, hard edges and pseudo-random noise.
    /// </summary>
    private static SKBitmap CreateBitmap(int width, int height, bool opaque)
    {
        var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var random = new Random(1471);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                byte r, g, b;
                if (y < height / 3)
                {
                    r = (byte)(x * 255 / Math.Max(1, width - 1));
                    g = (byte)(y * 255 / Math.Max(1, height - 1));
                    b = (byte)((x + y) % 256);
                }
                else if (y < 2 * height / 3)
                {
                    var on = ((x / 7) + (y / 5)) % 2 == 0;
                    r = on ? (byte)250 : (byte)3;
                    g = on ? (byte)10 : (byte)240;
                    b = (byte)(on ? 128 : 77);
                }
                else
                {
                    r = (byte)random.Next(256);
                    g = (byte)random.Next(256);
                    b = (byte)random.Next(256);
                }

                var alpha = opaque ? (byte)255 : (byte)((x * 7 + y * 13) % 256);
                bitmap.SetPixel(x, y, new SKColor(r, g, b, alpha));
            }
        }

        return bitmap;
    }

    private static byte[] CreatePdf(string content)
    {
        var bodies = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream",
        };

        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, Encoding.ASCII, leaveOpen: true) { NewLine = "\n" };
        writer.WriteLine("%PDF-1.4");
        writer.Flush();
        var offsets = new long[bodies.Length + 1];
        for (var i = 0; i < bodies.Length; i++)
        {
            offsets[i + 1] = ms.Position;
            writer.WriteLine($"{i + 1} 0 obj");
            writer.WriteLine(bodies[i]);
            writer.WriteLine("endobj");
            writer.Flush();
        }

        var xref = ms.Position;
        writer.WriteLine("xref");
        writer.WriteLine($"0 {bodies.Length + 1}");
        writer.WriteLine("0000000000 65535 f ");
        for (var i = 1; i <= bodies.Length; i++)
            writer.WriteLine($"{offsets[i]:D10} 00000 n ");
        writer.WriteLine("trailer");
        writer.WriteLine($"<< /Root 1 0 R /Size {bodies.Length + 1} >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xref.ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.WriteLine("%%EOF");
        writer.Flush();
        return ms.ToArray();
    }
}
