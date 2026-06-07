namespace Pdfe.Core.Authoring;

/// <summary>
/// The kind of interactive widget a <see cref="FillableTableCell"/> renders as
/// inside <see cref="PdfDocumentBuilder.FillableTable"/>.
/// </summary>
public enum FillableCellKind
{
    /// <summary>A single-line text input.</summary>
    Text,

    /// <summary>A checkbox (checked when the cell's value is truthy).</summary>
    CheckBox,

    /// <summary>A dropdown (combo box) populated from <see cref="FillableTableCell.Options"/>.</summary>
    Choice,
}

/// <summary>
/// One cell of a <see cref="FillableTableRow"/>: an interactive form field placed
/// within a table cell by <see cref="PdfDocumentBuilder.FillableTable"/>.
/// </summary>
/// <param name="FieldName">The AcroForm field name (must be unique in the document).</param>
/// <param name="Kind">Which widget to render.</param>
/// <param name="Value">The field's default value (for checkboxes, a truthy string checks it).</param>
/// <param name="Options">The dropdown options when <see cref="Kind"/> is <see cref="FillableCellKind.Choice"/>.</param>
/// <param name="Tooltip">The field's accessible name (<c>/TU</c>); shown by AT and on hover.</param>
public sealed record FillableTableCell(
    string FieldName,
    FillableCellKind Kind = FillableCellKind.Text,
    string? Value = null,
    IReadOnlyList<string>? Options = null,
    string? Tooltip = null);

/// <summary>
/// One body row of a <see cref="PdfDocumentBuilder.FillableTable"/>: a row header
/// label plus an interactive field per data column.
/// </summary>
/// <param name="Label">The row header text (rendered in the first, non-editable column).</param>
/// <param name="Cells">The editable cells, in column order.</param>
public sealed record FillableTableRow(
    string Label,
    IReadOnlyList<FillableTableCell> Cells);
