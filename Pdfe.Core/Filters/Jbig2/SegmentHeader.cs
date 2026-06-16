using System;
using System.Collections.Generic;

namespace Pdfe.Core.Filters.Jbig2;

/// <summary>
/// JBIG2 segment header as defined in ISO 14492 Section 7.2.2.
/// Segments are the fundamental unit of JBIG2 data organization.
/// </summary>
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
internal class SegmentHeader
{
    /// <summary>Segment number (unique within page/document)</summary>
    public uint SegmentNumber { get; set; }

    /// <summary>Segment flags byte (contains type, immediate/deferred lossless, page association)</summary>
    public byte Flags { get; set; }

    /// <summary>Segment type (0-62, see SegmentType enum)</summary>
    public int SegmentType { get; set; }

    /// <summary>Page number to which this segment applies (0 = document-level)</summary>
    public uint PageNumber { get; set; }

    /// <summary>Count of segments this segment refers to</summary>
    public int ReferredSegmentCount { get; set; }

    /// <summary>List of referred-to segment numbers</summary>
    public List<uint> ReferredSegments { get; set; } = new();

    /// <summary>Data length in bytes (0 = unknown/streaming)</summary>
    public uint DataLength { get; set; }

    /// <summary>Byte offset where segment data begins</summary>
    public int DataOffset { get; set; }
}

/// <summary>
/// JBIG2 segment types (ISO 14492 Section 7.2.2).
/// </summary>
internal enum SegmentType
{
    SymbolDictionary = 0,
    TextRegion = 4,
    ImmediateTextRegion = 6,
    ImmediateLosslessTextRegion = 7,
    PatternDictionary = 16,
    HalftoneRegion = 20,
    ImmediateHalftoneRegion = 22,
    ImmediateLosslessHalftoneRegion = 23,
    GenericRegion = 36,
    ImmediateGenericRegion = 38,
    ImmediateLosslessGenericRegion = 39,
    GenericRefinementRegion = 40,
    ImmediateGenericRefinementRegion = 42,
    ImmediateLosslessGenericRefinementRegion = 43,
    PageInformation = 48,
    EndOfPage = 49,
    EndOfStripe = 50,
    EndOfFile = 51,
    ProfileSegment = 52,
    Table = 53,
}

/// <summary>
/// Parses JBIG2 segment headers from embedded data.
/// Handles both single-segment and multi-segment pages.
/// </summary>
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
internal class SegmentHeaderParser
{
    private readonly byte[] _data;
    private int _pos;

    public SegmentHeaderParser(byte[] data)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
        _pos = 0;
    }

    /// <summary>
    /// Parse the next segment header from the data stream.
    /// Returns null if at end of data.
    /// </summary>
    public SegmentHeader? ParseSegmentHeader()
    {
        if (_pos + 11 > _data.Length)
            return null;

        var header = new SegmentHeader
        {
            DataOffset = _pos
        };

        // Segment number (4 bytes, big-endian)
        header.SegmentNumber = ReadUInt32();

        // Segment flags (1 byte)
        header.Flags = ReadByte();

        // Bits 5-0 contain the segment type. Bit 6 controls page-association
        // field size; bit 7 is the retain flag.
        header.SegmentType = header.Flags & 0x3F;

        header.ReferredSegmentCount = ReadReferredSegmentCount();

        // Parse referred-to segment list
        if (header.ReferredSegmentCount > 0)
        {
            int referredSegmentNumberSize = header.SegmentNumber > 65536
                ? 4
                : header.SegmentNumber > 256 ? 2 : 1;
            for (int i = 0; i < header.ReferredSegmentCount; i++)
            {
                header.ReferredSegments.Add(ReadSegmentNumber(referredSegmentNumberSize));
            }
        }

        // Page association
        if ((header.Flags & 0x40) != 0)
        {
            // Page number present (4 bytes)
            if (_pos + 4 > _data.Length)
                throw new InvalidOperationException("Truncated segment header: not enough data for page association");
            header.PageNumber = ReadUInt32();
        }
        else
        {
            // Short page number (1 byte)
            if (_pos >= _data.Length)
                throw new InvalidOperationException("Truncated segment header: not enough data for page number");
            header.PageNumber = ReadByte();
        }

        if (_pos + 4 > _data.Length)
            throw new InvalidOperationException("Truncated segment header: not enough data for length field");
        header.DataLength = ReadUInt32();

        header.DataOffset = _pos;
        return header;
    }

    /// <summary>
    /// Skip past the segment data. Call after parsing the header.
    /// </summary>
    public void SkipSegmentData(SegmentHeader header)
    {
        if (header.DataLength > 0)
        {
            _pos += (int)Math.Min(header.DataLength, _data.Length - _pos);
        }
    }

    /// <summary>
    /// Position in the data stream.
    /// </summary>
    public int Position => _pos;

    /// <summary>
    /// Set position in the data stream.
    /// </summary>
    public void SetPosition(int pos) => _pos = Math.Max(0, Math.Min(pos, _data.Length));

    /// <summary>
    /// Get the remaining bytes to parse.
    /// </summary>
    public int RemainingBytes => _data.Length - _pos;

    private byte ReadByte()
    {
        if (_pos >= _data.Length)
            throw new InvalidOperationException("Unexpected end of data");
        return _data[_pos++];
    }

    private ushort ReadUInt16()
    {
        if (_pos + 2 > _data.Length)
            throw new InvalidOperationException("Unexpected end of data");
        ushort value = (ushort)((_data[_pos] << 8) | _data[_pos + 1]);
        _pos += 2;
        return value;
    }

    private uint ReadUInt32()
    {
        if (_pos + 4 > _data.Length)
            throw new InvalidOperationException("Unexpected end of data");
        uint value = ((uint)_data[_pos] << 24)
                   | ((uint)_data[_pos + 1] << 16)
                   | ((uint)_data[_pos + 2] << 8)
                   | (uint)_data[_pos + 3];
        _pos += 4;
        return value;
    }

    private int ReadReferredSegmentCount()
    {
        byte first = ReadByte();
        int shortCount = first >> 5;
        if (shortCount < 7)
            return shortCount;

        if (_pos + 3 > _data.Length)
            throw new InvalidOperationException("Truncated segment header: not enough data for referred-to segment count");

        return ((first & 0x1F) << 24)
             | (_data[_pos++] << 16)
             | (_data[_pos++] << 8)
             | _data[_pos++];
    }

    private uint ReadSegmentNumber(int byteCount)
        => byteCount switch
        {
            1 => ReadByte(),
            2 => ReadUInt16(),
            4 => ReadUInt32(),
            _ => throw new ArgumentOutOfRangeException(nameof(byteCount), byteCount, "Unsupported JBIG2 segment number width"),
        };
}
