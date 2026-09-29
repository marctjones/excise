using Avalonia.Automation.Peers;
using Avalonia.Automation;
using Avalonia.Collections;
using Avalonia.Controls.Shapes;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Media;
using Avalonia.Reactive;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia;
using Excise.Avalonia.Imaging;
using Excise.Core.Document;
using Excise.Core.Editing;
using Excise.Core.Text;
using Excise.Rendering;
using SkiaSharp;
using System.Collections.Generic;
using System.Threading.Tasks;
using System;
using static Excise.Avalonia.Controls.PdfViewerControl;

namespace Excise.Avalonia.Controls;

/// <summary>
/// The single-page render, its bitmap cache and the overlay layers drawn over the page (#1842).
/// </summary>
internal sealed partial class SinglePageView
{
    private int _currentSinglePageRenderDpi = DefaultRenderDpi;

    // LRU bitmap cache so flipping back to a recently-viewed page is
    // instant. Capped small — bitmaps for a 200-page book can be ~6 MB
    // each in BGRA, so we trade a few tens of MB for snappy navigation.
    private const int PageCacheCapacity = 6;
    // Dip is the Image's LAYOUT size for the entry (logical page dips). It is
    // carried separately because the bitmap itself is stamped 96 DPI —
    // Avalonia's Image mispaints non-96-stamped bitmaps as a magnified
    // top-left pixel crop (#697; DpiStampedBitmapPaintProbeTests).
    private readonly SinglePageRenderLifetime<WriteableBitmap> _singlePageRenderLifetime =
        new(PageCacheCapacity);

    // Text-selection state. Cached letters are in PDF content points (Y-up)
    // for the page currently displayed; hit-testing routes through
    // PdfCoordinateMapper so the render scale and page rotation stay aligned.
    private int _lettersPageNumber = -1;
    private List<Letter>? _currentPageLetters; // raw glyph order
    private List<Letter>? _readingOrderedLetters; // for range slicing
    private double _columnGapThreshold = double.PositiveInfinity; // cached per page (#373)
    private Letter? _selectionAnchor;
    private Letter? _selectionFocus;

