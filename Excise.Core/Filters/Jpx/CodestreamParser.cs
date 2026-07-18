namespace Excise.Core.Filters.Jpx;

/// <summary>
/// Parses JPEG2000 codestream and JP2 box containers.
/// ISO/IEC 15444-1:2019 Section 6 (Codestream syntax).
/// </summary>
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
internal sealed class CodestreamParser
{
    private readonly byte[] _data;
    private int _pos;

    public CodestreamParser(byte[] data)
    {
        _data = data;
        _pos = 0;
    }

    /// <summary>
    /// Extract image metadata (width, height, components, bit depth).
    /// Parses JP2 container if present, otherwise assumes raw J2K codestream.
    /// </summary>
    public ImageMetadata ExtractMetadata()
    {
        // Check for JP2 box signature: "jP  " (0xFF, 0x4A, 0x50, 0x20)
        // or JP2 "ftyp" box marker (offset 4 bytes: 0x66, 0x74, 0x79, 0x70)
        if (_data.Length >= 4 && IsJp2Header())
        {
            SkipJp2Boxes();
        }

        // Now we should be at the J2K codestream (starts with SOC marker 0xFF4F).
        var metadata = new ImageMetadata();
        metadata = ParseCodestream(metadata);
        return metadata;
    }

    private bool IsJp2Header()
    {
        // Check for a JP2 box type at offset 4-7. A JP2 file usually starts
        // with the signature box (`jP  `) followed by `ftyp`; some producers
        // begin directly with `ftyp`.
        if (_data.Length >= 12)
        {
            if (_data[4] == 0x6A && _data[5] == 0x50 && _data[6] == 0x20 && _data[7] == 0x20)
                return true; // jP   signature box
            if (_data[4] == 0x66 && _data[5] == 0x74 && _data[6] == 0x79 && _data[7] == 0x70)
                return true; // ftyp box
        }
        return false;
    }

    private void SkipJp2Boxes()
    {
        // Skip JP2 boxes (jP, ftyp, jp2h) to reach the jp2c codestream box.
        // Each box: 4 bytes size (big-endian), 4 bytes type
        while (_pos < _data.Length - 8)
        {
            var start = _pos;
            var size = (long)ReadU32();
            var typeBytes = ReadBytes(4);
            var typeStr = System.Text.Encoding.ASCII.GetString(typeBytes);
            var headerSize = 8L;

            if (size == 1)
            {
                if (_pos > _data.Length - 8)
                    break;

                size = (long)ReadU64();
                headerSize = 16L;
            }
            else if (size == 0)
            {
                size = _data.Length - start;
            }

            if (size < headerSize || start + size > _data.Length)
                break;

            if (typeStr == "jp2c")
            {
                // Found the codestream box; the codestream data follows immediately
                // (Note: if size > 0, we're already positioned at the start of codestream data)
                return;
            }

            // Skip to next box
            _pos = (int)(start + size);
        }

        // If no jpc box found, assume we're at the start of raw J2K codestream
        _pos = 0;
    }

    private ImageMetadata ParseCodestream(ImageMetadata metadata)
    {
        // Expect SOC marker (0xFF4F).
        var marker = ReadMarker();
        if (marker != 0xFF4F)
            throw new ArgumentException($"Expected SOC marker (0xFF4F), got 0x{marker:X4}", nameof(marker));

        // Parse markers until EOC (0xFFD9).
        while (_pos < _data.Length - 1)
        {
            marker = ReadMarker();

            switch (marker)
            {
                case 0xFF51: // SIZ - image and component size
                    metadata = ParseSIZ(metadata);
                    break;

                case 0xFF52: // COD - coding style default
                    ParseCOD(); // For now, just skip (wavelet info)
                    break;

                case 0xFF53: // COC - coding style component
                    ParseCOC();
                    break;

                case 0xFF5C: // QCD - quantization default
                    ParseQCD();
                    break;

                case 0xFF5D: // QCC - quantization component
                    ParseQCC();
                    break;

                case 0xFF90: // SOT - start of tile
                    // Tile-part header; skip for metadata-only extraction
                    SkipMarkerPayload();
                    break;

                case 0xFF93: // SOD - start of data
                    // Codestream data follows; skip to next marker
                    SkipToNextMarker();
                    break;

                case 0xFFD9: // EOC - end of codestream
                    return metadata;

                default:
                    // Unknown marker; try to skip safely
                    SkipMarkerPayload();
                    break;
            }
        }

        return metadata;
    }

