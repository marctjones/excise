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

        // #1671: refuse an edit whose font cannot represent its text BEFORE any
        // edit is drawn. Each edit is flushed to its page as it is applied, so
        // discovering the bad one third of five would leave two already baked in
        // and a save that reports failure.
        var pending = operations.Where(operation => operation.IsPending && operation.HasText).ToList();
        foreach (var operation in pending)
        {
            if (operation.PageNumber < 1 || operation.PageNumber > document.PageCount)
                throw new ArgumentOutOfRangeException(nameof(operations), $"Page {operation.PageNumber} is outside the document.");

            var font = operation.Style.CreateFont();
            font.EnsureCanEncode(operation.Text, $"typewriter text on page {operation.PageNumber}");
            // #1978: DrawText reports overflow, but by then Dispose would flush
            // partial text. Refuse the entire batch before touching any page.
            var lineCount = TextWrapper.Wrap(operation.Text, font, operation.Bounds.Width).Count();
            if (TextWrapper.CountLinesThatFit(lineCount, operation.Bounds,
                    font.Size * operation.Style.LineSpacing) < lineCount)
                throw new ArgumentException(
                    $"Typewriter text on page {operation.PageNumber} does not fit its box; resize the box or reduce the font size before saving.",
                    nameof(operations));
        }

        var applied = new List<PdfTypewriterTextOperation>();
        foreach (var operation in pending)
        {
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
