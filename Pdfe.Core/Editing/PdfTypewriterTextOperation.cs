using Pdfe.Core.Document;

namespace Pdfe.Core.Editing;

/// <summary>
/// Editable text overlay that can be flattened into a page content stream.
/// </summary>
public sealed record PdfTypewriterTextOperation
{
    private PdfTypewriterTextOperation(
        PdfEditOperation editOperation,
        string text,
        PdfTypewriterTextStyle style)
    {
        ArgumentNullException.ThrowIfNull(editOperation);
        ArgumentNullException.ThrowIfNull(style);
        if (editOperation.Kind != PdfEditOperationKind.TypewriterText)
            throw new ArgumentException("Edit operation must be TypewriterText.", nameof(editOperation));

        EditOperation = editOperation;
        Text = text ?? string.Empty;
        Style = style;
    }

    public PdfEditOperation EditOperation { get; }
    public Guid Id => EditOperation.Id;
    public int PageNumber => EditOperation.PageNumber;
    public PdfRectangle Bounds => EditOperation.Bounds;
    public PdfEditOperationStatus Status => EditOperation.Status;
    public bool IsPending => EditOperation.IsPending;
    public string Text { get; }
    public PdfTypewriterTextStyle Style { get; }
    public bool HasText => !string.IsNullOrWhiteSpace(Text);

    public static PdfTypewriterTextOperation Create(
        int pageNumber,
        PdfRectangle bounds,
        string text,
        PdfTypewriterTextStyle? style = null)
    {
        var editOperation = PdfEditOperation.Create(
            PdfEditOperationKind.TypewriterText,
            pageNumber,
            bounds,
            canFlatten: true,
            description: "Typewriter text");

        return new PdfTypewriterTextOperation(editOperation, text, style ?? PdfTypewriterTextStyle.Default);
    }

    public PdfTypewriterTextOperation WithText(string text) =>
        new(EditOperation, text, Style);

    public PdfTypewriterTextOperation WithBounds(PdfRectangle bounds) =>
        new(EditOperation.WithBounds(bounds), Text, Style);

    public PdfTypewriterTextOperation WithPageAndBounds(int pageNumber, PdfRectangle bounds) =>
        new(EditOperation.WithPageAndBounds(pageNumber, bounds), Text, Style);

    public PdfTypewriterTextOperation WithStatus(PdfEditOperationStatus status) =>
        new(EditOperation.WithStatus(status), Text, Style);

    public PdfTypewriterTextOperation WithStyle(PdfTypewriterTextStyle style) =>
        new(EditOperation, Text, style);
}
