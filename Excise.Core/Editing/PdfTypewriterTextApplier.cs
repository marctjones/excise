using Excise.Core.Content;
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
            var (box, _) = UprightLayout(document.GetPage(operation.PageNumber), operation.Bounds);
            var lineCount = TextWrapper.Wrap(operation.Text, font, box.Width).Count();
            if (TextWrapper.CountLinesThatFit(lineCount, box,
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
            var (box, uprightToContent) = UprightLayout(page, operation.Bounds);
            using var graphics = page.GetGraphics();
            if (uprightToContent is { } m)
            {
                graphics.SaveState();
                graphics.Transform(m.A, m.B, m.C, m.D, m.E, m.F);
            }
            graphics.DrawText(
                operation.Text,
                style.CreateFont(),
                style.CreateBrush(),
                box,
                style.Alignment,
                style.LineSpacing);
            if (uprightToContent != null)
                graphics.RestoreState();

            applied.Add(operation.WithStatus(PdfEditOperationStatus.Applied));
        }

        return applied;
    }

    /// <summary>
    /// The box to lay the text out in, and the matrix that puts it on the page, so the
    /// text reads upright on the DISPLAYED page, as it was typed into the on-screen box
    /// (#1985). An unrotated page needs neither: the content box is used as it is and
    /// the output is unchanged. On a /Rotate page the content box is mapped into an
    /// upright frame (the displayed page with y up), laid out there, and that frame is
    /// mapped back through the page's own visual-to-content transform.
    /// </summary>
    private static (PdfRectangle Box, ContentTransform? UprightToContent) UprightLayout(
        PdfPage page, PdfRectangle contentBounds)
    {
        if (page.Rotation == 0)
            return (contentBounds, null);

        // Upright frame (x right, y up, displayed page) -> visual (y down) -> content.
        var flip = new ContentTransform(1, 0, 0, -1, 0, page.VisualHeight);
        var uprightToContent = flip.Multiply(page.VisualToContent);
        if (!uprightToContent.TryInvert(out var contentToUpright))
            return (contentBounds, null);
        return (contentToUpright.TransformBounds(contentBounds.Normalize()), uprightToContent);
    }

    public static PdfTypewriterTextOperation Apply(
        PdfDocument document,
        PdfTypewriterTextOperation operation)
    {
        var applied = Apply(document, new[] { operation });
        return applied.Count == 0 ? operation : applied[0];
    }
}
