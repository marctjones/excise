using Excise.App.Services;
using Excise.App.Services.Host;
using Excise.App.Services.Printing;
using Excise.App.Tests.Utilities.Fakes;
using Excise.App.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Excise.App.Tests.Utilities;

/// <summary>
/// Creates the production MainWindowViewModel contract with deterministic,
/// headless defaults. Tests override only the collaborator relevant to the
/// behavior under test; production construction remains owned by
/// ApplicationComposition.
/// </summary>
internal static class MainWindowViewModelTestFactory
{
    internal static MainWindowViewModel Create(
        ILogger<MainWindowViewModel>? logger = null,
        ILoggerFactory? loggerFactory = null,
        PdfDocumentService? documentService = null,
        RedactionService? redactionService = null,
        PdfTextExtractionService? textExtractionService = null,
        PdfSearchService? searchService = null,
        SignatureVerificationService? signatureService = null,
        FilenameSuggestionService? filenameSuggestionService = null,
        ToastService? toastService = null,
        RedactedCopyDialogFormatter? redactedCopyDialogFormatter = null,
        RedactionWorkflowService? redactionWorkflowService = null,
        DocumentTextIndexSession? textIndexSession = null,
        IUserDialogService? dialogService = null,
        SignatureVerificationSummaryFormatter? signatureSummaryFormatter = null,
        SignatureVerificationWorkflowService? signatureWorkflowService = null,
        PageOrganizationWorkflowService? pageOrganizationWorkflow = null,
        DocumentImageExportWorkflowService? imageExportWorkflow = null,
        AnnotationWorkflowService? annotationWorkflow = null,
        bool thumbnailPrewarmEnabled = true,
        ReleasedMemoryReclaimer? memoryReclaimer = null,
        IFilePicker? filePicker = null,
        IWindowHost? windowHost = null,
        ITextClipboard? clipboard = null,
        ISettingsStore? settingsStore = null,
        IRecentFilesStore? recentFilesStore = null,
        IDocumentPrinter? printer = null,
        DocumentPrintWorkflowService? printWorkflow = null)
    {
        // #1481: the default collects nothing, so the serial suite's many
        // close/replace calls do not each run a blocking, compacting gen2 GC.
        // Tests of the reclaim inject their own.
        memoryReclaimer ??= new ReleasedMemoryReclaimer(collect: static _ => { });
        loggerFactory ??= NullLoggerFactory.Instance;
        logger ??= NullLogger<MainWindowViewModel>.Instance;
        documentService ??= new PdfDocumentService(NullLogger<PdfDocumentService>.Instance);
        redactionService ??= new RedactionService(
            NullLogger<RedactionService>.Instance,
            loggerFactory);
        redactedCopyDialogFormatter ??= new RedactedCopyDialogFormatter();
        textExtractionService ??= new PdfTextExtractionService(
            NullLogger<PdfTextExtractionService>.Instance);
        redactionWorkflowService ??= new RedactionWorkflowService(
            redactionService,
            textExtractionService,
            NullLogger<RedactionWorkflowService>.Instance);
        searchService ??= new PdfSearchService(NullLogger<PdfSearchService>.Instance);
        var searchSession = new DocumentSearchSession(searchService);
        textIndexSession ??= new DocumentTextIndexSession(
            NullLogger<DocumentTextIndexSession>.Instance);
        filenameSuggestionService ??= new FilenameSuggestionService();
        toastService ??= new ToastService();
        dialogService ??= new NullUserDialogService();
        signatureService ??= new SignatureVerificationService(
            NullLogger<SignatureVerificationService>.Instance);
        signatureSummaryFormatter ??= new SignatureVerificationSummaryFormatter();
        signatureWorkflowService ??= new SignatureVerificationWorkflowService(
            signatureService,
            signatureSummaryFormatter,
            dialogService,
            NullLogger<SignatureVerificationWorkflowService>.Instance);
        pageOrganizationWorkflow ??= new PageOrganizationWorkflowService(
            documentService,
            dialogService,
            NullLogger<PageOrganizationWorkflowService>.Instance);
        imageExportWorkflow ??= new DocumentImageExportWorkflowService(
            new PageImageRenderer(),
            NullLogger<DocumentImageExportWorkflowService>.Instance);
        annotationWorkflow ??= new AnnotationWorkflowService(
            documentService,
            NullLogger<AnnotationWorkflowService>.Instance);

        // #1500 step 1: the PRODUCTION host adapters by default, one host per
        // view model. That is what keeps every existing test byte-identical —
        // vm.StorageProviderOverride and vm.MainWindowResolver forward into
        // this host, and AvaloniaFilePicker resolves from it, so the real
        // picker path still runs. Per-instance rather than shared so an
        // override set by one test cannot leak into the next view model.
        windowHost ??= new AvaloniaWindowHost();
        filePicker ??= new AvaloniaFilePicker(
            windowHost,
            NullLogger<AvaloniaFilePicker>.Instance);
        clipboard ??= new AvaloniaTextClipboard();

        // #1500 step 2: the FILE-backed stores by default, deliberately.
        // Defaulting to the in-memory fakes would be the cleaner-looking
        // choice and would break real tests: PerformancePreferencesTests
        // (Preferences_Save_WritesWindowSettings…, and the writer-interleaving
        // test that mixes a direct WindowSettings.Update with the view model's
        // own save) and PerformancePreferencesLiveApplyTests both drive a
        // factory-built view model and then assert on WindowSettings.Load()
        // from disk. Tests that want isolation pass InMemorySettingsStore.
        settingsStore ??= new FileSettingsStore();
        recentFilesStore ??= new FileRecentFilesStore();

        // #1545: never the macOS PDFKit printer. The suite runs on a Mac, and a
        // headless test must not reach AppKit's print sheet. The fake still
        // takes the real temp-copy path, so PrintCommand is exercised.
        printWorkflow ??= new DocumentPrintWorkflowService(
            redactionWorkflowService,
            printer ?? new RecordingDocumentPrinter(),
            NullLogger<DocumentPrintWorkflowService>.Instance);

        var viewModel = new MainWindowViewModel(
            logger,
            documentService,
            redactionService,
            redactedCopyDialogFormatter,
            redactionWorkflowService,
            textExtractionService,
            searchSession,
            textIndexSession,
            filenameSuggestionService,
            toastService,
            dialogService,
            signatureWorkflowService,
            pageOrganizationWorkflow,
            imageExportWorkflow,
            annotationWorkflow,
            memoryReclaimer,
            printWorkflow,
            filePicker,
            windowHost,
            clipboard,
            settingsStore,
            recentFilesStore);
        viewModel.ThumbnailPrewarmEnabled = thumbnailPrewarmEnabled;
        return viewModel;
    }
}
