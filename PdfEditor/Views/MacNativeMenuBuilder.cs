using Avalonia.Controls;
using Avalonia.Input;
using PdfEditor.ViewModels;
using System;
using System.Collections.Generic;

namespace PdfEditor.Views;

internal static class MacNativeMenuBuilder
{
    public static NativeMenu Create(MainWindowViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        var state = new MenuState(viewModel);
        return state.Create();
    }

    private sealed class MenuState
    {
        private readonly MainWindowViewModel _viewModel;
        private readonly List<NativeMenuItem> _documentItems = new();
        private readonly List<NativeMenuItem> _textSelectionItems = new();
        private readonly List<NativeMenuItem> _redactionItems = new();
        private readonly NativeMenuItem _saveItem;
        private readonly NativeMenuItem _recentFilesItem;
        private readonly NativeMenuItem _selectTextItem;
        private readonly NativeMenuItem _typewriterItem;
        private readonly NativeMenuItem _redactionModeItem;
        private readonly NativeMenuItem _viewClipboardItem;
        private readonly NativeMenuItem _redactionClipboardItem;
        private readonly NativeMenuItem _continuousScrollItem;
        private readonly NativeMenuItem _outlineItem;
        private readonly NativeMenuItem _thumbnailsItem;
        private readonly NativeMenuItem _revealHiddenTextItem;
        private readonly NativeMenuItem _revealRasterizedHiddenItem;

        public MenuState(MainWindowViewModel viewModel)
        {
            _viewModel = viewModel;
            _saveItem = CommandItem("Save", _viewModel.SaveFileCommand, Key.S);
            _recentFilesItem = Submenu("Open Recent");
            _selectTextItem = CommandItem("Select Text Mode", _viewModel.ToggleTextSelectionModeCommand, Key.T);
            _typewriterItem = CommandItem("Typewriter Mode", _viewModel.ToggleTypewriterModeCommand);
            _redactionModeItem = CommandItem("Redaction Mode", _viewModel.ToggleRedactionModeCommand, Key.R, modifiers: KeyModifiers.None);
            _viewClipboardItem = ToggleItem("Show Clipboard History", () =>
                _viewModel.IsClipboardSidebarVisible = !_viewModel.IsClipboardSidebarVisible);
            _redactionClipboardItem = ToggleItem("Show Clipboard History", () =>
                _viewModel.IsClipboardSidebarVisible = !_viewModel.IsClipboardSidebarVisible);
            _continuousScrollItem = CommandItem("Continuous Scroll", _viewModel.ToggleContinuousViewCommand, Key.C, KeyModifiers.Meta | KeyModifiers.Shift);
            _outlineItem = ToggleItem("Show Outline", () =>
                _viewModel.IsOutlineSidebarVisible = !_viewModel.IsOutlineSidebarVisible, Key.O, KeyModifiers.Meta | KeyModifiers.Shift);
            _thumbnailsItem = ToggleItem("Show Thumbnails", () =>
                _viewModel.IsThumbnailsSidebarVisible = !_viewModel.IsThumbnailsSidebarVisible, Key.T, KeyModifiers.Meta | KeyModifiers.Shift);
            _revealHiddenTextItem = ToggleItem("Reveal Hidden Text", () =>
                _viewModel.RevealHiddenText = !_viewModel.RevealHiddenText);
            _revealRasterizedHiddenItem = ToggleItem("Reveal Rasterized Hidden Text", () =>
                _viewModel.RevealRasterizedHidden = !_viewModel.RevealRasterizedHidden);
        }

        public NativeMenu Create()
        {
            var menu = new NativeMenu();

            Add(menu,
                Submenu("PDF Editor",
                    CommandItem("About PDF Editor", _viewModel.AboutCommand),
                    Separator(),
                    CommandItem("Preferences...", _viewModel.ShowPreferencesCommand, Key.OemComma),
                    Separator(),
                    CommandItem("Quit PDF Editor", _viewModel.ExitCommand, Key.Q)));

            Add(menu,
                Submenu("File",
                    CommandItem("Open...", _viewModel.OpenFileCommand, Key.O),
                    _recentFilesItem,
                    Separator(),
                    TrackDocumentItem(_saveItem),
                    TrackDocumentItem(CommandItem("Save As...", _viewModel.SaveAsCommand, Key.S, KeyModifiers.Meta | KeyModifiers.Shift)),
                    Separator(),
                    TrackDocumentItem(CommandItem("Close Document", _viewModel.CloseDocumentCommand, Key.W))));

            Add(menu,
                Submenu("Edit",
                    TrackDocumentItem(CommandItem("Find...", _viewModel.ToggleSearchCommand, Key.F)),
                    TrackDocumentItem(CommandItem("Find Next", _viewModel.FindNextCommand, Key.F3, KeyModifiers.None)),
                    TrackDocumentItem(CommandItem("Find Previous", _viewModel.FindPreviousCommand, Key.F3, KeyModifiers.Shift)),
                    Separator(),
                    TrackDocumentItem(_selectTextItem),
                    TrackDocumentItem(_typewriterItem),
                    TrackTextSelectionItem(CommandItem("Copy Selected Text", _viewModel.CopyTextCommand, Key.C))));

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
                    _viewClipboardItem));

