using Microsoft.Extensions.Logging;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;

namespace Excise.App.Services;

/// <summary>
/// GUI-facing redaction orchestrator. A thin shell over Excise.Core:
/// delegates glyph/image removal to <see cref="PdfPageRedactionExtensions"/>.
/// Redacting a term through a file is <c>Excise.Ocr.TermRedactionRunner</c>,
/// the workflow the CLI runs too (#1501).
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
internal class RedactionService
{
    private readonly ILogger<RedactionService> _logger;

    public RedactionService(ILogger<RedactionService> logger, ILoggerFactory _)
    {
        _logger = logger;
    }

    // #897 removed `_redactedTerms` / `RedactedTerms` / `ClearRedactedTerms`.
    //
    // They existed to feed PdfDocumentSanitizer.ScrubTerms with words harvested
    // from whatever prose happened to fall inside a redaction box — a design
    // that corrupts the document it is meant to protect. ScrubTerms performs
    // plain substring replacement, so a box over one ordinary sentence yields
    // terms like `you got time file` and turns `Younger` into `Ynger` and
    // `profile` into `pro`.
    //
    // Area redaction now strips the positionless carriers WHOLESALE inside
    // Excise.Core's page.RedactArea (on by default): you cannot name what was in
    // the box, so remove the carriers rather than guess at their contents.
    //
    // Nothing in production read the list — it had test references only, which
    // is how a corrupting path survived this long. The clipboard history the app
    // shows is a separate mechanism (MainWindowViewModel.ClipboardHistory).

    /// <summary>
    /// Redact a rectangular area on <paramref name="page"/>.
    /// </summary>
    public void RedactArea(PdfPage page, PdfPageRect area, RedactionOptions options)
        => RedactMarks(page.Document, new[] { new RedactionServiceMark(page, area, FromTextSelection: false) }, options);

    /// <summary>
    /// Redact every mark as one engine batch (<see cref="AreaRedactionBatch"/>): the
    /// marks in order, then one edit of the fonts that takes the removed characters
    /// out of them (#2066). A mark made from a text selection also takes the
    /// decorations sized to its run (#1905).
    /// </summary>
    public void RedactMarks(PdfDocument document, IReadOnlyList<RedactionServiceMark> marks, RedactionOptions options)
    {
        var coreMarks = new List<AreaRedactionMark>(marks.Count);
        foreach (var (page, area, fromTextSelection) in marks)
        {
            var visualArea = PdfCoordinateMapper.ToVisualPoints(page, area);
            if (!IntersectsVisualPage(visualArea, page.VisualWidth, page.VisualHeight))
            {
                _logger.LogWarning(
                    "Selection area may be outside page bounds. Page: ({W}x{H}), Selection: ({X},{Y},{SW}x{SH})",
                    page.VisualWidth, page.VisualHeight, visualArea.X, visualArea.Y, visualArea.Width, visualArea.Height);
            }

            coreMarks.Add(new AreaRedactionMark(
                page, PdfCoordinateMapper.ToContentPoints(page, area).ToPdfRectangle(), fromTextSelection));
        }

        // The engine also strips the document's positionless carriers (/Info,
        // XMP) by default — see #897 and the note at the top of this class —
        // and, unless kept, every attachment (#1572). It draws the covering box.
        // It also flattens the AcroForm widgets excise generated for a dynamic XFA
        // form before anything else (#2028 decision 17, in the engine since #2037);
        // the row goes on the document's XFA removals, which the redacted-copy
        // report lists. A font it could not edit goes on the document's ledger,
        // which the redacted-copy report lists too.
        var batch = AreaRedactionBatch.Apply(document, coreMarks, options);
        foreach (var report in batch.Marks)
        foreach (var carrier in report.Carriers)
        {
            if (carrier.Carrier.StartsWith(Excise.Core.Xfa.PdfXfaLayout.GeneratedFieldsFlattenedRow, StringComparison.Ordinal))
                _logger.LogInformation("XFA: {Row}", carrier.Carrier);
        }

        foreach (var mark in coreMarks)
            _logger.LogInformation("Redacted {Kind} {Area} on page {Page}",
                mark.IsTextSelection ? "text selection" : "area", mark.Area, mark.Page.PageNumber);
        foreach (var refusal in batch.FontRefusals)
            _logger.LogWarning("Not scrubbed: {Carrier}", refusal.Carrier);
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
}

/// <summary>One pending mark to redact: its page, its area as captured, and whether it is a text selection (#1905).</summary>
internal readonly record struct RedactionServiceMark(PdfPage Page, PdfPageRect Area, bool FromTextSelection);
