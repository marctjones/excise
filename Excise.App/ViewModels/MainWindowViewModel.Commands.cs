using Microsoft.Extensions.Logging;
using ReactiveUI;
using System;
using System.Reactive;

namespace Excise.App.ViewModels;

public partial class MainWindowViewModel
{
    // Mode indicator for status bar.
    public string CurrentModeText
    {
        get
        {
            if (IsRedactionMode) return "Redaction Mode";
            if (IsTypewriterMode) return "✎ Typewriter Mode";
            if (IsStickyNoteToolActive) return "\U0001F4CC Sticky Note Mode — click to place a note";
            if (IsPathAnnotationMode)
            {
                return PathAnnotationKind switch
                {
                    PathAnnotationKind.Line => "╱ Line Mode",
                    PathAnnotationKind.Arrow => "➤ Arrow Mode",
                    PathAnnotationKind.Polygon => "⬠ Polygon Mode — click to add points, double-click or Enter to finish",
                    PathAnnotationKind.PolyLine => "⌇ PolyLine Mode — click to add points, double-click or Enter to finish",
                    _ => "✒ Draw Mode",
                };
            }
            // #831: text selection is the resting default, not a special mode, so
            // it is no longer announced here — the view mode is more informative.
            if (IsContinuousView) return "Continuous Scroll";
            return "View Mode";
        }
    }

    public ReactiveCommand<Unit, Unit> ToggleFreehandModeCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleLineModeCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleArrowModeCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> TogglePolygonModeCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> TogglePolyLineModeCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> OpenFileCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> SaveFileCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> RemoveCurrentPageCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> AddPagesCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> InsertPagesBeforeCurrentCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> InsertPagesAfterCurrentCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ExtractCurrentPageCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ExtractSelectedPagesCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> CombineDocumentsCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> SplitDocumentCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> RemoveSelectedPagesCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> MoveSelectedPagesEarlierCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> MoveSelectedPagesLaterCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ClearSelectedPagesCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> MoveCurrentPageEarlierCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> MoveCurrentPageLaterCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleRedactionModeCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ApplyRedactionCommand { get; private set; } = null!;
    public ReactiveCommand<Guid, Unit> RemovePendingRedactionCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ClearAllRedactionsCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ApplyAllRedactionsCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleTextSelectionModeCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleFormAuthoringModeCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleTypewriterModeCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> DiscardPendingTypewriterEditsCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> GoToNextPendingTypewriterEditCommand { get; private set; } = null!;
    public ReactiveCommand<string, Unit> SetTypewriterColorCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> AddHighlightAnnotationFromSelectionCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> AddUnderlineAnnotationFromSelectionCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> AddStrikeOutAnnotationFromSelectionCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> AddSquigglyAnnotationFromSelectionCommand { get; private set; } = null!;

    /// <summary>
    /// Arms/disarms markup-annotation mode for the kind named in each
    /// property (#1792 follow-up) — select text while armed and the markup
    /// applies the instant the selection finishes, no separate "select then
    /// click Add" step. The direct Add*FromSelectionCommand above (apply to
    /// whatever's already selected) is unchanged and still available. See
    /// <see cref="IsMarkupAnnotationMode"/>.
    /// </summary>
    public ReactiveCommand<Unit, Unit> ToggleHighlightModeCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleUnderlineModeCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleStrikeOutModeCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleSquigglyModeCommand { get; private set; } = null!;

    public ReactiveCommand<Unit, Unit> AddSquareAnnotationFromDragCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> AddCircleAnnotationFromDragCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> AddFreeTextAnnotationFromDragCommand { get; private set; } = null!;
    public ReactiveCommand<string, Unit> AddStampAnnotationFromDragCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> AddImageStampAnnotationFromDragCommand { get; private set; } = null!;

    /// <summary>
    /// Arms/disarms shape-annotation mode (#1792) for the kind named in each
    /// property — a drag on the page directly places the annotation, the
    /// same one-gesture path <see cref="ToggleLineModeCommand"/> and its
    /// siblings already have. See <see cref="IsShapeAnnotationMode"/>.
    /// </summary>
    public ReactiveCommand<Unit, Unit> ToggleSquareModeCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleCircleModeCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleFreeTextModeCommand { get; private set; } = null!;

