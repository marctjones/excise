using Excise.App.Models;
using Excise.Core.Document;
using Excise.Core.Editing;
using Excise.Core.Security;
using Excise.Core.Text.Segmentation;
using Microsoft.Extensions.Logging;

namespace Excise.App.Services;

/// <summary>
/// Owns the desktop redaction transaction boundary. It delegates glyph
/// removal to <see cref="RedactionService"/> and carrier verification to the
/// shared <see cref="RedactedCopySafetyPolicy"/>; it never implements either.
/// </summary>
internal sealed class RedactionWorkflowService
{
    private readonly RedactionService _redactionService;
    private readonly PdfTextExtractionService _textExtractionService;
    private readonly ILogger<RedactionWorkflowService> _logger;

    public RedactionWorkflowService(
        RedactionService redactionService,
        PdfTextExtractionService textExtractionService,
        ILogger<RedactionWorkflowService> logger)
    {
        _redactionService = redactionService ?? throw new ArgumentNullException(nameof(redactionService));
        _textExtractionService = textExtractionService ?? throw new ArgumentNullException(nameof(textExtractionService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public RedactionMarkResult CaptureMark(RedactionMarkRequest request)
    {
        var previewText = string.Empty;
        if (!string.IsNullOrWhiteSpace(request.SourcePath))
        {
            try
            {
                previewText = _textExtractionService.ExtractTextFromArea(
                    request.SourcePath,
                    request.PageIndex,
                    request.PageArea);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "Could not extract redaction preview text");
            }
        }

        return new RedactionMarkResult(request.PageArea, previewText);
    }

    public RedactionApplicationResult ApplyToDocument(RedactionApplicationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Document);
        ArgumentNullException.ThrowIfNull(request.Redactions);
        ArgumentNullException.ThrowIfNull(request.TypewriterOperations);

        var skippedCount = 0;
        foreach (var redaction in request.Redactions)
        {
            if (redaction.PageNumber < 1 || redaction.PageNumber > request.Document.PageCount)
            {
                _logger.LogWarning(
                    "Skipping redaction for invalid page {Page}. Document has {PageCount} pages.",
                    redaction.PageNumber,
                    request.Document.PageCount);
                skippedCount++;
                continue;
            }

            _redactionService.RedactArea(
                request.Document.Pages[redaction.PageNumber - 1],
                redaction.PageArea);
        }

        var appliedTypewriterOperations = PdfTypewriterTextApplier.Apply(
            request.Document,
            request.TypewriterOperations);
        var safetyReport = RedactedCopySafetyPolicy.Evaluate(
            request.Document,
            RedactedCopySafetyRequest.ForAreas(
                request.Redactions.Select(ToSafetyArea).ToArray(),
                skippedCount));

        foreach (var failedStage in safetyReport.FailedStages)
        {
            _logger.LogWarning(
                "Redacted-copy safety stage {SafetyStage} could not complete",
                failedStage);
        }

        return new RedactionApplicationResult(
            request.Redactions.Count - skippedCount,
            skippedCount,
            appliedTypewriterOperations.Count,
            safetyReport);
    }

    public RedactedCopyResult CreateRedactedCopy(RedactedCopyRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputPath);
        var application = ApplyToDocument(request.Application);
        request.Application.Document.Save(request.OutputPath, request.EncryptionOptions);
        _logger.LogInformation(
            "Saved redacted copy with {AppliedCount} applied and {SkippedCount} skipped areas to {OutputPath}",
            application.AppliedRedactionCount,
            application.SkippedRedactionCount,
            request.OutputPath);
        return new RedactedCopyResult(request.OutputPath, application);
    }

    private static RedactedCopySafetyArea ToSafetyArea(RedactionAreaTransaction redaction) =>
        new(redaction.PageNumber, redaction.PageArea, redaction.PreviewText);
}

internal readonly record struct RedactionMarkRequest(
    string? SourcePath,
    int PageIndex,
    PdfPageRect PageArea);

internal sealed record RedactionMarkResult(PdfPageRect PageArea, string PreviewText);

internal sealed record RedactionAreaTransaction(
    int PageNumber,
    PdfPageRect PageArea,
    string PreviewText)
{
    public static RedactionAreaTransaction FromPending(PendingRedaction pending)
    {
        ArgumentNullException.ThrowIfNull(pending);
        return new(pending.PageNumber, pending.PageArea, pending.PreviewText);
    }
}

internal sealed record RedactionApplicationRequest(
    PdfDocument Document,
    IReadOnlyList<RedactionAreaTransaction> Redactions,
    IReadOnlyList<PdfTypewriterTextOperation> TypewriterOperations)
{
    public static RedactionApplicationRequest Capture(
        PdfDocument document,
        IEnumerable<PendingRedaction> pendingRedactions,
        IEnumerable<PdfTypewriterTextOperation> typewriterOperations)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(pendingRedactions);
        ArgumentNullException.ThrowIfNull(typewriterOperations);
        return new(
            document,
            pendingRedactions.Select(RedactionAreaTransaction.FromPending).ToArray(),
            typewriterOperations.ToArray());
    }
}

internal sealed record RedactionApplicationResult(
    int AppliedRedactionCount,
    int SkippedRedactionCount,
    int AppliedTypewriterOperationCount,
    RedactedCopySafetyReport SafetyReport);

internal sealed record RedactedCopyRequest(
    RedactionApplicationRequest Application,
    string OutputPath,
    PdfEncryptionOptions? EncryptionOptions)
{
    public static RedactedCopyRequest Capture(
        PdfDocument document,
        IEnumerable<PendingRedaction> pendingRedactions,
        IEnumerable<PdfTypewriterTextOperation> typewriterOperations,
        string outputPath,
        PdfEncryptionOptions? encryptionOptions) =>
        new(
            RedactionApplicationRequest.Capture(
                document,
                pendingRedactions,
                typewriterOperations),
            outputPath,
            encryptionOptions);
}

internal sealed record RedactedCopyResult(
    string OutputPath,
    RedactionApplicationResult Application);
