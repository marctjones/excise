using System;
using System.Collections.Generic;
using System.Linq;

namespace Pdfe.Core.Filters.Jbig2;

internal sealed class Jbig2CapabilityReport
{
    public Jbig2CapabilityReport(
        IReadOnlyList<Jbig2CapabilitySegment> segments,
        IReadOnlyList<string> features,
        IReadOnlyList<string> unsupportedFeatures,
        IReadOnlyDictionary<string, int> segmentTypeCounts,
        IReadOnlyList<string> diagnostics)
    {
        Segments = segments;
        Features = features;
        UnsupportedFeatures = unsupportedFeatures;
        SegmentTypeCounts = segmentTypeCounts;
        Diagnostics = diagnostics;
    }

    public IReadOnlyList<Jbig2CapabilitySegment> Segments { get; }
    public IReadOnlyList<string> Features { get; }
    public IReadOnlyList<string> UnsupportedFeatures { get; }
    public IReadOnlyDictionary<string, int> SegmentTypeCounts { get; }
    public IReadOnlyList<string> Diagnostics { get; }
}

internal sealed class Jbig2CapabilitySegment
{
    public uint SegmentNumber { get; init; }
    public string SegmentType { get; init; } = "";
    public int SegmentTypeCode { get; init; }
    public uint PageNumber { get; init; }
    public IReadOnlyList<uint> ReferredSegments { get; init; } = Array.Empty<uint>();
    public IReadOnlyList<string> Features { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> UnsupportedFeatures { get; init; } = Array.Empty<string>();
    public string? Diagnostic { get; init; }
}

internal static class Jbig2CapabilityClassifier
{
    public static Jbig2CapabilityReport Analyze(byte[] data, byte[]? globals = null)
    {
        if (data == null)
            throw new ArgumentNullException(nameof(data));

        var diagnostics = new List<string>();
        byte[] allData;
        try
        {
            allData = Jbig2StreamNormalizer.CombineGlobalsAndData(
                globals != null ? Jbig2StreamNormalizer.NormalizeFileHeader(globals) : null,
                Jbig2StreamNormalizer.NormalizeFileHeader(data));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            diagnostics.Add($"{ex.GetType().Name}: {ex.Message}");
            return new Jbig2CapabilityReport(
                Array.Empty<Jbig2CapabilitySegment>(),
                Array.Empty<string>(),
                Array.Empty<string>(),
                new Dictionary<string, int>(),
                diagnostics);
        }

        var segments = new List<Jbig2CapabilitySegment>();
        var featureSet = new SortedSet<string>(StringComparer.Ordinal);
        var unsupportedSet = new SortedSet<string>(StringComparer.Ordinal);
        var typeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var parser = new SegmentHeaderParser(allData);

        while (parser.RemainingBytes > 0)
        {
            SegmentHeader? header;
            try
            {
                header = parser.ParseSegmentHeader();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                diagnostics.Add($"segment-header@{parser.Position}: {ex.GetType().Name}: {ex.Message}");
                break;
            }

            if (header == null)
                break;

            string typeName = GetSegmentTypeName(header.SegmentType);
            typeCounts.TryGetValue(typeName, out var count);
            typeCounts[typeName] = count + 1;

            int dataLength = 0;
            string? diagnostic = null;
            if (header.DataLength > int.MaxValue)
            {
                diagnostic = $"Segment data length {header.DataLength} exceeds supported limits";
            }
            else
            {
                dataLength = (int)header.DataLength;
            }

            var segmentFeatures = new SortedSet<string>(StringComparer.Ordinal);
            var segmentUnsupported = new SortedSet<string>(StringComparer.Ordinal);

            if (diagnostic == null)
            {
                var segmentData = ExtractSegmentData(allData, header.DataOffset, dataLength);
                try
                {
                    ClassifySegment(header, segmentData, segmentFeatures, segmentUnsupported);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    diagnostic = $"{ex.GetType().Name}: {ex.Message}";
                }
            }

            foreach (var feature in segmentFeatures)
                featureSet.Add(feature);
            foreach (var unsupported in segmentUnsupported)
                unsupportedSet.Add(unsupported);

            if (diagnostic != null)
                diagnostics.Add($"segment {header.SegmentNumber} {typeName}: {diagnostic}");

            segments.Add(new Jbig2CapabilitySegment
            {
                SegmentNumber = header.SegmentNumber,
                SegmentType = typeName,
                SegmentTypeCode = header.SegmentType,
                PageNumber = header.PageNumber,
                ReferredSegments = header.ReferredSegments.ToArray(),
                Features = segmentFeatures.ToArray(),
                UnsupportedFeatures = segmentUnsupported.ToArray(),
                Diagnostic = diagnostic,
            });

            if (header.DataLength > 0)
                parser.SetPosition(header.DataOffset + dataLength);
        }

        return new Jbig2CapabilityReport(
            segments,
            featureSet.ToArray(),
            unsupportedSet.ToArray(),
            typeCounts.OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal),
            diagnostics);
    }

