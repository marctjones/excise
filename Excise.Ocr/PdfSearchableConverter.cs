using System;
using System.Collections.Generic;
using System.Threading;
using Excise.Core.Document;
using Excise.Core.Graphics;

namespace Excise.Ocr;

/// <summary>
/// Result of attempting to make a single page searchable.
/// </summary>
/// <param name="PageNumber">1-based page number.</param>
/// <param name="Skipped">True if the page was not OCR'd at all.</param>
/// <param name="AlreadyHadText">True if <paramref name="Skipped"/> because the page already had an extractable text layer.</param>
/// <param name="WordsWritten">Number of OCR words written as invisible text.</param>
/// <param name="WordsSkippedEncoding">
/// Number of recognized words that were NOT written because they contain a
/// character the invisible-text font can't represent (see
/// <see cref="PdfFont.CanEncodeFully"/>) — e.g. non-Latin-script OCR output.
/// A non-zero count here means this page is only partially searchable.
/// </param>
public readonly record struct SearchablePageResult(
    int PageNumber,
    bool Skipped,
    bool AlreadyHadText,
    int WordsWritten,
    int WordsSkippedEncoding);

/// <summary>
/// Result of making a whole document searchable.
/// </summary>
public sealed record SearchableDocumentResult(
    int PagesProcessed,
    int PagesSkipped,
    int TotalWordsWritten,
    int TotalWordsSkippedEncoding,
    IReadOnlyList<SearchablePageResult> Pages);

/// <summary>
/// Writes OCR-recognized text back into a scanned PDF as an invisible
/// (<c>Tr 3</c>) text layer, so the page becomes searchable/selectable
/// while its visual appearance is unchanged (#627). The raster image is
/// untouched; only new text-showing operators are appended.
/// </summary>
/// <remarks>
/// Render mode does not affect what search or redaction can reach — see
/// <see cref="PdfGraphics.DrawInvisibleText"/> and
/// <c>PdfDocumentRedactionExtensions.RedactText</c>, which routes every
/// text match (invisible or not) through <c>PdfPage.RedactArea</c>, and
/// that in turn clears both the glyphs AND any intersecting image content.
/// So redacting a word found via this layer removes it from both carriers.
/// </remarks>
public sealed class PdfSearchableConverter
{
    private readonly PdfOcrService _ocrService;

    public PdfSearchableConverter(PdfOcrService ocrService)
    {
        _ocrService = ocrService ?? throw new ArgumentNullException(nameof(ocrService));
    }

    /// <summary>
    /// OCR and write an invisible text layer onto a single page.
    /// </summary>
    /// <param name="page">The page to make searchable.</param>
    /// <param name="force">
    /// If true, OCR and overlay even if the page already has an extractable
    /// text layer. Default false: pages with real text are left untouched.
    /// </param>
    public SearchablePageResult MakePageSearchable(PdfPage page, bool force = false)
    {
        ArgumentNullException.ThrowIfNull(page);

        if (!force && page.Letters.Count > 0)
            return new SearchablePageResult(page.PageNumber, Skipped: true, AlreadyHadText: true, 0, 0);

        var ocr = _ocrService.RecognizePage(page);

        int written = 0;
        int skippedEncoding = 0;

        using (var graphics = page.GetGraphics())
        {
            foreach (var word in ocr.Words)
            {
                if (string.IsNullOrWhiteSpace(word.Text))
                    continue;

                var bbox = word.BoundingBox;
                var width = bbox.Width;
                if (width <= 0)
                    continue;

                // Font size from the box height: tall enough that the
                // glyphs' natural width/height stay in a sane ratio for Tz
                // scaling, floored so a degenerate near-zero-height TSV box
                // never produces an unusable font size.
                var fontSize = Math.Max(bbox.Height, 1.0);
                var font = PdfFont.Helvetica(fontSize);

                if (!font.CanEncodeFully(word.Text))
                {
                    skippedEncoding++;
                    continue;
                }

                graphics.DrawInvisibleText(word.Text, font, bbox.Left, bbox.Bottom, width);
                written++;
            }
        } // Dispose() flushes the accumulated operators in one content-stream write.

        return new SearchablePageResult(page.PageNumber, Skipped: false, AlreadyHadText: false, written, skippedEncoding);
    }

    /// <summary>
    /// OCR and write an invisible text layer onto every page of
    /// <paramref name="document"/> that doesn't already have one (unless
    /// <paramref name="force"/> is set).
    /// </summary>
    public SearchableDocumentResult MakeSearchable(
        PdfDocument document,
        bool force = false,
        IProgress<(int Done, int Total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        var total = document.PageCount;
        var pages = new List<SearchablePageResult>(total);
        int processed = 0, skipped = 0, wordsWritten = 0, wordsSkipped = 0;

        for (int p = 1; p <= total; p++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = MakePageSearchable(document.GetPage(p), force);
            pages.Add(result);

            if (result.Skipped) skipped++;
            else processed++;
            wordsWritten += result.WordsWritten;
            wordsSkipped += result.WordsSkippedEncoding;

            progress?.Report((p, total));
        }

        return new SearchableDocumentResult(processed, skipped, wordsWritten, wordsSkipped, pages);
    }
}
