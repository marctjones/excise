using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Excise.App.Models;
using Excise.App.Services.Host;
using Excise.App.ViewModels;
using Excise.App.Views;
using Microsoft.Extensions.Logging;

namespace Excise.App.Workspace;

/// <summary>
/// The application's open documents (#1463): which sessions exist, which
/// window shows each, where a newly opened file goes, and the quit review.
/// </summary>
/// <remarks>
/// Design: docs/architecture/main-window-architecture.md §7. One instance per
/// application. Everything runs on the UI thread.
/// </remarks>
internal sealed class DocumentWorkspace
{
    /// <summary>How far a new document window is offset from the window it was opened from.</summary>
    internal const int CascadeOffset = 28;

    private readonly DocumentSessionFactory _factory;
    private readonly ISettingsStore _settingsStore;
    private readonly ILogger _logger;
    private readonly List<DocumentSession> _sessions = new();
    private readonly List<MainWindow> _windows = new();
    private readonly Dictionary<DocumentSession, string> _loading = new();
    private ObservableCollection<string>? _sharedRecentFiles;
    private DocumentSession? _activeSession;
    private bool _quitInProgress;

    internal DocumentWorkspace(
        DocumentSessionFactory factory,
        ISettingsStore settingsStore,
        ILogger<DocumentWorkspace> logger)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Raised after a document window is created and before it is shown, so
    /// the application can wire what only the real app has (the #1478 cache
    /// trim coordinator and its OS pressure source).
    /// </summary>
    internal event Action<MainWindow>? WindowCreated;

    /// <summary>
    /// Asks the platform to quit. Defaults to the desktop lifetime's
    /// <c>TryShutdown</c>; a test replaces it.
    /// </summary>
    internal Action RequestShutdown { get; set; } = TryShutdownDesktop;

    internal IReadOnlyList<DocumentSession> Sessions => _sessions;

    internal IReadOnlyList<MainWindow> Windows => _windows;

    /// <summary>The session of the most recently activated document window.</summary>
    internal DocumentSession? ActiveSession =>
        _activeSession is { IsDisposed: false } active ? active : _sessions.LastOrDefault();

    /// <summary>True while an approved quit is shutting the application down.</summary>
    internal bool QuitApproved { get; private set; }

    internal bool HasUnsavedChanges => _sessions.Any(s => s.ViewModel.HasUnsavedDocumentChanges);

    /// <summary>
    /// A new session, registered with the workspace, sharing the app-wide
    /// recent-files list. It has no window yet.
    /// </summary>
    internal DocumentSession CreateSession()
    {
        var session = _factory.Create(this);
        if (_sharedRecentFiles == null)
            _sharedRecentFiles = session.ViewModel.RecentFiles;
        else
            session.ViewModel.RecentFiles = _sharedRecentFiles;

        session.ViewModel.SessionHost = session;
        _sessions.Add(session);
        _logger.LogInformation("Document session created ({Count} open)", _sessions.Count);
        return session;
    }

    /// <summary>
    /// Show <paramref name="session"/> in a new document window. With an
    /// <paramref name="origin"/>, the window takes the origin's size and is
    /// offset from it.
    /// </summary>
    internal MainWindow ShowInNewWindow(DocumentSession session, Window? origin = null)
    {
        ArgumentNullException.ThrowIfNull(session);

        var window = new MainWindow(_settingsStore)
        {
            DataContext = session.ViewModel,
        };
        session.AttachToWindow(window);

        if (origin != null)
            PlaceCascaded(window, origin);

        window.Activated += (_, _) => _activeSession = SessionShownIn(window);
        window.Closed += (_, _) => OnWindowClosed(window);
        _windows.Add(window);
        _activeSession = session;

        WindowCreated?.Invoke(window);
        window.Show();
        return window;
    }

    /// <summary>The session <paramref name="window"/> currently shows.</summary>
    internal DocumentSession? SessionShownIn(Window window) =>
        _sessions.FirstOrDefault(s => ReferenceEquals(s.Window, window)
                                      && ReferenceEquals(window.DataContext, s.ViewModel))
        ?? _sessions.FirstOrDefault(s => ReferenceEquals(s.Window, window));

    /// <summary>Every session hosted by <paramref name="window"/>.</summary>
    internal IReadOnlyList<DocumentSession> SessionsIn(Window window) =>
        _sessions.Where(s => ReferenceEquals(s.Window, window)).ToArray();

