using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Excise.Avalonia.Controls;

/// <summary>
/// The viewer's <c>System.Diagnostics.Metrics</c> surface (#1491). Readable
/// in-process by a <see cref="MeterListener"/> (the app's
/// <c>EXCISE_TRACE_VIEWER=&lt;path&gt;</c> JSONL sink) or out of process with
/// <c>dotnet-counters monitor --counters Excise.Viewer</c>.
/// </summary>
/// <remarks>
/// Nothing is recorded unless something listens: every recording site checks
/// the instrument's <see cref="Instrument.Enabled"/> before it builds tags
/// (a boxed tag value is an allocation even when no listener takes it), and the
/// cache-byte mirrors are only refreshed while a byte gauge is enabled.
/// <para>
/// Observable callbacks run on the LISTENER's thread (a sink timer, or the
/// EventPipe thread for dotnet-counters), never the UI thread. The continuous
/// tile cache and page slots are UI-thread collections, so the byte gauges read
/// mirrors the viewer refreshes on the UI thread when those collections change;
/// counts are single int reads, and the single-page cache takes its own lock.
/// </para>
/// </remarks>
internal static class ViewerMetrics
{
    internal const string MeterName = "Excise.Viewer";

    internal static readonly Meter Meter = new(MeterName);

    internal static readonly Histogram<double> BandRenderDuration = Meter.CreateHistogram<double>(
        "excise.viewer.continuous.band.render.duration", "ms",
        "Wall time of one continuous-view band render (SkiaRenderer.RenderPage), tagged by dpi.");

    internal static readonly Histogram<long> CompositeSize = Meter.CreateHistogram<long>(
        "excise.viewer.continuous.composite.size", "By",
        "Bytes of one published continuous-view page composite (BGRA), tagged by dpi.");

    internal static readonly Histogram<double> SinglePageRenderDuration = Meter.CreateHistogram<double>(
        "excise.viewer.single_page.render.duration", "ms",
        "Wall time of one single-page render that reached the screen (cache hits excluded), tagged by dpi.");

    internal static readonly ObservableGauge<long> ContinuousTileBytes = Meter.CreateObservableGauge(
        "excise.viewer.continuous.cache.resident_bytes", ObserveContinuousTileBytes, "By",
        "Estimated bytes held by continuous-view grid-cell tiles, per viewer.");

    internal static readonly ObservableGauge<long> ContinuousCompositeBytes = Meter.CreateObservableGauge(
        "excise.viewer.continuous.composite.resident_bytes", ObserveContinuousCompositeBytes, "By",
        "Bytes held by published continuous-view page composites, per viewer.");

    internal static readonly ObservableGauge<int> ContinuousTileCount = Meter.CreateObservableGauge(
        "excise.viewer.continuous.cache.entries", ObserveContinuousTileCount, "{tile}",
        "Continuous-view tiles in the LRU, per viewer.");

    internal static readonly ObservableGauge<int> ContinuousInFlight = Meter.CreateObservableGauge(
        "excise.viewer.continuous.renders.in_flight", ObserveContinuousInFlight, "{tile}",
        "Continuous-view tile renders in flight, per viewer.");

    internal static readonly ObservableCounter<long> ContinuousCacheHits = Meter.CreateObservableCounter(
        "excise.viewer.continuous.cache.hits", ObserveContinuousCacheHits, "{hit}",
        "Continuous-view tile-cache hits since the viewer was constructed.");

    internal static readonly ObservableGauge<int> SinglePageEntries = Meter.CreateObservableGauge(
        "excise.viewer.single_page.cache.entries", ObserveSinglePageEntries, "{bitmap}",
        "Bitmaps in the single-page LRU, per viewer.");

    internal static readonly ObservableCounter<long> SinglePageHits = Meter.CreateObservableCounter(
        "excise.viewer.single_page.cache.hits", ObserveSinglePageHits, "{hit}",
        "Single-page LRU hits since the viewer was constructed.");

