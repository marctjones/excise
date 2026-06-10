using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Reactive;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pdfe.Core.Document;
using Pdfe.Core.Text;
using Pdfe.Rendering;
using Pdfe.Avalonia.Imaging;
using Pdfe.Avalonia.Services;
using SkiaSharp;

namespace Pdfe.Avalonia.Controls;

/// <summary>
/// Reusable PDF viewer control with zoom, pan, and overlay support.
/// </summary>
public partial class PdfViewerControl : UserControl
{
    #region Dependency Properties

    /// <summary>
    /// The PDF document to display.
    /// </summary>
    public static readonly StyledProperty<PdfDocument?> DocumentProperty =
        AvaloniaProperty.Register<PdfViewerControl, PdfDocument?>(nameof(Document));

    public PdfDocument? Document
    {
        get => GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    /// <summary>
    /// Current page number (1-based).
    /// </summary>
    public static readonly StyledProperty<int> CurrentPageProperty =
        AvaloniaProperty.Register<PdfViewerControl, int>(nameof(CurrentPage), defaultValue: 1);

    public int CurrentPage
    {
        get => GetValue(CurrentPageProperty);
        set => SetValue(CurrentPageProperty, value);
    }

    /// <summary>
    /// Zoom level (1.0 = 100%).
    /// </summary>
    public static readonly StyledProperty<double> ZoomLevelProperty =
        AvaloniaProperty.Register<PdfViewerControl, double>(nameof(ZoomLevel), defaultValue: 1.0);

    public double ZoomLevel
    {
        get => GetValue(ZoomLevelProperty);
        set => SetValue(ZoomLevelProperty, value);
    }

    /// <summary>
    /// Interaction mode (None, Redaction, TextSelection, Pan).
    /// </summary>
    public static readonly StyledProperty<InteractionMode> InteractionModeProperty =
        AvaloniaProperty.Register<PdfViewerControl, InteractionMode>(nameof(InteractionMode));

    public InteractionMode InteractionMode
    {
        get => GetValue(InteractionModeProperty);
        set => SetValue(InteractionModeProperty, value);
    }

    /// <summary>
    /// View mode: <see cref="PdfViewMode.SinglePage"/> (the default — one page at
    /// a time, with all editing/redaction/selection interactions) or
    /// <see cref="PdfViewMode.Continuous"/> (a scrollable reading view of all
    /// pages, render-virtualized, with NO editing). Entering an editing
    /// interaction mode auto-switches back to SinglePage so the editing overlays
    /// (which are single-page by design, incl. security-critical redaction) are
    /// never driven against a continuous layout.
    /// </summary>
    public static readonly StyledProperty<PdfViewMode> ViewModeProperty =
        AvaloniaProperty.Register<PdfViewerControl, PdfViewMode>(nameof(ViewMode), defaultValue: PdfViewMode.SinglePage);

    public PdfViewMode ViewMode
    {
        get => GetValue(ViewModeProperty);
        set => SetValue(ViewModeProperty, value);
    }

    /// <summary>
    /// Is page currently loading?
    /// </summary>
    public static readonly StyledProperty<bool> IsLoadingProperty =
        AvaloniaProperty.Register<PdfViewerControl, bool>(nameof(IsLoading));

    public bool IsLoading
    {
        get => GetValue(IsLoadingProperty);
        private set => SetValue(IsLoadingProperty, value);
    }

    /// <summary>
    /// Does the control have an error?
    /// </summary>
    public static readonly StyledProperty<bool> HasErrorProperty =
        AvaloniaProperty.Register<PdfViewerControl, bool>(nameof(HasError));

    public bool HasError
    {
        get => GetValue(HasErrorProperty);
        private set => SetValue(HasErrorProperty, value);
    }

    /// <summary>
    /// Error message to display.
    /// </summary>
    public static readonly StyledProperty<string?> ErrorMessageProperty =
        AvaloniaProperty.Register<PdfViewerControl, string?>(nameof(ErrorMessage));

    public string? ErrorMessage
    {
        get => GetValue(ErrorMessageProperty);
        private set => SetValue(ErrorMessageProperty, value);
    }

    /// <summary>
    /// PDF annotations from the current page. When set, the AnnotationsLayer
    /// canvas is redrawn with coloured rectangles per annotation subtype.
    /// </summary>
    public static readonly StyledProperty<System.Collections.Generic.IEnumerable<Pdfe.Core.Document.PdfAnnotation>?> AnnotationsProperty =
        AvaloniaProperty.Register<PdfViewerControl, System.Collections.Generic.IEnumerable<Pdfe.Core.Document.PdfAnnotation>?>(nameof(Annotations));

    public System.Collections.Generic.IEnumerable<Pdfe.Core.Document.PdfAnnotation>? Annotations
    {
        get => GetValue(AnnotationsProperty);
        set => SetValue(AnnotationsProperty, value);
    }

    /// <summary>
    /// AcroForm fields on the current page. When set, the FormFieldsLayer
    /// canvas paints a clickable text input for each text/choice field and a
    /// checkbox for each button field. Mutating an input fires
    /// <see cref="FormFieldEdited"/>.
    /// </summary>
    public static readonly StyledProperty<System.Collections.Generic.IReadOnlyList<Pdfe.Core.Document.PdfField>?> FormFieldsProperty =
        AvaloniaProperty.Register<PdfViewerControl, System.Collections.Generic.IReadOnlyList<Pdfe.Core.Document.PdfField>?>(nameof(FormFields));

    public System.Collections.Generic.IReadOnlyList<Pdfe.Core.Document.PdfField>? FormFields
    {
        get => GetValue(FormFieldsProperty);
        set => SetValue(FormFieldsProperty, value);
    }

    /// <summary>
    /// Highlights for hidden-behind-overlay text to paint on top of the
    /// rendered page. Bound to a VM observable collection; whenever it
    /// changes, <see cref="RefreshHiddenTextOverlays"/> redraws them.
    /// </summary>
    public static readonly StyledProperty<System.Collections.Generic.IEnumerable<HiddenTextHighlight>?> HiddenTextHighlightsProperty =
        AvaloniaProperty.Register<PdfViewerControl, System.Collections.Generic.IEnumerable<HiddenTextHighlight>?>(nameof(HiddenTextHighlights));

    public System.Collections.Generic.IEnumerable<HiddenTextHighlight>? HiddenTextHighlights
    {
        get => GetValue(HiddenTextHighlightsProperty);
        set => SetValue(HiddenTextHighlightsProperty, value);
    }

    #endregion

    #region Events

    /// <summary>
    /// Fired when a redaction rectangle is drawn.
    /// </summary>
    public event EventHandler<RedactionDrawnEventArgs>? RedactionDrawn;

    /// <summary>
    /// Fired when text is selected.
    /// </summary>
    public event EventHandler<TextSelectedEventArgs>? TextSelected;

    /// <summary>
    /// Fired when the page changes.
    /// </summary>
    public event EventHandler<PageChangedEventArgs>? PageChanged;

    /// <summary>
    /// Fired when the user clicks an internal-document link. The handler
    /// typically sets <see cref="CurrentPage"/> to <see cref="LinkClickedEventArgs.PageNumber"/>.
    /// </summary>
    public event EventHandler<LinkClickedEventArgs>? LinkClicked;

    /// <summary>
    /// Fired when the user edits an AcroForm field via the FormFieldsLayer
    /// inputs. The control has already mutated the underlying PdfField; the
    /// host typically reacts by re-rendering the page so any baked-in
    /// appearance is refreshed.
    /// </summary>
    public event EventHandler<FormFieldEditedEventArgs>? FormFieldEdited;

    /// <summary>
    /// Fired when the user finishes drawing a new field rect in
    /// FormAuthoring mode. Carries the rect in PDF points (bottom-left
    /// origin) plus the host page number.
    /// </summary>
    public event EventHandler<FormFieldRectDrawnEventArgs>? FormFieldRectDrawn;

    #endregion

    #region Fields

    private readonly SkiaRenderer _renderer;
    private Image? _pdfImage;
    private Canvas? _overlayCanvas;
    private Canvas? _interactionLayer;
    private LayoutTransformControl? _zoomHost;
    private ScaleTransform? _zoomScaleTransform;
    private ScrollViewer? _scrollViewer;
    private Grid? _loadingOverlay;
    private ProgressBar? _loadingProgressBar;
    private Grid? _errorOverlay;
    private TextBlock? _errorMessageText;
    private Point _dragStart;
    private bool _isDragging;

    // Default render DPI for the on-screen viewer. 200 DPI was overkill —
    // a US-Letter page at 200 DPI is 1700×2200 (3.7M px); at 120 DPI it's
    // 1020×1320 (1.3M px), 3× less rasterisation work, and the difference
    // is invisible at typical zoom levels.
    private const int DefaultRenderDpi = 120;

    // LRU bitmap cache so flipping back to a recently-viewed page is
    // instant. Capped small — bitmaps for a 200-page book can be ~6 MB
    // each in BGRA, so we trade a few tens of MB for snappy navigation.
    private const int PageCacheCapacity = 6;
    private readonly LinkedList<(int Page, int Dpi, WriteableBitmap Bmp)> _pageCache = new();

    // Tracks the in-flight render so rapid paging cancels stale work.
    private CancellationTokenSource? _renderCts;

    // Text-selection state. Cached letters are in PDF points (Y-up) for
    // the page currently displayed; we look them up at the same DPI the
    // viewer renders at to map screen-DIP pointer coords into PDF coords.
    private const double TextSelectionRenderDpi = 120.0;
    private int _lettersPageNumber = -1;
    private List<Letter>? _currentPageLetters; // raw glyph order
    private List<Letter>? _readingOrderedLetters; // for range slicing
    private Letter? _selectionAnchor;
    private Letter? _selectionFocus;

    // Internal-link annotations on the current page. Lazy-loaded the
    // first time we hit-test on a given page; cleared when the page or
    // document changes. The same DPI and Y-flip rules used for letters
    // apply here.
    private int _linksPageNumber = -1;
    private IReadOnlyList<PdfLink>? _currentPageLinks;

    #endregion

    public PdfViewerControl()
    {
        InitializeComponent();
        _renderer = new SkiaRenderer();
        DetachedFromVisualTree += OnDetachedFromVisualTreeHandler;

        // Subscribe to property changes
        DocumentProperty.Changed.AddClassHandler<PdfViewerControl>((control, e) =>
            control.OnDocumentChanged());
        CurrentPageProperty.Changed.AddClassHandler<PdfViewerControl>((control, e) =>
            control.OnCurrentPageChanged());
        ZoomLevelProperty.Changed.AddClassHandler<PdfViewerControl>((control, e) =>
            control.OnZoomLevelChanged());
        IsLoadingProperty.Changed.AddClassHandler<PdfViewerControl>((control, e) =>
            control.OnLoadingStateChanged());
        HasErrorProperty.Changed.AddClassHandler<PdfViewerControl>((control, e) =>
            control.OnErrorStateChanged());
        ErrorMessageProperty.Changed.AddClassHandler<PdfViewerControl>((control, e) =>
            control.OnErrorMessageChanged());
        AnnotationsProperty.Changed.AddClassHandler<PdfViewerControl>((control, _) =>
            control.RedrawAnnotationsLayer());
        FormFieldsProperty.Changed.AddClassHandler<PdfViewerControl>((control, _) =>
            control.RedrawFormFieldsLayer());
        HiddenTextHighlightsProperty.Changed.AddClassHandler<PdfViewerControl>((control, e) =>
            control.OnHiddenTextHighlightsChanged(
                e.OldValue as System.Collections.Generic.IEnumerable<HiddenTextHighlight>,
                e.NewValue as System.Collections.Generic.IEnumerable<HiddenTextHighlight>));
        ViewModeProperty.Changed.AddClassHandler<PdfViewerControl>((control, _) =>
            control.OnViewModeChanged());
        // Editing interactions are single-page only. If the host turns on an
        // editing mode while we're in the continuous reading view, switch back
        // to single-page (at the current page) so the editing overlays line up
        // with a single rendered page — never a continuous stack.
        InteractionModeProperty.Changed.AddClassHandler<PdfViewerControl>((control, e) =>
        {
            if (control.ViewMode == PdfViewMode.Continuous
                && e.NewValue is InteractionMode m && IsEditingMode(m))
            {
                control.ViewMode = PdfViewMode.SinglePage;
            }
        });
    }

    private void OnDetachedFromVisualTreeHandler(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _viewportSubscription?.Dispose();
        _viewportSubscription = null;
        _continuousOffsetSubscription?.Dispose();
        _continuousOffsetSubscription = null;
        _continuousViewportSubscription?.Dispose();
        _continuousViewportSubscription = null;

        if (_continuousItems != null)
        {
            _continuousItems.ContainerPrepared -= OnContinuousContainerPrepared;
            _continuousItems.ContainerClearing -= OnContinuousContainerClearing;
        }
    }

    /// <summary>An interaction mode that draws/edits and therefore needs single-page layout.</summary>
    private static bool IsEditingMode(InteractionMode m) =>
        m is InteractionMode.Redaction or InteractionMode.TextSelection or InteractionMode.FormAuthoring;

    private System.Collections.Specialized.INotifyCollectionChanged? _watchedHighlights;

    private void OnHiddenTextHighlightsChanged(
        System.Collections.Generic.IEnumerable<HiddenTextHighlight>? oldValue,
        System.Collections.Generic.IEnumerable<HiddenTextHighlight>? newValue)
    {
        // If the bound value is an ObservableCollection, subscribe to its
        // changes so the overlay repaints when the VM adds/removes hits.
        if (_watchedHighlights != null)
        {
            _watchedHighlights.CollectionChanged -= OnHighlightsCollectionChanged;
            _watchedHighlights = null;
        }
        if (newValue is System.Collections.Specialized.INotifyCollectionChanged notify)
        {
            _watchedHighlights = notify;
            notify.CollectionChanged += OnHighlightsCollectionChanged;
        }
        RedrawHiddenTextOverlays();
    }

    private void OnHighlightsCollectionChanged(object? s, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => RedrawHiddenTextOverlays();

    private void RedrawHiddenTextOverlays()
    {
        var layer = this.FindControl<Canvas>("HiddenTextRevealLayer");
        if (layer == null) return;
        layer.Children.Clear();

        var highlights = HiddenTextHighlights;
        if (highlights == null) return;

        foreach (var h in highlights)
        {
            // Color code by source: yellow for structural (we have the
            // exact characters), orange for differential-OCR (recovered
            // from raster — confidence is OCR-typical, less certain).
            var (fill, stroke, ink) = h.Source == HiddenTextSource.DifferentialOcr
                ? (Color.FromArgb(220, 255, 165, 0),  // orange
                   Color.FromArgb(255, 200, 80, 0),
                   Color.FromArgb(255, 120, 40, 0))
                : (Color.FromArgb(230, 255, 255, 0),  // yellow
                   Color.FromArgb(255, 220, 20, 20),
                   Color.FromArgb(255, 180, 0, 0));

            var bg = new Rectangle
            {
                Width = Math.Max(h.ScreenBounds.Width, 8),
                Height = Math.Max(h.ScreenBounds.Height, 8),
                Fill = new SolidColorBrush(fill),
                Stroke = new SolidColorBrush(stroke),
                StrokeThickness = 2,
            };
            Canvas.SetLeft(bg, h.ScreenBounds.X);
            Canvas.SetTop(bg, h.ScreenBounds.Y);
            layer.Children.Add(bg);

            var label = new TextBlock
            {
                Text = h.Text,
                Foreground = new SolidColorBrush(ink),
                FontWeight = FontWeight.Bold,
                FontSize = Math.Max(10, h.ScreenBounds.Height * 0.75),
                TextWrapping = TextWrapping.NoWrap,
            };
            Canvas.SetLeft(label, h.ScreenBounds.X + 2);
            Canvas.SetTop(label, h.ScreenBounds.Y);
            layer.Children.Add(label);
        }
    }

    // DPI used for annotation-rect scaling (must match DefaultRenderDpi).
    private const double AnnotationRenderDpi = DefaultRenderDpi;

    private void RedrawAnnotationsLayer()
    {
        var layer = this.FindControl<Canvas>("AnnotationsLayer");
        if (layer == null) return;
        layer.Children.Clear();

        var annots = Annotations;
        if (annots == null || Document == null) return;

        var page = Document.GetPage(CurrentPage);
        double pageHeight = page.Height;
        double scale = AnnotationRenderDpi / 72.0;

        foreach (var a in annots)
        {
            var (fillColor, strokeColor) = AnnotationColors(a);
            var r = a.Rect;
            // Y-flip: PDF bottom-left → DIP top-left
            double dipX = r.Left  * scale;
            double dipY = (pageHeight - r.Top) * scale;
            double dipW = Math.Max((r.Right  - r.Left) * scale, 4);
            double dipH = Math.Max((r.Top - r.Bottom)  * scale, 4);

            var rect = new Rectangle
            {
                Width = dipW,
                Height = dipH,
                Fill = new SolidColorBrush(fillColor),
                Stroke = new SolidColorBrush(strokeColor),
                StrokeThickness = 1.5,
            };
            Canvas.SetLeft(rect, dipX);
            Canvas.SetTop(rect, dipY);
            layer.Children.Add(rect);
        }
    }

    private void RedrawFormFieldsLayer()
    {
        var layer = this.FindControl<Canvas>("FormFieldsLayer");
        if (layer == null) return;
        layer.Children.Clear();

        var fields = FormFields;
        if (fields == null || Document == null || fields.Count == 0) return;

        var page = Document.GetPage(CurrentPage);
        double pageHeight = page.Height;
        double scale = AnnotationRenderDpi / 72.0;

        foreach (var field in fields)
        {
            if (field.Rect is not Pdfe.Core.Document.PdfRectangle r) continue;

            // Y-flip: PDF bottom-left → DIP top-left
            double dipX = r.Left * scale;
            double dipY = (pageHeight - r.Top) * scale;
            double dipW = Math.Max((r.Right - r.Left) * scale, 12);
            double dipH = Math.Max((r.Top - r.Bottom) * scale, 12);

            Control? input = field.FieldType switch
            {
                Pdfe.Core.Document.PdfFieldType.Text   => CreateTextFieldInput(field, dipW, dipH),
                Pdfe.Core.Document.PdfFieldType.Choice => CreateChoiceFieldInput(field, dipW, dipH),
                Pdfe.Core.Document.PdfFieldType.Button => CreateButtonFieldInput(field, dipW, dipH),
                _ => null,
            };
            if (input == null) continue;

            input.Width = dipW;
            input.Height = dipH;
            Canvas.SetLeft(input, dipX);
            Canvas.SetTop(input, dipY);
            layer.Children.Add(input);
        }
    }

    private TextBox CreateTextFieldInput(Pdfe.Core.Document.PdfField field, double w, double h)
    {
        var box = new TextBox
        {
            Text = field.Value ?? string.Empty,
            IsReadOnly = field.IsReadOnly,
            AcceptsReturn = field.IsMultiline,
            Background = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0x80)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xCC, 0xAA, 0x00)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(2),
            FontSize = Math.Max(10, h * 0.6),
            VerticalContentAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
        };

        // Commit on Enter (single-line) or focus loss.
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && !field.IsMultiline)
            {
                CommitFieldEdit(field, box.Text);
                e.Handled = true;
            }
        };
        box.LostFocus += (_, _) => CommitFieldEdit(field, box.Text);
        return box;
    }

    private Control CreateChoiceFieldInput(Pdfe.Core.Document.PdfField field, double w, double h)
    {
        var combo = new ComboBox
        {
            ItemsSource = field.Options,
            SelectedItem = field.Value,
            IsEnabled = !field.IsReadOnly,
            Background = new SolidColorBrush(Color.FromArgb(0x20, 0x80, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x00, 0x88, 0xAA)),
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is string s)
                CommitFieldEdit(field, s);
        };
        return combo;
    }

    private Control CreateButtonFieldInput(Pdfe.Core.Document.PdfField field, double w, double h)
    {
        // Treat any value other than "Off"/null as checked for the MVP.
        // Acrobat stores radio-button states as the option's name (e.g.
        // "/Choice1"), so this works for both checkbox and the simplest
        // single-radio case.
        var checkBox = new CheckBox
        {
            IsChecked = !string.IsNullOrEmpty(field.Value)
                && !string.Equals(field.Value, "Off", StringComparison.OrdinalIgnoreCase),
            IsEnabled = !field.IsReadOnly,
            Background = new SolidColorBrush(Color.FromArgb(0x20, 0x00, 0xAA, 0x44)),
        };
        checkBox.IsCheckedChanged += (_, _) =>
        {
            var newValue = checkBox.IsChecked == true ? "Yes" : "Off";
            CommitFieldEdit(field, newValue);
        };
        return checkBox;
    }

    private void CommitFieldEdit(Pdfe.Core.Document.PdfField field, string? newValue)
    {
        // Skip a no-op assignment so we don't fire spurious re-render events.
        if (string.Equals(field.Value, newValue, StringComparison.Ordinal)) return;
        try
        {
            field.SetValue(newValue);
        }
        catch (InvalidOperationException) { return; } // read-only / signature
        catch (ArgumentException)        { return; } // choice value not in /Opt

        FormFieldEdited?.Invoke(this,
            new FormFieldEditedEventArgs(field.FullName, newValue, CurrentPage));
    }

    private static (Color Fill, Color Stroke) AnnotationColors(Pdfe.Core.Document.PdfAnnotation a)
    {
        return a.Subtype switch
        {
            Pdfe.Core.Document.PdfAnnotationSubtype.Highlight  => (Color.FromArgb(0x50, 0xFF, 0xFF, 0x00), Color.FromArgb(0xFF, 0xCC, 0xAA, 0x00)),
            Pdfe.Core.Document.PdfAnnotationSubtype.Underline  => (Color.FromArgb(0x40, 0x00, 0x80, 0xFF), Color.FromArgb(0xFF, 0x00, 0x60, 0xFF)),
            Pdfe.Core.Document.PdfAnnotationSubtype.StrikeOut  => (Color.FromArgb(0x40, 0xFF, 0x00, 0x00), Color.FromArgb(0xFF, 0xCC, 0x00, 0x00)),
            Pdfe.Core.Document.PdfAnnotationSubtype.Squiggly   => (Color.FromArgb(0x40, 0xFF, 0x80, 0x00), Color.FromArgb(0xFF, 0xFF, 0x60, 0x00)),
            Pdfe.Core.Document.PdfAnnotationSubtype.Link       => (Color.FromArgb(0x20, 0x00, 0x80, 0xFF), Color.FromArgb(0xFF, 0x00, 0x80, 0xFF)),
            Pdfe.Core.Document.PdfAnnotationSubtype.Text       => (Color.FromArgb(0x40, 0xFF, 0xDD, 0x00), Color.FromArgb(0xFF, 0xAA, 0x88, 0x00)),
            Pdfe.Core.Document.PdfAnnotationSubtype.Widget     => (Color.FromArgb(0x20, 0x00, 0xAA, 0x44), Color.FromArgb(0xFF, 0x00, 0x88, 0x33)),
            _                                                  => (Color.FromArgb(0x30, 0x80, 0x80, 0x80), Color.FromArgb(0xFF, 0x60, 0x60, 0x60)),
        };
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        // Get references to named controls
        _pdfImage = this.FindControl<Image>("PdfImage");
        _overlayCanvas = this.FindControl<Canvas>("OverlayCanvas");
        _interactionLayer = this.FindControl<Canvas>("InteractionLayer");
        _zoomHost = this.FindControl<LayoutTransformControl>("ZoomHost");
        _scrollViewer = this.FindControl<ScrollViewer>("PdfScrollViewer");
        _loadingOverlay = this.FindControl<Grid>("LoadingOverlay");
        _loadingProgressBar = this.FindControl<ProgressBar>("LoadingProgressBar");
        _errorOverlay = this.FindControl<Grid>("ErrorOverlay");
        _errorMessageText = this.FindControl<TextBlock>("ErrorMessageText");

        // Single scale transform on the LayoutTransformControl wrapper. Both
        // the Image and the OverlayCanvas live inside it, so they scale and
        // align together — no need for two parallel RenderTransforms.
        if (_zoomHost != null)
        {
            _zoomScaleTransform = _zoomHost.LayoutTransform as ScaleTransform;
        }

        // Pointer handlers — attached at the UserControl root level using
        // AddHandler with handledEventsToo:true so they fire even when an
        // intermediate control (e.g. an invisible-but-hit-testable
        // overlay Grid) intercepts the bubble path. Pre-fix attachment
        // was on _interactionLayer (zero-sized — never received events)
        // and then on _zoomHost (skipped when ErrorOverlay sat as a
        // sibling above it). Listening at the UserControl root catches
        // everything; the handlers compute pointer coords relative to
        // the ZoomHost wrapper themselves.
        AddHandler(PointerPressedEvent, OnInteractionLayerPointerPressed,
            global::Avalonia.Interactivity.RoutingStrategies.Tunnel | global::Avalonia.Interactivity.RoutingStrategies.Bubble,
            handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnInteractionLayerPointerMoved,
            global::Avalonia.Interactivity.RoutingStrategies.Tunnel | global::Avalonia.Interactivity.RoutingStrategies.Bubble,
            handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnInteractionLayerPointerReleased,
            global::Avalonia.Interactivity.RoutingStrategies.Tunnel | global::Avalonia.Interactivity.RoutingStrategies.Bubble,
            handledEventsToo: true);

        // Surface viewport changes (scrollbars appearing/disappearing,
        // sidebars toggling, window resizes). Subscribe directly to the
        // ScrollViewer's Viewport AvaloniaProperty — it raises only on
        // actual value changes. Initial implementation used LayoutUpdated,
        // which fires on EVERY layout pass of the whole tree and created a
        // feedback loop with tooltips: hovering a button popped a tooltip,
        // tooltip layout fired LayoutUpdated, our handler pushed Viewport
        // (sometimes oscillating sub-pixel) to the VM, ReapplyFitModeIfNeeded
        // re-set ZoomLevel, triggering yet more layout. Result: button
        // tooltips flickered and the button was unclickable.
        if (_scrollViewer != null)
        {
            // AnonymousObserver (Avalonia.Reactive) rather than a Subscribe(Action<T>)
            // overload — the latter comes from System.Reactive (Rx), which this
            // library deliberately does NOT depend on (the app got it transitively
            // via ReactiveUI). Avalonia ships AnonymousObserver for exactly this. (#365)
            _viewportSubscription = _scrollViewer
                .GetObservable(ScrollViewer.ViewportProperty)
                .Subscribe(new AnonymousObserver<Size>(OnScrollViewerViewportChanged));
        }

        InitializeContinuous();
    }

    /// <summary>
    /// The actual visible page area, in DIPs, *inside* the scroll bars.
    /// Use this — not the outer control's Bounds — for fit-zoom math, so
    /// the answer doesn't include the strip a vertical scrollbar steals.
    /// </summary>
    public Size GetVisibleViewportSize()
    {
        if (ViewMode == PdfViewMode.Continuous && _continuousScrollViewer != null)
        {
            var cv = _continuousScrollViewer.Viewport;
            if (cv.Width > 0 && cv.Height > 0) return cv;
        }

        if (_scrollViewer != null)
        {
            var v = _scrollViewer.Viewport;
            if (v.Width > 0 && v.Height > 0) return v;
        }
        return Bounds.Size;
    }

    /// <summary>Raised when the inside-the-scrollbars viewport size changes.</summary>
    public event EventHandler<Size>? VisibleViewportChanged;

    private Size _lastReportedViewport;
    private IDisposable? _viewportSubscription;
    private IDisposable? _continuousOffsetSubscription;
    private IDisposable? _continuousViewportSubscription;

    private void OnScrollViewerViewportChanged(Size newViewport)
    {
        if (newViewport.Width <= 0 || newViewport.Height <= 0) return;
        if (Math.Abs(newViewport.Width - _lastReportedViewport.Width) < 0.5 &&
            Math.Abs(newViewport.Height - _lastReportedViewport.Height) < 0.5) return;
        _lastReportedViewport = newViewport;
        VisibleViewportChanged?.Invoke(this, newViewport);
    }

    private void ReportActiveViewport()
    {
        var viewport = GetVisibleViewportSize();
        if (viewport.Width > 0 && viewport.Height > 0)
        {
            OnScrollViewerViewportChanged(viewport);
        }
    }

    private void OnZoomLevelChanged()
    {
        if (_zoomScaleTransform != null)
        {
            _zoomScaleTransform.ScaleX = ZoomLevel;
            _zoomScaleTransform.ScaleY = ZoomLevel;
        }
        if (ViewMode == PdfViewMode.Continuous)
            ApplyContinuousZoom();
    }

    private void OnLoadingStateChanged()
    {
        // Show the thin top-of-viewer progress bar while a render is in
        // flight. (The full-screen overlay is kept hidden — it was always
        // visually overpowering for sub-second renders and is replaced by
        // the indeterminate ProgressBar.)
        if (_loadingProgressBar != null)
            _loadingProgressBar.IsVisible = IsLoading;
        if (_loadingOverlay != null)
            _loadingOverlay.IsVisible = false;
    }

    private static readonly SolidColorBrush ErrorOverlayBrush =
        new(Color.FromArgb(0x80, 0x00, 0x00, 0x00));

    private void OnErrorStateChanged()
    {
        if (_errorOverlay != null)
        {
            _errorOverlay.IsVisible = HasError;
            _errorOverlay.IsHitTestVisible = HasError;
            // Set Background only while an error is actively shown — the
            // dim wash captures clicks intentionally then. With no error
            // the overlay has no Background and is fully transparent to
            // hit-testing, so in-page link clicks reach the page area.
            _errorOverlay.Background = HasError ? ErrorOverlayBrush : null;
        }
    }

    private void OnErrorMessageChanged()
    {
        if (_errorMessageText != null)
        {
            _errorMessageText.Text = ErrorMessage;
        }
    }

    #region Rendering

    private async void OnDocumentChanged()
    {
        // Drop cached bitmaps from the prior document (would render at wrong
        // pages otherwise) and cancel any render that was still finishing
        // for that document. Same for the page-letters cache used by
        // text-selection — if it referenced a page from the old document
        // we'd hit-test against stale glyphs.
        InvalidatePageCache();
        _renderCts?.Cancel();
        _currentPageLetters = null;
        _readingOrderedLetters = null;
        _lettersPageNumber = -1;
        _currentPageLinks = null;
        _linksPageNumber = -1;
        ClearSelectionHighlight();

        InvalidateContinuousCache();
        if (Document != null)
        {
            RefreshPageAnnotations();
            if (ViewMode == PdfViewMode.Continuous)
                RebuildContinuous();
            await RenderCurrentPageAsync();
        }
        else
        {
            Annotations = null;
            ClearDisplay();
            ClearContinuous();
        }
    }

    private async void OnCurrentPageChanged()
    {
        if (Document != null && CurrentPage >= 1 && CurrentPage <= Document.PageCount)
        {
            if (ViewMode == PdfViewMode.Continuous)
            {
                if (!_syncingPageFromScroll)
                {
                    ScrollToPageContinuous(CurrentPage);
                }

                PageChanged?.Invoke(this, new PageChangedEventArgs(CurrentPage));
                return;
            }

            // Drop selection state from the previous page — the cached
            // letters won't match the new page's geometry and we'd
            // otherwise hit-test against stale glyphs.
            _currentPageLetters = null;
            _readingOrderedLetters = null;
            _lettersPageNumber = -1;
            _selectionAnchor = null;
            _selectionFocus = null;
            _currentPageLinks = null;
            _linksPageNumber = -1;
            ClearSelectionHighlight();

            // Load annotations for the new page and refresh the overlay.
            RefreshPageAnnotations();

            await RenderCurrentPageAsync();
            PageChanged?.Invoke(this, new PageChangedEventArgs(CurrentPage));

            // In continuous mode, a CurrentPage change that did NOT originate
            // from the user scrolling (e.g. a "go to page" command or a clicked
            // link) should scroll the reading view to that page.
            if (ViewMode == PdfViewMode.Continuous && !_syncingPageFromScroll)
                ScrollToPageContinuous(CurrentPage);
        }
    }

    private void RefreshPageAnnotations()
    {
        if (Document == null || CurrentPage < 1 || CurrentPage > Document.PageCount)
        {
            Annotations = null;
            return;
        }
        try
        {
            var page = Document.GetPage(CurrentPage);
            Annotations = page.GetAnnotations();
        }
        catch
        {
            Annotations = null;
        }
    }

    private async Task RenderCurrentPageAsync()
    {
        if (Document == null || CurrentPage < 1 || CurrentPage > Document.PageCount)
            return;

        // Cache hit short-circuits the renderer entirely — this is the
        // common case for backwards-paging, undoing redactions, and
        // toggling overlays. Set Image.Source immediately so the user
        // doesn't even see a loading flicker.
        if (TryGetCached(CurrentPage, DefaultRenderDpi, out var cached))
        {
            if (_pdfImage != null) _pdfImage.Source = cached;
            HasError = false;
            ErrorMessage = null;
            return;
        }

        // Cancel any prior in-flight render. If the user is paging through
        // quickly we'd rather skip the now-stale page than make them wait.
        _renderCts?.Cancel();
        var cts = new CancellationTokenSource();
        _renderCts = cts;
        var token = cts.Token;

        var pageNumber = CurrentPage;
        var doc = Document;
        try
        {
            IsLoading = true;
            HasError = false;
            ErrorMessage = null;

            var skBitmap = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var page = doc.GetPage(pageNumber);
                var options = new Pdfe.Rendering.RenderOptions { Dpi = DefaultRenderDpi };
                return _renderer.RenderPage(page, options);
            }, token);

            try
            {
                // The user may have paged again while we were rendering —
                // honour the cancellation rather than overwriting the
                // freshly-rendered new page with the stale one.
                if (token.IsCancellationRequested) return;

                var bitmap = SkiaInterop.ToAvaloniaBitmap(skBitmap);
                if (bitmap != null)
                {
                    AddToCache(pageNumber, DefaultRenderDpi, bitmap);
                    if (_pdfImage != null) _pdfImage.Source = bitmap;
                }
            }
            finally
            {
                skBitmap?.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when paging quickly — drop the stale render silently.
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorMessage = $"Failed to render page: {ex.Message}";
        }
        finally
        {
            // Only the most-recent render should clear IsLoading; older
            // races would otherwise flicker the overlay back on.
            if (_renderCts == cts)
                IsLoading = false;
        }
    }

    private bool TryGetCached(int page, int dpi, out WriteableBitmap? bmp)
    {
        for (var node = _pageCache.First; node != null; node = node.Next)
        {
            if (node.Value.Page == page && node.Value.Dpi == dpi)
            {
                // Move to front (LRU touch).
                _pageCache.Remove(node);
                _pageCache.AddFirst(node);
                bmp = node.Value.Bmp;
                return true;
            }
        }
        bmp = null;
        return false;
    }

    private void AddToCache(int page, int dpi, WriteableBitmap bmp)
    {
        // Replace existing entry for same key (e.g. re-render after edit).
        for (var node = _pageCache.First; node != null; node = node.Next)
        {
            if (node.Value.Page == page && node.Value.Dpi == dpi)
            {
                node.Value.Bmp.Dispose();
                _pageCache.Remove(node);
                break;
            }
        }
        _pageCache.AddFirst((page, dpi, bmp));
        while (_pageCache.Count > PageCacheCapacity)
        {
            var last = _pageCache.Last!;
            _pageCache.RemoveLast();
            last.Value.Bmp.Dispose();
        }
    }

    /// <summary>Drop the cached bitmaps — call when document changes or content edits invalidate prior renders.</summary>
    public void InvalidatePageCache()
    {
        foreach (var entry in _pageCache) entry.Bmp.Dispose();
        _pageCache.Clear();
    }

    private void ClearDisplay()
    {
        if (_pdfImage != null)
        {
            _pdfImage.Source = null;
        }
    }

    #endregion

    #region Interaction

    private void OnInteractionLayerPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // First chance: internal-link click in any mode (including None).
        // Links are treated as ambient affordances — like a browser, not
        // a drawing tool — so the redaction/text-selection mode shouldn't
        // suppress them. We only consume the event when a link actually
        // hits, otherwise let the rest of the press handling continue.
        // GetPosition relative to OverlayCanvas — that control sits INSIDE
        // the LayoutTransformControl wrapper, so its local coordinate
        // system is bitmap-native (pre-zoom). Asking for coords relative
        // to the wrapper itself returns post-zoom values, which then
        // miss every link rect when the user has zoomed at all (auto-
        // fit-width on document load = always).
        var pressPoint = GetPressPoint(e);
        var linkHit = HitTestLinkAt(pressPoint);
        if (linkHit != null)
        {
            LinkClicked?.Invoke(this, new LinkClickedEventArgs(linkHit.DestinationPage));
            e.Handled = true;
            return;
        }

        if (InteractionMode == InteractionMode.None)
            return;

        var point = GetPressPoint(e);
        _dragStart = point;
        _isDragging = true;

        if (InteractionMode == InteractionMode.TextSelection)
        {
            // Text-selection mode: hit-test letters instead of drawing a
            // 2-D rectangle. Anchor is the letter under (or nearest to)
            // the press point; focus tracks pointer-moved.
            EnsurePageLettersLoaded();
            _selectionAnchor = HitTestLetterAt(point);
            _selectionFocus = _selectionAnchor;
            ClearSelectionHighlight();
            if (_selectionAnchor != null)
                DrawSelectionRange(new[] { _selectionAnchor });
        }

        e.Handled = true;
    }

    private void OnInteractionLayerPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDragging || InteractionMode == InteractionMode.None)
            return;

        var currentPoint = GetPressPoint(e);

        if (InteractionMode == InteractionMode.Redaction ||
            InteractionMode == InteractionMode.FormAuthoring)
        {
            DrawTemporaryRedactionRectangle(_dragStart, currentPoint);
        }
        else if (InteractionMode == InteractionMode.TextSelection)
        {
            // Letter-by-letter highlight as the user drags from anchor.
            if (_selectionAnchor == null || _readingOrderedLetters == null) return;
            var hit = HitTestLetterAt(currentPoint);
            if (hit == null) return;
            // Re-draw only when focus actually moves to a different letter.
            if (ReferenceEquals(hit, _selectionFocus)) return;
            _selectionFocus = hit;
            var range = TextSelectionEngine.RangeBetween(
                _readingOrderedLetters, _selectionAnchor, _selectionFocus);
            DrawSelectionRange(range);
        }

        e.Handled = true;
    }

    private void OnInteractionLayerPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDragging)
            return;

        _isDragging = false;

        if (InteractionMode == InteractionMode.Redaction)
        {
            var endPoint = GetPressPoint(e);
            var rect = CreateRect(_dragStart, endPoint);
            var adjustedRect = new Rect(
                rect.X / ZoomLevel, rect.Y / ZoomLevel,
                rect.Width / ZoomLevel, rect.Height / ZoomLevel);
            RedactionDrawn?.Invoke(this, new RedactionDrawnEventArgs(adjustedRect));
            ClearTemporaryDrawings();
        }
        else if (InteractionMode == InteractionMode.FormAuthoring)
        {
            var endPoint = GetPressPoint(e);
            var dipRect = CreateRect(_dragStart, endPoint);
            ClearTemporaryDrawings();
            // Convert from viewer DIPs to PDF points (Y-flipped).
            if (Document != null && dipRect.Width > 4 && dipRect.Height > 4)
            {
                var page = Document.GetPage(CurrentPage);
                double scale = AnnotationRenderDpi / 72.0;
                double pdfLeft   = dipRect.X / scale;
                double pdfRight  = (dipRect.X + dipRect.Width) / scale;
                double pdfTop    = page.Height - dipRect.Y / scale;
                double pdfBottom = page.Height - (dipRect.Y + dipRect.Height) / scale;
                var pdfRect = new Pdfe.Core.Document.PdfRectangle(
                    Math.Min(pdfLeft, pdfRight),
                    Math.Min(pdfBottom, pdfTop),
                    Math.Max(pdfLeft, pdfRight),
                    Math.Max(pdfBottom, pdfTop));
                FormFieldRectDrawn?.Invoke(this,
                    new FormFieldRectDrawnEventArgs(pdfRect, CurrentPage));
            }
        }
        else if (InteractionMode == InteractionMode.TextSelection &&
                 _selectionAnchor != null && _selectionFocus != null &&
                 _readingOrderedLetters != null)
        {
            var range = TextSelectionEngine.RangeBetween(
                _readingOrderedLetters, _selectionAnchor, _selectionFocus);
            var text = TextSelectionEngine.JoinText(range);
            var letterDips = range
                .Select(l => PdfRectangleToDips(l.GlyphRectangle))
                .ToList();
            // Bounding box of the whole run — keeps backwards compat with
            // listeners that just want a single Rect.
            Rect? bbox = letterDips.Count > 0
                ? UnionRects(letterDips)
                : (Rect?)null;
            TextSelected?.Invoke(this, new TextSelectedEventArgs(
                bbox ?? new Rect(), text, letterDips));
        }
        else
        {
            ClearTemporaryDrawings();
        }

        e.Handled = true;
    }

    /// <summary>
    /// Cache the current page's letters (in PDF points) keyed by page
    /// number so repeated text-selection drags on the same page don't
    /// re-extract. Letters are always re-fetched when CurrentPage changes.
    /// </summary>
    private void EnsurePageLettersLoaded()
    {
        if (Document == null) return;
        if (_lettersPageNumber == CurrentPage && _currentPageLetters != null) return;
        try
        {
            var page = Document.GetPage(CurrentPage);
            _currentPageLetters = page.Letters?.ToList() ?? new List<Letter>();
            _readingOrderedLetters = TextSelectionEngine.SortReadingOrder(_currentPageLetters);
            _lettersPageNumber = CurrentPage;
        }
        catch
        {
            _currentPageLetters = new List<Letter>();
            _readingOrderedLetters = new List<Letter>();
            _lettersPageNumber = CurrentPage;
        }
    }

    /// <summary>
    /// Pointer coords in bitmap-native (pre-zoom) DIPs. We need a control
    /// INSIDE the LayoutTransformControl wrapper to get pre-zoom values;
    /// asking the wrapper itself returns post-zoom values that miss
    /// every link/letter rect when zoom != 1 (which auto-fit makes the
    /// default).
    /// </summary>
    private Point GetPressPoint(PointerEventArgs e)
    {
        if (_overlayCanvas != null) return e.GetPosition(_overlayCanvas);
        if (_pdfImage != null) return e.GetPosition(_pdfImage);
        return e.GetPosition(this);
    }

    private void EnsurePageLinksLoaded()
    {
        if (Document == null) return;
        if (_linksPageNumber == CurrentPage && _currentPageLinks != null) return;
        try
        {
            var page = Document.GetPage(CurrentPage);
            _currentPageLinks = page.GetLinks();
            _linksPageNumber = CurrentPage;
        }
        catch
        {
            _currentPageLinks = System.Array.Empty<PdfLink>();
            _linksPageNumber = CurrentPage;
        }
    }

    private PdfLink? HitTestLinkAt(Point dipPoint)
    {
        EnsurePageLinksLoaded();
        if (_currentPageLinks == null || _currentPageLinks.Count == 0) return null;
        if (Document == null) return null;
        var page = Document.GetPage(CurrentPage);
        var pdfX = dipPoint.X * 72.0 / TextSelectionRenderDpi;
        var pdfY = page.Height - (dipPoint.Y * 72.0 / TextSelectionRenderDpi);
        foreach (var link in _currentPageLinks)
        {
            var r = link.Rect;
            if (pdfX >= r.Left && pdfX <= r.Right &&
                pdfY >= r.Bottom && pdfY <= r.Top)
                return link;
        }
        return null;
    }

    private Letter? HitTestLetterAt(Point dipPoint)
    {
        if (_currentPageLetters == null || _currentPageLetters.Count == 0) return null;
        if (Document == null) return null;
        var page = Document.GetPage(CurrentPage);
        // Pointer coords are in pre-zoom DIPs of the InteractionLayer
        // (which sits inside the LayoutTransformControl wrapper, so the
        // wrapper's zoom doesn't apply here — we get the natural DIPs of
        // the rendered bitmap). Convert to PDF points and flip Y.
        var pdfX = dipPoint.X * 72.0 / TextSelectionRenderDpi;
        var pdfY = page.Height - (dipPoint.Y * 72.0 / TextSelectionRenderDpi);
        return TextSelectionEngine.HitTest(_currentPageLetters, pdfX, pdfY);
    }

    private Rect PdfRectangleToDips(Pdfe.Core.Document.PdfRectangle r)
    {
        if (Document == null) return default;
        var page = Document.GetPage(CurrentPage);
        const double s = TextSelectionRenderDpi / 72.0;
        // PDF Y-up → DIP Y-down: dipTop = (page.Height - pdfTop) * s.
        var dipX = r.Left * s;
        var dipY = (page.Height - r.Top) * s;
        var dipW = (r.Right - r.Left) * s;
        var dipH = (r.Top - r.Bottom) * s;
        return new Rect(dipX, dipY, dipW, dipH);
    }

    private static Rect UnionRects(IReadOnlyList<Rect> rects)
    {
        var x1 = double.PositiveInfinity; var y1 = double.PositiveInfinity;
        var x2 = double.NegativeInfinity; var y2 = double.NegativeInfinity;
        foreach (var r in rects)
        {
            if (r.X < x1) x1 = r.X;
            if (r.Y < y1) y1 = r.Y;
            if (r.Right > x2) x2 = r.Right;
            if (r.Bottom > y2) y2 = r.Bottom;
        }
        return new Rect(x1, y1, x2 - x1, y2 - y1);
    }

    private void DrawSelectionRange(IReadOnlyList<Letter> letters)
    {
        var layer = this.FindControl<Canvas>("TextSelectionLayer");
        if (layer == null) return;
        layer.Children.Clear();
        var fill = new SolidColorBrush(Color.FromArgb(0x60, 0x33, 0x99, 0xFF));
        foreach (var l in letters)
        {
            var r = PdfRectangleToDips(l.GlyphRectangle);
            var rect = new Rectangle
            {
                Fill = fill,
                Width = r.Width,
                Height = r.Height
            };
            Canvas.SetLeft(rect, r.X);
            Canvas.SetTop(rect, r.Y);
            layer.Children.Add(rect);
        }
    }

    /// <summary>Clear any in-progress text selection (e.g. switching pages).</summary>
    public void ClearSelectionHighlight()
    {
        var layer = this.FindControl<Canvas>("TextSelectionLayer");
        layer?.Children.Clear();
    }

    private static Rect CreateRect(Point p1, Point p2)
    {
        var left = Math.Min(p1.X, p2.X);
        var top = Math.Min(p1.Y, p2.Y);
        var right = Math.Max(p1.X, p2.X);
        var bottom = Math.Max(p1.Y, p2.Y);

        return new Rect(left, top, right - left, bottom - top);
    }

    private Rectangle? _tempRedactionRect;
    private Rectangle? _tempSelectionRect;

    private void DrawTemporaryRedactionRectangle(Point start, Point end)
    {
        if (_interactionLayer == null) return;

        var rect = CreateRect(start, end);

        if (_tempRedactionRect == null)
        {
            _tempRedactionRect = new Rectangle
            {
                Fill = new SolidColorBrush(Color.FromArgb(0x40, 0x00, 0x00, 0x00)), // Semi-transparent black
                Stroke = Brushes.Red,
                StrokeThickness = 2,
                StrokeDashArray = new AvaloniaList<double> { 5, 5 }
            };
            _interactionLayer.Children.Add(_tempRedactionRect);
        }

        Canvas.SetLeft(_tempRedactionRect, rect.X);
        Canvas.SetTop(_tempRedactionRect, rect.Y);
        _tempRedactionRect.Width = rect.Width;
        _tempRedactionRect.Height = rect.Height;
        _tempRedactionRect.IsVisible = true;
    }

    private void DrawTemporarySelectionRectangle(Point start, Point end)
    {
        if (_interactionLayer == null) return;

        var rect = CreateRect(start, end);

        if (_tempSelectionRect == null)
        {
            _tempSelectionRect = new Rectangle
            {
                Fill = new SolidColorBrush(Color.FromArgb(0x40, 0x4C, 0xAF, 0x50)), // Semi-transparent green
                Stroke = new SolidColorBrush(Color.FromArgb(0xFF, 0x4C, 0xAF, 0x50)),
                StrokeThickness = 2,
                StrokeDashArray = new AvaloniaList<double> { 5, 5 }
            };
            _interactionLayer.Children.Add(_tempSelectionRect);
        }

        Canvas.SetLeft(_tempSelectionRect, rect.X);
        Canvas.SetTop(_tempSelectionRect, rect.Y);
        _tempSelectionRect.Width = rect.Width;
        _tempSelectionRect.Height = rect.Height;
        _tempSelectionRect.IsVisible = true;
    }

    private void ClearTemporaryDrawings()
    {
        if (_tempRedactionRect != null)
        {
            _tempRedactionRect.IsVisible = false;
        }

        if (_tempSelectionRect != null)
        {
            _tempSelectionRect.IsVisible = false;
        }
    }

    #endregion

    #region Public Methods

    /// <summary>
    /// Navigate to the next page.
    /// </summary>
    public void NextPage()
    {
        if (Document != null && CurrentPage < Document.PageCount)
        {
            CurrentPage++;
        }
    }

    /// <summary>
    /// Navigate to the previous page.
    /// </summary>
    public void PreviousPage()
    {
        if (CurrentPage > 1)
        {
            CurrentPage--;
        }
    }

    /// <summary>
    /// Zoom in by 25%.
    /// </summary>
    public void ZoomIn()
    {
        ZoomLevel = Math.Min(ZoomLevel * 1.25, 5.0);
    }

    /// <summary>
    /// Zoom out by 25%.
    /// </summary>
    public void ZoomOut()
    {
        ZoomLevel = Math.Max(ZoomLevel / 1.25, 0.1);
    }

    /// <summary>
    /// Reset zoom to 100%.
    /// </summary>
    public void ZoomToActualSize()
    {
        ZoomLevel = 1.0;
    }

    /// <summary>
    /// Add a search highlight rectangle at the specified coordinates.
    /// </summary>
    public void AddSearchHighlight(Rect area)
    {
        var searchLayer = this.FindControl<Canvas>("SearchHighlightsLayer");
        if (searchLayer == null) return;

        var highlight = new Rectangle
        {
            Fill = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0x00)), // Semi-transparent yellow
            Stroke = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0x98, 0x00)), // Orange border
            StrokeThickness = 1,
            Width = area.Width,
            Height = area.Height
        };

        Canvas.SetLeft(highlight, area.X);
        Canvas.SetTop(highlight, area.Y);
        searchLayer.Children.Add(highlight);
    }

    /// <summary>
    /// Clear all search highlights.
    /// </summary>
    public void ClearSearchHighlights()
    {
        var searchLayer = this.FindControl<Canvas>("SearchHighlightsLayer");
        searchLayer?.Children.Clear();
    }

    /// <summary>
    /// Add a pending redaction overlay at the specified coordinates.
    /// </summary>
    public void AddPendingRedaction(Rect area)
    {
        var redactionLayer = this.FindControl<Canvas>("PendingRedactionsLayer");
        if (redactionLayer == null) return;

        var rect = new Rectangle
        {
            Fill = Brushes.Transparent,
            Stroke = Brushes.Red,
            StrokeThickness = 2,
            StrokeDashArray = new AvaloniaList<double> { 5, 3 },
            Width = area.Width,
            Height = area.Height
        };

        Canvas.SetLeft(rect, area.X);
        Canvas.SetTop(rect, area.Y);
        redactionLayer.Children.Add(rect);
    }

    /// <summary>
    /// Clear all pending redaction overlays.
    /// </summary>
    public void ClearPendingRedactions()
    {
        var redactionLayer = this.FindControl<Canvas>("PendingRedactionsLayer");
        redactionLayer?.Children.Clear();
    }

    /// <summary>
    /// Add an applied redaction overlay (black rectangle) at the specified coordinates.
    /// </summary>
    public void AddAppliedRedaction(Rect area)
    {
        var appliedLayer = this.FindControl<Canvas>("AppliedRedactionsLayer");
        if (appliedLayer == null) return;

        var rect = new Rectangle
        {
            Fill = Brushes.Black,
            Stroke = Brushes.Black,
            StrokeThickness = 1,
            Width = area.Width,
            Height = area.Height
        };

        Canvas.SetLeft(rect, area.X);
        Canvas.SetTop(rect, area.Y);
        appliedLayer.Children.Add(rect);
    }

    /// <summary>
    /// Clear all applied redaction overlays.
    /// </summary>
    public void ClearAppliedRedactions()
    {
        var appliedLayer = this.FindControl<Canvas>("AppliedRedactionsLayer");
        appliedLayer?.Children.Clear();
    }

    /// <summary>
    /// Clear all overlays (search, pending redactions, applied redactions).
    /// </summary>
    public void ClearAllOverlays()
    {
        ClearSearchHighlights();
        ClearPendingRedactions();
        ClearAppliedRedactions();
    }

    #endregion
}

