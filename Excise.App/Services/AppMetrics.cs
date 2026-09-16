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

    internal static readonly Counter<long> CacheTrimRequests = Meter.CreateCounter<long>(
        "excise.app.cache_trim.requests", "{request}",
        "Viewer cache trims the app asked for, tagged by trigger (deactivated, minimized, idle, " +
        "os_pressure, gc_memory_load) and level (#1478). What each released is on Excise.Viewer.");

    internal static readonly Histogram<long> HeapReclaimDuration = Meter.CreateHistogram<long>(
        "excise.app.heap_reclaim.duration", "ms",
        "Wall time of one compacting gen2 collection after a document close/replace or an OS-pressure trim, " +
        "tagged by trigger (document_closed, document_replaced, os_pressure, gc_memory_load) (#1481).");

    internal static readonly Histogram<long> HeapReclaimHeapSize = Meter.CreateHistogram<long>(
        "excise.app.heap_reclaim.heap_size", "By",
        "GC.GetTotalMemory around that collection, tagged by trigger and phase (before, after) (#1481).");

    internal static readonly Histogram<double> ScenarioStepDuration = Meter.CreateHistogram<double>(
        "excise.app.perf_scenario.step.duration", "ms",
        "Wall time of one in-app performance-scenario step, tagged by scenario, step and op (#1497). " +
        "Recorded only while EXCISE_PERF_SCENARIO names a scenario file; the marker's purpose is to " +
        "segment the metrics JSONL by step, so band render times and cache bytes can be attributed to " +
        "the interaction that caused them.");

    internal static readonly Histogram<double> ScenarioSampleWindowDuration = Meter.CreateHistogram<double>(
        "excise.app.perf_scenario.sample_window.duration", "ms",
        "Wall time the scenario runner held still at a step boundary so the outer harness could take " +
        "its footprint/vmmap samples off a quiescent process (#1497). Reported separately and never " +
        "charged to the step it follows — the harness's own cost has to be visible, not hidden in a step.");

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

    internal static void RecordCacheTrimRequest(CacheTrimTrigger trigger, Excise.Avalonia.Controls.PdfViewerCacheTrimLevel level)
    {
        if (!CacheTrimRequests.Enabled) return;
        string triggerTag = trigger switch
        {
            CacheTrimTrigger.Deactivated => "deactivated",
            CacheTrimTrigger.Minimized => "minimized",
            CacheTrimTrigger.Idle => "idle",
            CacheTrimTrigger.OsPressure => "os_pressure",
            CacheTrimTrigger.GcMemoryLoad => "gc_memory_load",
            _ => "unknown",
        };
        string levelTag = level switch
        {
            Excise.Avalonia.Controls.PdfViewerCacheTrimLevel.Background => "background",
            Excise.Avalonia.Controls.PdfViewerCacheTrimLevel.Warn => "warn",
            Excise.Avalonia.Controls.PdfViewerCacheTrimLevel.Critical => "critical",
            _ => "unknown",
        };
        CacheTrimRequests.Add(1,
            new KeyValuePair<string, object?>("trigger", triggerTag),
            new KeyValuePair<string, object?>("level", levelTag));
    }

    /// <summary>
    /// Mark a performance-scenario step boundary in the metrics JSONL (#1497).
    /// </summary>
    /// <remarks>
    /// A <c>Histogram.Record</c> invokes the <c>MeterListener</c> callback
    /// synchronously on the calling thread, so the line is written and flushed
    /// at the boundary rather than on the sink's next interval tick. That is
    /// what makes the marker usable as a segmentation point for the
    /// <c>measurement</c> lines around it.
    /// </remarks>
    internal static void RecordScenarioStep(string scenario, string step, string op, double wallMs)
    {
        if (!ScenarioStepDuration.Enabled) return;
        ScenarioStepDuration.Record(wallMs,
            new KeyValuePair<string, object?>("scenario", scenario),
            new KeyValuePair<string, object?>("step", step),
            new KeyValuePair<string, object?>("op", op));
    }

    /// <summary>Mark how long a boundary held still for the outer sampler (#1497).</summary>
    internal static void RecordScenarioSampleWindow(string scenario, string step, double wallMs)
    {
        if (!ScenarioSampleWindowDuration.Enabled) return;
        ScenarioSampleWindowDuration.Record(wallMs,
            new KeyValuePair<string, object?>("scenario", scenario),
            new KeyValuePair<string, object?>("step", step));
    }

    internal static void RecordHeapReclaim(
        HeapReclaimTrigger trigger, long durationMs, long heapBefore, long heapAfter)
    {
        string triggerTag = trigger switch
        {
            HeapReclaimTrigger.DocumentClosed => "document_closed",
            HeapReclaimTrigger.DocumentReplaced => "document_replaced",
            HeapReclaimTrigger.OsPressure => "os_pressure",
            HeapReclaimTrigger.GcMemoryLoad => "gc_memory_load",
            _ => "unknown",
        };
        var triggerPair = new KeyValuePair<string, object?>("trigger", triggerTag);
        if (HeapReclaimDuration.Enabled)
            HeapReclaimDuration.Record(durationMs, triggerPair);
        if (HeapReclaimHeapSize.Enabled)
        {
            HeapReclaimHeapSize.Record(heapBefore, triggerPair, new KeyValuePair<string, object?>("phase", "before"));
            HeapReclaimHeapSize.Record(heapAfter, triggerPair, new KeyValuePair<string, object?>("phase", "after"));
        }
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
