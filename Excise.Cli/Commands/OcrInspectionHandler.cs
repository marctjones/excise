using Excise.Core.Document;
using Excise.Ocr;

namespace Excise.Cli.Commands;

internal static class OcrInspectionHandler
{
    internal static OcrInspectionResult Execute(
        OcrInspectionRequest request,
        IOcrTextRecognizer? recognizer = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FilePath);
        cancellationToken.ThrowIfCancellationRequested();

        var file = new FileInfo(request.FilePath);
        if (!file.Exists)
            throw new FileNotFoundException("The PDF input file does not exist.", file.FullName);

        recognizer ??= new TesseractTextRecognizer(
            request.Language,
            request.Dpi,
            request.TessdataPrefix);
        if (!recognizer.IsAvailable())
            throw new OcrUnavailableException();

        using var document = PdfDocument.Open(file.FullName);
        cancellationToken.ThrowIfCancellationRequested();

        DocumentPermissionGuard.Require(
            document,
            DocumentAction.Extract,
            "OCR text extraction",
            request.IgnorePermissions,
            request.ForAccessibility,
            accessibilityHint: "--for-accessibility");

        if (request.PageNumber is { } requestedPage &&
            (requestedPage < 1 || requestedPage > document.PageCount))
        {
            throw new DocumentPageOutOfRangeException(requestedPage, document.PageCount);
        }

        var firstPage = request.PageNumber ?? 1;
        var lastPage = request.PageNumber ?? document.PageCount;
        var pages = new OcrTextPageResult[lastPage - firstPage + 1];
        for (var pageNumber = firstPage; pageNumber <= lastPage; pageNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pages[pageNumber - firstPage] = new OcrTextPageResult(
                pageNumber,
                recognizer.RecognizeText(document.GetPage(pageNumber)));
        }

        return new OcrInspectionResult(file.FullName, document.PageCount, pages);
    }
}

internal interface IOcrTextRecognizer
{
    bool IsAvailable();

    string RecognizeText(PdfPage page);
}

internal sealed class TesseractTextRecognizer(
    string language,
    int dpi,
    string? tessdataPrefix) : IOcrTextRecognizer
{
    private readonly PdfOcrService _service = new(language, dpi, tessdataPrefix: tessdataPrefix);

    public bool IsAvailable() => _service.IsAvailable();

    public string RecognizeText(PdfPage page) => _service.RecognizePage(page).Text;
}

internal readonly record struct OcrInspectionRequest(
    string FilePath,
    int? PageNumber,
    int Dpi,
    string Language,
    string? TessdataPrefix,
    bool IgnorePermissions,
    bool ForAccessibility);

internal sealed record OcrInspectionResult(
    string FilePath,
    int PageCount,
    IReadOnlyList<OcrTextPageResult> Pages);

internal sealed record OcrTextPageResult(int PageNumber, string Text);

internal sealed class OcrUnavailableException : InvalidOperationException
{
}