    /// <summary>As the others, but the parameter selects which of the 15 preset stamps.</summary>
    public ReactiveCommand<string, Unit> ToggleStampModeCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleImageStampModeCommand { get; private set; } = null!;

    public ReactiveCommand<Unit, Unit> AddStickyNoteAnnotationCommand { get; private set; } = null!;

    /// <summary>
    /// Arms/disarms the click-to-place sticky-note tool (#1788), alongside
    /// (not instead of) <see cref="AddStickyNoteAnnotationCommand"/>'s modal
    /// prompt flow. See <see cref="IsStickyNoteToolActive"/>.
    /// </summary>
    public ReactiveCommand<Unit, Unit> ToggleStickyNoteToolCommand { get; private set; } = null!;

    public ReactiveCommand<Unit, Unit> ToggleOutlineCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleThumbnailsCommand { get; private set; } = null!;
    /// <summary>#1563 — View ▸ Show Attachments.</summary>
    public ReactiveCommand<Unit, Unit> ToggleAttachmentsCommand { get; private set; } = null!;

    /// <summary>Show or hide the page's annotations (#1022).</summary>
    public ReactiveCommand<Unit, Unit> ToggleAnnotationsCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleCommentAnnotationsCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleFieldAndLinkAnnotationsCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleAnnotationAuditModeCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleFormFieldHighlightingCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleClipboardSidebarCommand { get; private set; } = null!;
    /// <summary>#1789 — View ▸ Annotation Toolbar (a second, optional toolbar row).</summary>
    public ReactiveCommand<Unit, Unit> ToggleAnnotationToolbarCommand { get; private set; } = null!;
    /// <summary>#1789 — View ▸ Floating Annotation Palette (an optional, movable tool window).</summary>
    public ReactiveCommand<Unit, Unit> ToggleAnnotationPaletteCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleContinuousViewCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleRevealHiddenTextCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleRevealRasterizedHiddenCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> MakeSearchableCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> SecurityCommand { get; private set; } = null!;
    /// <summary>#1414 — view, save, or strip embedded files.</summary>
    public ReactiveCommand<Unit, Unit> AttachmentsCommand { get; private set; } = null!;
    /// <summary>#1563 — Attachments pane: save the selected attachment.</summary>
    public ReactiveCommand<Unit, Unit> SaveSelectedAttachmentCommand { get; private set; } = null!;
    /// <summary>#1563 — Attachments pane: save every attachment into a folder.</summary>
    public ReactiveCommand<Unit, int> SaveAllAttachmentsCommand { get; private set; } = null!;
    /// <summary>#1563 — Attachments pane: strip the embedded files (undoable pending edit).</summary>
    public ReactiveCommand<Unit, Unit> RemoveAllAttachmentsCommand { get; private set; } = null!;
    /// <summary>#1306 — stamp sequential Bates numbers on every page.</summary>
    public ReactiveCommand<Unit, Unit> BatesNumberingCommand { get; private set; } = null!;
    /// <summary>#1550 — write a smaller copy of the document.</summary>
    public ReactiveCommand<Unit, Unit> ReduceFileSizeCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, int> AutoDetectFieldsCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> SaveFlattenedFormCopyCommand { get; private set; } = null!;
    /// <summary>#1308 — apply a digital signature to the saved file.</summary>
    public ReactiveCommand<Unit, Unit> SignDocumentCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> CopyTextCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ZoomInCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ZoomOutCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> NextPageCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> PreviousPageCommand { get; private set; } = null!;
    public ReactiveCommand<int, Unit> GoToPageCommand { get; private set; } = null!;
    public ReactiveCommand<Models.OutlineNode, Unit> JumpToOutlineCommand =>
        _jumpToOutline ??= ReactiveCommand.Create<Models.OutlineNode>(JumpToOutline);
    private ReactiveCommand<Models.OutlineNode, Unit>? _jumpToOutline;

    public ReactiveCommand<Unit, Unit> RotatePageLeftCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> RotatePageRightCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> RotatePage180Command { get; private set; } = null!;