#region Event Args

/// <summary>
/// Event arguments for redaction drawn event.
/// </summary>
public class RedactionDrawnEventArgs : EventArgs
{
    public Rect Area { get; }

    public RedactionDrawnEventArgs(Rect area)
    {
        Area = area;
    }
}

/// <summary>
/// Event arguments for text selected event.
/// </summary>
public class TextSelectedEventArgs : EventArgs
{
    /// <summary>Joined text of the selected letter run, in reading order.</summary>
    public string Text { get; }
    /// <summary>Per-letter bounding boxes in viewer-DIP coordinates.</summary>
    public IReadOnlyList<Rect> LetterBoundsDips { get; }
    /// <summary>Bounding box of the entire selection. Backwards-compat with the rect-only listeners.</summary>
    public Rect Area { get; }

    public TextSelectedEventArgs(Rect area, string text, IReadOnlyList<Rect> letterBoundsDips)
    {
        Area = area;
        Text = text;
        LetterBoundsDips = letterBoundsDips;
    }

    /// <summary>Backwards-compat ctor — area only, empty text/bounds.</summary>
    public TextSelectedEventArgs(Rect area) : this(area, string.Empty, System.Array.Empty<Rect>()) { }
}

/// <summary>
/// Event arguments for an internal-document link click. Carries the
/// 1-based page number of the destination.
/// </summary>
public class LinkClickedEventArgs : EventArgs
{
    public int PageNumber { get; }
    public LinkClickedEventArgs(int pageNumber) { PageNumber = pageNumber; }
}

