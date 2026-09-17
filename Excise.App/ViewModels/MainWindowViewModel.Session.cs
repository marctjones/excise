using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
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
    private DocumentOpenMode _documentOpenMode = DocumentOpenMode.ReplaceCurrent;
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
