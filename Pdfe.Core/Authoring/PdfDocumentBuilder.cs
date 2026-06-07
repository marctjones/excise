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
    private PdfFont? _defaultFont;    // embedded font applied to text blocks (#398)
    private Tagging.StructureTreeBuilder? _tagging;                  // tagged-PDF tree (#275)
    private Tagging.StructureTreeBuilder.StructElem? _openElem;      // currently-open tagged block
    private string? _openTag;

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

    // ── document metadata (#381) ─────────────────────────────────────────────

    /// <summary>
    /// Enable tagged-PDF output (a logical structure tree for accessibility /
    /// PDF/UA, ISO 32000-2 §14.8): headings become H1-H4, paragraphs/key-values
    /// become P, tables become Table, each wrapped in marked content. Combine
    /// with <see cref="DefaultFont"/> (embedded fonts) and
    /// <see cref="Language"/> for an accessible document. Call before adding
    /// content.
    /// </summary>
    public PdfDocumentBuilder Tagged()
    {
        if (_tagging == null)
        {
            _tagging = new Tagging.StructureTreeBuilder(_document);
            _document.RegisterPreSaveAction(() => _tagging!.Write());
        }
        return this;
    }

    private static string HeadingTag(int level) => level switch
    {
        <= 1 => "H1", 2 => "H2", 3 => "H3", _ => "H4"
    };

    private void BeginTag(string structType, Tagging.StructureTreeBuilder.StructElem? parent = null)
    {
        if (_tagging == null) return;
        _openTag = structType;
        _openElem = _tagging.AddElement(structType, parent);
        OpenMarkedContent();
    }

    private void OpenMarkedContent()
    {
        if (_tagging == null || _openElem == null) return;
        int mcid = _tagging.AllocateMcid(_openElem, _currentPageNumber);
        _graphics!.BeginMarkedContent(_openTag!, mcid);
    }

    private void EndTag()
    {
        if (_tagging == null || _openElem == null) return;
        _graphics!.EndMarkedContent();
        _openElem = null;
        _openTag = null;
    }

    /// <summary>
    /// When tagging, add a Form structure element referencing the field's widget
    /// annotation (/OBJR) so the control appears in the accessibility tree (#407).
    /// </summary>
    private void TagFormField(Document.PdfField field)
    {
        if (_tagging == null) return;
        var widgetRef = _document.GetReferenceTo(field.RawDictionary);
        if (widgetRef != null)
            _tagging.AddObjectElement("Form", _currentPageNumber, widgetRef, field.RawDictionary);
    }

    /// <summary>
    /// Use an embedded font (e.g. <see cref="PdfFont.FromFile"/>) for all text
    /// blocks that don't specify their own — lets the builder render Unicode
    /// beyond the base-14 fonts (#398). The style's point size is applied to it.
    /// </summary>
    public PdfDocumentBuilder DefaultFont(PdfFont font) { _defaultFont = font; return this; }

    /// <summary>Apply the builder's default font to a style that has none.</summary>
    private TextStyle WithDefaultFont(TextStyle s) =>
        _defaultFont != null && s.Font == null ? s.WithFont(_defaultFont) : s;

    /// <summary>Sets the document title (Info <c>/Title</c>).</summary>
    public PdfDocumentBuilder Title(string title) { _document.SetTitle(title); return this; }

    /// <summary>Sets the document author (Info <c>/Author</c>).</summary>
    public PdfDocumentBuilder Author(string author) { _document.SetAuthor(author); return this; }

    /// <summary>Sets the document subject (Info <c>/Subject</c>).</summary>
    public PdfDocumentBuilder Subject(string subject) { _document.SetSubject(subject); return this; }

    /// <summary>Sets the document keywords (Info <c>/Keywords</c>).</summary>
    public PdfDocumentBuilder Keywords(string keywords) { _document.SetKeywords(keywords); return this; }

    /// <summary>
    /// Sets the document language as a BCP 47 tag (catalog <c>/Lang</c>, e.g.
    /// <c>"en-US"</c>) — required by PDF/UA for accessible documents.
    /// </summary>
    public PdfDocumentBuilder Language(string bcp47) { _document.Language = bcp47; return this; }

    // ── content blocks ──────────────────────────────────────────────────────

    /// <summary>
    /// Adds a heading. <paramref name="level"/> 1–4 maps to decreasing size;
    /// override the look entirely by passing a <paramref name="style"/>.
    /// </summary>
    public PdfDocumentBuilder Heading(string text, int level = 1, TextStyle? style = null)
        => ParagraphCore(text, WithDefaultFont(style ?? HeadingStyle(level)), HeadingTag(level));

    /// <summary>
    /// Adds a paragraph of text, word-wrapped to the content column and
    /// flowed across pages as needed. Honors hard line breaks in the input.
    /// </summary>
    public PdfDocumentBuilder Paragraph(string text, TextStyle? style = null)
        => ParagraphCore(text, WithDefaultFont(style ?? TextStyle.Body), "P");

    private PdfDocumentBuilder ParagraphCore(string text, TextStyle style, string structType)
    {
        EnsurePage();
        BeginTag(structType);

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

        EndTag();
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
        double ruleY = _cursorY;
        DrawArtifact(() => _graphics!.DrawLine(ContentLeft, ruleY, ContentLeft + ContentWidth, ruleY, pen));
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
        EnsurePage();
        BeginTag("P");
        Row(
            new[] { (label ?? string.Empty, labelStyle), (value ?? string.Empty, valueStyle) },
            new[] { labelWidth, 1 - labelWidth },
            cellPadding: 2,
            spaceAfter: 4);
        EndTag();
        return this;
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

        // Tagging: Table → TR (per row) → TD (per cell, with its own marked content).
        var tableElem = _tagging?.AddElement("Table");
        for (int i = 0; i < rowList.Count; i++)
        {
            var style = (headerRow && i == 0) ? TextStyle.Body.AsBold().WithSpaceAfter(0)
                                              : TextStyle.Body.WithSpaceAfter(0);
            var cells = new (string, TextStyle)[cols];
            for (int c = 0; c < cols; c++)
                cells[c] = (c < rowList[i].Count ? rowList[i][c] ?? string.Empty : string.Empty, style);

            var trElem = _tagging?.AddElement("TR", tableElem);
            // Header cells are TH, body cells TD.
            Row(cells, weights, cellPadding: 4, spaceAfter: 0, gridLines: gridLines,
                rowParent: trElem, cellStructType: headerRow && i == 0 ? "TH" : "TD");
        }

        _cursorY -= TextStyle.Body.SpaceAfter;
        return this;
    }

    /// <summary>Adds a bulleted list (one item per line, word-wrapped).</summary>
    public PdfDocumentBuilder BulletList(IEnumerable<string> items, string bullet = "•", TextStyle? style = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        return ListCore(items, _ => bullet, style);
    }

    /// <summary>Adds a numbered list (1., 2., 3., …).</summary>
    public PdfDocumentBuilder NumberedList(IEnumerable<string> items, TextStyle? style = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        return ListCore(items, i => $"{i + 1}.", style);
    }

    /// <summary>
    /// Renders a list. Each item gets a marker (drawn at the left) and word-wrapped
    /// body text indented past it. When tagging, emits L → LI per item.
    /// </summary>
    private PdfDocumentBuilder ListCore(IEnumerable<string> items, Func<int, string> marker, TextStyle? style)
    {
        var st = WithDefaultFont(style ?? TextStyle.Body);
        var font = st.ResolveFont();
        var brush = st.ResolveBrush();
        double lineHeight = st.LineHeight;
        const double indent = 18;

        EnsurePage();
        var listElem = _tagging?.AddElement("L");

        int i = 0;
        foreach (var item in items)
        {
            BeginTag("LI", listElem);          // opens marked content for this item
            EnsureSpace(lineHeight);
            double baseline = _cursorY - font.Ascender;
            _graphics!.DrawString(marker(i), font, brush, ContentLeft, baseline);

            var lines = WrapText(item ?? string.Empty, font, ContentWidth - indent).ToList();
            bool firstLine = true;
            foreach (var line in lines)
            {
                if (!firstLine) EnsureSpace(lineHeight);
                double lineBaseline = _cursorY - font.Ascender;
                _graphics!.DrawString(line, font, brush, ContentLeft + indent, lineBaseline);
                _cursorY -= lineHeight;
                firstLine = false;
            }
            EndTag();
            i++;
        }

        _cursorY -= st.SpaceAfter;
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
        string? defaultValue = null,
        string? tooltip = null,
        int? maxLength = null,
        bool comb = false)
    {
        EnsurePage();
        fieldName ??= NextFieldName("text");

        DrawFieldLabel(label, required);

        var bodyFont = TextStyle.Body.ResolveFont();
        double rows = multiline ? Math.Max(lines, 2) : 1;
        double boxHeight = rows * bodyFont.LineHeight + 6;

        var rect = ReserveBox(boxHeight);
        DrawBoxBorder(rect);
        // Default the accessible name (/TU) to the visible label so screen
        // readers announce the field even when no explicit tooltip is given.
        TagFormField(_document.AddTextField(_currentPageNumber, rect, fieldName,
            defaultValue: defaultValue, multiline: multiline, required: required,
            tooltip: tooltip ?? label, maxLength: maxLength, comb: comb));

        _cursorY -= TextStyle.Body.SpaceAfter;
        return this;
    }

    /// <summary>
    /// Adds a labelled date field — a text field with viewer-side date
    /// formatting (Acrobat <c>AFDate</c> actions). <paramref name="format"/>
    /// is an Acrobat date mask, e.g. <c>"yyyy-mm-dd"</c>.
    /// </summary>
    public PdfDocumentBuilder DateField(
        string label,
        string? fieldName = null,
        string format = "yyyy-mm-dd",
        bool required = false,
        string? tooltip = null)
    {
        EnsurePage();
        fieldName ??= NextFieldName("date");

        DrawFieldLabel(label, required);

        var bodyFont = TextStyle.Body.ResolveFont();
        double boxHeight = bodyFont.LineHeight + 6;
        var rect = ReserveBox(boxHeight);
        DrawBoxBorder(rect);
        TagFormField(_document.AddDateField(_currentPageNumber, rect, fieldName,
            format: format, required: required, tooltip: tooltip ?? $"{label} ({format})"));

        _cursorY -= TextStyle.Body.SpaceAfter;
        return this;
    }

    /// <summary>
    /// Adds a checkbox with a label to its right.
    /// </summary>
    public PdfDocumentBuilder CheckBox(
        string label,
        string? fieldName = null,
        bool checkedByDefault = false,
        string? tooltip = null)
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
        TagFormField(_document.AddCheckBox(_currentPageNumber, rect, fieldName,
            defaultChecked: checkedByDefault, tooltip: tooltip ?? label));

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
        string? defaultValue = null,
        string? tooltip = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        EnsurePage();
        fieldName ??= NextFieldName("choice");

        DrawFieldLabel(label, required: false);

        var bodyFont = TextStyle.Body.ResolveFont();
        double boxHeight = bodyFont.LineHeight + 6;
        var rect = ReserveBox(boxHeight);
        DrawBoxBorder(rect);
        TagFormField(_document.AddChoiceField(_currentPageNumber, rect, fieldName, options,
            defaultValue: defaultValue, tooltip: tooltip ?? label));

        _cursorY -= TextStyle.Body.SpaceAfter;
        return this;
    }

    /// <summary>
    /// Adds a table whose body cells are interactive AcroForm fields — a fillable
    /// grid. The first column is a non-editable row header; each remaining column
    /// holds a text input, checkbox, or dropdown (per <see cref="FillableTableCell.Kind"/>).
    /// Mirrors <see cref="Table"/>'s layout (column weights, gridlines, pagination)
    /// but places live fields instead of static text.
    /// </summary>
    /// <param name="columnHeaders">The data-column headers (excludes the row-label column).</param>
    /// <param name="rows">The body rows.</param>
    /// <param name="columnWeights">
    /// Relative widths for the data columns; null distributes evenly. The row-label
    /// column uses <paramref name="labelColumnWeight"/>.
    /// </param>
    /// <param name="labelColumnWeight">Relative width of the leading row-label column.</param>
    public PdfDocumentBuilder FillableTable(
        IReadOnlyList<string> columnHeaders,
        IReadOnlyList<FillableTableRow> rows,
        double[]? columnWeights = null,
        double labelColumnWeight = 1.0)
    {
        ArgumentNullException.ThrowIfNull(columnHeaders);
        ArgumentNullException.ThrowIfNull(rows);
        EnsurePage();

        int dataCols = columnHeaders.Count;
        int cols = dataCols + 1; // leading row-label column

        var rawWeights = new double[cols];
        rawWeights[0] = labelColumnWeight <= 0 ? 1.0 : labelColumnWeight;
        for (int c = 0; c < dataCols; c++)
            rawWeights[c + 1] = (columnWeights != null && c < columnWeights.Length) ? columnWeights[c] : 1.0;
        var weights = NormalizeWeights(rawWeights, cols);

        var tableElem = _tagging?.AddElement("Table");

        // Header row (static text): empty corner + the column headers.
        var headerCells = new (string, TextStyle)[cols];
        var headerStyle = TextStyle.Body.AsBold().WithSpaceAfter(0);
        headerCells[0] = (string.Empty, headerStyle);
        for (int c = 0; c < dataCols; c++)
            headerCells[c + 1] = (columnHeaders[c] ?? string.Empty, headerStyle);
        var headerTr = _tagging?.AddElement("TR", tableElem);
        Row(headerCells, weights, cellPadding: 4, spaceAfter: 0, gridLines: true, rowParent: headerTr, cellStructType: "TH");

        foreach (var row in rows)
            FillableRow(row, weights, tableElem);

        _cursorY -= TextStyle.Body.SpaceAfter;
        return this;
    }

    /// <summary>Renders one body row of a <see cref="FillableTable"/>: a text row
    /// header followed by an interactive field per data column.</summary>
    private void FillableRow(
        FillableTableRow row,
        double[] weights,
        Tagging.StructureTreeBuilder.StructElem? tableElem)
    {
        EnsurePage();

        int cols = weights.Length;
        var colWidths = new double[cols];
        for (int c = 0; c < cols; c++) colWidths[c] = weights[c] * ContentWidth;

        const double cellPadding = 4;
        var labelFont = TextStyle.Body.AsBold().ResolveFont();
        var fieldFont = TextStyle.Body.ResolveFont();
        double fieldHeight = fieldFont.LineHeight + 6;
        double rowHeight = fieldHeight + 2 * cellPadding;
        EnsureSpace(rowHeight);

        double top = _cursorY;
        var gridPen = new PdfPen(PdfColor.FromGray(0.7), 0.5);
        var trElem = _tagging?.AddElement("TR", tableElem);

        // Column 0: the row header (static text, tagged TH).
        double x = ContentLeft;
        bool tagCell = _tagging != null && trElem != null;
        if (tagCell)
        {
            var th = _tagging!.AddElement("TH", trElem);
            int mcid = _tagging.AllocateMcid(th, _currentPageNumber);
            _graphics!.BeginMarkedContent("TH", mcid);
        }
        double baseline = top - cellPadding - labelFont.Ascender;
        _graphics!.DrawString(row.Label ?? string.Empty, labelFont, PdfBrush.Black, x + cellPadding, baseline);
        if (tagCell) _graphics!.EndMarkedContent();
        DrawCellBorder(x, top, colWidths[0], rowHeight, gridPen);
        x += colWidths[0];

        // Data columns: a live field per cell.
        var cells = row.Cells;
        for (int c = 0; c < cols - 1; c++)
        {
            double cw = colWidths[c + 1];
            DrawCellBorder(x, top, cw, rowHeight, gridPen);

            if (cells != null && c < cells.Count)
            {
                var cell = cells[c];
                double fLeft = x + cellPadding;
                double fRight = x + cw - cellPadding;
                double fTop = top - cellPadding;
                double fBottom = fTop - fieldHeight;

                switch (cell.Kind)
                {
                    case FillableCellKind.CheckBox:
                        double boxSize = fieldFont.Size;
                        var boxRect = new PdfRectangle(fLeft, fTop - boxSize, fLeft + boxSize, fTop);
                        TagFormField(_document.AddCheckBox(_currentPageNumber, boxRect, cell.FieldName,
                            defaultChecked: IsCheckboxChecked(cell.Value), tooltip: cell.Tooltip));
                        break;

                    case FillableCellKind.Choice:
                        var choiceRect = new PdfRectangle(fLeft, fBottom, fRight, fTop);
                        TagFormField(_document.AddChoiceField(_currentPageNumber, choiceRect, cell.FieldName,
                            cell.Options ?? Array.Empty<string>(), defaultValue: cell.Value, tooltip: cell.Tooltip));
                        break;

                    default:
                        var textRect = new PdfRectangle(fLeft, fBottom, fRight, fTop);
                        TagFormField(_document.AddTextField(_currentPageNumber, textRect, cell.FieldName,
                            defaultValue: cell.Value, tooltip: cell.Tooltip));
                        break;
                }
            }
            x += cw;
        }

        _cursorY = top - rowHeight;
    }

    private void DrawCellBorder(double left, double top, double width, double height, PdfPen pen) =>
        DrawArtifact(() => _graphics!.DrawRectangle(left, top - height, width, height, fill: null, stroke: pen));

    private static readonly HashSet<string> CheckboxTruthy =
        new(StringComparer.OrdinalIgnoreCase) { "true", "yes", "y", "1", "x", "checked", "on" };

    private static bool IsCheckboxChecked(string? value) =>
        value != null && CheckboxTruthy.Contains(value.Trim());

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
        // A marked-content sequence can't span pages: close it on the outgoing
        // page and reopen a fresh MCID for the same element on the new page.
        bool reopen = _tagging != null && _openElem != null && _graphics != null;
        if (reopen) _graphics!.EndMarkedContent();

        _graphics?.Flush();
        _currentPage = _document.Pages.AddBlank(_pageSize.Width, _pageSize.Height);
        _currentPageNumber = _document.PageCount;
        _graphics = _currentPage.GetGraphics();
        _cursorY = _pageSize.Height - _margins.Top;

        if (reopen) OpenMarkedContent();
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
        DrawArtifact(() => _graphics!.DrawRectangle(rect.Left, rect.Bottom, rect.Width, rect.Height, fill: null, stroke: pen));
    }

    /// <summary>
    /// Draw decorative (non-content) graphics, wrapped as a PDF artifact when
    /// tagging is on so it stays out of the structure tree (PDF/UA).
    /// </summary>
    private void DrawArtifact(Action draw)
    {
        if (_tagging != null) _graphics!.BeginArtifact();
        draw();
        if (_tagging != null) _graphics!.EndMarkedContent();
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
        bool gridLines = false,
        Tagging.StructureTreeBuilder.StructElem? rowParent = null,
        string cellStructType = "TD")
    {
        EnsurePage();

        int cols = cells.Count;
        var colWidths = new double[cols];
        var styles = new TextStyle[cols];
        for (int c = 0; c < cols; c++)
        {
            colWidths[c] = weights[c] * ContentWidth;
            styles[c] = WithDefaultFont(cells[c].Style);
        }

        // Wrap each cell and find the tallest.
        var wrapped = new List<string>[cols];
        double maxLines = 1;
        for (int c = 0; c < cols; c++)
        {
            var font = styles[c].ResolveFont();
            wrapped[c] = WrapText(cells[c].Text ?? string.Empty, font, colWidths[c] - 2 * cellPadding).ToList();
            maxLines = Math.Max(maxLines, wrapped[c].Count);
        }

        double lineHeight = cols > 0 ? styles[0].LineHeight : TextStyle.Body.LineHeight;
        double rowHeight = maxLines * lineHeight + 2 * cellPadding;
        EnsureSpace(rowHeight);

        double top = _cursorY;
        double x = ContentLeft;
        for (int c = 0; c < cols; c++)
        {
            var style = styles[c];
            var font = style.ResolveFont();
            var brush = style.ResolveBrush();

            // Per-cell marked content tied to a TD/TH element (tables only).
            bool tagCell = _tagging != null && rowParent != null;
            if (tagCell)
            {
                var td = _tagging!.AddElement(cellStructType, rowParent);
                int mcid = _tagging.AllocateMcid(td, _currentPageNumber);
                _graphics!.BeginMarkedContent(cellStructType, mcid);
            }

            double textTop = top - cellPadding;
            for (int li = 0; li < wrapped[c].Count; li++)
            {
                double baseline = textTop - font.Ascender - li * lineHeight;
                _graphics!.DrawString(wrapped[c][li], font, brush, x + cellPadding, baseline);
            }
            if (tagCell) _graphics!.EndMarkedContent();

            if (gridLines)
            {
                var rect = new PdfRectangle(x, top - rowHeight, x + colWidths[c], top);
                var pen = new PdfPen(PdfColor.FromGray(0.7), 0.5);
                DrawArtifact(() => _graphics!.DrawRectangle(rect.Left, rect.Bottom, rect.Width, rect.Height, fill: null, stroke: pen));
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
    /// own metrics (delegates to the shared <see cref="TextWrapper"/>).
    /// </summary>
    internal static IEnumerable<string> WrapText(string text, PdfFont font, double maxWidth)
        => TextWrapper.Wrap(text, font, maxWidth);
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