    internal void RedrawHiddenTextOverlays()
    {
        var layer = HiddenTextRevealLayer;
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
    // DefaultRenderDpi, but huge page boxes lay out at a lower logical DPI.
    private double ViewerUnitsPerPoint => _currentSinglePageRenderDpi / PdfPageRect.PdfPointsPerInch;

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

    internal PdfPageRect ViewerDipsRect(Rect rect, int pageNumber) =>
        PdfPageRect.ViewerDips(pageNumber, rect.X, rect.Y, rect.Width, rect.Height, _currentSinglePageRenderDpi);

    private PdfPageRect ContentRect(PdfRectangle rect, int pageNumber) =>
        PdfPageRect.FromPdfRectangle(pageNumber, rect, PdfCoordinateSpace.ContentPoints);

    internal void RedrawAnnotationsLayer()
    {
        var layer = AnnotationsLayer;
        if (layer == null) return;
        layer.Children.Clear();

        var annots = Annotations;
        if (annots == null || Document == null) return;

        foreach (var a in annots)
        {
            var (fillColor, strokeColor) = AnnotationColors(a);

            // #1797: a /Text (sticky note) annotation already gets a
            // complete, fully-styled visual from SkiaRenderer.RenderStickyNoteDefault
            // — baked into the page raster this ambient overlay sits ON TOP
            // OF, not the small icon §12.5.6.4 describes. An additional
            // translucent rect here (at either a.Rect, the note's tiny fixed
            // anchor, or a.PopupRect, the already-opaque card) would only add
            // visual noise over a card that already reads clearly on its own
            // ("messed up text display" was THIS layer filling the old,
            // unified 200x150pt /Rect with a translucent tint on top of the
            // SAME card SkiaRenderer draws solid). Nothing else needs this
            // ambient presence indicator the way Highlight/Underline/etc. do,
            // so skip it entirely for Text.
            if (a.Subtype == Excise.Core.Document.PdfAnnotationSubtype.Text)
                continue;

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

    internal void RedrawFormFieldsLayer()
    {
        var layer = FormFieldsLayer;
        if (layer == null) return;
        layer.Children.Clear();

        var fields = FormFields;
        if (fields == null || Document == null || fields.Count == 0) return;

        var orderedFields = FormFieldInputFactory.OrderFormFieldsForTabbing(fields);

        for (var tabIndex = 0; tabIndex < orderedFields.Count; tabIndex++)
        {
            var field = orderedFields[tabIndex];
            if (field.Rect is not Excise.Core.Document.PdfRectangle r) continue;

            var viewerRect = ToAvaloniaRect(ToViewerDips(ContentRect(r, CurrentPage)));
            double dipW = Math.Max(viewerRect.Width, 4);
            double dipH = Math.Max(viewerRect.Height, 4);

            var input = FormFieldInputFactory.Build(field, dipW, dipH, tabIndex, _viewer);
            if (input == null) continue;

            Canvas.SetLeft(input, viewerRect.X);
            Canvas.SetTop(input, viewerRect.Y);
            layer.Children.Add(input);
        }
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

    /// <summary>
    /// The single-page layout is in logical-render-DPI dips (a 540pt page is
    /// 900 dips at the default 120), while the continuous view lays out at
    /// 96-dpi dips (PointsToDip: the same page is 720 dips). Without
    /// correction the same ZoomLevel displays 25% larger in single-page —
    /// the mode-entry size jump of #693. This factor makes the DISPLAYED
    /// size pt × 96/72 × zoom in both modes, without touching the internal
    /// 120-dpi coordinate space the overlays and hit-testing use.
    /// </summary>
    private double SinglePageDisplayScale =>
        96.0 / Math.Max(1, _currentSinglePageRenderDpi);

    private void UpdateZoomTransform()
    {
        if (_zoomScaleTransform == null) return;
        var scale = ZoomLevel * SinglePageDisplayScale;
        _zoomScaleTransform.ScaleX = scale;
        _zoomScaleTransform.ScaleY = scale;
    }

    internal void OnLoadingStateChanged()
    {
        // Show the thin top-of-viewer progress bar while a render is in
        // flight. (The full-screen overlay is kept hidden — it was always
        // visually overpowering for sub-second renders and is replaced by
        // the indeterminate ProgressBar.)
        //
        // IsIndeterminate must follow IsLoading too, not just IsVisible (#1462).
        // The theme's indeterminate animation targets the indicator's
        // TranslateTransform, and Avalonia only pauses animations whose target
        // is a Visual (AnimationInstance.Subscribed), so a hidden indeterminate
        // bar kept animating forever: one composition commit per frame, the
        // render loop never slept, and the app idled at 5-12% CPU after the
        // first page load. Clearing :indeterminate removes the style animation.
        if (LoadingProgressBar != null)
        {
            LoadingProgressBar.IsVisible = IsLoading;
            LoadingProgressBar.IsIndeterminate = IsLoading;
        }
        if (LoadingOverlay != null)
            LoadingOverlay.IsVisible = false;
    }

    private static readonly SolidColorBrush ErrorOverlayBrush =
        new(Color.FromArgb(0x80, 0x00, 0x00, 0x00));

    internal void OnErrorStateChanged()
    {
        if (ErrorOverlay != null)
        {
            ErrorOverlay.IsVisible = HasError;
            ErrorOverlay.IsHitTestVisible = HasError;
            // Set Background only while an error is actively shown — the
            // dim wash captures clicks intentionally then. With no error
            // the overlay has no Background and is fully transparent to
            // hit-testing, so in-page link clicks reach the page area.
            ErrorOverlay.Background = HasError ? ErrorOverlayBrush : null;
        }
    }

    internal void OnErrorMessageChanged()
    {
        if (ErrorMessageText != null)
        {
            ErrorMessageText.Text = ErrorMessage;
        }
    }

    internal async Task RenderCurrentPageAsync()
    {
        if (Document == null || CurrentPage < 1 || CurrentPage > Document.PageCount)
            return;

        var doc = Document;
        var pageNumber = CurrentPage;
        long requestSequence = ++_singlePageRequestSequence;
        var page = doc.GetPage(pageNumber);
        // Logical DPI drives layout and coordinate mapping (unchanged); the
        // raster is produced at the on-screen magnification (device-pixel-ratio ×
        // zoom) so text is crisp on HiDPI (#682) AND when zoomed in (#683),
        // bounded by the single-page memory budget. The plan is shared with
        // render-ahead (#1564), so both produce the same bitmap under one key.
        var spec = ComputeSinglePageRenderSpec(page);
        var logicalDpi = spec.LogicalDpi;
        if (logicalDpi != _currentSinglePageRenderDpi)
        {
            // The display-scale correction depends on the logical DPI, which
            // can differ per page (huge pages clamp down) — keep the
            // ZoomHost transform in sync so the on-screen size stays
            // pt × 96/72 × zoom regardless.
            _currentSinglePageRenderDpi = logicalDpi;
            UpdateZoomTransform();
        }
        var widthPt = spec.WidthPt;
        var heightPt = spec.HeightPt;
        double maxScale = spec.MaxScale;
        var renderDpi = spec.DeviceDpi;
        var bitmapDpi = spec.BitmapDpi;
        Trace($"SinglePageRender page={pageNumber} logicalDpi={logicalDpi} deviceDpi={renderDpi} " +
              $"bitmapDpi={bitmapDpi:F0} zoom={ZoomLevel:F3} dpr={EffectiveRenderScaling:F2} maxScale={maxScale:F2}");

        // #1564: render-ahead may be rendering this very page. Wait for it
        // rather than render it twice; any other look-ahead yields the CPU.
        if (!await JoinOrCancelSinglePageLookAheadAsync(doc, pageNumber, renderDpi, requestSequence))
            return;

        // Cache hit short-circuits the renderer entirely — this is the
        // common case for backwards-paging, undoing redactions, and
        // toggling overlays. Set Image.Source immediately so the user
        // doesn't even see a loading flicker. The cache is keyed by the
        // DEVICE render DPI so a monitor change (dpr) re-renders.
        if (_singlePageRenderLifetime.TryGet(pageNumber, renderDpi, out var cached, out var cachedDip))
        {
            // A cache hit is still a newer display request. Supersede an older
            // in-flight render so it cannot later overwrite this cached page.
            _singlePageRenderLifetime.CancelRender();
            IsLoading = false;
            Trace($"SinglePageRender page={pageNumber} CACHE-HIT dpi={renderDpi}");
            if (PdfImage != null)
            {
                var cachedBitmap = cached!;
                PdfImage.Width = cachedDip.Width;
                PdfImage.Height = cachedDip.Height;
                PdfImage.Source = cachedBitmap;
                _singlePagePublishCount++;
                ReleaseSinglePagePlaceholder();
                Trace($"ImageSet(cache) page={pageNumber} imgWidth={PdfImage.Width:F0} srcDip={cachedDip.Width:F0} srcPx={cachedBitmap.PixelSize.Width} zoom={ZoomLevel:F3}");
                if (_pendingSingleFraction >= 0)
                {
                    // Mode switch waiting to restore the reading position (#693).
                    Dispatcher.UIThread.Post(ApplyPendingSingleFraction, DispatcherPriority.Loaded);
                }
            }
            HasError = false;
            ErrorMessage = null;
            ScheduleSinglePageLookAhead();
            return;
        }

        // Cancel any prior in-flight render. If the user is paging through
        // quickly we'd rather skip the now-stale page than make them wait.
        using var renderLease = _singlePageRenderLifetime.BeginRender();
        var token = renderLease.Token;

        try
        {
            IsLoading = true;
            HasError = false;
            ErrorMessage = null;

            // Size the page and show the continuous composite for it while the
            // sharp render runs, if nothing real is on screen yet.
            ShowSinglePagePlaceholder(pageNumber, widthPt, heightPt, logicalDpi);

            long renderStart = ViewerMetrics.SinglePageRenderStart();
            // Built on the UI thread (it reads styled properties) — see the
            // continuous path's note.
            var options = SinglePageRenderOptions(renderDpi);
            var skBitmap = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                return _renderer.RenderPage(page, options, token);
            }, token);

            try
            {
                // The user may have paged again while we were rendering —
                // honour the cancellation rather than overwriting the
                // freshly-rendered new page with the stale one.
                if (!renderLease.IsCurrent) return;

                // Stamp 96 (dip Size == PixelSize) and carry the layout size
                // separately: Avalonia's Image paints a non-96-stamped bitmap
                // as a top-left pixel crop magnified by stamp/96 (#697;
                // DpiStampedBitmapPaintProbeTests). The layout size comes from
                // the page geometry, never from the raster (#1489): the renderer
                // ceils the pixel count and the device DPI is an integer, so
                // px × 96 / bitmapDpi missed pt × logicalDpi / 72 — the space
                // the overlay and every input rect map into — by up to ~0.4%.
                // Stretch="Fill" absorbs the sub-pixel raster difference.
                var bitmap = SkiaInterop.ToAvaloniaBitmap(skBitmap);
                if (bitmap != null)
                {
                    var dip = spec.LayoutSize;
                    Trace($"SinglePageRender page={pageNumber} RENDERED px={bitmap.PixelSize.Width}x{bitmap.PixelSize.Height} dip={dip.Width:F0}x{dip.Height:F0}");
                    AddToCache(pageNumber, renderDpi, bitmap, dip);
                    ViewerMetrics.RecordSinglePageRender(renderStart, renderDpi);
                    Trace($"ContVis={_viewer.ContinuousPart.ContinuousScrollViewer?.IsVisible} SingleVis={PdfScrollViewer?.IsVisible}");
                    Trace($"ImageSet page={pageNumber} imgWidth={PdfImage?.Width:F0} srcDip={dip.Width:F0}x{dip.Height:F0} srcPx={bitmap.PixelSize.Width} zoom={ZoomLevel:F3}");
                    if (PdfImage != null)
                    {
                        PdfImage.Width = dip.Width;
                        PdfImage.Height = dip.Height;
                        PdfImage.Source = bitmap;
                        _singlePagePublishCount++;
                        ReleaseSinglePagePlaceholder();
                    }
                    // A mode switch may be waiting to restore the carried
                    // reading position (#693); the ScrollViewer only gets a
                    // real extent after THIS render lands, so re-arm the
                    // bounded retry from here (Loaded runs post-layout).
                    if (_pendingSingleFraction >= 0)
                        Dispatcher.UIThread.Post(ApplyPendingSingleFraction, DispatcherPriority.Loaded);
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
            // An obsolete generation must not replace the current page with a
            // stale error any more than it may replace it with a stale bitmap.
            if (renderLease.IsCurrent)
            {
                HasError = true;
                ErrorMessage = $"Failed to render page: {ex.Message}";
            }
        }
        finally
        {
            // Only the most-recent render should clear IsLoading; older
            // races would otherwise flicker the overlay back on.
            if (renderLease.IsCurrent)
            {
                IsLoading = false;
                ScheduleSinglePageLookAhead();
            }
        }
    }

    internal void ClearDisplay()
    {
        _currentSinglePageRenderDpi = DefaultRenderDpi;
        // The ZoomHost scale depends on the logical DPI, and RenderCurrentPageAsync
        // refreshes it only when a page's logical DPI differs from this field.
        // Resetting the field without the transform would leave a clamped page's
        // scale in place for the next 120-DPI page (#1473).
        UpdateZoomTransform();
        if (PdfImage != null)
        {
            PdfImage.Source = null;
            PdfImage.Width = double.NaN;
            PdfImage.Height = double.NaN;
        }
        ReleaseSinglePagePlaceholder();
    }

    /// <summary>
    /// Add a search highlight rectangle at the specified coordinates.
    /// </summary>
    internal void AddSearchHighlight(PdfPageRect area)
    {
        var searchLayer = SearchHighlightsLayer;
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
    /// Clear all search highlights.
    /// </summary>
    internal void ClearSearchHighlights()
    {
        var searchLayer = SearchHighlightsLayer;
        searchLayer?.Children.Clear();
    }

    /// <summary>
    /// Add a pending redaction overlay at the specified coordinates.
    /// </summary>
    internal void AddPendingRedaction(PdfPageRect area)
    {
        var redactionLayer = PendingRedactionsLayer;
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
    /// Clear all pending redaction overlays.
    /// </summary>
    internal void ClearPendingRedactions()
    {
        var redactionLayer = PendingRedactionsLayer;
        redactionLayer?.Children.Clear();
    }

    /// <summary>
    /// Add an applied redaction overlay (black rectangle) at the specified coordinates.
    /// </summary>
    internal void AddAppliedRedaction(PdfPageRect area)
    {
        var appliedLayer = AppliedRedactionsLayer;
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
    /// Clear all applied redaction overlays.
    /// </summary>
    internal void ClearAppliedRedactions()
    {
        var appliedLayer = AppliedRedactionsLayer;
        appliedLayer?.Children.Clear();
    }

}