    public ReactiveCommand<Unit, Unit> ZoomActualSizeCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ZoomFitWidthCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ZoomFitPageCommand { get; private set; } = null!;

    public ReactiveCommand<Unit, Unit> SaveAsCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> CloseDocumentCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ExitCommand { get; private set; } = null!;
    public ReactiveCommand<string, Unit> LoadRecentFileCommand { get; private set; } = null!;

    public ReactiveCommand<Unit, Unit> ExportCurrentPageCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ExportPagesCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> PrintCommand { get; private set; } = null!;
    public ReactiveCommand<string, Unit> OpenExternalLinkCommand { get; private set; } = null!;
    public ReactiveCommand<string, Unit> ShowDangerousLinkRefusalCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> VerifySignaturesCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ShowPreferencesCommand { get; private set; } = null!;

    public ReactiveCommand<Unit, Unit> AboutCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ShowShortcutsCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ShowDocumentationCommand { get; private set; } = null!;

    private void InitializeCommands()
    {
        _logger.LogDebug("Setting up ReactiveUI commands");

        InitializeFileAndPageCommands();
        InitializeRedactionCommands();
        InitializeEditingModeCommands();
        InitializeAnnotationCommands();
        InitializeViewAndNavigationCommands();
        InitializeDocumentUtilityCommands();
        InitializeHelpCommands();

        InitializeSearchCommands();
        InitializeScriptingCommands();
        InitializeHistory();
    }

    private void InitializeFileAndPageCommands()
    {
        OpenFileCommand = ReactiveCommand.CreateFromTask(OpenFileAsync);
        SaveFileCommand = ReactiveCommand.CreateFromTask(SaveFileAsync);
        RemoveCurrentPageCommand = ReactiveCommand.CreateFromTask(RemoveCurrentPageAsync);
        AddPagesCommand = ReactiveCommand.CreateFromTask(AddPagesAsync);
        InsertPagesBeforeCurrentCommand = ReactiveCommand.CreateFromTask(InsertPagesBeforeCurrentAsync);
        InsertPagesAfterCurrentCommand = ReactiveCommand.CreateFromTask(InsertPagesAfterCurrentAsync);
        ExtractCurrentPageCommand = ReactiveCommand.CreateFromTask(ExtractCurrentPageAsync);
        ExtractSelectedPagesCommand = ReactiveCommand.CreateFromTask(ExtractSelectedPagesAsync);
        CombineDocumentsCommand = ReactiveCommand.CreateFromTask(CombineDocumentsAsync);
        SplitDocumentCommand = ReactiveCommand.CreateFromTask(SplitDocumentAsync);

        CombineDocumentsCommand.ThrownExceptions.Subscribe(ex =>
            _logger.LogError(ex, "CombineDocumentsCommand threw exception"));
        SplitDocumentCommand.ThrownExceptions.Subscribe(ex =>
            _logger.LogError(ex, "SplitDocumentCommand threw exception"));
        RemoveSelectedPagesCommand = ReactiveCommand.CreateFromTask(RemoveSelectedPagesAsync);
        MoveSelectedPagesEarlierCommand = ReactiveCommand.CreateFromTask(() => MoveSelectedPagesAsync(-1));
        MoveSelectedPagesLaterCommand = ReactiveCommand.CreateFromTask(() => MoveSelectedPagesAsync(1));
        ClearSelectedPagesCommand = ReactiveCommand.Create(ClearSelectedPages);
        MoveCurrentPageEarlierCommand = ReactiveCommand.CreateFromTask(MoveCurrentPageEarlierAsync);
        MoveCurrentPageLaterCommand = ReactiveCommand.CreateFromTask(MoveCurrentPageLaterAsync);
    }

    private void InitializeRedactionCommands()
    {
        ToggleRedactionModeCommand = ReactiveCommand.Create(ToggleRedactionMode);
        ApplyRedactionCommand = ReactiveCommand.CreateFromTask(MarkCurrentRedactionAsync);
        RemovePendingRedactionCommand = ReactiveCommand.Create<Guid>(RemovePendingRedaction);
        ClearAllRedactionsCommand = ReactiveCommand.Create(ClearAllRedactions);
        ApplyAllRedactionsCommand = ReactiveCommand.CreateFromTask(ApplyAllRedactionsAsync);

        ApplyRedactionCommand.ThrownExceptions.Subscribe(ex =>
            _logger.LogError(ex, "ApplyRedactionCommand threw exception"));
    }

