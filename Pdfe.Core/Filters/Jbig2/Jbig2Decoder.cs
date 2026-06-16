using System;
using System.Collections.Generic;

namespace Pdfe.Core.Filters.Jbig2;

/// <summary>
/// JBIG2 decoder for PDF's /JBIG2Decode filter.
/// Implements ISO 14492 (JBIG2 standard) for pure-managed .NET.
/// ISO 32000-2:2020 Section 8.3.6 covers PDF JBIG2 integration.
///
/// Supported features:
/// - Generic region decoding (template 0 arithmetic and MMR)
/// - Huffman symbol dictionaries without refinement/aggregation
/// - Huffman text regions without refinement
/// - Standard and referenced user Huffman tables for supported symbol/text paths
/// - Segment header parsing for embedded organization (Annex D.3)
/// - Typed metadata parsing for page, region, symbol dictionary, and text region bodies
///
/// Not yet implemented:
/// - Arithmetic-coded symbol dictionaries and text regions
/// - Symbol/text refinement and aggregate coding
/// - Refinement regions (segment type 37)
/// - Halftone regions
/// - Optional generic-region templates 1-3, custom AT pixels, and TPGDON
/// </summary>
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
internal static class Jbig2Decoder
{
    private static readonly byte[] FileHeaderId =
    {
        0x97, 0x4A, 0x42, 0x32, 0x0D, 0x0A, 0x1A, 0x0A
    };

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

        // Combine globals and page-specific data for segment parsing. PDF
        // JBIG2 streams normally use embedded organization without the JBIG2
        // file header, but standalone JBIG2 payloads can include the Annex D
        // header. Match PDFBox's boundary by accepting sequential headers and
        // rejecting random-access organization until segment data offsets are
        // modeled explicitly.
        byte[] allData = CombineGlobalsAndData(
            globals != null ? NormalizeFileHeader(globals) : null,
            NormalizeFileHeader(data));

        // Parse segments and extract page-level generic regions
        var decoder = new Jbig2PageDecoder(width, height);
        byte[] pageImage = decoder.DecodePage(allData);