    internal static readonly ObservableCounter<long> SinglePageMisses = Meter.CreateObservableCounter(
        "excise.viewer.single_page.cache.misses", ObserveSinglePageMisses, "{miss}",
        "Single-page LRU misses since the viewer was constructed.");

    /// <summary>True while a listener wants either continuous byte gauge.</summary>
    internal static bool ByteGaugesEnabled => ContinuousTileBytes.Enabled || ContinuousCompositeBytes.Enabled;

    private static readonly object RegistryGate = new();
    private static readonly List<Registration> Registry = new();
    private static int _nextViewerId;

    private sealed class Registration(PdfViewerControl viewer, int id)
    {
        public readonly WeakReference<PdfViewerControl> Viewer = new(viewer);
        public readonly KeyValuePair<string, object?> Tag = new("viewer", id);
    }

    /// <summary>
    /// Register a viewer for the per-viewer gauges and return its tag id. Held
    /// weakly: a collected viewer drops out of the next observation.
    /// </summary>
    internal static int Register(PdfViewerControl viewer)
    {
        lock (RegistryGate)
        {
            // Prune here too, so a process that constructs many viewers and
            // never observes (the test host) does not grow the list unbounded.
            Registry.RemoveAll(static r => !r.Viewer.TryGetTarget(out _));
            int id = ++_nextViewerId;
            Registry.Add(new Registration(viewer, id));
            return id;
        }
    }

    internal static void RecordBandRender(TimeSpan elapsed, int dpi)
    {
        if (BandRenderDuration.Enabled)
            BandRenderDuration.Record(elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("dpi", dpi));
    }

    internal static void RecordComposite(long bytes, int dpi)
    {
        if (CompositeSize.Enabled)
            CompositeSize.Record(bytes, new KeyValuePair<string, object?>("dpi", dpi));
    }

    /// <summary>Timestamp to pass to <see cref="RecordSinglePageRender"/>; 0 when nothing listens.</summary>
    internal static long SinglePageRenderStart() =>
        SinglePageRenderDuration.Enabled ? Stopwatch.GetTimestamp() : 0;

    internal static void RecordSinglePageRender(long startTimestamp, int dpi)
    {
        if (startTimestamp != 0 && SinglePageRenderDuration.Enabled)
            SinglePageRenderDuration.Record(
                Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
                new KeyValuePair<string, object?>("dpi", dpi));
    }

    private static IEnumerable<Measurement<long>> ObserveContinuousTileBytes() =>
        Observe(static v => v.MetricsContinuousTileBytes);

    private static IEnumerable<Measurement<long>> ObserveContinuousCompositeBytes() =>
        Observe(static v => v.MetricsContinuousCompositeBytes);

    private static IEnumerable<Measurement<int>> ObserveContinuousTileCount() =>
        Observe(static v => v.MetricsContinuousTileCount);

    private static IEnumerable<Measurement<int>> ObserveContinuousInFlight() =>
        Observe(static v => v.MetricsContinuousInFlightCount);

    private static IEnumerable<Measurement<long>> ObserveContinuousCacheHits() =>
        Observe(static v => (long)v.MetricsContinuousCacheHits);

    private static IEnumerable<Measurement<int>> ObserveSinglePageEntries() =>
        Observe(static v => v.MetricsSinglePageCache().EntryCount);

    private static IEnumerable<Measurement<long>> ObserveSinglePageHits() =>
        Observe(static v => v.MetricsSinglePageCache().Hits);

    private static IEnumerable<Measurement<long>> ObserveSinglePageMisses() =>
        Observe(static v => v.MetricsSinglePageCache().Misses);

    private static List<Measurement<T>> Observe<T>(Func<PdfViewerControl, T> read) where T : struct
    {
        var result = new List<Measurement<T>>();
        lock (RegistryGate)
        {
            for (int i = Registry.Count - 1; i >= 0; i--)
            {
                if (!Registry[i].Viewer.TryGetTarget(out var viewer))
                {
                    Registry.RemoveAt(i);
                    continue;
                }
                result.Add(new Measurement<T>(read(viewer), Registry[i].Tag));
            }
        }
        return result;
    }
}
