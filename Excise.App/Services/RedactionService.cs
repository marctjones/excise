using Avalonia;
using Microsoft.Extensions.Logging;
using Excise.App.Models;
using Excise.Core.Document;
using Excise.Core.Operations;
using Excise.Core.Text.Segmentation;
using Excise.Ocr;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Excise.App.Services;

/// <summary>
/// Options for redaction operations.
/// </summary>
public class RedactionOptions
{
    /// <summary>Remove redacted terms from document metadata (Info dict).</summary>
    public bool SanitizeMetadata { get; set; } = true;

    /// <summary>Strip the entire /Info dictionary for maximum hygiene.</summary>
    public bool RemoveAllMetadata { get; set; } = false;
}

/// <summary>
/// GUI-facing redaction orchestrator. A thin shell over Excise.Core:
/// delegates glyph/image removal to <see cref="PdfPageRedactionExtensions"/>
/// and text-search redaction to <see cref="PdfDocumentRedactionExtensions"/>.
/// </summary>
/// <remarks>
/// ⚠️ CRITICAL FOR AI CODING ASSISTANTS:
/// Redaction is TRUE GLYPH-LEVEL REMOVAL — glyphs are deleted from the PDF
/// content stream, not just visually covered. Do not replace the
/// content-stream rewrite with a visual-only black box; that is a
/// security regression. Tests in
/// <c>Excise.App.Tests.Security.ContentRemovalVerificationTests</c> pin
/// this property.
/// </remarks>
public class RedactionService
{
    private readonly ILogger<RedactionService> _logger;
    private readonly List<string> _redactedTerms = new();

    public RedactionService(ILogger<RedactionService> logger, ILoggerFactory _)
    {
        _logger = logger;
    }

    /// <summary>Text strings that have been redacted in the current session.</summary>
    public IReadOnlyList<string> RedactedTerms => _redactedTerms.AsReadOnly();

    /// <summary>Reset the redacted-terms list.</summary>
    public void ClearRedactedTerms() => _redactedTerms.Clear();

    /// <summary>
    /// Redact a rectangular area on <paramref name="page"/>.
    /// </summary>
    public void RedactArea(PdfPage page, PdfPageRect area)
    {
        var visualArea = PdfCoordinateMapper.ToVisualPoints(page, area);
        if (!IntersectsVisualPage(visualArea, page.VisualWidth, page.VisualHeight))
        {
            _logger.LogWarning(
                "Selection area may be outside page bounds. Page: ({W}x{H}), Selection: ({X},{Y},{SW}x{SH})",
                page.VisualWidth, page.VisualHeight, visualArea.X, visualArea.Y, visualArea.Width, visualArea.Height);
        }

        var coreRect = PdfCoordinateMapper.ToContentPoints(page, area).ToPdfRectangle().Normalize();

        // Snapshot the words about to be removed — after RedactArea
        // rewrites the content stream, extraction reports zero.
        var removed = page.Letters
            .Where(l => RedactionMath.Overlaps(l.GlyphRectangle, coreRect))
            .Select(l => l.Value)
            .ToList();

        page.RedactArea(coreRect, GlyphRemovalStrategy.AnyOverlap);
        AppendBlackRectangle(page, coreRect);

        foreach (var w in string.Concat(removed)
            .Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            _redactedTerms.Add(w);
        }

        _logger.LogInformation("Redacted {Count} characters on page", removed.Count);
    }

    /// <summary>
    /// Redact a rectangular area expressed in rendered-page pixels/DIPs.
    /// Prefer <see cref="RedactArea(PdfPage, PdfPageRect)"/> for new code.
    /// </summary>
    public void RedactArea(PdfPage page, Rect area, int renderDpi = 72)
    {
        RedactArea(
            page,
            PdfPageRect.ViewerDips(page.PageNumber, area.X, area.Y, area.Width, area.Height, renderDpi));
    }

