namespace Excise.Core.Writing;

/// <summary>
/// How hard Reduce File Size (#1550) may work. <see cref="Lossless"/> changes
/// only how bytes are stored; the other presets also downsample images whose
/// effective resolution is well above the preset's target, which is visible.
/// </summary>
public enum PdfOptimizationPreset
{
    /// <summary>
    /// Recompress, deduplicate and drop non-content data only. Every page
    /// renders exactly as before.
    /// </summary>
    Lossless,

    /// <summary>Images above 375 dpi are downsampled to 300 dpi (JPEG quality 85).</summary>
    High,

    /// <summary>Images above 188 dpi are downsampled to 150 dpi (JPEG quality 75). A 200 dpi scan qualifies.</summary>
    Standard,

    /// <summary>Images above 120 dpi are downsampled to 96 dpi (JPEG quality 60).</summary>
    Screen,
}

/// <summary>
/// The JPEG codec the image pass of <see cref="PdfDocumentOptimizer"/> uses.
/// Excise.Core has no JPEG implementation of its own (DCTDecode is a
/// pass-through filter, see <c>FilterSupportMap</c>), so the codec is
/// supplied by the caller — <c>Excise.Rendering.PdfImageCodecs.CreateJpegCodec()</c>
/// in the app and the CLI.
/// </summary>
public interface IPdfJpegCodec
{
    /// <summary>
    /// Decode a baseline or progressive JPEG to interleaved 8-bit samples with
    /// exactly <paramref name="components"/> channels (1 = gray, 3 = RGB).
    /// Returns null when the codestream does not describe an image of exactly
    /// <paramref name="width"/> × <paramref name="height"/> ×
    /// <paramref name="components"/>, or cannot be decoded.
    /// </summary>
    /// <param name="jpeg">The codestream.</param>
    /// <param name="width">The image dictionary's <c>/Width</c>.</param>
    /// <param name="height">The image dictionary's <c>/Height</c>.</param>
    /// <param name="components">1 or 3.</param>
    /// <param name="colorTransform">The <c>/DecodeParms /ColorTransform</c>
    /// value, when the image dictionary declares one.</param>
    byte[]? Decode(byte[] jpeg, int width, int height, int components, int? colorTransform);

    /// <summary>
    /// Encode interleaved 8-bit samples (1 or 3 channels) as a baseline JPEG
    /// that a PDF reader decodes with <c>/DCTDecode</c> and no
    /// <c>/DecodeParms</c>. Returns null when encoding is not possible.
    /// </summary>
    byte[]? Encode(byte[] samples, int width, int height, int components, int quality);
}

/// <summary>
/// Options for <see cref="PdfDocumentOptimizer"/> (#1550). Start from
/// <see cref="ForPreset"/>; the individual switches exist so a caller can turn
/// one part off.
/// </summary>
public sealed record PdfOptimizationOptions
{
    /// <summary>The preset these options were derived from.</summary>
    public PdfOptimizationPreset Preset { get; init; } = PdfOptimizationPreset.Lossless;

    /// <summary>
    /// Re-encode uncompressed and weakly encoded streams (ASCIIHex, ASCII85,
    /// LZW, RunLength, and Flate without a predictor) with the strongest Flate
    /// level, keeping the result only when it is smaller.
    /// </summary>
    public bool RecompressStreams { get; init; } = true;

    /// <summary>
    /// Point every reference to a byte-identical image, form XObject or font
    /// program at one copy.
    /// </summary>
    public bool DeduplicateStreams { get; init; } = true;

    /// <summary>Remove page <c>/Thumb</c> images; viewers regenerate thumbnails.</summary>
    public bool RemoveThumbnails { get; init; } = true;

    /// <summary>
    /// Remove <c>/PieceInfo</c> — other applications' private editing data
    /// (§14.5). It never contributes to what a page shows.
    /// </summary>
    public bool RemovePrivateApplicationData { get; init; } = true;

