using AwesomeAssertions;
using Pdfe.Core.Filters.Jbig2;
using Xunit;

namespace Pdfe.Core.Tests.Filters.Jbig2;

/// <summary>
/// Unit tests for SegmentHeaderParser.
/// Tests parsing of JBIG2 segment headers per ISO 14492 Section 7.2.2.
/// </summary>
public class SegmentHeaderParserTests
{
    /// <summary>
    /// Build a minimal valid JBIG2 segment header for testing.
    /// Segment number (4 bytes) + flags (1 byte) + referred count/retain bits
    /// (1 byte) + page association + segment data length (4 bytes).
    /// This is the minimal header with short page number and no referred-to segments.
    /// </summary>
    private static byte[] BuildMinimalSegmentHeader(uint segmentNum, byte flags, uint pageNum)
    {
        var buffer = new System.Collections.Generic.List<byte>();

        // Segment number (4 bytes, big-endian)
        buffer.Add((byte)((segmentNum >> 24) & 0xFF));
        buffer.Add((byte)((segmentNum >> 16) & 0xFF));
        buffer.Add((byte)((segmentNum >> 8) & 0xFF));
        buffer.Add((byte)(segmentNum & 0xFF));

        buffer.Add(flags);

        // Short-form referred-to segment count: top 3 bits are count, lower 5
        // are retain flags for count <= 4.
        buffer.Add(0);

        // Page number (1 byte for short form)
        if ((flags & 0x40) == 0)
        {
            // Short form
            buffer.Add((byte)pageNum);
        }
        else
        {
            // Long form (4 bytes)
            buffer.Add((byte)((pageNum >> 24) & 0xFF));
            buffer.Add((byte)((pageNum >> 16) & 0xFF));
            buffer.Add((byte)((pageNum >> 8) & 0xFF));
            buffer.Add((byte)(pageNum & 0xFF));
        }

        // Segment data length.
        buffer.Add(0);
        buffer.Add(0);
        buffer.Add(0);
        buffer.Add(0);

        return buffer.ToArray();
    }

    /// <summary>
    /// Test parsing a minimal generic region segment header.
    /// </summary>
    [Fact]
    public void ParseSegmentHeader_WithMinimalValidHeader_ParsesCorrectly()
    {
        uint segmentNum = 1;
        byte flags = 0x24; // Type 36 (generic region) in bits 5-0.
        uint pageNum = 1;

        byte[] headerData = BuildMinimalSegmentHeader(segmentNum, flags, pageNum);
        var parser = new SegmentHeaderParser(headerData);

        SegmentHeader? header = parser.ParseSegmentHeader();

        header.Should().NotBeNull();
        header!.SegmentNumber.Should().Be(segmentNum);
        header.SegmentType.Should().Be((int)SegmentType.GenericRegion);
        header.PageNumber.Should().Be(pageNum);
        header.DataLength.Should().Be(0);
    }

    /// <summary>
    /// Test that parsing null data throws.
    /// </summary>
    [Fact]
    public void Constructor_WithNullData_ThrowsArgumentNullException()
    {
        var action = () => new SegmentHeaderParser(null!);

        action.Should().Throw<ArgumentNullException>();
    }

    /// <summary>
    /// Test parsing returns null when at end of data.
    /// </summary>
    [Fact]
    public void ParseSegmentHeader_WhenAtEndOfData_ReturnsNull()
    {
        byte[] data = new byte[] { 0x00, 0x00 }; // Too short for valid header
        var parser = new SegmentHeaderParser(data);

        SegmentHeader? header = parser.ParseSegmentHeader();

        header.Should().BeNull();
    }

    /// <summary>
    /// Test that parser position tracking works.
    /// </summary>
    [Fact]
    public void Position_TrackingWorks_ReturnsCurrentByteIndex()
    {
        byte[] data = new byte[100];
        var parser = new SegmentHeaderParser(data);

        parser.Position.Should().Be(0);

        parser.SetPosition(10);
        parser.Position.Should().Be(10);

        parser.SetPosition(50);
        parser.Position.Should().Be(50);
    }

    /// <summary>
    /// Test RemainingBytes calculation.
    /// </summary>
    [Fact]
    public void RemainingBytes_CalculatesCorrectly()
    {
        byte[] data = new byte[100];
        var parser = new SegmentHeaderParser(data);

        parser.RemainingBytes.Should().Be(100);

        parser.SetPosition(25);
        parser.RemainingBytes.Should().Be(75);

        parser.SetPosition(100);
        parser.RemainingBytes.Should().Be(0);
    }

    /// <summary>
    /// Test that SetPosition clamps to valid range.
    /// </summary>
    [Fact]
    public void SetPosition_ClampsToValidRange()
    {
        byte[] data = new byte[100];
        var parser = new SegmentHeaderParser(data);

        // Negative should clamp to 0
        parser.SetPosition(-10);
        parser.Position.Should().Be(0);

        // Beyond end should clamp to data.Length
        parser.SetPosition(200);
        parser.Position.Should().Be(100);
    }

