using System.Collections.Generic;
using System.Linq;

namespace Excise.Core.Filters;

/// <summary>
/// Who actually implements the decoding for a given PDF stream filter.
/// </summary>
internal enum FilterDecoderOwner
{
    /// <summary>
    /// excise owns the decoder. A wrong pixel here is an excise bug and is
    /// worth chasing.
    /// </summary>
    Excise,

    /// <summary>
    /// A third-party library owns the decoder. excise chooses the library,
    /// hands it bytes and uses what comes back. A rendering difference that
    /// originates inside such a decoder is that library's behaviour, not an
    /// excise defect — worth KNOWING, not worth chasing.
    /// </summary>
    ThirdParty,

    /// <summary>
    /// Not decoded at the filter layer at all — the encoded bytes pass through
    /// and are handed to a consumer (typically the renderer) that decodes them.
    /// </summary>
    PassThrough,
}

/// <summary>
/// Where a filter's decoding actually happens, and therefore whose defect a
/// rendering difference is.
/// </summary>
internal sealed record FilterSupportProfile(
    string Filter,
    FilterDecoderOwner Owner,
    string? DelegatedTo,
    string Notes);

/// <summary>
/// The ownership map for every filter <see cref="PdfFilterRegistry.CreateDefault"/>
/// registers.
///
/// WHY THIS EXISTS
///
/// Several rendering-defect clusters turned out not to be excise defects at
/// all, and there was no way to tell without reading the decoder. "How good is
/// our DCTDecode support?" has no answer, because excise has no DCTDecode
/// implementation — the bytes are passed through and Skia decodes them. Any
/// difference between excise and mutool on a JPEG is a difference between
/// libjpeg-turbo and MuPDF's decoder.
///
/// That distinction decides whether a bug is worth opening. It is recorded here
/// rather than in prose so a test can check it stays true, and so adding a
/// filter forces an explicit answer to "who decodes this?".
///
/// It does NOT excuse everything near a delegated decoder. Both DCT defects
/// found in the 2026-08 corpus work were excise's and were fixed: a 4-byte
/// stream painted as an image (#878), and an inline image whose data started
/// one byte late because ID was followed by CRLF (#887). Neither was inside
/// the JPEG decoder. The rule is about where the DEFECT is, not which filter
/// name appears on the stream.
/// </summary>
internal static class FilterSupportMap
{
    private static readonly FilterSupportProfile[] Profiles =
    {
        new("FlateDecode", FilterDecoderOwner.ThirdParty, "System.IO.Compression",
            "Deflate via the BCL. Predictors (/Predictor, PNG and TIFF) ARE excise's — see PdfPredictor."),

        new("LZWDecode", FilterDecoderOwner.Excise, null,
            "Full excise implementation including /EarlyChange (§7.4.4.3), which was unread until #887."),

        new("ASCIIHexDecode", FilterDecoderOwner.Excise, null, "Trivial, complete."),

        new("ASCII85Decode", FilterDecoderOwner.Excise, null, "Trivial, complete."),

        new("RunLengthDecode", FilterDecoderOwner.Excise, null, "Complete."),

        new("CCITTFaxDecode", FilterDecoderOwner.Excise, null,
            "Full excise implementation (Group 3 1D/2D and Group 4). Assessable — see " +
            "CcittCapabilityClassifier."),

        new("JBIG2Decode", FilterDecoderOwner.Excise, null,
            "Full excise implementation. Assessable — see Jbig2CapabilityClassifier. Known gap: " +
            "retained symbol-dictionary coding contexts (#656)."),

        new("DCTDecode", FilterDecoderOwner.PassThrough, "SkiaSharp / libjpeg-turbo",
            "excise does NOT decode JPEG. The filter passes the encoded bytes through and the " +
            "renderer hands them to SKBitmap.Decode. excise owns only the surrounding colour " +
            "handling — Adobe APP14 transform detection and CMYK inversion. A pixel-level " +
            "difference against another renderer on a valid JPEG is a libjpeg-turbo-vs-theirs " +
            "difference and is NOT an excise bug."),

        new("JPXDecode", FilterDecoderOwner.ThirdParty, "CSJ2K",
            "excise owns the codestream METADATA parser (dimensions, components, bit depth) and " +
            "delegates pixel decoding to CSJ2K. excise's own JpxDecoder.Decode deliberately " +
            "throws NotSupportedException rather than emit silently wrong pixels — tier-1 EBCOT, " +
            "tier-2 packet assembly, inverse DWT and the colour transforms are not implemented."),

        new("BrotliDecode", FilterDecoderOwner.ThirdParty, "System.IO.Compression",
            "PDF 2.0 filter, decoded by the BCL."),

        new("Crypt", FilterDecoderOwner.PassThrough, "Excise.Core/Security handlers",
            "Not a compression filter. The bytes pass through untouched here because " +
            "decryption already happened in the security handler (Excise.Core/Security), " +
            "which excise does own — so a defect is ours, it is just not in this layer."),
    };

    /// <summary>Every filter with a declared owner.</summary>
    public static IReadOnlyList<FilterSupportProfile> All => Profiles;

    /// <summary>Look up a filter by its canonical PDF name, or null.</summary>
    public static FilterSupportProfile? Find(string filter)
        => Profiles.FirstOrDefault(p => string.Equals(p.Filter, filter, System.StringComparison.Ordinal));

    /// <summary>
    /// True when a rendering difference attributable to this filter's decoding
    /// is somebody else's behaviour rather than an excise defect.
    /// </summary>
    public static bool IsDelegated(string filter)
        => Find(filter) is { Owner: FilterDecoderOwner.ThirdParty or FilterDecoderOwner.PassThrough };
}
