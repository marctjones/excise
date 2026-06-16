using System;
using System.Collections.Generic;

namespace Pdfe.Core.Filters.Jbig2;

/// <summary>
/// JBIG2 decoder for PDF's /JBIG2Decode filter.
/// Implements ISO 14492 (JBIG2 standard) for pure-managed .NET.
/// ISO 32000-2:2020 Section 8.3.6 covers PDF JBIG2 integration.
///
/// Supported features:
/// - Generic region decoding (template 0)
/// - MQ arithmetic decoder with Qe-adaptive probability estimation
/// - Segment header parsing for embedded organization (Annex D.3)
///
/// Not yet implemented:
/// - Symbol dictionary decoding (segment type 0)
/// - Text region decoding (segment types 4-7)
/// - Refinement regions (segment type 37)
/// - Optional text region operators and templates 1-3
/// - Typical prediction (TPGDON optimization)
/// </summary>
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
internal static class Jbig2Decoder
{
    /// <summary>
    /// Decode JBIG2-encoded image data.
    /// </summary>
    /// <param name="data">The JBIG2Decode stream bytes from the PDF image stream.</param>
    /// <param name="globals">
    /// Optional JBIG2Globals stream (may be null). Contains global segments shared across pages.
    /// PDF specifies globals via /DecodeParms /JBIG2Globals pointing to a separate stream.
    /// </param>
    /// <param name="width">Image width in pixels (from /Width in image XObject dictionary).</param>
    /// <param name="height">Image height in pixels (from /Height in image XObject dictionary).</param>
    /// <returns>
    /// Decoded image as 1 bit-per-pixel, packed MSB-first, row-padded to byte boundary.
    /// Each row is padded with zero bits to reach a byte boundary.
    /// 1 = black (matches PDF's default photometric interpretation for JBIG2).
    /// </returns>
    /// <exception cref="NotSupportedException">
    /// Thrown if the data contains unsupported segment types (e.g., symbol dictionary, text region).
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the data is malformed or truncated.
    /// </exception>
    public static byte[] Decode(byte[] data, byte[]? globals, int width, int height)
    {
        if (data == null)
            throw new ArgumentNullException(nameof(data));
        if (width <= 0 || height <= 0)
            throw new ArgumentException("Width and height must be positive");

        // Combine globals and page-specific data for segment parsing
        byte[] allData = CombineGlobalsAndData(globals, data);

        // Parse segments and extract page-level generic regions
        var decoder = new Jbig2PageDecoder(width, height);
        byte[] pageImage = decoder.DecodePage(allData);

        return pageImage;
    }

    /// <summary>
    /// Combine global and page-specific JBIG2 data into a single stream for parsing.
    /// Per ISO 14492 Annex D.3 (embedded page organization):
    /// globals contain global segments, data contains page/end-of-page segments.
    /// </summary>
    private static byte[] CombineGlobalsAndData(byte[]? globals, byte[] data)
    {
        if (globals == null || globals.Length == 0)
            return data;

        byte[] combined = new byte[globals.Length + data.Length];
        Array.Copy(globals, 0, combined, 0, globals.Length);
        Array.Copy(data, 0, combined, globals.Length, data.Length);
        return combined;
    }
}

/// <summary>
/// Internal decoder for a single JBIG2 page.
/// </summary>
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
internal class Jbig2PageDecoder
{
    private readonly int _width;
    private readonly int _height;
    private readonly Dictionary<uint, SegmentData> _segments = new();

    public Jbig2PageDecoder(int width, int height)
    {
        _width = width;
        _height = height;
    }

    /// <summary>
    /// Decode a complete JBIG2 page from the combined global+page data.
    /// </summary>
    public byte[] DecodePage(byte[] data)
    {
        // Initialize page image (all white/0)
        int bytesPerRow = (_width + 7) / 8;
        byte[] pageImage = new byte[bytesPerRow * _height];

        // Parse all segments
        var parser = new SegmentHeaderParser(data);
        while (parser.RemainingBytes > 0)
        {
            SegmentHeader? header = parser.ParseSegmentHeader();
            if (header == null)
                break;

            try
            {
                // Extract segment data
                int dataLen = header.DataLength is > 0 and <= int.MaxValue
                    ? (int)header.DataLength
                    : EstimateRemainingLength(data, parser.Position);
                byte[] segmentData = ExtractSegmentData(data, header.DataOffset, dataLen);

                // Process the segment
                ProcessSegment(header, segmentData, pageImage);

                // Advance parser past the segment data
                if (header.DataLength > 0)
                    parser.SetPosition(header.DataOffset + (int)header.DataLength);
            }
            catch (NotSupportedException)
            {
                // Don't silently emit a blank/partial page. Propagate so the
                // caller (StreamDecompressor) falls back to the raw encoded
                // stream — better a skipped image than a silently-wrong one.
                throw;
            }
        }

        return pageImage;
    }

