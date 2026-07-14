using Microsoft.Extensions.Logging;
using Pdfe.Avalonia.Controls;
using Pdfe.Core.Document;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;

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
            if (IsTypewriterMode) return InteractionMode.Typewriter;
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
                if (_isTypewriterMode) IsTypewriterMode = false;
            }
            else
            {
                RestoreViewModeFromPreference();
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
        SyncFormFieldValueToServiceDocument(fieldName, newValue);
        FileState.FormFieldEditsCount++;
        NotifyFormDirtyStateChanged();
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
            AddFormFieldToDocument(_pdfCoreDocument, FormAuthoringFieldType, pageNumber, rect, name);
            if (_documentService.GetCurrentDocument() is { } serviceDocument)
                AddFormFieldToDocument(serviceDocument, FormAuthoringFieldType, pageNumber, rect, name);

            FileState.FormFieldEditsCount++;
            this.RaisePropertyChanged(nameof(CurrentPageFormFields));
            NotifyFormDirtyStateChanged();
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
        if (count > 0 && _documentService.GetCurrentDocument() is { } serviceDocument)
            PdfFormAutoDetector.Apply(serviceDocument, suggestions);
        if (count > 0)
        {
            FileState.FormFieldEditsCount += count;
            this.RaisePropertyChanged(nameof(CurrentPageFormFields));
            NotifyFormDirtyStateChanged();
            _logger.LogInformation("Auto-detected and added {Count} form field(s)", count);
        }

        return count;
    }

    private static void AddFormFieldToDocument(
        PdfDocument document,
        PdfFieldType type,
        int pageNumber,
        PdfRectangle rect,
        string name)
    {
        switch (type)
        {
            case PdfFieldType.Text:
                document.AddTextField(pageNumber, rect, name);
                break;
            case PdfFieldType.Button:
                document.AddCheckBox(pageNumber, rect, name);
                break;
            case PdfFieldType.Signature:
                document.AddSignatureField(pageNumber, rect, name);
                break;
            case PdfFieldType.Choice:
                // Choice fields need /Opt; default to two placeholder options
                // so the field is addressable from the GUI.
                document.AddChoiceField(pageNumber, rect, name, new[] { "Option 1", "Option 2" });
                break;
            default:
                throw new NotSupportedException($"Unsupported form field type: {type}");
        }
    }

    private void NotifyFormDirtyStateChanged()
    {
        this.RaisePropertyChanged(nameof(SaveButtonText));
        this.RaisePropertyChanged(nameof(StatusBarText));
    }

    private void SyncFormFieldValueToServiceDocument(string fieldName, string? value)
    {
        var serviceForm = _documentService.GetCurrentDocument()?.GetAcroForm();
        var serviceField = serviceForm?.FindField(fieldName);
        if (serviceField == null) return;

        try
        {
            if (!string.Equals(serviceField.Value, value, StringComparison.Ordinal))
                serviceField.SetValue(value);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            _logger.LogWarning(ex, "Failed to synchronize form field '{Field}' to save document", fieldName);
        }
    }

    private void SyncAllFormFieldValuesToServiceDocument()
    {
        var sourceForm = _pdfCoreDocument?.GetAcroForm();
        var serviceForm = _documentService.GetCurrentDocument()?.GetAcroForm();
        if (sourceForm == null || serviceForm == null) return;

        foreach (var sourceField in sourceForm.Fields)
            SyncFormFieldValueToServiceDocument(sourceField.FullName, sourceField.Value);
    }

    private async Task SaveFlattenedFormCopyAsync()
    {
        if (!_documentService.IsDocumentLoaded)
            return;

        if (_documentService.GetCurrentDocument()?.GetAcroForm() == null)
        {
            await _dialogService.ShowMessageAsync("No Form Fields", "This PDF does not contain interactive form fields to flatten.");
            return;
        }

        var storageProvider = GetStorageProvider();
        if (storageProvider == null)
            return;

        var file = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Flattened Form Copy",
            DefaultExtension = "pdf",
            SuggestedFileName = SuggestFlattenedFormFilename(_currentFilePath),
            FileTypeChoices = new[]
            {
                new FilePickerFileType("PDF Files")
                {
                    Patterns = new[] { "*.pdf" }
                }
            }
        });

        if (file?.Path.LocalPath is not { Length: > 0 } filePath)
            return;

        await SaveFlattenedFormCopyAsAsync(filePath);
    }

    public async Task SaveFlattenedFormCopyAsAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path cannot be empty", nameof(filePath));

        var document = _documentService.GetCurrentDocument();
        if (document == null)
            return;

        if (!await ConfirmEncryptionLossIfNeededAsync(document.IsEncrypted))
        {
            _logger.LogInformation("User declined to save a copy that would drop source encryption");
            return;
        }

        SyncAllFormFieldValuesToServiceDocument();
        using var flattenedCopy = PdfDocument.Open(document.SaveToBytes());
        ApplyPendingTypewriterText(flattenedCopy);
        flattenedCopy.FlattenAcroForm();
        flattenedCopy.Save(filePath);
        ClearPendingTypewriterText();
        FileState.MarkSaved();

        await LoadDocumentAsync(filePath);
        _toastService.ShowSuccess("Flattened form copy saved");
    }

    private static string SuggestFlattenedFormFilename(string currentFilePath)
    {
        if (string.IsNullOrWhiteSpace(currentFilePath))
            return "document_flattened.pdf";

        var directory = Path.GetDirectoryName(currentFilePath);
        var name = Path.GetFileNameWithoutExtension(currentFilePath);
        var extension = Path.GetExtension(currentFilePath);
        var fileName = $"{name}_flattened{(string.IsNullOrEmpty(extension) ? ".pdf" : extension)}";
        return string.IsNullOrWhiteSpace(directory) ? fileName : Path.Combine(directory, fileName);
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
