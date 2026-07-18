using Excise.Core.Document;
using Excise.Core.Graphics;

namespace Excise.Core.Editing;

/// <summary>
/// Flattens typewriter text overlays into page content streams.
/// </summary>
public static class PdfTypewriterTextApplier
{
    public static IReadOnlyList<PdfTypewriterTextOperation> Apply(
        PdfDocument document,
        IEnumerable<PdfTypewriterTextOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(operations);

        var applied = new List<PdfTypewriterTextOperation>();
        foreach (var operation in operations)
        {
            if (!operation.IsPending || !operation.HasText)
                continue;
            if (operation.PageNumber < 1 || operation.PageNumber > document.PageCount)
                throw new ArgumentOutOfRangeException(nameof(operations), $"Page {operation.PageNumber} is outside the document.");

            var page = document.GetPage(operation.PageNumber);
            var style = operation.Style;
            using var graphics = page.GetGraphics();
            graphics.DrawText(
                operation.Text,
                style.CreateFont(),
                style.CreateBrush(),
                operation.Bounds,
                style.Alignment,
                style.LineSpacing);

            applied.Add(operation.WithStatus(PdfEditOperationStatus.Applied));
        }

        return applied;
    }

    public static PdfTypewriterTextOperation Apply(
        PdfDocument document,
        PdfTypewriterTextOperation operation)
    {
        var applied = Apply(document, new[] { operation });
        return applied.Count == 0 ? operation : applied[0];
    }
}
