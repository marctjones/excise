using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Excise.App.ViewModels;
using Excise.Core.Automation;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows.Input;

namespace Excise.App.Views;

internal static class MacNativeMenuBuilder
{
    /// <summary>
    /// #1476: the typewriter colour presets, in the order the toolbar's style
    /// flyout and Edit &gt; Typewriter Text Color list them in MainWindow.axaml.
    ///
    /// <para><c>CommandId</c> is the per-preset semantic id (#1476 follow-up).
    /// A <see cref="NativeMenuItem"/> has nowhere to attach
    /// <c>CommandAccessibility</c> — on macOS the item's TITLE is what
    /// VoiceOver reads, and the titles here are already distinct — so the id
    /// travels with the preset instead, and
    /// <c>TypewriterColorPresetAccessibilityTests</c> uses it to hold this
    /// list, the window menu and the flyout swatches to the same eight
    /// presets with the same eight registry labels.</para>
    /// </summary>
    internal static readonly (string Name, string Hex, string CommandId)[] TypewriterColorPresets =
    [
        ("Black", "#000000", PdfCommandIds.TypewriterSetColorBlack),
        ("Gray", "#555555", PdfCommandIds.TypewriterSetColorGray),
        ("Red", "#D0021B", PdfCommandIds.TypewriterSetColorRed),
        ("Orange", "#F5A623", PdfCommandIds.TypewriterSetColorOrange),
        ("Green", "#2E7D32", PdfCommandIds.TypewriterSetColorGreen),
        ("Blue", "#1565C0", PdfCommandIds.TypewriterSetColorBlue),
        ("Purple", "#6A1B9A", PdfCommandIds.TypewriterSetColorPurple),
        ("White", "#FFFFFF", PdfCommandIds.TypewriterSetColorWhite),
    ];

    public static NativeMenu Create(MainWindowViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        var state = new MenuState(viewModel);
        return state.Create();
    }

    /// <summary>
    /// #1584: one session's menu-bar entries, detached from any menu so a
    /// window's single <see cref="NativeMenu"/> can show them. Avalonia.Native
    /// binds a window's native menu to the first managed menu it is given and
    /// throws if handed another, so a window never swaps menus: it swaps the
    /// top-level items of its one menu instead (<see cref="WindowNativeMenu"/>).
    /// </summary>
    public static SessionMenu CreateSession(MainWindowViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        var state = new MenuState(viewModel);
        var holder = state.Create();
        var items = holder.Items.ToArray();
        // Remove one at a time: Clear() raises Reset without the old items,
        // so NativeMenu would leave each item's Parent set and the window's
        // menu would refuse to adopt it.
        while (holder.Items.Count > 0)
            holder.Items.RemoveAt(holder.Items.Count - 1);
        return new SessionMenu(items, state.Refresh);
    }

    /// <summary>A session's top-level menu items and the refresh that keeps them current.</summary>
    internal sealed class SessionMenu(IReadOnlyList<NativeMenuItemBase> items, Action refresh)
    {
        public IReadOnlyList<NativeMenuItemBase> Items { get; } = items;

        public void Refresh() => refresh();
    }

    private sealed class MenuState
    {
        private readonly MainWindowViewModel _viewModel;
        private readonly List<NativeMenuItem> _documentItems = new();
        private readonly List<NativeMenuItem> _selectedPageItems = new();
        private readonly List<NativeMenuItem> _selectedPageRemoveItems = new();
        private readonly List<NativeMenuItem> _selectedPageMoveEarlierItems = new();
        private readonly List<NativeMenuItem> _selectedPageMoveLaterItems = new();
        private readonly List<NativeMenuItem> _textSelectionItems = new();
        private readonly List<NativeMenuItem> _annotationSelectionItems = new();
        private readonly List<NativeMenuItem> _redactionItems = new();
        private readonly NativeMenuItem _saveItem;
        private readonly NativeMenuItem _undoItem;
        private readonly NativeMenuItem _redoItem;
        private readonly NativeMenuItem _recentFilesItem;
        private readonly NativeMenuItem _selectTextItem;
        private readonly NativeMenuItem _typewriterItem;
        private readonly NativeMenuItem _typewriterNextEditItem;
        private readonly NativeMenuItem _typewriterDiscardItem;
        private readonly NativeMenuItem _redactionModeItem;
        private readonly NativeMenuItem _viewClipboardItem;
        private readonly NativeMenuItem _redactionClipboardItem;
        private readonly NativeMenuItem _continuousScrollItem;
        private readonly NativeMenuItem _outlineItem;
        private readonly NativeMenuItem _thumbnailsItem;
        private readonly NativeMenuItem _attachmentsItem;
        private readonly NativeMenuItem _annotationToolbarItem;
        private readonly NativeMenuItem _annotationPaletteItem;
        private readonly NativeMenuItem _revealHiddenTextItem;
        private readonly NativeMenuItem _revealRasterizedHiddenItem;
        private readonly NativeMenuItem _formAuthoringItem;
        private readonly NativeMenuItem _printItem;
        private readonly NativeMenuItem _windowItem;
        private readonly List<NativeMenuItem> _tabActionItems = new();
        private readonly List<DocumentTabSwitchCommand> _documentTabCommands = new();
        private readonly int _windowFixedItemCount;
        private IReadOnlyList<string>? _recentFilesSnapshot;
        private string? _openDocumentsSnapshot;