    /// <summary>
    /// Process a single segment and update the page image if applicable.
    /// </summary>
    private void ProcessSegment(SegmentHeader header, byte[] data, byte[] pageImage)
    {
        switch ((SegmentType)header.SegmentType)
        {
            case SegmentType.GenericRegion:
            case SegmentType.ImmediateGenericRegion:
            case SegmentType.ImmediateLosslessGenericRegion:
                DecodeGenericRegion(data, pageImage);
                break;

            case SegmentType.GenericRefinementRegion:
            case SegmentType.ImmediateGenericRefinementRegion:
            case SegmentType.ImmediateLosslessGenericRefinementRegion:
                // A refinement region is NOT a generic region — decoding it as
                // one would silently corrupt the image. Not yet supported.
                throw new NotSupportedException("Generic refinement region segments are not yet supported");

            case SegmentType.SymbolDictionary:
                // Symbol dictionaries are referenced by text regions; store but don't render directly
                _segments[header.SegmentNumber] = new SegmentData { Data = data, Type = (SegmentType)header.SegmentType };
                throw new NotSupportedException("Symbol dictionary segments are not yet supported");

            case SegmentType.TextRegion:
            case SegmentType.ImmediateTextRegion:
            case SegmentType.ImmediateLosslessTextRegion:
                throw new NotSupportedException("Text region segments are not yet supported");

            case SegmentType.PatternDictionary:
            case SegmentType.HalftoneRegion:
            case SegmentType.ImmediateHalftoneRegion:
            case SegmentType.ImmediateLosslessHalftoneRegion:
                throw new NotSupportedException($"Segment type {header.SegmentType} is not supported");

            case SegmentType.PageInformation:
            case SegmentType.EndOfPage:
            case SegmentType.EndOfStripe:
            case SegmentType.EndOfFile:
            case SegmentType.ProfileSegment:
            case SegmentType.Table:
                // These are metadata/control segments; skip
                break;

            default:
                throw new NotSupportedException($"Unknown segment type: {header.SegmentType}");
        }
    }

    /// <summary>
    /// Decode a generic region segment and composite it onto the page.
    /// </summary>
    private void DecodeGenericRegion(byte[] segmentData, byte[] pageImage)
    {
        if (segmentData.Length < 1)
            throw new InvalidOperationException("Generic region data too short");

        var regionDecoder = new GenericRegionDecoder();

        // Parse region flags (first byte)
        byte flags = segmentData[0];
        regionDecoder.ParseFlags(flags);

        // Decode the region image
        byte[] regionImage = regionDecoder.DecodeGenericRegion(segmentData, _width, _height, 0, 0);

        // Composite: OR the region onto the page (or use CombinationOperator from flags)
        int bytesPerRow = (_width + 7) / 8;
        for (int i = 0; i < regionImage.Length && i < pageImage.Length; i++)
        {
            pageImage[i] |= regionImage[i];
        }
    }

    /// <summary>
    /// Extract segment data from the combined buffer.
    /// </summary>
    private byte[] ExtractSegmentData(byte[] data, int offset, int length)
    {
        int actualLen = Math.Min(length, data.Length - offset);
        if (actualLen <= 0)
            return Array.Empty<byte>();

        byte[] result = new byte[actualLen];
        Array.Copy(data, offset, result, 0, actualLen);
        return result;
    }

    /// <summary>
    /// Estimate the remaining data length when segment header doesn't specify it.
    /// Used for streaming/unknown-length segments (rare in PDF context).
    /// </summary>
    private int EstimateRemainingLength(byte[] data, int currentPos)
    {
        // Conservative: return remaining bytes or a reasonable maximum
        return Math.Min(data.Length - currentPos, 1024 * 1024);
    }
}

/// <summary>
/// Cached segment data for reuse by dependent segments (e.g., text regions).
/// </summary>
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
internal class SegmentData
{
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public SegmentType Type { get; set; }
}