    private void InitializeEditingModeCommands()
    {
        ToggleTextSelectionModeCommand = ReactiveCommand.Create(ToggleTextSelectionMode);
        ToggleFormAuthoringModeCommand = ReactiveCommand.Create(() =>
        {
            // #642: authoring fields modifies the document — /P bit 4.
            // Block on entering the mode; leaving it is free.
            if (!IsFormAuthoringMode && !EnsureDocumentPermission(p => p.CanModify,
                "Form authoring", "modifying the document (/P bit 4)"))
            {
                return;
            }

            IsFormAuthoringMode = !IsFormAuthoringMode;
        });
        ToggleTypewriterModeCommand = ReactiveCommand.Create(ToggleTypewriterMode);
        // #934 D: drawing ink is annotating — /P bit 6, same gate the other
        // annotation commands use. Blocked on entering; leaving is free.
        ToggleFreehandModeCommand = ReactiveCommand.Create(() => TogglePathMode(PathAnnotationKind.Ink));
        ToggleLineModeCommand = ReactiveCommand.Create(() => TogglePathMode(PathAnnotationKind.Line));
        ToggleArrowModeCommand = ReactiveCommand.Create(() => TogglePathMode(PathAnnotationKind.Arrow));
        TogglePolygonModeCommand = ReactiveCommand.Create(() => TogglePathMode(PathAnnotationKind.Polygon));
        TogglePolyLineModeCommand = ReactiveCommand.Create(() => TogglePathMode(PathAnnotationKind.PolyLine));
        DiscardPendingTypewriterEditsCommand = ReactiveCommand.Create(DiscardPendingTypewriterEdits);
        GoToNextPendingTypewriterEditCommand = ReactiveCommand.Create(GoToNextPendingTypewriterEdit);
        SetTypewriterColorCommand = ReactiveCommand.Create<string>(hex => SetTypewriterColor(hex));
    }

    private void InitializeAnnotationCommands()
    {
        AddHighlightAnnotationFromSelectionCommand = ReactiveCommand.CreateFromTask(AddHighlightAnnotationFromSelectionAsync);
        // #912: three of the thirteen subtypes Core could author and the app
        // could not reach. Same selection gesture as Highlight above.
        AddUnderlineAnnotationFromSelectionCommand = ReactiveCommand.CreateFromTask(AddUnderlineAnnotationFromSelectionAsync);
        AddStrikeOutAnnotationFromSelectionCommand = ReactiveCommand.CreateFromTask(AddStrikeOutAnnotationFromSelectionAsync);
        AddSquigglyAnnotationFromSelectionCommand = ReactiveCommand.CreateFromTask(AddSquigglyAnnotationFromSelectionAsync);
        ToggleHighlightModeCommand = ReactiveCommand.Create(() => ToggleMarkupMode(MarkupAnnotationKind.Highlight));
        ToggleUnderlineModeCommand = ReactiveCommand.Create(() => ToggleMarkupMode(MarkupAnnotationKind.Underline));
        ToggleStrikeOutModeCommand = ReactiveCommand.Create(() => ToggleMarkupMode(MarkupAnnotationKind.StrikeOut));
        ToggleSquigglyModeCommand = ReactiveCommand.Create(() => ToggleMarkupMode(MarkupAnnotationKind.Squiggly));
        AddSquareAnnotationFromDragCommand = ReactiveCommand.CreateFromTask(AddSquareAnnotationFromDragAsync);
        AddCircleAnnotationFromDragCommand = ReactiveCommand.CreateFromTask(AddCircleAnnotationFromDragAsync);
        AddFreeTextAnnotationFromDragCommand = ReactiveCommand.CreateFromTask(() => AddFreeTextAnnotationFromDragAsync(null));
        AddStampAnnotationFromDragCommand = ReactiveCommand.CreateFromTask<string>(AddStampAnnotationFromDragAsync);
        AddImageStampAnnotationFromDragCommand = ReactiveCommand.CreateFromTask(() => AddImageStampAnnotationFromDragAsync(null));
        ToggleSquareModeCommand = ReactiveCommand.Create(() => ToggleShapeMode(ShapeAnnotationKind.Square));
        ToggleCircleModeCommand = ReactiveCommand.Create(() => ToggleShapeMode(ShapeAnnotationKind.Circle));
        ToggleFreeTextModeCommand = ReactiveCommand.Create(() => ToggleShapeMode(ShapeAnnotationKind.FreeText));
        ToggleStampModeCommand = ReactiveCommand.Create<string>(
            stampName => ToggleShapeMode(ShapeAnnotationKind.Stamp, stampName));
        ToggleImageStampModeCommand = ReactiveCommand.Create(() => ToggleShapeMode(ShapeAnnotationKind.ImageStamp));
        AddStickyNoteAnnotationCommand = ReactiveCommand.CreateFromTask(() => AddStickyNoteAnnotationAsync());
        ToggleStickyNoteToolCommand = ReactiveCommand.Create(
            () => { IsStickyNoteToolActive = !IsStickyNoteToolActive; });
    }

