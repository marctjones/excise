using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.Logging;
using Excise.App.Services;

namespace Excise.App.ViewModels;

/// <summary>
/// #1002 — drag-and-drop a PDF onto the window to open it.
/// </summary>
/// <remarks>
/// This was not an untested workflow, it was a missing one: there were zero
/// references to <c>DragEventArgs</c>, <c>DragDrop</c> or <c>AllowDrop</c>
/// anywhere in <c>Excise.App</c> or <c>Excise.Avalonia</c>, so nothing would
/// have happened had a user dropped a file on the window.
/// </remarks>
public partial class MainWindowViewModel
{
    /// <summary>
    /// Open the first openable PDF from an OS drag-and-drop payload.
    /// </summary>
    /// <returns>
    /// <c>true</c> when a document was opened. <c>false</c> when the drop
    /// carried no local, existing PDF, or when the user declined to abandon
    /// unsaved changes.
    /// </returns>
    /// <remarks>
    /// The decision (which file, whether to replace the open document, what to
    /// do about unsaved edits) is here so the drop handler in code-behind stays
    /// a two-line adapter from <c>DragEventArgs</c> to this call.
    /// </remarks>
    public async Task<bool> OpenDroppedFilesAsync(IReadOnlyList<IStorageItem> files)
    {
        var path = DroppedPdfResolver.ResolveFirstPdf(files);
        if (path == null)
        {
            // Silently ignoring a drop the user meant as an open is worse than
            // saying nothing happened, but a dialog for every stray drag would
            // be noise — a status line is the proportionate signal.
            _logger.LogInformation("Drop ignored: no local, existing .pdf among {Count} dropped item(s)", files.Count);
            OperationStatus = "Dropped item is not a PDF file.";
            return false;
        }

        // #1233: a drop REPLACES the open document, so it destroys unsaved
        // edits exactly as File ▸ Open does and must ask the same question.
        if (!await ConfirmDiscardUnsavedChangesAsync("open the dropped document"))
        {
            _logger.LogInformation("Drop of {Path} cancelled at the unsaved-changes prompt", path);
            return false;
        }

        _logger.LogInformation("Opening dropped PDF: {Path}", path);
        await LoadDocumentAsync(path);
        return true;
    }
}