        public MenuState(MainWindowViewModel viewModel)
        {
            _viewModel = viewModel;
            _saveItem = CommandItem("Save", _viewModel.SaveFileCommand, Key.S);
            // #782: Cmd+Z / Cmd+Shift+Z app-wide undo/redo.
            _undoItem = CommandItem("Undo", _viewModel.UndoCommand, Key.Z);
            _redoItem = CommandItem("Redo", _viewModel.RedoCommand, Key.Z, KeyModifiers.Meta | KeyModifiers.Shift);
            _recentFilesItem = Submenu("Open Recent");
            _selectTextItem = CommandItem("Select Text Mode", _viewModel.ToggleTextSelectionModeCommand, Key.T);
            _typewriterItem = CommandItem("Typewriter Mode", _viewModel.ToggleTypewriterModeCommand);
            // #780: keep the discard / next-pending-edit affordances reachable on
            // macOS's native menu, not just the in-window AXAML menu.
            _typewriterNextEditItem = CommandItem("Go to Next Pending Type-over Edit", _viewModel.GoToNextPendingTypewriterEditCommand);
            _typewriterDiscardItem = CommandItem("Discard Pending Type-over Edits", _viewModel.DiscardPendingTypewriterEditsCommand);
            _redactionModeItem = CommandItem("Redaction Mode", _viewModel.ToggleRedactionModeCommand, Key.R, modifiers: KeyModifiers.None);
            _viewClipboardItem = ToggleItem("Show Clipboard History", _viewModel.ToggleClipboardSidebarCommand);
            _redactionClipboardItem = ToggleItem("Show Clipboard History", _viewModel.ToggleClipboardSidebarCommand);
            _continuousScrollItem = CommandItem("Continuous Scroll", _viewModel.ToggleContinuousViewCommand, Key.C, KeyModifiers.Meta | KeyModifiers.Shift);
            _outlineItem = ToggleItem("Show Outline", _viewModel.ToggleOutlineCommand, Key.O, KeyModifiers.Meta | KeyModifiers.Shift);
            _thumbnailsItem = ToggleItem("Show Thumbnails", _viewModel.ToggleThumbnailsCommand, Key.T, KeyModifiers.Meta | KeyModifiers.Shift);
            // #1563: the in-window menu is hidden on macOS, so the pane toggle
            // must exist here too or it is unreachable on the primary platform.
            _attachmentsItem = ToggleItem("Show Attachments", _viewModel.ToggleAttachmentsCommand);
            // #1789: same reason — the optional annotation toolbar row and
            // floating palette have no other entry point on macOS.
            _annotationToolbarItem = ToggleItem("Annotation Toolbar", _viewModel.ToggleAnnotationToolbarCommand);
            _annotationPaletteItem = ToggleItem("Floating Annotation Palette", _viewModel.ToggleAnnotationPaletteCommand);
            _revealHiddenTextItem = ToggleItem("Reveal Hidden Text", _viewModel.ToggleRevealHiddenTextCommand);
            _revealRasterizedHiddenItem = ToggleItem("Reveal Rasterized Hidden Text", _viewModel.ToggleRevealRasterizedHiddenCommand);
            // #1476: the main toolbar hides low-priority actions when narrow, so
            // each must be reachable from the menu. On macOS this native menu is
            // the only one (the in-window MainMenuBar is hidden).
            _formAuthoringItem = CommandItem("Form Authoring Mode", _viewModel.ToggleFormAuthoringModeCommand);
            // #1545: enabled only when a document is open AND its /P flags allow
            // printing, so it is not a document item; Refresh sets it.
            _printItem = CommandItem("Print...", _viewModel.PrintCommand, Key.P);
            // #1598/#1552/#1553: this window's own document tabs, then macOS's
            // window-tab actions, then the open documents.
            //
            // ⚠️ The first two items are THE ONLY WAY Control-Tab reaches the
            // in-app tabs on macOS. MainWindow.OnTabSwitchKeyDown handles the
            // same gesture through a tunnelling KeyDown handler, which works on
            // Windows and Linux and never fires here: AppKit takes Control-Tab
            // as a key-view/key-equivalent keystroke, so Avalonia is not told
            // (#1598, measured with a real CGEvent). Avalonia maps Key.Tab to
            // the NSMenuItem key equivalent "\t" (KeyTransform.mm), and an
            // item's native validation asks its IsEnabled, which Avalonia keeps
            // in step with Command.CanExecute — hence the live CanExecute on
            // DocumentTabSwitchCommand rather than a flag set here.
            //
            // The macOS actions below them keep AppKit's own names with
            // "Window" added: they switch the NSWindow tab group (several excise
            // WINDOWS merged into one), which is a different thing from the
            // document tabs of one window, and two identically titled pairs in
            // one menu would be indistinguishable.
            _windowItem = Submenu("Window",
                DocumentTabItem("Show Previous Tab", -1, new KeyGesture(Key.Tab, KeyModifiers.Control | KeyModifiers.Shift)),
                DocumentTabItem("Show Next Tab", +1, new KeyGesture(Key.Tab, KeyModifiers.Control)),
                Separator(),
                TabActionItem("Show Previous Window Tab", Workspace.MacWindowTabbing.TabAction.SelectPreviousTab),
                TabActionItem("Show Next Window Tab", Workspace.MacWindowTabbing.TabAction.SelectNextTab),
                // ⚠️ #1615: these two are the APPKIT actions on the NSWindow tab
                // group, while the in-window AXAML menu's identically titled
                // items act on the in-app tabs. Left as they were deliberately:
                // which a macOS user should get is a product call.
                TabActionItem("Move Tab to New Window", Workspace.MacWindowTabbing.TabAction.MoveTabToNewWindow),
                TabActionItem("Merge All Windows", Workspace.MacWindowTabbing.TabAction.MergeAllWindows),
                Separator(),
                TabActionItem("Show or Hide Tab Bar", Workspace.MacWindowTabbing.TabAction.ToggleTabBar));
            // #1598: the document list after these is rebuilt on every change,
            // so the fixed prefix is counted once rather than derived from
            // _tabActionItems.Count (which stopped being the whole prefix the
            // moment the two items above were added).
            _windowFixedItemCount = _windowItem.Menu!.Items.Count;
        }

