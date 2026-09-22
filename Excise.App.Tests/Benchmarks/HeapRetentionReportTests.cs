using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Excise.App.Services;
using Excise.App.ViewModels;
using Excise.Rendering;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Excise.App.Tests.Benchmarks;

/// <summary>
/// Report-only heap retention measurement for #1481 / #1469. It asserts nothing:
/// it drives the services the GUI uses to open, index, pre-warm and page through
/// a document, closes it, and prints committed .NET heap, live managed bytes and
/// RSS at every stage so GC options can be A/B'd without a GUI.
///
/// Opt in with <c>EXCISE_HEAP_REPORT=1</c>. Knobs:
/// <list type="bullet">
/// <item><c>EXCISE_HEAP_REPORT_MODE</c>: what runs after each close —
/// <c>none</c> (default), <c>forced-compacting</c>, <c>aggressive</c>.</item>
/// <item><c>EXCISE_HEAP_REPORT_STAGE_GC=1</c>: force a full GC at every stage
/// boundary so the live-bytes column attributes growth per stage (#1469). It
/// changes the heap's shape, so leave it off for the #1481 A/B.</item>
/// <item><c>EXCISE_HEAP_REPORT_PAUSE_SECONDS</c>: sleep in the first cycle so
/// <c>dotnet-gcdump collect -p &lt;pid&gt;</c> can run; <c>_PAUSE_AT</c> picks
/// the stage (<c>indexed</c> or <c>prewarmed</c>, default <c>prewarmed</c>) and
/// <c>_PAUSE_MARKER</c> names a file that receives the pid when the pause starts.</item>
/// <item><c>EXCISE_HEAP_REPORT_SURVIVOR_MB</c>: keep this many MB of small
/// long-lived objects scattered across the heap, modelling the GUI's live
/// objects, so a non-compacting GC cannot simply return whole regions.</item>
/// <item><c>EXCISE_HEAP_REPORT_OUT</c>: also append the TSV rows to this file.</item>
/// <item><c>EXCISE_HEAP_REPORT_PDF</c>, <c>_CYCLES</c>, <c>_PAGES</c>, <c>_DPI</c>.</item>
/// </list>
/// Run GC configuration variants by setting <c>DOTNET_GCConserveMemory</c> on
/// the <c>dotnet test</c> command; the testhost inherits it and the report
/// prints the effective GC configuration.
/// </summary>
/// <remarks>
/// The open → close cycle lives in a synchronous, non-inlined method on
/// purpose. In the async test body the document local was hoisted into the
/// state machine and stayed reachable after close, which reported 140–258 MB of
/// "live" heap that the GUI does not retain.
/// </remarks>
[Collection("AvaloniaTests")]
public sealed class HeapRetentionReportTests
{
    private readonly ITestOutputHelper _out;
    private readonly StringBuilder _rows = new();

    public HeapRetentionReportTests(ITestOutputHelper output) => _out = output;

    [Fact(Timeout = 900000)]
    public async Task OpenIndexPrewarmPageClose_ReportsHeapRetention()
    {
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("EXCISE_HEAP_REPORT") == "1",
            "Report-only heap retention measurement (#1481/#1469); set EXCISE_HEAP_REPORT=1 to run it.");

        var pdf = Environment.GetEnvironmentVariable("EXCISE_HEAP_REPORT_PDF")
            ?? FindUpwards(Path.Combine("test-pdfs", "federal", "irs-1040-instructions.pdf"));
        Assert.SkipUnless(pdf is not null && File.Exists(pdf),
            "irs-1040-instructions.pdf not found above the test output directory; set EXCISE_HEAP_REPORT_PDF.");

        var settings = new ReportSettings(
            Pdf: pdf!,
            Mode: Environment.GetEnvironmentVariable("EXCISE_HEAP_REPORT_MODE") ?? "none",
            StageGc: Environment.GetEnvironmentVariable("EXCISE_HEAP_REPORT_STAGE_GC") == "1",
            Cycles: ReadInt("EXCISE_HEAP_REPORT_CYCLES", 3),
            Pages: ReadInt("EXCISE_HEAP_REPORT_PAGES", 30),
            Dpi: ReadInt("EXCISE_HEAP_REPORT_DPI", 192),
            PauseSeconds: ReadInt("EXCISE_HEAP_REPORT_PAUSE_SECONDS", 0),
            SurvivorMb: ReadInt("EXCISE_HEAP_REPORT_SURVIVOR_MB", 0));
        _survivors = new SurvivorHeap(settings.SurvivorMb);

        var config = string.Join(" ", GC.GetConfigurationVariables()
            .Where(kv => kv.Key is "ConserveMemory" or "RetainVM" or "ServerGC" or "ConcurrentGC" or "HeapHardLimit")
            .Select(kv => $"{kv.Key}={kv.Value}"));
        Emit($"# pid={Environment.ProcessId} {settings} gcConfig: {config} " +
             $"isServer={GCSettings.IsServerGC} latency={GCSettings.LatencyMode}");
        Emit("cycle\tstage\tcommittedMB\theapSizeMB\tfragmentedMB\tliveMB\trssMB\tpeakCommittedMB\tpeakRssMB\tgen2Count\tnote");

