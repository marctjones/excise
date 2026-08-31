using Excise.Core.Authoring;
using Excise.Core.Document;
using Excise.Core.Validation;

namespace Excise.Cli.Commands;

internal static class ValidationHandler
{
    internal static ValidationResult Execute(
        ValidationRequest request,
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

        var reports = new List<ValidationReport>
        {
            PdfUaValidator.Validate(document),
        };
        cancellationToken.ThrowIfCancellationRequested();

        if (request.PdfAConformance is { } conformance)
            reports.Add(PdfAStructuralValidator.Validate(document, conformance));

        return new ValidationResult(
            file.FullName,
            file.Name,
            reports.All(report => report.CheckedSubsetConformant),
            reports);
    }
}

internal readonly record struct ValidationRequest(
    string FilePath,
    string? Password,
    PdfAConformance? PdfAConformance);

internal sealed record ValidationResult(
    string FilePath,
    string FileName,
    bool CheckedSubsetConformant,
    IReadOnlyList<ValidationReport> Reports);
