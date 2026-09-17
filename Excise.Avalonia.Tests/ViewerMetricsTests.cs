using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using AwesomeAssertions;
using Excise.Avalonia.Controls;
using Xunit;

namespace Excise.Avalonia.Tests;

/// <summary>
/// #1491: the Excise.Viewer meter. The recording sites inside the render
/// pipeline are driven on Skia in Excise.App.Tests (ViewerMetricsSiteTests);
/// these pin the parts that need no renderer — the helpers publish, the
/// per-viewer gauges read the right viewer, and nothing is enabled or
/// allocated when nothing listens.
/// </summary>
public class ViewerMetricsTests
{
    private static Task<T> OnUiThread<T>(Func<T> body) =>
        HeadlessSessionGuard.Session().Dispatch(body, CancellationToken.None);

    private static Instrument[] AllInstruments() =>
    [
        ViewerMetrics.BandRenderDuration,
        ViewerMetrics.LookAheadRenderDuration,
        ViewerMetrics.CompositeSize,
        ViewerMetrics.SinglePageRenderDuration,
        ViewerMetrics.ContinuousTileBytes,
        ViewerMetrics.ContinuousCompositeBytes,
        ViewerMetrics.ContinuousTileCount,
        ViewerMetrics.ContinuousInFlight,
        ViewerMetrics.ContinuousCacheHits,
        ViewerMetrics.SinglePageEntries,
        ViewerMetrics.SinglePageHits,
        ViewerMetrics.SinglePageMisses,
        ViewerMetrics.CacheTrims,
        ViewerMetrics.CacheTrimReleasedBytes,
        ViewerMetrics.DecodedSampleReleases,
        ViewerMetrics.DecodedSampleReleasedBytes,
    ];

    [Fact]
    public void Metrics_NoListener_EveryInstrumentIsDisabled_AndRecordingAllocatesNothing()
    {
        AllInstruments().Should().OnlyContain(i => !i.Enabled,
            "with no listener attached no instrument may be enabled");
        ViewerMetrics.ByteGaugesEnabled.Should().BeFalse();

        // Warm the JIT so the measured loop sees steady-state code only.
        Exercise();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000; i++)
            Exercise();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.Should().Be(0,
            "the recording helpers run on every band, composite and page render; " +
            "with nothing listening they must not box a tag or build a measurement");

