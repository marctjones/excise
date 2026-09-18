using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Excise.App.Workspace;
using ReactiveUI;

namespace Excise.App.ViewModels;

/// <summary>
/// The in-app document tabs of one window (#1554). The window shows the
/// selected tab's session; the others keep their state (document, undo,
/// selection, marks) and hold no viewer caches, because a window has one
/// viewer.
/// </summary>
/// <remarks>Design: docs/architecture/main-window-architecture.md §7.3.</remarks>
public sealed class DocumentTabsViewModel : ReactiveObject
{
    private readonly ITabsHost _host;
    private DocumentTabViewModel? _selectedTab;

    /// <summary>What the tab strip asks of the workspace.</summary>
    internal interface ITabsHost
    {
        Task CloseTabAsync(DocumentTabsViewModel tabs, DocumentTabViewModel tab);
        void MoveTabToNewWindow(DocumentTabsViewModel tabs, DocumentTabViewModel tab);
        Task CopyPathAsync(string path);
        void RevealInFileManager(string path);
    }

    internal DocumentTabsViewModel(ITabsHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        Tabs.CollectionChanged += OnTabsChanged;
        SelectNextTabCommand = ReactiveCommand.Create(() => SelectRelative(+1));
        SelectPreviousTabCommand = ReactiveCommand.Create(() => SelectRelative(-1));
    }

    public ObservableCollection<DocumentTabViewModel> Tabs { get; } = new();

    public DocumentTabViewModel? SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (value != null && !Tabs.Contains(value))
                return;
            if (ReferenceEquals(value, _selectedTab))
                return;

            this.RaiseAndSetIfChanged(ref _selectedTab, value);
            foreach (var tab in Tabs)
                tab.IsSelected = ReferenceEquals(tab, value);
            RaiseOverflowChanged();
        }
    }

    /// <summary>The strip is shown only when there is more than one tab to switch between.</summary>
    public bool IsStripVisible => Tabs.Count > 1;

    public ReactiveCommand<Unit, Unit> SelectNextTabCommand { get; }

    public ReactiveCommand<Unit, Unit> SelectPreviousTabCommand { get; }


    /// <summary>
    /// The overflow list: every tab, so a tab too narrow to read (or a strip
    /// too full to show them all) is still one click away. The strip never
    /// scrolls. Built fresh on every read.
    /// </summary>
    public ObservableCollection<MenuItem> OverflowMenuItems
    {
        get
        {
            var items = new ObservableCollection<MenuItem>();
            foreach (var tab in Tabs)
            {
                var item = new MenuItem
                {
                    Header = tab.DisplayTitle,
                    Command = tab.SelectCommand,
                    ToggleType = MenuItemToggleType.Radio,
                    IsChecked = tab.IsSelected,
                };
                AutomationProperties.SetName(item, tab.AccessibleName);
                items.Add(item);
            }
            return items;
        }
    }

    internal DocumentTabViewModel? TabFor(DocumentSession session) =>
        Tabs.FirstOrDefault(t => ReferenceEquals(t.Session, session));

    internal DocumentTabViewModel Add(DocumentSession session, bool select)
    {
        ArgumentNullException.ThrowIfNull(session);
        var tab = TabFor(session);
        if (tab == null)
        {
            tab = new DocumentTabViewModel(this, session);
            Tabs.Add(tab);
        }

        if (select || SelectedTab == null)
            SelectedTab = tab;
        return tab;
    }

    /// <summary>
    /// Take <paramref name="session"/>'s tab out, selecting its right-hand
    /// neighbour (or the new last tab) if it was selected. The session itself
    /// is not disposed here.
    /// </summary>
    internal bool Remove(DocumentSession session)
    {
        var tab = TabFor(session);
        if (tab == null)
            return false;

        var index = Tabs.IndexOf(tab);
        var wasSelected = ReferenceEquals(tab, SelectedTab);
        if (wasSelected)
        {
            var next = Tabs.Count > 1
                ? Tabs[index + 1 < Tabs.Count ? index + 1 : index - 1]
                : null;
            SelectedTab = next;
        }

        Tabs.RemoveAt(index);
        tab.Detach();
        return true;
    }

    /// <summary>The window closed: let go of every tab and its session.</summary>
    internal void DetachAll()
    {
        _selectedTab = null;
        foreach (var tab in Tabs)
            tab.Detach();
        Tabs.Clear();
    }

    /// <summary>Drag-to-reorder: move the tab at <paramref name="from"/> to <paramref name="to"/>.</summary>
    internal void Move(int from, int to)
    {
        if (from < 0 || from >= Tabs.Count || to < 0 || to >= Tabs.Count || from == to)
            return;
        Tabs.Move(from, to);
    }

    private void SelectRelative(int step)
    {
        if (Tabs.Count < 2)
            return;
        var index = SelectedTab == null ? 0 : Tabs.IndexOf(SelectedTab);
        SelectedTab = Tabs[((index + step) % Tabs.Count + Tabs.Count) % Tabs.Count];
    }

    internal Task CloseTabAsync(DocumentTabViewModel tab) => _host.CloseTabAsync(this, tab);

    internal void CloseOtherTabs(DocumentTabViewModel keep)
    {
        foreach (var tab in Tabs.Where(t => !ReferenceEquals(t, keep)).ToArray())
            _ = _host.CloseTabAsync(this, tab);
    }

    internal void MoveToNewWindow(DocumentTabViewModel tab) => _host.MoveTabToNewWindow(this, tab);

    internal Task CopyPathAsync(DocumentTabViewModel tab) =>
        tab.FilePath is { } path ? _host.CopyPathAsync(path) : Task.CompletedTask;

    internal void Reveal(DocumentTabViewModel tab)
    {
        if (tab.FilePath is { } path)
            _host.RevealInFileManager(path);
    }

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        for (var i = 0; i < Tabs.Count; i++)
            Tabs[i].SetPosition(i + 1, Tabs.Count);
        this.RaisePropertyChanged(nameof(IsStripVisible));
        RaiseOverflowChanged();
    }

    internal void RaiseOverflowChanged() => this.RaisePropertyChanged(nameof(OverflowMenuItems));
}

