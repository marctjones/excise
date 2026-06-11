namespace Pdfe.Core.Document;

/// <summary>
/// Page placement metadata for a form field widget annotation.
/// </summary>
public sealed class PdfFieldWidget
{
    public PdfFieldWidget(PdfRectangle rect, int? pageNumber, string? exportValue)
    {
        Rect = rect;
        PageNumber = pageNumber;
        ExportValue = exportValue;
    }

    /// <summary>The widget rectangle in PDF page coordinates.</summary>
    public PdfRectangle Rect { get; }

    /// <summary>The 1-based page number hosting the widget, when known.</summary>
    public int? PageNumber { get; }

    /// <summary>
    /// The widget's non-Off appearance state name, commonly the checkbox or
    /// radio export value. Null when the PDF does not declare one.
    /// </summary>
    public string? ExportValue { get; }
}
