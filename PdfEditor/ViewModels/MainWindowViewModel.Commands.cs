using Microsoft.Extensions.Logging;
using ReactiveUI;
using System;
using System.Reactive;

namespace PdfEditor.ViewModels;

public partial class MainWindowViewModel
{
    // Mode indicator for status bar.
    public string CurrentModeText
    {
        get
        {
            if (IsRedactionMode) return "🔴 Redaction Mode";
            if (IsTextSelectionMode) return "📝 Text Selection Mode";
            if (IsTypewriterMode) return "✎ Typewriter Mode";
            if (IsContinuousView) return "📜 Continuous Scroll";
            return "👆 View Mode";
        }
    }

    public ReactiveCommand<Unit, Unit> OpenFileCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> SaveFileCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> RemoveCurrentPageCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> AddPagesCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> InsertPagesBeforeCurrentCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> InsertPagesAfterCurrentCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ExtractCurrentPageCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ExtractSelectedPagesCommand { get; private set; } = null!;
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
    public ReactiveCommand<Unit, Unit> ToggleOutlineCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleThumbnailsCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleContinuousViewCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, int> AutoDetectFieldsCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> SaveFlattenedFormCopyCommand { get; private set; } = null!;
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
    public ReactiveCommand<Unit, Unit> VerifySignaturesCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ShowPreferencesCommand { get; private set; } = null!;

    public ReactiveCommand<Unit, Unit> AboutCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ShowShortcutsCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ShowDocumentationCommand { get; private set; } = null!;

    private void InitializeCommands()
    {
        _logger.LogDebug("Setting up ReactiveUI commands");

        OpenFileCommand = ReactiveCommand.CreateFromTask(OpenFileAsync);
        SaveFileCommand = ReactiveCommand.CreateFromTask(SaveFileAsync);
        RemoveCurrentPageCommand = ReactiveCommand.CreateFromTask(RemoveCurrentPageAsync);
        AddPagesCommand = ReactiveCommand.CreateFromTask(AddPagesAsync);
        InsertPagesBeforeCurrentCommand = ReactiveCommand.CreateFromTask(InsertPagesBeforeCurrentAsync);
        InsertPagesAfterCurrentCommand = ReactiveCommand.CreateFromTask(InsertPagesAfterCurrentAsync);
        ExtractCurrentPageCommand = ReactiveCommand.CreateFromTask(ExtractCurrentPageAsync);
        ExtractSelectedPagesCommand = ReactiveCommand.CreateFromTask(ExtractSelectedPagesAsync);
        RemoveSelectedPagesCommand = ReactiveCommand.CreateFromTask(RemoveSelectedPagesAsync);
        MoveSelectedPagesEarlierCommand = ReactiveCommand.CreateFromTask(() => MoveSelectedPagesAsync(-1));
        MoveSelectedPagesLaterCommand = ReactiveCommand.CreateFromTask(() => MoveSelectedPagesAsync(1));
        ClearSelectedPagesCommand = ReactiveCommand.Create(ClearSelectedPages);
        MoveCurrentPageEarlierCommand = ReactiveCommand.CreateFromTask(MoveCurrentPageEarlierAsync);
        MoveCurrentPageLaterCommand = ReactiveCommand.CreateFromTask(MoveCurrentPageLaterAsync);
        ToggleRedactionModeCommand = ReactiveCommand.Create(ToggleRedactionMode);
        ApplyRedactionCommand = ReactiveCommand.CreateFromTask(ApplyRedactionAsync);
        RemovePendingRedactionCommand = ReactiveCommand.Create<Guid>(RemovePendingRedaction);
        ClearAllRedactionsCommand = ReactiveCommand.Create(ClearAllRedactions);
        ApplyAllRedactionsCommand = ReactiveCommand.CreateFromTask(ApplyAllRedactionsAsync);

        ApplyRedactionCommand.ThrownExceptions.Subscribe(ex =>
            _logger.LogError(ex, "ApplyRedactionCommand threw exception"));

        ToggleTextSelectionModeCommand = ReactiveCommand.Create(ToggleTextSelectionMode);
        ToggleFormAuthoringModeCommand = ReactiveCommand.Create(() => { IsFormAuthoringMode = !IsFormAuthoringMode; });
        ToggleTypewriterModeCommand = ReactiveCommand.Create(ToggleTypewriterMode);
        ToggleOutlineCommand = ReactiveCommand.Create(ToggleOutlineSidebar);
        ToggleThumbnailsCommand = ReactiveCommand.Create(ToggleThumbnailsSidebar);
        ToggleContinuousViewCommand = ReactiveCommand.Create(ToggleContinuousView);
        AutoDetectFieldsCommand = ReactiveCommand.Create(() => AutoDetectAndApplyFormFields());
        SaveFlattenedFormCopyCommand = ReactiveCommand.CreateFromTask(SaveFlattenedFormCopyAsync);
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

        SaveAsCommand = ReactiveCommand.CreateFromTask(SaveAsAsync);
        CloseDocumentCommand = ReactiveCommand.Create(CloseDocument);
        ExitCommand = ReactiveCommand.Create(Exit);
        LoadRecentFileCommand = ReactiveCommand.CreateFromTask<string>(LoadRecentFileAsync);

        ExportCurrentPageCommand = ReactiveCommand.CreateFromTask(ExportCurrentPageAsync);
        ExportPagesCommand = ReactiveCommand.CreateFromTask(ExportPagesAsync);
        PrintCommand = ReactiveCommand.CreateFromTask(PrintAsync);
        VerifySignaturesCommand = ReactiveCommand.CreateFromTask(VerifySignaturesAsync);
        ShowPreferencesCommand = ReactiveCommand.Create(ShowPreferences);

        AboutCommand = ReactiveCommand.Create(ShowAbout);
        ShowShortcutsCommand = ReactiveCommand.Create(ShowKeyboardShortcuts);
        ShowDocumentationCommand = ReactiveCommand.Create(ShowDocumentation);

        InitializeSearchCommands();
        InitializeScriptingCommands();
    }
}