    /// <summary>The session that has <paramref name="path"/> open, if any.</summary>
    internal DocumentSession? FindSessionShowing(string path)
    {
        var full = NormalizePath(path);
        if (full == null)
            return null;

        return _sessions.FirstOrDefault(s =>
            !s.IsDisposed &&
            ((_loading.TryGetValue(s, out var loading) && PathsEqual(loading, full)) ||
             (s.FilePath is { } open && PathsEqual(NormalizePath(open), full))));
    }

    /// <summary>Bring <paramref name="session"/>'s window to the front.</summary>
    internal void Activate(DocumentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _activeSession = session;
        if (session.Window is not { } window)
            return;

        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
    }

    /// <summary>
    /// The open mode that applies to a request from <paramref name="origin"/>,
    /// with <see cref="DocumentOpenMode.Automatic"/> resolved.
    /// </summary>
    internal DocumentOpenMode ResolveOpenMode(DocumentSession origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        return origin.ViewModel.DocumentOpenMode switch
        {
            DocumentOpenMode.ReplaceCurrent => DocumentOpenMode.ReplaceCurrent,
            DocumentOpenMode.NewTab => DocumentOpenMode.NewTab,
            _ => DocumentOpenMode.NewWindow,
        };
    }

    internal bool OpensDocumentsElsewhere(DocumentSession origin) =>
        !IsEmpty(origin) && ResolveOpenMode(origin) != DocumentOpenMode.ReplaceCurrent;

    /// <summary>
    /// No document open and none on its way in. Two opens delivered back to
    /// back (a double-click in Finder while the first file still loads) must
    /// not both pick the same empty window.
    /// </summary>
    internal bool IsEmpty(DocumentSession session) =>
        !session.ViewModel.IsDocumentLoaded && !_loading.ContainsKey(session);

    /// <summary>
    /// Open <paramref name="paths"/> by the §7.4 rules: an already-open file is
    /// brought forward, an empty requesting window takes the first file, and
    /// the rest follow the open mode.
    /// </summary>
    internal async Task OpenDocumentsAsync(
        IReadOnlyList<string> paths,
        DocumentSession? origin,
        bool replaceConfirmed = false)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var targets = paths
            .Select(NormalizePath)
            .Where(p => p != null)
            .Select(p => p!)
            .Distinct(PathComparer)
            .ToList();
        if (targets.Count == 0)
            return;

        if (origin is not { IsDisposed: false })
            origin = ActiveSession;
        if (origin == null)
        {
            origin = CreateSession();
            ShowInNewWindow(origin);
        }

        var mode = ResolveOpenMode(origin);
        var originTaken = false;

