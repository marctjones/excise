using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Excise.App.Services;

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

    public async Task<PageOrganizationResult> RemovePagesAsync(IEnumerable<int> pageIndices, int currentPageIndex)
    {
        if (!_documentService.IsDocumentLoaded)
            return PageOrganizationResult.NoChange(currentPageIndex);

        var indices = ValidPageIndices(pageIndices).ToArray();
        if (indices.Length == 0 || indices.Length >= _documentService.PageCount)
            return PageOrganizationResult.NoChange(currentPageIndex);

        await ShowOperationWarningsAsync(indices);

        var newPageIndex = RemapCurrentPageAfterRemoval(currentPageIndex, indices, _documentService.PageCount);
        _documentService.RemovePages(indices);
        newPageIndex = Math.Min(newPageIndex, Math.Max(0, _documentService.PageCount - 1));

        _logger.LogInformation("Removed {Count} selected page(s)", indices.Length);
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

    public async Task<PageOrganizationResult> MovePagesAsync(
        IEnumerable<int> pageIndices,
        int delta,
        int currentPageIndex)
    {
        if (!_documentService.IsDocumentLoaded)
            return PageOrganizationResult.NoChange(currentPageIndex);

        var indices = ValidPageIndices(pageIndices).ToArray();
        if (indices.Length == 0)
            return PageOrganizationResult.NoChange(currentPageIndex);

        var movable = delta < 0
            ? indices.Any(i => i > 0 && !indices.Contains(i - 1))
            : indices.Any(i => i < _documentService.PageCount - 1 && !indices.Contains(i + 1));
        if (!movable)
            return PageOrganizationResult.NoChange(currentPageIndex, indices);

        await ShowOperationWarningsAsync(indices);

        var newCurrentPageIndex = RemapCurrentPageAfterMove(currentPageIndex, indices, delta, _documentService.PageCount);
        var newSelectedPageIndices = _documentService.MovePages(indices, delta);

        _logger.LogInformation("Moved {Count} selected page(s) by delta {Delta}", indices.Length, delta);
        return PageOrganizationResult.Changed(newCurrentPageIndex, newSelectedPageIndices);
    }

    /// <summary>
    /// Merge every page of each source PDF into a new document saved at
    /// <paramref name="outputPath"/>. Does not affect the currently-loaded
    /// document, so it does not report a <see cref="PageOrganizationResult"/>
    /// change (there is nothing on screen to refresh).
    /// </summary>
    public Task MergeDocumentsAsync(IReadOnlyList<string> sourcePaths, string outputPath)
    {
        _documentService.MergeDocumentsToPdf(sourcePaths, outputPath);
        _logger.LogInformation("Combined {Count} document(s) into {OutputPath}", sourcePaths.Count, outputPath);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Split the currently-loaded document into multiple files under
    /// <paramref name="outputFolder"/> and return the written paths.
    /// Does not modify the currently-loaded document.
    /// </summary>
    public Task<IReadOnlyList<string>> SplitDocumentAsync(
        string outputFolder,
        SplitMode mode,
        int pagesPerChunk = 1,
        IReadOnlyList<int>? boundaries = null)
    {
        if (!_documentService.IsDocumentLoaded)
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        var paths = _documentService.SplitDocument(outputFolder, mode, pagesPerChunk, boundaries);
        _logger.LogInformation("Split document into {Count} file(s) in {OutputFolder}", paths.Count, outputFolder);
        return Task.FromResult(paths);
    }

    internal Task<SplitDocumentResult> SplitDocumentAsync(SplitDocumentRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputFolder);
        return SplitDocumentCoreAsync(request);
    }

    internal static SplitSpecificationParseResult ParseSplitSpecification(string? specification)
    {
        if (string.IsNullOrWhiteSpace(specification))
            return SplitSpecificationParseResult.Invalid("Enter a split specification.");

        var normalized = specification.Trim();
        if (string.Equals(normalized, "single", StringComparison.OrdinalIgnoreCase))
            return SplitSpecificationParseResult.Valid(new(SplitMode.SinglePages, 1, null));

        if (string.Equals(normalized, "bookmarks", StringComparison.OrdinalIgnoreCase))
            return SplitSpecificationParseResult.Valid(new(SplitMode.Bookmarks, 1, null));

        if (normalized.Contains(','))
        {
            var boundaries = normalized
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(value => int.TryParse(value, out var pageNumber) ? pageNumber - 1 : -1)
                .Where(pageIndex => pageIndex >= 0)
                .Distinct()
                .OrderBy(pageIndex => pageIndex)
                .ToArray();
            return boundaries.Length == 0
                ? SplitSpecificationParseResult.Invalid(
                    $"Could not parse page numbers from \"{normalized}\".")
                : SplitSpecificationParseResult.Valid(
                    new(SplitMode.PageBoundaries, 1, boundaries));
        }

        return int.TryParse(normalized, out var pagesPerChunk) && pagesPerChunk > 0
            ? SplitSpecificationParseResult.Valid(
                new(SplitMode.EveryNPages, pagesPerChunk, null))
            : SplitSpecificationParseResult.Invalid(
                $"Could not understand \"{normalized}\".");
    }

    private async Task<SplitDocumentResult> SplitDocumentCoreAsync(SplitDocumentRequest request)
    {
        var paths = await SplitDocumentAsync(
            request.OutputFolder,
            request.Specification.Mode,
            request.Specification.PagesPerChunk,
            request.Specification.Boundaries);
        return new SplitDocumentResult(paths);
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

    private IEnumerable<int> ValidPageIndices(IEnumerable<int> pageIndices) =>
        pageIndices
            .Where(i => i >= 0 && i < _documentService.PageCount)
            .Distinct()
            .OrderBy(i => i);

    private static int RemapCurrentPageAfterRemoval(
        int currentPageIndex,
        IReadOnlyCollection<int> removedIndices,
        int originalPageCount)
    {
        if (removedIndices.Contains(currentPageIndex))
            return Math.Min(removedIndices.Min(), originalPageCount - removedIndices.Count - 1);

        var removedBeforeCurrent = removedIndices.Count(i => i < currentPageIndex);
        return currentPageIndex - removedBeforeCurrent;
    }

    private static int RemapCurrentPageAfterMove(
        int currentPageIndex,
        IEnumerable<int> pageIndices,
        int delta,
        int pageCount)
    {
        var current = currentPageIndex;
        var selected = pageIndices.OrderBy(i => i).ToHashSet();
        var traversal = delta < 0
            ? selected.OrderBy(i => i).ToArray()
            : selected.OrderByDescending(i => i).ToArray();

        foreach (var index in traversal)
        {
            if (!selected.Contains(index))
                continue;

            var target = index + delta;
            if (target < 0 || target >= pageCount || selected.Contains(target))
                continue;

            if (current == index)
                current = target;
            else if (current == target)
                current = index;

            selected.Remove(index);
            selected.Add(target);
        }

        return current;
    }
}

public sealed record PageOrganizationResult(
    bool DidChange,
    int? CurrentPageIndex,
    IReadOnlyList<int> SelectedPageIndices)
{
    public static PageOrganizationResult Changed(
        int? currentPageIndex = null,
        IReadOnlyList<int>? selectedPageIndices = null) =>
        new(true, currentPageIndex, selectedPageIndices ?? Array.Empty<int>());

    public static PageOrganizationResult NoChange(
        int? currentPageIndex = null,
        IReadOnlyList<int>? selectedPageIndices = null) =>
        new(false, currentPageIndex, selectedPageIndices ?? Array.Empty<int>());
}

internal sealed record SplitSpecification(
    SplitMode Mode,
    int PagesPerChunk,
    IReadOnlyList<int>? Boundaries);

internal sealed record SplitSpecificationParseResult(
    SplitSpecification? Specification,
    string? ErrorMessage)
{
    public bool IsValid => Specification is not null;

    public static SplitSpecificationParseResult Valid(SplitSpecification specification) =>
        new(specification, null);

    public static SplitSpecificationParseResult Invalid(string errorMessage) =>
        new(null, errorMessage);
}

internal readonly record struct SplitDocumentRequest(
    string OutputFolder,
    SplitSpecification Specification);

internal sealed record SplitDocumentResult(IReadOnlyList<string> WrittenPaths);