    private ImageMetadata ParseSIZ(ImageMetadata metadata)
    {
        // SIZ - Section 6.2.1.1
        // Marker (2 bytes) + Length (2 bytes) + Payload (Length - 2)
        var length = ReadU16();
        var payloadStart = _pos;

        var capabilities = ReadU16(); // Rsiz (profile)
        var xsiz = ReadU32();
        var ysiz = ReadU32();
        var xosiz = ReadU32();
        var yosiz = ReadU32();
        metadata.Width = (int)Math.Max(0, xsiz - xosiz);
        metadata.Height = (int)Math.Max(0, ysiz - yosiz);
        _ = ReadU32(); // XOTSiz (tile width offset)
        _ = ReadU32(); // YOTSiz (tile height offset)
        _ = ReadU32(); // XTSiz (tile width)
        _ = ReadU32(); // YTSiz (tile height)

        var nComps = (int)ReadU16(); // Number of components
        metadata.Components = nComps;

        // Parse Csiz (component size) information
        for (int i = 0; i < nComps; i++)
        {
            var bpc = ReadU8();
            if (i == 0)
            {
                metadata.BitsPerComponent = (bpc & 0x7F) + 1;
                metadata.SignedComponent = (bpc & 0x80) != 0;
            }
            _ = ReadU8(); // XRsiz
            _ = ReadU8(); // YRsiz
        }

        // Advance past any remaining SIZ payload
        var bytesRead = _pos - payloadStart;
        var remainingInPayload = length - 2 - bytesRead;
        if (remainingInPayload > 0)
        {
            _pos += remainingInPayload;
        }

        return metadata;
    }

    private void ParseCOD()
    {
        // COD - Coding style default (Section 6.2.2.1)
        // We skip this for metadata-only extraction; it contains decomposition levels, filter type, etc.
        SkipMarkerPayload();
    }

    private void ParseCOC()
    {
        // COC - Coding style component (Section 6.2.2.2)
        SkipMarkerPayload();
    }

    private void ParseQCD()
    {
        // QCD - Quantization default (Section 6.2.4.1)
        SkipMarkerPayload();
    }

    private void ParseQCC()
    {
        // QCC - Quantization component (Section 6.2.4.2)
        SkipMarkerPayload();
    }

    private void SkipMarkerPayload()
    {
        // Marker payload: length (2 bytes) + payload
        if (_pos >= _data.Length - 1)
            return;

        var length = ReadU16();
        _pos += Math.Max(0, length - 2); // -2 because we already read the length field
    }

    private void SkipToNextMarker()
    {
        // Skip bytes until we find the next marker (0xFF 0xXX where XX != 0x00, 0xFF)
        while (_pos < _data.Length - 1)
        {
            if (_data[_pos] == 0xFF && _data[_pos + 1] != 0x00 && _data[_pos + 1] != 0xFF)
            {
                break; // Found next marker
            }
            _pos++;
        }
    }

    private ushort ReadMarker()
    {
        if (_pos >= _data.Length - 1)
            throw new ArgumentException("Unexpected end of data while reading marker");

        var b1 = _data[_pos++];
        var b2 = _data[_pos++];

        if (b1 != 0xFF)
            throw new ArgumentException($"Invalid marker (expected 0xFF, got 0x{b1:X2})");

        return (ushort)(0xFF00 | b2);
    }

    private uint ReadU32()
    {
        if (_pos > _data.Length - 4)
            throw new ArgumentException("Unexpected end of data while reading U32");

        uint val = ((uint)_data[_pos] << 24) |
                   ((uint)_data[_pos + 1] << 16) |
                   ((uint)_data[_pos + 2] << 8) |
                   _data[_pos + 3];
        _pos += 4;
        return val;
    }

    private ulong ReadU64()
    {
        if (_pos > _data.Length - 8)
            throw new ArgumentException("Unexpected end of data while reading U64");

        ulong val = 0;
        for (var i = 0; i < 8; i++)
            val = (val << 8) | _data[_pos++];
        return val;
    }

    private ushort ReadU16()
    {
        if (_pos > _data.Length - 2)
            throw new ArgumentException("Unexpected end of data while reading U16");

        var val = (ushort)((_data[_pos] << 8) | _data[_pos + 1]);
        _pos += 2;
        return val;
    }

    private byte ReadU8()
    {
        if (_pos >= _data.Length)
            throw new ArgumentException("Unexpected end of data while reading U8");

        return _data[_pos++];
    }

    private byte[] ReadBytes(int count)
    {
        if (_pos > _data.Length - count)
            throw new ArgumentException($"Unexpected end of data while reading {count} bytes");

        var result = new byte[count];
        Array.Copy(_data, _pos, result, 0, count);
        _pos += count;
        return result;
    }
}

/// <summary>
/// Image metadata extracted from JPEG2000 codestream.
/// </summary>
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
internal sealed class ImageMetadata
{
    public int Width { get; set; }
    public int Height { get; set; }
    public int Components { get; set; }
    public int BitsPerComponent { get; set; }
    public bool SignedComponent { get; set; }
}
