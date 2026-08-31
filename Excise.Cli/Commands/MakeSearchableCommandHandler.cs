using Excise.Core.Document;
using Excise.Ocr;

namespace Excise.Cli.Commands;

internal static class MakeSearchableCommandHandler
{
    internal static MakeSearchableCommandResult Execute(
        MakeSearchableCommandRequest request,
        ISearchablePageConverter? converter = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputPath);
        cancellationToken.ThrowIfCancellationRequested();

        var input = new FileInfo(request.InputPath);
        if (!input.Exists)
            throw new FileNotFoundException("The PDF input file does not exist.", input.FullName);

        converter ??= new TesseractSearchablePageConverter(
            request.Language,
            request.Dpi,
            request.TessdataPrefix);
        if (!converter.IsAvailable())
            throw new OcrUnavailableException();

        var outputPath = Path.GetFullPath(request.OutputPath);
        using var document = PdfDocumentLifetime.OpenInputForOutput(input.FullName, outputPath);
        cancellationToken.ThrowIfCancellationRequested();

        var firstPage = request.PageNumber.GetValueOrDefault(1);
        var lastPage = request.PageNumber ?? document.PageCount;
        if (firstPage < 1 || firstPage > document.PageCount || lastPage < firstPage || lastPage > document.PageCount)
            throw new DocumentPageOutOfRangeException(firstPage, document.PageCount);

        var pages = new List<SearchablePageResult>(lastPage - firstPage + 1);
        for (var pageNumber = firstPage; pageNumber <= lastPage; pageNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pages.Add(converter.MakePageSearchable(document.GetPage(pageNumber), request.Force));
        }

        cancellationToken.ThrowIfCancellationRequested();
        // #643: preserve source encryption (empty-password sources only here).
        document.Save(outputPath, document.GetReEncryptionOptions(userPassword: null));

        return new MakeSearchableCommandResult(
            input.FullName,
            outputPath,
            firstPage,
            lastPage,
            pages);
    }
}

internal interface ISearchablePageConverter
{
    bool IsAvailable();

    SearchablePageResult MakePageSearchable(PdfPage page, bool force);
}

internal sealed class TesseractSearchablePageConverter : ISearchablePageConverter
{
    private readonly PdfOcrService _ocr;
    private readonly PdfSearchableConverter _converter;

    internal TesseractSearchablePageConverter(string language, int dpi, string? tessdataPrefix)
    {
        _ocr = new PdfOcrService(language, dpi, tessdataPrefix: tessdataPrefix);
        _converter = new PdfSearchableConverter(_ocr);
    }

    public bool IsAvailable() => _ocr.IsAvailable();

    public SearchablePageResult MakePageSearchable(PdfPage page, bool force)
        => _converter.MakePageSearchable(page, force);
}

internal readonly record struct MakeSearchableCommandRequest(
    string InputPath,
    string OutputPath,
    int? PageNumber,
    int Dpi,
    string Language,
    string? TessdataPrefix,
    bool Force);

internal sealed record MakeSearchableCommandResult(
    string InputPath,
    string OutputPath,
    int FirstPage,
    int LastPage,
    IReadOnlyList<SearchablePageResult> Pages)
{
    internal int PagesProcessed => Pages.Count(page => !page.Skipped);
    internal int PagesSkipped => Pages.Count(page => page.Skipped);
    internal int TotalWordsWritten => Pages.Sum(page => page.WordsWritten);
    internal IReadOnlyList<SearchablePageResult> EncodingGaps => Pages
        .Where(page => page.WordsSkippedEncoding > 0)
        .ToArray();
}
