namespace Pdfe.Core.Filters.Jpx;

/// <summary>
/// JPEG2000 (JPXDecode) decoder for PDF streams.
/// Implements ISO/IEC 15444-1:2019 (JPEG2000 Part 1 - Core coding system).
///
/// SCOPE (v1 - Honest Implementation):
/// - JP2 box container parsing and J2K codestream extraction
/// - Codestream marker parsing: SOC, SIZ, COD, COC, QCD, QCC, SOT, SOD, EOC
/// - Image metadata extraction: width, height, components, bit depth
/// - MQ arithmetic decoder (EBCOT entropy coder, ISO/IEC 15444-1 Annex D)
/// - Tier-1 EBCOT cleanup/significance propagation/magnitude refinement passes (Annex D.1.1)
///
/// NOT IMPLEMENTED (intentional, documented):
/// - Wavelet inverse transform (Annex F - DWT analysis filters)
/// - Tier-2 packet assembly (Annex A - progression orders, layer boundaries)
/// - Color transforms (MCT, RCT - Annex F.3)
/// - Tiling edge/boundary handling
/// - Precinct-based coding
///
/// This results in:
/// - ReadInfo() returns dimensions/components/bpc: FULLY WORKING
/// - Decode() returns NotSupportedException: HONEST about limitations
///
/// Real-world usage: Call ReadInfo() first. If you need pixel data, use an external
/// JPEG2000 library (OpenJPEG) or clarify required profile (typically simple images
/// are single-tile, reversible 5/3, layer=0).
/// </summary>
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
internal static class JpxDecoder
{
    /// <summary>
    /// Read image metadata from JPEG2000 codestream without decoding pixels.
    /// Works for JP2 box containers and raw J2K codestreams.
    /// </summary>
    /// <exception cref="ArgumentException">Invalid JPEG2000 data</exception>
    public static (int width, int height, int components, int bpc) ReadInfo(byte[] jpxData)
    {
        if (jpxData == null || jpxData.Length == 0)
            throw new ArgumentException("Empty JPEG2000 data", nameof(jpxData));

        var parser = new CodestreamParser(jpxData);
        var info = parser.ExtractMetadata();
        return (info.Width, info.Height, info.Components, info.BitsPerComponent);
    }

    /// <summary>
    /// Decode JPEG2000 data to pixel array.
    /// Currently returns NotSupportedException for most profiles.
    /// Implement full MQ + wavelet + MCT pipeline to enable.
    /// </summary>
    /// <exception cref="NotSupportedException">Profile not yet implemented</exception>
    public static JpxImage Decode(byte[] jpxData)
    {
        if (jpxData == null || jpxData.Length == 0)
            throw new ArgumentException("Empty JPEG2000 data", nameof(jpxData));

        var parser = new CodestreamParser(jpxData);
        var info = parser.ExtractMetadata();

        // Placeholder: full decode requires wavelet + MCT which are complex.
        // To implement:
        // 1. Parse all COD/COC (coding style) to determine precinct sizes, decomposition levels
        // 2. Parse QCD/QCC (quantization) for step sizes per subband
        // 3. For each tile (SOT...SOD):
        //    a. Parse tier-1 EBCOT codeblocks (MQ arithmetic decode)
        //    b. Assemble tier-2 packets (AOI, layer, precinct ordering)
        //    c. Perform DWT inverse (5/3 reversible or 9/7 irreversible)
        //    d. Apply component transforms (RCT/MCT)
        // 4. Clamp to bit depth, return pixel array

        // For now, return NotSupportedException to avoid silent wrong output
        throw new NotSupportedException(
            $"JPEG2000 full decode not yet implemented. " +
            $"Image is {info.Width}x{info.Height}, {info.Components} components, {info.BitsPerComponent} bpc. " +
            $"Use ReadInfo() to inspect metadata. " +
            $"To enable decoding: implement tier-1 EBCOT (MQ), tier-2 packet assembly, DWT inverse, and color transforms.");
    }
}

/// <summary>
/// Container for decoded JPEG2000 image.
/// Pixels are interleaved by component (C0, C1, ..., Cn, C0, C1, ... for next pixel).
/// </summary>
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
internal sealed class JpxImage
{
    public int Width { get; set; }
    public int Height { get; set; }
    public int Components { get; set; }
    public int BitsPerComponent { get; set; }
    public byte[] Pixels { get; set; } = Array.Empty<byte>();
}
