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
    {
        var visualArea = PdfCoordinateMapper.ToVisualPoints(page, area);
        if (!IntersectsVisualPage(visualArea, page.VisualWidth, page.VisualHeight))
        {
            _logger.LogWarning(
                "Selection area may be outside page bounds. Page: ({W}x{H}), Selection: ({X},{Y},{SW}x{SH})",
                page.VisualWidth, page.VisualHeight, visualArea.X, visualArea.Y, visualArea.Width, visualArea.Height);
        }

        var coreRect = PdfCoordinateMapper.ToContentPoints(page, area).ToPdfRectangle();

        // The engine also strips the document's positionless carriers (/Info,
        // XMP) by default — see #897 and the note at the top of this class —
        // and, unless kept, every attachment (#1572). It draws the covering box.
        page.RedactArea(coreRect, options);

        _logger.LogInformation("Redacted area {Area} on page {Page}", coreRect, page.PageNumber);
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