    /// <summary>Redact multiple rectangles on the same page.</summary>
    public void RedactAreas(PdfPage page, IEnumerable<PdfPageRect> areas)
    {
        foreach (var area in areas)
            RedactArea(page, area);
    }

    /// <summary>Redact multiple rendered-page rectangles on the same page.</summary>
    public void RedactAreas(PdfPage page, IEnumerable<Rect> areas, int renderDpi = 150)
    {
        foreach (var area in areas)
            RedactArea(page, area, renderDpi);
    }

    private static bool IntersectsVisualPage(PdfPageRect visualArea, double visualPageWidth, double visualPageHeight)
    {
        const double tolerance = 50;
        return visualArea.Space == PdfCoordinateSpace.VisualPoints &&
               visualArea.Width > 0 &&
               visualArea.Height > 0 &&
               visualArea.X >= -tolerance &&
               visualArea.Y >= -tolerance &&
               visualArea.Right <= visualPageWidth + tolerance &&
               visualArea.Y2 <= visualPageHeight + tolerance;
    }

    /// <summary>
    /// Redact every occurrence of <paramref name="textToRedact"/> in the
    /// PDF at <paramref name="inputPath"/>, writing to <paramref name="outputPath"/>.
    /// Shares the same Excise.Core pipeline as area-click redaction.
    /// </summary>
    /// <param name="allowLowConfidence">
    /// #650: before redacting, excise's own extraction is checked against an
    /// independent oracle (mutool, or tesseract if mutool isn't installed)
    /// for this specific document. When that check comes back Severe — the
    /// oracle finds substantially more/different text than excise extracted,
    /// the same signature as a real redaction leak — the redaction is
    /// refused by default. Pass true to proceed anyway. A Degraded or
    /// Unverified result never blocks; it's surfaced in
    /// <see cref="TextRedactionResult.Warnings"/> instead.
    /// </param>
    public TextRedactionResult RedactText(
        string inputPath, string outputPath, string textToRedact, bool caseSensitive = false,
        bool allowLowConfidence = false)
    {
        _logger.LogInformation("RedactText: '{Text}' in {Input}", textToRedact, inputPath);

        try
        {
            using var doc = PdfDocument.Open(File.ReadAllBytes(inputPath));

            var confidence = new RedactionConfidenceChecker().CheckDocument(doc, sourceFilePath: inputPath);
            if (confidence.ShouldRefuse && !allowLowConfidence)
            {
                _logger.LogWarning(
                    "RedactText refused for '{Text}': extraction-confidence check reported {Tier} (oracle: {Oracle})",
                    textToRedact, confidence.Tier, confidence.Oracle);
                return TextRedactionResult.Failed(
                    $"Redaction refused: excise's own text extraction disagrees sharply with an independent " +
                    $"check ({confidence.Oracle}) on this document — the same signature as a real redaction " +
                    "leak. This may be a false alarm, but proceeding without understanding why requires " +
                    "explicit confirmation.");
            }

            int totalMatches = doc.RedactText(textToRedact, caseSensitive);
            // #643: this path opens without a password, so only empty-user-
            // password encrypted sources reach here — their redacted output
            // stays encrypted with the same parameters.
            doc.Save(outputPath, doc.GetReEncryptionOptions(userPassword: null));

            if (totalMatches > 0)
                _redactedTerms.Add(textToRedact);

            var warnings = confidence.ShouldWarn
                ? new[] { BuildConfidenceWarning(confidence) }
                : null;

            _logger.LogInformation("Redacted {Count} occurrence(s) of '{Text}'", totalMatches, textToRedact);
            return TextRedactionResult.Succeeded(totalMatches, warnings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RedactText failed for '{Text}'", textToRedact);
            return TextRedactionResult.Failed($"Redaction failed: {ex.Message}");
        }
    }

    private static string BuildConfidenceWarning(RedactionConfidenceReport confidence) =>
        confidence.Tier == RedactionConfidenceTier.Unverified
            ? "Redaction could not be independently verified — neither mutool nor tesseract is installed. " +
              "excise's own extraction was used as-is; install one of those tools for a confidence check on future redactions."
            : $"Redaction succeeded, but excise's extraction differs somewhat from an independent check " +
              $"({confidence.Oracle}) on one or more pages of this document. Review the result before relying on it.";

    /// <summary>
    /// Full workflow: redact multiple areas on <paramref name="page"/>,
    /// optionally sanitize/strip metadata on <paramref name="document"/>.
    /// </summary>
    public void RedactWithOptions(PdfDocument document, PdfPage page, IEnumerable<Rect> areas,
        RedactionOptions options, int renderDpi = 150)
    {
        ClearRedactedTerms();

        foreach (var area in areas)
            RedactArea(page, area, renderDpi);

        if (options.RemoveAllMetadata)
            StripAllMetadata(document);
        else if (options.SanitizeMetadata)
            SanitizeMetadata(document, _redactedTerms);
    }

    /// <summary>
    /// Full workflow: redact multiple typed areas on <paramref name="page"/>,
    /// optionally sanitize/strip metadata on <paramref name="document"/>.
    /// </summary>
    public void RedactWithOptions(PdfDocument document, PdfPage page, IEnumerable<PdfPageRect> areas,
        RedactionOptions options)
    {
        ClearRedactedTerms();

        foreach (var area in areas)
            RedactArea(page, area);

        if (options.RemoveAllMetadata)
            StripAllMetadata(document);
        else if (options.SanitizeMetadata)
            SanitizeMetadata(document, _redactedTerms);
    }

    /// <summary>
    /// Remove every entry of <paramref name="terms"/> from the document's
    /// non-page text carriers: <c>/Info</c>, the XMP <c>/Metadata</c> packet,
    /// outline (bookmark) titles, and annotation <c>/Contents</c>.
    /// </summary>
    /// <remarks>
    /// Delegates to <see cref="PdfDocumentSanitizer"/> (#608). This previously
    /// scrubbed only <c>/Info</c>, which left three carriers holding the redacted
    /// string in a document whose glyphs were perfectly removed — most visibly a
    /// bookmark title, which a reader shows in the navigation sidebar without the
    /// page ever being opened.
    /// </remarks>
    public void SanitizeMetadata(PdfDocument document, IEnumerable<string> terms) =>
        PdfDocumentSanitizer.ScrubTerms(document, terms);

    /// <summary>Remove the <c>/Info</c> dictionary entirely.</summary>
    public void StripAllMetadata(PdfDocument document)
    {
        if (document.Trailer.ContainsKey("Info"))
            document.Trailer.Remove("Info");
    }

    /// <summary>
    /// Append the visual-confirmation black rectangle as a fill op in
    /// the page's content stream. <c>q 0 0 0 rg X Y W H re f Q</c>.
    /// </summary>
    private static void AppendBlackRectangle(PdfPage page, PdfRectangle rect)
    {
        var content = page.GetContentStream();
        var ops = content.Operators.ToList();
        ops.Add(Excise.Core.Content.ContentOperator.SaveState());
        ops.Add(Excise.Core.Content.ContentOperator.SetFillRgb(0, 0, 0));
        ops.Add(Excise.Core.Content.ContentOperator.Rectangle(
            rect.Left, rect.Bottom, rect.Right - rect.Left, rect.Top - rect.Bottom));
        ops.Add(Excise.Core.Content.ContentOperator.Fill());
        ops.Add(Excise.Core.Content.ContentOperator.RestoreState());
        page.SetContentStream(new Excise.Core.Content.ContentStream(ops));
    }
}

/// <summary>Geometry helpers used to pre-filter letters before redaction.</summary>
internal static class RedactionMath
{
    public static bool Overlaps(PdfRectangle glyph, PdfRectangle area)
    {
        var g = glyph.Normalize();
        var a = area.Normalize();
        return g.IntersectsWith(a);
    }
}
