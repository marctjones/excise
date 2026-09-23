using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Threading;
using AwesomeAssertions;
using Excise.App.Services;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Excise.Avalonia.Controls;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #1481: one compacting gen2 collection after a document is closed or
/// replaced, or after an OS-pressure cache trim, and never otherwise. The
/// collector is injected so each test can record when it runs, and the
/// close/replace tests check, INSIDE that callback, that the old document is
/// already unreachable: a collection that runs while something still holds the
/// document frees nothing, and a test that only counted calls would pass anyway.
/// </summary>
[Collection("AvaloniaTests")]
public class ReleasedMemoryReclaimTests
{
    private readonly ITestOutputHelper _output;

    public ReleasedMemoryReclaimTests(ITestOutputHelper output) => _output = output;

    [FixedAvaloniaFact(Timeout = 120_000)]
    public async Task ReplaceThenClose_EachQueueOneCollection_ThatRunsOnlyOnceTheOldDocumentIsUnreachable()
    {
        var recorder = new ReachabilityRecorder();
        using var indexSession = IdleIndexSession();
        var vm = MainWindowViewModelTestFactory.Create(
            textIndexSession: indexSession,
            thumbnailPrewarmEnabled: false,
            memoryReclaimer: new ReleasedMemoryReclaimer(recorder.Collect));

        await RunReplaceThenCloseAsync(vm, recorder, settle: () => PumpForAsync(TimeSpan.FromMilliseconds(300)));
    }

    [FixedAvaloniaFact(Timeout = 180_000)]
    public async Task WithTheMainWindowShown_ReplaceThenClose_CollectOnlyOnceTheViewerHasLetGoToo()
    {
        var recorder = new ReachabilityRecorder();
        using var indexSession = IdleIndexSession();
        var vm = MainWindowViewModelTestFactory.Create(
            textIndexSession: indexSession,
            thumbnailPrewarmEnabled: false,
            memoryReclaimer: new ReleasedMemoryReclaimer(recorder.Collect));
        var window = new MainWindow { DataContext = vm, Width = 1024, Height = 768 };
        window.Show();
        try
        {
            // The viewer binds PdfCoreDocument, renders pages on the pool and
            // caches tiles: each is a place the old document could survive.
            await RunReplaceThenCloseAsync(vm, recorder, settle: () => PumpForAsync(TimeSpan.FromMilliseconds(1500)));
        }
        finally
        {
            window.Close();
        }
    }

    [FixedAvaloniaFact]
    public async Task PressureTrims_AtWarnAndCritical_QueueACollection_BackToBackOnesCoalesce_SoftTrimsNever()
    {
        var collections = new List<HeapReclaimTrigger>();
        var trims = new List<PdfViewerCacheTrimLevel>();
        var reclaimer = new ReleasedMemoryReclaimer(collections.Add);
        var policy = new CacheTrimPolicy(OnMemoryPressure: true, SoftTriggers: true, IdleDelay: TimeSpan.FromMinutes(10));

        using (var coordinator = new ViewerCacheTrimCoordinator(trims.Add, policy, () => (0, 0), memoryReclaimer: reclaimer))
        {
            coordinator.OnPressure(MemoryPressureLevel.Warn);
            collections.Should().BeEmpty("the collection is posted, so it runs after the trim, not inside it");
            await PumpForAsync(TimeSpan.FromMilliseconds(200));
            collections.Should().Equal(HeapReclaimTrigger.OsPressure);

            coordinator.OnPressure(MemoryPressureLevel.Critical);
            await PumpForAsync(TimeSpan.FromMilliseconds(200));
            collections.Should().Equal(HeapReclaimTrigger.OsPressure, HeapReclaimTrigger.OsPressure);

            // libdispatch reports Warn then Critical back to back.
            await Task.Run(() =>
            {
                coordinator.PostPressure(MemoryPressureLevel.Warn);
                coordinator.PostPressure(MemoryPressureLevel.Critical);
            });
            await PumpForAsync(TimeSpan.FromMilliseconds(300));
            collections.Should().HaveCount(3, "Warn then Critical back to back is one collection");
            trims.Should().Equal(
                new[]
                {
                    PdfViewerCacheTrimLevel.Warn, PdfViewerCacheTrimLevel.Critical,
                    PdfViewerCacheTrimLevel.Warn, PdfViewerCacheTrimLevel.Critical,
                },
                "coalescing the collections must not coalesce the trims");

            coordinator.OnPressure(MemoryPressureLevel.Normal);
            coordinator.OnDeactivated();
            coordinator.OnMinimized();
            await PumpForAsync(TimeSpan.FromMilliseconds(300));
            trims.Should().HaveCount(6, "fixture: the soft triggers did trim, at Background");
            collections.Should().HaveCount(3,
                "Normal releases nothing, and Background trims are frequent and cheap: no collection for them");
        }

        collections.Clear();
        using (var fallback = new ViewerCacheTrimCoordinator(
                   trims.Add, policy with { SoftTriggers = false }, () => (900, 800), memoryReclaimer: reclaimer))
        {
            fallback.OnActivity();
            await PumpForAsync(TimeSpan.FromMilliseconds(200));
        }
        collections.Should().Equal(new[] { HeapReclaimTrigger.GcMemoryLoad },
            "the GC memory-load fallback is the pressure signal where no native source exists");
    }

