using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using global::Avalonia;
using global::Avalonia.Media.Imaging;
using global::Avalonia.Threading;
using Excise.Rendering;

namespace Excise.Avalonia.Controls;

/// <summary>
/// Render-ahead (#1564): once the visible page has finished drawing and the
/// viewer is idle, render the page a page turn would show next (then the one
/// before it) at the current zoom, so the turn is a cache hit and draws in one
/// step.
/// </summary>
/// <remarks>
/// <para>
/// Why: the #1544 speed benchmark (2026-09-17) measured excise page turns
/// finishing in 73–120 ms (Altona p95 394 ms) against 30–40 ms for Preview and
/// Acrobat, and most excise turns drew in TWO steps (IRS 52 of 60, Altona 31
/// of 45): the page moved, then its pixels arrived 36–70 ms later. The
/// continuous view renders only the viewport plus
/// <see cref="ContinuousTileOverscanDip"/> (256 DIP); at fit-width a page is
/// ~1000 DIP tall, so the next page was never rendered before the turn.
/// </para>
/// <para>
/// What it renders. A page turn in the continuous view is
/// <see cref="NextPage"/> → <see cref="ScrollToPageContinuous(int)"/>, which
/// sets <c>Offset.Y</c> to the target slot's top. Look-ahead therefore
/// simulates the render pass at THAT offset — same
/// <see cref="RequiredTileCells"/>, same <see cref="CellKey"/>, same per-page
/// batch — rather than "rendering page N+1". The keys it caches are the keys
/// the real pass will ask for, and a page's missing cells form the same batch
/// the real pass would issue, so the band render and its slicing are the same
/// calls and the composite is pixel-identical (pinned by
/// <c>RenderAheadTests</c>). Look-ahead changes timing only.
/// </para>
/// <para>
/// Cost and bounds. One look-ahead batch at a time, behind the same render
/// gate as visible bands (width ≥ 2, so a visible band always gets a slot),
/// and only when no visible band is in flight. Its tiles are charged to the
/// existing tile budget (<see cref="ContinuousCacheByteBudget"/>) and may evict
/// scroll-back tiles, never a tile of the current bands. At fit-width on a 2×
/// display a letter page is ~1464×1894 px, ~11 MiB, so N±1 costs ~22 MiB of a
/// 200 MiB budget. Every trim level drops them (they are outside the current
/// bands), and a trim never re-arms look-ahead: the #1478 contract is that a
/// trim re-renders nothing.
/// </para>
/// <para>
/// No timer, no polling (#1462). Look-ahead is scheduled from exactly two
/// places — the end of a render pass with nothing left to render, and the end
/// of a band render — and each step either starts a batch for cells it has not
/// tried at this scroll position or marks the position done. Keys are tried at
/// most once per position (a tile the budget refuses is not retried), so the
/// chain ends and an idle viewer does no work.
/// </para>
/// </remarks>
public partial class PdfViewerControl
{
    /// <summary>
    /// Whether the viewer renders the next and previous page ahead (#1564).
    /// Default on. Tests turn it off to compare a cold page turn with a
    /// pre-rendered one.
    /// </summary>
    internal bool RenderAheadEnabled
    {
        get => _renderAheadEnabled;
        set
        {
            _renderAheadEnabled = value;
            ContinuousPart.SetRenderAheadEnabled(value);
            if (!value)
            {
                ContinuousPart.CancelContinuousLookAhead();
                CancelSinglePageLookAhead();
            }
        }
    }

    private bool _renderAheadEnabled = true;

    // ---- Single-page view -------------------------------------------------

    /// <summary>
    /// Everything that decides a single-page raster: shared by the visible
    /// render and look-ahead so both produce the same bitmap under the same
    /// cache key (<c>(page, DeviceDpi)</c>).
    /// </summary>
    internal readonly record struct SinglePageRenderSpec(
        int LogicalDpi, int DeviceDpi, double BitmapDpi, double MaxScale,
        double WidthPt, double HeightPt, Size LayoutSize);

    private SinglePageRenderSpec ComputeSinglePageRenderSpec(Excise.Core.Document.PdfPage page)
    {
        var logicalDpi = EffectiveSinglePageRenderDpi(page);
        var widthPt = page.VisualWidth;
        var heightPt = page.VisualHeight;
        double scale = ZoomLevel * EffectiveRenderScaling;
        double maxScale = MaxSinglePageRenderScale(widthPt, heightPt, logicalDpi);
        var (renderDpi, bitmapDpi) = SinglePageRenderPlan(logicalDpi, scale, maxScale);
        return new SinglePageRenderSpec(logicalDpi, renderDpi, bitmapDpi, maxScale, widthPt, heightPt,
            SinglePageLayoutSize(widthPt, heightPt, logicalDpi));
    }

