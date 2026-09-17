using System.Collections.Generic;
using System.Threading.Tasks;
using Excise.App.ViewModels;

namespace Excise.App.Workspace;

/// <summary>
/// What a document session's view model may ask of the application around it
/// (#1551). Implemented by <see cref="DocumentSession"/>, which forwards to the
/// <see cref="DocumentWorkspace"/> with itself as the requester.
/// </summary>
/// <remarks>
/// <see cref="MainWindowViewModel.SessionHost"/> is null for a view model
/// built on its own (every existing test, and the design-time graph). Every
/// caller then keeps the single-document behaviour it had before this
/// interface existed, so the interface is purely additive.
/// </remarks>
internal interface IDocumentSessionHost
{
    /// <summary>
    /// True when opening a document from this session would open it in
    /// another window or tab rather than replace this session's document.
    /// The caller then skips the unsaved-changes prompt, because nothing is
    /// discarded.
    /// </summary>
    bool OpensDocumentsElsewhere { get; }

    /// <summary>
    /// Open <paramref name="paths"/> by the workspace's routing rules
    /// (docs/architecture/main-window-architecture.md §7.4).
    /// <paramref name="replaceConfirmed"/> says the caller already asked the
    /// unsaved-changes question for this session.
    /// </summary>
    Task OpenDocumentsAsync(IReadOnlyList<string> paths, bool replaceConfirmed);

    /// <summary>
    /// Close this session's window or tab when another session exists. The
    /// caller has already run its unsaved-changes prompt. Returns false when
    /// this is the only session: the caller keeps today's close-document
    /// behaviour (the document closes, the empty window stays).
    /// </summary>
    bool TryCloseSession();

    /// <summary>Review every session's unsaved changes, then quit.</summary>
    Task RequestQuitAsync();

    /// <summary>
    /// Every open document, in opening order, for the Window menu (#1553).
    /// </summary>
    IReadOnlyList<OpenDocumentEntry> OpenDocuments { get; }

    /// <summary>Bring the document <paramref name="entry"/> describes to the front.</summary>
    void ActivateDocument(OpenDocumentEntry entry);

    /// <summary>
    /// Apply a saved Preferences dialog to every OTHER session. Preferences
    /// are app-wide; the redaction policies among them must never differ
    /// between two open windows.
    /// </summary>
    void ApplyPreferencesToOtherSessions(PreferencesViewModel preferences);
}

/// <summary>
/// One open document as the Window menu shows it (#1553).
/// </summary>
/// <param name="Title">The file name, or "Untitled" for an empty window.</param>
/// <param name="FilePath">The full path, or null for an empty window.</param>
/// <param name="IsCurrent">True for the session asking.</param>
/// <param name="HasUnsavedChanges">True when the document has unsaved edits.</param>
/// <param name="Key">Identifies the session to <see cref="IDocumentSessionHost.ActivateDocument"/>.</param>
internal sealed record OpenDocumentEntry(
    string Title,
    string? FilePath,
    bool IsCurrent,
    bool HasUnsavedChanges,
    object Key);
