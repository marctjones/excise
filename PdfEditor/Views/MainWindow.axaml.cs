using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Pdfe.Avalonia.Controls;
using Pdfe.Core.Document;
using PdfEditor.Models;
using PdfEditor.ViewModels;
using System;
using System.Collections.Specialized;
using System.Linq;

namespace PdfEditor.Views;

public partial class MainWindow : Window
{
    private PdfViewerControl? _pdfViewerControl;
    private readonly WindowSettings _windowSettings;
    private object? _nativeMenuDataContext;
    private int? _draggedThumbnailPageIndex;

    // Single reusable UI-thread timer for toast auto-dismiss. A DispatcherTimer
    // (vs System.Timers.Timer) ticks on the dispatcher itself, so it stops when
    // the dispatcher stops and never marshals a callback (Dispatcher.InvokeAsync)
    // into a torn-down/foreign dispatcher. The old per-toast wall-clock timer
    // posted its dismiss continuation 5s later from a ThreadPool thread — often
    // after the owning test had finished — which deadlocked the headless
    // dispatcher under --blame-hang-timeout and made GUI tests (e.g.
    // CtrlS_SavesFile) flaky. See the KeyboardShortcutTests quarantine.
    private DispatcherTimer? _toastTimer;

    public MainWindow()
    {
        InitializeComponent();

        if (OperatingSystem.IsMacOS())
        {
            MainMenuBar.IsVisible = false;
            TitleBarAppLabel.Margin = new Thickness(86, 0, 10, 0);
        }

        // Load and apply window settings (Issue #23)
        _windowSettings = WindowSettings.Load();
        _windowSettings.ApplyTo(this);

        // Save settings on close
        this.Closing += (s, e) =>
        {
            _windowSettings.CaptureFrom(this);
            _windowSettings.Save();
            // Cancel any pending toast auto-dismiss so nothing is left queued on
            // the dispatcher when the window/test tears down.
            _toastTimer?.Stop();
        };

        // Add keyboard handler for Ctrl+C
        this.KeyDown += MainWindow_KeyDown;

        // Subscribe to search highlights changes
        this.DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        // Get reference to PdfViewerControl
        _pdfViewerControl ??= this.FindControl<PdfViewerControl>("PdfViewerControl");

        if (DataContext is MainWindowViewModel viewModel)
        {
            ConfigurePlatformMenu(viewModel);

            // Subscribe to toast notifications
            viewModel.ToastService.ToastRequested += OnToastRequested;

            // Subscribe to search highlights collection changes
            viewModel.CurrentPageSearchHighlights.CollectionChanged += OnSearchHighlightsChanged;

            // Subscribe to redaction collection changes
            viewModel.RedactionWorkflow.PendingRedactions.CollectionChanged += OnRedactionsChanged;
            viewModel.RedactionWorkflow.AppliedRedactions.CollectionChanged += OnRedactionsChanged;
            viewModel.AnnotationsChanged += OnAnnotationsChanged;

            // Subscribe to page changes to update redaction overlays
            viewModel.PropertyChanged += (s, args) =>
            {
                if (args.PropertyName == nameof(viewModel.CurrentPageIndex))
                {
                    UpdateRedactionOverlays();
                }
            };

            // Push the viewer's *visible* viewport (inside-the-scrollbars)
            // into the VM so Fit Width / Fit Page fit against what the user
            // actually sees, not the outer control bounds. Using outer
            // bounds gave a result ~16-20 DIPs too big — exactly the strip
            // a vertical scrollbar reserves — which made Fit Width pop a
            // horizontal scrollbar that then stole more space and broke
            // the fit recursively.
            if (_pdfViewerControl != null)
            {
                var initial = _pdfViewerControl.GetVisibleViewportSize();
                viewModel.ViewportWidth = initial.Width;
                viewModel.ViewportHeight = initial.Height;
                _pdfViewerControl.VisibleViewportChanged += (s, size) =>
                {
                    viewModel.ViewportWidth = size.Width;
                    viewModel.ViewportHeight = size.Height;
                };
            }
        }
    }

    private void ConfigurePlatformMenu(MainWindowViewModel viewModel)
    {
        if (!OperatingSystem.IsMacOS() || ReferenceEquals(_nativeMenuDataContext, viewModel))
        {
            return;
        }

        NativeMenu.SetMenu(this, MacNativeMenuBuilder.Create(viewModel));
        _nativeMenuDataContext = viewModel;
    }