    /// <summary>
    /// Resolution images are downsampled to, in pixels per inch at their
    /// largest placed size. Null disables the image pass.
    /// </summary>
    public int? TargetImageDpi { get; init; }

    /// <summary>
    /// Only images above this effective resolution are downsampled (1.25× the
    /// target in every preset), so an image just over the target is not
    /// resampled for a marginal gain.
    /// </summary>
    public int ImageDpiThreshold { get; init; }

    /// <summary>JPEG quality (1–100) for images the image pass re-encodes.</summary>
    public int JpegQuality { get; init; } = 75;

    /// <summary>
    /// The JPEG codec. Without one the image pass still downsamples
    /// losslessly-stored images (re-encoding them with Flate) but leaves JPEG
    /// images untouched.
    /// </summary>
    public IPdfJpegCodec? JpegCodec { get; init; }

    /// <summary>The settings each preset stands for.</summary>
    public static PdfOptimizationOptions ForPreset(PdfOptimizationPreset preset, IPdfJpegCodec? jpegCodec = null)
        => preset switch
        {
            PdfOptimizationPreset.Lossless => new PdfOptimizationOptions { Preset = preset, JpegCodec = jpegCodec },
            PdfOptimizationPreset.High => new PdfOptimizationOptions
            {
                Preset = preset, TargetImageDpi = 300, ImageDpiThreshold = 375, JpegQuality = 85, JpegCodec = jpegCodec,
            },
            PdfOptimizationPreset.Standard => new PdfOptimizationOptions
            {
                Preset = preset, TargetImageDpi = 150, ImageDpiThreshold = 188, JpegQuality = 75, JpegCodec = jpegCodec,
            },
            PdfOptimizationPreset.Screen => new PdfOptimizationOptions
            {
                Preset = preset, TargetImageDpi = 96, ImageDpiThreshold = 120, JpegQuality = 60, JpegCodec = jpegCodec,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Unknown optimization preset."),
        };

    /// <summary>
    /// Parse a preset name as the CLI spells it (<c>lossless</c>, <c>high</c>,
    /// <c>standard</c>, <c>screen</c>), case-insensitively.
    /// </summary>
    public static bool TryParsePreset(string? text, out PdfOptimizationPreset preset)
    {
        preset = PdfOptimizationPreset.Lossless;
        if (string.IsNullOrWhiteSpace(text) || !text.Trim().All(char.IsLetter))
            return false;
        return Enum.TryParse(text.Trim(), ignoreCase: true, out preset)
               && Enum.IsDefined(preset);
    }
}

/// <summary>What <see cref="PdfDocumentOptimizer"/> changed (#1550).</summary>
public sealed record PdfOptimizationResult
{
    /// <summary>The preset that ran.</summary>
    public PdfOptimizationPreset Preset { get; init; }

    /// <summary>Streams re-encoded with a stronger Flate level.</summary>
    public int StreamsRecompressed { get; init; }

    /// <summary>Byte-identical streams folded into another copy.</summary>
    public int StreamsDeduplicated { get; init; }

    /// <summary>Page thumbnail entries removed.</summary>
    public int ThumbnailsRemoved { get; init; }

    /// <summary><c>/PieceInfo</c> entries removed.</summary>
    public int PrivateDataEntriesRemoved { get; init; }

    /// <summary>Images downsampled and re-encoded.</summary>
    public int ImagesDownsampled { get; init; }

    /// <summary>
    /// Images above the preset's threshold that were left alone, with the
    /// reason, e.g. "unsupported color space" — never silently skipped.
    /// </summary>
    public IReadOnlyDictionary<string, int> ImagesSkipped { get; init; } = new Dictionary<string, int>();

    /// <summary>
    /// Things the caller should tell the user, e.g. that signatures in the
    /// source will not validate in the rewritten copy.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>Size of the written file, when the optimizer wrote one; otherwise 0.</summary>
    public long OutputSizeBytes { get; init; }
}