    /// <summary>
    /// The render options for a single page. One place, so a look-ahead
    /// render cannot drift from the visible one. Reads styled properties, so
    /// UI thread only. MaxSinglePagePreviewPixels is a DEVICE-pixel (memory)
    /// ceiling, so it is NOT scaled by the device-pixel-ratio: a normal page at
    /// device resolution stays far under it (crisp), while a very large page is
    /// still capped at the same memory bound (it simply doesn't gain the HiDPI
    /// sharpening).
    /// </summary>
    private Excise.Rendering.RenderOptions SinglePageRenderOptions(int deviceDpi) => new()
    {
        Dpi = deviceDpi,
        MaxPixelCount = MaxSinglePagePreviewPixels,
        RenderAnnotations = ShowAnnotations,
        ShowCommentAnnotations = ShowCommentAnnotations,
        ShowFieldAndLinkAnnotations = ShowFieldAndLinkAnnotations,
        RevealHiddenAnnotations = RevealHiddenAnnotations,
        HighlightFormFields = HighlightFormFields,
    };

    private sealed class SinglePageLookAhead
    {
        internal SinglePageLookAhead(Excise.Core.Document.PdfDocument document, int page, int dpi)
        {
            Document = document;
            Page = page;
            Dpi = dpi;
        }

        internal Excise.Core.Document.PdfDocument Document { get; }
        internal int Page { get; }
        internal int Dpi { get; }
        internal CancellationTokenSource Source { get; } = new();
        internal Task Task { get; set; } = Task.CompletedTask;
    }

    private readonly record struct SinglePageLookAheadAnchor(
        Excise.Core.Document.PdfDocument? Document, int Page, double Zoom, double RenderScaling, long Generation);

    private SinglePageLookAhead? _singlePageLookAhead;
    private bool _singlePageLookAheadScheduled;
    private SinglePageLookAheadAnchor _singlePageLookAheadAnchor;
    private bool _singlePageLookAheadDone;
    private readonly HashSet<(int Page, int Dpi)> _singlePageLookAheadAttempted = new();
    // Bumped by anything that makes an in-flight look-ahead result stale
    // (document, content or annotation-setting change).
    private long _singlePageLookAheadGeneration;
    // Bumped by every RenderCurrentPageAsync call, so a call that waited on a
    // look-ahead render can tell whether a newer request superseded it.
    private long _singlePageRequestSequence;

    internal int SinglePageLookAheadStartCount { get; private set; }
    internal int SinglePageLookAheadCompletedCount { get; private set; }
    internal int SinglePageLookAheadCancellationCount { get; private set; }
    internal int SinglePageLookAheadJoinCount { get; private set; }

    /// <summary>
    /// Called on the render thread with the page number just before a
    /// single-page look-ahead render starts (tests only), so a test can hold
    /// it in flight.
    /// </summary>
    internal Action<int>? SinglePageLookAheadStartingForTests { get; set; }

    /// <summary>True while a single-page look-ahead render is running (tests).</summary>
    internal bool SinglePageLookAheadInFlight => _singlePageLookAhead != null;

    /// <summary>Whether (page, device DPI) is in the single-page cache, without touching LRU order (tests).</summary>
    internal bool SinglePageCacheContainsForTests(int page) =>
        Document is { } doc && page >= 1 && page <= doc.PageCount
        && _singlePageRenderLifetime.Contains(page, ComputeSinglePageRenderSpec(doc.GetPage(page)).DeviceDpi);

