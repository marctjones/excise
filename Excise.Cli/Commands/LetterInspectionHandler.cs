using Excise.Core.Document;

namespace Excise.Cli.Commands;

internal static class LetterInspectionHandler
{
    internal static LetterInspectionResult Execute(
        LetterInspectionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FilePath);
        ArgumentOutOfRangeException.ThrowIfNegative(request.Limit);
        cancellationToken.ThrowIfCancellationRequested();

        var file = new FileInfo(request.FilePath);
        if (!file.Exists)
            throw new FileNotFoundException("The PDF input file does not exist.", file.FullName);

        using var document = PdfDocument.Open(file.FullName);
        cancellationToken.ThrowIfCancellationRequested();

        DocumentPermissionGuard.Require(
            document,
            DocumentAction.Extract,
            "letter/text extraction",
            request.IgnorePermissions,
            request.ForAccessibility,
            accessibilityHint: "--for-accessibility");

        if (request.PageNumber < 1 || request.PageNumber > document.PageCount)
            throw new DocumentPageOutOfRangeException(request.PageNumber, document.PageCount);

        var letters = document.GetPage(request.PageNumber).Letters;
        var selectedCount = Math.Min(letters.Count, request.Limit);
        var selected = new LetterPositionResult[selectedCount];
        for (var index = 0; index < selectedCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var letter = letters[index];
            selected[index] = new LetterPositionResult(
                letter.Value,
                letter.StartX,
                letter.StartY,
                letter.Width,
                letter.FontName);
        }

        return new LetterInspectionResult(
            file.FullName,
            request.PageNumber,
            letters.Count,
            selected);
    }
}

internal readonly record struct LetterInspectionRequest(
    string FilePath,
    int PageNumber,
    int Limit,
    bool IgnorePermissions,
    bool ForAccessibility);

internal sealed record LetterInspectionResult(
    string FilePath,
    int PageNumber,
    int TotalLetterCount,
    IReadOnlyList<LetterPositionResult> Letters);

internal sealed record LetterPositionResult(
    string Value,
    double StartX,
    double StartY,
    double Width,
    string FontName);