        public NativeMenu Create()
        {
            var menu = new NativeMenu();

            Add(menu,
                Submenu("File",
                    CommandItem("Open...", _viewModel.OpenFileCommand, Key.O),
                    _recentFilesItem,
                    Separator(),
                    TrackDocumentItem(_saveItem),
                    TrackDocumentItem(CommandItem("Save As...", _viewModel.SaveAsCommand, Key.S, KeyModifiers.Meta | KeyModifiers.Shift)),
                    TrackDocumentItem(CommandItem("Save Flattened Form Copy...", _viewModel.SaveFlattenedFormCopyCommand)),
                    Separator(),
                    TrackDocumentItem(CommandItem("Close Document", _viewModel.CloseDocumentCommand, Key.W))));

            Add(menu,
                Submenu("Edit",
                    _undoItem,
                    _redoItem,
                    Separator(),
                    TrackDocumentItem(CommandItem("Find...", _viewModel.ToggleSearchCommand, Key.F)),
                    TrackDocumentItem(CommandItem("Find Next", _viewModel.FindNextCommand, Key.F3, KeyModifiers.None)),
                    TrackDocumentItem(CommandItem("Find Previous", _viewModel.FindPreviousCommand, Key.F3, KeyModifiers.Shift)),
                    Separator(),
                    TrackDocumentItem(_selectTextItem),
                    TrackDocumentItem(_typewriterItem),
                    TrackDocumentItem(TypewriterColorSubmenu()),
                    TrackDocumentItem(_formAuthoringItem),
                    _typewriterNextEditItem,
                    _typewriterDiscardItem,
                    TrackTextSelectionItem(CommandItem("Copy Selected Text", _viewModel.CopyTextCommand, Key.C))));

            Add(menu,
                Submenu("Annotate",
                    TrackDocumentItem(CommandItem("Highlight Tool", _viewModel.ToggleHighlightModeCommand)),
                    TrackAnnotationSelectionItem(CommandItem("Add Highlight From Selection", _viewModel.AddHighlightAnnotationFromSelectionCommand)),
                    TrackDocumentItem(CommandItem("Add Sticky Note...", _viewModel.AddStickyNoteAnnotationCommand)),
                    TrackDocumentItem(CommandItem("Place Sticky Note (Click Page)", _viewModel.ToggleStickyNoteToolCommand))));

            Add(menu,
                Submenu("View",
                    TrackDocumentItem(CommandItem("Zoom In", _viewModel.ZoomInCommand, Key.OemPlus)),
                    TrackDocumentItem(CommandItem("Zoom Out", _viewModel.ZoomOutCommand, Key.OemMinus)),
                    TrackDocumentItem(CommandItem("Actual Size", _viewModel.ZoomActualSizeCommand, Key.D0)),
                    Separator(),
                    TrackDocumentItem(CommandItem("Fit Width", _viewModel.ZoomFitWidthCommand, Key.D1)),
                    TrackDocumentItem(CommandItem("Fit Page", _viewModel.ZoomFitPageCommand, Key.D2)),
                    Separator(),
                    TrackDocumentItem(_continuousScrollItem),
                    Separator(),
                    _outlineItem,
                    _thumbnailsItem,
                    _attachmentsItem,
                    _viewClipboardItem,
                    Separator(),
                    _annotationToolbarItem,
                    _annotationPaletteItem));

            Add(menu,
                Submenu("Document",
                    TrackDocumentItem(CommandItem("Add Pages...", _viewModel.AddPagesCommand)),
                    TrackDocumentItem(CommandItem("Insert Pages Before Current...", _viewModel.InsertPagesBeforeCurrentCommand)),
                    TrackDocumentItem(CommandItem("Insert Pages After Current...", _viewModel.InsertPagesAfterCurrentCommand)),
                    TrackDocumentItem(CommandItem("Extract Current Page...", _viewModel.ExtractCurrentPageCommand)),
                    TrackSelectedPageItem(CommandItem("Extract Selected Pages...", _viewModel.ExtractSelectedPagesCommand)),
                    Separator(),
                    TrackDocumentItem(CommandItem("Move Page Earlier", _viewModel.MoveCurrentPageEarlierCommand)),
                    TrackDocumentItem(CommandItem("Move Page Later", _viewModel.MoveCurrentPageLaterCommand)),
                    TrackSelectedPageMoveEarlierItem(CommandItem("Move Selected Pages Earlier", _viewModel.MoveSelectedPagesEarlierCommand)),
                    TrackSelectedPageMoveLaterItem(CommandItem("Move Selected Pages Later", _viewModel.MoveSelectedPagesLaterCommand)),
                    Separator(),
                    TrackDocumentItem(CommandItem("Remove Current Page", _viewModel.RemoveCurrentPageCommand)),
                    TrackSelectedPageRemoveItem(CommandItem("Remove Selected Pages", _viewModel.RemoveSelectedPagesCommand)),
                    TrackSelectedPageItem(CommandItem("Clear Page Selection", _viewModel.ClearSelectedPagesCommand)),
                    Separator(),
                    TrackDocumentItem(CommandItem("Rotate Left 90 degrees", _viewModel.RotatePageLeftCommand, Key.L)),
                    TrackDocumentItem(CommandItem("Rotate Right 90 degrees", _viewModel.RotatePageRightCommand, Key.R)),
                    TrackDocumentItem(CommandItem("Rotate 180 degrees", _viewModel.RotatePage180Command)),
                    Separator(),
                    TrackDocumentItem(CommandItem("Export Current Page...", _viewModel.ExportCurrentPageCommand, Key.E)),
                    TrackDocumentItem(CommandItem("Export All Pages as Images...", _viewModel.ExportPagesCommand)),
                    _printItem,
                    Separator(),
                    // #1448. Listed here deliberately, same reasoning as #1414
                    // above: on macOS the in-window menu bar is hidden
                    // (MainWindow.axaml.cs), so a command that exists only in
                    // MainWindow.axaml is unreachable on this project's
                    // primary platform. Without this, setting/changing/
                    // removing a document password is unreachable on macOS.
                    TrackDocumentItem(CommandItem("Security...", _viewModel.SecurityCommand)),
                    // #1550. Same reasoning: the in-window menu is hidden on macOS.
                    TrackDocumentItem(CommandItem("Reduce File Size...", _viewModel.ReduceFileSizeCommand))));

            Add(menu,
                Submenu("Redaction",
                    TrackDocumentItem(_redactionModeItem),
                    TrackRedactionItem(CommandItem("Apply Redaction", _viewModel.ApplyRedactionCommand, Key.Enter, KeyModifiers.None)),
                    Separator(),
                    _redactionClipboardItem));

            Add(menu,
                Submenu("Tools",
                    TrackDocumentItem(CommandItem("Verify Digital Signatures...", _viewModel.VerifySignaturesCommand)),
                    // #1414. Listed here deliberately: on macOS the in-window
                    // menu bar is hidden (MainWindow.axaml.cs), so an item that
                    // exists only in MainWindow.axaml is unreachable on this
                    // project's primary platform — which is exactly the
                    // "wired to nothing" failure this work is about.
                    TrackDocumentItem(CommandItem("Attachments", _viewModel.AttachmentsCommand)),
                    TrackDocumentItem(CommandItem("Bates Numbering...", _viewModel.BatesNumberingCommand)),
                    TrackDocumentItem(_revealHiddenTextItem),
                    TrackDocumentItem(_revealRasterizedHiddenItem),
                    // #1476: hidden early on a narrow toolbar.
                    TrackDocumentItem(CommandItem("Auto-detect Form Fields", _viewModel.AutoDetectFieldsCommand))));

            Add(menu, _windowItem);

            Add(menu,
                Submenu("Help",
                    CommandItem("Keyboard Shortcuts", _viewModel.ShowShortcutsCommand, Key.F1, KeyModifiers.None),
                    CommandItem("Documentation", _viewModel.ShowDocumentationCommand)));

            menu.NeedsUpdate += (_, _) => Refresh();
            // #1551: RecentFiles is one collection shared by every session, so
            // subscribing to it here would root this menu — and its view model —
            // for the life of the process. The view model relays collection
            // changes as RecentFileMenuItems and drops that subscription when
            // its session ends; the menu listens to the view model alone.
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            Refresh();
            return menu;
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (ShouldRefreshFor(e.PropertyName))
                QueueRefresh();
        }