    private void ScheduleSinglePageLookAhead()
    {
        if (!_renderAheadEnabled || _singlePageLookAheadScheduled)
            return;
        _singlePageLookAheadScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _singlePageLookAheadScheduled = false;
            try { RunSinglePageLookAheadStep(); } catch { }
        }, DispatcherPriority.Background);
    }

    private void RunSinglePageLookAheadStep()
    {
        if (!_renderAheadEnabled || ViewMode != PdfViewMode.SinglePage || !IsAttachedToVisualTree())
            return;
        var doc = Document;
        if (doc == null || IsLoading || _singlePageLookAhead != null)
            return;
        if (CurrentPage < 1 || CurrentPage > doc.PageCount)
            return;

        var anchor = new SinglePageLookAheadAnchor(doc, CurrentPage, ZoomLevel, EffectiveRenderScaling,
            _singlePageLookAheadGeneration);
        if (!anchor.Equals(_singlePageLookAheadAnchor))
        {
            _singlePageLookAheadAnchor = anchor;
            _singlePageLookAheadAttempted.Clear();
            _singlePageLookAheadDone = false;
        }
        if (_singlePageLookAheadDone)
            return;

        foreach (int target in new[] { CurrentPage + 1, CurrentPage - 1 })
        {
            if (target < 1 || target > doc.PageCount)
                continue;
            var page = doc.GetPage(target);
            var spec = ComputeSinglePageRenderSpec(page);
            if (!_singlePageLookAheadAttempted.Add((target, spec.DeviceDpi)))
                continue;
            if (_singlePageRenderLifetime.Contains(target, spec.DeviceDpi))
                continue;

            StartSinglePageLookAhead(doc, page, target, spec);
            return;
        }

        _singlePageLookAheadDone = true;
    }

    private void StartSinglePageLookAhead(
        Excise.Core.Document.PdfDocument doc, Excise.Core.Document.PdfPage page, int pageNumber, SinglePageRenderSpec spec)
    {
        var lookAhead = new SinglePageLookAhead(doc, pageNumber, spec.DeviceDpi);
        _singlePageLookAhead = lookAhead;
        long generation = _singlePageLookAheadGeneration;
        var options = SinglePageRenderOptions(spec.DeviceDpi);
        var token = lookAhead.Source.Token;
        var starting = SinglePageLookAheadStartingForTests;
        SinglePageLookAheadStartCount++;
        Trace($"SinglePageLookAhead start page={pageNumber} dpi={spec.DeviceDpi}");
        lookAhead.Task = RunAsync();

        async Task RunAsync()
        {
            bool landed = false;
            try
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                // Its own renderer: SkiaRenderer carries per-render state and
                // the visible render may start on _renderer while this runs.
                var skBitmap = await Task.Run(() =>
                {
                    starting?.Invoke(pageNumber);
                    return new SkiaRenderer().RenderPage(page, options, token);
                }, token);
                try
                {
                    if (token.IsCancellationRequested || generation != _singlePageLookAheadGeneration
                        || !ReferenceEquals(Document, doc))
                        return;
                    var bitmap = Imaging.SkiaInterop.ToAvaloniaBitmap(skBitmap);
                    if (bitmap == null)
                        return;
                    var shown = PdfImage?.Source as WriteableBitmap;
                    _singlePageRenderLifetime.Add(pageNumber, spec.DeviceDpi, bitmap, spec.LayoutSize,
                        keep: b => ReferenceEquals(b, shown));
                    ViewerMetrics.RecordLookAheadRender(watch.Elapsed, spec.DeviceDpi, ViewerMetrics.LookAheadSinglePage);
                    SinglePageLookAheadCompletedCount++;
                    landed = true;
                    Trace($"SinglePageLookAhead cached page={pageNumber} dpi={spec.DeviceDpi} ms={watch.ElapsedMilliseconds}");
                }
                finally
                {
                    skBitmap?.Dispose();
                }
            }
            catch (OperationCanceledException)
            {
                SinglePageLookAheadCancellationCount++;
            }
            catch
            {
                // A page that fails to render here fails visibly when navigated to.
            }
            finally
            {
                if (ReferenceEquals(_singlePageLookAhead, lookAhead))
                    _singlePageLookAhead = null;
                lookAhead.Source.Dispose();
                if (landed || !token.IsCancellationRequested)
                    ScheduleSinglePageLookAhead();
            }
        }
    }

    /// <summary>
    /// A visible single-page request for (<paramref name="pageNumber"/>,
    /// <paramref name="deviceDpi"/>) is about to start. If look-ahead is
    /// rendering exactly that page, wait for it instead of rendering it twice;
    /// otherwise cancel it so the visible render gets the CPU. Returns false
    /// when a newer request superseded this one while it waited.
    /// </summary>
    private async Task<bool> JoinOrCancelSinglePageLookAheadAsync(
        Excise.Core.Document.PdfDocument doc, int pageNumber, int deviceDpi, long requestSequence)
    {
        if (_singlePageLookAhead is not { } lookAhead)
            return true;
        if (lookAhead.Page != pageNumber || lookAhead.Dpi != deviceDpi || !ReferenceEquals(lookAhead.Document, doc))
        {
            CancelSinglePageLookAhead();
            return true;
        }

        SinglePageLookAheadJoinCount++;
        IsLoading = true;
        try { await lookAhead.Task; } catch { }
        return requestSequence == _singlePageRequestSequence && ReferenceEquals(Document, doc);
    }

    private void CancelSinglePageLookAhead()
    {
        if (_singlePageLookAhead is { } lookAhead)
        {
            try { lookAhead.Source.Cancel(); } catch (ObjectDisposedException) { }
            Trace($"SinglePageLookAhead cancel page={lookAhead.Page}");
        }
    }

    /// <summary>
    /// Cached single-page results may be stale (document change, content or
    /// annotation-setting change): cancel and start over.
    /// </summary>
    /// <remarks>
    /// The plan is forgotten too, not only superseded by the generation bump:
    /// its anchor holds the document, and <see cref="TrimCaches"/> writes one
    /// in either view. Left in place across a close, it kept the closed
    /// document and everything it had decoded alive: Altona's footprint after
    /// Close Document stayed at 705 MB instead of ~326 MB whenever an idle
    /// trim had run first (#1543 bench, develop beed1e8b).
    /// </remarks>
    private void InvalidateSinglePageLookAhead()
    {
        _singlePageLookAheadGeneration++;
        CancelSinglePageLookAhead();
        _singlePageLookAheadAnchor = default;
        _singlePageLookAheadDone = false;
        _singlePageLookAheadAttempted.Clear();
    }

    private void SuppressSinglePageLookAheadAfterTrim()
    {
        CancelSinglePageLookAhead();
        if (Document is { } doc)
        {
            _singlePageLookAheadAnchor = new SinglePageLookAheadAnchor(doc, CurrentPage, ZoomLevel,
                EffectiveRenderScaling, _singlePageLookAheadGeneration);
            _singlePageLookAheadDone = true;
        }
    }

    private bool IsAttachedToVisualTree() => global::Avalonia.Controls.TopLevel.GetTopLevel(this) != null;
}