    private static readonly CacheTrimPolicy IdleSoon =
        new(OnMemoryPressure: true, SoftTriggers: true, IdleDelay: TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// A reclaimer that runs each request inline, so every request is one
    /// recorded collection and the coordinator's own gating is what is counted.
    /// </summary>
    private static ReleasedMemoryReclaimer InlineReclaimer(List<HeapReclaimTrigger> collections) =>
        new(collections.Add, post: run => run());

    [FixedAvaloniaFact]
    public async Task IdleTrim_ReclaimsOnce_ThenNotAgainUntilActivity()
    {
        var collections = new List<HeapReclaimTrigger>();
        var trims = new List<PdfViewerCacheTrimLevel>();
        using var coordinator = new ViewerCacheTrimCoordinator(
            trims.Add, IdleSoon, () => (0, 0), memoryReclaimer: InlineReclaimer(collections));

        coordinator.OnActivity();
        await PumpUntilAsync(() => trims.Count > 0, TimeSpan.FromSeconds(10));
        trims.Should().Equal(PdfViewerCacheTrimLevel.Background);
        collections.Should().Equal(new[] { HeapReclaimTrigger.Idle },
            "#1496/#1713: an idle trim asks for exactly one reclaim, tagged Idle, unconditionally — gating it on " +
            "reported fragmentation (removed by #1713) missed most of what a reclaim actually freed");

        await PumpForAsync(TimeSpan.FromMilliseconds(400));
        trims.Should().HaveCount(1, "fixture: the one-shot idle timer does not fire again without activity");
        coordinator.TryRequestBackgroundTrim().Should().BeTrue();
        trims.Should().HaveCount(2, "fixture: a second idle trim did run");
        collections.Should().HaveCount(1,
            "a second idle trim in the same idle period must not reclaim again: an app left idle for hours pays one GC");

        coordinator.OnActivity();
        await PumpUntilAsync(() => trims.Count > 2, TimeSpan.FromSeconds(10));
        collections.Should().Equal(new[] { HeapReclaimTrigger.Idle, HeapReclaimTrigger.Idle },
            "activity starts a new idle period, which may reclaim once more");
    }

    [FixedAvaloniaFact]
    public void DeactivateAndMinimize_NeverReclaim()
    {
        var collections = new List<HeapReclaimTrigger>();
        var trims = new List<PdfViewerCacheTrimLevel>();
        using var coordinator = new ViewerCacheTrimCoordinator(
            trims.Add, IdleSoon with { IdleDelay = TimeSpan.FromMinutes(10) }, () => (0, 0),
            memoryReclaimer: InlineReclaimer(collections));

        coordinator.OnDeactivated();
        coordinator.OnMinimized();
        coordinator.OnActivity();
        coordinator.OnDeactivated();

        trims.Should().Equal(Enumerable.Repeat(PdfViewerCacheTrimLevel.Background, 3),
            "fixture: the soft triggers did trim");
        collections.Should().BeEmpty("a window switch must not pay a blocking compacting GC");
    }

    [FixedAvaloniaFact]
    public void TryRequestBackgroundTrim_TakesTheSameReclaimPath_AsTheIdleTimer()
    {
        var collections = new List<HeapReclaimTrigger>();
        var trims = new List<PdfViewerCacheTrimLevel>();
        using (var coordinator = new ViewerCacheTrimCoordinator(
                   trims.Add, IdleSoon with { IdleDelay = TimeSpan.FromMinutes(10) }, () => (0, 0),
                   memoryReclaimer: InlineReclaimer(collections)))
        {
            coordinator.TryRequestBackgroundTrim().Should().BeTrue();
            collections.Should().Equal(new[] { HeapReclaimTrigger.Idle },
                "the #1497 harness trim reclaims exactly as the idle timer would");

            coordinator.OnActivity();
            coordinator.TryRequestBackgroundTrim().Should().BeTrue();
            collections.Should().Equal(new[] { HeapReclaimTrigger.Idle, HeapReclaimTrigger.Idle },
                "activity started a new idle period, so this one reclaims again");
        }
        trims.Should().Equal(PdfViewerCacheTrimLevel.Background, PdfViewerCacheTrimLevel.Background);

        collections.Clear();
        using (var off = new ViewerCacheTrimCoordinator(
                   trims.Add, IdleSoon with { SoftTriggers = false }, () => (0, 0),
                   memoryReclaimer: InlineReclaimer(collections)))
        {
            off.TryRequestBackgroundTrim().Should().BeFalse("soft triggers off refuses the trim");
        }
        collections.Should().BeEmpty("a refused trim asks for no reclaim");
    }

    [FixedAvaloniaFact]
    public void TwoWindows_SharingTheIdleGate_ReclaimOncePerAppIdlePeriod()
    {
        var collections = new List<HeapReclaimTrigger>();
        var gate = new IdleReclaimGate();
        var policy = IdleSoon with { IdleDelay = TimeSpan.FromMinutes(10) };
        using var first = new ViewerCacheTrimCoordinator(
            _ => { }, policy, () => (0, 0), memoryReclaimer: InlineReclaimer(collections), idleReclaimGate: gate);
        using var second = new ViewerCacheTrimCoordinator(
            _ => { }, policy, () => (0, 0), memoryReclaimer: InlineReclaimer(collections), idleReclaimGate: gate);

        first.TryRequestBackgroundTrim().Should().BeTrue();
        second.TryRequestBackgroundTrim().Should().BeTrue();
        collections.Should().Equal(new[] { HeapReclaimTrigger.Idle },
            "the heap is the process's: a second idle window must not run a second blocking collection");

        second.OnActivity();
        first.TryRequestBackgroundTrim().Should().BeTrue();
        collections.Should().Equal(new[] { HeapReclaimTrigger.Idle, HeapReclaimTrigger.Idle },
            "activity in ANY window starts a new idle period");

        // Unshared (the default), each coordinator keeps its own period.
        collections.Clear();
        using var alone1 = new ViewerCacheTrimCoordinator(
            _ => { }, policy, () => (0, 0), memoryReclaimer: InlineReclaimer(collections));
        using var alone2 = new ViewerCacheTrimCoordinator(
            _ => { }, policy, () => (0, 0), memoryReclaimer: InlineReclaimer(collections));
        alone1.TryRequestBackgroundTrim();
        alone2.TryRequestBackgroundTrim();
        collections.Should().HaveCount(2, "fixture: without a shared gate the periods are independent");
    }

    [FixedAvaloniaFact]
    public async Task Requests_CoalesceUntilTheQueuedCollectionRuns_AndNothingRunsWithoutARequest()
    {
        var collections = new List<HeapReclaimTrigger>();
        var posted = new List<Action>();
        var manual = new ReleasedMemoryReclaimer(collections.Add, posted.Add);

        manual.Request(HeapReclaimTrigger.DocumentReplaced);
        manual.Request(HeapReclaimTrigger.DocumentClosed);
        manual.Request(HeapReclaimTrigger.OsPressure);
        posted.Should().ContainSingle("requests coalesce into the collection already queued");
        collections.Should().BeEmpty("a request only queues; it never collects inline");
        posted[0]();
        collections.Should().Equal(new[] { HeapReclaimTrigger.DocumentReplaced }, "the queued collection keeps the trigger that queued it");

        manual.Request(HeapReclaimTrigger.DocumentClosed);
        posted.Should().HaveCount(2, "once the queued collection has run, the next request queues again");
        posted[1]();
        collections.Should().Equal(HeapReclaimTrigger.DocumentReplaced, HeapReclaimTrigger.DocumentClosed);

        // The production post: no request, no collection, however long the app idles.
        var dispatched = new List<HeapReclaimTrigger>();
        var reclaimer = new ReleasedMemoryReclaimer(dispatched.Add);
        await PumpForAsync(TimeSpan.FromMilliseconds(300));
        dispatched.Should().BeEmpty();
        reclaimer.Request(HeapReclaimTrigger.DocumentClosed);
        dispatched.Should().BeEmpty("posted to the dispatcher, not run inline");
        await PumpUntilAsync(() => dispatched.Count > 0, TimeSpan.FromSeconds(10));
        await PumpForAsync(TimeSpan.FromMilliseconds(500));
        dispatched.Should().Equal(new[] { HeapReclaimTrigger.DocumentClosed }, "one collection per request burst, nothing periodic (#1462)");

        typeof(ReleasedMemoryReclaimer)
            .GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(f => f.FieldType)
            .Should().NotContain(t => t == typeof(DispatcherTimer) || t == typeof(System.Threading.Timer)
                || t == typeof(System.Timers.Timer) || t == typeof(PeriodicTimer),
                "the reclaimer holds no timer: it must never wake an idle app");
    }

    [FixedAvaloniaFact(Timeout = 120_000)]
    public async Task ReplacingALargeDocument_TheCollectionReturnsItsHeap_AndRecordsTheMetric()
    {
        // #1691. This fixture used to be a 64 MB content stream, on the premise
        // that "the document keeps its file bytes (it is opened from a byte[])".
        // #1567 deleted that: OpenCurrent now opens a FileStream and the
        // document holds no file bytes, so the old fixture put 0.2 MB of an
        // expected 61 MB on the managed heap and the assertion below was
        // subtracting two readings that never contained the payload.
        //
        // What the reclaim actually returns is render-derived. The #1713 heap
        // dump, before and after one reclaim: Byte[] 223 -> 124 MB,
        // ContentOperator 8.3 -> 0, LOH 312 -> 130 MB. So the fixture decodes a
        // large image instead, and the window is SHOWN, because the viewer is
        // the caching path (PageImageRenderer is documented as the uncached
        // export boundary and would leave nothing behind).
        const int imageSide = 4000;
        const int payloadBytes = imageSide * imageSide * 3;   // ~48 MB decoded
        var large = WriteLargeImagePdf(imageSide);
        var small = TempMultiPagePdf(1);

        long heapBeforeCollect = 0, heapAfterCollect = 0;
        GCMemoryInfo lastGc = default;
        var triggers = new List<HeapReclaimTrigger>();
        var reclaimer = new ReleasedMemoryReclaimer(trigger =>
        {
            heapBeforeCollect = GC.GetTotalMemory(forceFullCollection: false);
            ReleasedMemoryReclaimer.CollectReleasedMemory(trigger);
            heapAfterCollect = GC.GetTotalMemory(forceFullCollection: false);
            lastGc = GC.GetGCMemoryInfo(GCKind.Any);
            triggers.Add(trigger);
        });

        var measurements = new List<(string Instrument, long Value, Dictionary<string, string?> Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == AppMetrics.MeterName && instrument.Name.StartsWith("excise.app.heap_reclaim.", StringComparison.Ordinal))
                    l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            var map = new Dictionary<string, string?>();
            foreach (var tag in tags)
                map[tag.Key] = tag.Value?.ToString();
            lock (measurements) measurements.Add((instrument.Name, value, map));
        });
        listener.Start();

