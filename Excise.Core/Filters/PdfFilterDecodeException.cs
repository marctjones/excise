namespace Excise.Core.Filters;

/// <summary>
/// Why a filter could not decode a stream. The distinction is user-facing: an
/// unimplemented feature is excise's gap and a bug report, corrupt data is the
/// file's problem and usually is not (#1396).
/// </summary>
internal enum PdfFilterDecodeFailureKind
{
    /// <summary>A legal construct excise has not implemented.</summary>
    Unimplemented,

    /// <summary>Data that does not conform to the codec.</summary>
    CorruptInput,
}

/// <summary>
/// A filter attempted a decode and could not complete it.
/// </summary>
/// <remarks>
/// This exists so a failed decode can FAIL rather than return its own input
/// (#1396). Handing the still-encoded bytes back is not a neutral no-op: for a
/// filter whose output is image SAMPLES, the caller rasterises those compressed
/// bytes as pixels — visual noise presented as page content, with no
/// diagnostic. It cost a black page on pdfium's bug_867501.pdf.
///
/// ⚠️ Pass-through is NOT always wrong, which is why this is thrown from one
/// place rather than everywhere a filter gives back its input. <c>DCTDecode</c>
/// is a registered <c>PassThroughFilterDecoder</c> BY DESIGN, and
/// <c>JPXDecode</c> falls back to pass-through by design: for both, the image
/// layer takes the codestream and decodes it itself (see
/// <c>SkiaRenderer.Images.GetTerminalJpxData</c>, which reads exactly those
/// bytes). JBIG2 has no such second consumer — nothing downstream recognises a
/// JBIG2 codestream — so for JBIG2, and only JBIG2, pass-through means the
/// bytes are painted.
/// </remarks>
internal sealed class PdfFilterDecodeException : Exception
{
    public PdfFilterDecodeException(
        string filterName,
        PdfFilterDecodeFailureKind kind,
        string detail,
        Exception? innerException = null)
        : base(Describe(filterName, kind, detail), innerException)
    {
        FilterName = filterName;
        Kind = kind;
    }

    public string FilterName { get; }

    public PdfFilterDecodeFailureKind Kind { get; }

    private static string Describe(string filterName, PdfFilterDecodeFailureKind kind, string detail)
    {
        var cause = kind == PdfFilterDecodeFailureKind.Unimplemented
            ? "unimplemented feature"
            : "corrupt or non-conforming data";
        return $"/{filterName} could not decode this stream ({cause}): {detail}";
    }
}