    private static void ClassifySegment(
        SegmentHeader header,
        byte[] data,
        ISet<string> features,
        ISet<string> unsupported)
    {
        switch ((SegmentType)header.SegmentType)
        {
            case SegmentType.SymbolDictionary:
                ClassifySymbolDictionary(data, features, unsupported);
                break;
            case SegmentType.TextRegion:
            case SegmentType.ImmediateTextRegion:
            case SegmentType.ImmediateLosslessTextRegion:
                ClassifyTextRegion(data, features, unsupported);
                break;
            case SegmentType.PatternDictionary:
                ClassifyPatternDictionary(data, features, unsupported);
                break;
            case SegmentType.HalftoneRegion:
            case SegmentType.ImmediateHalftoneRegion:
            case SegmentType.ImmediateLosslessHalftoneRegion:
                ClassifyHalftoneRegion(data, features, unsupported);
                break;
            case SegmentType.GenericRegion:
            case SegmentType.ImmediateGenericRegion:
            case SegmentType.ImmediateLosslessGenericRegion:
                ClassifyGenericRegion(data, features, unsupported);
                break;
            case SegmentType.GenericRefinementRegion:
            case SegmentType.ImmediateGenericRefinementRegion:
            case SegmentType.ImmediateLosslessGenericRefinementRegion:
                ClassifyGenericRefinementRegion(data, features, unsupported);
                break;
            case SegmentType.PageInformation:
                ClassifyPageInformation(data, features);
                break;
            case SegmentType.Table:
                features.Add("huffman-table");
                Jbig2HuffmanTableSegment.Parse(data);
                break;
            case SegmentType.EndOfPage:
                features.Add("end-of-page");
                break;
            case SegmentType.EndOfStripe:
                features.Add("end-of-stripe");
                break;
            case SegmentType.EndOfFile:
                features.Add("end-of-file");
                break;
            case SegmentType.ProfileSegment:
                features.Add("profile-segment");
                break;
            default:
                features.Add("unknown-segment");
                unsupported.Add("unknown-segment");
                break;
        }
    }

