using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using Excise.App.Models;
using Excise.Core.Document;

namespace Excise.App.ViewModels;

/// <summary>
/// #1414 — attachment disclosure, save, and stripping.
/// </summary>
/// <remarks>
/// <para>
/// <c>Excise.Core</c> could already read embedded files
/// (<c>GetEmbeddedFiles()</c>, including raw <c>.Bytes</c>) and remove them
/// (<c>ScrubEmbeddedFiles()</c>), both tested. The GUI had no surface for any
/// of it: a repo-wide grep for <c>EmbeddedFile</c>/<c>Attachment</c> across
/// <c>Excise.App/ViewModels</c> and <c>Excise.App/Views</c> found nothing but
/// an unrelated comment. A user could not see, save, or strip an attachment.
/// </para>
/// <para>
/// Why this matters beyond convenience: an attachment can carry the very data
/// the visible page was redacted of (ZUGFeRD/Factur-X invoices embed a full XML
/// copy of the document). Attachments are invisible in the page view, so a user
/// who cannot list them cannot know they are shipping them.
/// </para>
/// </remarks>
public partial class MainWindowViewModel
{
    /// <summary>Embedded files in the open document, refreshed on open.</summary>
    public ObservableCollection<AttachmentEntry> Attachments { get; } = new();

    /// <summary>True when the open document carries at least one embedded file.</summary>
    public bool HasAttachments => Attachments.Count > 0;

    /// <summary>Status line for the attachments dialog.</summary>
    public string AttachmentsSummary => Attachments.Count switch
    {
        0 => "This document has no attachments.",
        1 => "This document carries 1 attachment.",
        _ => $"This document carries {Attachments.Count} attachments.",
    };

    private AttachmentEntry? _selectedAttachment;

    /// <summary>The row the user has selected in the attachments list.</summary>
    public AttachmentEntry? SelectedAttachment
    {
        get => _selectedAttachment;
        set => this.RaiseAndSetIfChanged(ref _selectedAttachment, value);
    }

    /// <summary>
    /// Test seam for the "where do I save this attachment?" picker, mirroring
    /// <see cref="PickSavePdfPathOverride"/>. Headless has no storage provider,
    /// so without this the save path is unreachable from a test.
    /// </summary>
    internal Func<string, Task<string?>>? PickAttachmentSavePathOverride { get; set; }

    /// <summary>
    /// Re-read the document's embedded files into <see cref="Attachments"/>.
    /// </summary>
    internal void RefreshAttachments()
    {
        Attachments.Clear();

        var document = _documentService.GetCurrentDocument();
        if (document != null)
        {
            try
            {
                foreach (var file in document.GetEmbeddedFiles())
                {
                    Attachments.Add(new AttachmentEntry(
                        Name: file.Name,
                        FileName: string.IsNullOrWhiteSpace(file.FileName) ? file.Name : file.FileName!,
                        Description: file.Description,
                        MimeType: file.MimeType,
                        // Derived from the DECODED bytes, not from /Params /Size:
                        // a producer's declared size is a claim, not evidence.
                        SizeInBytes: file.Bytes?.Length));
                }
            }
            catch (Exception ex)
            {
                // A malformed name tree must not stop the document opening.
                _logger.LogError(ex, "Failed to read embedded files");
            }
        }

        this.RaisePropertyChanged(nameof(HasAttachments));
        this.RaisePropertyChanged(nameof(AttachmentsSummary));
    }

