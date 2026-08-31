using Excise.Core.Document;

namespace Excise.Cli.Commands;

internal static class InfoCommandHandler
{
    public static DocumentInfoResult Execute(
        DocumentInfoRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FilePath);
        ArgumentOutOfRangeException.ThrowIfNegative(request.PageDetailLimit);
        cancellationToken.ThrowIfCancellationRequested();

        var file = new FileInfo(request.FilePath);
        if (!file.Exists)
            throw new FileNotFoundException("The PDF input file does not exist.", file.FullName);

        using var document = request.Password is null
            ? PdfDocument.Open(file.FullName)
            : PdfDocument.Open(file.FullName, request.Password);
        cancellationToken.ThrowIfCancellationRequested();

        var pageDetailCount = Math.Min(document.PageCount, request.PageDetailLimit);
        var pages = new DocumentPageInfo[pageDetailCount];
        for (var pageNumber = 1; pageNumber <= pageDetailCount; pageNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = document.GetPage(pageNumber);
            pages[pageNumber - 1] = new DocumentPageInfo(
                pageNumber,
                page.Width,
                page.Height);
        }

        return new DocumentInfoResult(
            file.FullName,
            file.Name,
            file.Length,
            document.Version,
            document.PageCount,
            document.IsEncrypted,
            new DocumentMetadataInfo(
                document.Title,
                document.Author,
                document.Subject,
                document.Creator,
                document.Producer),
            pages);
    }
}

internal readonly record struct DocumentInfoRequest(
    string FilePath,
    string? Password,
    int PageDetailLimit = 10);

internal sealed record DocumentInfoResult(
    string FilePath,
    string FileName,
    long SizeBytes,
    string Version,
    int PageCount,
    bool Encrypted,
    DocumentMetadataInfo Metadata,
    IReadOnlyList<DocumentPageInfo> Pages);

internal sealed record DocumentMetadataInfo(
    string? Title,
    string? Author,
    string? Subject,
    string? Creator,
    string? Producer);

internal readonly record struct DocumentPageInfo(
    int PageNumber,
    double Width,
    double Height);
