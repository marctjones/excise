using System;

namespace Pdfe.Core.Filters.Jbig2;

/// <summary>
/// Decodes JBIG2 generic regions (ISO 14492 Section 6.2).
/// Generic regions use context-based arithmetic coding to encode arbitrary binary images.
/// </summary>
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
internal class GenericRegionDecoder
{
    /// <summary>Template used for context computation (0-3)</summary>
    private int _template;

    /// <summary>Is typical prediction optimization enabled?</summary>
    private bool _typicalPredictionGenericDecodingOn;

    private bool _isMmrEncoded;
    private Jbig2AdaptiveTemplatePixel[] _adaptiveTemplatePixels = DefaultTemplate0AdaptivePixels();

    /// <summary>
    /// Decode a generic region.
    /// ISO 14492 Section 6.2.3 - Generic Region Segment Data.
    /// </summary>
    public byte[] DecodeGenericRegion(byte[] regionData, int width, int height, int x, int y)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentException("Invalid region dimensions");
        if (_isMmrEncoded)
            return DecodeMmrGenericRegion(regionData, width, height);
        if (_template != 0)
            throw new NotSupportedException($"JBIG2 generic region template {_template} is not yet supported");
        if (_typicalPredictionGenericDecodingOn)
            throw new NotSupportedException("JBIG2 generic-region typical prediction is not yet supported");
        if (!UsesDefaultTemplate0AdaptivePixels())
            throw new NotSupportedException("Custom JBIG2 generic-region adaptive template pixels are not yet supported");

        var decoder = new Jbig2MqArithmeticDecoder(
            regionData,
            Jbig2ArithmeticGenericRegionDecoder.ContextCount);
        return Jbig2ArithmeticGenericRegionDecoder.Decode(
            decoder,
            width,
            height,
            _template,
            _adaptiveTemplatePixels).Data;
    }

    private static byte[] DecodeMmrGenericRegion(byte[] regionData, int width, int height)
    {
        return Jbig2MmrDecoder.Decode(regionData, width, height);
    }

    /// <summary>
    /// Parse generic region segment flags.
    /// ISO 14492 Section 6.2.1.
    /// </summary>
    public void ParseFlags(byte flags)
    {
        _isMmrEncoded = (flags & 0x01) != 0;
        _template = (flags >> 1) & 0x03;
        _typicalPredictionGenericDecodingOn = (flags & 0x08) != 0;
        bool usesExtendedTemplates = (flags & 0x10) != 0;
        _adaptiveTemplatePixels = GetDefaultAdaptiveTemplatePixels(_template, usesExtendedTemplates, _isMmrEncoded);
    }

    public void Configure(Jbig2GenericRegionSegment segment)
    {
        _isMmrEncoded = segment.IsMmrEncoded;
        _template = segment.Template;
        _typicalPredictionGenericDecodingOn = segment.TypicalPredictionGenericDecodingOn;
        _adaptiveTemplatePixels = segment.AdaptiveTemplatePixels;
    }

    private bool UsesDefaultTemplate0AdaptivePixels()
    {
        var defaults = DefaultTemplate0AdaptivePixels();
        if (_adaptiveTemplatePixels.Length != defaults.Length)
            return false;

        for (int i = 0; i < defaults.Length; i++)
        {
            if (_adaptiveTemplatePixels[i] != defaults[i])
                return false;
        }

        return true;
    }

    private static Jbig2AdaptiveTemplatePixel[] GetDefaultAdaptiveTemplatePixels(int template, bool usesExtendedTemplates, bool isMmrEncoded)
    {
        if (isMmrEncoded)
            return Array.Empty<Jbig2AdaptiveTemplatePixel>();
        if (template != 0 || usesExtendedTemplates)
            return Array.Empty<Jbig2AdaptiveTemplatePixel>();

        return DefaultTemplate0AdaptivePixels();
    }

    private static Jbig2AdaptiveTemplatePixel[] DefaultTemplate0AdaptivePixels()
        =>
        [
            new(3, -1),
            new(-3, -1),
            new(2, -2),
            new(-2, -2),
        ];
}