            Add(menu,
                Submenu("Document",
                    TrackDocumentItem(CommandItem("Add Pages...", _viewModel.AddPagesCommand)),
                    TrackDocumentItem(CommandItem("Remove Current Page", _viewModel.RemoveCurrentPageCommand)),
                    Separator(),
                    TrackDocumentItem(CommandItem("Rotate Left 90 degrees", _viewModel.RotatePageLeftCommand, Key.L)),
                    TrackDocumentItem(CommandItem("Rotate Right 90 degrees", _viewModel.RotatePageRightCommand, Key.R)),
                    TrackDocumentItem(CommandItem("Rotate 180 degrees", _viewModel.RotatePage180Command)),
                    Separator(),
                    TrackDocumentItem(CommandItem("Export Current Page...", _viewModel.ExportCurrentPageCommand, Key.E)),
                    TrackDocumentItem(CommandItem("Export All Pages as Images...", _viewModel.ExportPagesCommand)),
                    TrackDocumentItem(CommandItem("Print...", _viewModel.PrintCommand, Key.P))));

            Add(menu,
                Submenu("Redaction",
                    TrackDocumentItem(_redactionModeItem),
                    TrackRedactionItem(CommandItem("Apply Redaction", _viewModel.ApplyRedactionCommand, Key.Enter, KeyModifiers.None)),
                    Separator(),
                    _redactionClipboardItem));

            Add(menu,
                Submenu("Tools",
                    TrackDocumentItem(CommandItem("Verify Digital Signatures...", _viewModel.VerifySignaturesCommand)),
                    TrackDocumentItem(_revealHiddenTextItem),
                    TrackDocumentItem(_revealRasterizedHiddenItem)));

            Add(menu,
                Submenu("Help",
                    CommandItem("Keyboard Shortcuts", _viewModel.ShowShortcutsCommand, Key.F1, KeyModifiers.None),
                    CommandItem("Documentation", _viewModel.ShowDocumentationCommand)));

            menu.NeedsUpdate += (_, _) => Refresh();
            Refresh();
            return menu;
        }

        private void Refresh()
        {
            _saveItem.Header = _viewModel.SaveButtonText;

            var isDocumentLoaded = _viewModel.IsDocumentLoaded;
            foreach (var item in _documentItems)
                item.IsEnabled = isDocumentLoaded;
            foreach (var item in _textSelectionItems)
                item.IsEnabled = isDocumentLoaded && _viewModel.IsTextSelectionMode;
            foreach (var item in _redactionItems)
                item.IsEnabled = isDocumentLoaded && _viewModel.IsRedactionMode;
            _selectTextItem.ToggleType = MenuItemToggleType.CheckBox;
            _selectTextItem.IsChecked = _viewModel.IsTextSelectionMode;
            _typewriterItem.ToggleType = MenuItemToggleType.CheckBox;
            _typewriterItem.IsChecked = _viewModel.IsTypewriterMode;
            _redactionModeItem.ToggleType = MenuItemToggleType.CheckBox;
            _redactionModeItem.IsChecked = _viewModel.IsRedactionMode;
            _continuousScrollItem.ToggleType = MenuItemToggleType.CheckBox;
            _continuousScrollItem.IsChecked = _viewModel.IsContinuousView;
            _outlineItem.IsChecked = _viewModel.IsOutlineSidebarVisible;
            _thumbnailsItem.IsChecked = _viewModel.IsThumbnailsSidebarVisible;
            _viewClipboardItem.IsChecked = _viewModel.IsClipboardSidebarVisible;
            _redactionClipboardItem.IsChecked = _viewModel.IsClipboardSidebarVisible;
            _revealHiddenTextItem.IsChecked = _viewModel.RevealHiddenText;
            _revealRasterizedHiddenItem.IsChecked = _viewModel.RevealRasterizedHidden;

            RefreshRecentFiles();
        }

        private void RefreshRecentFiles()
        {
            var recentMenu = _recentFilesItem.Menu ??= new NativeMenu();
            recentMenu.Items.Clear();

            if (_viewModel.RecentFiles.Count == 0)
            {
                Add(recentMenu, new NativeMenuItem("No Recent Files") { IsEnabled = false });
                return;
            }

            foreach (var path in _viewModel.RecentFiles)
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

        private NativeMenuItem TrackTextSelectionItem(NativeMenuItem item)
        {
            _textSelectionItems.Add(item);
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

        private static NativeMenuItem ToggleItem(
            string header,
            Action toggle,
            Key? key = null,
            KeyModifiers modifiers = KeyModifiers.Meta)
        {
            var item = new NativeMenuItem(header)
            {
                ToggleType = MenuItemToggleType.CheckBox
            };
            if (key.HasValue)
                item.Gesture = new KeyGesture(key.Value, modifiers);
            item.Click += (_, _) => toggle();
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
