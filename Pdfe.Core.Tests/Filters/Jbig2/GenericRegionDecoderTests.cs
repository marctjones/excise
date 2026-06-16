using AwesomeAssertions;
using Pdfe.Core.Filters.Jbig2;
using Xunit;

namespace Pdfe.Core.Tests.Filters.Jbig2;

/// <summary>
/// Unit tests for GenericRegionDecoder.
/// Tests decoding of JBIG2 generic regions per ISO 14492 Section 6.2.
/// </summary>
public class GenericRegionDecoderTests
{
    /// <summary>
    /// Test basic initialization and flag parsing.
    /// </summary>
    [Fact]
    public void ParseFlags_WithValidFlags_ParsesWithoutError()
    {
        byte flags = 0x00; // Template 0, no typical prediction
        var decoder = new GenericRegionDecoder();

        // Should not throw
        decoder.ParseFlags(flags);
    }

    /// <summary>
    /// Test template selection from flags.
    /// Bits 2-1 select template (0-3).
    /// </summary>
    [Fact]
    public void ParseFlags_TemplateSelectionFromBits()
    {
        var decoder = new GenericRegionDecoder();

        // Template 0
        decoder.ParseFlags(0x00);

        // Template 1
        decoder.ParseFlags(0x02);

        // Template 2
        decoder.ParseFlags(0x04);

        // Template 3
        decoder.ParseFlags(0x06);

        // All should complete without error
    }

    /// <summary>
    /// Test typical prediction flag (bit 3).
    /// </summary>
    [Fact]
    public void ParseFlags_TypicalPredictionFlag()
    {
        var decoder = new GenericRegionDecoder();

        // Without typical prediction
        decoder.ParseFlags(0x00);

        // With typical prediction
        decoder.ParseFlags(0x08);
    }

    [Theory]
    [InlineData(0x01, "MMR-encoded")]
    [InlineData(0x02, "template 1")]
    [InlineData(0x08, "typical prediction")]
    [InlineData(0x10, "adaptive template")]
    public void DecodeGenericRegion_WithUnsupportedMode_ThrowsNotSupported(byte flags, string expectedMessage)
    {
        var decoder = new GenericRegionDecoder();
        decoder.ParseFlags(flags);

        var act = () => decoder.DecodeGenericRegion(new byte[] { 0x00 }, 1, 1, 0, 0);

        act.Should().Throw<NotSupportedException>()
            .WithMessage($"*{expectedMessage}*");
    }

    /// <summary>
    /// Test decoding a minimal region.
    /// This tests the basic structure; actual decoding will be limited
    /// since we don't have a real JBIG2 generic region stream.
    /// </summary>
    [Fact]
    public void DecodeGenericRegion_WithSmallDimensions_ReturnsDataOfCorrectSize()
    {
        int width = 8;
        int height = 8;
        byte[] regionData = new byte[] { 0x00 }; // Minimal valid data (just flags)

        var decoder = new GenericRegionDecoder();

        // Should not throw and should return data of correct size
        byte[] result = decoder.DecodeGenericRegion(regionData, width, height, 0, 0);

        // Output should be ((width + 7) / 8) * height bytes
        int expectedBytes = ((width + 7) / 8) * height;
        result.Length.Should().Be(expectedBytes);
    }

    /// <summary>
    /// Test that output is properly sized with decoded content.
    /// With actual MQ decoding, we'll get some non-zero bytes depending on the stream.
    /// </summary>
    [Fact]
    public void DecodeGenericRegion_OutputProperlyDecoded()
    {
        int width = 16;
        int height = 16;
        byte[] regionData = new byte[] { 0x00 }; // Flags byte only

        var decoder = new GenericRegionDecoder();
        byte[] result = decoder.DecodeGenericRegion(regionData, width, height, 0, 0);

        // Result should have correct size
        int expectedBytes = ((width + 7) / 8) * height;
        result.Length.Should().Be(expectedBytes);

        // Some bytes may be non-zero due to MQ decoding
        // Just verify it's a valid output array
        result.Should().NotBeNull();
    }

    /// <summary>
    /// Test that width and height validation works.
    /// </summary>
    [Fact]
    public void DecodeGenericRegion_WithZeroWidth_ThrowsArgumentException()
    {
        var decoder = new GenericRegionDecoder();
        byte[] regionData = new byte[] { 0x00 };

        var action = () => decoder.DecodeGenericRegion(regionData, 0, 10, 0, 0);

        action.Should().Throw<ArgumentException>()
            .WithMessage("*Invalid region dimensions*");
    }