    private static void ClassifySymbolDictionary(byte[] data, ISet<string> features, ISet<string> unsupported)
    {
        var segment = Jbig2SymbolDictionarySegment.Parse(data);
        features.Add("symbol-dictionary");
        features.Add(segment.IsHuffmanEncoded ? "symbol-dictionary.huffman" : "symbol-dictionary.arithmetic");
        features.Add($"symbol-dictionary.template-{segment.SdTemplate}");
        features.Add($"symbol-dictionary.refinement-template-{segment.SdrTemplate}");

        if (!segment.IsHuffmanEncoded && !UsesDefaultTemplate0AdaptivePixels(segment.AdaptiveTemplatePixels))
            features.Add("symbol-dictionary.arithmetic.custom-at");
        if (segment.UseRefinementAggregation)
        {
            features.Add("symbol-dictionary.refinement-aggregation");
            unsupported.Add("symbol-dictionary.refinement-aggregation");
        }
        if (segment.IsCodingContextUsed)
        {
            features.Add("symbol-dictionary.context-used");
            unsupported.Add("symbol-dictionary.context-used");
        }
        if (segment.IsCodingContextRetained)
        {
            features.Add("symbol-dictionary.context-retained");
            unsupported.Add("symbol-dictionary.context-retained");
        }
        if (segment.AdaptiveTemplatePixels.Length > 0)
            features.Add("symbol-dictionary.adaptive-template-pixels");
        if (segment.RefinementAdaptiveTemplatePixels.Length > 0)
            features.Add("symbol-dictionary.refinement-adaptive-template-pixels");
        if (segment.IsHuffmanEncoded)
        {
            if (segment.SdHuffDecodeHeightSelection == 3)
                features.Add("symbol-dictionary.huffman.user-dh");
            if (segment.SdHuffDecodeWidthSelection == 3)
                features.Add("symbol-dictionary.huffman.user-dw");
            if (segment.SdHuffBmSizeSelection == 1)
                features.Add("symbol-dictionary.huffman.user-bmsize");
            if (segment.SdHuffAggInstanceSelection == 1)
                features.Add("symbol-dictionary.huffman.user-agginst");
        }
    }

    private static void ClassifyTextRegion(byte[] data, ISet<string> features, ISet<string> unsupported)
    {
        var segment = Jbig2TextRegionSegment.Parse(data);
        features.Add("text-region");
        features.Add(segment.IsHuffmanEncoded ? "text-region.huffman" : "text-region.arithmetic");
        features.Add($"text-region.reference-corner-{segment.ReferenceCorner}");
        features.Add($"text-region.log-strips-{segment.LogSbStrips}");
        features.Add($"text-region.refinement-template-{segment.SbrTemplate}");

        if (segment.UseRefinement)
        {
            features.Add("text-region.refinement");
            unsupported.Add("text-region.refinement");
        }
        if (segment.IsTransposed)
            features.Add("text-region.transposed");
        if (segment.DefaultPixel != 0)
            features.Add("text-region.default-pixel-1");
        if (segment.RefinementAdaptiveTemplatePixels.Length > 0)
            features.Add("text-region.refinement-adaptive-template-pixels");
        if (segment.HuffmanFlags is { } huffmanFlags)
        {
            AddTextHuffmanSelectorFeatures(huffmanFlags, features);
        }
    }

    private static void AddTextHuffmanSelectorFeatures(Jbig2TextRegionHuffmanFlags flags, ISet<string> features)
    {
        if (flags.SbHuffFs == 3)
            features.Add("text-region.huffman.user-fs");
        if (flags.SbHuffDs == 3)
            features.Add("text-region.huffman.user-ds");
        if (flags.SbHuffDt == 3)
            features.Add("text-region.huffman.user-dt");
        if (flags.SbHuffRdWidth == 3)
            features.Add("text-region.huffman.user-rdw");
        if (flags.SbHuffRdHeight == 3)
            features.Add("text-region.huffman.user-rdh");
        if (flags.SbHuffRdx == 3)
            features.Add("text-region.huffman.user-rdx");
        if (flags.SbHuffRdy == 3)
            features.Add("text-region.huffman.user-rdy");
        if (flags.SbHuffRSize == 1)
            features.Add("text-region.huffman.user-rsize");
    }

    private static void ClassifyGenericRegion(byte[] data, ISet<string> features, ISet<string> unsupported)
    {
        var segment = Jbig2GenericRegionSegment.Parse(data);
        features.Add("generic-region");
        features.Add(segment.IsMmrEncoded ? "generic-region.mmr" : "generic-region.arithmetic");
        features.Add($"generic-region.template-{segment.Template}");

        if (segment.UsesExtendedTemplates)
            features.Add("generic-region.extended-templates");
        if (segment.TypicalPredictionGenericDecodingOn)
            features.Add("generic-region.typical-prediction");
        if (!segment.IsMmrEncoded && UsesCustomGenericAdaptiveTemplatePixels(segment))
            features.Add("generic-region.custom-at");
    }