/// <summary>
/// Event arguments for the user finishing a drag-rect in FormAuthoring
/// mode. The rect is in PDF points, bottom-left origin.
/// </summary>
public class FormFieldRectDrawnEventArgs : EventArgs
{
    public Pdfe.Core.Document.PdfRectangle Rect { get; }
    public int PageNumber { get; }
    public FormFieldRectDrawnEventArgs(Pdfe.Core.Document.PdfRectangle rect, int pageNumber)
    {
        Rect = rect;
        PageNumber = pageNumber;
    }
}

/// <summary>
/// Event arguments for an AcroForm field edit. The control has already
/// mutated <see cref="Pdfe.Core.Document.PdfField.SetValue"/>; carries the
/// field's full name and the new value (null if cleared).
/// </summary>
public class FormFieldEditedEventArgs : EventArgs
{
    public string FieldName { get; }
    public string? NewValue { get; }
    public int PageNumber { get; }
    public FormFieldEditedEventArgs(string fieldName, string? newValue, int pageNumber)
    {
        FieldName = fieldName;
        NewValue = newValue;
        PageNumber = pageNumber;
    }
}

/// <summary>
/// Event arguments for page changed event.
/// </summary>
public class PageChangedEventArgs : EventArgs
{
    public int PageNumber { get; }