        using var peak = new PeakSampler();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Snapshot(0, "baseline", peak, forceGc: false, "after a full GC");

        for (var cycle = 1; cycle <= settings.Cycles; cycle++)
        {
            peak.Reset();
            RunOpenPageCloseCycle(cycle, settings, peak);

            Snapshot(cycle, "closed", peak, forceGc: false, "no GC");
            var liveAfterDefault = GC.GetTotalMemory(forceFullCollection: true);
            Snapshot(cycle, "closed+defaultGC", peak, forceGc: false,
                $"GetTotalMemory(true) live={Mb(liveAfterDefault):F1}");

            if (settings.Mode != "none")
            {
                var gcMode = settings.Mode == "aggressive" ? GCCollectionMode.Aggressive : GCCollectionMode.Forced;
                var stopwatch = Stopwatch.StartNew();
                GC.Collect(GC.MaxGeneration, gcMode, blocking: true, compacting: true);
                stopwatch.Stop();
                var info = GC.GetGCMemoryInfo(GCKind.FullBlocking);
                var pauseMs = info.PauseDurations.Length > 0 ? info.PauseDurations[0].TotalMilliseconds : double.NaN;
                Snapshot(cycle, "closed+" + settings.Mode, peak, forceGc: false,
                    $"wallMs={stopwatch.Elapsed.TotalMilliseconds:F1} gcPauseMs={pauseMs:F1} compacted={info.Compacted}");
            }

            await Task.Delay(TimeSpan.FromSeconds(10));
            Snapshot(cycle, "closed+idle10s", peak, forceGc: false, "no GC");
        }