        InvertForPdfImageSamples(pageImage);
        return pageImage;
    }

    private static void InvertForPdfImageSamples(byte[] pageImage)
    {
        // JBIG2 coding procedures model 1 bits as black pixels. Once the
        // /JBIG2Decode filter has produced image samples for a PDF image
        // XObject, DeviceGray's default Decode array maps 0 to black and
        // 1 to white. Keep the internal JBIG2 convention during composition
        // and invert only at the filter boundary.
        for (int i = 0; i < pageImage.Length; i++)
            pageImage[i] = (byte)~pageImage[i];
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

    private static byte[] NormalizeFileHeader(byte[] data)
    {
        if (!HasFileHeader(data))
            return data;

        if (data.Length < 9)
            throw new InvalidOperationException("Truncated JBIG2 file header");

        var headerFlags = data[8];
        var isSequential = (headerFlags & 0x01) != 0;
        if (!isSequential)
            throw new NotSupportedException("Random-access JBIG2 file organization is not supported");

        var amountOfPagesUnknown = (headerFlags & 0x02) != 0;
        var headerLength = amountOfPagesUnknown ? 9 : 13;
        if (data.Length < headerLength)
            throw new InvalidOperationException("Truncated JBIG2 page-count field");

        var normalized = new byte[data.Length - headerLength];
        Array.Copy(data, headerLength, normalized, 0, normalized.Length);
        return normalized;
    }

    private static bool HasFileHeader(byte[] data)
    {
        if (data.Length < FileHeaderId.Length)
            return false;

        for (var i = 0; i < FileHeaderId.Length; i++)
        {
            if (data[i] != FileHeaderId[i])
                return false;
        }

        return true;
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
    private Jbig2PageInformation? _pageInformation;

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
                Jbig2GenericRefinementRegionSegment.Parse(data);
                throw new NotSupportedException("Generic refinement region segments are not yet supported");

            case SegmentType.SymbolDictionary:
                CacheSymbolDictionary(header, data);
                break;

            case SegmentType.TextRegion:
                CacheTextRegion(header, data);
                break;

            case SegmentType.ImmediateTextRegion:
            case SegmentType.ImmediateLosslessTextRegion:
                DecodeTextRegion(header, data, pageImage);
                break;

            case SegmentType.PatternDictionary:
                CachePatternDictionary(header, data);
                break;

            case SegmentType.HalftoneRegion:
            case SegmentType.ImmediateHalftoneRegion:
            case SegmentType.ImmediateLosslessHalftoneRegion:
                Jbig2HalftoneRegionSegment.Parse(data);
                throw new NotSupportedException($"Segment type {header.SegmentType} is not supported");

            case SegmentType.PageInformation:
                _pageInformation = Jbig2PageInformation.Parse(data);
                if (_pageInformation.Value.DefaultPixelValue != 0)
                    Array.Fill(pageImage, (byte)0xFF);
                break;

            case SegmentType.EndOfPage:
            case SegmentType.EndOfStripe:
            case SegmentType.EndOfFile:
            case SegmentType.ProfileSegment:
                // These are metadata/control segments; skip
                break;

            case SegmentType.Table:
                CacheHuffmanTable(header, data);
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
        var segment = Jbig2GenericRegionSegment.Parse(segmentData);
        if (segment.Region.BitmapWidth > int.MaxValue || segment.Region.BitmapHeight > int.MaxValue)
            throw new InvalidOperationException("JBIG2 generic region dimensions exceed supported limits");
        if (segment.Region.XLocation > int.MaxValue || segment.Region.YLocation > int.MaxValue)
            throw new InvalidOperationException("JBIG2 generic region coordinates exceed supported limits");

        var regionDecoder = new GenericRegionDecoder();
        regionDecoder.Configure(segment);

        // Decode the region image
        byte[] bitmapData = segmentData.AsSpan(segment.BitmapDataOffset, segment.BitmapDataLength).ToArray();
        byte[] regionImage = regionDecoder.DecodeGenericRegion(
            bitmapData,
            (int)segment.Region.BitmapWidth,
            (int)segment.Region.BitmapHeight,
            (int)segment.Region.XLocation,
            (int)segment.Region.YLocation);

        var combinationOperator = segment.Region.CombinationOperator;
        if (_pageInformation is { CombinationOperatorOverrideAllowed: false } pageInformation)
            combinationOperator = pageInformation.CombinationOperator;

        Jbig2BitmapCompositor.Composite(
            pageImage,
            _width,
            _height,
            regionImage,
            (int)segment.Region.BitmapWidth,
            (int)segment.Region.BitmapHeight,
            (int)segment.Region.XLocation,
            (int)segment.Region.YLocation,
            combinationOperator);
    }

    private void CacheSymbolDictionary(SegmentHeader header, byte[] data)
    {
        var segment = Jbig2SymbolDictionarySegment.Parse(data);
        var decoded = Jbig2SymbolDictionaryDecoder.Decode(
            segment,
            data.AsSpan(segment.PayloadDataOffset, segment.PayloadDataLength),
            ResolveImportedSymbols(header),
            ResolveUserHuffmanTables(header));

        _segments[header.SegmentNumber] = new SegmentData
        {
            Data = data,
            Type = (SegmentType)header.SegmentType,
            SymbolDictionary = segment,
            DecodedSymbolDictionary = decoded,
        };
    }

    private IReadOnlyList<Jbig2Bitmap> ResolveImportedSymbols(SegmentHeader header)
    {
        if (header.ReferredSegments.Count == 0)
            return Array.Empty<Jbig2Bitmap>();

        var symbols = new List<Jbig2Bitmap>();
        foreach (uint segmentNumber in header.ReferredSegments)
        {
            if (!_segments.TryGetValue(segmentNumber, out var segment)
                || segment.DecodedSymbolDictionary == null)
                continue;

            symbols.AddRange(segment.DecodedSymbolDictionary.ExportedSymbols);
        }

        return symbols;
    }

    private IReadOnlyList<Jbig2HuffmanTable> ResolveUserHuffmanTables(SegmentHeader header)
    {
        if (header.ReferredSegments.Count == 0)
            return Array.Empty<Jbig2HuffmanTable>();

        var tables = new List<Jbig2HuffmanTable>();
        foreach (uint segmentNumber in header.ReferredSegments)
        {
            if (!_segments.TryGetValue(segmentNumber, out var segment)
                || segment.DecodedHuffmanTable == null)
                continue;

            tables.Add(segment.DecodedHuffmanTable);
        }

        return tables;
    }

    private void CacheTextRegion(SegmentHeader header, byte[] data)
    {
        var segment = Jbig2TextRegionSegment.Parse(data);
        var bitmap = Jbig2TextRegionDecoder.Decode(
            segment,
            data.AsSpan(segment.PayloadDataOffset, segment.PayloadDataLength),
            ResolveImportedSymbols(header),
            ResolveUserHuffmanTables(header));

        _segments[header.SegmentNumber] = new SegmentData
        {
            Data = data,
            Type = (SegmentType)header.SegmentType,
            TextRegion = segment,
            TextRegionBitmap = bitmap,
        };
    }

    private void DecodeTextRegion(SegmentHeader header, byte[] data, byte[] pageImage)
    {
        var segment = Jbig2TextRegionSegment.Parse(data);
        var bitmap = Jbig2TextRegionDecoder.Decode(
            segment,
            data.AsSpan(segment.PayloadDataOffset, segment.PayloadDataLength),
            ResolveImportedSymbols(header),
            ResolveUserHuffmanTables(header));

        if (segment.Region.XLocation > int.MaxValue || segment.Region.YLocation > int.MaxValue)
            throw new InvalidOperationException("JBIG2 text region coordinates exceed supported limits");

        var combinationOperator = segment.Region.CombinationOperator;
        if (_pageInformation is { CombinationOperatorOverrideAllowed: false } pageInformation)
            combinationOperator = pageInformation.CombinationOperator;

        Jbig2BitmapCompositor.Composite(
            new Jbig2Bitmap(_width, _height, pageImage),
            bitmap,
            (int)segment.Region.XLocation,
            (int)segment.Region.YLocation,
            combinationOperator);
    }

    private void CacheHuffmanTable(SegmentHeader header, byte[] data)
    {
        var segment = Jbig2HuffmanTableSegment.Parse(data);
        var decoded = Jbig2UserHuffmanTableBuilder.Build(
            segment,
            data.AsSpan(segment.PayloadDataOffset, segment.PayloadDataLength));

        _segments[header.SegmentNumber] = new SegmentData
        {
            Data = data,
            Type = (SegmentType)header.SegmentType,
            HuffmanTable = segment,
            DecodedHuffmanTable = decoded,
        };
    }

    private void CachePatternDictionary(SegmentHeader header, byte[] data)
    {
        var segment = Jbig2PatternDictionarySegment.Parse(data);
        _segments[header.SegmentNumber] = new SegmentData
        {
            Data = data,
            Type = (SegmentType)header.SegmentType,
            PatternDictionary = segment,
        };
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
    public Jbig2SymbolDictionarySegment? SymbolDictionary { get; set; }
    public Jbig2DecodedSymbolDictionary? DecodedSymbolDictionary { get; set; }
    public Jbig2TextRegionSegment? TextRegion { get; set; }
    public Jbig2Bitmap? TextRegionBitmap { get; set; }
    public Jbig2HuffmanTableSegment? HuffmanTable { get; set; }
    public Jbig2HuffmanTable? DecodedHuffmanTable { get; set; }
    public Jbig2PatternDictionarySegment? PatternDictionary { get; set; }
}
