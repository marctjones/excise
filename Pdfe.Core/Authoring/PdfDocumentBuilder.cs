using Pdfe.Core.Document;
using Pdfe.Core.Graphics;

namespace Pdfe.Core.Authoring;

/// <summary>
/// A friendly, high-level API for authoring PDFs from structured content
/// without touching raw coordinates or content-stream operators.
///
/// <para>
/// Content flows top-to-bottom inside the page's content area (page minus
/// <see cref="PageMargins"/>); the builder wraps text, advances a cursor, and
/// adds pages automatically when a block would overflow. It sits on top of the
/// low-level <see cref="PdfGraphics"/> / <see cref="AcroFormAuthoring"/> API —
/// drop down to those (via <see cref="Custom"/> or <see cref="Build"/>) when
/// you need full control.
/// </para>
///
/// <example>
/// <code>
/// var bytes = PdfDocumentBuilder.Create()
///     .Heading("Application")
///     .Paragraph("Please complete every field.")
///     .KeyValue("Name", "Ada Lovelace")
///     .TextField("Comments", "comments", multiline: true)
///     .SaveToBytes();
/// </code>
/// </example>
///
/// <remarks>
/// This is the writer-side facade tracked by issue #383. It targets the
/// base-14 fonts and Latin text available today; Unicode/embedded fonts
/// (#378), richer layout (#379), more AcroForm options (#380) and document
/// metadata (#381) extend it.
/// </remarks>
/// </summary>
public sealed class PdfDocumentBuilder
{
    private readonly PdfDocument _document;
    private readonly PageSize _pageSize;
    private readonly PageMargins _margins;

    private PdfPage? _currentPage;
    private PdfGraphics? _graphics;
    private int _currentPageNumber;   // 1-based
    private double _cursorY;          // PDF coords (bottom-left origin), top of next block
    private int _autoFieldSeq;

    private PdfDocumentBuilder(PageSize pageSize, PageMargins margins)
    {
        _pageSize = pageSize;
        _margins = margins;
        _document = PdfDocument.CreateNew();
    }

    /// <summary>
    /// Starts a new document. Defaults to US Letter with 1-inch margins.
    /// </summary>
    public static PdfDocumentBuilder Create(PageSize? pageSize = null, PageMargins? margins = null) =>
        new(pageSize ?? PageSize.Letter, margins ?? PageMargins.Default);

    /// <summary>The left edge of the content column, in PDF points.</summary>
    public double ContentLeft => _margins.Left;

    /// <summary>The width of the content column, in PDF points.</summary>
    public double ContentWidth => _pageSize.Width - _margins.Left - _margins.Right;

    /// <summary>The number of pages added so far.</summary>
    public int PageCount => _document.PageCount;

    // ── content blocks ──────────────────────────────────────────────────────

    /// <summary>
    /// Adds a heading. <paramref name="level"/> 1–4 maps to decreasing size;
    /// override the look entirely by passing a <paramref name="style"/>.
    /// </summary>
    public PdfDocumentBuilder Heading(string text, int level = 1, TextStyle? style = null)
    {
        style ??= HeadingStyle(level);
        return Paragraph(text, style);
    }

    /// <summary>
    /// Adds a paragraph of text, word-wrapped to the content column and
    /// flowed across pages as needed. Honors hard line breaks in the input.
    /// </summary>
    public PdfDocumentBuilder Paragraph(string text, TextStyle? style = null)
    {
        style ??= TextStyle.Body;
        EnsurePage();

        var font = style.ResolveFont();
        var brush = style.ResolveBrush();
        double lineHeight = style.LineHeight;

        foreach (var line in WrapText(text ?? string.Empty, font, ContentWidth))
        {
            EnsureSpace(lineHeight);
            // Baseline sits one ascent below the top of the line box.
            double baseline = _cursorY - font.Ascender;
            DrawAligned(line, font, brush, style.Alignment, baseline);
            _cursorY -= lineHeight;
        }

        _cursorY -= style.SpaceAfter;
        return this;
    }

    /// <summary>Adds vertical blank space, in points.</summary>
    public PdfDocumentBuilder Spacer(double points)
    {
        EnsurePage();
        _cursorY -= points;
        return this;
    }

    /// <summary>Draws a horizontal rule across the content column.</summary>
    public PdfDocumentBuilder HorizontalRule(double thickness = 0.5, PdfColor? color = null)
    {
        EnsurePage();
        EnsureSpace(thickness + 6);
        _cursorY -= 3;
        var pen = new PdfPen(color ?? PdfColor.FromGray(0.6), thickness);
        _graphics!.DrawLine(ContentLeft, _cursorY, ContentLeft + ContentWidth, _cursorY, pen);
        _cursorY -= 3;
        return this;
    }

