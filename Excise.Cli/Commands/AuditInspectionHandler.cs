using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.Ocr;

namespace Excise.Cli.Commands;

internal static class AuditInspectionHandler
{
    internal static AuditInspectionResult Execute(
        AuditInspectionRequest request,
        IDifferentialOcrScanner? differentialScanner = null,
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
        var structuralHits = HiddenTextDetector.Scan(
            document,
            includeVisibleFailedRedactions: true);
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<DifferentialOcrHit> differentialHits = [];
        if (request.Deep)
        {
            differentialScanner ??= new TesseractDifferentialOcrScanner();
            if (!differentialScanner.IsAvailable())
                throw new DeepAuditUnavailableException();

            var bytes = File.ReadAllBytes(file.FullName);
            cancellationToken.ThrowIfCancellationRequested();
            differentialHits = differentialScanner.Scan(bytes);
        }

        return new AuditInspectionResult(
            file.FullName,
            request.Deep,
            structuralHits,
            differentialHits);
    }
}

internal interface IDifferentialOcrScanner
{
    bool IsAvailable();

    IReadOnlyList<DifferentialOcrHit> Scan(byte[] pdfBytes);
}

internal sealed class TesseractDifferentialOcrScanner : IDifferentialOcrScanner
{
    private readonly PdfOcrService _ocr = new(useNativeFastPath: true);

    public bool IsAvailable() => _ocr.IsAvailable();

    public IReadOnlyList<DifferentialOcrHit> Scan(byte[] pdfBytes)
        => new DifferentialOcrAuditor(_ocr).Scan(pdfBytes);
}

internal readonly record struct AuditInspectionRequest(
    string FilePath,
    string? Password,
    bool Deep);

internal sealed record AuditInspectionResult(
    string FilePath,
    bool DeepRun,
    IReadOnlyList<HiddenTextRecord> StructuralHits,
    IReadOnlyList<DifferentialOcrHit> DifferentialOcrHits)
{
    internal int TotalHitCount => StructuralHits.Count + DifferentialOcrHits.Count;
}

internal sealed class DeepAuditUnavailableException : InvalidOperationException
{
}
