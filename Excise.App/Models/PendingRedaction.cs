using System;
using Excise.Core.Document;
using Excise.Core.Primitives;

namespace Excise.App.Models;

/// <summary>
/// Represents a redaction area that has been marked but not yet applied.
/// Part of mark-then-apply workflow.
/// </summary>
internal class PendingRedaction
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
    /// The page node this mark was drawn on. <see cref="PageNumber"/> is only where that page
    /// sits NOW: delete, move or insert pages (or undo one) and the number changes while the
    /// page, and the text on it, does not. Null for a mark made with no document to anchor to.
    /// </summary>
    public PdfDictionary? PageIdentity { get; set; }

    /// <summary>
    /// True when the page this mark was drawn on is no longer in the document (it was deleted;
    /// undo brings it, and the mark, back). Such a mark redacts nothing.
    /// </summary>
    public bool IsOnRemovedPage { get; set; }

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
        $"{(IsOnRemovedPage ? "Removed page" : $"Page {PageNumber}")}: {(string.IsNullOrWhiteSpace(PreviewText) ? "[Area]" : PreviewText)}";
}
