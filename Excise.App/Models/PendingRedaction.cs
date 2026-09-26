using System;
using Excise.Core.Document;

namespace Excise.App.Models;

/// <summary>
/// Represents a redaction area that has been marked but not yet applied.
/// Part of mark-then-apply workflow.
/// </summary>
public class PendingRedaction
{
    /// <summary>
    /// Unique identifier for this pending redaction
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Page number (1-based) where redaction will be applied
    /// </summary>
    public int PageNumber { get; set; }

    /// <summary>
    /// Page-scoped area to redact. GUI-created redactions are usually
    /// <see cref="PdfCoordinateSpace.ViewerDips"/>; services convert this
    /// through <see cref="PdfCoordinateMapper"/> before mutating the PDF.
    /// </summary>
    public PdfPageRect PageArea { get; set; } =
        PdfPageRect.FromContentPoints(1, new PdfRectangle(0, 0, 0, 0));

    /// <summary>
    /// Preview of text that will be removed (for user review)
    /// </summary>
    public string PreviewText { get; set; } = string.Empty;

    /// <summary>
    /// When this redaction was marked
    /// </summary>
    public DateTime MarkedTime { get; set; } = DateTime.Now;

    /// <summary>
    /// User-friendly display text
    /// </summary>
    public string DisplayText =>
        $"Page {PageNumber}: {(string.IsNullOrWhiteSpace(PreviewText) ? "[Area]" : PreviewText)}";
}
