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

    /// <summary>True when this session is one of several tabs in its window (#1554).</summary>
    bool CanMoveToNewWindow { get; }

    /// <summary>
    /// Pick documents and open them as tabs of THIS session's window (#1628),
    /// whatever the open-mode preference says.
    /// </summary>
    /// <remarks>
    /// Lives here rather than on <c>DocumentTabsViewModel</c> because the tab
    /// strip's "+" button has to have a command even in a window that has no
    /// tabs view model yet — a leaf button with a null Command is a dead
    /// affordance, which CommandBindingSweepTests refuses.
    /// </remarks>
    Task OpenInNewTabAsync();

    /// <summary>
    /// True when this session's window has more than one tab to switch between
    /// (#1598). Same condition as <see cref="CanMoveToNewWindow"/> today, kept
    /// separate because they answer different questions and
    /// <c>MacNativeMenuBuilder</c>'s tab-switch items are enabled by this one.
    /// </summary>
    bool CanSwitchTabs { get; }

    /// <summary>
    /// Show the next (<paramref name="step"/> +1) or previous (-1) tab of this
    /// session's window, wrapping at the ends (#1598). A no-op when the window
    /// has one tab.
    /// </summary>
    /// <remarks>
    /// The window's tabs are the view's business, but on macOS a KEYSTROKE
    /// cannot reach them: AppKit treats Control-Tab as a key-view/key-equivalent
    /// keystroke, so it never arrives at Avalonia's KeyDown (measured by
    /// <c>reader_speed_bench.py --multi</c> with a real CGEvent). The native
    /// Window menu has to carry the gesture, and the native menu is built from a
    /// view model — so the view model needs a way to ask.
    /// </remarks>
    void SwitchTab(int step);

    /// <summary>Move this session's tab into a window of its own.</summary>
    void MoveToNewWindow();

    /// <summary>True when more than one document window is open.</summary>
    bool CanMergeAllWindows { get; }

    /// <summary>Gather every window's documents as tabs of this session's window.</summary>
    void MergeAllWindows();

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
