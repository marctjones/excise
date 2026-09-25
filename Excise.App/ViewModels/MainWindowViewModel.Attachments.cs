using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using Excise.App.Models;
using Excise.App.Services;
using Excise.Core.Document;
using Excise.Core.Security;

namespace Excise.App.ViewModels;

/// <summary>
/// #1414 / #1563 — the Attachments pane: disclosure, save, and stripping of
/// embedded files.
/// </summary>
/// <remarks>
/// <para>
/// An attachment can carry the very data the visible page was redacted of
/// (ZUGFeRD/Factur-X invoices embed a full XML copy of the document), and it is
/// invisible in the page view. #1414 made attachments reachable through a
/// dialog; #1563 puts them in a sidebar pane that is visible by default, so a
/// user sees them without looking for them.
/// </para>
/// <para>
/// excise never opens or runs an attachment. An embedded file is arbitrary
/// content chosen by whoever made the PDF; handing it to the operating system
/// to "open with" is the attack that made PDF attachments notorious. Saving to
/// a location the user picks is the only way an attachment leaves excise.
/// </para>
/// </remarks>
public partial class MainWindowViewModel
{
    /// <summary>Embedded files in the open document, refreshed on open.</summary>
    public ObservableCollection<AttachmentEntry> Attachments { get; } = new();

    /// <summary>True when the open document carries at least one embedded file.</summary>
    public bool HasAttachments => Attachments.Count > 0;

    /// <summary>One-line summary of what the document carries.</summary>
    public string AttachmentsSummary => Attachments.Count switch
    {
        0 => "This document has no attachments.",
        1 => "This document carries 1 attachment.",
        _ => $"This document carries {Attachments.Count} attachments.",
    };

    /// <summary>
    /// The pane's empty state. The pane stays visible when there is nothing to
    /// list (#1563), so it says why it is empty rather than going blank.
    /// </summary>
    public string AttachmentsEmptyText => IsDocumentLoaded ? "No attachments" : "No document open";

    /// <summary>Count shown beside the pane title; empty when there are none.</summary>
    public string AttachmentsCountText => Attachments.Count == 0 ? string.Empty : Attachments.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private bool _isAttachmentsSidebarVisible = true;

    /// <summary>
    /// Whether the Attachments pane is shown. On by default (#1563); the user's
    /// choice is persisted in <c>window.json</c> by the main window.
    /// </summary>
    public bool IsAttachmentsSidebarVisible
    {
        get => _isAttachmentsSidebarVisible;
        set
        {
            this.RaiseAndSetIfChanged(ref _isAttachmentsSidebarVisible, value);
            this.RaisePropertyChanged(nameof(IsRightSidebarVisible));
            // The banner says either "See the Attachments pane" or how to show
            // it; hiding the pane while it is up must not leave it lying.
            this.RaisePropertyChanged(nameof(AttachmentsNoticeMessage));
        }
    }

    /// <summary>View ▸ Show Attachments.</summary>
    public void ToggleAttachmentsSidebar() =>
        IsAttachmentsSidebarVisible = !IsAttachmentsSidebarVisible;

    /// <summary>Restore the persisted pane visibility (called by the main window at startup).</summary>
    internal void ApplyAttachmentsPanePreference(bool visible) =>
        IsAttachmentsSidebarVisible = visible;

    /// <summary>
    /// Raised when Document ▸ Attachments asks for the pane to take keyboard
    /// focus. Focus is view mechanics, so the view subscribes and moves it.
    /// </summary>
    public event EventHandler? AttachmentsPaneFocusRequested;

    private AttachmentEntry? _selectedAttachment;