    /// <summary>
    /// Save one attachment to disk. This is the ONLY way excise writes an
    /// attachment out, and it always requires an explicit user action and an
    /// explicit destination — the capability's own policy is "permit explicit
    /// save to disk only".
    /// </summary>
    /// <remarks>
    /// excise never opens or executes an attachment. An embedded file is
    /// arbitrary attacker-controlled content; handing it to the OS to "open
    /// with" on the user's behalf is the entire attack that made PDF
    /// attachments notorious.
    /// </remarks>
    public async Task<bool> SaveAttachmentAsync(AttachmentEntry entry, string outputPath)
    {
        var document = _documentService.GetCurrentDocument();
        if (document == null)
            return false;

        var file = document.GetEmbeddedFiles()
            .FirstOrDefault(f => string.Equals(f.Name, entry.Name, StringComparison.Ordinal));

        if (file?.Bytes == null)
        {
            _logger.LogWarning("Attachment {Name} has no decodable bytes; nothing saved", entry.Name);
            _toastService.ShowError("Could not save attachment", $"{entry.FileName} could not be decoded.");
            return false;
        }

        try
        {
            await File.WriteAllBytesAsync(outputPath, file.Bytes);
            _logger.LogInformation("Saved attachment {Name} to {Path}", entry.Name, outputPath);
            _toastService.ShowSuccess($"Saved {Path.GetFileName(outputPath)}");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save attachment {Name} to {Path}", entry.Name, outputPath);
            _toastService.ShowError("Could not save attachment", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Prompt for a destination and save the selected attachment there.
    /// </summary>
    public async Task SaveSelectedAttachmentAsync()
    {
        var entry = SelectedAttachment;
        if (entry == null)
            return;

        var path = PickAttachmentSavePathOverride != null
            ? await PickAttachmentSavePathOverride(entry.FileName)
            : await PickAttachmentSavePathAsync(entry.FileName);

        if (string.IsNullOrWhiteSpace(path))
            return;

        await SaveAttachmentAsync(entry, path);
    }

    /// <summary>
    /// Test seam: set to open no window, so the command itself stays drivable
    /// from a headless test (dialogs need a real owner window).
    /// </summary>
    internal Func<Task>? ShowAttachmentsDialogOverride { get; set; }

    /// <summary>
    /// Document ▸ Attachments…
    /// </summary>
    private async Task ShowAttachmentsDialogAsync()
    {
        if (!_documentService.IsDocumentLoaded)
        {
            await _dialogService.ShowMessageAsync("Attachments", "Open a PDF to review its attachments.");
            return;
        }

        // Re-read rather than trusting the list captured at open: a strip, a
        // save-as or a reload may have changed it since.
        RefreshAttachments();

        if (ShowAttachmentsDialogOverride != null)
        {
            await ShowAttachmentsDialogOverride();
            return;
        }

        var owner = GetMainWindow();
        if (owner == null)
        {
            _logger.LogWarning("Could not get main window for the Attachments dialog");
            return;
        }

        var window = new Views.AttachmentsDialog { DataContext = this };
        await window.ShowDialog(owner);
    }

    /// <summary>
    /// The real "save attachment as" picker. Unlike
    /// <c>PickSavePdfPathAsync</c> this must NOT force a .pdf extension —
    /// an attachment is arbitrary content (XML, images, spreadsheets), and
    /// renaming a ZUGFeRD invoice to .pdf on the way out would be a
    /// corruption we invented.
    /// </summary>
    private async Task<string?> PickAttachmentSavePathAsync(string suggestedName)
    {
        var storageProvider = GetStorageProvider();
        if (storageProvider == null)
        {
            _logger.LogWarning("Storage provider unavailable, cannot show save-attachment dialog");
            return null;
        }

        var file = await global::Excise.App.Services.StoragePickers.SaveFileAsync(
            storageProvider,
            new global::Avalonia.Platform.Storage.FilePickerSaveOptions
            {
                Title = "Save Attachment",
                SuggestedFileName = suggestedName,
            },
            _logger);

        var path = file?.Path.LocalPath;
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    /// <summary>
    /// Remove every embedded file from the in-memory document.
    /// </summary>
    /// <remarks>
    /// Deliberately does NOT write the file. The removal becomes a pending edit
    /// like any other, so it goes out through the ordinary save routing — which
    /// means an ORIGINAL source is preserved and the stripped document is
    /// written as a copy (#1233). Stripping attachments in place on someone's
    /// only copy would be destroying data to protect it.
    /// </remarks>
    public void StripAllAttachments()
    {
        var document = _documentService.GetCurrentDocument();
        if (document == null || Attachments.Count == 0)
            return;

        var removed = Attachments.Count;
        document.ScrubEmbeddedFiles();

        // Count it as a page edit so HasUnsavedChanges is true and the save
        // routing treats the document as modified.
        FileState.PageEditsCount++;

        RefreshAttachments();
        this.RaisePropertyChanged(nameof(SaveButtonText));
        this.RaisePropertyChanged(nameof(StatusBarText));

        _logger.LogInformation("Stripped {Count} embedded file(s); save to persist", removed);
        _toastService.ShowSuccess(
            removed == 1
                ? "1 attachment removed — save to persist"
                : $"{removed} attachments removed — save to persist");
    }
}