        private void QueueRefresh()
        {
            if (Dispatcher.UIThread.CheckAccess())
                Refresh();
            else
                Dispatcher.UIThread.Post(Refresh, DispatcherPriority.Background);
        }

        private static bool ShouldRefreshFor(string? propertyName) =>
            propertyName is null
            or nameof(MainWindowViewModel.SaveButtonText)
            or nameof(MainWindowViewModel.CanUndo)
            or nameof(MainWindowViewModel.CanRedo)
            or nameof(MainWindowViewModel.UndoMenuHeader)
            or nameof(MainWindowViewModel.RedoMenuHeader)
            or nameof(MainWindowViewModel.IsDocumentLoaded)
            or nameof(MainWindowViewModel.CanPrint)
            or nameof(MainWindowViewModel.HasSelectedPages)
            or nameof(MainWindowViewModel.CanRemoveSelectedPages)
            or nameof(MainWindowViewModel.CanMoveSelectedPagesEarlier)
            or nameof(MainWindowViewModel.CanMoveSelectedPagesLater)
            or nameof(MainWindowViewModel.IsTextSelectionMode)
            or nameof(MainWindowViewModel.IsTypewriterMode)
            or nameof(MainWindowViewModel.IsFormAuthoringMode)
            or nameof(MainWindowViewModel.HasPendingTypewriterEdits)
            or nameof(MainWindowViewModel.HasTextSelection)
            or nameof(MainWindowViewModel.IsRedactionMode)
            or nameof(MainWindowViewModel.IsContinuousView)
            or nameof(MainWindowViewModel.IsOutlineSidebarVisible)
            or nameof(MainWindowViewModel.IsThumbnailsSidebarVisible)
            or nameof(MainWindowViewModel.IsAttachmentsSidebarVisible)
            or nameof(MainWindowViewModel.IsAnnotationToolbarVisible)
            or nameof(MainWindowViewModel.IsAnnotationPaletteVisible)
            or nameof(MainWindowViewModel.IsClipboardSidebarVisible)
            or nameof(MainWindowViewModel.RevealHiddenText)
            or nameof(MainWindowViewModel.RevealRasterizedHidden)
            or nameof(MainWindowViewModel.OpenDocuments)
            or nameof(MainWindowViewModel.RecentFiles)
            or nameof(MainWindowViewModel.RecentFileMenuItems);

