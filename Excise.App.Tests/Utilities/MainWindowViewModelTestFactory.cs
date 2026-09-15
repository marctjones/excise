using Excise.App.Services;
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
        ReleasedMemoryReclaimer? memoryReclaimer = null)
    {
        // #1481: the default collects nothing, so the serial suite's many
        // close/replace calls do not each run a blocking, compacting gen2 GC.
        // Tests of the reclaim inject their own.
        memoryReclaimer ??= new ReleasedMemoryReclaimer(collect: static _ => { }, relieveNativeHeap: static () => 0);
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
            memoryReclaimer);
        viewModel.ThumbnailPrewarmEnabled = thumbnailPrewarmEnabled;
        return viewModel;
    }
}
