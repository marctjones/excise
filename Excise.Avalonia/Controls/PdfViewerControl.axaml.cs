using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
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
using SkiaSharp;

namespace Excise.Avalonia.Controls;

/// <summary>
/// Reusable PDF viewer control with zoom, pan, and overlay support.
/// </summary>
public partial class PdfViewerControl : UserControl, IFormFieldEditSink, IViewerState
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
    /// Interaction mode: what a pointer drag on the page does.
    /// </summary>
    public static readonly StyledProperty<InteractionMode> InteractionModeProperty =
        AvaloniaProperty.Register<PdfViewerControl, InteractionMode>(nameof(InteractionMode));

    public InteractionMode InteractionMode
    {
        get => GetValue(InteractionModeProperty);
        set => SetValue(InteractionModeProperty, value);
    }

    /// <summary>
    /// How <see cref="InteractionMode.PathAnnotation"/> collects points —
    /// a freehand stroke or a two-point segment (#934 D, E).
    /// </summary>
    public static readonly StyledProperty<PathCaptureKind> PathCaptureKindProperty =
        AvaloniaProperty.Register<PdfViewerControl, PathCaptureKind>(nameof(PathCaptureKind));

    public PathCaptureKind PathCaptureKind
    {
        get => GetValue(PathCaptureKindProperty);
        set => SetValue(PathCaptureKindProperty, value);
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
    /// How selected/copied text is linearised into reading order (#774).
    /// Defaults to <see cref="ReadingOrderStrategy.ColumnAware"/> — the
    /// highest-quality multi-column copy. Changing it drops the cached
    /// per-page letter ordering so the next selection uses the new strategy.
    /// </summary>
    public static readonly StyledProperty<ReadingOrderStrategy> ReadingOrderStrategyProperty =
        AvaloniaProperty.Register<PdfViewerControl, ReadingOrderStrategy>(
            nameof(ReadingOrderStrategy), defaultValue: ReadingOrderStrategy.ColumnAware);

    public ReadingOrderStrategy ReadingOrderStrategy
    {
        get => GetValue(ReadingOrderStrategyProperty);
        set => SetValue(ReadingOrderStrategyProperty, value);
    }

    /// <summary>
    /// How copied text is whitespaced between lines — paragraph/list-aware
    /// <see cref="WhitespaceMode.Smart"/> (default) or the older
    /// line-faithful mode. Independent of reading order: this only affects the
    /// separators <see cref="TextSelectionEngine.JoinText(System.Collections.Generic.IReadOnlyList{Excise.Core.Text.Letter}, WhitespaceMode)"/>
    /// inserts, so changing it needs no re-sort — only the accessible-text cache
    /// is dropped.
    /// </summary>
    public static readonly StyledProperty<WhitespaceMode> WhitespaceModeProperty =
        AvaloniaProperty.Register<PdfViewerControl, WhitespaceMode>(
            nameof(WhitespaceMode), defaultValue: WhitespaceMode.Smart);

    public WhitespaceMode WhitespaceMode
    {
        get => GetValue(WhitespaceModeProperty);
        set => SetValue(WhitespaceModeProperty, value);
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
    /// Whether the page's annotations are drawn. Default is <c>true</c>.
    ///
    /// Off renders the page content stream alone. Annotations are genuinely
    /// part of what a conforming viewer shows (§12.5), so this is not a
    /// fidelity switch — it answers a different question: what is IN the page
    /// versus what is overlaid on it. For a redaction tool that distinction
    /// matters, because a FreeText annotation looks like page content and is
    /// not, and a Widget's value is real text living outside the content
    /// stream entirely.
    /// </summary>
    public static readonly StyledProperty<bool> ShowAnnotationsProperty =
        AvaloniaProperty.Register<PdfViewerControl, bool>(nameof(ShowAnnotations), defaultValue: true);

    public bool ShowAnnotations
    {
        get => GetValue(ShowAnnotationsProperty);
        set => SetValue(ShowAnnotationsProperty, value);
    }


    /// <summary>
    /// Show COMMENT annotations — notes, FreeText, text markup, shapes, Ink,
    /// Stamp, FileAttachment, Caret (#1021).
    /// </summary>
    public static readonly StyledProperty<bool> ShowCommentAnnotationsProperty =
        AvaloniaProperty.Register<PdfViewerControl, bool>(nameof(ShowCommentAnnotations), defaultValue: true);

    public bool ShowCommentAnnotations
    {
        get => GetValue(ShowCommentAnnotationsProperty);
        set => SetValue(ShowCommentAnnotationsProperty, value);
    }

    /// <summary>
    /// Show FORM FIELDS and LINKS. Separate from comments because a field's
    /// value is page content a reviewer must see even with markup hidden.
    /// </summary>
    public static readonly StyledProperty<bool> ShowFieldAndLinkAnnotationsProperty =
        AvaloniaProperty.Register<PdfViewerControl, bool>(nameof(ShowFieldAndLinkAnnotations), defaultValue: true);

    public bool ShowFieldAndLinkAnnotations
    {
        get => GetValue(ShowFieldAndLinkAnnotationsProperty);
        set => SetValue(ShowFieldAndLinkAnnotationsProperty, value);
    }

    /// <summary>
    /// AUDIT MODE — reveal annotations that <c>/F</c> Hidden or NoView
    /// suppresses. Off by default; it draws what no conforming viewer shows.
    /// </summary>
    public static readonly StyledProperty<bool> RevealHiddenAnnotationsProperty =
        AvaloniaProperty.Register<PdfViewerControl, bool>(nameof(RevealHiddenAnnotations));

    public bool RevealHiddenAnnotations
    {
        get => GetValue(RevealHiddenAnnotationsProperty);
        set => SetValue(RevealHiddenAnnotationsProperty, value);
    }

    /// <summary>
    /// Tint fillable form fields. Off by default — viewer chrome, never
    /// exported.
    /// </summary>
    public static readonly StyledProperty<bool> HighlightFormFieldsProperty =
        AvaloniaProperty.Register<PdfViewerControl, bool>(nameof(HighlightFormFields));

    public bool HighlightFormFields
    {
        get => GetValue(HighlightFormFieldsProperty);
        set => SetValue(HighlightFormFieldsProperty, value);
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
    /// The 1-based page under the pointer at the moment a right-click opened the context menu, or 0
    /// when no menu is open (#1817). "Current page" commands run from that menu act on THIS page:
    /// in continuous view the page under the pointer is not the page filling the viewport, and
    /// rotating or removing the wrong one is the failure this exists to prevent. Set on the
    /// right-press and cleared when the menu closes, so a toolbar command afterwards is unaffected.
    /// </summary>
    public static readonly StyledProperty<int> ContextMenuPageNumberProperty =
        AvaloniaProperty.Register<PdfViewerControl, int>(nameof(ContextMenuPageNumber));

    public int ContextMenuPageNumber
    {
        get => GetValue(ContextMenuPageNumberProperty);
        set => SetValue(ContextMenuPageNumberProperty, value);
    }

    /// <summary>
    /// The annotation under the pointer when a right-click opened the context menu, or null (#1815).
    /// Never a link, popup or form field, and never a hidden one. Cleared when the menu closes.
    /// </summary>
    public static readonly StyledProperty<Excise.Core.Document.PdfAnnotation?> ContextMenuAnnotationProperty =
        AvaloniaProperty.Register<PdfViewerControl, Excise.Core.Document.PdfAnnotation?>(nameof(ContextMenuAnnotation));

    public Excise.Core.Document.PdfAnnotation? ContextMenuAnnotation
    {
        get => GetValue(ContextMenuAnnotationProperty);
        set => SetValue(ContextMenuAnnotationProperty, value);
    }

    /// <summary>
    /// Supplies the AcroForm fields of ANY page by 1-based number. The continuous
    /// view shows many pages at once, so the current-page-only
    /// <see cref="FormFields"/> cannot feed it (#1807); each realized page slot
    /// asks for its own page's fields. Null leaves the continuous view read-only.
    /// </summary>
    public static readonly StyledProperty<Func<int, System.Collections.Generic.IReadOnlyList<Excise.Core.Document.PdfField>>?> PageFormFieldsProviderProperty =
        AvaloniaProperty.Register<PdfViewerControl, Func<int, System.Collections.Generic.IReadOnlyList<Excise.Core.Document.PdfField>>?>(nameof(PageFormFieldsProvider));

    public Func<int, System.Collections.Generic.IReadOnlyList<Excise.Core.Document.PdfField>>? PageFormFieldsProvider
    {
        get => GetValue(PageFormFieldsProviderProperty);
        set => SetValue(PageFormFieldsProviderProperty, value);
    }

    /// <summary>
    /// Asked before an input's edit is stored in its field (#1874). False refuses the edit:
    /// the field keeps its value and the input shows it again. The host enforces the
    /// document's form-fill permission here and tells the user why; a refusal after the
    /// store could not take the value back out of the field. Null admits every edit.
    /// </summary>
    public static readonly StyledProperty<Func<bool>?> FormFieldEditGateProperty =
        AvaloniaProperty.Register<PdfViewerControl, Func<bool>?>(nameof(FormFieldEditGate));

    public Func<bool>? FormFieldEditGate
    {
        get => GetValue(FormFieldEditGateProperty);
        set => SetValue(FormFieldEditGateProperty, value);
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
    /// Raised when the pointer enters or leaves an annotation carrying
    /// <c>/Contents</c> or <c>/T</c> (#1074), with a one-line description or
    /// null on exit.
    ///
    /// <para>Links are excluded — they have their own hover affordance via
    /// <see cref="LinkHovered"/>, and two strings competing for one status line
    /// helps nobody.</para>
    ///
    /// <para>This is READ AND DISPLAY only. It surfaces text already in the
    /// file; nothing here creates, edits or deletes an annotation.</para>
    /// </summary>
    public event EventHandler<AnnotationHoveredEventArgs>? AnnotationHovered;

    /// <summary>
    /// Fired when the user clicks an EXISTING /Text annotation's icon (#1788).
    /// Ambient — fires in every <see cref="InteractionMode"/>, exactly like a
    /// link click, so a note can be reopened for editing without first
    /// switching away from whatever tool is active.
    /// </summary>
    public event EventHandler<StickyNoteClickedEventArgs>? StickyNoteClicked;

    /// <summary>
    /// Fired when <see cref="InteractionMode.StickyNote"/> is active and the
    /// user clicks a page point that is NOT an existing note's icon (#1788).
    /// A single click, not a drag — the host places the note's icon exactly
    /// there and opens its popup immediately.
    /// </summary>
    public event EventHandler<StickyNotePlacementRequestedEventArgs>? StickyNotePlacementRequested;

    /// <summary>
    /// Fired when a press-and-drag on an existing, NOT-currently-editing note
    /// moves it past the click/drag threshold (#1794) — the drag counterpart
    /// to <see cref="StickyNoteClicked"/> for the same ambient press/release
    /// gesture on a note's on-screen card. See
    /// <c>OnInteractionLayerPointerReleased</c> for the disambiguation.
    /// </summary>
    public event EventHandler<StickyNoteMovedEventArgs>? StickyNoteMoved;

    /// <summary>
    /// Fired when the user edits an AcroForm field via the FormFieldsLayer
    /// inputs. <see cref="FormFieldEditGate"/> admitted the edit and the control
    /// has already mutated the underlying PdfField; the host typically reacts
    /// by re-rendering the page so any baked-in appearance is refreshed.
    /// </summary>
    public event EventHandler<FormFieldEditedEventArgs>? FormFieldEdited;

    /// <summary>
    /// Fired when a field refuses an edit because it cannot represent the text
    /// (#1671) — the value was NOT stored and the input reverts to the stored
    /// value. The host must tell the user; nothing else will.
    /// </summary>
    public event EventHandler<FormFieldEditRejectedEventArgs>? FormFieldEditRejected;

    /// <summary>
    /// Fired when the user finishes drawing a new field rect in
    /// FormAuthoring mode. Carries the rect in PDF points (bottom-left
    /// origin) plus the host page number.
    /// </summary>
    public event EventHandler<FormFieldRectDrawnEventArgs>? FormFieldRectDrawn;

    /// <summary>
    /// Fired when the user finishes drawing a rect in
    /// <see cref="InteractionMode.ShapeAnnotation"/> mode. Carries the rect in
    /// PDF points (bottom-left origin) plus the host page number — the host
    /// decides which annotation type it becomes.
    /// </summary>
    public event EventHandler<ShapeAnnotationRectDrawnEventArgs>? ShapeAnnotationRectDrawn;

    /// <summary>
    /// Raised when the user finishes a free-form drawing gesture in
    /// <see cref="InteractionMode.PathAnnotation"/> mode. Points are already in
    /// PDF content coordinates (#934 D).
    /// </summary>
    public event EventHandler<AnnotationPathDrawnEventArgs>? AnnotationPathDrawn;

    public event EventHandler<TypewriterTextCreatedEventArgs>? TypewriterTextCreated;
    public event EventHandler<TypewriterTextEditedEventArgs>? TypewriterTextEdited;
    public event EventHandler<TypewriterTextBoundsChangedEventArgs>? TypewriterTextBoundsChanged;
    public event EventHandler<TypewriterTextDeletedEventArgs>? TypewriterTextDeleted;

    #endregion

    #region Fields

    private Point _dragStart;
    private bool _isDragging;

    /// <summary>
    /// Click-vs-drag disambiguation for an ambient press on an existing,
    /// resting sticky note (#1794) — staged on press, resolved on release.
    /// See <c>PdfViewerControl.Interaction.cs</c>'s press/release handlers.
    /// </summary>
    /// <param name="IconRect">The note's own /Rect — its identity, unaffected by a move (#1797).</param>
    /// <param name="CardRect">The card's current /Rect (linked /Popup's own /Rect) — what a drag actually repositions.</param>
    private readonly record struct StickyNoteDragCandidate(
        int PageNumber, PdfRectangle IconRect, PdfRectangle CardRect, Point PressDips, double PressPdfX, double PressPdfY);

    private StickyNoteDragCandidate? _stickyNoteDragCandidate;

    // The single-page view's LOGICAL layout DPI: the Image is laid out at
    // pt × 120/72 DIPs, and the overlay, hit-testing and every redaction,
    // typewriter and form rect map through it. Despite the name it has not
    // been the render DPI since #682 made the raster follow the display, and
    // since #1487 the raster is at the device resolution, 96 × zoom × dpr
    // (SinglePageRenderPlan). It began as a render DPI, chosen over 200 for
    // 3× less rasterisation work; changing it now would move input mapping.
    internal const int DefaultRenderDpi = 120;
    // One raster pixel per device pixel per unit of zoom × dpr (#1487): an
    // Avalonia DIP is 1/96 inch. Same basis as ContinuousBaseDpi (#1480).
    internal const int SinglePageDeviceBaseDpi = 96;
    internal const int MinSinglePageRenderDpi = 12;
    internal const long MaxSinglePagePreviewPixels = 64L * 1024L * 1024L;

    // Per-page link and annotation lists for hit-testing, both views (#1842).
    // The single-page view drops the links when its page changes, as it
    // always has.
    private readonly ViewerPageCaches _pageCaches;
    /// <summary>Last link the pointer hovered, for hover enter/exit edge detection (#625).</summary>
    private PdfLink? _lastHoveredLink;

    #endregion

    public PdfViewerControl()
    {
        _pageCaches = new ViewerPageCaches(() => Document);
        InitializeComponent();
        ApplyViewTemplates();
        WireTemplateParts();
        MetricsViewerId = ViewerMetrics.Register(this);
        Focusable = true;
        UpdateViewerAutomationProperties();
        DetachedFromVisualTree += OnDetachedFromVisualTreeHandler;
        AttachedToVisualTree += OnAttachedToVisualTreeHandler;
    }

    /// <summary>
    /// The mirror of <see cref="OnDetachedFromVisualTreeHandler"/> (#1929): restore the
    /// viewport subscription and re-arm the continuous view, so a viewer moved to another
    /// host keeps reporting its viewport and following scrolls.
    /// </summary>
    private void OnAttachedToVisualTreeHandler(object? sender, VisualTreeAttachmentEventArgs e)
    {
        SubscribeSinglePageViewport();
        ContinuousPart.OnAttached();
    }

    /// <summary>
    /// ⚠️ Class handlers are STATIC scope — one registration fires for every
    /// instance of the control. These lived in the instance constructor for a
    /// long time, which meant every constructed viewer added a duplicate
    /// handler set: with N viewers ever created in the process, one property
    /// change invoked each handler N times. The app creates a single viewer
    /// so it never noticed; the test host creates dozens, and the duplicate
    /// OnZoomLevelChanged invocations re-entered ApplyContinuousZoom and
    /// destroyed the #700 zoom anchor mid-flight (plus N-fold duplicate
    /// renders everywhere else). Register once, in the static constructor.
    /// </summary>
    static PdfViewerControl()
    {
        DocumentProperty.Changed.AddClassHandler<PdfViewerControl>((control, e) =>
            control.OnDocumentChanged());
        CurrentPageProperty.Changed.AddClassHandler<PdfViewerControl>((control, e) =>
            control.OnCurrentPageChanged());
        ZoomLevelProperty.Changed.AddClassHandler<PdfViewerControl>((control, e) =>
            control.OnZoomLevelChanged());
        IsLoadingProperty.Changed.AddClassHandler<PdfViewerControl>((control, e) =>
            control.SinglePagePart.OnLoadingStateChanged());
        HasErrorProperty.Changed.AddClassHandler<PdfViewerControl>((control, e) =>
            control.SinglePagePart.OnErrorStateChanged());
        ErrorMessageProperty.Changed.AddClassHandler<PdfViewerControl>((control, e) =>
            control.SinglePagePart.OnErrorMessageChanged());
        AnnotationsProperty.Changed.AddClassHandler<PdfViewerControl>((control, _) =>
            control.SinglePagePart.RedrawAnnotationsLayer());
        FormFieldsProperty.Changed.AddClassHandler<PdfViewerControl>((control, _) =>
        {
            control.SinglePagePart.RedrawFormFieldsLayer();
            control.ContinuousPart.RefreshContinuousFormFieldsIfChanged();
        });
        PageFormFieldsProviderProperty.Changed.AddClassHandler<PdfViewerControl>((control, _) =>
            control.ContinuousPart.RefreshContinuousFormFieldsIfChanged());

        // #1817: forget the right-clicked page when its menu closes. Posted, not immediate: a menu
        // item's command runs around the time the menu closes and must still see the page. Registered
        // here, once per process, like every handler above (a per-instance class handler stacks).
        ContextMenuProperty.Changed.AddClassHandler<PdfViewerControl>((control, e) =>
        {
            if (e.OldValue is ContextMenu closedOld) closedOld.Closed -= control.OnContextMenuClosed;
            if (e.NewValue is ContextMenu added) added.Closed += control.OnContextMenuClosed;
        });
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
        ReadingOrderStrategyProperty.Changed.AddClassHandler<PdfViewerControl>((control, _) =>
            control.OnReadingOrderStrategyChanged());
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

            // #1648: leaving typewriter mode discards boxes nobody typed in.
            // An empty box is a click the user backed out of; keeping it leaves
            // an invisible artefact in the document that only reappears when
            // they next enter the mode.
            if (e.OldValue is InteractionMode.Typewriter && e.NewValue is not InteractionMode.Typewriter)
                control.SinglePagePart.DiscardEmptyPendingTypewriterText();

            // #1648: nothing is being edited until the user picks a box. Without
            // this, re-entering the mode dresses whichever box was last focused
            // — the reader is shown an editing box they did not ask for, and the
            // page stops looking like the document.
            if (e.NewValue is not InteractionMode.Typewriter)
                control.SinglePagePart.ClearTypewriterFocus();

            control.SinglePagePart.RedrawTypewriterLayer();
        });
    }

    private void OnDetachedFromVisualTreeHandler(object? sender, VisualTreeAttachmentEventArgs e)
    {
        // A detached single-page viewer must not publish an in-flight result
        // into controls that are no longer attached (the view's Detach).
        SinglePagePart.Detach();

        // A detached viewer (e.g. a closed window during a test) must do NO more
        // continuous rendering: a queued render pass / in-flight cell completion
        // touching the now-disposed document would throw and destabilise the
        // shared dispatcher (observed as a cross-test ObjectDisposedException
        // cleanup-failure cascade). The continuous view's Detach sets the flag
        // that hard-stops all continuous work, drops its scroll subscriptions and
        // container hooks, and cancels in-flight cell renders (#848).
        _viewportSubscription?.Dispose();
        _viewportSubscription = null;
        ContinuousPart.Detach();
    }

    /// <summary>
    /// An interaction mode that DRAWS/EDITS onto a single rendered page and
    /// therefore needs single-page layout, so entering it in the continuous
    /// reading view auto-switches back to single-page. Text selection is
    /// deliberately excluded (#815): it is a read affordance like link clicking,
    /// not an edit, and now works in the continuous view via a per-page selection
    /// overlay — so it must NOT force single-page.
    /// </summary>
    private static bool IsEditingMode(InteractionMode m) =>
        m is InteractionMode.Redaction or InteractionMode.FormAuthoring or InteractionMode.Typewriter;

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
            // #1648: placing a box abandons any earlier one nobody typed in.
            // The new box is exempt — it is empty by definition.
            if (e.Action is System.Collections.Specialized.NotifyCollectionChangedAction.Add
                && e.NewItems?.Count == 1
                && e.NewItems[0] is Excise.Core.Editing.PdfTypewriterTextOperation added)
            {
                DiscardEmptyPendingTypewriterText(except: added.Id);
            }

            RedrawTypewriterLayer();
        }
    }


    internal static Rect ToAvaloniaRect(PdfPageRect rect) =>
        new(rect.X, rect.Y, rect.Width, rect.Height);


    /// <summary>
    /// A vertex path in progress belongs to the mode and gesture that started
    /// it. Leaving draw mode — or switching Polygon to Ink — must not leave a
    /// half-built path to be committed later by an unrelated click.
    /// </summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == InteractionModeProperty ||
            change.Property == PathCaptureKindProperty)
        {
            CancelVertexPath();
        }

        // Toggling annotations changes the PIXELS, so cached page bands are
        // stale. Without this the setting appears to do nothing until something
        // else happens to invalidate the cache — which is how a toggle ends up
        // reported as "doesn't work" when the renderer is fine.
        if (change.Property == ShowAnnotationsProperty
            || change.Property == ShowCommentAnnotationsProperty
            || change.Property == ShowFieldAndLinkAnnotationsProperty
            || change.Property == RevealHiddenAnnotationsProperty
            || change.Property == HighlightFormFieldsProperty)
        {
            ContinuousPart.InvalidateContinuousCache();
            SinglePagePart.InvalidateSinglePageLookAhead();
            InvalidateVisual();
            // #1473: no hidden single-page render in continuous view; the switch
            // to single-page renders with the new annotation settings. The
            // single-page cache is keyed by page and DPI only, so drop it here
            // or that switch would show bitmaps rendered with the old settings.
            // Dropping it disposes the bitmap the hidden Image may still show.
            if (ViewMode == PdfViewMode.Continuous)
            {
                InvalidatePageCache();
                ClearDisplay();
            }
            else
            {
                _ = RenderCurrentPageAsync();
            }
        }
    }


    // #1842: the facade is the one state both views read (IViewerState); the
    // interface is implemented by the styled properties above, except this one.
    double IViewerState.RenderScaling => EffectiveRenderScaling;

    // #1842: the facade is the sink every form-field input reports to, in both views.
    bool IFormFieldEditSink.AdmitEdit() => FormFieldEditGate?.Invoke() != false;

    void IFormFieldEditSink.EditStored(Excise.Core.Document.PdfField field, string? newValue, string? oldValue) =>
        // The field's own page: in continuous view (#1807) several pages are
        // editable at once, so CurrentPage is only the scroll anchor. Read at
        // commit time, not when the input was built.
        FormFieldEdited?.Invoke(this,
            new FormFieldEditedEventArgs(field, newValue, field.PageNumber ?? CurrentPage, oldValue));

    void IFormFieldEditSink.EditRejected(string fieldName, string message) =>
        FormFieldEditRejected?.Invoke(this, new FormFieldEditRejectedEventArgs(fieldName, message));


    /// <summary>
    /// Give the two views their templates now (#1842 Phase B). A <c>TemplatedControl</c> is
    /// otherwise templated when it is first styled and measured, which for a viewer outside a
    /// window never happens; the wiring below, the viewer's API and its tests use the views'
    /// parts from construction on, as they did when the views were <c>UserControl</c>s.
    /// <c>ApplyStyling</c> finds each view's ControlTheme in this control's own resources
    /// (PdfViewerControl.axaml) through the view's logical parent, before the viewer has a
    /// window; attaching to one later keeps the same template.
    /// </summary>
    private void ApplyViewTemplates()
    {
        foreach (var view in new global::Avalonia.Controls.Primitives.TemplatedControl[] { ContinuousPart, SinglePagePart })
        {
            view.ApplyStyling();
            if (view.Template is null)
                throw new InvalidOperationException(
                    $"{view.GetType().Name} has no ControlTheme in reach; PdfViewerControl.axaml merges it (#1842).");
            view.ApplyTemplate();
        }
    }

    /// <summary>
    /// Wire the template parts after the generated <c>InitializeComponent</c> has
    /// loaded the XAML (once) and <see cref="ApplyViewTemplates"/> has given the two
    /// views their parts (#1842). The nine root
    /// <c>AddHandler</c> registrations below must stay in this file: the GUI
    /// interaction registry keys its viewer rows on it.
    /// </summary>
    private void WireTemplateParts()
    {
        WireSinglePageView();

        // Pointer handlers — attached at the UserControl root level using
        // AddHandler with handledEventsToo:true so they fire even when an
        // intermediate control (e.g. an invisible-but-hit-testable
        // overlay Grid) intercepts the bubble path. Pre-fix attachment
        // was on _interactionLayer (zero-sized — never received events)
        // and then on ZoomHost (skipped when ErrorOverlay sat as a
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
        // Leaving the control must clear hover feedback. The move handler
        // cannot do it — it only runs while the pointer is INSIDE — so without
        // this the status bar keeps describing something the pointer is nowhere
        // near. Affected link hover (#625) from the start; annotation hover
        // (#1074) inherited it.
        //
        // ⚠️ DIRECT, not Tunnel|Bubble. PointerExited and PointerEntered are
        // registered RoutingStrategies.Direct in Avalonia (verified against
        // InputElement.PointerExitedEvent.RoutingStrategies, which reports
        // Direct where PointerMoved reports Tunnel, Bubble). A Tunnel|Bubble
        // registration here never fires at all — which it did not, and the
        // silence was misread as "headless does not raise the event" and cost
        // this fix a round trip (#1075).
        AddHandler(PointerExitedEvent, OnViewerPointerExited,
            global::Avalonia.Interactivity.RoutingStrategies.Direct,
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

        // Wheel-zoom and middle-button pan (#827). Registered on Tunnel ONLY so
        // the root sees the gesture before the inner ScrollViewer's own bubble
        // handler — letting us suppress native scroll for Ctrl+wheel by marking
        // the event Handled. Tunnel-only (not Tunnel|Bubble) deliberately: the
        // #675 double-fire came from registering both passes.
        AddHandler(PointerWheelChangedEvent, OnViewerPointerWheelChanged,
            global::Avalonia.Interactivity.RoutingStrategies.Tunnel,
            handledEventsToo: true);
        AddHandler(PointerPressedEvent, OnPanPointerPressed,
            global::Avalonia.Interactivity.RoutingStrategies.Tunnel,
            handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnPanPointerMoved,
            global::Avalonia.Interactivity.RoutingStrategies.Tunnel,
            handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnPanPointerReleased,
            global::Avalonia.Interactivity.RoutingStrategies.Tunnel,
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
        SubscribeSinglePageViewport();

        WireContinuousView();
    }

    /// <summary>
    /// Subscribe to the single-page scroller's viewport. Runs at construction and on every
    /// attach (#1929); the detach handler disposes it. Once while subscribed.
    /// </summary>
    private void SubscribeSinglePageViewport()
    {
        if (_viewportSubscription != null || SinglePagePart.PdfScrollViewer == null) return;

        // AnonymousObserver (Avalonia.Reactive) rather than a Subscribe(Action<T>)
        // overload — the latter comes from System.Reactive (Rx), which this
        // library deliberately does NOT depend on (the app got it transitively
        // via ReactiveUI). Avalonia ships AnonymousObserver for exactly this. (#365)
        _viewportSubscription = SinglePagePart.PdfScrollViewer
            .GetObservable(ScrollViewer.ViewportProperty)
            .Subscribe(new AnonymousObserver<Size>(OnScrollViewerViewportChanged));
    }

    private void OnContextMenuClosed(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            ContextMenuPageNumber = 0;
            ContextMenuAnnotation = null;
        }, DispatcherPriority.Background);

    /// <summary>
    /// The actual visible page area, in DIPs, *inside* the scroll bars.
    /// Use this — not the outer control's Bounds — for fit-zoom math, so
    /// the answer doesn't include the strip a vertical scrollbar steals.
    /// </summary>
    public Size GetVisibleViewportSize()
    {
        if (ViewMode == PdfViewMode.Continuous && ContinuousPart.ContinuousScrollViewer != null)
        {
            var cv = ContinuousPart.ContinuousScrollViewer.Viewport;
            if (cv.Width > 0 && cv.Height > 0) return cv;
        }

        if (SinglePagePart.PdfScrollViewer != null)
        {
            var v = SinglePagePart.PdfScrollViewer.Viewport;
            if (v.Width > 0 && v.Height > 0) return v;
        }
        return Bounds.Size;
    }

    /// <summary>Raised when the inside-the-scrollbars viewport size changes.</summary>
    public event EventHandler<Size>? VisibleViewportChanged;

    private Size _lastReportedViewport;
    private IDisposable? _viewportSubscription;

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

        // Enter / Escape / Backspace terminate a vertex path (#934 F). Checked
        // first and only while one is in progress, so these keys stay available
        // to everything else the rest of the time.
        if (HandleVertexPathKey(e.Key))
        {
            e.Handled = true;
            return;
        }

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
                case Key.Down:
                    // Down joins PageDown/Right as next-page nav. The window-level
                    // MainWindow_KeyDown maps Down→next page too, but arrow keys
                    // never reach that BUBBLING handler as unhandled (Avalonia's
                    // input pipeline consumes them first), so the viewer's TUNNEL
                    // handler is the only place Down actually routes. This mirrors
                    // Left/Right, which already page-navigate here. (#827)
                    NextPage();
                    handled = true;
                    break;
                case Key.PageUp:
                case Key.Left:
                case Key.Up:
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
                case Key.H:
                    // Structure navigation (#631): H jumps to the next heading,
                    // Shift+H to the previous — the screen-reader convention —
                    // crossing pages as needed. No-op (unhandled) on untagged
                    // documents so the key stays available to other handlers.
                    handled = MoveToNextStructure(
                        backward: e.KeyModifiers.HasFlag(KeyModifiers.Shift),
                        headingsOnly: true);
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

        NotifyAutomationPageTextChangedIfNeeded();
    }

    /// <summary>
    /// Expose the document content to the platform accessibility tree via a
    /// custom peer that carries the current page's text (issue #631). The
    /// rendered page is otherwise an opaque bitmap to screen readers.
    /// </summary>
    protected override AutomationPeer OnCreateAutomationPeer() =>
        new Excise.Avalonia.Automation.PdfViewerAutomationPeer(this);

    // Content identity last announced to the automation tree; used to raise a
    // Name change on the synthetic page-text peer only when the page's text
    // could actually have changed (page/document/content-rewrite), not on
    // zoom or view-mode churn that also refreshes automation properties.
    private object? _announcedTextDocument;
    private int _announcedTextPage = -1;
    private long _announcedTextRenderVersion = -1;

    private void NotifyAutomationPageTextChangedIfNeeded()
    {
        if (ReferenceEquals(_announcedTextDocument, Document)
            && _announcedTextPage == CurrentPage
            && _announcedTextRenderVersion == RenderVersion)
            return;

        _announcedTextDocument = Document;
        _announcedTextPage = CurrentPage;
        _announcedTextRenderVersion = RenderVersion;

        // FromElement returns null until some automation client materialized
        // the peer — no screen reader, no cost.
        (ControlAutomationPeer.FromElement(this) as Excise.Avalonia.Automation.PdfViewerAutomationPeer)
            ?.NotifyPageTextChanged();
    }

    // Cache for the joined accessible page text. Keyed by reference identity
    // of the reading-ordered letter list: every invalidation path (page
    // change, document change, content rewrite via RenderVersion) replaces
    // _readingOrderedLetters, so a stale join can never be returned.
    private List<Letter>? _accessibleTextSource;
    private string? _accessibleTextCache;

    /// <summary>
    /// The current page's extractable text in reading order, for the
    /// accessibility tree (issue #631). Uses the same letter pipeline as
    /// text-selection copy: geometric reading order (top-to-bottom lines,
    /// left-to-right in a line) joined with word/line breaks. Empty when no
    /// document is loaded or the page has no extractable text.
    /// </summary>
    internal string GetAccessiblePageText()
    {
        if (Document == null || CurrentPage < 1 || CurrentPage > Document.PageCount)
            return string.Empty;

        var source = SinglePagePart.LoadedReadingOrderedLetters();
        if (source == null || source.Count == 0)
            return string.Empty;

        if (!ReferenceEquals(source, _accessibleTextSource) || _accessibleTextCache == null)
        {
            // Accessible text stays LineFaithful on purpose: WhitespaceMode is a
            // COPY preference. Smart mode's paragraph blank lines and list
            // indentation would change what a screen reader announces (spoken
            // pauses/indentation) — out of scope for this feature.
            _accessibleTextCache = TextSelectionEngine.JoinText(source);
            _accessibleTextSource = source;
        }

        return _accessibleTextCache;
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
        SinglePagePart.OnZoomLevelChanged();
        if (ViewMode == PdfViewMode.Continuous)
        {
            ContinuousPart.ApplyContinuousZoom();
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


    #region Property reactions and the single-page render plan

    private async void OnDocumentChanged()
    {
        // Drop cached bitmaps from the prior document (would render at wrong
        // pages otherwise) and cancel any render that was still finishing
        // for that document. Same for the page-letters cache used by
        // text-selection — if it referenced a page from the old document
        // we'd hit-test against stale glyphs.
        InvalidatePageCache();
        SinglePagePart.CancelRender();
        IsLoading = false;
        SinglePagePart.ForgetPageLetters();
        _pageCaches.ClearLinks();
        ClearSelectionHighlight();

        var keepPagesOnScreen = ContinuousPart.TakeKeepPagesOnScreenRequest();
        ContinuousPart.InvalidateContinuousCache(keepPagesOnScreen);
        if (Document != null)
        {
            RefreshPageAnnotations();
            RedrawTypewriterLayer();
            if (ViewMode == PdfViewMode.Continuous)
            {
                // #1876: a save's reload renders into the slots it kept.
                if (keepPagesOnScreen)
                    ContinuousPart.RenderVisibleContinuousTiles();
                else
                    ContinuousPart.RebuildContinuous();
                // #1473: the single-page Image is hidden in continuous view, so
                // rendering it here was a full page render nobody saw, plus a
                // bitmap held in the single-page cache. OnViewModeChanged renders
                // the current page when single-page becomes visible. Drop the
                // hidden Image's Source: InvalidatePageCache above has disposed
                // it, and an Image measured with a disposed bitmap throws.
                ClearDisplay();
            }
            else
            {
                await RenderCurrentPageAsync();
            }
        }
        else
        {
            Annotations = null;
            RedrawTypewriterLayer();
            ClearDisplay();
            ContinuousPart.ClearContinuous();
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
                    ContinuousPart.ScrollToPageContinuous(CurrentPage);
                }

                PageChanged?.Invoke(this, new PageChangedEventArgs(CurrentPage));
                UpdateViewerAutomationProperties();
                return;
            }

            // Drop selection state from the previous page — the cached
            // letters won't match the new page's geometry and we'd
            // otherwise hit-test against stale glyphs.
            SinglePagePart.ForgetPageLetters();
            SinglePagePart.ForgetLetterSelection();
            _pageCaches.ClearLinks();
            ClearSelectionHighlight();

            // Load annotations for the new page and refresh the overlay.
            RefreshPageAnnotations();
            RedrawTypewriterLayer();

            await RenderCurrentPageAsync();
            PageChanged?.Invoke(this, new PageChangedEventArgs(CurrentPage));
            UpdateViewerAutomationProperties();

            // A switch to continuous view that happened while this render awaited
            // already carried the reading fraction through OnViewModeChanged.
            // Do not replace it with a late page-top scroll (#1931).
        }
    }

    /// <summary>
    /// The reading-order strategy changed (#774): drop the cached per-page
    /// letter ordering (single-page and continuous) so the next selection
    /// re-sorts with the new strategy. Any in-flight selection endpoints are
    /// cleared because they reference letters from the stale ordering.
    /// </summary>
    private void OnReadingOrderStrategyChanged()
    {
        SinglePagePart.ForgetReadingOrder();
        SinglePagePart.ForgetLetterSelection();
        ContinuousPart.InvalidateContinuousCache();
    }

    private void OnRenderVersionChanged()
    {
        if (Document == null)
            return;

        InvalidatePageCache();
        ContinuousPart.InvalidateContinuousCache();
        SinglePagePart.ForgetPageLetters();
        SinglePagePart.ForgetLetterSelection();
        _pageCaches.ClearLinks();
        ClearSelectionHighlight();
        RefreshPageAnnotations();
        RedrawTypewriterLayer();

        if (ViewMode == PdfViewMode.Continuous)
        {
            ContinuousPart.RebuildContinuous();
            ContinuousPart.RenderVisibleContinuousTiles();
            // InvalidatePageCache above disposed the bitmap the hidden
            // single-page Image may still show (#1473).
            ClearDisplay();
        }
        else
        {
            _ = RenderCurrentPageAsync();
        }

        // A RenderVersion bump means the page content was rewritten (e.g. a
        // redaction was applied) — the accessible page text must follow
        // (issue #631).
        NotifyAutomationPageTextChangedIfNeeded();
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


    internal static int EffectiveSinglePageRenderDpi(PdfPage page)
    {
        var widthPt = page.VisualWidth;
        var heightPt = page.VisualHeight;
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
    /// returns the DPI to rasterize at and the equivalent
    /// BitmapDpi, <c>deviceDpi × 96 / logicalDpi</c>, so that
    /// <c>deviceDpi / (bitmapDpi / 96) == logicalDpi</c> holds exactly. The
    /// Image is NOT sized from the raster through it:
    /// <see cref="SinglePageLayoutSize"/> sizes it from the page geometry
    /// (#1489), which is what keeps the layout size, the ScaleTransform and
    /// every coordinate mapping unchanged; only pixel density changes. Until
    /// #1489 BitmapDpi was <c>96 × scale</c>, a rounding away from the integer
    /// device DPI, and it sized the Image. BitmapDpi is NOT stamped on the
    /// bitmap: Avalonia's Image mispaints non-96-stamped bitmaps as a magnified
    /// top-left pixel crop (#697; DpiStampedBitmapPaintProbeTests) — the bitmap
    /// stays 96-stamped and the Image's Width/Height carry the layout size
    /// instead.
    /// <para>
    /// The DPI is <c>96 × scale</c> — one raster pixel per device pixel —
    /// whatever the logical DPI (#1487). The ZoomHost displays every page at
    /// <c>pt × 96/72 × zoom</c> DIPs and an Avalonia DIP is 1/96 inch, so the
    /// display's device resolution is <c>96 × zoom × dpr</c>. Until #1487 this
    /// was <c>logicalDpi × scale</c>: the 120-dpi LAYOUT scale used as a render
    /// DPI, 1.25× the display's linear resolution and 1.56× its pixels. The
    /// continuous view made the same change in #1480, where a 1:1 render matched
    /// mutool's edge sharpness and the 1.25× render downscaled was softer. Only
    /// pixel density changed; the layout is still sized at the logical DPI.
    /// <c>scale</c> is still floored at 1, so zoomed out below 100% on a 1×
    /// display the page renders at 96 DPI; removing that floor as #1472 did for
    /// the continuous view is a separate change.
    /// </para>
    /// <para>
    /// <paramref name="maxScale"/> is a multiple of the logical DPI and caps the
    /// raster at the single-page memory budget: beyond it the ScaleTransform
    /// upscales (soft at extreme zoom) rather than allocating an unbounded
    /// bitmap. The cap applies to the DPI itself, not to the scale, because a
    /// huge page's logical DPI is already clamped to fit the budget at scale 1
    /// and must not be pushed back over it by the 96-DPI base. It is floored:
    /// rounding up past it can put the ceiled raster over MaxPixelCount, where
    /// the renderer throws instead of rendering.
    /// </para>
    /// </summary>
    internal static (int DeviceDpi, double BitmapDpi) SinglePageRenderPlan(int logicalDpi, double scale, double maxScale)
    {
        double s = Math.Max(1.0, scale <= 0 ? 1.0 : scale);
        int budgetDpi = (int)Math.Floor(logicalDpi * Math.Max(1.0, maxScale) + 1e-9);
        int deviceDpi = Math.Min((int)Math.Round(SinglePageDeviceBaseDpi * s), budgetDpi);
        deviceDpi = Math.Max(MinSinglePageRenderDpi, deviceDpi);
        double bitmapDpi = deviceDpi * 96.0 / logicalDpi;
        return (deviceDpi, bitmapDpi);
    }

    /// <summary>
    /// The single page's layout size in logical DIPs (pure; #1489): the page
    /// geometry at the logical DPI, which is the space the overlay, hit-testing
    /// and every redaction, typewriter and form rect map through. The
    /// placeholder and the published render both size the Image with this,
    /// never with the raster's pixel count.
    /// </summary>
    internal static Size SinglePageLayoutSize(double widthPt, double heightPt, int logicalDpi) =>
        new(widthPt * logicalDpi / PdfPageRect.PdfPointsPerInch,
            heightPt * logicalDpi / PdfPageRect.PdfPointsPerInch);

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

    /// <summary>Drop the cached bitmaps — call when document changes or content edits invalidate prior renders.</summary>
    public void InvalidatePageCache()
    {
        SinglePagePart.InvalidateCache();

        // #1794: a sticky-note edit or drag-to-move mutates an existing
        // annotation's /Rect/Contents on the SAME PdfAnnotation-owning
        // PdfDocument instance in place — the annotation cache holds
        // PdfAnnotation wrappers whose Rect/Contents were captured at parse
        // time, so a stale entry here would hit-test a moved note at its OLD
        // position (or an edited one with its OLD text) until the next
        // unrelated cache-clearing event. Content edits invalidating prior
        // renders is exactly this case, even in single-page view where
        // RefreshContinuousLayout's own clear is a no-op.
        _pageCaches.ClearAnnotations();
    }


    #endregion

    #region Public Methods

    /// <summary>
    /// The document's page structure changed in place (rotate, move, insert, delete) on
    /// the same <see cref="Document"/> instance, so no property change tells the views.
    /// Continuous view rebuilds its layout (<see cref="RefreshContinuousLayout"/>). Single
    /// page drops the selection, whose letters were mapped for the old geometry, and
    /// re-renders the displayed page.
    /// </summary>
    /// <remarks>
    /// #1983: single page used to do nothing here. At an unchanged zoom the Image kept the
    /// pre-rotation bitmap while search highlights were placed for the new rotation, so
    /// every highlight sat off its text. The re-render cannot reuse the old bitmap because
    /// the single-page cache is keyed by rotation as well as page and DPI; the bitmap on
    /// screen is replaced when the new one lands, not disposed first (the hazard the
    /// view model's reload comment warns about).
    /// </remarks>
    public void RefreshAfterStructureChange()
    {
        RefreshContinuousLayout();
        if (Document == null || ViewMode != PdfViewMode.SinglePage)
            return;

        SinglePagePart.ForgetLetterSelection();
        ClearSelectionHighlight();
        _ = RenderCurrentPageAsync();
    }

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


    #endregion
}
