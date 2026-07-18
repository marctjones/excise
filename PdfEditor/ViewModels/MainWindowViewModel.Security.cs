using Avalonia.Platform.Storage;
using Microsoft.Extensions.Logging;
using Pdfe.Core.Security;
using Pdfe.Core.Writing;
using ReactiveUI;
using System;
using System.IO;
using System.Threading.Tasks;

namespace PdfEditor.ViewModels;

/// <summary>
/// "Document &gt; Security..." GUI wiring (#641): set, change, or remove
/// password protection on the currently-open document, on top of the
/// already-landed encryption writer (<see cref="PdfEncryptionOptions"/> +
/// <see cref="PdfDocumentWriter"/> — #639/#640). Mirrors
/// <c>MainWindowViewModel.Searchable.cs</c>'s shape: this partial is only
/// the View → ViewModel → engine orchestration.
///
/// Deliberately writes to a NEW file the user picks (Save-As semantics),
/// rather than mutating the live in-memory document and deferring to the
/// app's normal Save command. Since #643 the normal save path *preserves*
/// the source's existing protection (<see cref="PdfDocumentService.SaveDocument"/>
/// re-encrypts via <see cref="PdfDocumentService.GetReEncryptionOptions"/>);
/// this dialog remains the one place that *changes* it — sets, replaces, or
/// removes protection — and Save-As keeps that intent separate from the
/// shared save path. It mirrors the established pattern for operations that
/// produce a new PDF from the current in-memory state (see
/// <c>CombineDocumentsAsync</c>/<c>SplitDocumentAsync</c>), needs no
/// live-document swap, and needs no "reload with the new password" step —
/// see <see cref="ApplySecurity"/>/<see cref="RemoveProtection"/>, which
/// write straight from the already-open (already-decrypted)
/// <see cref="Pdfe.Core.Document.PdfDocument"/> obtained via
/// <see cref="PdfDocumentService.GetCurrentDocument"/>.
/// </summary>
public partial class MainWindowViewModel
{
    private async Task ShowSecurityDialogAsync()
    {
        if (!_documentService.IsDocumentLoaded)
        {
            _logger.LogWarning("Document Security requested with no document loaded");
            await _dialogService.ShowMessageAsync("Document Security", "Open a PDF before setting a password.");
            return;
        }

        var owner = GetMainWindow();
        if (owner == null)
        {
            _logger.LogWarning("Could not get main window for Document Security dialog");
            return;
        }

        var isEncrypted = _documentService.IsEncrypted;
        var dialogViewModel = new SecurityDialogViewModel(
            isEncrypted,
            verifyCurrentPassword: _documentService.VerifyPassword,
            applyAsync: ApplySecurityWithPickerAsync,
            removeProtectionAsync: RemoveProtectionWithPickerAsync);
        dialogViewModel.Completed += (_, path) =>
            _toastService.ShowSuccess("Document Security", $"Saved to {Path.GetFileName(path)}");

        var window = new Views.SecurityDialog
        {
            DataContext = dialogViewModel,
        };

        await window.ShowDialog(owner);
    }

    /// <summary>
    /// Prompts for a save target, then applies password protection there.
    /// Returns the output path, or null if the user cancelled the picker.
    /// </summary>
    private async Task<string?> ApplySecurityWithPickerAsync(
        string? userPassword, string? ownerPassword, PdfEncryptionAlgorithm algorithm)
    {
        var storageProvider = GetStorageProvider();
        if (storageProvider == null)
        {
            _logger.LogWarning("Storage provider unavailable, cannot show Save Protected PDF dialog");
            return null;
        }

        var file = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Protected PDF As",
            DefaultExtension = "pdf",
            SuggestedFileName = SuggestSecuredFileName(),
            FileTypeChoices = new[]
            {
                new FilePickerFileType("PDF Files") { Patterns = new[] { "*.pdf" } }
            }
        });

        if (file == null)
            return null;

        var outputPath = file.Path.LocalPath;
        if (string.IsNullOrWhiteSpace(outputPath))
            return null;

        ApplySecurity(outputPath, userPassword, ownerPassword, algorithm);
        return outputPath;
    }

    /// <summary>
    /// Confirms the (consequential, irreversible-in-the-output-file) intent
    /// to drop protection, prompts for a save target, then writes an
    /// unprotected copy. Returns the output path, or null if the user
    /// declined the confirmation or cancelled the picker.
    /// </summary>
    private async Task<string?> RemoveProtectionWithPickerAsync()
    {
        var confirmed = await _dialogService.ShowConfirmAsync(
            "Remove Password Protection",
            "This saves an unprotected copy of the document. Anyone with the file will be able to open it " +
            "without a password. Continue?");
        if (!confirmed)
            return null;

        var storageProvider = GetStorageProvider();
        if (storageProvider == null)
        {
            _logger.LogWarning("Storage provider unavailable, cannot show Save PDF dialog");
            return null;
        }

        var file = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Unprotected PDF As",
            DefaultExtension = "pdf",
            SuggestedFileName = SuggestSecuredFileName(),
            FileTypeChoices = new[]
            {
                new FilePickerFileType("PDF Files") { Patterns = new[] { "*.pdf" } }
            }
        });

        if (file == null)
            return null;

        var outputPath = file.Path.LocalPath;
        if (string.IsNullOrWhiteSpace(outputPath))
            return null;

        RemoveProtection(outputPath);
        return outputPath;
    }

    private string SuggestSecuredFileName()
    {
        var baseName = string.IsNullOrWhiteSpace(DocumentName) ? "document" : Path.GetFileNameWithoutExtension(DocumentName);
        return $"{baseName}.pdf";
    }

    /// <summary>
    /// Writes the currently-open document to <paramref name="outputPath"/>
    /// encrypted with the given passwords/algorithm — including when both
    /// passwords are empty, which is a valid "encrypted, no open prompt"
    /// configuration and must NOT be treated as "remove protection".
    /// Internal (not private) so tests can drive this directly, mirroring
    /// <c>RunMakeSearchableAsync</c>'s visibility.
    /// </summary>
    internal void ApplySecurity(string outputPath, string? userPassword, string? ownerPassword, PdfEncryptionAlgorithm algorithm)
    {
        var document = _documentService.GetCurrentDocument()
            ?? throw new InvalidOperationException("No document loaded.");

        var options = new PdfEncryptionOptions
        {
            UserPassword = userPassword,
            OwnerPassword = ownerPassword,
            Algorithm = algorithm,
        };

        document.Save(outputPath, options);
        _logger.LogInformation("Document Security: wrote protected copy to {OutputPath}", outputPath);
    }

    /// <summary>
    /// Writes the currently-open document to <paramref name="outputPath"/>
    /// with no encryption at all — the only path in this feature that
    /// drops protection. Internal so tests can drive this directly.
    /// </summary>
    internal void RemoveProtection(string outputPath)
    {
        var document = _documentService.GetCurrentDocument()
            ?? throw new InvalidOperationException("No document loaded.");

        document.Save(outputPath, encryptionOptions: null);
        _logger.LogInformation("Document Security: wrote unprotected copy to {OutputPath}", outputPath);
    }
}