        public void Refresh()
        {
            _saveItem.Header = _viewModel.SaveButtonText;
            _undoItem.Header = _viewModel.UndoMenuHeader.TrimStart('_');
            _undoItem.IsEnabled = _viewModel.CanUndo;
            _redoItem.Header = _viewModel.RedoMenuHeader.TrimStart('_');
            _redoItem.IsEnabled = _viewModel.CanRedo;

            var isDocumentLoaded = _viewModel.IsDocumentLoaded;
            foreach (var item in _documentItems)
                item.IsEnabled = isDocumentLoaded;
            _printItem.IsEnabled = _viewModel.CanPrint;
            _printItem.ToolTip = _viewModel.PrintDisabledReason;
            foreach (var item in _selectedPageItems)
                item.IsEnabled = isDocumentLoaded && _viewModel.HasSelectedPages;
            foreach (var item in _selectedPageRemoveItems)
                item.IsEnabled = isDocumentLoaded && _viewModel.CanRemoveSelectedPages;
            foreach (var item in _selectedPageMoveEarlierItems)
                item.IsEnabled = isDocumentLoaded && _viewModel.CanMoveSelectedPagesEarlier;
            foreach (var item in _selectedPageMoveLaterItems)
                item.IsEnabled = isDocumentLoaded && _viewModel.CanMoveSelectedPagesLater;
            foreach (var item in _textSelectionItems)
                item.IsEnabled = isDocumentLoaded && _viewModel.IsTextSelectionMode;
            foreach (var item in _annotationSelectionItems)
                item.IsEnabled = isDocumentLoaded && _viewModel.HasTextSelection;
            foreach (var item in _redactionItems)
                item.IsEnabled = isDocumentLoaded && _viewModel.IsRedactionMode;
            _selectTextItem.ToggleType = MenuItemToggleType.CheckBox;
            _selectTextItem.IsChecked = _viewModel.IsTextSelectionMode;
            _typewriterItem.ToggleType = MenuItemToggleType.CheckBox;
            _typewriterItem.IsChecked = _viewModel.IsTypewriterMode;
            _formAuthoringItem.ToggleType = MenuItemToggleType.CheckBox;
            _formAuthoringItem.IsChecked = _viewModel.IsFormAuthoringMode;
            _typewriterNextEditItem.IsEnabled = isDocumentLoaded && _viewModel.HasPendingTypewriterEdits;
            _typewriterDiscardItem.IsEnabled = isDocumentLoaded && _viewModel.HasPendingTypewriterEdits;
            _redactionModeItem.ToggleType = MenuItemToggleType.CheckBox;
            _redactionModeItem.IsChecked = _viewModel.IsRedactionMode;
            _continuousScrollItem.ToggleType = MenuItemToggleType.CheckBox;
            _continuousScrollItem.IsChecked = _viewModel.IsContinuousView;
            _outlineItem.IsChecked = _viewModel.IsOutlineSidebarVisible;
            _thumbnailsItem.IsChecked = _viewModel.IsThumbnailsSidebarVisible;
            _attachmentsItem.IsChecked = _viewModel.IsAttachmentsSidebarVisible;
            _annotationToolbarItem.IsChecked = _viewModel.IsAnnotationToolbarVisible;
            _annotationPaletteItem.IsChecked = _viewModel.IsAnnotationPaletteVisible;
            _viewClipboardItem.IsChecked = _viewModel.IsClipboardSidebarVisible;
            _redactionClipboardItem.IsChecked = _viewModel.IsClipboardSidebarVisible;
            _revealHiddenTextItem.IsChecked = _viewModel.RevealHiddenText;
            _revealRasterizedHiddenItem.IsChecked = _viewModel.RevealRasterizedHidden;

            RefreshRecentFiles();
            RefreshWindowMenu();
        }

