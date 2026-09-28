using System.Buffers.Binary;
using System.IO.Compression;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Core.Tests.Graphics;

/// <summary>
/// #1908: DrawImage places a JPEG or PNG as an image XObject in the page resources, one
/// entry per distinct file, painted by <c>q w 0 0 h x y cm /ImN Do Q</c>. The PNG fixtures
/// are written here from known pixels so the decoded samples can be checked exactly.
/// </summary>
public class PdfGraphicsImageTests
{
    /// <summary>A PNG of the given raw (unfiltered) rows, each written with filter type 0.</summary>
    private static byte[] Png(int colorType, int depth, int width, byte[][] rows, byte[]? plte = null, byte[]? trns = null,
        int interlace = 0)
    {
        using var file = new MemoryStream();
        file.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        void Chunk(string type, byte[] data)
        {
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
            file.Write(length);
            file.Write(System.Text.Encoding.ASCII.GetBytes(type));
            file.Write(data);
            file.Write(new byte[4]); // CRC: not checked by readers of this test's output
        }

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), rows.Length);
        (header[8], header[9], header[12]) = ((byte)depth, (byte)colorType, (byte)interlace);
        Chunk("IHDR", header);
        if (plte != null) Chunk("PLTE", plte);
        if (trns != null) Chunk("tRNS", trns);

        using var zlibbed = new MemoryStream();
        using (var z = new ZLibStream(zlibbed, CompressionLevel.Optimal, leaveOpen: true))
            foreach (var row in rows)
            {
                z.WriteByte(0);
                z.Write(row);
            }
        Chunk("IDAT", zlibbed.ToArray());
        Chunk("IEND", []);
        return file.ToArray();
    }

    /// <summary>The start of a JPEG: SOI, an APP0 segment, then a frame header. Enough for DrawImage, which reads only the header.</summary>
    private static byte[] JpegHeader(byte sof, int width, int height, int components, int precision = 8)
    {
        var bytes = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x4A, 0x46 };
        bytes.AddRange([0xFF, sof, 0x00, (byte)(8 + 3 * components), (byte)precision,
            (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, (byte)components]);
        for (int c = 0; c < components; c++)
            bytes.AddRange([(byte)(c + 1), 0x11, 0x00]);
        bytes.AddRange([0xFF, 0xD9]);
        return bytes.ToArray();
    }

    private static readonly byte[] RgbaPng = Png(6, 8, 2, [[255, 0, 0, 255, 0, 255, 0, 128], [0, 0, 255, 0, 9, 9, 9, 255]]);

    private static PdfStream Image(PdfPage page, string name) => page.GetXObject(name).Should().BeOfType<PdfStream>().Subject;

    [Fact]
    public void DrawImage_PaintsTheUnitSquareMappedOntoTheBox()
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(200, 200);
        using var g = page.GetGraphics();

        g.DrawImage(RgbaPng, 10, 20, 100, 50);

        g.GetOperators().Should().Be("q\n100 0 0 50 10 20 cm\n/Im1 Do\nQ\n");
    }

    [Fact]
    public void DrawImage_RgbaPng_IsAnRgbImageWithItsAlphaAsSoftMask()
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(200, 200);
        using (var g = page.GetGraphics())
            g.DrawImage(RgbaPng, 0, 0, 10, 10);

        var image = Image(page, "Im1");
        image.GetNameOrNull("Subtype").Should().Be("Image");
        (image.GetInt("Width"), image.GetInt("Height"), image.GetInt("BitsPerComponent")).Should().Be((2, 2, 8));
        image.GetNameOrNull("ColorSpace").Should().Be("DeviceRGB");
        image.DecodedData.Should().Equal(255, 0, 0, 0, 255, 0, 0, 0, 255, 9, 9, 9);

        var mask = doc.Resolve(image.GetOptional("SMask")!).Should().BeOfType<PdfStream>().Subject;
        mask.GetNameOrNull("ColorSpace").Should().Be("DeviceGray");
        mask.DecodedData.Should().Equal(255, 128, 0, 255);
    }

    [Fact]
    public void DrawImage_OpaquePng_HasNoSoftMask()
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(200, 200);
        using (var g = page.GetGraphics())
            g.DrawImage(Png(2, 8, 1, [[1, 2, 3]]), 0, 0, 10, 10);

        Image(page, "Im1").ContainsKey("SMask").Should().BeFalse();
        Image(page, "Im1").DecodedData.Should().Equal(1, 2, 3);
    }

    [Fact]
    public void DrawImage_PalettePngWithTransparency_ExpandsThePaletteAndItsAlpha()
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(200, 200);
        using (var g = page.GetGraphics())
            g.DrawImage(Png(3, 8, 3, [[0, 1, 2]], plte: [10, 20, 30, 40, 50, 60, 70, 80, 90], trns: [0, 200]), 0, 0, 10, 10);

        var image = Image(page, "Im1");
        image.DecodedData.Should().Equal(10, 20, 30, 40, 50, 60, 70, 80, 90);
        doc.Resolve(image.GetOptional("SMask")!).Should().BeOfType<PdfStream>()
            .Which.DecodedData.Should().Equal(0, 200, 255);
    }

    [Fact]
    public void DrawImage_16BitGrayPng_KeepsTheHighByteAndItsColourKey()
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(200, 200);
        using (var g = page.GetGraphics())
            g.DrawImage(Png(0, 16, 2, [[0x12, 0x34, 0xAB, 0xCD]], trns: [0x12, 0x34]), 0, 0, 10, 10);

        var image = Image(page, "Im1");
        image.DecodedData.Should().Equal(0x12, 0x12, 0x12, 0xAB, 0xAB, 0xAB);
        doc.Resolve(image.GetOptional("SMask")!).Should().BeOfType<PdfStream>()
            .Which.DecodedData.Should().Equal(0, 255);
    }

    [Theory]
    [InlineData(0xC0, 3, "DeviceRGB")]
    [InlineData(0xC2, 1, "DeviceGray")]
    public void DrawImage_Jpeg_IsEmbeddedAsIsWithDctDecode(byte sof, int components, string colorSpace)
    {
        var jpeg = JpegHeader(sof, 640, 480, components);
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(200, 200);
        using (var g = page.GetGraphics())
            g.DrawImage(jpeg, 0, 0, 10, 10);

        var image = Image(page, "Im1");
        image.GetNameOrNull("Filter").Should().Be("DCTDecode");
        (image.GetInt("Width"), image.GetInt("Height")).Should().Be((640, 480));
        image.GetNameOrNull("ColorSpace").Should().Be(colorSpace);
        image.EncodedData.Should().Equal(jpeg);
    }

    [Fact]
    public void DrawImage_SameBytesTwice_AddsOneXObject_EvenAcrossGraphicsContexts()
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(200, 200);
        using (var g = page.GetGraphics())
        {
            g.DrawImage(RgbaPng, 0, 0, 10, 10);
            g.DrawImage((byte[])RgbaPng.Clone(), 50, 50, 10, 10);
            g.DrawImage(Png(2, 8, 1, [[1, 2, 3]]), 0, 0, 10, 10);
            g.GetOperators().Should().Contain("/Im1 Do\nQ\nq\n10 0 0 10 50 50 cm\n/Im1 Do").And.Contain("/Im2 Do");
        }
        using (var g = page.GetGraphics())
        {
            g.DrawImage(RgbaPng, 0, 0, 10, 10);
            g.GetOperators().Should().Contain("/Im1 Do");
        }

        page.GetXObject("Im3").Should().BeNull();
    }

    [Fact]
    public void DrawImage_SkipsXObjectNamesAlreadyOnThePage()
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(200, 200);
        var xobjects = new PdfDictionary();
        xobjects["Im1"] = new PdfStream();
        var resources = new PdfDictionary();
        resources["XObject"] = xobjects;
        page.Dictionary["Resources"] = resources;

        using var g = page.GetGraphics();
        g.DrawImage(RgbaPng, 0, 0, 10, 10);

        g.GetOperators().Should().Contain("/Im2 Do");
        xobjects.Keys.Select(k => k.Value).Should().Equal("Im1", "Im2");
    }

    [Fact]
    public void DrawImage_AfterATranslucentFill_PaintsAtFullOpacity()
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(200, 200);
        using var g = page.GetGraphics();
        g.DrawRectangle(0, 0, 5, 5, new PdfBrush(PdfColor.Red) { Opacity = 0.5 });
        g.DrawImage(RgbaPng, 0, 0, 10, 10);

        g.GetOperators().Should().EndWith("f\n/GS2 gs\nq\n10 0 0 10 0 0 cm\n/Im1 Do\nQ\n");
        page.GetExtGState("GS2")!.GetOptional("ca")!.GetInt().Should().Be(1);
    }

    [Fact]
    public void DrawImage_SurvivesSaveAndReopen()
    {
        byte[] saved;
        using (var doc = PdfDocument.CreateNew())
        {
            var page = doc.Pages.AddBlank(200, 200);
            using (var g = page.GetGraphics())
                g.DrawImage(RgbaPng, 10, 20, 100, 50);
            saved = doc.SaveToBytes();
        }

        using var reopened = PdfDocument.Open(saved);
        var p = reopened.GetPage(1);
        System.Text.Encoding.Latin1.GetString(p.GetContentStreamBytes()).Should().Contain("q\n100 0 0 50 10 20 cm\n/Im1 Do\nQ\n");
        var image = Image(p, "Im1");
        image.DecodedData.Should().Equal(255, 0, 0, 0, 255, 0, 0, 0, 255, 9, 9, 9);
        reopened.Resolve(image.GetOptional("SMask")!).Should().BeOfType<PdfStream>()
            .Which.DecodedData.Should().Equal(255, 128, 0, 255);
    }

    public static TheoryData<string, byte[]> Unsupported => new()
    {
        { "not an image", [1, 2, 3, 4] },
        { "interlaced PNG", Png(2, 8, 1, [[1, 2, 3]], interlace: 1) },
        { "4-bit PNG", Png(0, 4, 2, [[0x12]]) },
        { "PNG with corrupt image data", CorruptIdat(Png(2, 8, 1, [[1, 2, 3]])) },
        { "CMYK JPEG", JpegHeader(0xC0, 10, 10, 4) },
        { "12-bit JPEG", JpegHeader(0xC1, 10, 10, 3, precision: 12) },
        { "lossless JPEG", JpegHeader(0xC3, 10, 10, 3) },
        { "JPEG with its height in a DNL segment", JpegHeader(0xC0, 10, 0, 3) },
    };

    /// <summary>Overwrite the IDAT payload (it follows the 33-byte signature and IHDR) with bytes that are not zlib.</summary>
    private static byte[] CorruptIdat(byte[] png)
    {
        int idat = 8 + 25 + 8;
        png.AsSpan(idat, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(idat - 8))).Fill(0x5A);
        return png;
    }

    [Theory]
    [MemberData(nameof(Unsupported))]
    public void DrawImage_UnsupportedImage_ThrowsAndChangesNothing(string what, byte[] bytes)
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(200, 200);
        using var g = page.GetGraphics();

        FluentActions.Invoking(() => g.DrawImage(bytes, 0, 0, 10, 10)).Should().Throw<ArgumentException>(what);
        g.GetOperators().Should().BeEmpty();
        page.Resources?.ContainsKey("XObject").Should().NotBe(true, "a refused image adds no resource entry");
    }

    [Fact]
    public void MixedDrawingWithImages_EveryPathObjectIsWellFormed()
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(200, 200);
        using var g = page.GetGraphics();
        g.SaveState();
        g.ClipRectangle(0, 0, 100, 100);
        g.DrawImage(RgbaPng, 0, 0, 50, 50);
        g.DrawCircle(50, 50, 20, PdfBrush.Blue, PdfPen.Black);
        g.MoveTo(0, 0);
        g.DrawImage(RgbaPng, 10, 10, 50, 50);
        g.LineTo(10, 10);
        g.Stroke(PdfPen.Black);
        g.RestoreState();

        PdfGraphicsOperatorOrderTests.AssertPathObjectsWellFormed(g.GetOperators());
    }
}