        static void Exercise()
        {
            ViewerMetrics.RecordBandRender(TimeSpan.FromMilliseconds(3), 120);
            ViewerMetrics.RecordLookAheadRender(TimeSpan.FromMilliseconds(3), 120, ViewerMetrics.LookAheadContinuous);
            ViewerMetrics.RecordComposite(4096, 120);
            ViewerMetrics.RecordSinglePageRender(ViewerMetrics.SinglePageRenderStart(), 144);
            ViewerMetrics.RecordCacheTrim(PdfViewerCacheTrimLevel.Critical, 1, 2, 3);
            ViewerMetrics.RecordDecodedSampleRelease(ViewerMetrics.DecodedSampleReleaseUnrealized, 2, 4096);
        }
    }

    [Fact]
    public void Metrics_DecodedSampleRelease_CountsStreamsAndBytes_TaggedByReason_AndSkipsEmptyPasses()
    {
        using var capture = MetricCapture.Start();

        ViewerMetrics.RecordDecodedSampleRelease(ViewerMetrics.DecodedSampleReleaseUnrealized, streams: 3, bytes: 12_000);
        ViewerMetrics.RecordDecodedSampleRelease(ViewerMetrics.DecodedSampleReleaseTrim, streams: 1, bytes: 500);
        ViewerMetrics.RecordDecodedSampleRelease(ViewerMetrics.DecodedSampleReleaseTrim, streams: 0, bytes: 0);

        var releases = capture.Of("excise.viewer.decoded_samples.releases");
        releases.Should().HaveCount(2, "a pass that released nothing records nothing");
        releases.Single(m => m.Tag("reason") == "unrealized").Value.Should().Be(3);
        releases.Single(m => m.Tag("reason") == "trim").Value.Should().Be(1);

        var bytes = capture.Of("excise.viewer.decoded_samples.released");
        bytes.Single(m => m.Tag("reason") == "unrealized").Value.Should().Be(12_000);
        bytes.Single(m => m.Tag("reason") == "trim").Value.Should().Be(500);
        capture.Instruments["excise.viewer.decoded_samples.released"].Unit.Should().Be("By");
    }

    [Fact]
    public void Metrics_CacheTrim_CountsTheTrim_AndRecordsReleasedBytesPerCache_TaggedByLevel()
    {
        using var capture = MetricCapture.Start();

        ViewerMetrics.RecordCacheTrim(PdfViewerCacheTrimLevel.Warn, tileBytes: 4096, compositeBytes: 0, singlePageBytes: 256);

        capture.Single("excise.viewer.cache.trims").Should()
            .Match<CapturedMeasurement>(m => m.Value == 1 && m.Tag("level") == "warn");
        var released = capture.Of("excise.viewer.cache.trim.released");
        released.Should().HaveCount(3, "one sample per cache, zero included, so a live session sees what each level touched");
        released.Should().OnlyContain(m => m.Tag("level") == "warn");
        released.Single(m => m.Tag("kind") == "tiles").Value.Should().Be(4096);
        released.Single(m => m.Tag("kind") == "composites").Value.Should().Be(0);
        released.Single(m => m.Tag("kind") == "single_page").Value.Should().Be(256);
        capture.Instruments["excise.viewer.cache.trim.released"].Unit.Should().Be("By");
    }

    [Fact]
    public void Metrics_Histograms_PublishValueAndDpiTag()
    {
        using var capture = MetricCapture.Start();

        ViewerMetrics.RecordBandRender(TimeSpan.FromMilliseconds(12.5), 120);
        ViewerMetrics.RecordComposite(1_234_567, 120);
        ViewerMetrics.RecordSinglePageRender(ViewerMetrics.SinglePageRenderStart(), 144);

        capture.Single("excise.viewer.continuous.band.render.duration").Should()
            .Match<CapturedMeasurement>(m => m.Value == 12.5 && m.Tag("dpi") == "120");
        capture.Single("excise.viewer.continuous.composite.size").Should()
            .Match<CapturedMeasurement>(m => m.Value == 1_234_567 && m.Tag("dpi") == "120");
        capture.Single("excise.viewer.single_page.render.duration").Should()
            .Match<CapturedMeasurement>(m => m.Value >= 0 && m.Tag("dpi") == "144");
        capture.Instruments["excise.viewer.continuous.band.render.duration"].Unit.Should().Be("ms");
        capture.Instruments["excise.viewer.continuous.composite.size"].Unit.Should().Be("By");
    }

    [Fact]
    public void Metrics_LookAheadRender_IsItsOwnHistogram_TaggedByDpiAndView()
    {
        using var capture = MetricCapture.Start();

        ViewerMetrics.RecordLookAheadRender(TimeSpan.FromMilliseconds(40), 192, ViewerMetrics.LookAheadContinuous);
        ViewerMetrics.RecordLookAheadRender(TimeSpan.FromMilliseconds(55), 144, ViewerMetrics.LookAheadSinglePage);

        var renders = capture.Of("excise.viewer.lookahead.render.duration");
        renders.Single(m => m.Tag("view") == "continuous").Should()
            .Match<CapturedMeasurement>(m => m.Value == 40 && m.Tag("dpi") == "192");
        renders.Single(m => m.Tag("view") == "single_page").Should()
            .Match<CapturedMeasurement>(m => m.Value == 55 && m.Tag("dpi") == "144");
        capture.Of("excise.viewer.continuous.band.render.duration").Should().BeEmpty(
            "a render-ahead is not a band the reader waited for (#1564)");
        capture.Instruments["excise.viewer.lookahead.render.duration"].Unit.Should().Be("ms");
    }

    [Fact]
    public async Task Metrics_TileCacheGauges_ReportEachViewerSeparately()
    {
        await OnUiThread(() =>
        {
            using var capture = MetricCapture.Start();
            var withTile = new PdfViewerControl();
            var empty = new PdfViewerControl();
            withTile.AddToContinuousCache(
                new PdfViewerControl.ContinuousTileKey(Page: 1, Dpi: 120, PageWidthDip: 816, PageHeightDip: 1056, Col: 0, Row: 0),
                new WriteableBitmap(new PixelSize(256, 128), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul));

            capture.Listener.RecordObservableInstruments();

            long expected = PdfViewerControl.ContinuousTileByteSize(256, 128);
            capture.ForViewer("excise.viewer.continuous.cache.resident_bytes", withTile).Value.Should().Be(expected);
            capture.ForViewer("excise.viewer.continuous.cache.entries", withTile).Value.Should().Be(1);
            capture.ForViewer("excise.viewer.continuous.cache.resident_bytes", empty).Value.Should().Be(0);
            capture.ForViewer("excise.viewer.continuous.cache.entries", empty).Value.Should().Be(0);
            capture.ForViewer("excise.viewer.single_page.cache.entries", withTile).Value.Should().Be(0);
            withTile.MetricsViewerId.Should().NotBe(empty.MetricsViewerId);
            return true;
        });
    }

    internal readonly record struct CapturedMeasurement(string Instrument, double Value, KeyValuePair<string, object?>[] Tags)
    {
        public string? Tag(string key) =>
            Tags.FirstOrDefault(t => t.Key == key).Value is { } v ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    /// <summary>An in-process listener on the Excise.Viewer meter that records every measurement.</summary>
    internal sealed class MetricCapture : IDisposable
    {
        private readonly object _gate = new();
        private readonly List<CapturedMeasurement> _measurements = new();

        public MeterListener Listener { get; } = new();
        public Dictionary<string, Instrument> Instruments { get; } = new();

        public static MetricCapture Start()
        {
            var capture = new MetricCapture();
            capture.Listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name != ViewerMetrics.MeterName) return;
                lock (capture._gate) capture.Instruments[instrument.Name] = instrument;
                listener.EnableMeasurementEvents(instrument);
            };
            capture.Listener.SetMeasurementEventCallback<double>((i, v, t, _) => capture.Add(i, v, t));
            capture.Listener.SetMeasurementEventCallback<long>((i, v, t, _) => capture.Add(i, v, t));
            capture.Listener.SetMeasurementEventCallback<int>((i, v, t, _) => capture.Add(i, v, t));
            capture.Listener.Start();
            return capture;
        }

        private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            lock (_gate) _measurements.Add(new CapturedMeasurement(instrument.Name, value, tags.ToArray()));
        }

        public CapturedMeasurement[] Of(string instrument)
        {
            lock (_gate) return _measurements.Where(m => m.Instrument == instrument).ToArray();
        }

        public CapturedMeasurement Single(string instrument) => Of(instrument).Should().ContainSingle().Subject;

        public CapturedMeasurement ForViewer(string instrument, PdfViewerControl viewer) =>
            Of(instrument).Where(m => m.Tag("viewer") == viewer.MetricsViewerId.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Should().ContainSingle().Subject;

        public void Dispose() => Listener.Dispose();
    }
}
