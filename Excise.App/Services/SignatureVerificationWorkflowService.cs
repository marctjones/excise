using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace Excise.App.Services;

public sealed class SignatureVerificationWorkflowService
{
    private readonly SignatureVerificationService _signatureService;
    private readonly SignatureVerificationSummaryFormatter _summaryFormatter;
    private readonly IUserDialogService _dialogService;
    private readonly ILogger<SignatureVerificationWorkflowService> _logger;

    public SignatureVerificationWorkflowService(
        SignatureVerificationService signatureService,
        SignatureVerificationSummaryFormatter summaryFormatter,
        IUserDialogService dialogService,
        ILogger<SignatureVerificationWorkflowService> logger)
    {
        ArgumentNullException.ThrowIfNull(signatureService);
        ArgumentNullException.ThrowIfNull(summaryFormatter);
        ArgumentNullException.ThrowIfNull(dialogService);
        ArgumentNullException.ThrowIfNull(logger);

        _signatureService = signatureService;
        _summaryFormatter = summaryFormatter;
        _dialogService = dialogService;
        _logger = logger;
    }

    public async Task VerifyAsync(bool isDocumentLoaded, string? currentFilePath)
    {
        if (!isDocumentLoaded || string.IsNullOrEmpty(currentFilePath))
        {
            _logger.LogWarning("No document loaded for signature verification");
            await _dialogService.ShowMessageAsync("Verify Signatures", "Open a PDF before verifying signatures.");
            return;
        }

        try
        {
            _logger.LogInformation("Verifying digital signatures");

            var results = _signatureService.VerifySignatures(currentFilePath);
            if (results.Count == 0)
            {
                _logger.LogInformation("No digital signatures found in document");
                await _dialogService.ShowMessageAsync("Verify Signatures", "No digital signatures were found in this document.");
                return;
            }

            _logger.LogInformation("Found {SignatureCount} signatures", results.Count);
            await _dialogService.ShowMessageAsync("Verify Signatures", _summaryFormatter.Format(results));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error verifying signatures");
            await _dialogService.ShowMessageAsync("Verify Signatures", $"Signature verification failed: {ex.Message}");
        }
    }
}