    public PageChangedEventArgs(int pageNumber)
    {
        PageNumber = pageNumber;
    }
}

#endregion

#region Enums

/// <summary>
/// Interaction modes for the PDF viewer.
/// </summary>
/// <summary>
/// How the viewer lays out pages. <see cref="SinglePage"/> shows one page with
/// full editing; <see cref="Continuous"/> is a render-virtualized scrolling
/// reading view of all pages with no editing.
/// </summary>
public enum PdfViewMode
{
    /// <summary>One page at a time, with all editing interactions (the default).</summary>
    SinglePage,

    /// <summary>Scrollable all-pages reading view, render-virtualized, no editing.</summary>
    Continuous,
}

public enum InteractionMode
{
    /// <summary>
    /// No interaction (view only).
    /// </summary>
    None,

    /// <summary>
    /// Draw redaction rectangles.
    /// </summary>
    Redaction,

    /// <summary>
    /// Select text areas.
    /// </summary>
    TextSelection,

    /// <summary>
    /// Pan/scroll the document.
    /// </summary>
    Pan,

    /// <summary>
    /// Drag to define a new AcroForm field rect. The host listens for
    /// <see cref="PdfViewerControl.FormFieldRectDrawn"/> and materialises
    /// a field of the user-selected type.
    /// </summary>
    FormAuthoring,
}

#endregion
