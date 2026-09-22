using Microsoft.Extensions.Logging;
using Excise.Avalonia.Controls;
using Excise.Core.Document;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;

namespace Excise.App.ViewModels;

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
            if (IsPathAnnotationMode) return InteractionMode.PathAnnotation;
            if (IsStickyNoteToolActive) return InteractionMode.StickyNote;
            if (IsShapeAnnotationMode) return InteractionMode.ShapeAnnotation;
            return InteractionMode.None;
        }
    }

    private bool _isStickyNoteToolActive;

    /// <summary>
    /// When true, clicking the page places a sticky note at that exact point
    /// and opens its popup for typing (#1788) — the click-to-place path
    /// alongside <see cref="AddStickyNoteAnnotationCommand"/>'s existing
    /// modal-prompt flow, which this does not replace. Mutually exclusive
    /// with the other editing modes, same as <see cref="IsPathAnnotationMode"/>.
    /// </summary>
    public bool IsStickyNoteToolActive
    {
        get => _isStickyNoteToolActive;
        set
        {
            if (_isStickyNoteToolActive == value)
                return;

            // #642: placing a sticky note is annotating — /P bit 6.
            if (value && !EnsureDocumentPermission(p => p.CanAnnotate,
                "Adding a sticky note", "adding or modifying annotations (/P bit 6)"))
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _isStickyNoteToolActive, value);
            if (value)
            {
                ViewMode = PdfViewMode.SinglePage;
                if (_isRedactionMode) IsRedactionMode = false;
                if (_isTextSelectionMode) IsTextSelectionMode = false;
                if (_isTypewriterMode) IsTypewriterMode = false;
                if (_isFormAuthoringMode) IsFormAuthoringMode = false;
                if (_isPathAnnotationMode) IsPathAnnotationMode = false;
                if (_isShapeAnnotationMode) IsShapeAnnotationMode = false;
                if (_isMarkupAnnotationMode) IsMarkupAnnotationMode = false;
            }
            else
            {
                RestoreViewModeFromPreference();
                if (!IsEditingModeActive) IsTextSelectionMode = true;
            }

            this.RaisePropertyChanged(nameof(InteractionMode));
            this.RaisePropertyChanged(nameof(CurrentModeText));
        }
    }

    private bool _isPathAnnotationMode;

    /// <summary>
    /// When true, dragging on the page draws a path that becomes an annotation
    /// — which annotation is <see cref="PathAnnotationKind"/> (#934 D, E).
    /// Mutually exclusive with the other editing modes, exactly as they are
    /// with each other — two drawing modes live at once would make the drag
    /// gesture ambiguous.
    /// </summary>
    public bool IsPathAnnotationMode
    {
        get => _isPathAnnotationMode;
        set
        {
            if (_isPathAnnotationMode == value)
                return;

            this.RaiseAndSetIfChanged(ref _isPathAnnotationMode, value);
            if (value)
            {
                ViewMode = PdfViewMode.SinglePage;
                if (_isRedactionMode) IsRedactionMode = false;
                if (_isTextSelectionMode) IsTextSelectionMode = false;
                if (_isTypewriterMode) IsTypewriterMode = false;
                if (_isFormAuthoringMode) IsFormAuthoringMode = false;
                if (_isStickyNoteToolActive) IsStickyNoteToolActive = false;
                if (_isShapeAnnotationMode) IsShapeAnnotationMode = false;
                if (_isMarkupAnnotationMode) IsMarkupAnnotationMode = false;
            }
            else
            {
                RestoreViewModeFromPreference();
                if (!IsEditingModeActive) IsTextSelectionMode = true;
            }

            this.RaisePropertyChanged(nameof(InteractionMode));
            this.RaisePropertyChanged(nameof(CurrentModeText));
        }
    }

    private PathAnnotationKind _pathAnnotationKind = PathAnnotationKind.Ink;

    /// <summary>
    /// Which annotation a drawn path becomes. Selecting the kind is what the
    /// menu items do; the capture, the coordinate conversion and the event are
    /// shared between them (#934).
    /// </summary>
    public PathAnnotationKind PathAnnotationKind
    {
        get => _pathAnnotationKind;
        set
        {
            this.RaiseAndSetIfChanged(ref _pathAnnotationKind, value);
            this.RaisePropertyChanged(nameof(PathCaptureKind));
            this.RaisePropertyChanged(nameof(CurrentModeText));
        }
    }

    /// <summary>
    /// The gesture the viewer should capture for the selected kind. Ink wants
    /// every sample; a Line wants only where the drag began and ended.
    /// </summary>
    public PathCaptureKind PathCaptureKind => PathAnnotationKind switch
    {
        PathAnnotationKind.Ink => PathCaptureKind.Freehand,
        PathAnnotationKind.Polygon or PathAnnotationKind.PolyLine => PathCaptureKind.Vertices,
        _ => PathCaptureKind.Segment,
    };

    /// <summary>
    /// Enter path-annotation mode with <paramref name="kind"/> selected, or
    /// leave it if that kind is already active.
    ///
    /// Switching KIND while the mode is on selects the new kind rather than
    /// switching the mode off — otherwise picking Arrow while Line is active
    /// would silently drop the user out of drawing entirely.
    /// </summary>
    private void TogglePathMode(PathAnnotationKind kind)
    {
        // #642: drawing an annotation is annotating — /P bit 6. Gate on
        // entering; leaving is free.
        if ((!IsPathAnnotationMode || PathAnnotationKind != kind) &&
            !EnsureDocumentPermission(p => p.CanAnnotate,
                "Drawing annotations", "adding or modifying annotations (/P bit 6)"))
        {
            return;
        }

        if (IsPathAnnotationMode && PathAnnotationKind == kind)
        {
            IsPathAnnotationMode = false;
            return;
        }

        PathAnnotationKind = kind;
        IsPathAnnotationMode = true;
    }

    private bool _isShapeAnnotationMode;

    /// <summary>
    /// When true, dragging a rectangle on the page becomes an annotation —
    /// which one is <see cref="ShapeAnnotationKind"/> (#1792 follow-up: gives
    /// Square/Circle/FreeText/Stamp/ImageStamp the same direct one-drag-
    /// places-it path <see cref="IsPathAnnotationMode"/> already has, instead
    /// of borrowing <see cref="IsRedactionMode"/>'s drag gesture). Mutually
    /// exclusive with the other editing modes, same as the others.
    /// </summary>
    public bool IsShapeAnnotationMode
    {
        get => _isShapeAnnotationMode;
        set
        {
            if (_isShapeAnnotationMode == value)
                return;

            this.RaiseAndSetIfChanged(ref _isShapeAnnotationMode, value);
            if (value)
            {
                ViewMode = PdfViewMode.SinglePage;
                if (_isRedactionMode) IsRedactionMode = false;
                if (_isTextSelectionMode) IsTextSelectionMode = false;
                if (_isTypewriterMode) IsTypewriterMode = false;
                if (_isFormAuthoringMode) IsFormAuthoringMode = false;
                if (_isPathAnnotationMode) IsPathAnnotationMode = false;
                if (_isStickyNoteToolActive) IsStickyNoteToolActive = false;
                if (_isMarkupAnnotationMode) IsMarkupAnnotationMode = false;
            }
            else
            {
                RestoreViewModeFromPreference();
                if (!IsEditingModeActive) IsTextSelectionMode = true;
            }

            this.RaisePropertyChanged(nameof(InteractionMode));
            this.RaisePropertyChanged(nameof(CurrentModeText));
        }
    }

    private ShapeAnnotationKind _shapeAnnotationKind = ShapeAnnotationKind.Square;

    /// <summary>
    /// Which annotation a dragged rect becomes. Selecting the kind is what
    /// the menu/toolbar/palette items do; the capture, the coordinate
    /// conversion and the event are shared between them.
    /// </summary>
    public ShapeAnnotationKind ShapeAnnotationKind
    {
        get => _shapeAnnotationKind;
        set
        {
            this.RaiseAndSetIfChanged(ref _shapeAnnotationKind, value);
            this.RaisePropertyChanged(nameof(CurrentModeText));
        }
    }

    private string? _stagedStampName;

    /// <summary>
    /// The stamp name to use when the next drawn rect becomes a Stamp
    /// (<see cref="ShapeAnnotationKind.Stamp"/>) — set by
    /// <see cref="ToggleStampMode"/>, one of the 15 preset names
    /// <c>AddStampAnnotationFromDragCommand</c> already accepts.
    /// </summary>
    public string? StagedStampName
    {
        get => _stagedStampName;
        private set => this.RaiseAndSetIfChanged(ref _stagedStampName, value);
    }

    /// <summary>
    /// Enter shape-annotation mode with <paramref name="kind"/> selected, or
    /// leave it if that (kind, stamp name) pair is already active — same
    /// re-selection-not-exit rule as <see cref="TogglePathMode"/>, so picking
    /// Circle while Square is active selects Circle rather than leaving the
    /// mode. <paramref name="stampName"/> is only meaningful for
    /// <see cref="ShapeAnnotationKind.Stamp"/>.
    /// </summary>
    private void ToggleShapeMode(ShapeAnnotationKind kind, string? stampName = null)
    {
        // #642: drawing an annotation is annotating — /P bit 6. Gate on
        // entering; leaving is free.
        var alreadyActive = IsShapeAnnotationMode && ShapeAnnotationKind == kind &&
            (kind != ShapeAnnotationKind.Stamp || StagedStampName == stampName);

        if (!alreadyActive && !EnsureDocumentPermission(p => p.CanAnnotate,
                "Drawing annotations", "adding or modifying annotations (/P bit 6)"))
        {
            return;
        }

        if (alreadyActive)
        {
            IsShapeAnnotationMode = false;
            return;
        }

        ShapeAnnotationKind = kind;
        StagedStampName = kind == ShapeAnnotationKind.Stamp ? stampName : null;
        IsShapeAnnotationMode = true;
    }

    private bool _isMarkupAnnotationMode;

    /// <summary>
    /// When true, finishing an ordinary text selection (the ONLY gesture
    /// <see cref="InteractionMode.TextSelection"/> ever had) also applies
    /// <see cref="MarkupAnnotationKind"/> to it immediately — arm the tool
    /// first, drag over the text, release, done — instead of selecting text
    /// then separately clicking Add*FromSelection. That older direct-apply
    /// path (<see cref="AddHighlightAnnotationFromSelectionCommand"/> etc.)
    /// is unchanged and still available.
    ///
    /// Deliberately does NOT introduce its own InteractionMode: the gesture
    /// IS plain text selection, unchanged, so arming this just sets
    /// <see cref="IsTextSelectionMode"/> and lets <see cref="MainWindow.OnTextSelected"/>
    /// notice a kind is armed once the selection finishes.
    /// </summary>
    public bool IsMarkupAnnotationMode
    {
        get => _isMarkupAnnotationMode;
        set
        {
            if (_isMarkupAnnotationMode == value)
                return;

            if (value)
            {
                // Arms the real gesture FIRST, while this property's own
                // backing field is still false — see IsTextSelectionMode's
                // setter for why that ordering matters.
                IsTextSelectionMode = true;
                if (_isPathAnnotationMode) IsPathAnnotationMode = false;
            }

            this.RaiseAndSetIfChanged(ref _isMarkupAnnotationMode, value);
            this.RaisePropertyChanged(nameof(CurrentModeText));
        }
    }

    private MarkupAnnotationKind _markupAnnotationKind = MarkupAnnotationKind.Highlight;

    /// <summary>
    /// Which markup a finished selection becomes. Selecting the kind is what
    /// the menu/toolbar/palette items do; the selection gesture itself is
    /// shared between them.
    /// </summary>
    public MarkupAnnotationKind MarkupAnnotationKind
    {
        get => _markupAnnotationKind;
        set
        {
            this.RaiseAndSetIfChanged(ref _markupAnnotationKind, value);
            this.RaisePropertyChanged(nameof(CurrentModeText));
        }
    }

    /// <summary>
    /// Enter markup-annotation mode with <paramref name="kind"/> selected, or
    /// leave it if that kind is already active — same re-selection-not-exit
    /// rule as <see cref="ToggleShapeMode"/>.
    /// </summary>
    private void ToggleMarkupMode(MarkupAnnotationKind kind)
    {
        // #642: applying markup is annotating — /P bit 6. Gate on entering;
        // leaving is free.
        var alreadyActive = IsMarkupAnnotationMode && MarkupAnnotationKind == kind;

        if (!alreadyActive && !EnsureDocumentPermission(p => p.CanAnnotate,
                "Marking up text", "adding or modifying annotations (/P bit 6)"))
        {
            return;
        }

        if (alreadyActive)
        {
            IsMarkupAnnotationMode = false;
            return;
        }

        MarkupAnnotationKind = kind;
        IsMarkupAnnotationMode = true;
    }

    /// <summary>
    /// Called by MainWindow's OnTextSelected, right after it updates
    /// SelectedText/CurrentTextSelectionPageArea from a finished selection —
    /// dispatches to whichever Add*FromSelection method
    /// <see cref="MarkupAnnotationKind"/> currently selects, if the mode is
    /// armed and the selection is non-empty. Mode stays armed afterward
    /// (matches ToggleShapeMode/TogglePathMode: mark several in a row
    /// without re-arming).
    /// </summary>
    public async Task HandleTextSelectionFinishedForMarkupAsync()
    {
        if (!IsMarkupAnnotationMode || string.IsNullOrEmpty(SelectedText))
            return;

        switch (MarkupAnnotationKind)
        {
            case MarkupAnnotationKind.Highlight:
                await AddHighlightAnnotationFromSelectionAsync();
                break;
            case MarkupAnnotationKind.Underline:
                await AddUnderlineAnnotationFromSelectionAsync();
                break;
            case MarkupAnnotationKind.StrikeOut:
                await AddStrikeOutAnnotationFromSelectionAsync();
                break;
            case MarkupAnnotationKind.Squiggly:
                await AddSquigglyAnnotationFromSelectionAsync();
                break;
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
            if (_isFormAuthoringMode == value)
                return;

            this.RaiseAndSetIfChanged(ref _isFormAuthoringMode, value);
            if (value)
            {
                ViewMode = PdfViewMode.SinglePage;
                if (_isRedactionMode) IsRedactionMode = false;
                if (_isTextSelectionMode) IsTextSelectionMode = false;
                if (_isTypewriterMode) IsTypewriterMode = false;
                if (_isStickyNoteToolActive) IsStickyNoteToolActive = false;
                if (_isShapeAnnotationMode) IsShapeAnnotationMode = false;
                if (_isMarkupAnnotationMode) IsMarkupAnnotationMode = false;
            }
            else
            {
                RestoreViewModeFromPreference();
                // #831: restore selection only when returning to reading, not
                // when switching into another editing mode (see IsRedactionMode).
                if (!IsEditingModeActive) IsTextSelectionMode = true;
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
            if (_pdfCoreDocument == null || CurrentPageIndex < 0 || CurrentPageIndex >= TotalPages)
                return Array.Empty<PdfField>();
            try
            {
                return _pdfCoreDocument.GetPage(CurrentPageIndex + 1).GetFormFields();
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

        // #642: /P bit 6 or 9 gates filling interactive form fields.
        if (!EnsureDocumentPermission(p => p.CanFillForms,
            "Filling form fields", "filling in form fields (/P bit 6 or 9)"))
        {
            return;
        }

        SyncFormFieldValueToServiceDocument(fieldName, newValue);
        FileState.FormFieldEditsCount++;
        NotifyFormDirtyStateChanged();
        _logger.LogInformation("Form field '{Field}' set to '{Value}'",
            Excise.Core.Text.UnicodeTextSafety.EscapeForDisplay(fieldName), newValue);
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

        // #642: creating new form fields modifies the document — /P bit 4.
        if (!EnsureDocumentPermission(p => p.CanModify,
            "Adding a form field", "modifying the document (/P bit 4)"))
        {
            return;
        }

        try
        {
            var name = NextUniqueFieldName(_pdfCoreDocument, FormAuthoringFieldType);
            AddFormFieldToDocument(_pdfCoreDocument, FormAuthoringFieldType, pageNumber, rect, name);
            // #917: normally a no-op — the save document IS this document, and
            // applying the field twice created it twice (caught by three form
            // tests the moment the two were unified). The guard stays because
            // the transition is not finished: some paths still hand the viewer
            // a separate instance.
            if (_documentService.GetCurrentDocument() is { } serviceDocument
                && !ReferenceEquals(serviceDocument, _pdfCoreDocument))
            {
                AddFormFieldToDocument(serviceDocument, FormAuthoringFieldType, pageNumber, rect, name);
            }

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

        // #642: applying detected fields modifies the document — /P bit 4.
        if (!EnsureDocumentPermission(p => p.CanModify,
            "Adding auto-detected form fields", "modifying the document (/P bit 4)"))
        {
            return 0;
        }

        var suggestions = PdfFormAutoDetector.Scan(_pdfCoreDocument);
        if (suggestions.Count == 0) return 0;

        var count = PdfFormAutoDetector.Apply(_pdfCoreDocument, suggestions);
        // #917: same guard, same reason — without it every auto-detected field
        // is applied twice to the one document.
        if (count > 0 && _documentService.GetCurrentDocument() is { } serviceDocument
            && !ReferenceEquals(serviceDocument, _pdfCoreDocument))
        {
            PdfFormAutoDetector.Apply(serviceDocument, suggestions);
        }
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

    /// <summary>
    /// A field refused a typed value (#1671) — the text has characters the
    /// field's font cannot represent. The viewer has already put the stored
    /// value back, so what is on screen is what will be saved; this is the
    /// only place the user is told the typed value was NOT kept.
    /// </summary>
    public void OnFormFieldEditRejected(string fieldName, string message)
    {
        var shownName = Excise.Core.Text.UnicodeTextSafety.EscapeForDisplay(fieldName);
        _logger.LogWarning("Form field '{Field}' refused an edit: {Message}", shownName, message);
        _toastService.ShowError(
            $"Value for '{shownName}' was NOT saved",
            message);
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
            _logger.LogWarning(ex, "Failed to synchronize form field '{Field}' to save document",
                Excise.Core.Text.UnicodeTextSafety.EscapeForDisplay(fieldName));

            // #1671: an ArgumentException here is the field refusing text it
            // cannot represent — the saved file will hold the OLD value, and a
            // log line is not telling the user that.
            if (ex is ArgumentException)
                OnFormFieldEditRejected(fieldName, ex.Message);
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

        // #1623: this message IS reachable. The button and menu item are
        // IsEnabled-bound to IsDocumentLoaded, NOT to the document having form
        // fields, so any PDF without an /AcroForm reaches this guard with the
        // command enabled. Keep the message — it is the only thing that tells
        // the user why nothing happened.
        if (_documentService.GetCurrentDocument()?.GetAcroForm() == null)
        {
            await _dialogService.ShowMessageAsync("No Form Fields", "This PDF does not contain interactive form fields to flatten.");
            return;
        }

        var file = await _filePicker.SaveFileAsync(new global::Excise.App.Services.Host.SaveFileRequest
        {
            Title = "Save Flattened Form Copy",
            DefaultExtension = "pdf",
            SuggestedFileName = SuggestFlattenedFormFilename(_currentFilePath),
            Filters = new[] { global::Excise.App.Services.Host.FilePickerFilters.Pdf },
        });

        if (file is not { Length: > 0 } filePath)
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

        SyncAllFormFieldValuesToServiceDocument();
        using var flattenedCopy = PdfDocument.Open(document.SaveToBytes());
        ApplyPendingTypewriterText(flattenedCopy);
        flattenedCopy.FlattenAcroForm();
        // #643: an encrypted source's flattened copy stays encrypted with the
        // same parameters and password. (The in-memory round-trip above is
        // plaintext by design; encryption is re-applied on the final write.)
        flattenedCopy.Save(filePath, _documentService.GetReEncryptionOptions());
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
