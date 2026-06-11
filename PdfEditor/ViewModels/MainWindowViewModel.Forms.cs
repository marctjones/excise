using Microsoft.Extensions.Logging;
using Pdfe.Avalonia.Controls;
using Pdfe.Core.Document;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PdfEditor.ViewModels;

public partial class MainWindowViewModel
{
    public InteractionMode InteractionMode
    {
        get
        {
            if (IsRedactionMode) return InteractionMode.Redaction;
            if (IsTextSelectionMode) return InteractionMode.TextSelection;
            if (IsFormAuthoringMode) return InteractionMode.FormAuthoring;
            return InteractionMode.None;
        }
    }

    private bool _isFormAuthoringMode;

    /// <summary>
    /// When true, dragging on the page draws a new AcroForm field rect of
    /// type <see cref="FormAuthoringFieldType"/>.
    /// </summary>
    public bool IsFormAuthoringMode
    {
        get => _isFormAuthoringMode;
        set
        {
            this.RaiseAndSetIfChanged(ref _isFormAuthoringMode, value);
            if (value)
            {
                ViewMode = PdfViewMode.SinglePage;
                if (_isRedactionMode) IsRedactionMode = false;
                if (_isTextSelectionMode) IsTextSelectionMode = false;
            }

            this.RaisePropertyChanged(nameof(InteractionMode));
            this.RaisePropertyChanged(nameof(CurrentModeText));
        }
    }

    private PdfFieldType _formAuthoringFieldType = PdfFieldType.Text;

    /// <summary>
    /// Field type the next drag-rect should produce when authoring.
    /// </summary>
    public PdfFieldType FormAuthoringFieldType
    {
        get => _formAuthoringFieldType;
        set => this.RaiseAndSetIfChanged(ref _formAuthoringFieldType, value);
    }

    /// <summary>
    /// AcroForm fields whose widget annotations are on the currently
    /// displayed page. Bound to PdfViewerControl.FormFields so the user can
    /// edit values inline. Empty when the document has no AcroForm.
    /// </summary>
    public IReadOnlyList<PdfField> CurrentPageFormFields
    {
        get
        {
            if (_pdfCoreDocument == null || _currentPageIndex < 0 || _currentPageIndex >= TotalPages)
                return Array.Empty<PdfField>();
            try
            {
                return _pdfCoreDocument.GetPage(_currentPageIndex + 1).GetFormFields();
            }
            catch
            {
                return Array.Empty<PdfField>();
            }
        }
    }

    /// <summary>
    /// Called by MainWindow when PdfViewerControl raises FormFieldEdited.
    /// The viewer has already mutated the field value via PdfField.SetValue,
    /// so all that remains is to mark the document dirty so the Save command
    /// activates. The form-fill overlay reflects the new value already; the
    /// underlying bitmap is left as-is (the user sees the text in the input
    /// box, not a rasterized appearance, until they save and re-open).
    /// </summary>
    public void OnFormFieldEdited(string fieldName, string? newValue)
    {
        if (_pdfCoreDocument == null) return;
        FileState.FormFieldEditsCount++;
        _logger.LogInformation("Form field '{Field}' set to '{Value}'", fieldName, newValue);
    }

    /// <summary>
    /// Called by MainWindow when the viewer raises FormFieldRectDrawn.
    /// Materialises a real form field via the AcroFormAuthoring API and
    /// refreshes the on-screen overlay so the new field is immediately
    /// editable.
    /// </summary>
    public void OnFormFieldRectDrawn(PdfRectangle rect, int pageNumber)
    {
        if (_pdfCoreDocument == null) return;
        try
        {
            var name = NextUniqueFieldName(_pdfCoreDocument, FormAuthoringFieldType);
            switch (FormAuthoringFieldType)
            {
                case PdfFieldType.Text:
                    _pdfCoreDocument.AddTextField(pageNumber, rect, name);
                    break;
                case PdfFieldType.Button:
                    _pdfCoreDocument.AddCheckBox(pageNumber, rect, name);
                    break;
                case PdfFieldType.Signature:
                    _pdfCoreDocument.AddSignatureField(pageNumber, rect, name);
                    break;
                case PdfFieldType.Choice:
                    // Choice fields need /Opt; default to two placeholder
                    // options so the field is at least addressable from
                    // the GUI. The user is expected to edit /Opt later
                    // through the field properties dialog.
                    _pdfCoreDocument.AddChoiceField(pageNumber, rect, name,
                        new[] { "Option 1", "Option 2" });
                    break;
                default:
                    return;
            }

            FileState.FormFieldEditsCount++;
            this.RaisePropertyChanged(nameof(CurrentPageFormFields));
            _logger.LogInformation("Added {Type} field '{Name}' to page {Page}",
                FormAuthoringFieldType, name, pageNumber);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to add form field at page {Page}", pageNumber);
        }
    }

    /// <summary>
    /// Run the auto-detector on the current document and apply suggestions.
    /// </summary>
    public int AutoDetectAndApplyFormFields()
    {
        if (_pdfCoreDocument == null) return 0;
        var suggestions = PdfFormAutoDetector.Scan(_pdfCoreDocument);
        if (suggestions.Count == 0) return 0;

        var count = PdfFormAutoDetector.Apply(_pdfCoreDocument, suggestions);
        if (count > 0)
        {
            FileState.FormFieldEditsCount += count;
            this.RaisePropertyChanged(nameof(CurrentPageFormFields));
            _logger.LogInformation("Auto-detected and added {Count} form field(s)", count);
        }

        return count;
    }

    private static string NextUniqueFieldName(PdfDocument doc, PdfFieldType type)
    {
        var prefix = type switch
        {
            PdfFieldType.Text      => "Text",
            PdfFieldType.Button    => "Checkbox",
            PdfFieldType.Choice    => "Choice",
            PdfFieldType.Signature => "Signature",
            _                      => "Field",
        };
        var existing = doc.GetAcroForm()?.Fields.Select(f => f.FullName).ToHashSet()
            ?? new HashSet<string>();
        for (int i = 1; i < 10_000; i++)
        {
            var name = $"{prefix}{i}";
            if (!existing.Contains(name)) return name;
        }

        return $"{prefix}{Guid.NewGuid():N}".Substring(0, 16);
    }
}