/// <summary>One document tab (#1554).</summary>
public sealed class DocumentTabViewModel : ReactiveObject
{
    private readonly DocumentTabsViewModel _owner;
    private bool _isSelected;
    private int _position = 1;
    private int _count = 1;

    internal DocumentTabViewModel(DocumentTabsViewModel owner, DocumentSession session)
    {
        _owner = owner;
        Session = session;
        Session.ViewModel.PropertyChanged += OnSessionPropertyChanged;

        SelectCommand = ReactiveCommand.Create(() => { _owner.SelectedTab = this; });
        CloseCommand = ReactiveCommand.CreateFromTask(() => _owner.CloseTabAsync(this));
        CloseOthersCommand = ReactiveCommand.Create(() => _owner.CloseOtherTabs(this));
        MoveToNewWindowCommand = ReactiveCommand.Create(() => _owner.MoveToNewWindow(this));
        CopyPathCommand = ReactiveCommand.CreateFromTask(() => _owner.CopyPathAsync(this));
        RevealCommand = ReactiveCommand.Create(() => _owner.Reveal(this));
    }

    internal DocumentSession Session { get; }

    /// <summary>The file name, or "Untitled" for a tab with no document.</summary>
    public string Title =>
        Session.FilePath is { } path ? System.IO.Path.GetFileName(path) : "Untitled";

    /// <summary>The label: the title, with a marker for unsaved edits.</summary>
    public string DisplayTitle => HasUnsavedChanges ? $"{Title} •" : Title;

    public string? FilePath => Session.FilePath;

    public string ToolTip => FilePath ?? "No document open";

    public bool HasUnsavedChanges => Session.ViewModel.HasUnsavedDocumentChanges;

    public bool IsSelected
    {
        get => _isSelected;
        internal set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }

    /// <summary>
    /// What a screen reader says: name, position, unsaved state (#1554
    /// acceptance: "tabs are announced with position and unsaved state").
    /// </summary>
    public string AccessibleName =>
        $"{Title}, tab {_position} of {_count}" + (HasUnsavedChanges ? ", unsaved changes" : string.Empty);

    public string CloseButtonName => $"Close {Title}";

    /// <summary>Reveal in Finder on macOS, in the file manager elsewhere.</summary>
    public string RevealHeader =>
        OperatingSystem.IsMacOS() ? "Reveal in Finder"
        : OperatingSystem.IsWindows() ? "Show in Explorer"
        : "Show in File Manager";

    public bool HasFilePath => FilePath != null;

    public ReactiveCommand<Unit, Unit> SelectCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseOthersCommand { get; }
    public ReactiveCommand<Unit, Unit> MoveToNewWindowCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyPathCommand { get; }
    public ReactiveCommand<Unit, Unit> RevealCommand { get; }

    internal void SetPosition(int position, int count)
    {
        if (_position == position && _count == count)
            return;
        _position = position;
        _count = count;
        this.RaisePropertyChanged(nameof(AccessibleName));
    }

    internal void Detach() => Session.ViewModel.PropertyChanged -= OnSessionPropertyChanged;

    // FileState does not raise its own changes; SaveButtonText and
    // StatusBarText are what every dirty-state change raises by hand.
    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainWindowViewModel.DocumentName):
            case nameof(MainWindowViewModel.IsDocumentLoaded):
            case nameof(MainWindowViewModel.SaveButtonText):
            case nameof(MainWindowViewModel.StatusBarText):
            case null:
                Refresh();
                break;
        }
    }

    private string? _lastSignature;

    private void Refresh()
    {
        var signature = $"{Title}|{FilePath}|{HasUnsavedChanges}";
        if (signature == _lastSignature)
            return;
        _lastSignature = signature;
        this.RaisePropertyChanged(nameof(Title));
        this.RaisePropertyChanged(nameof(DisplayTitle));
        this.RaisePropertyChanged(nameof(FilePath));
        this.RaisePropertyChanged(nameof(ToolTip));
        this.RaisePropertyChanged(nameof(HasUnsavedChanges));
        this.RaisePropertyChanged(nameof(HasFilePath));
        this.RaisePropertyChanged(nameof(AccessibleName));
        this.RaisePropertyChanged(nameof(CloseButtonName));
        _owner.RaiseOverflowChanged();
    }
}
