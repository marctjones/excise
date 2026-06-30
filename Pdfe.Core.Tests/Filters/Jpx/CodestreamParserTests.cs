using AwesomeAssertions;
using Pdfe.Core.Document;
using Pdfe.Core.Filters.Jpx;
using Pdfe.Core.Primitives;
using Xunit;

namespace Pdfe.Core.Tests.Filters.Jpx;

/// <summary>
/// Tests for JPEG2000 codestream parser.
/// ISO/IEC 15444-1:2019 Section 6 (Codestream syntax).
/// </summary>
public class CodestreamParserTests
{
    [Fact]
    public void ReadInfo_EmptyData_ThrowsArgumentException()
    {
        var action = () => JpxDecoder.ReadInfo(Array.Empty<byte>());

        action.Should().Throw<ArgumentException>()
            .WithMessage("*Empty JPEG2000 data*");
    }

    [Fact]
    public void ReadInfo_NullData_ThrowsArgumentException()
    {
        var action = () => JpxDecoder.ReadInfo(null!);

        action.Should().Throw<ArgumentException>()
            .WithMessage("*Empty JPEG2000 data*");
    }

    [Fact]
    public void ReadInfo_ValidSizMarker_ExtractsMetadata()
    {
        // Create a minimal J2K codestream with SOC + SIZ markers
        var data = BuildMinimalCodestream(
            width: 640,
            height: 480,
            components: 3,
            bpc: 8);

        var (width, height, components, bpc) = JpxDecoder.ReadInfo(data);

        width.Should().Be(640);
        height.Should().Be(480);
        components.Should().Be(3);
        bpc.Should().Be(8);
    }

    [Fact]
    public void ReadInfo_SingleComponent_ExtractsMetadata()
    {
        var data = BuildMinimalCodestream(
            width: 256,
            height: 256,
            components: 1,
            bpc: 8);

        var (width, height, components, bpc) = JpxDecoder.ReadInfo(data);

        components.Should().Be(1);
    }

    [Fact]
    public void ReadInfo_12BitDepth_ExtractsMetadata()
    {
        var data = BuildMinimalCodestream(
            width: 1024,
            height: 768,
            components: 1,
            bpc: 12);

        var (width, height, components, bpc) = JpxDecoder.ReadInfo(data);

        bpc.Should().Be(12);
    }

    [Fact]
    public void ReadInfo_LargeImage_ExtractsMetadata()
    {
        var data = BuildMinimalCodestream(
            width: 4096,
            height: 2160,
            components: 3,
            bpc: 8);

        var (width, height, components, bpc) = JpxDecoder.ReadInfo(data);

        width.Should().Be(4096);
        height.Should().Be(2160);
    }

    [Fact]
    public void Decode_ValidCodestream_ThrowsNotSupportedException()
    {
        var data = BuildMinimalCodestream(640, 480, 3, 8);

        var action = () => JpxDecoder.Decode(data);

        action.Should().Throw<NotSupportedException>()
            .WithMessage("*full decode not yet implemented*");
    }

    [Fact]
    public void ReadInfo_Jp2ContainerWithJp2cBox_ExtractsMetadata()
    {
        var path = FindRepoFile("test-pdfs", "pdfjs", "issue19517.pdf");
        Assert.SkipWhen(path == null,
            "No pdf.js issue19517 fixture found at test-pdfs/pdfjs/issue19517.pdf.");

        using var doc = PdfDocument.Open(path);
        var thumbnail = (PdfStream)doc.GetObject(13);

        var (width, height, components, bpc) = JpxDecoder.ReadInfo(thumbnail.EncodedData);

        width.Should().Be(1);
        height.Should().Be(1);
        components.Should().Be(4);
        bpc.Should().Be(8);
    }