    /// <summary>
    /// The row the user has selected. Selecting an attachment that belongs to a
    /// page annotation navigates to that page, the same way selecting an outline
    /// entry does.
    /// </summary>
    public AttachmentEntry? SelectedAttachment
    {
        get => _selectedAttachment;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedAttachment, value);
            if (value?.PageNumber is int page && page >= 1 && page <= TotalPages)
                CurrentPageIndex = page - 1;
        }
    }

    /// <summary>
    /// Test seam for the "where do I save this attachment?" picker, mirroring
    /// <see cref="PickSavePdfPathOverride"/>. Headless has no storage provider,
    /// so without this the save path is unreachable from a test.
    /// </summary>
    internal Func<string, Task<string?>>? PickAttachmentSavePathOverride { get; set; }

    /// <summary>
    /// Re-read the document's embedded files into <see cref="Attachments"/>.
    /// With no document loaded this empties the list.
    /// </summary>
    internal void RefreshAttachments()
    {
        Attachments.Clear();
        SelectedAttachment = null;

        var document = _documentService.GetCurrentDocument();
        if (document != null)
        {
            try
            {
                foreach (var file in document.GetEmbeddedFiles())
                    Attachments.Add(ToEntry(file));
            }
            catch (Exception ex)
            {
                // A malformed name tree must not stop the document opening.
                _logger.LogError(ex, "Failed to read embedded files");
            }
        }

        RaiseAttachmentsChanged();
    }

    /// <summary>
    /// Empty the list without reading the document. The open path uses this
    /// before the new document is current: <see cref="RefreshAttachments"/>
    /// there would re-list the document being replaced.
    /// </summary>
    private void ClearAttachments()
    {
        Attachments.Clear();
        SelectedAttachment = null;
        RaiseAttachmentsChanged();
    }

    private void RaiseAttachmentsChanged()
    {
        this.RaisePropertyChanged(nameof(HasAttachments));
        this.RaisePropertyChanged(nameof(AttachmentsSummary));
        this.RaisePropertyChanged(nameof(AttachmentsEmptyText));
        this.RaisePropertyChanged(nameof(AttachmentsCountText));
        this.RaisePropertyChanged(nameof(AttachmentsNoticeMessage));
    }

    private static AttachmentEntry ToEntry(PdfEmbeddedFile file) => new(
        Name: file.Name,
        FileName: string.IsNullOrWhiteSpace(file.FileName) ? file.Name : file.FileName!,
        Description: file.Description,
        MimeType: file.MimeType,
        // Derived from the DECODED bytes, not from /Params /Size:
        // a producer's declared size is a claim, not evidence.
        SizeInBytes: file.Bytes?.Length,
        ModifiedDate: file.ModDate,
        PageNumber: file.PageNumber);

    /// <summary>
    /// The embedded file a row stands for. Matched by position first, because
    /// a name tree may repeat a key, then by name.
    /// </summary>
    private PdfEmbeddedFile? ResolveAttachment(PdfDocument document, AttachmentEntry entry)
    {
        var files = document.GetEmbeddedFiles();
        var index = Attachments.IndexOf(entry);
        if (index >= 0 && index < files.Count && string.Equals(files[index].Name, entry.Name, StringComparison.Ordinal))
            return files[index];
        return files.FirstOrDefault(f => string.Equals(f.Name, entry.Name, StringComparison.Ordinal));
    }

    /// <summary>/P bit 5: an attachment's bytes are document content leaving excise.</summary>
    private bool EnsureAttachmentExtractionPermitted(string actionDescription) =>
        EnsureDocumentPermission(DocumentAction.Extract, actionDescription);

    /// <summary>
    /// Save one attachment to <paramref name="outputPath"/>. Writes the decoded
    /// bytes exactly; never opens or runs them.
    /// </summary>
    public async Task<bool> SaveAttachmentAsync(AttachmentEntry entry, string outputPath)
    {
        var document = _documentService.GetCurrentDocument();
        if (document == null)
            return false;

        if (!EnsureAttachmentExtractionPermitted("Saving an attachment"))
            return false;

        var file = ResolveAttachment(document, entry);
        if (file?.Bytes == null)
        {
            _logger.LogWarning("Attachment {Name} has no decodable bytes; nothing saved", entry.Name);
            _toastService.ShowError("Could not save attachment", $"{entry.DisplayName} could not be decoded.");
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
    /// Save Attachment…: prompt for a destination and save the selected row.
    /// The permission check runs before the picker, so a refused save does not
    /// first ask where to put the file.
    /// </summary>
    public async Task SaveSelectedAttachmentAsync()
    {
        var entry = SelectedAttachment;
        if (entry == null || _documentService.GetCurrentDocument() == null)
            return;

        if (!EnsureAttachmentExtractionPermitted("Saving an attachment"))
            return;

        var suggested = AttachmentFileNames.ToSafeFileName(entry.FileName, Attachments.IndexOf(entry) + 1);
        var path = PickAttachmentSavePathOverride != null
            ? await PickAttachmentSavePathOverride(suggested)
            : await PickAttachmentSavePathAsync(suggested);

        if (string.IsNullOrWhiteSpace(path))
            return;

        await SaveAttachmentAsync(entry, path);
    }

    /// <summary>
    /// Save All Attachments…: write every attachment into a folder the user
    /// picks. Names come from <see cref="AttachmentFileNames"/> (no path
    /// traversal, no hidden or reserved names) and never overwrite: a clash
    /// becomes <c>name (2).ext</c>.
    /// </summary>
    /// <returns>How many files were written.</returns>
    public async Task<int> SaveAllAttachmentsAsync()
    {
        var document = _documentService.GetCurrentDocument();
        if (document == null || Attachments.Count == 0)
            return 0;

        if (!EnsureAttachmentExtractionPermitted("Saving attachments"))
            return 0;

        var folder = await PickFolderAsync("Save All Attachments");
        if (string.IsNullOrWhiteSpace(folder))
            return 0;

        var files = document.GetEmbeddedFiles();
        var saved = 0;
        var failed = new List<string>();
        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            var displayName = Excise.Core.Text.UnicodeTextSafety.EscapeForDisplay(file.FileName ?? file.Name);
            if (file.Bytes == null)
            {
                failed.Add(displayName);
                continue;
            }

            try
            {
                var target = AttachmentFileNames.UniquePathIn(
                    folder, AttachmentFileNames.ToSafeFileName(file.FileName ?? file.Name, i + 1));
                // CreateNew: if something appeared at this path since the
                // uniqueness check, fail rather than overwrite it.
                await using (var stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await stream.WriteAsync(file.Bytes);
                saved++;
                _logger.LogInformation("Saved attachment {Name} to {Path}", file.Name, target);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save attachment {Name} into {Folder}", file.Name, folder);
                failed.Add(displayName);
            }
        }

        if (failed.Count == 0)
            _toastService.ShowSuccess(saved == 1 ? "Saved 1 attachment" : $"Saved {saved} attachments");
        else
            _toastService.ShowError(
                "Some attachments were not saved",
                $"Saved {saved} of {files.Count}. Not saved: {string.Join(", ", failed)}.");
        return saved;
    }

    /// <summary>
    /// Test seam kept from the dialog era: when set, Document ▸ Attachments calls
    /// it after revealing the pane, so a headless test can observe the command.
    /// </summary>
    internal Func<Task>? ShowAttachmentsPaneOverride { get; set; }

    /// <summary>
    /// Document ▸ Attachments: re-read the list, show the pane, and ask the view
    /// to focus it.
    /// </summary>
    private async Task ShowAttachmentsPaneAsync()
    {
        if (!_documentService.IsDocumentLoaded)
        {
            await _dialogService.ShowMessageAsync("Attachments", "Open a PDF to review its attachments.");
            return;
        }

        // Re-read rather than trusting the list captured at open: a strip, a
        // save-as or a reload may have changed it since.
        RefreshAttachments();
        IsAttachmentsSidebarVisible = true;
        AttachmentsPaneFocusRequested?.Invoke(this, EventArgs.Empty);

        if (ShowAttachmentsPaneOverride != null)
            await ShowAttachmentsPaneOverride();
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
        // No DefaultExtension and no filter, deliberately: see the summary above.
        return await _filePicker.SaveFileAsync(new global::Excise.App.Services.Host.SaveFileRequest
        {
            Title = "Save Attachment",
            SuggestedFileName = suggestedName,
        });
    }

    /// <summary>
    /// Remove All Attachments: strip every embedded file from the in-memory
    /// document, as an undoable pending edit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately does NOT write the file. The removal becomes a pending edit
    /// like any other, so it goes out through the ordinary save routing — which
    /// means an ORIGINAL source is preserved and the stripped document is
    /// written as a copy (#1233). Stripping attachments in place on someone's
    /// only copy would be destroying data to protect it.
    /// </para>
    /// <para>
    /// Since #1572 the strip covers every route an attachment takes — the
    /// document-level tree and <c>/AF</c> arrays AND page
    /// <c>/FileAttachment</c> annotations (plus embedded media) — so the count
    /// is what the strip itself reports removing, which can exceed the rows the
    /// pane lists. The list is still re-read afterwards, and anything left
    /// there is reported rather than hidden.
    /// </para>
    /// </remarks>
    public void StripAllAttachments()
    {
        var document = _documentService.GetCurrentDocument();
        if (document == null || Attachments.Count == 0)
            return;

        var (removedFiles, restore) = document.ScrubEmbeddedFilesReversibly();
        RefreshAttachments();
        var removed = removedFiles.Count;

        if (removed <= 0)
        {
            // The strip found nothing to remove: leave the document clean and say so.
            restore();
            RefreshAttachments();
            _toastService.ShowWarning(
                "No attachments removed",
                "excise found no embedded file it could remove.");
            return;
        }

        // Count it as a page edit so HasUnsavedChanges is true and the save
        // routing treats the document as modified.
        FileState.PageEditsCount++;
        RaiseStripBookkeeping();

        var description = removed == 1 ? "Remove attachment" : "Remove attachments";
        _history.Push(
            description,
            undo: () =>
            {
                restore();
                FileState.PageEditsCount = Math.Max(0, FileState.PageEditsCount - 1);
                RefreshAttachments();
                RaiseStripBookkeeping();
            },
            redo: () =>
            {
                (_, restore) = document.ScrubEmbeddedFilesReversibly();
                FileState.PageEditsCount++;
                RefreshAttachments();
                RaiseStripBookkeeping();
            });

        _logger.LogInformation("Stripped {Count} embedded file(s); save to persist", removed);
        var message = removed == 1
            ? "1 attachment removed — save to persist"
            : $"{removed} attachments removed — save to persist";
        if (Attachments.Count > 0)
        {
            // Not expected since #1572; if it happens the user must know.
            _toastService.ShowWarning(
                message,
                Attachments.Count == 1
                    ? "1 attachment could not be removed."
                    : $"{Attachments.Count} attachments could not be removed.");
        }
        else
        {
            _toastService.ShowSuccess(message);
        }
    }

    private void RaiseStripBookkeeping()
    {
        this.RaisePropertyChanged(nameof(SaveButtonText));
        this.RaisePropertyChanged(nameof(StatusBarText));
    }

    /// <summary>Title of the attachments banner.</summary>
    internal const string AttachmentsNoticeTitleText = "Document has attachments";

    /// <summary>Title of the attachments banner, for the view to bind to.</summary>
    public string AttachmentsNoticeTitle => AttachmentsNoticeTitleText;

    private bool _isAttachmentsNoticeOpen;

    /// <summary>
    /// Whether the attachments banner is showing. The banner's close button
    /// writes false back through the two-way binding.
    /// </summary>
    public bool IsAttachmentsNoticeOpen
    {
        get => _isAttachmentsNoticeOpen;
        set => this.RaiseAndSetIfChanged(ref _isAttachmentsNoticeOpen, value);
    }

    /// <summary>What the document carries, and where to look at it.</summary>
    public string AttachmentsNoticeMessage
    {
        get
        {
            if (!HasAttachments)
                return string.Empty;

            var what = Attachments.Count == 1
                ? "1 embedded file travels with this PDF."
                : $"{Attachments.Count} embedded files travel with this PDF.";
            var where = IsAttachmentsSidebarVisible
                ? " See the Attachments pane."
                : " Show them with View ▸ Show Attachments.";
            return what + where;
        }
    }

    /// <summary>
    /// The banner shown when an opened document carries attachments (#1414's
    /// "warn when their presence is not otherwise obvious").
    /// </summary>
    /// <remarks>
    /// A PERSISTENT, closable banner, not a toast (#1619), modelled on the XFA
    /// notice beside it. Two reasons, one measured and one about the warning
    /// itself:
    /// <list type="bullet">
    /// <item>The notices row sits above the document, so a toast's 5 s
    /// auto-dismiss re-laid out the window and shifted the page under the
    /// reader five seconds after it opened. Measured on
    /// irs-1040-instructions (the one reader-bench document with an embedded
    /// file): the #1544 launch-drawn tail was that dismissal and nothing else —
    /// the process used 0.0% of a core from 3.0 s until the timer fired at
    /// STEP 13 + 5.002 s.</item>
    /// <item>What it warns about — a carrier the page view cannot show, which
    /// can hold a full copy of the data the page was redacted of — does not
    /// stop being true after five seconds. It now stays until the reader
    /// dismisses it or the document changes, exactly like the XFA notice.</item>
    /// </list>
    /// </remarks>
    private void ShowAttachmentsNoticeOnOpen()
    {
        if (!HasAttachments)
            return;

        this.RaisePropertyChanged(nameof(AttachmentsNoticeMessage));
        IsAttachmentsNoticeOpen = true;
    }

    /// <summary>Hide the banner; the document it described is gone or replaced.</summary>
    private void ClearAttachmentsNotice() => IsAttachmentsNoticeOpen = false;
}
