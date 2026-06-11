using System;
using Avalonia;
using Pdfe.Core.Document;

namespace PdfEditor.Models;

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
    /// Legacy viewer-space area accessor. Prefer <see cref="PageArea"/>.
    /// </summary>
    public Rect Area
    {
        get => new(PageArea.X, PageArea.Y, PageArea.Width, PageArea.Height);
        set => PageArea = PdfPageRect.ViewerDips(
            Math.Max(PageNumber, 1),
            value.X,
            value.Y,
            value.Width,
            value.Height,
            RenderDpi);
    }

    /// <summary>
    /// DPI of the rendered page coordinate space used by <see cref="Area"/>.
    /// The main viewer currently reports 120 DPI; older service paths default to 150 DPI.
    /// </summary>
    public int RenderDpi
    {
        get => (int)Math.Round(PageArea.Dpi);
        set => PageArea = PdfPageRect.ViewerDips(
            Math.Max(PageNumber, 1),
            PageArea.X,
            PageArea.Y,
            PageArea.Width,
            PageArea.Height,
            value);
    }

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
