using AwesomeAssertions;
using Pdfe.Core.Filters.Jbig2;
using Xunit;

namespace Pdfe.Core.Tests.Filters.Jbig2;

public class Jbig2CapabilityClassifierTests
{
    private static readonly byte[] FileHeaderId =
    {
        0x97, 0x4A, 0x42, 0x32, 0x0D, 0x0A, 0x1A, 0x0A
    };

    private static byte[] BuildSegment(uint segmentNumber, SegmentType type, byte[] segmentData, uint pageNumber = 1)
    {
        var header = new[]
        {
            (byte)(segmentNumber >> 24),
            (byte)(segmentNumber >> 16),
            (byte)(segmentNumber >> 8),
            (byte)segmentNumber,
            (byte)type,
            (byte)0,
            (byte)pageNumber,
            (byte)(segmentData.Length >> 24),
            (byte)(segmentData.Length >> 16),
            (byte)(segmentData.Length >> 8),
            (byte)segmentData.Length,
        };

        byte[] result = new byte[header.Length + segmentData.Length];
        Array.Copy(header, 0, result, 0, header.Length);
        Array.Copy(segmentData, 0, result, header.Length, segmentData.Length);
        return result;
    }

    private static byte[] BuildSegment(uint segmentNumber, SegmentType type, uint pageNumber = 1)
        => BuildSegment(segmentNumber, type, Array.Empty<byte>(), pageNumber);

    private static byte[] BuildGenericRefinementRegionBody(bool typicalPrediction)
    {
        var body = new List<byte>
        {
            0, 0, 0, 1, // region width
            0, 0, 0, 1, // region height
            0, 0, 0, 0, // x
            0, 0, 0, 0, // y
            0x04,       // region combination operator: Replace
            (byte)(typicalPrediction ? 0x02 : 0x00), // template 0, optional TPGRON
            0xFF, 0xFF, // default generic refinement AT pixel 0: disabled
            0xFF, 0xFF, // default generic refinement AT pixel 1: disabled
        };

        return body.ToArray();
    }

    private static byte[] BuildTextRegionBody(bool huffmanEncoded, bool useRefinement)
    {
        ushort flags = (ushort)((huffmanEncoded ? 0x0001 : 0) | (useRefinement ? 0x0002 : 0));
        var body = new List<byte>
        {
            0, 0, 0, 1, // region width
            0, 0, 0, 1, // region height
            0, 0, 0, 0, // x
            0, 0, 0, 0, // y
            0x04,       // region combination operator: Replace
            (byte)(flags >> 8),
            (byte)flags,
        };
        if (huffmanEncoded)
            body.AddRange([0x00, 0x00]);
        if (useRefinement)
        {
            body.AddRange([
                0xFF, 0xFF, // default text refinement AT pixel 0: disabled
                0xFF, 0xFF, // default text refinement AT pixel 1: disabled
            ]);
        }

        body.AddRange([0, 0, 0, 1]); // one symbol instance
        return body.ToArray();
    }

    [Fact]
    public void Analyze_ClassifiesArithmeticSymbolAndTextRegionsAsSupportedBuckets()
    {
        byte[] arithmeticSymbolDictionary =
        [
            0x00, 0x00,             // arithmetic, no refinement
            0x03, 0xFF,             // template 0 adaptive pixels
            0xFD, 0xFF,
            0x02, 0xFE,
            0xFE, 0xFE,
            0x00, 0x00, 0x00, 0x00, // exported symbols
            0x00, 0x00, 0x00, 0x00, // new symbols
        ];
        byte[] arithmeticTextRegion =
        [
            0, 0, 0, 8, // region width
            0, 0, 0, 1, // region height
            0, 0, 0, 0, // x
            0, 0, 0, 0, // y
            0x04,       // region combination operator: Replace
            0x00, 0x00, // arithmetic text flags, no refinement
            0x00, 0x00, 0x00, 0x00, // zero symbol instances
        ];

        byte[] data = BuildSegment(1, SegmentType.SymbolDictionary, arithmeticSymbolDictionary)
            .Concat(BuildSegment(2, SegmentType.ImmediateTextRegion, arithmeticTextRegion))
            .ToArray();

        var report = Jbig2CapabilityClassifier.Analyze(data);

        report.Features.Should().Contain("symbol-dictionary.arithmetic");
        report.Features.Should().Contain("text-region.arithmetic");
        report.UnsupportedFeatures.Should().BeEmpty();
        report.SegmentTypeCounts["SymbolDictionary"].Should().Be(1);
        report.SegmentTypeCounts["ImmediateTextRegion"].Should().Be(1);
    }

