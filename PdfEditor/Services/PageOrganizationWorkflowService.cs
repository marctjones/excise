using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PdfEditor.Services;

public sealed class PageOrganizationWorkflowService
{
    private readonly PdfDocumentService _documentService;
    private readonly IUserDialogService _dialogService;
    private readonly ILogger<PageOrganizationWorkflowService> _logger;

    public PageOrganizationWorkflowService(
        PdfDocumentService documentService,
        IUserDialogService dialogService,
        ILogger<PageOrganizationWorkflowService> logger)
    {
        ArgumentNullException.ThrowIfNull(documentService);
        ArgumentNullException.ThrowIfNull(dialogService);
        ArgumentNullException.ThrowIfNull(logger);

        _documentService = documentService;
        _dialogService = dialogService;
        _logger = logger;
    }

    public async Task<PageOrganizationResult> RemovePageAsync(int pageIndex)
    {
        if (!_documentService.IsDocumentLoaded || _documentService.PageCount <= 1)
            return PageOrganizationResult.NoChange(pageIndex);

        await ShowOperationWarningsAsync(new[] { pageIndex });

        _documentService.RemovePage(pageIndex);
        var newPageIndex = Math.Min(pageIndex, Math.Max(0, _documentService.PageCount - 1));
        _logger.LogInformation("Removed page {PageIndex}; current page should become {NewPageIndex}", pageIndex, newPageIndex);

        return PageOrganizationResult.Changed(newPageIndex);
    }

    public async Task<PageOrganizationResult> InsertPagesFromFileAsync(string sourcePdfPath, int insertAtIndex)
    {
        if (!_documentService.IsDocumentLoaded)
            return PageOrganizationResult.NoChange();

        await ShowOperationWarningsAsync();
        _documentService.InsertPagesFromPdf(sourcePdfPath, insertAtIndex);

        _logger.LogInformation("Inserted pages from {SourcePdfPath} at {InsertAtIndex}", sourcePdfPath, insertAtIndex);
        return PageOrganizationResult.Changed();
    }

    public async Task ExtractPagesToFileAsync(string outputPath, IEnumerable<int> pageIndices)
    {
        if (!_documentService.IsDocumentLoaded)
            return;

        var materialized = pageIndices.Distinct().ToArray();
        await ShowOperationWarningsAsync(materialized);
        _documentService.ExtractPagesToPdf(outputPath, materialized);

        _logger.LogInformation("Extracted {PageCount} page(s) to {OutputPath}", materialized.Length, outputPath);
    }

    public async Task<PageOrganizationResult> MovePageAsync(int fromIndex, int toIndex)
    {
        if (!_documentService.IsDocumentLoaded || fromIndex == toIndex)
            return PageOrganizationResult.NoChange(fromIndex);

        await ShowOperationWarningsAsync(new[] { fromIndex, toIndex });
        _documentService.MovePage(fromIndex, toIndex);

        _logger.LogInformation("Moved page from {FromIndex} to {ToIndex}", fromIndex, toIndex);
        return PageOrganizationResult.Changed(toIndex);
    }

    private async Task ShowOperationWarningsAsync(IEnumerable<int>? pageIndices = null)
    {
        var diagnostics = _documentService.AnalyzePageOperationPreservation(pageIndices);
        if (!diagnostics.HasWarnings)
            return;

        await _dialogService.ShowMessageAsync(
            "Page Organization",
            "This operation may require manual review after saving:\n\n" +
            string.Join("\n", diagnostics.Warnings.Select(w => $"- {w}")));
    }
}

public sealed record PageOrganizationResult(bool DidChange, int? CurrentPageIndex)
{
    public static PageOrganizationResult Changed(int? currentPageIndex = null) => new(true, currentPageIndex);

    public static PageOrganizationResult NoChange(int? currentPageIndex = null) => new(false, currentPageIndex);
}
