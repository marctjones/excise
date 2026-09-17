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
    /// Apply a saved Preferences dialog to every OTHER session. Preferences
    /// are app-wide; the redaction policies among them must never differ
    /// between two open windows.
    /// </summary>
    void ApplyPreferencesToOtherSessions(PreferencesViewModel preferences);
}
