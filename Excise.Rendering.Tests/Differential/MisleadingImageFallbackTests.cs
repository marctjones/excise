using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// A decoder that cannot decode must draw NOTHING and say so — never fabricate
/// pixels that a reader cannot tell from page content (#1383).
///
/// <para>Unlike the rest of this directory these tests are not oracle-agreement
/// tests, and deliberately so: on <c>bug_603518.pdf</c> every renderer produces
/// garbage (mutool leaves the remainder white, pdftocairo fills with the last
/// decoded tone, excise used to paint flat grey), so there is no correct output
/// to agree with. §7.4.8 defers DCTDecode to ISO/IEC 10918 and prescribes no
/// recovery, and §7.4.9 resolves exactly this precedence question for JPXDecode
/// — a present <c>/ColorSpace</c> wins — while no equivalent clause exists for
/// DCTDecode. The file is non-conformant and a conforming reader may reject the
/// image. This pins that decision.</para>
///
/// <para>What IS verified independently is the premise: the test parses the
/// embedded JPEG's SOF marker out of the raw bytes itself, without going
/// through any excise decoder, so "the JPEG declares 3 components while the
/// dictionary declares DeviceCMYK" is established from the file rather than
/// from the code under test.</para>
/// </summary>
public class MisleadingImageFallbackTests
{
    [Fact]
    public void DctComponentCountContradictingTheColorSpace_DrawsNothingAndIsDiagnosed()
    {
        var path = Path.Combine(FindRepoRoot(), "test-pdfs", "pdfium", "pixel", "bug_603518.pdf");
        Assert.SkipUnless(File.Exists(path), "corpus fixture not present: pdfium/pixel/bug_603518.pdf");

        // The premise, read from the file rather than from excise: the image
        // dictionary claims DeviceCMYK (4 components) and the JPEG's own SOF
        // header declares 3.
        var bytes = File.ReadAllBytes(path);
        SofComponentCount(bytes).Should().Be(3,
            "this fixture's value is that its JPEG and its dictionary disagree; " +
            "if the embedded JPEG ever became 4-component the test below would " +
            "be pinning nothing");

        var diagnostics = new List<string>();
        using var doc = PdfDocument.Open(path);
        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1), new RenderOptions { Dpi = 150, Diagnostics = diagnostics });

        // The specific fabrication: Skia fills truncated scan data with 0x80,
        // which reached the page as a large flat (128,128,128) rectangle —
        // 2,414,880 px at this dpi before the fix.
        FlatGreyPixels(bitmap).Should().BeLessThan(1000,
            "a DCTDecode stream whose component count contradicts its dictionary " +
            "describes two incompatible images, not one ambiguous one. Falling " +
            "through to a generic decode painted Skia's 0x80 fill as a grey block " +
            "the reader cannot distinguish from real content (#1383)");

        diagnostics.Should().Contain(d => d.Contains("#1383"),
            "a refusal nobody can see is indistinguishable from content that was " +
            "never there — the whole point of drawing nothing is that it is REPORTED");
    }

    /// <summary>
    /// The other half, and the one that matters more: the guard must not have
    /// widened into "any DCT failure". An ordinary DCTDecode image still has to
    /// render, and its ink is checked against mutool rather than against a
    /// checked-in excise number.
    /// </summary>
    [Fact]
    public void OrdinaryDctImage_StillDecodes()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var jpeg = EncodeJpeg(64, 64);
        var pdf = BuildDctImagePdf(jpeg, "/DeviceRGB", 64, 64);
        var path = Path.Combine(Path.GetTempPath(), $"excise-1383-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdf);
        try
        {
            using var doc = PdfDocument.Open(pdf);
            using var ours = new SkiaRenderer().RenderPage(doc.GetPage(1), new RenderOptions { Dpi = 72 });
            using var reference = MutoolReferenceRenderer.RenderPage(path, 1, 72);
            Assert.SkipWhen(reference == null, "mutool could not render the fixture");

            var refInk = InkedPixels(reference!);
            refInk.Should().BeGreaterThan(100, "the reference must actually draw the image");
            InkedPixels(ours).Should().BeGreaterThan(refInk / 2,
                "a well-formed DCTDecode image must still decode. If this fails, the " +
                "#1383 refusal has widened from 'the component count contradicts the " +
                "/ColorSpace' into 'any decode failure', which silently drops " +
                "recoverable images — the dangerous direction the issue calls out");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Component count from the JPEG's own SOF marker, parsed here rather than
    /// through any excise decoder, so the premise is independent of the code
    /// under test. Returns -1 when no SOF is found.
    /// </summary>
    private static int SofComponentCount(byte[] data)
    {
        for (var i = 0; i + 9 < data.Length; i++)
        {
            if (data[i] != 0xFF) continue;
            var marker = data[i + 1];
            // SOF0..SOF3, SOF5..SOF7, SOF9..SOF11, SOF13..SOF15 — every start
            // of frame except the DHT/JPG/DAC markers that share the range.
            var isSof = marker is >= 0xC0 and <= 0xCF
                        && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
            if (!isSof) continue;
            return data[i + 9];
        }

        return -1;
    }

    /// <summary>
    /// Pixels that are exactly Skia's 0x80 fill for undecodable scan data.
    /// </summary>
    private static int FlatGreyPixels(SKBitmap bmp)
    {
        var count = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.Red == 128 && c.Green == 128 && c.Blue == 128) count++;
            }

        return count;
    }

    private static long InkedPixels(SKBitmap bmp)
    {
        long inked = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if ((c.Red + c.Green + c.Blue) / 3 < 250) inked++;
            }

        return inked;
    }

    private static byte[] EncodeJpeg(int width, int height)
    {
        using var bmp = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                // Wide, smooth bands: a high-frequency pattern through JPEG is
                // ringing, not a decode test.
                var v = (byte)(x < width / 2 ? 24 : 200);
                bmp.SetPixel(x, y, new SKColor(v, (byte)(255 - v), 64));
            }

        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 95);
        return data.ToArray();
    }

    private static byte[] BuildDctImagePdf(byte[] jpeg, string colorSpace, int width, int height)
    {
        using var ms = new MemoryStream();
        void Write(string s) => ms.Write(System.Text.Encoding.ASCII.GetBytes(s));

        var content = System.Text.Encoding.ASCII.GetBytes("q 100 0 0 100 20 20 cm /Im0 Do Q\n");
        var offsets = new List<long>();
        Write("%PDF-1.7\n");

        offsets.Add(ms.Position);
        Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        offsets.Add(ms.Position);
        Write("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 140 140] >>\nendobj\n");
        offsets.Add(ms.Position);
        Write("3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 4 0 R "
              + "/Resources << /XObject << /Im0 5 0 R >> >> >>\nendobj\n");
        offsets.Add(ms.Position);
        Write($"4 0 obj\n<< /Length {content.Length} >>\nstream\n");
        ms.Write(content);
        Write("endstream\nendobj\n");
        offsets.Add(ms.Position);
        Write($"5 0 obj\n<< /Type /XObject /Subtype /Image /Width {width} /Height {height} "
              + $"/ColorSpace {colorSpace} /BitsPerComponent 8 /Filter /DCTDecode "
              + $"/Length {jpeg.Length} >>\nstream\n");
        ms.Write(jpeg);
        Write("\nendstream\nendobj\n");

        var xref = ms.Position;
        Write($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) Write($"{o:D10} 00000 n \n");
        Write($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF");
        return ms.ToArray();
    }

    private static string FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "excise.sln")))
            d = d.Parent;
        return d?.FullName ?? AppContext.BaseDirectory;
    }
}