    private void InitializeViewAndNavigationCommands()
    {
        ToggleOutlineCommand = ReactiveCommand.Create(ToggleOutlineSidebar);
        ToggleThumbnailsCommand = ReactiveCommand.Create(ToggleThumbnailsSidebar);
        ToggleAttachmentsCommand = ReactiveCommand.Create(ToggleAttachmentsSidebar);
        ToggleAnnotationsCommand = ReactiveCommand.Create(ToggleAnnotationsVisible);
        ToggleCommentAnnotationsCommand = ReactiveCommand.Create(ToggleCommentAnnotationsVisible);
        ToggleFieldAndLinkAnnotationsCommand = ReactiveCommand.Create(ToggleFieldAndLinkAnnotationsVisible);
        ToggleAnnotationAuditModeCommand = ReactiveCommand.Create(ToggleAnnotationAuditMode);
        ToggleFormFieldHighlightingCommand = ReactiveCommand.Create(ToggleFormFieldHighlighting);
        ToggleClipboardSidebarCommand = ReactiveCommand.Create(ToggleClipboardSidebar);
        ToggleAnnotationToolbarCommand = ReactiveCommand.Create(ToggleAnnotationToolbar);
        ToggleAnnotationPaletteCommand = ReactiveCommand.Create(ToggleAnnotationPalette);
        ToggleContinuousViewCommand = ReactiveCommand.Create(ToggleContinuousView);
        ToggleRevealHiddenTextCommand = ReactiveCommand.Create(() => { RevealHiddenText = !RevealHiddenText; });
        ToggleRevealRasterizedHiddenCommand = ReactiveCommand.Create(() => { RevealRasterizedHidden = !RevealRasterizedHidden; });
        CopyTextCommand = ReactiveCommand.CreateFromTask(CopyTextAsync);
        ZoomInCommand = ReactiveCommand.Create(ZoomIn);
        ZoomOutCommand = ReactiveCommand.Create(ZoomOut);
        NextPageCommand = ReactiveCommand.CreateFromTask(NextPageAsync);
        PreviousPageCommand = ReactiveCommand.CreateFromTask(PreviousPageAsync);
        GoToPageCommand = ReactiveCommand.CreateFromTask<int>(GoToPageAsync);

        RotatePageLeftCommand = ReactiveCommand.CreateFromTask(RotatePageLeftAsync);
        RotatePageRightCommand = ReactiveCommand.CreateFromTask(RotatePageRightAsync);
        RotatePage180Command = ReactiveCommand.CreateFromTask(RotatePage180Async);

        ZoomActualSizeCommand = ReactiveCommand.Create(ZoomActualSize);
        ZoomFitWidthCommand = ReactiveCommand.Create(ZoomFitWidth);
        ZoomFitPageCommand = ReactiveCommand.Create(ZoomFitPage);
    }

