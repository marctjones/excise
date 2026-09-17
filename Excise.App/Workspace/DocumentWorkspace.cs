using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Excise.App.Models;
using Excise.App.Services.Host;
using Excise.App.ViewModels;
using Excise.App.Views;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using System.Reactive;
using System.Reactive.Linq;

namespace Excise.App.Workspace;

/// <summary>
/// The application's open documents (#1463): which sessions exist, which
/// window shows each, where a newly opened file goes, and the quit review.
/// </summary>
/// <remarks>
/// Design: docs/architecture/main-window-architecture.md §7. One instance per
/// application. Everything runs on the UI thread.
/// </remarks>
internal sealed class DocumentWorkspace : DocumentTabsViewModel.ITabsHost
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
        session.ViewModel.PropertyChanged += OnSessionPropertyChanged;
        _sessions.Add(session);
        _logger.LogInformation("Document session created ({Count} open)", _sessions.Count);
        NotifyOpenDocumentsChanged();
        return session;
    }

    /// <summary>
    /// Show <paramref name="session"/> in a new document window. With an
    /// <paramref name="origin"/>, the window takes the origin's size and is
    /// offset from it.
    /// </summary>
    internal MainWindow ShowInNewWindow(DocumentSession session, Window? origin = null, bool joinNativeTabs = true)
    {
        ArgumentNullException.ThrowIfNull(session);

        var window = new MainWindow(_settingsStore)
        {
            DataContext = session.ViewModel,
        };
        session.AttachToWindow(window);

        // #1554: every document window holds tabs; one tab shows no strip.
        var tabs = new DocumentTabsViewModel(this);
        tabs.Add(session, select: true);
        window.DocumentTabs = tabs;
        tabs.PropertyChanged += (_, e) =>
        {
            // The shown tab is the active document when its window is the
            // active one (or held the active document before the switch).
            if (e.PropertyName == nameof(DocumentTabsViewModel.SelectedTab) &&
                (window.IsActive || ReferenceEquals(_activeSession?.Window, window)) &&
                tabs.SelectedTab?.Session is { IsDisposed: false } shown)
                _activeSession = shown;
            NotifyOpenDocumentsChanged();
        };

        if (origin != null)
            PlaceCascaded(window, origin);

        window.Activated += (_, _) => _activeSession = SessionShownIn(window);
        window.Closed += (_, _) => OnWindowClosed(window);
        _windows.Add(window);
        _activeSession = session;

        WindowCreated?.Invoke(window);
        window.Show();

        // #1552: Avalonia disables native tabbing on every window; turn it back
        // on, and honour "Prefer tabs when opening documents" for a window
        // opened from another one. No-ops off macOS and in a headless host.
        if (OperatingSystem.IsMacOS())
        {
            MacWindowTabbing.Prepare(window, _logger);
            if (joinNativeTabs && origin != null && !ReferenceEquals(origin, window))
                MacWindowTabbing.JoinTabGroupIfPreferred(window, origin, _logger);
        }

        return window;
    }

    /// <summary>
    /// The open documents in opening order, as the Window menu lists them,
    /// with <paramref name="current"/> marked.
    /// </summary>
    internal IReadOnlyList<OpenDocumentEntry> DescribeOpenDocuments(DocumentSession? current)
    {
        var entries = new List<OpenDocumentEntry>(_sessions.Count);
        foreach (var session in _sessions)
        {
            if (session.IsDisposed)
                continue;
            var path = session.FilePath;
            var title = path != null ? Path.GetFileName(path) : "Untitled";
            entries.Add(new OpenDocumentEntry(
                title,
                path,
                ReferenceEquals(session, current),
                session.ViewModel.HasUnsavedDocumentChanges,
                session));
        }

        return entries;
    }

    /// <summary>The session <paramref name="window"/> currently shows.</summary>
    internal DocumentSession? SessionShownIn(Window window) =>
        (window as MainWindow)?.DocumentTabs?.SelectedTab?.Session
        ?? _sessions.FirstOrDefault(s => ReferenceEquals(s.Window, window)
                                         && ReferenceEquals(window.DataContext, s.ViewModel))
        ?? _sessions.FirstOrDefault(s => ReferenceEquals(s.Window, window));

    /// <summary>Every session hosted by <paramref name="window"/>, in tab order.</summary>
    internal IReadOnlyList<DocumentSession> SessionsIn(Window window)
    {
        var hosted = _sessions.Where(s => ReferenceEquals(s.Window, window)).ToList();
        if ((window as MainWindow)?.DocumentTabs is { } tabs)
            hosted.Sort((a, b) => IndexIn(tabs, a).CompareTo(IndexIn(tabs, b)));
        return hosted;
    }

    private static int IndexIn(DocumentTabsViewModel tabs, DocumentSession session)
    {
        var tab = tabs.TabFor(session);
        return tab == null ? int.MaxValue : tabs.Tabs.IndexOf(tab);
    }

    /// <summary>
    /// Show <paramref name="session"/> as a new, selected tab of
    /// <paramref name="window"/> (#1554).
    /// </summary>
    internal void ShowInNewTab(DocumentSession session, MainWindow window)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(window);
        if (window.DocumentTabs is not { } tabs)
        {
            ShowInNewWindow(session, window);
            return;
        }

        session.AttachToWindow(window);
        tabs.Add(session, select: true);
        _activeSession = session;
        NotifyOpenDocumentsChanged();
        window.Activate();
    }

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

        if (window is MainWindow { DocumentTabs: { } tabs } && tabs.TabFor(session) is { } tab)
            tabs.SelectedTab = tab;

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
            DocumentOpenMode.NewTab when origin.Window is MainWindow { DocumentTabs: not null }
                => DocumentOpenMode.NewTab,
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
            if (mode == DocumentOpenMode.NewTab && origin.Window is MainWindow tabWindow)
                ShowInNewTab(session, tabWindow);
            else
                ShowInNewWindow(session, origin.Window);
            await LoadIntoAsync(session, path);

            // A file that did not open (a cancelled password prompt, a damaged
            // file) must not leave an empty window behind it.
            if (!session.IsDisposed && !session.ViewModel.IsDocumentLoaded)
                CloseSession(session);
        }
    }

    /// <summary>
    /// Close <paramref name="session"/>'s tab, or its window when it is the
    /// window's only tab, provided another session exists (Close Document with
    /// more than one open). False keeps the caller's single-document behaviour.
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

    // ── #1554: what the tab strip asks for ──────────────────────────────────

    /// <summary>
    /// A tab's close button: exactly Close Document for that session, so the
    /// unsaved-changes prompt and the last-document rule are the same ones.
    /// </summary>
    Task DocumentTabsViewModel.ITabsHost.CloseTabAsync(DocumentTabsViewModel tabs, DocumentTabViewModel tab)
    {
        if (tab.Session.IsDisposed)
            return Task.CompletedTask;
        Activate(tab.Session);
        return ExecuteAsync(tab.Session.ViewModel.CloseDocumentCommand);
    }

    void DocumentTabsViewModel.ITabsHost.MoveTabToNewWindow(DocumentTabsViewModel tabs, DocumentTabViewModel tab) =>
        MoveToNewWindow(tab.Session);

    async Task DocumentTabsViewModel.ITabsHost.CopyPathAsync(string path)
    {
        var clipboard = (ActiveSession?.Window as TopLevel)?.Clipboard;
        if (clipboard != null)
            await clipboard.SetTextAsync(path);
    }

    void DocumentTabsViewModel.ITabsHost.RevealInFileManager(string path) =>
        FileManagerReveal.Reveal(path, _logger);

    /// <summary>
    /// Take <paramref name="session"/> out of its window's tabs into a window
    /// of its own. A window's only tab stays where it is.
    /// </summary>
    internal MainWindow? MoveToNewWindow(DocumentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Window is not MainWindow { DocumentTabs: { Tabs.Count: > 1 } tabs } origin)
            return null;

        tabs.Remove(session);
        // Moving a tab OUT must not put it straight back into a native tab group.
        return ShowInNewWindow(session, origin, joinNativeTabs: false);
    }

    /// <summary>
    /// Every other window's tabs join <paramref name="target"/>, and the
    /// emptied windows close (#1554). Nothing is prompted: no document closes.
    /// </summary>
    internal void MergeAllWindowsInto(MainWindow target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.DocumentTabs is not { } targetTabs)
            return;

        foreach (var window in _windows.Where(w => !ReferenceEquals(w, target)).ToArray())
        {
            foreach (var session in SessionsIn(window))
            {
                window.DocumentTabs?.Remove(session);
                session.AttachToWindow(target);
                targetTabs.Add(session, select: false);
            }

            // Its sessions now belong to the target, so the emptied window's
            // close disposes nothing and asks nothing.
            window.Close();
        }

        NotifyOpenDocumentsChanged();
        target.Activate();
    }

    private static async Task ExecuteAsync(ReactiveCommand<Unit, Unit> command) => await command.Execute();

    private void CloseSession(DocumentSession session)
    {
        // #1554: one of several tabs goes as a tab, never with its window.
        if (session.Window is MainWindow { DocumentTabs: { Tabs.Count: > 1 } tabs })
        {
            tabs.Remove(session);
            RemoveSession(session);
            return;
        }

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
        // the dispatcher may still hold it); it must not hold a session too,
        // neither as its DataContext nor through its tabs.
        var tabs = window.DocumentTabs;
        window.DocumentTabs = null;
        tabs?.DetachAll();
        window.DataContext = null;

        ReassignDesktopMainWindow(window);
    }

    private void RemoveSession(DocumentSession session)
    {
        _sessions.Remove(session);
        _loading.Remove(session);
        if (ReferenceEquals(_activeSession, session))
            _activeSession = null;
        session.ViewModel.PropertyChanged -= OnSessionPropertyChanged;
        session.Dispose();
        _logger.LogInformation("Document session closed ({Count} open)", _sessions.Count);
        NotifyOpenDocumentsChanged();
    }

    // The Window menu shows every document's name and unsaved state, so any
    // session's change of either is news to all of them. FileState does not
    // raise its own changes; SaveButtonText and StatusBarText are what every
    // dirty-state change raises by hand, so they stand in for it.
    private void OnSessionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainWindowViewModel.DocumentName):
            case nameof(MainWindowViewModel.IsDocumentLoaded):
            case nameof(MainWindowViewModel.SaveButtonText):
            case nameof(MainWindowViewModel.StatusBarText):
            case null:
                NotifyOpenDocumentsChanged();
                break;
        }
    }

    private void NotifyOpenDocumentsChanged()
    {
        foreach (var session in _sessions.ToArray())
            session.ViewModel.NotifyOpenDocumentsChanged();
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