    /// <summary>
    /// Adds a "label: value" row — the label bold on the left, the value
    /// wrapped on the right. <paramref name="labelWidth"/> is the left
    /// column width as a fraction (0–1) of the content column.
    /// </summary>
    public PdfDocumentBuilder KeyValue(string label, string value, double labelWidth = 0.3)
    {
        labelWidth = Math.Clamp(labelWidth, 0.1, 0.9);
        var labelStyle = TextStyle.Body.AsBold().WithSpaceAfter(0);
        var valueStyle = TextStyle.Body.WithSpaceAfter(0);
        return Row(
            new[] { (label ?? string.Empty, labelStyle), (value ?? string.Empty, valueStyle) },
            new[] { labelWidth, 1 - labelWidth },
            cellPadding: 2,
            spaceAfter: 4);
    }

    /// <summary>
    /// Adds a table. <paramref name="columnWeights"/> are relative widths
    /// (normalized to the content column); omit to split evenly. The first
    /// row can optionally be drawn as a bold header.
    /// </summary>
    public PdfDocumentBuilder Table(
        IEnumerable<IReadOnlyList<string>> rows,
        double[]? columnWeights = null,
        bool headerRow = false,
        bool gridLines = true)
    {
        ArgumentNullException.ThrowIfNull(rows);
        EnsurePage();

        var rowList = rows.Select(r => (IReadOnlyList<string>)r.ToList()).ToList();
        if (rowList.Count == 0)
            return this;

        int cols = rowList.Max(r => r.Count);
        double[] weights = NormalizeWeights(columnWeights, cols);

        for (int i = 0; i < rowList.Count; i++)
        {
            var style = (headerRow && i == 0) ? TextStyle.Body.AsBold().WithSpaceAfter(0)
                                              : TextStyle.Body.WithSpaceAfter(0);
            var cells = new (string, TextStyle)[cols];
            for (int c = 0; c < cols; c++)
                cells[c] = (c < rowList[i].Count ? rowList[i][c] ?? string.Empty : string.Empty, style);

            Row(cells, weights, cellPadding: 4, spaceAfter: 0, gridLines: gridLines);
        }

        _cursorY -= TextStyle.Body.SpaceAfter;
        return this;
    }

    /// <summary>Forces subsequent content onto a new page.</summary>
    public PdfDocumentBuilder PageBreak()
    {
        NewPage();
        return this;
    }

    // ── form fields (fillable AcroForm) ─────────────────────────────────────

    /// <summary>
    /// Adds a labelled text input field. The label is drawn above the input
    /// box. Set <paramref name="multiline"/> for a taller box.
    /// </summary>
    public PdfDocumentBuilder TextField(
        string label,
        string? fieldName = null,
        bool multiline = false,
        bool required = false,
        int lines = 1,
        string? defaultValue = null)
    {
        EnsurePage();
        fieldName ??= NextFieldName("text");

        DrawFieldLabel(label, required);

        var bodyFont = TextStyle.Body.ResolveFont();
        double rows = multiline ? Math.Max(lines, 2) : 1;
        double boxHeight = rows * bodyFont.LineHeight + 6;

        var rect = ReserveBox(boxHeight);
        DrawBoxBorder(rect);
        _document.AddTextField(_currentPageNumber, rect, fieldName,
            defaultValue: defaultValue, multiline: multiline, required: required);

        _cursorY -= TextStyle.Body.SpaceAfter;
        return this;
    }

    /// <summary>
    /// Adds a checkbox with a label to its right.
    /// </summary>
    public PdfDocumentBuilder CheckBox(
        string label,
        string? fieldName = null,
        bool checkedByDefault = false)
    {
        EnsurePage();
        fieldName ??= NextFieldName("check");

        var font = TextStyle.Body.ResolveFont();
        double box = font.Size;                 // square checkbox sized to the text
        double rowHeight = Math.Max(box, font.LineHeight) + 4;
        EnsureSpace(rowHeight);

        double top = _cursorY;
        double bottom = top - box;
        var rect = new PdfRectangle(ContentLeft, bottom, ContentLeft + box, top);
        DrawBoxBorder(rect);
        _document.AddCheckBox(_currentPageNumber, rect, fieldName, defaultChecked: checkedByDefault);

        // Label baseline aligned to the checkbox.
        double baseline = top - font.Ascender;
        _graphics!.DrawString(label ?? string.Empty, font, PdfBrush.Black, ContentLeft + box + 6, baseline);

        _cursorY -= rowHeight;
        return this;
    }