    private static bool UsesCustomGenericAdaptiveTemplatePixels(Jbig2GenericRegionSegment segment)
    {
        if (segment.AdaptiveTemplatePixels.Length == 0)
            return false;
        return segment.Template != 0 || !UsesDefaultTemplate0AdaptivePixels(segment.AdaptiveTemplatePixels);
    }

    private static bool UsesDefaultTemplate0AdaptivePixels(IReadOnlyList<Jbig2AdaptiveTemplatePixel> pixels)
    {
        var defaults = new[]
        {
            new Jbig2AdaptiveTemplatePixel(3, -1),
            new Jbig2AdaptiveTemplatePixel(-3, -1),
            new Jbig2AdaptiveTemplatePixel(2, -2),
            new Jbig2AdaptiveTemplatePixel(-2, -2),
        };

        if (pixels.Count != defaults.Length)
            return false;

        for (int i = 0; i < defaults.Length; i++)
        {
            if (pixels[i] != defaults[i])
                return false;
        }

        return true;
    }

    private static void ClassifyGenericRefinementRegion(byte[] data, ISet<string> features, ISet<string> unsupported)
    {
        var segment = Jbig2GenericRefinementRegionSegment.Parse(data);
        features.Add("generic-refinement-region");
        features.Add($"generic-refinement-region.template-{segment.Template}");
        if (segment.TypicalPredictionGenericRefinementOn)
        {
            features.Add("generic-refinement-region.typical-prediction");
            unsupported.Add("generic-refinement-region.typical-prediction");
        }
        if (segment.AdaptiveTemplatePixels.Length > 0)
            features.Add("generic-refinement-region.adaptive-template-pixels");
    }

    private static void ClassifyPatternDictionary(byte[] data, ISet<string> features, ISet<string> unsupported)
    {
        var segment = Jbig2PatternDictionarySegment.Parse(data);
        features.Add("pattern-dictionary");
        features.Add(segment.IsMmrEncoded ? "pattern-dictionary.mmr" : "pattern-dictionary.arithmetic");
        features.Add($"pattern-dictionary.template-{segment.Template}");
        unsupported.Add("pattern-dictionary");
    }

    private static void ClassifyHalftoneRegion(byte[] data, ISet<string> features, ISet<string> unsupported)
    {
        var segment = Jbig2HalftoneRegionSegment.Parse(data);
        features.Add("halftone-region");
        features.Add(segment.IsMmrEncoded ? "halftone-region.mmr" : "halftone-region.arithmetic");
        features.Add($"halftone-region.template-{segment.Template}");
        unsupported.Add("halftone-region");
        if (segment.SkipEnabled)
            features.Add("halftone-region.skip");
        if (segment.DefaultPixel != 0)
            features.Add("halftone-region.default-pixel-1");
    }

    private static void ClassifyPageInformation(byte[] data, ISet<string> features)
    {
        var page = Jbig2PageInformation.Parse(data);
        features.Add("page-information");
        if (page.DefaultPixelValue != 0)
            features.Add("page-information.default-pixel-1");
        if (page.CombinationOperatorOverrideAllowed)
            features.Add("page-information.combination-override");
        if (page.RequiresAuxiliaryBuffer)
            features.Add("page-information.auxiliary-buffer");
        if (page.MightContainRefinements)
            features.Add("page-information.might-contain-refinements");
        if (page.IsLossless)
            features.Add("page-information.lossless");
        if (page.IsStriped)
            features.Add("page-information.striped");
    }

    private static string GetSegmentTypeName(int segmentType)
        => Enum.IsDefined(typeof(SegmentType), segmentType)
            ? ((SegmentType)segmentType).ToString()
            : $"Unknown{segmentType}";

    private static byte[] ExtractSegmentData(byte[] data, int offset, int length)
    {
        if (length == 0)
            return Array.Empty<byte>();

        int actualLen = Math.Min(length, data.Length - offset);
        if (actualLen <= 0)
            return Array.Empty<byte>();

        byte[] result = new byte[actualLen];
        Array.Copy(data, offset, result, 0, actualLen);
        return result;
    }
}
