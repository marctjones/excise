using System.Collections.Generic;
using Excise.Core.Document;

namespace Excise.Ocr;

/// <summary>
/// One OCR-recognized word with its position in page space.
/// </summary>
/// <param name="Text">Recognized text.</param>
/// <param name="BoundingBox">Word bounding box in PDF points (bottom-left origin).</param>
/// <param name="Confidence">Tesseract confidence score, 0.0–1.0.</param>
public sealed record OcrWord(
    string Text,
    PdfRectangle BoundingBox,
    float Confidence);

/// <summary>
/// Result of OCR-ing a page or image: the full extracted text plus
/// per-word detail.
/// </summary>
public sealed record OcrResult(
    string Text,
    IReadOnlyList<OcrWord> Words)
{
    public static OcrResult Empty { get; } = new("", System.Array.Empty<OcrWord>());
}