    [Fact]
    public void Analyze_ClassifiesRetainedArithmeticSymbolContextsAsUnsupported()
    {
        byte[] arithmeticSymbolDictionary =
        [
            0x03, 0x00,             // arithmetic with context used/retained
            0x03, 0xFF,             // template 0 adaptive pixels
            0xFD, 0xFF,
            0x02, 0xFE,
            0xFE, 0xFE,
            0x00, 0x00, 0x00, 0x00, // exported symbols
            0x00, 0x00, 0x00, 0x00, // new symbols
        ];

        var report = Jbig2CapabilityClassifier.Analyze(
            BuildSegment(1, SegmentType.SymbolDictionary, arithmeticSymbolDictionary));

        report.Features.Should().Contain("symbol-dictionary.context-used");
        report.Features.Should().Contain("symbol-dictionary.context-retained");
        report.UnsupportedFeatures.Should().Contain("symbol-dictionary.context-used");
        report.UnsupportedFeatures.Should().Contain("symbol-dictionary.context-retained");
    }

    [Fact]
    public void Analyze_ClassifiesSupportedHuffmanCustomSelectorsWithoutUnsupportedBucket()
    {
        byte[] customHuffmanSymbolDictionary =
        [
            0x00, 0x7D,             // Huffman with user DH/DW/BMSIZE selectors
            0x00, 0x00, 0x00, 0x01, // exported symbols
            0x00, 0x00, 0x00, 0x01, // new symbols
        ];

        var report = Jbig2CapabilityClassifier.Analyze(
            BuildSegment(1, SegmentType.SymbolDictionary, customHuffmanSymbolDictionary));

        report.Features.Should().Contain("symbol-dictionary.huffman");
        report.Features.Should().Contain("symbol-dictionary.huffman.user-dh");
        report.Features.Should().Contain("symbol-dictionary.huffman.user-dw");
        report.Features.Should().Contain("symbol-dictionary.huffman.user-bmsize");
        report.UnsupportedFeatures.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_ClassifiesGenericRefinementWithoutTpgronAsSupported()
    {
        var report = Jbig2CapabilityClassifier.Analyze(BuildSegment(
            1,
            SegmentType.ImmediateGenericRefinementRegion,
            BuildGenericRefinementRegionBody(typicalPrediction: false)));

        report.Features.Should().Contain("generic-refinement-region");
        report.Features.Should().Contain("generic-refinement-region.template-0");
        report.Features.Should().Contain("generic-refinement-region.adaptive-template-pixels");
        report.UnsupportedFeatures.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_ClassifiesGenericRefinementTpgronAsSpecificUnsupportedBucket()
    {
        var report = Jbig2CapabilityClassifier.Analyze(BuildSegment(
            1,
            SegmentType.ImmediateGenericRefinementRegion,
            BuildGenericRefinementRegionBody(typicalPrediction: true)));

        report.Features.Should().Contain("generic-refinement-region.typical-prediction");
        report.UnsupportedFeatures.Should().Contain("generic-refinement-region.typical-prediction");
        report.UnsupportedFeatures.Should().NotContain("generic-refinement-region");
    }

    [Fact]
    public void Analyze_ClassifiesArithmeticTextRefinementAsSupported()
    {
        var report = Jbig2CapabilityClassifier.Analyze(BuildSegment(
            1,
            SegmentType.ImmediateTextRegion,
            BuildTextRegionBody(huffmanEncoded: false, useRefinement: true)));

        report.Features.Should().Contain("text-region.refinement");
        report.UnsupportedFeatures.Should().NotContain("text-region.refinement");
        report.UnsupportedFeatures.Should().NotContain("text-region.refinement.huffman");
    }

    [Fact]
    public void Analyze_ClassifiesHuffmanTextRefinementAsSpecificUnsupportedBucket()
    {
        var report = Jbig2CapabilityClassifier.Analyze(BuildSegment(
            1,
            SegmentType.ImmediateTextRegion,
            BuildTextRegionBody(huffmanEncoded: true, useRefinement: true)));

        report.Features.Should().Contain("text-region.refinement");
        report.UnsupportedFeatures.Should().Contain("text-region.refinement.huffman");
        report.UnsupportedFeatures.Should().NotContain("text-region.refinement");
    }

    [Fact]
    public void Analyze_NormalizesSequentialFileHeader()
    {
        byte[] header = FileHeaderId.Concat(new byte[] { 0x03 }).ToArray(); // sequential, page count unknown
        byte[] data = header.Concat(BuildSegment(1, SegmentType.EndOfFile)).ToArray();

        var report = Jbig2CapabilityClassifier.Analyze(data);

        report.Features.Should().Contain("end-of-file");
        report.Diagnostics.Should().BeEmpty();
    }
}
