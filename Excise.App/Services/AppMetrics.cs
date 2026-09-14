using Excise.App.ViewModels;
using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Threading;

namespace Excise.App.Services;

/// <summary>
/// The app's <c>System.Diagnostics.Metrics</c> surface (#1491): document-open
/// phase timings, text-index progress and thumbnail renders. Readable in-process
/// by <see cref="MetricsJsonlSink"/> or out of process with
/// <c>dotnet-counters monitor --counters Excise.App</c>.
/// </summary>
/// <remarks>
/// Observes state the app already keeps (<see cref="DocumentOpenTiming"/>,
/// <see cref="DocumentTextIndex.PagesIndexed"/>,
/// <see cref="ThumbnailCacheService.RenderCount"/>) rather than adding
/// bookkeeping to the paths that produce it. Recording checks
/// <see cref="Instrument.Enabled"/> first, so nothing is built when nothing
/// listens.
/// </remarks>
internal static class AppMetrics
{
    internal const string MeterName = "Excise.App";

    internal static readonly Meter Meter = new(MeterName);

    internal static readonly Histogram<long> DocumentOpenPhaseDuration = Meter.CreateHistogram<long>(
        "excise.app.document_open.phase.duration", "ms",
        "Elapsed time from open start to each document-open phase, tagged by phase " +
        "(the EXCISE_RESPONSIVENESS_REPORT workflow names).");

    internal static readonly ObservableGauge<int> TextIndexPagesIndexed = Meter.CreateObservableGauge(
        "excise.app.text_index.pages_indexed", () => ObserveTextIndex(static i => i.PagesIndexed), "{page}",
        "Pages the current document's search index has extracted.");

    internal static readonly ObservableGauge<int> TextIndexPagesTotal = Meter.CreateObservableGauge(
        "excise.app.text_index.pages_total", () => ObserveTextIndex(static i => i.PageCount), "{page}",
        "Pages in the document the current search index is building for.");

    internal static readonly ObservableCounter<long> ThumbnailRenders = Meter.CreateObservableCounter(
        "excise.app.thumbnail.renders", ObserveThumbnailRenders, "{render}",
        "Thumbnail renderer invocations (disk-cache hits excluded) since process start.");

    private static WeakReference<DocumentTextIndex>? _textIndex;

    private static readonly object ThumbnailGate = new();
    private static readonly List<WeakReference<ThumbnailCacheService>> LiveThumbnailCaches = new();
    private static long _retiredThumbnailRenders;

    internal static void RecordDocumentOpen(DocumentOpenTiming timing)
    {
        if (!DocumentOpenPhaseDuration.Enabled) return;
        Record(timing.DocumentInstancesLoadedElapsedMs, "document_instances_loaded");
        Record(timing.FirstPageVisibleElapsedMs, "first_page_visible");
        Record(timing.ThumbnailPlaceholdersReadyElapsedMs, "thumbnail_placeholders_ready");
        Record(timing.OutlineReadyElapsedMs, "outline_ready");
        Record(timing.SearchIndexStartedElapsedMs, "search_index_started");
        Record(timing.TotalLoadElapsedMs, "total_load");

        static void Record(long ms, string phase) =>
            DocumentOpenPhaseDuration.Record(ms, new KeyValuePair<string, object?>("phase", phase));
    }

    /// <summary>The index the text-index gauges report; the most recently started one wins.</summary>
    internal static void TrackTextIndex(DocumentTextIndex index) =>
        Volatile.Write(ref _textIndex, new WeakReference<DocumentTextIndex>(index));

    internal static void RegisterThumbnailCache(ThumbnailCacheService cache)
    {
        lock (ThumbnailGate)
        {
            LiveThumbnailCaches.RemoveAll(static r => !r.TryGetTarget(out _));
            LiveThumbnailCaches.Add(new WeakReference<ThumbnailCacheService>(cache));
        }
    }

    /// <summary>
    /// Fold a disposed cache's renders into the process total so the counter
    /// stays monotonic when a document closes. Idempotent.
    /// </summary>
    internal static void RetireThumbnailCache(ThumbnailCacheService cache)
    {
        lock (ThumbnailGate)
        {
            int index = LiveThumbnailCaches.FindIndex(r => r.TryGetTarget(out var c) && ReferenceEquals(c, cache));
            if (index < 0) return;
            LiveThumbnailCaches.RemoveAt(index);
            _retiredThumbnailRenders += cache.RenderCount;
        }
    }

    private static Measurement<int>[] ObserveTextIndex(Func<DocumentTextIndex, int> read)
    {
        var tracked = Volatile.Read(ref _textIndex);
        return tracked != null && tracked.TryGetTarget(out var index)
            ? [new Measurement<int>(read(index))]
            : [];
    }

    private static Measurement<long> ObserveThumbnailRenders()
    {
        lock (ThumbnailGate)
        {
            long total = _retiredThumbnailRenders;
            foreach (var reference in LiveThumbnailCaches)
            {
                if (reference.TryGetTarget(out var cache))
                    total += cache.RenderCount;
            }
            return new Measurement<long>(total);
        }
    }
}