        foreach (var path in targets)
        {
            var existing = FindSessionShowing(path);
            if (existing != null && (!ReferenceEquals(existing, origin) || mode != DocumentOpenMode.ReplaceCurrent))
            {
                _logger.LogInformation("{Path} is already open; bringing its window forward", path);
                Activate(existing);
                continue;
            }

            if (!originTaken && IsEmpty(origin))
            {
                originTaken = true;
                await LoadIntoAsync(origin, path);
                continue;
            }

            if (mode == DocumentOpenMode.ReplaceCurrent)
            {
                // One window, one document: a multi-file request replaces with
                // the first file only, as the single-document app always did.
                if (originTaken)
                    continue;
                originTaken = true;

                // #1233: replacing discards the open document's edits.
                if (!replaceConfirmed &&
                    !await origin.ViewModel.ConfirmDiscardUnsavedChangesAsync("open a different document"))
                {
                    _logger.LogInformation("Open of {Path} cancelled at the unsaved-changes prompt", path);
                    return;
                }

                await LoadIntoAsync(origin, path);
                continue;
            }

            var session = CreateSession();
            ShowInNewWindow(session, origin.Window);
            await LoadIntoAsync(session, path);

            // A file that did not open (a cancelled password prompt, a damaged
            // file) must not leave an empty window behind it.
            if (!session.IsDisposed && !session.ViewModel.IsDocumentLoaded)
                CloseSession(session);
        }
    }

    /// <summary>
    /// Close <paramref name="session"/>'s window when another session exists
    /// (Close Document with more than one open). False keeps the caller's
    /// single-document behaviour.
    /// </summary>
    internal bool TryCloseSession(DocumentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (_sessions.Count(s => !s.IsDisposed) <= 1)
            return false;

        CloseSession(session);
        return true;
    }

    /// <summary>
    /// Review every session's unsaved changes, activating each dirty window
    /// before asking. False as soon as one answer keeps its document.
    /// </summary>
    internal async Task<bool> ReviewUnsavedChangesAsync(
        string actionDescription,
        IEnumerable<DocumentSession>? sessions = null)
    {
        foreach (var session in (sessions ?? _sessions).ToArray())
        {
            if (session.IsDisposed || !session.ViewModel.HasUnsavedDocumentChanges)
                continue;

            Activate(session);
            if (!await session.ViewModel.ConfirmDiscardUnsavedChangesAsync(actionDescription))
                return false;
        }

        return true;
    }

    /// <summary>
    /// File ▸ Exit and Cmd+Q: review every dirty session, then quit. Any
    /// Cancel (or a save that did not happen) keeps the application open.
    /// </summary>
    internal async Task RequestQuitAsync()
    {
        if (_quitInProgress)
            return;

        _quitInProgress = true;
        try
        {
            if (!await ReviewUnsavedChangesAsync("quit excise"))
            {
                _logger.LogInformation("Quit cancelled at an unsaved-changes prompt");
                return;
            }

            QuitApproved = true;
            RequestShutdown();
        }
        finally
        {
            // TryShutdown is synchronous: if a window still refused to close,
            // the next quit must review again.
            QuitApproved = false;
            _quitInProgress = false;
        }
    }

    /// <summary>
    /// Preferences are app-wide: a saved dialog reaches every other session.
    /// The redaction policies among them must never differ between windows.
    /// </summary>
    internal void ApplyPreferencesToOtherSessions(DocumentSession origin, PreferencesViewModel preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        foreach (var session in _sessions.ToArray())
        {
            if (ReferenceEquals(session, origin) || session.IsDisposed)
                continue;
            preferences.SaveToMainViewModel(session.ViewModel);
        }
    }

    private void CloseSession(DocumentSession session)
    {
        if (session.Window is { } window)
        {
            // The window's Closing guard asks about unsaved changes; its
            // Closed handler disposes the session.
            window.Close();
            return;
        }

        RemoveSession(session);
    }

    private async Task LoadIntoAsync(DocumentSession session, string path)
    {
        _loading[session] = path;
        try
        {
            await session.ViewModel.LoadDocumentAsync(path);
        }
        catch (Exception ex)
        {
            // LoadDocumentAsync reports its own failures; what reaches here is
            // a path that vanished between the picker and the load.
            _logger.LogError(ex, "Failed to open {Path}", path);
            session.ViewModel.ToastService.ShowError($"Could not open {Path.GetFileName(path)}", ex.Message);
        }
        finally
        {
            _loading.Remove(session);
        }
    }

    private void OnWindowClosed(MainWindow window)
    {
        _windows.Remove(window);
        foreach (var session in SessionsIn(window))
            RemoveSession(session);

        // A closed window can outlive its close for a while (the platform and
        // the dispatcher may still hold it); it must not hold a session too.
        window.DataContext = null;

        ReassignDesktopMainWindow(window);
    }

    private void RemoveSession(DocumentSession session)
    {
        _sessions.Remove(session);
        _loading.Remove(session);
        if (ReferenceEquals(_activeSession, session))
            _activeSession = null;
        session.Dispose();
        _logger.LogInformation("Document session closed ({Count} open)", _sessions.Count);
    }

    private void ReassignDesktopMainWindow(Window closed)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;
        if (!ReferenceEquals(desktop.MainWindow, closed))
            return;

        // Code that still asks the lifetime for "the" main window (the
        // clipboard adapter, the performance harness) must get a live one.
        var next = (ActiveSession?.Window as MainWindow) ?? _windows.LastOrDefault();
        if (next != null)
            desktop.MainWindow = next;
    }

    private static void PlaceCascaded(Window window, Window origin)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        if (origin.WindowState != WindowState.Maximized)
        {
            window.Width = origin.Width;
            window.Height = origin.Height;
        }
        else
        {
            window.WindowState = WindowState.Maximized;
        }

        var scale = origin.RenderScaling > 0 ? origin.RenderScaling : 1.0;
        var offset = (int)Math.Round(CascadeOffset * scale);
        window.Position = new PixelPoint(origin.Position.X + offset, origin.Position.Y + offset);
    }

    private static void TryShutdownDesktop()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.TryShutdown();
    }

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static bool PathsEqual(string? a, string? b) =>
        a != null && b != null && PathComparer.Equals(a, b);

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