        /// <summary>
        /// #1553: the Window menu's document list, rebuilt only when the list,
        /// a name or an unsaved marker changed.
        /// </summary>
        private void RefreshWindowMenu()
        {
            var documents = _viewModel.OpenDocuments;
            foreach (var item in _tabActionItems)
                item.IsEnabled = documents.Count > 1;
            // #1598: the tab-switch items take their enabled state from their
            // command, because that is what a native key equivalent is
            // validated against at the moment the key is pressed. Setting
            // IsEnabled here as well would fight Avalonia, which writes it from
            // Command.CanExecute on every CanExecuteChanged.
            foreach (var command in _documentTabCommands)
                command.RaiseCanExecuteChanged();

            var snapshot = string.Join('\n', documents.Select(d =>
                $"{d.Title}|{d.FilePath}|{d.IsCurrent}|{d.HasUnsavedChanges}"));
            if (snapshot == _openDocumentsSnapshot)
                return;
            _openDocumentsSnapshot = snapshot;

            var windowMenu = _windowItem.Menu ??= new NativeMenu();
            // The tab actions and their separators stay; the document entries
            // after them are replaced.
            var fixedCount = _windowFixedItemCount;
            while (windowMenu.Items.Count > fixedCount)
                windowMenu.Items.RemoveAt(windowMenu.Items.Count - 1);

            if (documents.Count == 0)
                return;

            Add(windowMenu, Separator());
            foreach (var document in documents)
            {
                Add(windowMenu, new NativeMenuItem(MainWindowViewModel.OpenDocumentMenuHeader(document))
                {
                    ToolTip = document.FilePath,
                    Command = _viewModel.ActivateOpenDocumentCommand,
                    CommandParameter = document,
                    ToggleType = MenuItemToggleType.CheckBox,
                    IsChecked = document.IsCurrent,
                });
            }
        }

