using System;
using System.Diagnostics;

namespace Excise.App.Automation;

/// <summary>
/// One in-process reading taken at a scenario step boundary (#1497).
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>Nothing here may force a collection.</b> #1496 is specifically about
/// committed-but-free managed heap — about 550 MB of it on a scrolled Altona
/// against a 345 MB live heap. A <c>GC.Collect()</c> or a
/// <c>GC.GetTotalMemory(forceFullCollection: true)</c> in the sampler would
/// destroy the quantity being measured and make the instrument report that the
/// defect does not exist. So: <c>GC.GetTotalMemory(false)</c> and
/// <c>GC.GetGCMemoryInfo(GCKind.Any)</c> only.
/// </para>
/// <para>
/// RSS and CPU are read here as a cross-check, never as the authority — the
/// outer harness's <c>footprint</c> / <c>ps</c> numbers are what #1461 and
/// #1496 are argued in, and macOS keeps counting freed native tiles in malloc
/// regions that the runtime knows nothing about. <c>Process.Refresh()</c> is
/// mandatory before every read: #1406 was exactly this bug, and a fixture that
/// peaked at 2.53 GiB read back as 84 MiB without it.
/// </para>
/// </remarks>
internal readonly record struct PerfSample(
    long LiveHeapBytes,
    long CommittedBytes,
    long HeapSizeBytes,
    long FragmentedBytes,
    long AllocatedBytes,
    long WorkingSetBytes,
    double CpuTotalMs,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    int ContinuousInFlight,
    int ContinuousEntries,
    long ContinuousResidentBytes,
    long ContinuousByteBudget,
    int SinglePageEntries,
    double ZoomLevel,
    int CurrentPageIndex,
    int TotalPages)
{
    /// <summary>Read the runtime/process half of a sample. Never forces a GC.</summary>
    internal static PerfSample FromRuntime()
    {
        var info = GC.GetGCMemoryInfo(GCKind.Any);

        // Refresh before reading: Process caches WorkingSet64 and
        // TotalProcessorTime, and a stale snapshot is #1406.
        using var process = Process.GetCurrentProcess();
        process.Refresh();

        return new PerfSample(
            LiveHeapBytes: GC.GetTotalMemory(forceFullCollection: false),
            CommittedBytes: info.TotalCommittedBytes,
            HeapSizeBytes: info.HeapSizeBytes,
            FragmentedBytes: info.FragmentedBytes,
            AllocatedBytes: GC.GetTotalAllocatedBytes(precise: false),
            WorkingSetBytes: process.WorkingSet64,
            CpuTotalMs: process.TotalProcessorTime.TotalMilliseconds,
            Gen0Collections: GC.CollectionCount(0),
            Gen1Collections: GC.CollectionCount(1),
            Gen2Collections: GC.CollectionCount(2),
            ContinuousInFlight: 0,
            ContinuousEntries: 0,
            ContinuousResidentBytes: 0,
            ContinuousByteBudget: 0,
            SinglePageEntries: 0,
            ZoomLevel: 0,
            CurrentPageIndex: -1,
            TotalPages: 0);
    }
}