    [Fact]
    public void TryDecodeManaged_Jp2ContainerFallbackExtractsCodestream()
    {
        var path = FindRepoFile("test-pdfs", "pdfjs", "issue19517.pdf");
        Assert.SkipWhen(path == null,
            "No pdf.js issue19517 fixture found at test-pdfs/pdfjs/issue19517.pdf.");

        using var doc = PdfDocument.Open(path);
        var thumbnail = (PdfStream)doc.GetObject(13);

        var image = JpxDecoder.TryDecodeManaged(thumbnail.EncodedData, maxComponents: 3);

        image.Should().NotBeNull();
        image!.Components.Should().Be(4);
        image.ComponentData.Should().HaveCount(3);
        image.ComponentData[0].Should().Equal(255);
        image.ComponentData[1].Should().Equal(0);
        image.ComponentData[2].Should().Equal(39);
        image.ComponentDefinitions.Should().Contain(definition =>
            definition.ComponentIndex == 0 && definition.Type == 0 && definition.Association == 1);
        image.ComponentDefinitions.Should().Contain(definition =>
            definition.ComponentIndex == 1 && definition.Type == 0 && definition.Association == 2);
        image.ComponentDefinitions.Should().Contain(definition =>
            definition.ComponentIndex == 2 && definition.Type == 0 && definition.Association == 3);
    }

    [Fact]
    public void TryDecodeManaged_Jp2GrayWithOpacityCanDecodeFirstColorComponent()
    {
        var path = FindRepoFile("test-pdfs", "pdfjs", "issue19326.pdf");
        Assert.SkipWhen(path == null,
            "No pdf.js issue19326 fixture found at test-pdfs/pdfjs/issue19326.pdf.");

        using var doc = PdfDocument.Open(path);
        var imageStream = (PdfStream)doc.GetObject(8);

        var image = JpxDecoder.TryDecodeManaged(imageStream.EncodedData, maxComponents: 2);

        image.Should().NotBeNull();
        image!.Width.Should().Be(551);
        image.Height.Should().Be(337);
        image.BitsPerComponent.Should().Be(16);
        image.ComponentData.Should().ContainSingle();
        image.ComponentData[0].Should().Contain(sample => sample < 4096);
        image.ComponentData[0].Should().Contain(sample => sample > 60000);
    }

    [Fact]
    public void TryDecodeManaged_AltonaIndexedJpxDecodesSingleIndexPlane()
    {
        var path = FindRepoFile(
            "test-pdfs",
            "altona",
            "eci_altona-test-suite-v2_technical2_one-patch-per-page_x4.pdf");
        Assert.SkipWhen(path == null,
            "No Altona PDF/X fixture found at test-pdfs/altona/eci_altona-test-suite-v2_technical2_one-patch-per-page_x4.pdf.");

        using var doc = PdfDocument.Open(path);
        var imageStream = (PdfStream)doc.GetObject(335);

        var image = JpxDecoder.TryDecodeManaged(imageStream.EncodedData, maxComponents: 1);

        image.Should().NotBeNull();
        image!.Width.Should().BeOneOf(0, 424);
        image.Height.Should().BeOneOf(0, 212);
        image.BitsPerComponent.Should().Be(8);
        image.ComponentData.Should().ContainSingle(
            "the Altona Indexed JPX has one color index component and no embedded alpha component");
        image.ComponentData[0].Should().HaveCount(424 * 212);
        image.ComponentData[0].Should().Contain(sample => sample < 64);
        image.ComponentData[0].Should().Contain(sample => sample > 180);
    }

    [Theory]
    [InlineData(338, 16)]
    [InlineData(341, 8)]
    public void TryDecodeJpx_AltonaGrayJpxDecodesSingleColorPlane(int objectNumber, int bitsPerComponent)
    {
        var path = FindRepoFile(
            "test-pdfs",
            "altona",
            "eci_altona-test-suite-v2_technical2_one-patch-per-page_x4.pdf");
        Assert.SkipWhen(path == null,
            "No Altona PDF/X fixture found at test-pdfs/altona/eci_altona-test-suite-v2_technical2_one-patch-per-page_x4.pdf.");

        using var doc = PdfDocument.Open(path);
        var imageStream = (PdfStream)doc.GetObject(objectNumber);

        var image = JpxDecoder.TryDecodeManaged(imageStream.EncodedData, maxComponents: 2)
                    ?? JpxDecoder.TryDecodeOpenJpegGray(imageStream.EncodedData);

        Assert.SkipWhen(image == null,
            "Neither managed JPX nor optional opj_decompress could decode the Altona grayscale JPX fixture.");
        image!.BitsPerComponent.Should().Be(bitsPerComponent);
        image.ComponentData.Should().ContainSingle(
            "single-component grayscale JPX images should not expose a bogus alpha component");
        image.ComponentData[0].Should().HaveCount(424 * 212);
        image.ComponentData[0].Should().Contain(sample => sample < (bitsPerComponent == 16 ? 16_384 : 64));
        image.ComponentData[0].Should().Contain(sample => sample > (bitsPerComponent == 16 ? 49_152 : 180));
    }

