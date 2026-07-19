using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
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
using Excise.Core.Document;
using Excise.Core.Editing;
using Excise.Core.Text;
using Excise.Rendering;
using Excise.Avalonia.Imaging;
using Excise.Avalonia.Services;
using SkiaSharp;

namespace Excise.Avalonia.Controls;

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
    /// Monotonic host-provided content version. Increment when the same
    /// document instance has visually changed and the viewer should invalidate
    /// page caches and render the current view again.
    /// </summary>
    public static readonly StyledProperty<long> RenderVersionProperty =
        AvaloniaProperty.Register<PdfViewerControl, long>(nameof(RenderVersion));

    public long RenderVersion
    {
        get => GetValue(RenderVersionProperty);
        set => SetValue(RenderVersionProperty, value);
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
    public static readonly StyledProperty<System.Collections.Generic.IEnumerable<Excise.Core.Document.PdfAnnotation>?> AnnotationsProperty =
        AvaloniaProperty.Register<PdfViewerControl, System.Collections.Generic.IEnumerable<Excise.Core.Document.PdfAnnotation>?>(nameof(Annotations));

    public System.Collections.Generic.IEnumerable<Excise.Core.Document.PdfAnnotation>? Annotations
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
    public static readonly StyledProperty<System.Collections.Generic.IReadOnlyList<Excise.Core.Document.PdfField>?> FormFieldsProperty =
        AvaloniaProperty.Register<PdfViewerControl, System.Collections.Generic.IReadOnlyList<Excise.Core.Document.PdfField>?>(nameof(FormFields));

    public System.Collections.Generic.IReadOnlyList<Excise.Core.Document.PdfField>? FormFields
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

    public static readonly StyledProperty<System.Collections.Generic.IEnumerable<PdfTypewriterTextOperation>?> TypewriterTextOperationsProperty =
        AvaloniaProperty.Register<PdfViewerControl, System.Collections.Generic.IEnumerable<PdfTypewriterTextOperation>?>(nameof(TypewriterTextOperations));

    public System.Collections.Generic.IEnumerable<PdfTypewriterTextOperation>? TypewriterTextOperations
    {
        get => GetValue(TypewriterTextOperationsProperty);
        set => SetValue(TypewriterTextOperationsProperty, value);
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
    /// Fired when the user clicks an external (http/https/mailto) link
    /// (#625). The handler is responsible for confirming with the user
    /// before navigating — this control only reports the click, it never
    /// opens anything itself.
    /// </summary>
    public event EventHandler<ExternalLinkClickedEventArgs>? ExternalLinkClicked;

    /// <summary>
    /// Fired when the user clicks a link excise refuses to run — /Launch,
    /// /GoToE, /GoToR, or a URI action with a non-allowlisted scheme (#625).
    /// The handler typically shows a message explaining the refusal.
    /// </summary>
    public event EventHandler<DangerousLinkClickedEventArgs>? DangerousLinkClicked;

    /// <summary>
    /// Fired as the pointer moves over a link (any kind) or off one (#625).
    /// <c>null</c> target text means "no longer hovering a link" — hosts
    /// typically clear their status-bar hover text in that case.
    /// </summary>
    public event EventHandler<LinkHoveredEventArgs>? LinkHovered;

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

    public event EventHandler<TypewriterTextCreatedEventArgs>? TypewriterTextCreated;
    public event EventHandler<TypewriterTextEditedEventArgs>? TypewriterTextEdited;
    public event EventHandler<TypewriterTextBoundsChangedEventArgs>? TypewriterTextBoundsChanged;
    public event EventHandler<TypewriterTextDeletedEventArgs>? TypewriterTextDeleted;

    #endregion

    #region Fields

    private readonly SkiaRenderer _renderer;
    private Image? _pdfImage;
    private Canvas? _overlayCanvas;
    private Canvas? _interactionLayer;
    private Canvas? _typewriterLayer;
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
    private const int MinSinglePageRenderDpi = 12;
    private const long MaxSinglePagePreviewPixels = 64L * 1024L * 1024L;
    private int _currentSinglePageRenderDpi = DefaultRenderDpi;

    // LRU bitmap cache so flipping back to a recently-viewed page is
    // instant. Capped small — bitmaps for a 200-page book can be ~6 MB
    // each in BGRA, so we trade a few tens of MB for snappy navigation.
    private const int PageCacheCapacity = 6;
    private readonly LinkedList<(int Page, int Dpi, WriteableBitmap Bmp)> _pageCache = new();

    // Tracks the in-flight render so rapid paging cancels stale work.
    private CancellationTokenSource? _renderCts;

    // Text-selection state. Cached letters are in PDF content points (Y-up)
    // for the page currently displayed; hit-testing routes through
    // PdfCoordinateMapper so the render scale and page rotation stay aligned.
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
    /// <summary>Last link the pointer hovered, for hover enter/exit edge detection (#625).</summary>
    private PdfLink? _lastHoveredLink;

    #endregion

    public PdfViewerControl()
    {
        InitializeComponent();
        _renderer = new SkiaRenderer();
        Focusable = true;
        UpdateViewerAutomationProperties();
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
        TypewriterTextOperationsProperty.Changed.AddClassHandler<PdfViewerControl>((control, e) =>
            control.OnTypewriterTextOperationsChanged(
                e.OldValue as System.Collections.Generic.IEnumerable<PdfTypewriterTextOperation>,
                e.NewValue as System.Collections.Generic.IEnumerable<PdfTypewriterTextOperation>));
        ViewModeProperty.Changed.AddClassHandler<PdfViewerControl>((control, _) =>
            control.OnViewModeChanged());
        RenderVersionProperty.Changed.AddClassHandler<PdfViewerControl>((control, _) =>
            control.OnRenderVersionChanged());
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

            control.RedrawTypewriterLayer();
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
        m is InteractionMode.Redaction or InteractionMode.TextSelection or InteractionMode.FormAuthoring or InteractionMode.Typewriter;

    private System.Collections.Specialized.INotifyCollectionChanged? _watchedHighlights;
    private System.Collections.Specialized.INotifyCollectionChanged? _watchedTypewriterTextOperations;

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

    private void OnTypewriterTextOperationsChanged(
        System.Collections.Generic.IEnumerable<PdfTypewriterTextOperation>? oldValue,
        System.Collections.Generic.IEnumerable<PdfTypewriterTextOperation>? newValue)
    {
        if (_watchedTypewriterTextOperations != null)
        {
            _watchedTypewriterTextOperations.CollectionChanged -= OnTypewriterTextOperationsCollectionChanged;
            _watchedTypewriterTextOperations = null;
        }

        if (newValue is System.Collections.Specialized.INotifyCollectionChanged notify)
        {
            _watchedTypewriterTextOperations = notify;
            notify.CollectionChanged += OnTypewriterTextOperationsCollectionChanged;
        }

        RedrawTypewriterLayer();
    }

    private void OnTypewriterTextOperationsCollectionChanged(
        object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is System.Collections.Specialized.NotifyCollectionChangedAction.Add
            or System.Collections.Specialized.NotifyCollectionChangedAction.Remove
            or System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
        {
            RedrawTypewriterLayer();
        }
    }

    private void RedrawHiddenTextOverlays()
    {
        var layer = this.FindControl<Canvas>("HiddenTextRevealLayer");
        if (layer == null) return;
        layer.Children.Clear();

        var highlights = HiddenTextHighlights;
        if (highlights == null) return;

        foreach (var h in highlights)
        {
            var bounds = ToAvaloniaRect(ToViewerDips(h.Bounds));
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
                Width = Math.Max(bounds.Width, 8),
                Height = Math.Max(bounds.Height, 8),
                Fill = new SolidColorBrush(fill),
                Stroke = new SolidColorBrush(stroke),
                StrokeThickness = 2,
            };
            Canvas.SetLeft(bg, bounds.X);
            Canvas.SetTop(bg, bounds.Y);
            layer.Children.Add(bg);

            var label = new TextBlock
            {
                Text = h.Text,
                Foreground = new SolidColorBrush(ink),
                FontWeight = FontWeight.Bold,
                FontSize = Math.Max(10, bounds.Height * 0.75),
                TextWrapping = TextWrapping.NoWrap,
            };
            Canvas.SetLeft(label, bounds.X + 2);
            Canvas.SetTop(label, bounds.Y);
            layer.Children.Add(label);
        }
    }

    // DPI used for single-page viewer overlay scaling. Most pages use
    // DefaultRenderDpi, but huge page boxes may render at a lower preview DPI.
    private double ViewerUnitsPerPoint => _currentSinglePageRenderDpi / PdfPageRect.PdfPointsPerInch;

    private static Rect ToAvaloniaRect(PdfPageRect rect) =>
        new(rect.X, rect.Y, rect.Width, rect.Height);

    private PdfPageRect ToViewerDips(PdfPageRect rect)
    {
        if (rect.Space == PdfCoordinateSpace.ViewerDips &&
            Math.Abs(rect.UnitsPerPoint - ViewerUnitsPerPoint) < 0.000001)
        {
            return rect;
        }

        if (Document == null || rect.PageNumber < 1 || rect.PageNumber > Document.PageCount)
            return rect;

        return PdfCoordinateMapper.ToViewerDips(Document.GetPage(rect.PageNumber), rect, _currentSinglePageRenderDpi);
    }

    private PdfPageRect ViewerDipsRect(Rect rect, int pageNumber) =>
        PdfPageRect.ViewerDips(pageNumber, rect.X, rect.Y, rect.Width, rect.Height, _currentSinglePageRenderDpi);

    private PdfPageRect ContentRect(PdfRectangle rect, int pageNumber) =>
        PdfPageRect.FromPdfRectangle(pageNumber, rect, PdfCoordinateSpace.ContentPoints);

    private void RedrawAnnotationsLayer()
    {
        var layer = this.FindControl<Canvas>("AnnotationsLayer");
        if (layer == null) return;
        layer.Children.Clear();

        var annots = Annotations;
        if (annots == null || Document == null) return;

        foreach (var a in annots)
        {
            var (fillColor, strokeColor) = AnnotationColors(a);
            var r = ToAvaloniaRect(ToViewerDips(ContentRect(a.Rect, CurrentPage)));
            double dipW = Math.Max(r.Width, 4);
            double dipH = Math.Max(r.Height, 4);

            var rect = new Rectangle
            {
                Width = dipW,
                Height = dipH,
                Fill = new SolidColorBrush(fillColor),
                Stroke = new SolidColorBrush(strokeColor),
                StrokeThickness = 1.5,
            };
            Canvas.SetLeft(rect, r.X);
            Canvas.SetTop(rect, r.Y);
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

        var orderedFields = fields
            .Where(field => field.Rect.HasValue)
            .OrderByDescending(field => field.Rect!.Value.Top)
            .ThenBy(field => field.Rect!.Value.Left)
            .ThenBy(field => field.FullName, StringComparer.Ordinal)
            .ToList();

        for (var tabIndex = 0; tabIndex < orderedFields.Count; tabIndex++)
        {
            var field = orderedFields[tabIndex];
            if (field.Rect is not Excise.Core.Document.PdfRectangle r) continue;

            var viewerRect = ToAvaloniaRect(ToViewerDips(ContentRect(r, CurrentPage)));
            double dipW = Math.Max(viewerRect.Width, 12);
            double dipH = Math.Max(viewerRect.Height, 12);

            Control? input = field.FieldType switch
            {
                Excise.Core.Document.PdfFieldType.Text   => CreateTextFieldInput(field, dipW, dipH),
                Excise.Core.Document.PdfFieldType.Choice => CreateChoiceFieldInput(field, dipW, dipH),
                Excise.Core.Document.PdfFieldType.Button => CreateButtonFieldInput(field, dipW, dipH),
                _ => null,
            };
            if (input == null) continue;

            input.Width = dipW;
            input.Height = dipH;
            ApplyFormFieldChrome(input, field, tabIndex);
            Canvas.SetLeft(input, viewerRect.X);
            Canvas.SetTop(input, viewerRect.Y);
            layer.Children.Add(input);
        }
    }

    private TextBox CreateTextFieldInput(Excise.Core.Document.PdfField field, double w, double h)
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
            VerticalContentAlignment = field.IsMultiline
                ? global::Avalonia.Layout.VerticalAlignment.Top
                : global::Avalonia.Layout.VerticalAlignment.Center,
        };

        // Commit on Enter (single-line), Ctrl+Enter (multiline), or focus loss.
        // Escape restores the last committed value.
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && !field.IsMultiline)
            {
                CommitFieldEdit(field, box.Text);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && field.IsMultiline && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                CommitFieldEdit(field, box.Text);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                box.Text = field.Value ?? string.Empty;
                e.Handled = true;
            }
        };
        box.LostFocus += (_, _) => CommitFieldEdit(field, box.Text);
        return box;
    }

    private Control CreateChoiceFieldInput(Excise.Core.Document.PdfField field, double w, double h)
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
        combo.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                combo.SelectedItem = field.Value;
                e.Handled = true;
            }
        };
        return combo;
    }

    private Control CreateButtonFieldInput(Excise.Core.Document.PdfField field, double w, double h)
    {
        if (field.ButtonExportValues.Count > 1)
        {
            var options = field.ButtonExportValues;
            var combo = new ComboBox
            {
                ItemsSource = options,
                SelectedItem = options.Contains(field.Value ?? string.Empty) ? field.Value : null,
                IsEnabled = !field.IsReadOnly,
                Background = new SolidColorBrush(Color.FromArgb(0x20, 0x00, 0xAA, 0x44)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x00, 0x88, 0x33)),
            };
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedItem is string s)
                    CommitFieldEdit(field, s);
            };
            return combo;
        }

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

    private static void ApplyFormFieldChrome(Control input, Excise.Core.Document.PdfField field, int tabIndex)
    {
        input.TabIndex = tabIndex;
        input.IsEnabled = input.IsEnabled && !field.IsReadOnly;
        ToolTip.SetTip(input, field.FullName);

        input.GotFocus += (_, _) => SetFormFieldFocusChrome(input, focused: true);
        input.LostFocus += (_, _) => SetFormFieldFocusChrome(input, focused: false);
    }

    private static void SetFormFieldFocusChrome(Control input, bool focused)
    {
        var brush = new SolidColorBrush(focused
            ? Color.FromArgb(0xFF, 0x00, 0x5F, 0xCC)
            : Color.FromArgb(0xFF, 0x88, 0x88, 0x88));
        var thickness = new Thickness(focused ? 2 : 1);

        switch (input)
        {
            case TextBox textBox:
                textBox.BorderBrush = brush;
                textBox.BorderThickness = thickness;
                break;
            case ComboBox comboBox:
                comboBox.BorderBrush = brush;
                comboBox.BorderThickness = thickness;
                break;
            case CheckBox checkBox:
                checkBox.BorderBrush = brush;
                checkBox.BorderThickness = thickness;
                break;
        }
    }

    private void CommitFieldEdit(Excise.Core.Document.PdfField field, string? newValue)
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

    private static (Color Fill, Color Stroke) AnnotationColors(Excise.Core.Document.PdfAnnotation a)
    {
        return a.Subtype switch
        {
            Excise.Core.Document.PdfAnnotationSubtype.Highlight  => (Color.FromArgb(0x50, 0xFF, 0xFF, 0x00), Color.FromArgb(0xFF, 0xCC, 0xAA, 0x00)),
            Excise.Core.Document.PdfAnnotationSubtype.Underline  => (Color.FromArgb(0x40, 0x00, 0x80, 0xFF), Color.FromArgb(0xFF, 0x00, 0x60, 0xFF)),
            Excise.Core.Document.PdfAnnotationSubtype.StrikeOut  => (Color.FromArgb(0x40, 0xFF, 0x00, 0x00), Color.FromArgb(0xFF, 0xCC, 0x00, 0x00)),
            Excise.Core.Document.PdfAnnotationSubtype.Squiggly   => (Color.FromArgb(0x40, 0xFF, 0x80, 0x00), Color.FromArgb(0xFF, 0xFF, 0x60, 0x00)),
            Excise.Core.Document.PdfAnnotationSubtype.Link       => (Color.FromArgb(0x20, 0x00, 0x80, 0xFF), Color.FromArgb(0xFF, 0x00, 0x80, 0xFF)),
            Excise.Core.Document.PdfAnnotationSubtype.Text       => (Color.FromArgb(0x40, 0xFF, 0xDD, 0x00), Color.FromArgb(0xFF, 0xAA, 0x88, 0x00)),
            Excise.Core.Document.PdfAnnotationSubtype.Widget     => (Color.FromArgb(0x20, 0x00, 0xAA, 0x44), Color.FromArgb(0xFF, 0x00, 0x88, 0x33)),
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
        _typewriterLayer = this.FindControl<Canvas>("TypewriterLayer");
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
        // Register on a SINGLE routing pass (Bubble). handledEventsToo:true
        // still delivers the event even when an intermediate control marked it
        // handled, so the root handler catches everything — but only ONCE.
        // Registering for Tunnel|Bubble fired each handler twice per event
        // (once descending, once ascending), which double-dispatched pointer
        // presses — e.g. a single in-page link click was handled twice (#675).
        AddHandler(PointerPressedEvent, OnInteractionLayerPointerPressed,
            global::Avalonia.Interactivity.RoutingStrategies.Bubble,
            handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnInteractionLayerPointerMoved,
            global::Avalonia.Interactivity.RoutingStrategies.Bubble,
            handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnInteractionLayerPointerReleased,
            global::Avalonia.Interactivity.RoutingStrategies.Bubble,
            handledEventsToo: true);
        AddHandler(KeyDownEvent, OnViewerKeyDown,
            global::Avalonia.Interactivity.RoutingStrategies.Tunnel | global::Avalonia.Interactivity.RoutingStrategies.Bubble,
            handledEventsToo: false);

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

    private void OnViewerKeyDown(object? sender, KeyEventArgs e)
    {
        if (IsKeyboardEditingSource(e.Source))
            return;

        bool handled = false;
        bool control = e.KeyModifiers.HasFlag(KeyModifiers.Control) ||
                       e.KeyModifiers.HasFlag(KeyModifiers.Meta);

        if (control)
        {
            switch (e.Key)
            {
                case Key.Add:
                case Key.OemPlus:
                    ZoomIn();
                    handled = true;
                    break;
                case Key.Subtract:
                case Key.OemMinus:
                    ZoomOut();
                    handled = true;
                    break;
                case Key.D0:
                case Key.NumPad0:
                    ZoomToActualSize();
                    handled = true;
                    break;
            }
        }
        else
        {
            switch (e.Key)
            {
                case Key.PageDown:
                case Key.Right:
                    NextPage();
                    handled = true;
                    break;
                case Key.PageUp:
                case Key.Left:
                    PreviousPage();
                    handled = true;
                    break;
                case Key.Home:
                    if (Document != null)
                    {
                        CurrentPage = 1;
                        handled = true;
                    }
                    break;
                case Key.End:
                    if (Document != null)
                    {
                        CurrentPage = Document.PageCount;
                        handled = true;
                    }
                    break;
            }
        }

        if (handled)
            e.Handled = true;
    }

    private static bool IsKeyboardEditingSource(object? source) =>
        source is TextBox or ComboBox;

    private void UpdateViewerAutomationProperties()
    {
        string name = Document == null
            ? "PDF viewer, no document loaded"
            : $"PDF viewer, page {CurrentPage} of {Document.PageCount}";
        AutomationProperties.SetName(this, name);

        string status = Document == null
            ? $"No document loaded; zoom {ZoomLevel:P0}; {ViewModeDescription(ViewMode)}"
            : $"Page {CurrentPage} of {Document.PageCount}; zoom {ZoomLevel:P0}; {ViewModeDescription(ViewMode)}";
        AutomationProperties.SetItemStatus(this, status);

        AutomationProperties.SetHelpText(this, BuildViewerAutomationHelpText());
    }

    private string BuildViewerAutomationHelpText()
    {
        const string keys = "Use Page Up and Page Down to change pages. Use Control plus Plus, Minus, or 0 to change zoom.";
        if (Document == null || CurrentPage < 1 || CurrentPage > Document.PageCount)
            return keys;

        string preview = ExtractCurrentPageTextPreview(maxLength: 500);
        return string.IsNullOrWhiteSpace(preview)
            ? $"{ViewModeDescription(ViewMode)}. {keys}"
            : $"{ViewModeDescription(ViewMode)}. Current page text preview: {preview}. {keys}";
    }

    private string ExtractCurrentPageTextPreview(int maxLength)
    {
        try
        {
            if (Document == null || CurrentPage < 1 || CurrentPage > Document.PageCount)
                return string.Empty;

            string text = Document.GetPage(CurrentPage).Text ?? string.Empty;
            text = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (text.Length <= maxLength)
                return text;

            return text[..maxLength] + "...";
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ViewModeDescription(PdfViewMode mode) =>
        mode == PdfViewMode.Continuous ? "continuous reading view" : "single-page editing view";

    private void OnZoomLevelChanged()
    {
        if (_zoomScaleTransform != null)
        {
            Trace($"Zoom -> {ZoomLevel:F3} mode={ViewMode} page={CurrentPage}");
            _zoomScaleTransform.ScaleX = ZoomLevel;
            _zoomScaleTransform.ScaleY = ZoomLevel;
        }
        if (ViewMode == PdfViewMode.Continuous)
        {
            ApplyContinuousZoom();
        }
        else
        {
            // Single-page: the ScaleTransform above gives instant visual zoom;
            // re-render at the new zoom's device resolution so text re-crisps
            // instead of upscaling the previous raster (#683). RenderCurrentPageAsync
            // cancels any in-flight render, so a fast zoom coalesces to the last
            // level; a cache hit at an already-seen zoom is instant.
            _ = RenderCurrentPageAsync();
        }
        UpdateViewerAutomationProperties();
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
            RedrawTypewriterLayer();
            if (ViewMode == PdfViewMode.Continuous)
                RebuildContinuous();
            await RenderCurrentPageAsync();
        }
        else
        {
            Annotations = null;
            RedrawTypewriterLayer();
            ClearDisplay();
            ClearContinuous();
        }

        UpdateViewerAutomationProperties();
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
                UpdateViewerAutomationProperties();
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
            RedrawTypewriterLayer();

            await RenderCurrentPageAsync();
            PageChanged?.Invoke(this, new PageChangedEventArgs(CurrentPage));
            UpdateViewerAutomationProperties();

            // In continuous mode, a CurrentPage change that did NOT originate
            // from the user scrolling (e.g. a "go to page" command or a clicked
            // link) should scroll the reading view to that page.
            if (ViewMode == PdfViewMode.Continuous && !_syncingPageFromScroll)
                ScrollToPageContinuous(CurrentPage);
        }
    }

    private void OnRenderVersionChanged()
    {
        if (Document == null)
            return;

        InvalidatePageCache();
        InvalidateContinuousCache();
        _currentPageLetters = null;
        _readingOrderedLetters = null;
        _lettersPageNumber = -1;
        _selectionAnchor = null;
        _selectionFocus = null;
        _currentPageLinks = null;
        _linksPageNumber = -1;
        ClearSelectionHighlight();
        RefreshPageAnnotations();
        RedrawTypewriterLayer();

        if (ViewMode == PdfViewMode.Continuous)
        {
            RebuildContinuous();
            RenderVisibleContinuousTiles();
        }
        else
        {
            _ = RenderCurrentPageAsync();
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

        var doc = Document;
        var pageNumber = CurrentPage;
        var page = doc.GetPage(pageNumber);
        // Logical DPI drives layout and coordinate mapping (unchanged); the
        // raster is produced at the on-screen magnification (device-pixel-ratio ×
        // zoom) so text is crisp on HiDPI (#682) AND when zoomed in (#683),
        // bounded by the single-page memory budget.
        var logicalDpi = EffectiveSinglePageRenderDpi(page);
        _currentSinglePageRenderDpi = logicalDpi;
        var box = page.CropBox.Normalize();
        var widthPt = page.Rotation is 90 or 270 ? box.Height : box.Width;
        var heightPt = page.Rotation is 90 or 270 ? box.Width : box.Height;
        double scale = ZoomLevel * EffectiveRenderScaling;
        double maxScale = MaxSinglePageRenderScale(widthPt, heightPt, logicalDpi);
        var (renderDpi, bitmapDpi) = SinglePageRenderPlan(logicalDpi, scale, maxScale);
        Trace($"SinglePageRender page={pageNumber} logicalDpi={logicalDpi} deviceDpi={renderDpi} " +
              $"bitmapDpi={bitmapDpi:F0} zoom={ZoomLevel:F3} dpr={EffectiveRenderScaling:F2} maxScale={maxScale:F2}");
        // MaxSinglePagePreviewPixels is a DEVICE-pixel (memory) ceiling, so it is
        // NOT scaled by the device-pixel-ratio: a normal page at device resolution
        // stays far under it (crisp), while a very large page is still capped at
        // the same memory bound (it simply doesn't gain the HiDPI sharpening).
        long maxPixels = MaxSinglePagePreviewPixels;

        // Cache hit short-circuits the renderer entirely — this is the
        // common case for backwards-paging, undoing redactions, and
        // toggling overlays. Set Image.Source immediately so the user
        // doesn't even see a loading flicker. The cache is keyed by the
        // DEVICE render DPI so a monitor change (dpr) re-renders.
        if (TryGetCached(pageNumber, renderDpi, out var cached))
        {
            Trace($"SinglePageRender page={pageNumber} CACHE-HIT dpi={renderDpi}");
            if (_pdfImage != null)
            {
                var cachedBitmap = cached!;
                _pdfImage.Width = cachedBitmap.Size.Width;
                _pdfImage.Height = cachedBitmap.Size.Height;
                _pdfImage.Source = cachedBitmap;
                Trace($"ImageSet(cache) page={pageNumber} imgWidth={_pdfImage.Width:F0} srcDip={cachedBitmap.Size.Width:F0} srcPx={cachedBitmap.PixelSize.Width} zoom={ZoomLevel:F3}");
            }
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

        try
        {
            IsLoading = true;
            HasError = false;
            ErrorMessage = null;

            var skBitmap = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var options = new Excise.Rendering.RenderOptions
                {
                    Dpi = renderDpi,
                    MaxPixelCount = maxPixels
                };
                return _renderer.RenderPage(page, options);
            }, token);

            try
            {
                // The user may have paged again while we were rendering —
                // honour the cancellation rather than overwriting the
                // freshly-rendered new page with the stale one.
                if (token.IsCancellationRequested) return;

                var bitmap = SkiaInterop.ToAvaloniaBitmap(skBitmap, bitmapDpi);
                if (bitmap != null)
                {
                    Trace($"SinglePageRender page={pageNumber} RENDERED px={bitmap.PixelSize.Width}x{bitmap.PixelSize.Height} dip={bitmap.Size.Width:F0}x{bitmap.Size.Height:F0}");
                    AddToCache(pageNumber, renderDpi, bitmap);
                    Trace($"ContVis={_continuousScrollViewer?.IsVisible} SingleVis={_scrollViewer?.IsVisible}");
                    Trace($"ImageSet page={pageNumber} imgWidth={_pdfImage?.Width:F0} srcDip={bitmap.Size.Width:F0}x{bitmap.Size.Height:F0} srcPx={bitmap.PixelSize.Width} zoom={ZoomLevel:F3}");
                    if (_pdfImage != null)
                    {
                        _pdfImage.Width = bitmap.Size.Width;
                        _pdfImage.Height = bitmap.Size.Height;
                        _pdfImage.Source = bitmap;
                    }
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

    internal static int EffectiveSinglePageRenderDpi(PdfPage page)
    {
        var box = page.CropBox.Normalize();
        var rotation = page.Rotation;   // already canonical {0,90,180,270}
        var widthPt = rotation is 90 or 270 ? box.Height : box.Width;
        var heightPt = rotation is 90 or 270 ? box.Width : box.Height;
        if (widthPt <= 0 || heightPt <= 0)
            return DefaultRenderDpi;

        var defaultPixels = (widthPt * DefaultRenderDpi / PdfPageRect.PdfPointsPerInch) *
                            (heightPt * DefaultRenderDpi / PdfPageRect.PdfPointsPerInch);
        if (defaultPixels <= MaxSinglePagePreviewPixels)
            return DefaultRenderDpi;

        var dpi = (int)Math.Floor(Math.Sqrt(MaxSinglePagePreviewPixels / (widthPt * heightPt)) *
                                  PdfPageRect.PdfPointsPerInch);
        return Math.Clamp(dpi, MinSinglePageRenderDpi, DefaultRenderDpi);
    }

    /// <summary>
    /// Device-resolution render plan for a single page (pure; unit-tested).
    /// <paramref name="scale"/> is the on-screen magnification the raster must
    /// resolve — the display device-pixel-ratio times the zoom level — so text
    /// stays crisp both on HiDPI displays (#682) and when zoomed in (#683). It
    /// returns the DPI to rasterize at (device resolution) and the DPI to stamp
    /// on the resulting bitmap so its DIP size equals the *logical* size. That
    /// invariant — <c>deviceDpi / (bitmapDpi / 96) == logicalDpi</c> — is what
    /// keeps the Image layout size, the ScaleTransform, and every coordinate
    /// mapping unchanged; only pixel density changes. <paramref name="maxScale"/>
    /// caps the raster at the single-page memory budget: beyond it the
    /// ScaleTransform upscales (soft at extreme zoom) rather than allocating an
    /// unbounded bitmap. At scale=1 it is an exact no-op (device == logical,
    /// stamp == 96).
    /// </summary>
    internal static (int DeviceDpi, double BitmapDpi) SinglePageRenderPlan(int logicalDpi, double scale, double maxScale)
    {
        double s = Math.Clamp(scale <= 0 ? 1.0 : scale, 1.0, Math.Max(1.0, maxScale));
        int deviceDpi = (int)Math.Round(logicalDpi * s);
        double bitmapDpi = 96.0 * s;
        return (deviceDpi, bitmapDpi);
    }

    /// <summary>
    /// The largest render scale that keeps a single page's raster within the
    /// device-pixel (memory) budget (pure; unit-tested). Always ≥ 1.
    /// </summary>
    internal static double MaxSinglePageRenderScale(double widthPt, double heightPt, int logicalDpi)
    {
        double logicalPixels = (widthPt * logicalDpi / PdfPageRect.PdfPointsPerInch) *
                               (heightPt * logicalDpi / PdfPageRect.PdfPointsPerInch);
        if (logicalPixels <= 0) return 1.0;
        return Math.Max(1.0, Math.Sqrt(MaxSinglePagePreviewPixels / logicalPixels));
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
        _currentSinglePageRenderDpi = DefaultRenderDpi;
        if (_pdfImage != null)
        {
            _pdfImage.Source = null;
            _pdfImage.Width = double.NaN;
            _pdfImage.Height = double.NaN;
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
        UpdateViewerAutomationProperties();
    }

    /// <summary>
    /// Zoom out by 25%.
    /// </summary>
    public void ZoomOut()
    {
        ZoomLevel = Math.Max(ZoomLevel / 1.25, 0.1);
        UpdateViewerAutomationProperties();
    }

    /// <summary>
    /// Reset zoom to 100%.
    /// </summary>
    public void ZoomToActualSize()
    {
        ZoomLevel = 1.0;
        UpdateViewerAutomationProperties();
    }

    /// <summary>
    /// Add a search highlight rectangle at the specified coordinates.
    /// </summary>
    public void AddSearchHighlight(PdfPageRect area)
    {
        var searchLayer = this.FindControl<Canvas>("SearchHighlightsLayer");
        if (searchLayer == null) return;

        var viewerArea = ToAvaloniaRect(ToViewerDips(area));
        var highlight = new Rectangle
        {
            Fill = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0x00)), // Semi-transparent yellow
            Stroke = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0x98, 0x00)), // Orange border
            StrokeThickness = 1,
            Width = viewerArea.Width,
            Height = viewerArea.Height
        };

        Canvas.SetLeft(highlight, viewerArea.X);
        Canvas.SetTop(highlight, viewerArea.Y);
        searchLayer.Children.Add(highlight);
    }

    /// <summary>
    /// Add a search highlight rectangle already expressed in viewer DIPs.
    /// Prefer <see cref="AddSearchHighlight(PdfPageRect)"/> for new code.
    /// </summary>
    public void AddSearchHighlight(Rect area) =>
        AddSearchHighlight(ViewerDipsRect(area, CurrentPage));

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
    public void AddPendingRedaction(PdfPageRect area)
    {
        var redactionLayer = this.FindControl<Canvas>("PendingRedactionsLayer");
        if (redactionLayer == null) return;

        var viewerArea = ToAvaloniaRect(ToViewerDips(area));
        var rect = new Rectangle
        {
            Fill = Brushes.Transparent,
            Stroke = Brushes.Red,
            StrokeThickness = 2,
            StrokeDashArray = new AvaloniaList<double> { 5, 3 },
            Width = viewerArea.Width,
            Height = viewerArea.Height
        };

        Canvas.SetLeft(rect, viewerArea.X);
        Canvas.SetTop(rect, viewerArea.Y);
        redactionLayer.Children.Add(rect);
    }

    /// <summary>
    /// Add a pending redaction overlay already expressed in viewer DIPs.
    /// Prefer <see cref="AddPendingRedaction(PdfPageRect)"/> for new code.
    /// </summary>
    public void AddPendingRedaction(Rect area) =>
        AddPendingRedaction(ViewerDipsRect(area, CurrentPage));

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
    public void AddAppliedRedaction(PdfPageRect area)
    {
        var appliedLayer = this.FindControl<Canvas>("AppliedRedactionsLayer");
        if (appliedLayer == null) return;

        var viewerArea = ToAvaloniaRect(ToViewerDips(area));
        var rect = new Rectangle
        {
            Fill = Brushes.Black,
            Stroke = Brushes.Black,
            StrokeThickness = 1,
            Width = viewerArea.Width,
            Height = viewerArea.Height
        };

        Canvas.SetLeft(rect, viewerArea.X);
        Canvas.SetTop(rect, viewerArea.Y);
        appliedLayer.Children.Add(rect);
    }

    /// <summary>
    /// Add an applied redaction overlay already expressed in viewer DIPs.
    /// Prefer <see cref="AddAppliedRedaction(PdfPageRect)"/> for new code.
    /// </summary>
    public void AddAppliedRedaction(Rect area) =>
        AddAppliedRedaction(ViewerDipsRect(area, CurrentPage));

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
