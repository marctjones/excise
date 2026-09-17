using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Reactive;
using Avalonia.Automation;
using Avalonia.Controls;
using Excise.App.Models;
using Excise.App.Workspace;
using Microsoft.Extensions.Logging;
using ReactiveUI;

namespace Excise.App.ViewModels;

/// <summary>
/// #1551: this view model is one document session. The members here connect
/// it to the <see cref="DocumentWorkspace"/> around it; with no
/// <see cref="SessionHost"/> (a view model built on its own, as every
/// single-window test does) each path behaves exactly as it did before
/// multi-document support existed.
/// </summary>
/// <remarks>Design: docs/architecture/main-window-architecture.md §7.</remarks>
public partial class MainWindowViewModel
{
    private NotifyCollectionChangedEventHandler? _recentFilesChangedHandler;
    private ObservableCollection<string>? _observedRecentFiles;
    private DocumentOpenMode _documentOpenMode = DocumentOpenMode.Automatic;
    private string? _openDocumentsSignature;
    private ReactiveCommand<OpenDocumentEntry, Unit>? _activateOpenDocumentCommand;
    private ReactiveCommand<Unit, Unit>? _moveToNewWindowCommand;
    private ReactiveCommand<Unit, Unit>? _mergeAllWindowsCommand;
    private bool _sessionReleased;

    /// <summary>
    /// The workspace-facing host of this session, or null when the view model
    /// stands alone.
    /// </summary>
    internal IDocumentSessionHost? SessionHost { get; set; }

    /// <summary>
    /// Set by the window once it has applied the persisted preferences to this
    /// session, so binding the session to a window again (a tab switch) does not
    /// reset its view mode or performance settings.
    /// </summary>
    internal bool WindowPreferencesApplied { get; set; }

    /// <summary>The open document's path; empty when none is open.</summary>
    internal string CurrentFilePath => _currentFilePath;

    /// <summary>
    /// Where a document opened from this session goes when it already shows
    /// one. App-wide like every preference; the workspace reads it from the
    /// requesting session.
    /// </summary>
    internal DocumentOpenMode DocumentOpenMode
    {
        get => _documentOpenMode;
        set => this.RaiseAndSetIfChanged(ref _documentOpenMode, value);
    }

    /// <summary>
    /// Every open document (#1553), in opening order, this one marked current.
    /// Empty for a view model with no workspace.
    /// </summary>
    internal IReadOnlyList<OpenDocumentEntry> OpenDocuments =>
        SessionHost?.OpenDocuments ?? Array.Empty<OpenDocumentEntry>();

    /// <summary>Window ▸ &lt;document&gt;: bring that document's window forward.</summary>
    internal ReactiveCommand<OpenDocumentEntry, Unit> ActivateOpenDocumentCommand =>
        _activateOpenDocumentCommand ??= ReactiveCommand.Create<OpenDocumentEntry>(
            entry => SessionHost?.ActivateDocument(entry));

    /// <summary>
    /// True when this window has more than one tab, so Window ▸ Show Next/
    /// Previous Tab does something (#1598). False for a view model with no
    /// session host, which is every plain unit test.
    /// </summary>
    internal bool CanSwitchTabs => SessionHost?.CanSwitchTabs == true;

    /// <summary>
    /// Window ▸ Show Next Tab (+1) / Show Previous Tab (-1) (#1598): the
    /// in-app document tabs of THIS window, not macOS's window tabs.
    /// </summary>
    /// <remarks>
    /// This exists on the view model because on macOS the only thing that can
    /// receive Control-Tab is the native menu, which is built from a view model
    /// (<c>MacNativeMenuBuilder</c>). AppKit consumes Control-Tab as a
    /// key-view/key-equivalent keystroke before Avalonia's tunnelling KeyDown
    /// handler ever sees it.
    /// </remarks>
    internal void SwitchTab(int step) => SessionHost?.SwitchTab(step);

    /// <summary>Window ▸ Move Tab to New Window (#1554).</summary>
    internal ReactiveCommand<Unit, Unit> MoveToNewWindowCommand =>
        _moveToNewWindowCommand ??= ReactiveCommand.Create(() => SessionHost?.MoveToNewWindow());

    /// <summary>Window ▸ Merge All Windows (#1554).</summary>
    internal ReactiveCommand<Unit, Unit> MergeAllWindowsCommand =>
        _mergeAllWindowsCommand ??= ReactiveCommand.Create(() => SessionHost?.MergeAllWindows());