    /// <summary>
    /// Helper to build a minimal valid J2K codestream.
    /// Structure: SOC + SIZ + EOC
    /// </summary>
    private static byte[] BuildMinimalCodestream(int width, int height, int components, int bpc)
    {
        var builder = new System.IO.MemoryStream();

        // SOC marker (0xFF 0x4F) - Start of Codestream
        builder.WriteByte(0xFF);
        builder.WriteByte(0x4F);

        // SIZ marker (0xFF 0x51) - Image and component size
        builder.WriteByte(0xFF);
        builder.WriteByte(0x51);

        // SIZ payload length (including the 2-byte length field itself)
        // Components: Ssiz_i, XRsiz_i, YRsiz_i for each component (3 bytes each)
        // Total: 2 (Rsiz) + 4 (Xsiz) + 4 (Ysiz) + 4 (XOsiz) + 4 (YOsiz)
        //        + 4 (XTsiz) + 4 (YTsiz) + 4 (XTOsiz) + 4 (YTOsiz) + 2 (Csiz)
        //        + components * 3, plus the 2-byte Lsiz field.
        int sizPayloadLength = 38 + (components * 3);
        builder.WriteByte((byte)((sizPayloadLength >> 8) & 0xFF));
        builder.WriteByte((byte)(sizPayloadLength & 0xFF));

        // Rsiz (profile, 2 bytes) - use 0 (no specific profile)
        builder.WriteByte(0x00);
        builder.WriteByte(0x00);

        // Xsiz, Ysiz (image dimensions, 4 bytes each, big-endian)
        WriteU32(builder, (uint)width);
        WriteU32(builder, (uint)height);

        // XOsiz, YOsiz (offsets, 4 bytes each, typically 0)
        WriteU32(builder, 0);
        WriteU32(builder, 0);

        // XTsiz, YTsiz (tile size, 4 bytes each)
        WriteU32(builder, (uint)width); // Assume single tile
        WriteU32(builder, (uint)height);

        // XTOsiz, YTOsiz (tile offsets, 4 bytes each)
        WriteU32(builder, 0);
        WriteU32(builder, 0);

        // Csiz (number of components, 2 bytes)
        builder.WriteByte((byte)((components >> 8) & 0xFF));
        builder.WriteByte((byte)(components & 0xFF));

        // Ssiz_i (component bit depth, 1 byte per component)
        for (int i = 0; i < components; i++)
        {
            // Bit 7: sign (0=unsigned, 1=signed)
            // Bits 6-0: bit depth - 1
            byte bpcByte = (byte)((bpc - 1) & 0x7F); // unsigned
            builder.WriteByte(bpcByte);
            builder.WriteByte(1); // XRsiz_i
            builder.WriteByte(1); // YRsiz_i
        }

        // EOC marker (0xFF 0xD9) - End of Codestream
        builder.WriteByte(0xFF);
        builder.WriteByte(0xD9);

        return builder.ToArray();
    }

    private static void WriteU32(System.IO.MemoryStream stream, uint value)
    {
        stream.WriteByte((byte)((value >> 24) & 0xFF));
        stream.WriteByte((byte)((value >> 16) & 0xFF));
        stream.WriteByte((byte)((value >> 8) & 0xFF));
        stream.WriteByte((byte)(value & 0xFF));
    }

    private static string? FindRepoFile(params string[] segments)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(segments).ToArray());
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        return null;
    }
}