    private void OnSearchHighlightsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateSearchHighlightsCanvas();
    }

    /// <summary>
    /// Theme-independent click handler for the outline TreeView. Walks
    /// up from the click target to find the nearest TreeViewItem and
    /// invokes JumpToOutline on its OutlineNode DataContext. Avoids the
    /// FluentAvalonia-specific chevron/content hit-test boundary that
    /// makes the SelectedItem path silently swallow clicks.
    /// </summary>
    private void OnOutlineTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var src = e.Source as Control;
        while (src != null)
        {
            if (src is Avalonia.Controls.TreeViewItem tvi &&
                tvi.DataContext is PdfEditor.Models.OutlineNode node)
            {
                vm.JumpToOutline(node);
                return;
            }
            src = src.Parent as Control;
        }
    }

    /// <summary>
    /// Fires whenever a thumbnail Image's effective visible area changes
    /// (item virtualisation, scrolling the strip, sidebar toggling). When
    /// the area is non-empty we ask the VM to ensure that page's thumbnail
    /// is loaded — VM dedupes already-loaded pages and coalesces concurrent
    /// requests, so we can call this freely.
    /// </summary>
    private void OnThumbnailViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        if (sender is not Image img) return;
        if (DataContext is not MainWindowViewModel vm) return;
        if (img.Tag is not int pageIndex) return;
        if (e.EffectiveViewport.Width <= 0 || e.EffectiveViewport.Height <= 0) return;

        // Fire-and-forget — VM handles its own dispatching to the UI thread.
        _ = vm.EnsureThumbnailLoadedAsync(pageIndex);
    }

    private void OnThumbnailPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is CheckBox)
            return;
        if (sender is not Control { DataContext: PageThumbnail thumbnail })
            return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        _draggedThumbnailPageIndex = thumbnail.PageIndex;
    }

    private void OnThumbnailPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_draggedThumbnailPageIndex is not int fromIndex)
            return;

        _draggedThumbnailPageIndex = null;

        if (sender is not Control { DataContext: PageThumbnail thumbnail })
            return;
        if (DataContext is not MainWindowViewModel vm)
            return;

        var toIndex = thumbnail.PageIndex;
        if (fromIndex == toIndex)
            return;

        e.Handled = true;
        _ = vm.MovePageAsync(fromIndex, toIndex);
    }

    /// <summary>
    /// Enter inside the search box triggers an immediate search (skips
    /// the debounce). Escape closes the search bar.
    /// </summary>
    private void OnSearchTextBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        if (e.Key == Key.Enter)
        {
            vm.FindCommand?.Execute().Subscribe();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            vm.CloseSearchCommand?.Execute().Subscribe();
            e.Handled = true;
        }
    }

    private void UpdateSearchHighlightsCanvas()
    {
        if (_pdfViewerControl == null)
            return;

        _pdfViewerControl.ClearSearchHighlights();

        if (DataContext is not MainWindowViewModel viewModel)
            return;

        foreach (var rect in viewModel.CurrentPageSearchHighlights)
        {
            _pdfViewerControl.AddSearchHighlight(rect);
        }
    }

    private void OnRedactionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateRedactionOverlays();
    }

    private void OnAnnotationsChanged(object? sender, EventArgs e)
    {
        _pdfViewerControl ??= this.FindControl<PdfViewerControl>("PdfViewerControl");
        if (_pdfViewerControl?.Document == null ||
            _pdfViewerControl.CurrentPage < 1 ||
            _pdfViewerControl.CurrentPage > _pdfViewerControl.Document.PageCount)
        {
            if (_pdfViewerControl != null)
                _pdfViewerControl.Annotations = null;
            return;
        }

        _pdfViewerControl.Annotations = _pdfViewerControl.Document
            .GetPage(_pdfViewerControl.CurrentPage)
            .GetAnnotations();
    }

    private void UpdateRedactionOverlays()
    {
        if (_pdfViewerControl == null)
            return;

        _pdfViewerControl.ClearPendingRedactions();
        _pdfViewerControl.ClearAppliedRedactions();

        if (DataContext is not MainWindowViewModel viewModel)
            return;

        var currentPage = viewModel.CurrentPageIndex + 1; // DisplayPageNumber is 1-based

        // Draw pending redactions (red dashed border)
        foreach (var pending in viewModel.RedactionWorkflow.GetPendingForPage(currentPage))
        {
            _pdfViewerControl.AddPendingRedaction(pending.PageArea);
        }

        // Draw applied redactions (black solid rectangle)
        foreach (var applied in viewModel.RedactionWorkflow.GetAppliedForPage(currentPage))
        {
            _pdfViewerControl.AddAppliedRedaction(applied.PageArea);
        }
    }

    private void MainWindow_KeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
            return;

        // Sidebar toggles. These Ctrl+Shift combos are checked FIRST, before the
        // plain Ctrl+O handler below (which doesn't exclude Shift and would
        // otherwise swallow Ctrl+Shift+O). (#369)
        // Ctrl+Shift+O: toggle the outline / bookmarks sidebar
        if (e.Key == Key.O && e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            viewModel.ToggleOutlineCommand?.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        // Ctrl+Shift+T: toggle the page-previews / thumbnails sidebar
        if (e.Key == Key.T && e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            viewModel.ToggleThumbnailsCommand?.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        // Ctrl+O: Open file
        if (e.Key == Key.O && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            viewModel.OpenFileCommand?.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        // Ctrl+S: Save file
        if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            viewModel.SaveFileCommand?.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        // Ctrl+Shift+S: Save As
        if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            viewModel.SaveAsCommand?.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        // Ctrl+W: Close document
        if (e.Key == Key.W && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            viewModel.CloseDocumentCommand?.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        // Ctrl+P: Print
        if (e.Key == Key.P && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            viewModel.PrintCommand?.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        // F1: Show keyboard shortcuts
        if (e.Key == Key.F1)
        {
            viewModel.ShowShortcutsCommand?.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        // Ctrl+F: Toggle search bar (and put focus in the input so the
        // user can type immediately). Without the focus hop the search
        // bar appears but keystrokes go to whatever was focused before
        // — which looks like "search doesn't do anything".
        if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            viewModel.ToggleSearchCommand?.Execute().Subscribe();
            if (viewModel.IsSearchVisible)
            {
                var searchBox = this.FindControl<TextBox>("SearchTextBox");
                // The Border that hosts the TextBox just toggled
                // IsVisible — wait for the layout pass to finish before
                // we try to focus it.
                Dispatcher.UIThread.Post(() =>
                {
                    searchBox?.Focus();
                    searchBox?.SelectAll();
                }, DispatcherPriority.Background);
            }
            e.Handled = true;
            return;
        }

        // F3: Find next
        if (e.Key == Key.F3 && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            viewModel.FindNextCommand?.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        // Shift+F3: Find previous
        if (e.Key == Key.F3 && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            viewModel.FindPreviousCommand?.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        // Escape: Close search if visible
        if (e.Key == Key.Escape && viewModel.IsSearchVisible)
        {
            viewModel.CloseSearchCommand?.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        // Ctrl+L: Rotate page left
        if (e.Key == Key.L && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            viewModel.RotatePageLeftCommand?.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        // R (unmodified): Toggle redaction mode (B2 keyboard shortcut)
        if (e.Key == Key.R && !e.KeyModifiers.HasFlag(KeyModifiers.Control) &&
            !e.KeyModifiers.HasFlag(KeyModifiers.Shift) && !e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            viewModel.ToggleRedactionModeCommand?.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        // Ctrl+R: Rotate page right
        if (e.Key == Key.R && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            viewModel.RotatePageRightCommand?.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        // Ctrl+0: Actual size (100%)
        if (e.Key == Key.D0 && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            viewModel.ZoomActualSizeCommand?.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        // Ctrl+1: Fit width
        if (e.Key == Key.D1 && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            viewModel.ZoomFitWidthCommand?.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        // Ctrl+2: Fit page
        if (e.Key == Key.D2 && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            viewModel.ZoomFitPageCommand?.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        // Ctrl++: Zoom in
        if ((e.Key == Key.OemPlus || e.Key == Key.Add) && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            viewModel.ZoomInCommand?.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        // Ctrl+-: Zoom out
        if ((e.Key == Key.OemMinus || e.Key == Key.Subtract) && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            viewModel.ZoomOutCommand?.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        // T (unmodified): Toggle text selection mode
        if (e.Key == Key.T && !e.KeyModifiers.HasFlag(KeyModifiers.Control) &&
            !e.KeyModifiers.HasFlag(KeyModifiers.Shift) && !e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            // Skip if TextBox is focused (e.g., search box)
            var focusedElement = FocusManager.GetFocusedElement();
            if (focusedElement is TextBox)
                return;

            viewModel.ToggleTextSelectionModeCommand?.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        // Page Down / Down Arrow: Next page
        if (e.Key == Key.PageDown || (e.Key == Key.Down && !e.KeyModifiers.HasFlag(KeyModifiers.Control)))
        {
            viewModel.NextPageCommand?.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        // Page Up / Up Arrow: Previous page
        if (e.Key == Key.PageUp || (e.Key == Key.Up && !e.KeyModifiers.HasFlag(KeyModifiers.Control)))
        {
            viewModel.PreviousPageCommand?.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        // Home: First page
        if (e.Key == Key.Home)
        {
            viewModel.GoToPageCommand?.Execute(0).Subscribe();
            e.Handled = true;
            return;
        }

        // End: Last page
        if (e.Key == Key.End)
        {
            var lastPage = viewModel.TotalPages - 1;
            if (lastPage >= 0)
            {
                viewModel.GoToPageCommand?.Execute(lastPage).Subscribe();
            }
            e.Handled = true;
            return;
        }

        // Ctrl+C: Copy text
        if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (viewModel.IsTextSelectionMode)
            {
                viewModel.CopyTextCommand.Execute().Subscribe();
                e.Handled = true;
            }
        }
    }

    // ==================================================================================
    // PDF VIEWER CONTROL EVENT HANDLERS
    // ==================================================================================

    private void OnRedactionDrawn(object? sender, RedactionDrawnEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
            return;

        // The PdfViewerControl provides a page-scoped, tagged rectangle.
        // The view model backfills legacy Rect/DPI properties from this value.
        viewModel.CurrentRedactionPageArea = e.PageArea;

        // Automatically apply the redaction when selection is completed
        if (e.PageArea.Width > 5 && e.PageArea.Height > 5)
        {
            viewModel.ApplyRedactionCommand.Execute().Subscribe();
        }
    }

    /// <summary>
    /// Internal link clicked in the page area. The destination page is
    /// 1-based; the VM tracks 0-based CurrentPageIndex.
    /// </summary>
    private void OnLinkClicked(object? sender, LinkClickedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel) return;
        var idx = e.PageNumber - 1;
        if (idx < 0 || idx >= viewModel.TotalPages) return;
        viewModel.CurrentPageIndex = idx;
    }

    private void OnTextSelected(object? sender, TextSelectedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
            return;

        // Pre-fix this set CurrentTextSelectionArea (a 2D rect) and asked
        // the VM to re-extract text within the rect. The new text-line
        // selection path computes the actual text in the control via
        // letter hit-testing, so the event already carries the joined
        // string — feed it directly.
        viewModel.CurrentTextSelectionArea = e.Area;
        viewModel.CurrentTextSelectionPageArea =
            e.Area.Width > 0 && e.Area.Height > 0
                ? PdfPageRect.ViewerDips(
                    viewModel.CurrentPage,
                    e.Area.X,
                    e.Area.Y,
                    e.Area.Width,
                    e.Area.Height,
                    MainWindowViewModel.DefaultViewerRenderDpi)
                : null;
        if (!string.IsNullOrEmpty(e.Text))
        {
            _ = viewModel.SetSelectedTextAndCopyAsync(e.Text);
        }
        else
        {
            viewModel.SelectedText = string.Empty;
        }
    }

    private void OnPageChanged(object? sender, PageChangedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
            return;

        // Update ViewModel page index (convert from 1-based to 0-based)
        viewModel.CurrentPageIndex = e.PageNumber - 1;
    }

    /// <summary>
    /// Handle an AcroForm field edit. The viewer has already mutated the
    /// PdfField. We tell the VM so it can mark the document dirty and
    /// re-render the page so the appearance reflects the new value (the
    /// form field overlay sits on top, but if /NeedAppearances is honored
    /// by the renderer the bitmap underneath will refresh too).
    /// </summary>
    private void OnFormFieldEdited(object? sender, FormFieldEditedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel) return;
        viewModel.OnFormFieldEdited(e.FieldName, e.NewValue);
    }

    /// <summary>
    /// User finished drag-defining a new form-field rect in authoring mode.
    /// </summary>
    private void OnFormFieldRectDrawn(object? sender, FormFieldRectDrawnEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel) return;
        viewModel.OnFormFieldRectDrawn(e.Rect, e.PageNumber);
    }

    private void OnTypewriterTextCreated(object? sender, TypewriterTextCreatedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel) return;
        viewModel.OnTypewriterTextCreated(e.Rect, e.PageNumber);
    }

    private void OnTypewriterTextEdited(object? sender, TypewriterTextEditedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel) return;
        viewModel.OnTypewriterTextEdited(e.OperationId, e.Text, e.PageNumber);
    }

    private void OnTypewriterTextBoundsChanged(object? sender, TypewriterTextBoundsChangedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel) return;
        viewModel.OnTypewriterTextBoundsChanged(e.OperationId, e.Rect, e.PageNumber);
    }

    private void OnTypewriterTextDeleted(object? sender, TypewriterTextDeletedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel) return;
        viewModel.OnTypewriterTextDeleted(e.OperationId);
    }

    /// <summary>
    /// Toolbar combo selection — translate the selected ComboBoxItem to the
    /// corresponding PdfFieldType for the next drag.
    /// </summary>
    private void OnFormFieldTypeChanged(object? sender, Avalonia.Controls.SelectionChangedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel) return;
        if (sender is not ComboBox cb || cb.SelectedItem is not ComboBoxItem item) return;
        var content = item.Content?.ToString() ?? "Text";
        viewModel.FormAuthoringFieldType = content switch
        {
            "Checkbox" => Pdfe.Core.Document.PdfFieldType.Button,
            "Choice"   => Pdfe.Core.Document.PdfFieldType.Choice,
            "Signature"=> Pdfe.Core.Document.PdfFieldType.Signature,
            _          => Pdfe.Core.Document.PdfFieldType.Text,
        };
    }

    /// <summary>
    /// Handle toast notifications from the ViewModel.
    /// Shows an InfoBar notification for 5 seconds, then auto-dismisses.
    /// </summary>
    private void OnToastRequested(object? sender, PdfEditor.Services.ToastService.ToastEventArgs e)
    {
        try
        {
            var infoBar = this.FindControl<FluentAvalonia.UI.Controls.FAInfoBar>("ToastInfoBar");
            if (infoBar == null)
                return;

            // Set severity based on toast severity level
            infoBar.Severity = e.Severity switch
            {
                PdfEditor.Services.ToastService.ToastSeverity.Error => FluentAvalonia.UI.Controls.FAInfoBarSeverity.Error,
                PdfEditor.Services.ToastService.ToastSeverity.Warning => FluentAvalonia.UI.Controls.FAInfoBarSeverity.Warning,
                PdfEditor.Services.ToastService.ToastSeverity.Success => FluentAvalonia.UI.Controls.FAInfoBarSeverity.Success,
                _ => FluentAvalonia.UI.Controls.FAInfoBarSeverity.Informational
            };

            // Set message and optional details
            infoBar.Title = e.Message;
            infoBar.Message = e.Details ?? string.Empty;

            // Show the InfoBar
            infoBar.IsOpen = true;

            // Auto-dismiss after 5 seconds using a single reusable UI-thread
            // timer. Restarting an existing timer (rather than spawning a new
            // System.Timers.Timer per toast) means at most one pending dismiss
            // exists, it runs on the dispatcher, and it cannot leak a callback
            // past the dispatcher's lifetime — which is what made headless GUI
            // tests flaky.
            _toastTimer ??= CreateToastTimer(infoBar);
            _toastTimer.Stop();
            _toastTimer.Start();
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"Error displaying toast: {ex.Message}");
        }
    }

    /// <summary>
    /// Build the reusable toast auto-dismiss timer. Ticks once (Stop() in the
    /// handler), on the UI thread, so it is inherently bound to the dispatcher's
    /// lifetime — no cross-thread marshaling, nothing left to drain at teardown.
    /// </summary>
    private DispatcherTimer CreateToastTimer(FluentAvalonia.UI.Controls.FAInfoBar infoBar)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += (s, args) =>
        {
            timer.Stop();
            infoBar.IsOpen = false;
        };
        return timer;
    }
}