        using var indexSession = IdleIndexSession();
        var vm = MainWindowViewModelTestFactory.Create(
            textIndexSession: indexSession, thumbnailPrewarmEnabled: false, memoryReclaimer: reclaimer);
        var window = new MainWindow { DataContext = vm, Width = 1024, Height = 768 };
        window.Show();
        try
        {
            // #1691. The reading BEFORE the large document opens. The assertion
            // below subtracts two whole-heap readings, and a whole-heap reading
            // in a full-suite run is mostly OTHER tests' live heap. Without the
            // baseline, `withLarge` cannot distinguish "the payload was on the
            // heap and stayed there" from "the payload was never on the heap",
            // and those two want opposite fixes: one is a retention bug, the
            // other means this assertion is measuring nothing.
            long baseline = GC.GetTotalMemory(forceFullCollection: true);

            await vm.LoadDocumentAsync(large);
            vm.TotalPages.Should().Be(1, "fixture: the large document opens");
            // The viewer renders on a pool thread; opening alone decodes
            // nothing. Pump until the decode has actually landed on the heap,
            // rather than sleeping a guessed interval.
            await PumpUntilAsync(
                () => GC.GetTotalMemory(forceFullCollection: false) - baseline > payloadBytes / 2,
                TimeSpan.FromSeconds(30));
            long withLarge = GC.GetTotalMemory(forceFullCollection: true);

            // Captured while the large document is still current: once the
            // replace lands, SaveDocumentForTests is the small one. The strong
            // reference stays inside the helper's frame, never in this one.
            var weakLarge = WeakCurrentDocument(vm);

            await vm.LoadDocumentAsync(small);
            await PumpUntilAsync(() => triggers.Count > 0, TimeSpan.FromSeconds(30));
            await PumpForAsync(TimeSpan.FromMilliseconds(200));

            // Same three-step as ReachabilityRecorder: a finalizable object is
            // not collected until the queue has drained, so one Collect() can
            // report a dead object as alive.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            bool documentStillAlive = weakLarge.IsAlive;
            long heapAfterFullCollect = GC.GetTotalMemory(forceFullCollection: true);

            _output.WriteLine(
                $"live heap with the large document: {withLarge / 1048576.0:F1} MB; " +
                $"at the hook before collecting: {heapBeforeCollect / 1048576.0:F1} MB; " +
                $"after: {heapAfterCollect / 1048576.0:F1} MB; " +
                $"gen{lastGc.Generation} compacted={lastGc.Compacted} heapSize={lastGc.HeapSizeBytes / 1048576.0:F1} MB " +
                $"committed={lastGc.TotalCommittedBytes / 1048576.0:F1} MB pause={lastGc.PauseDurations[0].TotalMilliseconds:F1} ms");

            // #1691 diagnostics: which of the three outcomes this run is.
            _output.WriteLine(
                $"[#1691] baseline before opening: {baseline / 1048576.0:F1} MB; " +
                $"payload accounted on the heap (withLarge - baseline): {(withLarge - baseline) / 1048576.0:F1} MB " +
                $"of an expected {payloadBytes / 1048576.0:F1} MB; " +
                $"replaced document still reachable after a full collect: {documentStillAlive}; " +
                $"heap after that full collect: {heapAfterFullCollect / 1048576.0:F1} MB " +
                $"(net vs baseline {(heapAfterFullCollect - baseline) / 1048576.0:F1} MB)");

            triggers.Should().Equal(HeapReclaimTrigger.DocumentReplaced);

            // #1691. These two run BEFORE the heap assertion deliberately, so
            // that when this test fails the message says WHICH thing broke
            // rather than only that two numbers were close together. They are
            // additions, not a relaxation: the heap assertion below is
            // untouched, and a run that satisfies both still has to satisfy it.
            //
            //   this one fails      -> the payload never reached the managed
            //                          heap under these conditions, so the heap
            //                          assertion is comparing two readings that
            //                          never contained it. Fix the measurement,
            //                          not the app.
            //   the next one fails  -> something still roots the replaced
            //                          document. A real retention bug; find the
            //                          root (dotnet-gcdump), do not touch this
            //                          test.
            //   both pass, heap one -> the bytes outlive the document that owned
            //   fails                  them; they are held somewhere else.
            (withLarge - baseline).Should().BeGreaterThan(payloadBytes / 2,
                "fixture precondition: opening the large document must put its payload on the managed heap, " +
                "or the heap comparison below cannot observe the payload being returned");
            documentStillAlive.Should().BeFalse(
                "the replaced document must be unreachable once the reclaim has run, or its heap cannot come back");

            // Half the payload, not all of it: the test host's own heap moves
            // by a few MB between the two readings, and the point is that the
            // document's bytes came back, not an exact figure.
            (withLarge - heapAfterCollect).Should().BeGreaterThan(payloadBytes / 2,
                "the replaced document's bytes must be returned by the collection");
            lastGc.Generation.Should().Be(2);
            lastGc.Compacted.Should().BeTrue("the collection compacts, so the freed LOH does not stay as fragmentation");

            lock (measurements)
            {
                measurements.Should().ContainSingle(m => m.Instrument == "excise.app.heap_reclaim.duration")
                    .Which.Tags["trigger"].Should().Be("document_replaced");
                var heap = measurements.Where(m => m.Instrument == "excise.app.heap_reclaim.heap_size").ToArray();
                heap.Select(m => m.Tags["phase"]).Should().Equal("before", "after");
                (heap[0].Value - heap[1].Value).Should().BeGreaterThan(payloadBytes / 2,
                    "the metric reports the same drop the app gets");
            }
        }
        finally
        {
            await vm.CloseDocumentCommand.Execute();
            window.Close();
            TestPdfGenerator.CleanupTestFile(large);
            TestPdfGenerator.CleanupTestFile(small);
        }
    }

    private static async Task RunReplaceThenCloseAsync(MainWindowViewModel vm, ReachabilityRecorder recorder, Func<Task> settle)
    {
        var first = TempMultiPagePdf(3);
        var second = TempMultiPagePdf(2);
        try
        {
            await vm.LoadDocumentAsync(first);
            await settle();
            recorder.Calls.Should().BeEmpty("opening into an empty window releases no document");

            recorder.Watched = WeakCurrentDocument(vm);
            await vm.LoadDocumentAsync(second);
            await PumpUntilAsync(() => recorder.Calls.Count > 0, TimeSpan.FromSeconds(30));
            await settle();
            recorder.Calls.Should().ContainSingle("a replace queues exactly one collection")
                .Which.Should().Be((HeapReclaimTrigger.DocumentReplaced, false),
                    "when the collection runs, nothing may still hold the replaced document");

            recorder.Watched = WeakCurrentDocument(vm);
            await vm.CloseDocumentCommand.Execute();
            await PumpUntilAsync(() => recorder.Calls.Count > 1, TimeSpan.FromSeconds(30));
            await settle();
            recorder.Calls.Should().HaveCount(2, "a close queues exactly one collection");
            recorder.Calls[1].Should().Be((HeapReclaimTrigger.DocumentClosed, false),
                "when the collection runs, nothing may still hold the closed document");
        }
        finally
        {
            TestPdfGenerator.CleanupTestFile(first);
            TestPdfGenerator.CleanupTestFile(second);
        }
    }

    /// <summary>
    /// Not inlined, so no local in the (async) caller holds the document: the
    /// only reference the test keeps is weak.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference WeakCurrentDocument(MainWindowViewModel vm)
    {
        var document = vm.SaveDocumentForTests;
        document.Should().NotBeNull("fixture: a document is open");
        return new WeakReference(document);
    }

    private sealed class ReachabilityRecorder
    {
        public WeakReference? Watched;
        public List<(HeapReclaimTrigger Trigger, bool WatchedStillAlive)> Calls { get; } = new();

        public void Collect(HeapReclaimTrigger trigger)
        {
            bool alive = false;
            if (Watched != null)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                alive = Watched.IsAlive;
            }
            Calls.Add((trigger, alive));
        }
    }

    // A long build delay keeps the background text-index build parked in its
    // cancellable delay, so a cancelled build is not still walking the old
    // document on a pool thread when the collection runs.
    private static DocumentTextIndexSession IdleIndexSession() =>
        new(NullLogger<DocumentTextIndexSession>.Instance, buildDelay: TimeSpan.FromMinutes(10));

    private static string TempMultiPagePdf(int pageCount)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-reclaim-{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount);
        return path;
    }

    /// <summary>One page whose content stream is <paramref name="payloadBytes"/> of PDF comment lines.</summary>
    private static string WriteLargePdf(int payloadBytes)
    {
        if (payloadBytes % 100 != 0)
            throw new ArgumentOutOfRangeException(nameof(payloadBytes), "must be a multiple of the 100-byte line");
        var path = Path.Combine(Path.GetTempPath(), $"excise-reclaim-large-{Guid.NewGuid():N}.pdf");
        var offsets = new long[5];
        using (var stream = File.Create(path))
        {
            void Write(string text)
            {
                var bytes = Encoding.ASCII.GetBytes(text);
                stream.Write(bytes, 0, bytes.Length);
            }

            Write("%PDF-1.4\n");
            offsets[1] = stream.Position;
            Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
            offsets[2] = stream.Position;
            Write("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
            offsets[3] = stream.Position;
            Write("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>\nendobj\n");
            offsets[4] = stream.Position;
            Write($"4 0 obj\n<< /Length {payloadBytes} >>\nstream\n");
            var line = Encoding.ASCII.GetBytes("% " + new string('x', 97) + "\n");
            for (int i = 0; i < payloadBytes / line.Length; i++)
                stream.Write(line, 0, line.Length);
            Write("\nendstream\nendobj\n");
            long xref = stream.Position;
            Write("xref\n0 5\n0000000000 65535 f \n");
            for (int i = 1; i <= 4; i++)
                Write($"{offsets[i]:D10} 00000 n \n");
            Write($"trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        }
        return path;
    }

    /// <summary>
    /// One page drawing a <paramref name="side"/>x<paramref name="side"/> raw
    /// DeviceRGB image (#1691). Deliberately NOT a big content stream: after
    /// #1567 the document reads from a FileStream and holds no file bytes, and
    /// a stream of PDF comments materialises nothing when parsed. What the
    /// idle/replace reclaim actually returns is render-derived — the heap dump
    /// on #1713 shows Byte[] 223 -> 124 MB and the LOH 312 -> 130 MB — so the
    /// fixture has to make the app DECODE something.
    ///
    /// The image is uncompressed so its decoded size is predictable, and it is
    /// drawn small on purpose: per #1677 decode cost tracks SOURCE pixels, not
    /// output, so a 4000x4000 source costs ~48 MB however little of the page it
    /// covers. Rows are written one at a time — buffering the whole image here
    /// would put the payload on the TEST's heap, which is the heap being
    /// measured.
    /// </summary>
    private static string WriteLargeImagePdf(int side)
    {
        int payload = side * side * 3;
        var path = Path.Combine(Path.GetTempPath(), $"excise-reclaim-image-{Guid.NewGuid():N}.pdf");
        var offsets = new long[6];
        using (var stream = File.Create(path))
        {
            void Write(string text)
            {
                var bytes = Encoding.ASCII.GetBytes(text);
                stream.Write(bytes, 0, bytes.Length);
            }

            Write("%PDF-1.4\n");
            offsets[1] = stream.Position;
            Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
            offsets[2] = stream.Position;
            Write("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
            offsets[3] = stream.Position;
            Write("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Resources << /XObject << /Im0 5 0 R >> >> /Contents 4 0 R >>\nendobj\n");
            offsets[4] = stream.Position;
            var content = Encoding.ASCII.GetBytes("q 612 0 0 792 0 0 cm /Im0 Do Q\n");
            Write($"4 0 obj\n<< /Length {content.Length} >>\nstream\n");
            stream.Write(content, 0, content.Length);
            Write("endstream\nendobj\n");
            offsets[5] = stream.Position;
            Write($"5 0 obj\n<< /Type /XObject /Subtype /Image /Width {side} /Height {side} "
                + $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Length {payload} >>\nstream\n");
            var row = new byte[side * 3];
            for (int y = 0; y < side; y++)
            {
                // Vary per row so nothing downstream can collapse it to a
                // constant: a uniform image is exactly what a clever decoder
                // would store as one value, and the fixture would measure zero.
                for (int x = 0; x < row.Length; x++)
                    row[x] = (byte)((x + y) & 0xFF);
                stream.Write(row, 0, row.Length);
            }
            Write("\nendstream\nendobj\n");
            long xref = stream.Position;
            Write("xref\n0 6\n0000000000 65535 f \n");
            for (int i = 1; i <= 5; i++)
                Write($"{offsets[i]:D10} 00000 n \n");
            Write($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        }
        return path;
    }

    private static async Task PumpUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > timeout)
                throw new TimeoutException("condition not met");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }

    private static async Task PumpForAsync(TimeSpan duration)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < duration)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }
}