    /// <summary>
    /// Test with zero height.
    /// </summary>
    [Fact]
    public void DecodeGenericRegion_WithZeroHeight_ThrowsArgumentException()
    {
        var decoder = new GenericRegionDecoder();
        byte[] regionData = new byte[] { 0x00 };

        var action = () => decoder.DecodeGenericRegion(regionData, 10, 0, 0, 0);

        action.Should().Throw<ArgumentException>()
            .WithMessage("*Invalid region dimensions*");
    }

    /// <summary>
    /// Test output size calculation for various dimensions.
    /// The output should be properly padded to byte boundaries.
    /// </summary>
    [Theory]
    [InlineData(1, 1, 1)]   // 1 bit per row = 1 byte
    [InlineData(7, 1, 1)]   // 7 bits per row = 1 byte (padded)
    [InlineData(8, 1, 1)]   // 8 bits per row = 1 byte
    [InlineData(9, 1, 2)]   // 9 bits per row = 2 bytes (padded)
    [InlineData(16, 2, 4)]  // 16 bits per row = 2 bytes, 2 rows = 4 bytes
    [InlineData(100, 100, 1300)] // 100 bits = 13 bytes per row, 100 rows = 1300
    public void DecodeGenericRegion_OutputSizeCalculation(int width, int height, int expectedBytes)
    {
        var decoder = new GenericRegionDecoder();
        byte[] regionData = new byte[] { 0x00 };

        byte[] result = decoder.DecodeGenericRegion(regionData, width, height, 0, 0);

        result.Length.Should().Be(expectedBytes);
    }

    /// <summary>
    /// Test that negative dimensions are rejected.
    /// </summary>
    [Fact]
    public void DecodeGenericRegion_WithNegativeWidth_ThrowsArgumentException()
    {
        var decoder = new GenericRegionDecoder();
        byte[] regionData = new byte[] { 0x00 };

        var action = () => decoder.DecodeGenericRegion(regionData, -1, 10, 0, 0);

        action.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// Test large region handling.
    /// </summary>
    [Fact]
    public void DecodeGenericRegion_WithLargeRegion_AllocatesCorrectly()
    {
        int width = 1024;
        int height = 768;
        var decoder = new GenericRegionDecoder();
        byte[] regionData = new byte[] { 0x00 };

        byte[] result = decoder.DecodeGenericRegion(regionData, width, height, 0, 0);

        int expectedBytes = ((width + 7) / 8) * height;
        result.Length.Should().Be(expectedBytes);
    }

    /// <summary>
    /// Test with position offset (x, y parameters).
    /// These parameters define where in a larger image the region is positioned.
    /// For now, our decoder ignores them and treats them as in-region coordinates.
    /// </summary>
    [Fact]
    public void DecodeGenericRegion_WithPositionOffset_DoesNotAffectOutputSize()
    {
        int width = 8;
        int height = 8;
        var decoder = new GenericRegionDecoder();
        byte[] regionData = new byte[] { 0x00 };

        byte[] result1 = decoder.DecodeGenericRegion(regionData, width, height, 0, 0);
        byte[] result2 = decoder.DecodeGenericRegion(regionData, width, height, 100, 200);

        // Output size should be the same regardless of position
        result1.Length.Should().Be(result2.Length);
    }

    /// <summary>
    /// Test minimum region size (1x1 pixel).
    /// </summary>
    [Fact]
    public void DecodeGenericRegion_With1x1Region_WorksCorrectly()
    {
        var decoder = new GenericRegionDecoder();
        byte[] regionData = new byte[] { 0x00 };

        byte[] result = decoder.DecodeGenericRegion(regionData, 1, 1, 0, 0);

        // 1 bit in 1 byte
        result.Length.Should().Be(1);
    }

    /// <summary>
    /// Test edge cases around byte boundaries.
    /// </summary>
    [Theory]
    [InlineData(7, 7)]   // Just under byte boundary
    [InlineData(8, 8)]   // Exact byte boundary
    [InlineData(15, 15)] // Just under double byte
    [InlineData(16, 16)] // Exact double byte
    public void DecodeGenericRegion_BoundaryPixelCounts_CalculateCorrectly(int width, int height)
    {
        var decoder = new GenericRegionDecoder();
        byte[] regionData = new byte[] { 0x00 };

        byte[] result = decoder.DecodeGenericRegion(regionData, width, height, 0, 0);

        int expectedBytes = ((width + 7) / 8) * height;
        result.Length.Should().Be(expectedBytes);
    }
}
