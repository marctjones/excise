using Excise.Core.Parsing;

namespace Excise.Core.Filters;

/// <summary>
/// Shared decompression-bomb guard for filter decoders (ISO 32000-2 §7.4). A
/// crafted stream a few KB in size can expand to gigabytes via FlateDecode or
/// LZWDecode, exhausting memory before any image-dimension or render-time
/// guard would ever see it -- those guards look at the DECODED stream's
/// content, and a stream that never finishes decoding never reaches them
/// (#1408).
///
/// The ceiling is deliberately generous -- far larger than any legitimate
/// single PDF stream (a content stream, an object stream, an image) needs --
/// so it exists only to turn an unbounded allocation into a typed, safely
/// recoverable failure, the same way <see cref="Jbig2.Jbig2Decoder"/> refuses
/// an absurd claimed image size before allocating rather than trying to be
/// exactly right about what "too big" means.
/// </summary>
internal static class StreamDecodeLimits
{
    /// <summary>
    /// 512 MiB. For scale: <c>RenderOptions.DefaultMaxPixelCount</c> (256M
    /// pixels) bounds a decoded BITMAP at roughly this same order of
    /// magnitude; this bounds the decoded STREAM BYTES that feed one.
    /// </summary>
    internal const long MaxDecompressedBytes = 512L * 1024L * 1024L;

    /// <summary>
    /// Throws a <see cref="PdfParseException"/> with <c>IsResourceGuard</c>
    /// set once decoded output crosses <paramref name="maxBytes"/>. Callers
    /// making this check must check this DURING decode, in small increments
    /// -- checking only the final size after fully materializing the output
    /// defeats the guard's entire purpose.
    /// </summary>
    internal static void ThrowIfExceeded(long decodedBytesSoFar, long maxBytes)
    {
        if (decodedBytesSoFar > maxBytes)
        {
            throw new PdfParseException(
                $"Decompressed stream output exceeds the {maxBytes:N0}-byte decode ceiling " +
                "(ISO 32000-2 §7.4) -- refusing to allocate further; this is the signature of " +
                "a decompression bomb, not a legitimate document.")
            {
                IsResourceGuard = true,
            };
        }
    }
}