        var outPath = Environment.GetEnvironmentVariable("EXCISE_HEAP_REPORT_OUT");
        if (!string.IsNullOrEmpty(outPath))
            File.AppendAllText(outPath, _rows.ToString());
    }

    private SurvivorHeap _survivors = new(0);

    private sealed record ReportSettings(
        string Pdf, string Mode, bool StageGc, int Cycles, int Pages, int Dpi, int PauseSeconds, int SurvivorMb)
    {
        public override string ToString()
            => $"mode={Mode} stageGc={StageGc} cycles={Cycles} pages={Pages} dpi={Dpi} survivorMb={SurvivorMb} pdf={Path.GetFileName(Pdf)}";
    }

    /// <summary>
    /// Models the GUI's long-lived managed objects (the live gcdump held ~106 MB
    /// in 1.36 M objects): a fixed number of small objects kept alive for the
    /// whole run, with a slice replaced at random at every stage and page so the
    /// survivors end up scattered across regions that document work also used.
    /// A non-compacting GC cannot return a region that still holds one of them.
    /// </summary>
    private sealed class SurvivorHeap
    {
        private const int ObjectBytes = 96;
        private readonly object?[] _slots;
        private readonly Random _random = new(1481);

        public SurvivorHeap(int megabytes)
            => _slots = new object?[Math.Max(0, megabytes) * 1024 * 1024 / ObjectBytes];

        public void Churn()
        {
            if (_slots.Length == 0) return;
            var replacements = _slots.Length / 40;
            for (var i = 0; i < replacements; i++)
                _slots[_random.Next(_slots.Length)] = new byte[ObjectBytes - 24];
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void RunOpenPageCloseCycle(int cycle, ReportSettings settings, PeakSampler peak)
    {
        var documentService = new PdfDocumentService(NullLogger<PdfDocumentService>.Instance);
        using var indexSession = new DocumentTextIndexSession(
            NullLogger<DocumentTextIndexSession>.Instance, TimeSpan.Zero);
        // #1565: the shipped pre-warm waits for a quiet period; this report
        // measures what the pre-warm costs, not when it starts.
        using var thumbnails = new ThumbnailSidebarSession(NullLogger.Instance)
        {
            // #1565: the pre-warm ships off; this report measures what it costs.
            PrewarmEnabled = true,
            PrewarmIdleDelay = TimeSpan.Zero,
        };
        string? thumbnailDir = null;

        try
        {
            documentService.LoadDocument(settings.Pdf);
            var document = documentService.GetCurrentDocument()!;
            Snapshot(cycle, "opened", peak, settings.StageGc);

            indexSession.Start(document);
            indexSession.BuildCompletion.GetAwaiter().GetResult();
            Snapshot(cycle, "indexed", peak, settings.StageGc, $"ready={indexSession.Current?.IsReady}");
            PauseForDump(cycle, "indexed", settings);

            thumbnails.Start(settings.Pdf, document, document.PageCount,
                cacheSalt: "heap-report-" + Guid.NewGuid().ToString("N"));
            thumbnailDir = ReadThumbnailCacheDir(thumbnails);
            thumbnails.PrewarmTask?.WaitAsync(TimeSpan.FromMinutes(10)).GetAwaiter().GetResult();
            Snapshot(cycle, "prewarmed", peak, settings.StageGc);
            PauseForDump(cycle, "prewarmed", settings);

            var renderer = new SkiaRenderer();
            var renderCount = Math.Min(settings.Pages, document.PageCount);
            for (var page = 1; page <= renderCount; page++)
            {
                using var bitmap = renderer.RenderPage(
                    document.GetPage(page), new RenderOptions { Dpi = settings.Dpi });
                _survivors.Churn();
            }
            Snapshot(cycle, "paged", peak, settings.StageGc, $"rendered={renderCount}");
        }
        finally
        {
            indexSession.Cancel();
            thumbnails.Reset();
            documentService.CloseDocument();
            if (thumbnailDir is not null && Directory.Exists(thumbnailDir))
            {
                try { Directory.Delete(thumbnailDir, recursive: true); }
                catch (IOException) { }
            }
        }
    }

    private void PauseForDump(int cycle, string stage, ReportSettings settings)
    {
        var pauseAt = Environment.GetEnvironmentVariable("EXCISE_HEAP_REPORT_PAUSE_AT") ?? "prewarmed";
        if (cycle != 1 || settings.PauseSeconds <= 0 || !string.Equals(pauseAt, stage, StringComparison.Ordinal))
            return;
        Emit($"# PAUSE stage={stage} pid={Environment.ProcessId} for {settings.PauseSeconds}s (gcdump window)");
        var marker = Environment.GetEnvironmentVariable("EXCISE_HEAP_REPORT_PAUSE_MARKER");
        if (!string.IsNullOrEmpty(marker))
            File.WriteAllText(marker, Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        Thread.Sleep(TimeSpan.FromSeconds(settings.PauseSeconds));
    }

    private void Snapshot(int cycle, string stage, PeakSampler peak, bool forceGc, string note = "")
    {
        if (!stage.StartsWith("closed", StringComparison.Ordinal))
            _survivors.Churn();
        var live = GC.GetTotalMemory(forceFullCollection: forceGc);
        var info = GC.GetGCMemoryInfo(GCKind.Any);
        peak.Sample();
        Emit(string.Join('\t',
            cycle.ToString(CultureInfo.InvariantCulture),
            stage,
            F(Mb(info.TotalCommittedBytes)),
            F(Mb(info.HeapSizeBytes)),
            F(Mb(info.FragmentedBytes)),
            F(Mb(live)),
            F(Mb(CurrentRss())),
            F(Mb(peak.PeakCommitted)),
            F(Mb(peak.PeakRss)),
            GC.CollectionCount(2).ToString(CultureInfo.InvariantCulture),
            note));
    }

    private void Emit(string line)
    {
        _out.WriteLine(line);
        _rows.AppendLine(line);
    }

    private static string? ReadThumbnailCacheDir(ThumbnailSidebarSession session)
    {
        var field = typeof(ThumbnailSidebarSession).GetField("_cache", BindingFlags.Instance | BindingFlags.NonPublic);
        return (field?.GetValue(session) as ThumbnailCacheService)?.CacheDir;
    }

    private static long CurrentRss()
    {
        using var process = Process.GetCurrentProcess();
        return process.WorkingSet64;
    }

    private static double Mb(long bytes) => bytes / (1024.0 * 1024.0);

    private static string F(double value) => value.ToString("F1", CultureInfo.InvariantCulture);

    private static int ReadInt(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private static string? FindUpwards(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>Samples committed GC bytes and RSS every 50 ms to catch the cycle peak.</summary>
    private sealed class PeakSampler : IDisposable
    {
        private readonly Timer _timer;
        private long _peakCommitted;
        private long _peakRss;

        public PeakSampler() => _timer = new Timer(_ => Sample(), null, 0, 50);

        public long PeakCommitted => Interlocked.Read(ref _peakCommitted);
        public long PeakRss => Interlocked.Read(ref _peakRss);

        public void Sample()
        {
            UpdateMax(ref _peakCommitted, GC.GetGCMemoryInfo(GCKind.Any).TotalCommittedBytes);
            UpdateMax(ref _peakRss, CurrentRss());
        }

        public void Reset()
        {
            Interlocked.Exchange(ref _peakCommitted, 0);
            Interlocked.Exchange(ref _peakRss, 0);
        }

        public void Dispose() => _timer.Dispose();

        private static void UpdateMax(ref long target, long value)
        {
            long current;
            do
            {
                current = Interlocked.Read(ref target);
                if (value <= current) return;
            }
            while (Interlocked.CompareExchange(ref target, value, current) != current);
        }
    }
}