    /// <summary>
    /// Adds a labelled dropdown (combo box) populated with
    /// <paramref name="options"/>.
    /// </summary>
    public PdfDocumentBuilder Dropdown(
        string label,
        IEnumerable<string> options,
        string? fieldName = null,
        string? defaultValue = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        EnsurePage();
        fieldName ??= NextFieldName("choice");

        DrawFieldLabel(label, required: false);

        var bodyFont = TextStyle.Body.ResolveFont();
        double boxHeight = bodyFont.LineHeight + 6;
        var rect = ReserveBox(boxHeight);
        DrawBoxBorder(rect);
        _document.AddChoiceField(_currentPageNumber, rect, fieldName, options, defaultValue: defaultValue);

        _cursorY -= TextStyle.Body.SpaceAfter;
        return this;
    }

    /// <summary>
    /// Escape hatch: draw directly with <see cref="PdfGraphics"/>. The callback
    /// receives the current page's graphics and a layout snapshot (content
    /// box + cursor). Advance the cursor yourself via the returned height by
    /// calling <see cref="Spacer"/> afterward if needed.
    /// </summary>
    public PdfDocumentBuilder Custom(Action<PdfGraphics, LayoutContext> draw)
    {
        ArgumentNullException.ThrowIfNull(draw);
        EnsurePage();
        draw(_graphics!, new LayoutContext(ContentLeft, _cursorY, ContentWidth, _cursorY - _margins.Bottom, _currentPageNumber));
        return this;
    }

    // ── output ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Finalizes layout and returns the underlying <see cref="PdfDocument"/>
    /// for further manipulation or saving.
    /// </summary>
    public PdfDocument Build()
    {
        _graphics?.Flush();
        return _document;
    }

    /// <summary>Builds and serializes the document to a byte array.</summary>
    public byte[] SaveToBytes() => Build().SaveToBytes();

    /// <summary>Builds and writes the document to a file.</summary>
    public void Save(string path) => Build().Save(path);

    /// <summary>Builds and writes the document to a stream.</summary>
    public void Save(Stream stream) => Build().Save(stream);

    // ── layout engine ───────────────────────────────────────────────────────

    private void EnsurePage()
    {
        if (_currentPage == null)
            NewPage();
    }

    private void NewPage()
    {
        _graphics?.Flush();
        _currentPage = _document.Pages.AddBlank(_pageSize.Width, _pageSize.Height);
        _currentPageNumber = _document.PageCount;
        _graphics = _currentPage.GetGraphics();
        _cursorY = _pageSize.Height - _margins.Top;
    }

    /// <summary>Ensure at least <paramref name="height"/> remains; else paginate.</summary>
    private void EnsureSpace(double height)
    {
        EnsurePage();
        if (_cursorY - height < _margins.Bottom)
            NewPage();
    }

    /// <summary>Reserve a full-width box of the given height; returns its rect.</summary>
    private PdfRectangle ReserveBox(double height)
    {
        EnsureSpace(height);
        double top = _cursorY;
        double bottom = top - height;
        _cursorY = bottom;
        return new PdfRectangle(ContentLeft, bottom, ContentLeft + ContentWidth, top);
    }

    private void DrawAligned(string text, PdfFont font, PdfBrush brush, TextAlignment alignment, double baseline)
    {
        double x = alignment switch
        {
            TextAlignment.Center => ContentLeft + ContentWidth / 2,
            TextAlignment.Right => ContentLeft + ContentWidth,
            _ => ContentLeft
        };
        _graphics!.DrawString(text, font, brush, x, baseline, alignment);
    }

    private void DrawFieldLabel(string label, bool required)
    {
        var style = TextStyle.Body.AsBold().WithSpaceAfter(2);
        Paragraph(required ? $"{label} *" : label, style);
    }

    private void DrawBoxBorder(PdfRectangle rect)
    {
        var pen = new PdfPen(PdfColor.FromGray(0.5), 0.75);
        _graphics!.DrawRectangle(rect.Left, rect.Bottom, rect.Width, rect.Height, fill: null, stroke: pen);
    }

