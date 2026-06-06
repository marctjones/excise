using AwesomeAssertions;
using Pdfe.Core.Filters.Jpx;
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

    /// <summary>
    /// Helper to build a minimal valid J2K codestream.
    /// Structure: SOC + SIZ + EOC
    /// </summary>
    private static byte[] BuildMinimalCodestream(int width, int height, int components, int bpc)
    {
        var builder = new System.IO.MemoryStream();

        // SOC marker (0xFF 0xD9) - Start of Codestream
        builder.WriteByte(0xFF);
        builder.WriteByte(0xD9);

        // SIZ marker (0xFF 0xD0) - Image and component size
        builder.WriteByte(0xFF);
        builder.WriteByte(0xD0);

        // SIZ payload length (including the 2-byte length field itself)
        // Components: we need Ssiz_i for each component (1 byte each)
        // Total: 2 (Rsiz) + 4 (Xsiz) + 4 (Ysiz) + 4 (XOsiz) + 4 (YOsiz)
        //        + 4 (XTsiz) + 4 (YTsiz) + 4 (XTOsiz) + 4 (YTOsiz) + 2 (Csiz) + components (Ssiz_i)
        int sizPayloadLength = 2 + 2 + 4 + 4 + 4 + 4 + 4 + 4 + 4 + 4 + 2 + components;
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
        }

        // EOC marker (0xFF 0xD8) - End of Codestream
        builder.WriteByte(0xFF);
        builder.WriteByte(0xD8);

        return builder.ToArray();
    }

    private static void WriteU32(System.IO.MemoryStream stream, uint value)
    {
        stream.WriteByte((byte)((value >> 24) & 0xFF));
        stream.WriteByte((byte)((value >> 16) & 0xFF));
        stream.WriteByte((byte)((value >> 8) & 0xFF));
        stream.WriteByte((byte)(value & 0xFF));
    }
}