        /// <summary>
        /// One of this window's in-app document tabs (#1598, #1554), with the
        /// key equivalent Safari uses. The command's CanExecute is live, so the
        /// item is enabled — and its key equivalent therefore active — exactly
        /// while the window has more than one tab.
        /// </summary>
        private NativeMenuItem DocumentTabItem(string header, int step, KeyGesture gesture)
        {
            var command = new DocumentTabSwitchCommand(_viewModel, step);
            _documentTabCommands.Add(command);
            return new NativeMenuItem(header) { Command = command, Gesture = gesture };
        }

        /// <summary>
        /// Show the next or previous document tab of the window showing this
        /// session (#1598). Not a <c>ReactiveCommand</c> on the view model
        /// because its enabled state is a property of the WINDOW, which the
        /// session does not own and can move between.
        /// </summary>
        private sealed class DocumentTabSwitchCommand(MainWindowViewModel viewModel, int step) : ICommand
        {
            public event System.EventHandler? CanExecuteChanged;

            public bool CanExecute(object? parameter) => viewModel.CanSwitchTabs;

            public void Execute(object? parameter) => viewModel.SwitchTab(step);

            public void RaiseCanExecuteChanged() =>
                CanExecuteChanged?.Invoke(this, System.EventArgs.Empty);
        }

        private NativeMenuItem TabActionItem(string header, Workspace.MacWindowTabbing.TabAction action)
        {
            var item = new NativeMenuItem(header) { Command = new TabActionCommand(action) };
            _tabActionItems.Add(item);
            return item;
        }

        /// <summary>
        /// An AppKit tab action as a command, so the native menu keeps its
        /// "every leaf has a command" contract (CommandBindingSweepTests).
        /// </summary>
        private sealed class TabActionCommand(Workspace.MacWindowTabbing.TabAction action) : ICommand
        {
            public event System.EventHandler? CanExecuteChanged
            {
                add { }
                remove { }
            }

            public bool CanExecute(object? parameter) => true;

            public void Execute(object? parameter) =>
                Workspace.MacWindowTabbing.Perform(action, logger: null);
        }