    /// <summary>
    /// Draws a single multi-column row, wrapping each cell within its column.
    /// Used by <see cref="KeyValue"/> and <see cref="Table"/>.
    /// </summary>
    private PdfDocumentBuilder Row(
        IReadOnlyList<(string Text, TextStyle Style)> cells,
        double[] weights,
        double cellPadding,
        double spaceAfter,
        bool gridLines = false)
    {
        EnsurePage();

        int cols = cells.Count;
        var colWidths = new double[cols];
        for (int c = 0; c < cols; c++)
            colWidths[c] = weights[c] * ContentWidth;

        // Wrap each cell and find the tallest.
        var wrapped = new List<string>[cols];
        double maxLines = 1;
        for (int c = 0; c < cols; c++)
        {
            var font = cells[c].Style.ResolveFont();
            wrapped[c] = WrapText(cells[c].Text ?? string.Empty, font, colWidths[c] - 2 * cellPadding).ToList();
            maxLines = Math.Max(maxLines, wrapped[c].Count);
        }

        double lineHeight = cells.Count > 0 ? cells[0].Style.LineHeight : TextStyle.Body.LineHeight;
        double rowHeight = maxLines * lineHeight + 2 * cellPadding;
        EnsureSpace(rowHeight);

        double top = _cursorY;
        double x = ContentLeft;
        for (int c = 0; c < cols; c++)
        {
            var style = cells[c].Style;
            var font = style.ResolveFont();
            var brush = style.ResolveBrush();
            double textTop = top - cellPadding;
            for (int li = 0; li < wrapped[c].Count; li++)
            {
                double baseline = textTop - font.Ascender - li * lineHeight;
                _graphics!.DrawString(wrapped[c][li], font, brush, x + cellPadding, baseline);
            }

            if (gridLines)
            {
                var rect = new PdfRectangle(x, top - rowHeight, x + colWidths[c], top);
                var pen = new PdfPen(PdfColor.FromGray(0.7), 0.5);
                _graphics!.DrawRectangle(rect.Left, rect.Bottom, rect.Width, rect.Height, fill: null, stroke: pen);
            }

            x += colWidths[c];
        }

        _cursorY = top - rowHeight - spaceAfter;
        return this;
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static double[] NormalizeWeights(double[]? weights, int cols)
    {
        if (weights == null || weights.Length == 0)
        {
            var even = new double[cols];
            Array.Fill(even, 1.0 / cols);
            return even;
        }

        var w = new double[cols];
        double sum = 0;
        for (int c = 0; c < cols; c++)
        {
            double v = c < weights.Length ? Math.Max(0, weights[c]) : 0;
            w[c] = v;
            sum += v;
        }
        if (sum <= 0)
        {
            Array.Fill(w, 1.0 / cols);
            return w;
        }
        for (int c = 0; c < cols; c++)
            w[c] /= sum;
        return w;
    }

    private static TextStyle HeadingStyle(int level) => level switch
    {
        <= 1 => new TextStyle { Size = 18, Bold = true, SpaceAfter = 10 },
        2 => new TextStyle { Size = 14, Bold = true, SpaceAfter = 8 },
        3 => new TextStyle { Size = 12, Bold = true, SpaceAfter = 6 },
        _ => new TextStyle { Size = 11, Bold = true, SpaceAfter = 5 }
    };

    private string NextFieldName(string prefix) => $"{prefix}_{++_autoFieldSeq}";

    /// <summary>
    /// Greedy word-wrap to <paramref name="maxWidth"/> points using the font's
    /// own metrics. Hard line breaks (\n, \r\n) in the input are preserved; a
    /// single word longer than the column is emitted on its own (over)long line.
    /// </summary>
    internal static IEnumerable<string> WrapText(string text, PdfFont font, double maxWidth)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield return string.Empty;
            yield break;
        }

        var hardLines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        foreach (var hardLine in hardLines)
        {
            if (hardLine.Length == 0)
            {
                yield return string.Empty;
                continue;
            }

            var words = hardLine.Split(' ');
            var current = new System.Text.StringBuilder();
            foreach (var word in words)
            {
                if (current.Length == 0)
                {
                    current.Append(word);
                    continue;
                }

                var candidate = current.ToString() + " " + word;
                if (maxWidth > 0 && font.MeasureWidth(candidate) > maxWidth)
                {
                    yield return current.ToString();
                    current.Clear();
                    current.Append(word);
                }
                else
                {
                    current.Append(' ').Append(word);
                }
            }

            if (current.Length > 0)
                yield return current.ToString();
        }
    }
}

/// <summary>
/// A snapshot of the current layout passed to <see cref="PdfDocumentBuilder.Custom"/>.
/// Coordinates are in PDF points (bottom-left origin).
/// </summary>
/// <param name="Left">Left edge of the content column.</param>
/// <param name="Top">Y of the current cursor (top of the next block).</param>
/// <param name="Width">Width of the content column.</param>
/// <param name="RemainingHeight">Space remaining above the bottom margin.</param>
/// <param name="PageNumber">Current page number (1-based).</param>
public readonly record struct LayoutContext(double Left, double Top, double Width, double RemainingHeight, int PageNumber);
