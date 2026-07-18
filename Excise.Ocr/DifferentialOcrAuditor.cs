using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;

namespace Excise.Ocr;

/// <summary>
/// Words discovered by OCRing the page with overlays stripped that
/// were absent (or significantly less prominent) in the OCR of the
/// page as displayed — i.e., text the document was visually hiding
/// behind an opaque object that survives at the pixel level.
/// </summary>
/// <param name="PageNumber">1-based page number.</param>
/// <param name="Text">The hidden word as recognized by OCR.</param>
/// <param name="BoundingBox">Word location in PDF points (bottom-left).</param>
/// <param name="Confidence">Tesseract confidence, 0.0–1.0.</param>
public sealed record DifferentialOcrHit(
    int PageNumber,
    string Text,
    Excise.Core.Document.PdfRectangle BoundingBox,
    float Confidence);

/// <summary>
/// Catches the "rasterized + overlay-hidden" leak class that
/// content-stream auditors miss. Opens a PDF twice: as-is, and with
/// <see cref="ObstructionStripper"/> applied. OCRs both. Words present
/// in the stripped render but missing from the as-is render were text
/// the document was successfully hiding visually.
/// </summary>
/// <remarks>
/// Useful against scanned PDFs where someone drew a black rectangle
/// over a region — structurally there's no <c>Tj</c> to detect, but
/// the pixels are recoverable from the underlying image.
/// </remarks>
public sealed class DifferentialOcrAuditor
{
    private readonly PdfOcrService _ocr;

    public DifferentialOcrAuditor(PdfOcrService ocrService)
    {
        _ocr = ocrService;
    }

    /// <summary>Scan every page, returning every hit on every page.</summary>
    public IReadOnlyList<DifferentialOcrHit> Scan(byte[] pdfBytes)
    {
        var all = new List<DifferentialOcrHit>();
        using var src = PdfDocument.Open(pdfBytes);
        for (int p = 1; p <= src.PageCount; p++)
            all.AddRange(ScanPage(pdfBytes, p));
        return all;
    }

    /// <summary>
    /// Scan one page. Re-opens the PDF from <paramref name="pdfBytes"/>
    /// for the stripped version so the as-is version isn't mutated.
    /// </summary>
    public IReadOnlyList<DifferentialOcrHit> ScanPage(byte[] pdfBytes, int pageNumber)
    {
        // OCR the page as displayed.
        OcrResult asIs;
        double pageHeight;
        using (var doc = PdfDocument.Open(pdfBytes))
        {
            var page = doc.GetPage(pageNumber);
            pageHeight = page.Height;
            asIs = _ocr.RecognizePage(page);
        }

        // OCR the page with obstructions stripped.
        OcrResult stripped;
        using (var doc = PdfDocument.Open(pdfBytes))
        {
            var page = doc.GetPage(pageNumber);
            ObstructionStripper.StripObstructions(page);
            stripped = _ocr.RecognizePage(page);
        }

        // The diff: any word in `stripped` that doesn't appear in
        // `asIs` (case-insensitive, exact-text) was hidden.
        var visibleVocab = new HashSet<string>(
            asIs.Words.Select(w => Normalize(w.Text)),
            StringComparer.OrdinalIgnoreCase);

        var hits = new List<DifferentialOcrHit>();
        foreach (var w in stripped.Words)
        {
            if (string.IsNullOrWhiteSpace(w.Text)) continue;
            if (visibleVocab.Contains(Normalize(w.Text))) continue;
            hits.Add(new DifferentialOcrHit(
                pageNumber, w.Text, w.BoundingBox, w.Confidence));
        }
        return hits;
    }

    /// <summary>Convenience overload that loads the bytes for you.</summary>
    public IReadOnlyList<DifferentialOcrHit> ScanFile(string pdfPath)
        => Scan(File.ReadAllBytes(pdfPath));

    /// <summary>
    /// Loose comparison: strip surrounding punctuation so "SECRET"
    /// matches "SECRET," etc. Tesseract often pins random punctuation
    /// to word boundaries.
    /// </summary>
    private static string Normalize(string s)
        => s.Trim('.', ',', ':', ';', '!', '?', '"', '\'', '(', ')', '[', ']');
}
