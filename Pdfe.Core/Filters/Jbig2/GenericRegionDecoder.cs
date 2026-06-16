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
            throw new NotSupportedException("MMR-encoded JBIG2 generic regions are not yet supported");
        if (_template != 0)
            throw new NotSupportedException($"JBIG2 generic region template {_template} is not yet supported");
        if (_typicalPredictionGenericDecodingOn)
            throw new NotSupportedException("JBIG2 generic-region typical prediction is not yet supported");
        if (!UsesDefaultTemplate0AdaptivePixels())
            throw new NotSupportedException("Custom JBIG2 generic-region adaptive template pixels are not yet supported");

        // Allocate output: 1 bit per pixel, packed MSB-first, row-padded to byte boundary
        int bytesPerRow = (width + 7) / 8;
        byte[] output = new byte[bytesPerRow * height];

        // For now, implement template 0 with a simple context model
        DecodeWithTemplate0(regionData, width, height, output);

        return output;
    }

    /// <summary>
    /// Decode using template 0 (ISO 14492 Section 6.2.5.1).
    /// Template 0 uses 10 context pixels:
    ///   (x-2,y) (x-1,y) (x,y)
    ///   (x-2,y-1) (x-1,y-1) (x,y-1)
    ///   (x-3,y-1) (x-2,y-2) (x-1,y-2) (x,y-2)
    /// </summary>
    private void DecodeWithTemplate0(byte[] regionData, int width, int height, byte[] output)
    {
        var decoder = new MqDecoder(regionData);
        int bytesPerRow = (width + 7) / 8;

        // Context model: 10-bit context (2^10 = 1024 possible contexts)
        // For simplicity, we'll use a basic non-adaptive context
        int[] context = new int[1]; // Simplified: single context state

        // Decode pixel by pixel
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                // Build the context from the 10 neighboring pixels
                int ctx = BuildTemplate0Context(output, bytesPerRow, x, y, width, height);

                // Decode the current pixel
                bool pixelValue = decoder.Decode(ref ctx);

                // Set the pixel in the output
                int byteIdx = y * bytesPerRow + (x / 8);
                int bitIdx = 7 - (x % 8);

                if (pixelValue)
                {
                    output[byteIdx] |= (byte)(1 << bitIdx);
                }
            }
        }
    }

    /// <summary>
    /// Build a 10-bit context for template 0.
    /// The context is formed by 10 neighbor pixels arranged as:
    ///   p2 p1 cx
    ///   p5 p4 p3
    ///   p9 p8 p7 p6
    /// Returns a value 0-1023 representing the context.
    /// </summary>
    private int BuildTemplate0Context(byte[] output, int bytesPerRow, int x, int y, int width, int height)
    {
        int ctx = 0;

        // Helper to get a pixel value
        bool GetPixel(int px, int py)
        {
            if (px < 0 || px >= width || py < 0 || py >= height)
                return false;

            int byteIdx = py * bytesPerRow + (px / 8);
            int bitIdx = 7 - (px % 8);
            return (output[byteIdx] & (1 << bitIdx)) != 0;
        }

        // Template 0 neighbors (in context index order)
        // Bit 9 (MSB): (x-2, y)
        if (GetPixel(x - 2, y)) ctx |= (1 << 9);

        // Bit 8: (x-1, y)
        if (GetPixel(x - 1, y)) ctx |= (1 << 8);

        // Bit 7: (x-2, y-1)
        if (GetPixel(x - 2, y - 1)) ctx |= (1 << 7);

        // Bit 6: (x-1, y-1)
        if (GetPixel(x - 1, y - 1)) ctx |= (1 << 6);

        // Bit 5: (x, y-1)
        if (GetPixel(x, y - 1)) ctx |= (1 << 5);

        // Bit 4: (x-3, y-1)
        if (GetPixel(x - 3, y - 1)) ctx |= (1 << 4);

        // Bit 3: (x-2, y-2)
        if (GetPixel(x - 2, y - 2)) ctx |= (1 << 3);

        // Bit 2: (x-1, y-2)
        if (GetPixel(x - 1, y - 2)) ctx |= (1 << 2);

        // Bit 1: (x, y-2)
        if (GetPixel(x, y - 2)) ctx |= (1 << 1);

        // Bit 0: (x+1, y-2) -- optional for template 0, included for completeness
        if (GetPixel(x + 1, y - 2)) ctx |= (1 << 0);

        return ctx;
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