        private void RefreshRecentFiles()
        {
            var recentMenu = _recentFilesItem.Menu ??= new NativeMenu();
            var snapshot = _viewModel.RecentFiles.ToArray();
            if (_recentFilesSnapshot != null
                && snapshot.SequenceEqual(_recentFilesSnapshot)
                && recentMenu.Items.Count > 0)
            {
                return;
            }

            recentMenu.Items.Clear();
            _recentFilesSnapshot = snapshot;

            if (snapshot.Length == 0)
            {
                Add(recentMenu, new NativeMenuItem("No Recent Files") { IsEnabled = false });
                return;
            }

            foreach (var path in snapshot)
            {
                Add(recentMenu, new NativeMenuItem(System.IO.Path.GetFileName(path))
                {
                    ToolTip = path,
                    Command = _viewModel.LoadRecentFileCommand,
                    CommandParameter = path
                });
            }
        }

        private NativeMenuItem TrackDocumentItem(NativeMenuItem item)
        {
            _documentItems.Add(item);
            return item;
        }

        private NativeMenuItem TrackSelectedPageItem(NativeMenuItem item)
        {
            _selectedPageItems.Add(item);
            return item;
        }

        private NativeMenuItem TrackSelectedPageRemoveItem(NativeMenuItem item)
        {
            _selectedPageRemoveItems.Add(item);
            return item;
        }

        private NativeMenuItem TrackSelectedPageMoveEarlierItem(NativeMenuItem item)
        {
            _selectedPageMoveEarlierItems.Add(item);
            return item;
        }

        private NativeMenuItem TrackSelectedPageMoveLaterItem(NativeMenuItem item)
        {
            _selectedPageMoveLaterItems.Add(item);
            return item;
        }

        private NativeMenuItem TrackTextSelectionItem(NativeMenuItem item)
        {
            _textSelectionItems.Add(item);
            return item;
        }

        private NativeMenuItem TrackAnnotationSelectionItem(NativeMenuItem item)
        {
            _annotationSelectionItems.Add(item);
            return item;
        }

        private NativeMenuItem TrackRedactionItem(NativeMenuItem item)
        {
            _redactionItems.Add(item);
            return item;
        }

        private static NativeMenuItem CommandItem(
            string header,
            System.Windows.Input.ICommand? command,
            Key? key = null,
            KeyModifiers modifiers = KeyModifiers.Meta)
        {
            var item = new NativeMenuItem(header)
            {
                Command = command,
                IsEnabled = command != null
            };

            if (key.HasValue)
                item.Gesture = new KeyGesture(key.Value, modifiers);

            return item;
        }

        /// <summary>
        /// #1476: Edit &gt; Typewriter Text Color. Each preset is tracked as a
        /// document item, matching MainWindow.axaml's IsDocumentLoaded gate; with
        /// no active box the colour applies to the next box drawn.
        /// </summary>
        private NativeMenuItem TypewriterColorSubmenu() =>
            Submenu("Typewriter Text Color",
                MacNativeMenuBuilder.TypewriterColorPresets
                    .Select(preset => TrackDocumentItem(
                        ParameterItem(preset.Name, _viewModel.SetTypewriterColorCommand, preset.Hex)))
                    .ToArray());

        private static NativeMenuItem ParameterItem(string header, ICommand? command, object parameter) =>
            new(header)
            {
                Command = command,
                CommandParameter = parameter,
                IsEnabled = command != null,
            };

        private static NativeMenuItem ToggleItem(
            string header,
            ICommand command,
            Key? key = null,
            KeyModifiers modifiers = KeyModifiers.Meta)
        {
            var item = new NativeMenuItem(header)
            {
                ToggleType = MenuItemToggleType.CheckBox,
                Command = command
            };
            if (key.HasValue)
                item.Gesture = new KeyGesture(key.Value, modifiers);
            return item;
        }

        private static NativeMenuItem Submenu(string header, params NativeMenuItem[] items)
        {
            var item = new NativeMenuItem(header)
            {
                Menu = new NativeMenu()
            };
            foreach (var child in items)
                Add(item.Menu, child);
            return item;
        }

        private static NativeMenuItem Separator() => new NativeMenuItemSeparator();

        private static void Add(NativeMenu menu, NativeMenuItem item) =>
            menu.Items.Add(item);
    }
}