    private void InitializeDocumentUtilityCommands()
    {
        MakeSearchableCommand = ReactiveCommand.CreateFromTask(MakeSearchableAsync);
        MakeSearchableCommand.ThrownExceptions.Subscribe(ex =>
            _logger.LogError(ex, "MakeSearchableCommand threw exception"));
        SecurityCommand = ReactiveCommand.CreateFromTask(ShowSecurityDialogAsync);
        AttachmentsCommand = ReactiveCommand.CreateFromTask(ShowAttachmentsPaneAsync);
        // Always invocable, like Undo/Redo: each no-ops when there is nothing
        // to act on, and the pane binds IsEnabled to the same state. A
        // WhenAnyValue gate would force ReactiveUI's global initialization,
        // which this VM deliberately does not depend on (see History.cs).
        SaveSelectedAttachmentCommand = ReactiveCommand.CreateFromTask(SaveSelectedAttachmentAsync);
        SaveAllAttachmentsCommand = ReactiveCommand.CreateFromTask(SaveAllAttachmentsAsync);
        RemoveAllAttachmentsCommand = ReactiveCommand.Create(StripAllAttachments);
        SaveSelectedAttachmentCommand.ThrownExceptions.Subscribe(ex =>
        {
            _logger.LogError(ex, "SaveSelectedAttachmentCommand threw exception");
            _toastService.ShowError("Could not save attachment", ex.Message);
        });
        SaveAllAttachmentsCommand.ThrownExceptions.Subscribe(ex =>
        {
            _logger.LogError(ex, "SaveAllAttachmentsCommand threw exception");
            _toastService.ShowError("Could not save attachments", ex.Message);
        });
        RemoveAllAttachmentsCommand.ThrownExceptions.Subscribe(ex =>
        {
            _logger.LogError(ex, "RemoveAllAttachmentsCommand threw exception");
            _toastService.ShowError("Could not remove attachments", ex.Message);
        });
        BatesNumberingCommand = ReactiveCommand.CreateFromTask(ApplyBatesNumberingAsync);
        ReduceFileSizeCommand = ReactiveCommand.CreateFromTask(ReduceFileSizeAsync);
        ReduceFileSizeCommand.ThrownExceptions.Subscribe(ex =>
            _logger.LogError(ex, "ReduceFileSizeCommand threw exception"));
        SecurityCommand.ThrownExceptions.Subscribe(ex =>
            _logger.LogError(ex, "SecurityCommand threw exception"));
        AutoDetectFieldsCommand = ReactiveCommand.Create(() => AutoDetectAndApplyFormFields());
        SaveFlattenedFormCopyCommand = ReactiveCommand.CreateFromTask(SaveFlattenedFormCopyAsync);
        SignDocumentCommand = ReactiveCommand.CreateFromTask(SignDocumentAsync);

        SaveAsCommand = ReactiveCommand.CreateFromTask(SaveAsAsync);
        // #1233: both of these destroy unsaved edits, so both are async now —
        // they have to await the unsaved-changes prompt before proceeding.
        CloseDocumentCommand = ReactiveCommand.CreateFromTask(CloseDocumentAsync);
        ExitCommand = ReactiveCommand.CreateFromTask(ExitAsync);
        LoadRecentFileCommand = ReactiveCommand.CreateFromTask<string>(LoadRecentFileAsync);

        ExportCurrentPageCommand = ReactiveCommand.CreateFromTask(ExportCurrentPageAsync);
        ExportPagesCommand = ReactiveCommand.CreateFromTask(ExportPagesAsync);
        PrintCommand = ReactiveCommand.CreateFromTask(PrintAsync);
        OpenExternalLinkCommand = ReactiveCommand.CreateFromTask<string>(OpenExternalLinkAsync);
        ShowDangerousLinkRefusalCommand = ReactiveCommand.CreateFromTask<string>(ShowDangerousLinkRefusalAsync);
        VerifySignaturesCommand = ReactiveCommand.CreateFromTask(VerifySignaturesAsync);
        ShowPreferencesCommand = ReactiveCommand.Create(ShowPreferences);
    }

    private void InitializeHelpCommands()
    {
        AboutCommand = ReactiveCommand.Create(ShowAbout);
        ShowShortcutsCommand = ReactiveCommand.Create(ShowKeyboardShortcuts);
        ShowDocumentationCommand = ReactiveCommand.Create(ShowDocumentation);
    }
}
