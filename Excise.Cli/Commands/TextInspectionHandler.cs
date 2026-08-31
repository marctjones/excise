using Excise.Core.Document;

namespace Excise.Cli.Commands;

internal static class TextInspectionHandler
{
    internal static TextInspectionResult Execute(
        TextInspectionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FilePath);
        cancellationToken.ThrowIfCancellationRequested();

        var file = new FileInfo(request.FilePath);
        if (!file.Exists)
            throw new FileNotFoundException("The PDF input file does not exist.", file.FullName);

        using var document = string.IsNullOrEmpty(request.Password)
            ? PdfDocument.Open(file.FullName)
            : PdfDocument.Open(file.FullName, request.Password);
        cancellationToken.ThrowIfCancellationRequested();

        DocumentPermissionGuard.Require(
            document,
            DocumentAction.Extract,
            "text extraction",
            request.IgnorePermissions,
            request.ForAccessibility,
            accessibilityHint: request.AccessibilityHint,
            overrideHint: request.OverrideHint);

        if (request.PageNumber is { } requestedPage &&
            (requestedPage < 1 || requestedPage > document.PageCount))
        {
            throw new DocumentPageOutOfRangeException(requestedPage, document.PageCount);
        }

        var firstPage = request.PageNumber ?? 1;
        var lastPage = request.PageNumber ?? document.PageCount;
        var pages = new TextPageResult[lastPage - firstPage + 1];
        for (var pageNumber = firstPage; pageNumber <= lastPage; pageNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pages[pageNumber - firstPage] = new TextPageResult(
                pageNumber,
                document.GetPage(pageNumber).Text);
        }

        return new TextInspectionResult(
            file.FullName,
            document.PageCount,
            request.PageNumber,
            pages);
    }
}

internal readonly record struct TextInspectionRequest(
    string FilePath,
    string? Password,
    int? PageNumber,
    bool IgnorePermissions,
    bool ForAccessibility,
    string AccessibilityHint = "--for-accessibility",
    string OverrideHint = "--ignore-permissions");

internal sealed record TextInspectionResult(
    string FilePath,
    int PageCount,
    int? SelectedPageNumber,
    IReadOnlyList<TextPageResult> Pages);

internal sealed record TextPageResult(int PageNumber, string Text);

internal sealed class DocumentPageOutOfRangeException(int pageNumber, int pageCount)
    : ArgumentOutOfRangeException(
        nameof(pageNumber),
        pageNumber,
        $"Page {pageNumber} is outside the document range 1..{pageCount}.")
{
    internal int PageNumber { get; } = pageNumber;

    internal int PageCount { get; } = pageCount;
}
