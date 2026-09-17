using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Excise.App.Services.Host;
using Excise.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Excise.App.Workspace;

/// <summary>
/// One open document (#1551): a dependency-injection scope and the
/// <see cref="MainWindowViewModel"/> resolved from it, plus the window that
/// currently shows it.
/// </summary>
/// <remarks>
/// Everything document-shaped is scoped (see
/// <c>ApplicationComposition</c>), so two sessions never share a document
/// service, a search or index session, a toast service, or a dialog owner.
/// Disposing the session closes its document and disposes the scope, which is
/// what lets a closed window's memory go (§7.6 of
/// docs/architecture/main-window-architecture.md).
/// </remarks>
internal sealed class DocumentSession : IDocumentSessionHost, IDisposable
{
    private readonly DocumentWorkspace _workspace;
    private readonly IServiceScope? _scope;
    private readonly Func<Window?> _fallbackOwner;
    private bool _disposed;

    internal DocumentSession(
        DocumentWorkspace workspace,
        IServiceScope? scope,
        MainWindowViewModel viewModel,
        IWindowHost windowHost,
        Func<Window?> fallbackOwner)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _scope = scope;
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        WindowHost = windowHost ?? throw new ArgumentNullException(nameof(windowHost));
        _fallbackOwner = fallbackOwner ?? throw new ArgumentNullException(nameof(fallbackOwner));

        // The session's dialogs and pickers are owned by the window that shows
        // it, never by whichever window the desktop lifetime calls "main".
        WindowHost.MainWindowResolver = () => Window ?? _fallbackOwner();
    }

    internal MainWindowViewModel ViewModel { get; }

    internal IWindowHost WindowHost { get; }

    /// <summary>The window that currently shows this session, or null.</summary>
    internal Window? Window { get; private set; }

    internal bool IsDisposed => _disposed;

    /// <summary>The full path of the open document, or null when none is open.</summary>
    internal string? FilePath =>
        ViewModel.IsDocumentLoaded && !string.IsNullOrEmpty(ViewModel.CurrentFilePath)
            ? ViewModel.CurrentFilePath
            : null;

    internal void AttachToWindow(Window? window) => Window = window;

    bool IDocumentSessionHost.OpensDocumentsElsewhere => _workspace.OpensDocumentsElsewhere(this);

    Task IDocumentSessionHost.OpenDocumentsAsync(IReadOnlyList<string> paths, bool replaceConfirmed) =>
        _workspace.OpenDocumentsAsync(paths, this, replaceConfirmed);

    bool IDocumentSessionHost.TryCloseSession() => _workspace.TryCloseSession(this);

    Task IDocumentSessionHost.RequestQuitAsync() => _workspace.RequestQuitAsync();

    IReadOnlyList<OpenDocumentEntry> IDocumentSessionHost.OpenDocuments => _workspace.DescribeOpenDocuments(this);

    void IDocumentSessionHost.ActivateDocument(OpenDocumentEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Key is DocumentSession session && !session.IsDisposed)
            _workspace.Activate(session);
    }

    bool IDocumentSessionHost.CanMoveToNewWindow =>
        Window is Views.MainWindow { DocumentTabs.Tabs.Count: > 1 };

    void IDocumentSessionHost.MoveToNewWindow() => _workspace.MoveToNewWindow(this);

    bool IDocumentSessionHost.CanMergeAllWindows => _workspace.Windows.Count > 1;

    void IDocumentSessionHost.MergeAllWindows()
    {
        if (Window is Views.MainWindow window)
            _workspace.MergeAllWindowsInto(window);
    }

    void IDocumentSessionHost.ApplyPreferencesToOtherSessions(PreferencesViewModel preferences) =>
        _workspace.ApplyPreferencesToOtherSessions(this, preferences);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        ViewModel.SessionHost = null;
        ViewModel.ReleaseSessionResources();
        WindowHost.MainWindowResolver = _fallbackOwner;
        Window = null;
        _scope?.Dispose();
    }
}
