using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AwesomeAssertions;
using Excise.App.Services;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using PdfCoreDocument = Excise.Core.Document.PdfDocument;

namespace Excise.App.Tests.Unit;

/// <summary>
/// #1491: the Excise.App meter and the EXCISE_TRACE_VIEWER=&lt;path&gt; JSONL sink.
/// </summary>
[Collection("AvaloniaTests")]
public class AppMetricsTests
{
    private static readonly DocumentOpenTiming Timing = new(
        FilePath: "/tmp/metrics.pdf",
        PageCount: 3,
        DocumentInstancesLoadedElapsedMs: 10,
        FirstPageVisibleElapsedMs: 20,
        ThumbnailPlaceholdersReadyElapsedMs: 21,
        OutlineReadyElapsedMs: 22,
        SearchIndexStartedElapsedMs: 23,
        TotalLoadElapsedMs: 40);

    [Fact]
    public void Metrics_NoListener_EveryAppInstrumentIsDisabled_AndRecordingAllocatesNothing()
    {
        new Instrument[]
        {
            AppMetrics.DocumentOpenPhaseDuration,
            AppMetrics.TextIndexPagesIndexed,
            AppMetrics.TextIndexPagesTotal,
            AppMetrics.ThumbnailRenders,
        }.Should().OnlyContain(i => !i.Enabled);

        AppMetrics.RecordDocumentOpen(Timing);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000; i++)
            AppMetrics.RecordDocumentOpen(Timing);
        (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);
    }

    [Fact]
    public async Task Metrics_TextIndexGauges_ReportTheLatestIndexProgress()
    {
        using var capture = new AppMeterCapture();
        using var document = PdfCoreDocument.Open(MultiPagePdfBytes(3));
        using var session = new DocumentTextIndexSession(
            NullLogger<DocumentTextIndexSession>.Instance, buildDelay: TimeSpan.Zero);

        var index = session.Start(document);
        await session.BuildCompletion;
        index.IsReady.Should().BeTrue("fixture: the build must finish");

        capture.Listener.RecordObservableInstruments();
        capture.Last("excise.app.text_index.pages_indexed").Should().Be(3);
        capture.Last("excise.app.text_index.pages_total").Should().Be(3);
    }

    [FixedAvaloniaFact]
    public async Task Metrics_ThumbnailRenderCounter_CountsRenders_AndStaysMonotonicAcrossDispose()
    {
        using var capture = new AppMeterCapture();
        var path = Path.Combine(Path.GetTempPath(), $"excise-metrics-thumb-{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 2);
        using var document = PdfCoreDocument.Open(File.ReadAllBytes(path));
        var session = new ThumbnailSidebarSession(NullLogger.Instance) { PrewarmEnabled = false };
        try
        {
            capture.Listener.RecordObservableInstruments();
            double before = capture.Last("excise.app.thumbnail.renders");

            // A fresh cache salt guarantees a disk-cache miss, so page 0 is rendered.
            session.Start(path, document, pageCount: 2, cacheSalt: Guid.NewGuid().ToString("N"));
            await session.EnsureLoadedAsync(0);

            capture.Listener.RecordObservableInstruments();
            double afterLoad = capture.Last("excise.app.thumbnail.renders");
            afterLoad.Should().BeGreaterThanOrEqualTo(before + 1);

            session.Dispose();
            capture.Listener.RecordObservableInstruments();
            capture.Last("excise.app.thumbnail.renders").Should().Be(afterLoad,
                "a disposed cache's renders are folded into the process total, not dropped");
        }
        finally
        {
            session.Dispose();
            File.Delete(path);
        }
    }

    [FixedAvaloniaFact]
    public async Task Metrics_DocumentOpen_RecordsEveryPhaseFromTheViewModelTiming()
    {
        using var capture = new AppMeterCapture();
        var path = Path.Combine(Path.GetTempPath(), $"excise-metrics-open-{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 2);
        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        var window = new MainWindow { DataContext = vm, Width = 1024, Height = 768 };
        window.Show();
        try
        {
            await vm.LoadDocumentAsync(path);
            var timing = vm.LastDocumentOpenTiming;
            timing.Should().NotBeNull("fixture: the open must complete");

            var phases = capture.All("excise.app.document_open.phase.duration")
                .ToDictionary(m => m.Tags["phase"], m => m.Value);
            phases.Keys.Should().BeEquivalentTo(
                "document_instances_loaded", "first_page_visible", "thumbnail_placeholders_ready",
                "outline_ready", "search_index_started", "total_load");
            phases["first_page_visible"].Should().Be(timing!.FirstPageVisibleElapsedMs);
            phases["total_load"].Should().Be(timing.TotalLoadElapsedMs);
        }
        finally
        {
            window.Close();
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1")]
    [InlineData(" 1 ")]
    [InlineData("0")]
    [InlineData("true")]
    public void MetricsJsonlSink_ResolvePath_IgnoresValuesThatAreNotAPath(string? value)
    {
        MetricsJsonlSink.ResolvePath(value).Should().BeNull();
        MetricsJsonlSink.TryStart(value, NullLogger.Instance, TimeSpan.FromHours(1)).Should().BeNull();
        AppMetrics.DocumentOpenPhaseDuration.Enabled.Should().BeFalse("no sink means no listener");
    }

    [Theory]
    [InlineData("metrics.jsonl")]
    [InlineData("logs/metrics.log")]
    public void MetricsJsonlSink_ResolvePath_AcceptsRelativePaths(string value)
    {
        MetricsJsonlSink.ResolvePath(value).Should().Be(Path.GetFullPath(value));
    }

    [Fact]
    public void MetricsJsonlSink_Unset_StartsNothing()
    {
        var previous = Environment.GetEnvironmentVariable(MetricsJsonlSink.EnvironmentVariable);
        Environment.SetEnvironmentVariable(MetricsJsonlSink.EnvironmentVariable, null);
        try
        {
            MetricsJsonlSink.TryStartFromEnvironment(NullLogger.Instance).Should().BeNull();
            AppMetrics.DocumentOpenPhaseDuration.Enabled.Should().BeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable(MetricsJsonlSink.EnvironmentVariable, previous);
        }
    }

    [Fact]
    public void MetricsJsonlSink_Path_WritesOneParseableJsonObjectPerLine()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"excise-metrics-{Guid.NewGuid():N}");
        var path = Path.Combine(dir, "nested", "metrics.jsonl");
        try
        {
            var sink = MetricsJsonlSink.TryStart(path, NullLogger.Instance, TimeSpan.FromHours(1));
            sink.Should().NotBeNull();
            try
            {
                AppMetrics.DocumentOpenPhaseDuration.Enabled.Should().BeTrue("the sink listens to Excise.App");
                AppMetrics.RecordDocumentOpen(Timing);
                // The last-GC fields are zero until a collection has run.
                GC.Collect();
                sink!.Tick();
            }
            finally
            {
                sink!.Dispose();
            }
            AppMetrics.DocumentOpenPhaseDuration.Enabled.Should().BeFalse("disposing the sink detaches its listener");

            var lines = File.ReadAllLines(path);
            lines.Should().NotBeEmpty();
            var roots = lines.Select(line => JsonDocument.Parse(line).RootElement).ToArray();
            roots.All(r => r.TryGetProperty("ts", out _) && r.TryGetProperty("kind", out _))
                .Should().BeTrue("every line carries ts and kind");

            roots[0].GetProperty("kind").GetString().Should().Be("session-start");
            roots[0].GetProperty("pid").GetInt32().Should().Be(Environment.ProcessId);

            var totalLoad = roots.Single(r =>
                r.GetProperty("kind").GetString() == "measurement" &&
                r.GetProperty("instrument").GetString() == "excise.app.document_open.phase.duration" &&
                r.GetProperty("tags").GetProperty("phase").GetString() == "total_load");
            totalLoad.GetProperty("value").GetDouble().Should().Be(40);
            totalLoad.GetProperty("unit").GetString().Should().Be("ms");
            totalLoad.GetProperty("meter").GetString().Should().Be("Excise.App");

            roots.Should().Contain(r => r.GetProperty("kind").GetString() == "observation" &&
                r.GetProperty("instrument").GetString() == "excise.app.thumbnail.renders");

            var snapshot = roots.Single(r => r.GetProperty("kind").GetString() == "snapshot");
            snapshot.GetProperty("workingSetBytes").GetInt64().Should().BeGreaterThan(0);
            snapshot.GetProperty("lastGcCommittedBytes").GetInt64().Should().BeGreaterThan(0);
            snapshot.GetProperty("lastGcHeapSizeBytes").GetInt64().Should().BeGreaterThan(0);
            snapshot.GetProperty("liveHeapBytes").GetInt64().Should().BeGreaterThan(0);
            snapshot.GetProperty("cpuTotalMs").GetDouble().Should().BeGreaterThan(0);
            snapshot.TryGetProperty("instrument", out _).Should().BeFalse("null fields are omitted");
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    private static byte[] MultiPagePdfBytes(int pageCount)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-metrics-{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount);
        try { return File.ReadAllBytes(path); }
        finally { File.Delete(path); }
    }

    private readonly record struct Captured(string Instrument, double Value, Dictionary<string, string> Tags);

    private sealed class AppMeterCapture : IDisposable
    {
        private readonly object _gate = new();
        private readonly List<Captured> _all = new();

        public MeterListener Listener { get; } = new();

        public AppMeterCapture()
        {
            Listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == AppMetrics.MeterName)
                    listener.EnableMeasurementEvents(instrument);
            };
            Listener.SetMeasurementEventCallback<long>((i, v, t, _) => Add(i, v, t));
            Listener.SetMeasurementEventCallback<int>((i, v, t, _) => Add(i, v, t));
            Listener.Start();
        }

        private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var map = new Dictionary<string, string>();
            foreach (var tag in tags)
                map[tag.Key] = Convert.ToString(tag.Value, CultureInfo.InvariantCulture) ?? string.Empty;
            lock (_gate) _all.Add(new Captured(instrument.Name, value, map));
        }

        public Captured[] All(string instrument)
        {
            lock (_gate) return _all.Where(m => m.Instrument == instrument).ToArray();
        }

        public double Last(string instrument)
        {
            var matches = All(instrument);
            matches.Should().NotBeEmpty($"{instrument} must have been observed");
            return matches[^1].Value;
        }

        public void Dispose() => Listener.Dispose();
    }
}
