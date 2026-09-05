using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Excise.App.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Excise.App.ViewModels;

/// <summary>
/// #1308 — the production entry point for applying a digital signature.
///
/// <para><see cref="SignatureApplicationService"/> and
/// <see cref="SigningCertificateFactory"/> existed and were tested, but nothing
/// outside the test project called them: deterministic reachability found them
/// reachable from Excise.App.Tests and from no production code. A capability
/// that only its own tests can reach is not shipped, it is shelved.</para>
///
/// <para><b>Signing is deliberately the last operation on a file.</b> excise
/// saves by full rewrite, and a full rewrite invalidates any signature already
/// present (the service guards this, and adding a second signature needs
/// incremental update — #623). So this signs the file ON DISK rather than the
/// in-memory document, and refuses while edits are unsaved: signing a stale
/// file would hand back a valid signature over the wrong bytes, which is worse
/// than refusing.</para>
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>
    /// Pick a PKCS#12 certificate, pick an output path, sign. The certificate
    /// password is requested through the same prompt an encrypted document
    /// uses, and is never logged or retained.
    /// </summary>
    public async Task SignDocumentAsync()
    {
        var document = _documentService.GetCurrentDocument();
        if (document == null)
        {
            _toastService.ShowWarning("Open a document before signing");
            return;
        }

        if (string.IsNullOrWhiteSpace(_currentFilePath) || !File.Exists(_currentFilePath))
        {
            _toastService.ShowWarning("Save the document before signing",
                "A signature covers the bytes on disk, so the file must exist first.");
            return;
        }

        if (FileState.HasUnsavedChanges)
        {
            _toastService.ShowWarning("Save your changes before signing",
                "A signature would cover the last saved file, not the edits on screen.");
            return;
        }

        var storageProvider = GetStorageProvider();
        if (storageProvider == null)
            return;

        var certFiles = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a signing certificate (PKCS#12)",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("PKCS#12 certificate") { Patterns = new[] { "*.p12", "*.pfx" } },
            },
        });

        if (certFiles is not { Count: > 0 } || certFiles[0].Path.LocalPath is not { Length: > 0 } certPath)
            return;

        // Cancel returns null; an empty string is a legitimate empty password.
        var password = await _dialogService.PromptPasswordAsync(
            "Certificate Password",
            $"Password for {Path.GetFileName(certPath)}");
        if (password == null)
            return;

        var outFile = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Signed Copy",
            DefaultExtension = "pdf",
            SuggestedFileName = SuggestSignedFilename(_currentFilePath),
            FileTypeChoices = new[]
            {
                new FilePickerFileType("PDF Files") { Patterns = new[] { "*.pdf" } },
            },
        });

        if (outFile?.Path.LocalPath is not { Length: > 0 } outputPath)
            return;

        await SignDocumentAsAsync(certPath, password, outputPath);
    }

    /// <summary>
    /// The headless half, so the workflow is reachable and testable without a
    /// file picker. Reports through the toast surface and never throws.
    /// </summary>
    public async Task SignDocumentAsAsync(string certificatePath, string? password, string outputPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(certificatePath);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        var inputPath = _currentFilePath;
        try
        {
            await Task.Run(() =>
            {
                using var certificate = SigningCertificateFactory.LoadFromPkcs12(certificatePath, password);
                var service = new SignatureApplicationService(
                    _signingLogger ??= NullLogger<SignatureApplicationService>.Instance);
                service.SignFile(inputPath, outputPath, certificate, new SignatureApplicationOptions
                {
                    Reason = "Approved in excise",
                    SigningTime = DateTimeOffset.Now,
                });
            });

            _logger.LogInformation("Signed {Input} to {Output}", inputPath, outputPath);
            _toastService.ShowSuccess("Document signed",
                $"Signed copy written to {Path.GetFileName(outputPath)}");
        }
        catch (Exception ex)
        {
            // The common failures are a wrong certificate password and a file
            // that already carries a signature (#623). Both are the user's to
            // act on, so they are surfaced rather than swallowed.
            _logger.LogWarning(ex, "Signing failed for {Input}", inputPath);
            _toastService.ShowError("Signing failed", ex.Message);
        }
    }

    private ILogger<SignatureApplicationService>? _signingLogger;

    internal static string SuggestSignedFilename(string currentFilePath)
    {
        if (string.IsNullOrWhiteSpace(currentFilePath))
            return "document_signed.pdf";

        var name = Path.GetFileNameWithoutExtension(currentFilePath);
        var extension = Path.GetExtension(currentFilePath);
        return $"{name}_signed{(string.IsNullOrEmpty(extension) ? ".pdf" : extension)}";
    }
}
