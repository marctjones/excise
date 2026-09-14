using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using AwesomeAssertions;
using Excise.App.Tests.Utilities;
using Excise.Avalonia.Controls;
using Xunit;
using PdfCoreDocument = Excise.Core.Document.PdfDocument;

namespace Excise.App.Tests.Controls;

/// <summary>
/// #1491: the Excise.Viewer instruments are recorded at their real sites — the
/// continuous band render, the composite publish, the single-page render — and
/// the byte gauges' mirrors are refreshed by the real cache and composite
/// changes, on Skia with a real document.
/// </summary>
[Collection("AvaloniaTests")]
public class ViewerMetricsSiteTests
{
    [FixedAvaloniaFact]
    public async Task Metrics_ContinuousRender_RecordsBandAndComposite_AndRefreshesByteGauges()
    {
        using var capture = new ViewerMeterCapture();
        var (window, viewer, items) = ContinuousTileEvictionCompositeTests.ShowContinuousViewer(pageCount: 1);
        try
        {
            var composite = await ContinuousTileEvictionCompositeTests.WaitForSettledCompositeAsync(
                window, viewer, items, pageNumber: 1);

            var bands = capture.Of("excise.viewer.continuous.band.render.duration");
            bands.Should().NotBeEmpty("the settled page was rendered as at least one band");
            bands.Should().OnlyContain(m => m.Value >= 0 && m.Dpi != null);

            var composites = capture.Of("excise.viewer.continuous.composite.size");
            composites.Should().NotBeEmpty("the settled page published a composite");
            composites.Should().Contain(m => m.Value ==
                PdfViewerControl.ContinuousTileByteSize(composite.PixelSize.Width, composite.PixelSize.Height),
                "the recorded size is the published composite's own BGRA size");

            capture.Listener.RecordObservableInstruments();
            capture.ForViewer("excise.viewer.continuous.cache.resident_bytes", viewer).Should().BeGreaterThan(0);
            capture.ForViewer("excise.viewer.continuous.composite.resident_bytes", viewer)
                .Should().Be(viewer.ContinuousCompositeResidentBytes());
            capture.ForViewer("excise.viewer.continuous.cache.entries", viewer)
                .Should().Be(viewer.GetRenderDiagnostics().ContinuousEntryCount);
        }
        finally
        {
            window.Close();
            viewer.Document?.Dispose();
        }
    }

    [FixedAvaloniaFact]
    public async Task Metrics_SinglePageRender_RecordsDuration_AndCacheCounters()
    {
        using var capture = new ViewerMeterCapture();
        var viewer = new PdfViewerControl();
        var window = new Window { Content = viewer, Width = 900, Height = 700 };
        window.Show();
        try
        {
            viewer.Document = PdfCoreDocument.Open(TestPdfGenerator.CreateSimplePdf("metrics page"));
            var deadline = Stopwatch.StartNew();
            while (viewer.SinglePagePublishCount == 0 && deadline.Elapsed < TimeSpan.FromSeconds(30))
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(25);
            }
            viewer.SinglePagePublishCount.Should().BeGreaterThan(0, "fixture: the page must render");

            capture.Of("excise.viewer.single_page.render.duration").Should().ContainSingle()
                .Which.Dpi.Should().NotBeNull();

            capture.Listener.RecordObservableInstruments();
            var diagnostics = viewer.GetRenderDiagnostics();
            capture.ForViewer("excise.viewer.single_page.cache.entries", viewer).Should().Be(diagnostics.SinglePageEntryCount);
            capture.ForViewer("excise.viewer.single_page.cache.misses", viewer).Should().Be(diagnostics.SinglePageMisses);
        }
        finally
        {
            window.Close();
            viewer.Document?.Dispose();
        }
    }

    private readonly record struct Captured(string Instrument, double Value, string? Dpi, string? Viewer);

    private sealed class ViewerMeterCapture : IDisposable
    {
        private readonly object _gate = new();
        private readonly List<Captured> _all = new();

        public MeterListener Listener { get; } = new();

        public ViewerMeterCapture()
        {
            Listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Excise.Viewer")
                    listener.EnableMeasurementEvents(instrument);
            };
            Listener.SetMeasurementEventCallback<double>((i, v, t, _) => Add(i, v, t));
            Listener.SetMeasurementEventCallback<long>((i, v, t, _) => Add(i, v, t));
            Listener.SetMeasurementEventCallback<int>((i, v, t, _) => Add(i, v, t));
            Listener.Start();
        }

        private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            string? dpi = null, viewer = null;
            foreach (var tag in tags)
            {
                var text = Convert.ToString(tag.Value, CultureInfo.InvariantCulture);
                if (tag.Key == "dpi") dpi = text;
                else if (tag.Key == "viewer") viewer = text;
            }
            lock (_gate) _all.Add(new Captured(instrument.Name, value, dpi, viewer));
        }

        public Captured[] Of(string instrument)
        {
            lock (_gate) return _all.Where(m => m.Instrument == instrument).ToArray();
        }

        /// <summary>The latest observation of <paramref name="instrument"/> for <paramref name="viewer"/>.</summary>
        public double ForViewer(string instrument, PdfViewerControl viewer)
        {
            var id = viewer.MetricsViewerId.ToString(CultureInfo.InvariantCulture);
            var matches = Of(instrument).Where(m => m.Viewer == id).ToArray();
            matches.Should().NotBeEmpty($"{instrument} must report viewer {id}");
            return matches[^1].Value;
        }

        public void Dispose() => Listener.Dispose();
    }
}
