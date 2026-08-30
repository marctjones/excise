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
        PdfRenderService? renderService = null,
        RedactionService? redactionService = null,
        PdfTextExtractionService? textExtractionService = null,
        PdfSearchService? searchService = null,
        SignatureVerificationService? signatureService = null,
        FilenameSuggestionService? filenameSuggestionService = null,
        ToastService? toastService = null,
        RedactedCopySafetyService? redactedCopySafetyService = null,
        DocumentTextIndexSession? textIndexSession = null,
        IUserDialogService? dialogService = null,
        SignatureVerificationSummaryFormatter? signatureSummaryFormatter = null,
        SignatureVerificationWorkflowService? signatureWorkflowService = null,
        PageOrganizationWorkflowService? pageOrganizationWorkflow = null,
        AnnotationWorkflowService? annotationWorkflow = null,
        bool thumbnailPrewarmEnabled = true)
    {
        loggerFactory ??= NullLoggerFactory.Instance;
        logger ??= NullLogger<MainWindowViewModel>.Instance;
        documentService ??= new PdfDocumentService(NullLogger<PdfDocumentService>.Instance);
        renderService ??= new PdfRenderService(NullLogger<PdfRenderService>.Instance);
        redactionService ??= new RedactionService(
            NullLogger<RedactionService>.Instance,
            loggerFactory);
        redactedCopySafetyService ??= new RedactedCopySafetyService(
            NullLogger<RedactedCopySafetyService>.Instance);
        textExtractionService ??= new PdfTextExtractionService(
            NullLogger<PdfTextExtractionService>.Instance);
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
        annotationWorkflow ??= new AnnotationWorkflowService(
            documentService,
            NullLogger<AnnotationWorkflowService>.Instance);

        var viewModel = new MainWindowViewModel(
            logger,
            documentService,
            renderService,
            redactionService,
            redactedCopySafetyService,
            textExtractionService,
            searchSession,
            textIndexSession,
            filenameSuggestionService,
            toastService,
            dialogService,
            signatureWorkflowService,
            pageOrganizationWorkflow,
            annotationWorkflow);
        viewModel.ThumbnailPrewarmEnabled = thumbnailPrewarmEnabled;
        return viewModel;
    }
}