    /// <summary>
    /// The in-window Window menu (Windows and Linux; macOS builds its native
    /// menu from <see cref="OpenDocuments"/>). Built fresh on every read, like
    /// <see cref="RecentFileMenuItems"/>.
    /// </summary>
    public ObservableCollection<MenuItem> OpenDocumentMenuItems
    {
        get
        {
            var items = new ObservableCollection<MenuItem>();
            var documents = OpenDocuments;
            if (documents.Count == 0)
            {
                items.Add(new MenuItem { Header = "No open documents", IsEnabled = false });
                return items;
            }

            items.Add(new MenuItem
            {
                Header = "Move Tab to New Window",
                Command = MoveToNewWindowCommand,
                IsEnabled = SessionHost?.CanMoveToNewWindow == true,
            });
            items.Add(new MenuItem
            {
                Header = "Merge All Windows",
                Command = MergeAllWindowsCommand,
                IsEnabled = SessionHost?.CanMergeAllWindows == true,
            });
            items.Add(new MenuItem { Header = "-" });

            foreach (var document in documents)
            {
                var item = new MenuItem
                {
                    Header = OpenDocumentMenuHeader(document),
                    Command = ActivateOpenDocumentCommand,
                    CommandParameter = document,
                    ToggleType = MenuItemToggleType.Radio,
                    IsChecked = document.IsCurrent,
                };
                if (document.FilePath != null)
                    ToolTip.SetTip(item, document.FilePath);
                AutomationProperties.SetName(item, OpenDocumentAccessibleName(document));
                items.Add(item);
            }

            return items;
        }
    }

    /// <summary>The menu label: the file name, with a marker for unsaved edits.</summary>
    internal static string OpenDocumentMenuHeader(OpenDocumentEntry document) =>
        document.HasUnsavedChanges ? $"{document.Title} \u2022" : document.Title;

    /// <summary>What a screen reader says for a Window menu entry.</summary>
    internal static string OpenDocumentAccessibleName(OpenDocumentEntry document) =>
        document.HasUnsavedChanges ? $"{document.Title}, unsaved changes" : document.Title;

    /// <summary>
    /// The workspace's list of documents, or a name or unsaved state in it,
    /// changed. Raises only when what the menu shows actually differs, because
    /// the workspace forwards every status-bar change (a link hover included).
    /// </summary>
    internal void NotifyOpenDocumentsChanged()
    {
        var signature = string.Join('\n', OpenDocuments.Select(d =>
            $"{d.Title}|{d.FilePath}|{d.IsCurrent}|{d.HasUnsavedChanges}"))
            + $"|{SessionHost?.CanMoveToNewWindow}|{SessionHost?.CanMergeAllWindows}";
        if (signature == _openDocumentsSignature)
            return;
        _openDocumentsSignature = signature;
        this.RaisePropertyChanged(nameof(OpenDocuments));
        this.RaisePropertyChanged(nameof(OpenDocumentMenuItems));
    }

    /// <summary>Apply the persisted open mode; an unknown value keeps the default.</summary>
    internal void ApplyDocumentOpenModePreference(string? mode)
    {
        if (Enum.TryParse<DocumentOpenMode>(mode, out var parsed) && Enum.IsDefined(parsed))
            DocumentOpenMode = parsed;
    }

    /// <summary>
    /// Keep <see cref="HasRecentFiles"/> and <see cref="RecentFileMenuItems"/>
    /// current when the collection changes from anywhere. With one shared
    /// recent-files list per application, the session that adds a file is not
    /// necessarily the one whose menu is open.
    /// </summary>
    private void ObserveRecentFiles(ObservableCollection<string>? collection)
    {
        if (ReferenceEquals(collection, _observedRecentFiles))
            return;

        if (_observedRecentFiles != null && _recentFilesChangedHandler != null)
            _observedRecentFiles.CollectionChanged -= _recentFilesChangedHandler;

        _observedRecentFiles = collection;
        if (collection == null)
            return;

        _recentFilesChangedHandler ??= (_, _) =>
        {
            this.RaisePropertyChanged(nameof(HasRecentFiles));
            this.RaisePropertyChanged(nameof(RecentFileMenuItems));
        };
        collection.CollectionChanged += _recentFilesChangedHandler;
    }

    /// <summary>
    /// The session is ending: close its document without a prompt (the
    /// caller already asked), and release what the view model owns directly.
    /// Scoped services are released by the session's scope.
    /// </summary>
    internal void ReleaseSessionResources()
    {
        if (_sessionReleased)
            return;
        _sessionReleased = true;

        try
        {
            CloseDocument();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Closing the document of an ending session failed");
        }

        _documentService.DocumentReleased -= OnDocumentReleased;
        ObserveRecentFiles(null);
        _thumbnailSession.Dispose();
        PerformanceSettingsApplied = null;
        ViewerTileCacheResidentBytesProvider = null;
        _logger.LogInformation("Document session released");
    }
}