    /// <summary>
    /// Test parsing multiple segments in sequence.
    /// </summary>
    [Fact]
    public void ParseSegmentHeader_MultipleSegmentsInSequence_ParsesEach()
    {
        // Create two minimal headers back-to-back
        byte[] header1 = BuildMinimalSegmentHeader(1, 0x20, 1);
        byte[] header2 = BuildMinimalSegmentHeader(2, 0x24, 1);

        // Combine them
        var combined = new System.Collections.Generic.List<byte>();
        combined.AddRange(header1);
        combined.AddRange(header2);

        var parser = new SegmentHeaderParser(combined.ToArray());

        // Parse first header
        SegmentHeader? seg1 = parser.ParseSegmentHeader();
        seg1.Should().NotBeNull();
        seg1!.SegmentNumber.Should().Be(1u);

        // Parse second header
        SegmentHeader? seg2 = parser.ParseSegmentHeader();
        seg2.Should().NotBeNull();
        seg2!.SegmentNumber.Should().Be(2u);

        // Third parse should return null (no more data)
        SegmentHeader? seg3 = parser.ParseSegmentHeader();
        seg3.Should().BeNull();
    }

    /// <summary>
    /// Test page information segment flag (bit 6 = 1 for 4-byte page number).
    /// </summary>
    [Fact]
    public void ParseSegmentHeader_WithLongPageNumber_ParsesCorrectly()
    {
        uint segmentNum = 5;
        byte flags = 0x40; // Set bit 6 for long page number
        uint pageNum = 0x12345678;

        byte[] headerData = BuildMinimalSegmentHeader(segmentNum, flags, pageNum);
        var parser = new SegmentHeaderParser(headerData);

        SegmentHeader? header = parser.ParseSegmentHeader();

        header.Should().NotBeNull();
        header!.SegmentNumber.Should().Be(segmentNum);
        header.PageNumber.Should().Be(pageNum);
    }

    /// <summary>
    /// Test that truncated segment header returns null.
    /// The implementation checks length before reading and returns null.
    /// </summary>
    [Fact]
    public void ParseSegmentHeader_WithTruncatedData_ReturnsNull()
    {
        // Data too short (only 3 bytes, need at least 7 for minimal header)
        byte[] truncatedData = new byte[] { 0x00, 0x00, 0x00 };
        var parser = new SegmentHeaderParser(truncatedData);

        var result = parser.ParseSegmentHeader();

        // Should return null since not enough data
        result.Should().BeNull();
    }

    /// <summary>
    /// Test segment data offset tracking.
    /// The DataOffset field should point to where segment data begins (after header ends).
    /// </summary>
    [Fact]
    public void ParseSegmentHeader_DataOffsetPointsToSegmentData()
    {
        byte[] header = BuildMinimalSegmentHeader(1, 0x20, 1);
        var parser = new SegmentHeaderParser(header);

        SegmentHeader? parsed = parser.ParseSegmentHeader();

        parsed.Should().NotBeNull();
        // DataOffset should point to where the segment data begins (after header)
        // For a minimal header with short page number, this is at the end of the header bytes
        parsed!.DataOffset.Should().Be(11);
    }

    [Fact]
    public void ParseSegmentHeader_WithShortReferredSegments_ParsesAbsoluteSegmentNumbers()
    {
        var data = new System.Collections.Generic.List<byte>();
        data.AddRange(new byte[] { 0, 0, 1, 1 }); // Segment number 257 => 2-byte referred numbers.
        data.Add(0x04); // Text region.
        data.Add(0x40); // Short count 2 in top three bits, retain flags clear.
        data.AddRange(new byte[] { 0, 1, 0, 2 });
        data.Add(1); // Short page association.
        data.AddRange(new byte[] { 0, 0, 0, 0 });

        var parser = new SegmentHeaderParser(data.ToArray());

        var header = parser.ParseSegmentHeader();

        header.Should().NotBeNull();
        header!.ReferredSegmentCount.Should().Be(2);
        header.ReferredSegments.Should().Equal(1u, 2u);
        header.SegmentType.Should().Be((int)SegmentType.TextRegion);
    }

    [Fact]
    public void ParseSegmentHeader_WithExtendedReferredSegments_SkipsRetentionFlags()
    {
        var data = new System.Collections.Generic.List<byte>();
        data.AddRange(new byte[] { 0, 0, 0, 7 }); // Segment number.
        data.Add((byte)SegmentType.ImmediateLosslessTextRegion);
        data.AddRange(new byte[] { 0xE0, 0, 0, 6 }); // Extended referred-segment count = 6.
        data.Add(0); // One byte of retention flags for this segment + 6 references.
        data.AddRange(new byte[] { 1, 2, 3, 4, 5, 6 });
        data.Add(1); // Short page association.
        data.AddRange(new byte[] { 0, 0, 0, 12 });

        var parser = new SegmentHeaderParser(data.ToArray());

        var header = parser.ParseSegmentHeader();

        header.Should().NotBeNull();
        header!.ReferredSegmentCount.Should().Be(6);
        header.ReferredSegments.Should().Equal(1u, 2u, 3u, 4u, 5u, 6u);
        header.PageNumber.Should().Be(1);
        header.DataLength.Should().Be(12);
        header.DataOffset.Should().Be(21);
    }
}
