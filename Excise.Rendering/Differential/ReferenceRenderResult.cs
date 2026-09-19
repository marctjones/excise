using System.Diagnostics;
using SkiaSharp;

namespace Excise.Rendering.Differential;

/// <summary>
/// Result from an external reference renderer subprocess.
/// </summary>
public sealed record ReferenceRenderResult(
    SKBitmap? Bitmap,
    string Status,
    string? ErrorMessage,
    long ElapsedMs,
    long? PeakWorkingSetBytes = null,
    long? CpuMs = null);

/// <summary>
/// OS-reported resources consumed by one external reference-renderer process.
/// Kept separate from the bitmap result so performance comparisons never need
/// to retain or cache a renderer's image output.
/// </summary>
public readonly record struct ReferenceProcessResources(long? PeakWorkingSetBytes, long? CpuMs)
{
    /// <summary>
    /// How often the running child is sampled. ⚠️ #1674 — this used to WAIT first and
    /// sample second, with a 100 ms wait. A process that exited inside that first wait
    /// returned from <c>WaitForExit(100)</c> immediately, so the sampling line below was
    /// never reached, and the post-exit <see cref="Capture"/> throws (the OS has already
    /// discarded the accounting) and is swallowed into null. Every run shorter than
    /// 100 ms therefore reported NO cpu and NO peak RSS at all.
    ///
    /// <para>That is not uniform noise — it deletes the measurement for whichever tool is
    /// FASTEST. Measured 2026-09-19 over 9 fixtures x 3 runs x 3 shapes: excise NativeAOT
    /// kept 15 samples of 27 and mutool 45 of 81, while ghostscript, pdftocairo and pdfbox
    /// — the three slowest — lost none. The surviving samples were the slow ones, so the
    /// AOT build reported a HIGHER cpu median (216 ms) than the Debug build (106 ms) while
    /// its wall clock was LOWER (148 vs 213 ms). A reader would conclude the opposite of
    /// the truth.</para>
    ///
    /// <para>So: sample BEFORE waiting, and wait in short slices. The cost is one
    /// <c>proc_pidinfo</c>-class syscall per slice — about 0.5% overhead on a 1.5 s render
    /// — which is worth paying to stop silently dropping the fast half of the data.</para>
    /// </summary>
    private const int SampleIntervalMs = 10;

    public static bool WaitForExitAndCapture(Process process, int timeoutMs, out ReferenceProcessResources resources)
    {
        var elapsed = Stopwatch.StartNew();
        long peakWorkingSetBytes = 0;
        long cpuMs = 0;
        while (elapsed.ElapsedMilliseconds < timeoutMs)
        {
            // Sample FIRST: a process that exits during the wait below leaves nothing
            // readable behind, so anything not captured while it was alive is lost.
            var sample = Capture(process);
            peakWorkingSetBytes = Math.Max(peakWorkingSetBytes, sample.PeakWorkingSetBytes ?? 0);
            cpuMs = Math.Max(cpuMs, sample.CpuMs ?? 0);

            if (process.WaitForExit(SampleIntervalMs))
            {
                process.WaitForExit(); // flush redirected output before the caller reads it
                resources = Merge(peakWorkingSetBytes, cpuMs, Capture(process));
                return true;
            }
        }

        resources = new ReferenceProcessResources(
            peakWorkingSetBytes > 0 ? peakWorkingSetBytes : null,
            cpuMs > 0 ? cpuMs : null);
        return false;
    }

    public static ReferenceProcessResources Capture(Process process)
    {
        try
        {
            // #1406 — Process caches WorkingSet64/PeakWorkingSet64/TotalProcessorTime
            // internally and never re-queries the OS unless Refresh() is called first.
            // WaitForExitAndCapture polls this method every ~100ms across a render that
            // takes seconds, and without Refresh() every sample after the first returned
            // the SAME near-process-start snapshot — so a fixture that actually peaked at
            // 2.53 GiB was reported as 84 MiB, a >30x undercount. Measured directly: an
            // isolated repro process that grows from 2MB to 520MB read a flat 2.5MB on
            // every sample without Refresh() and tracked the real growth with it.
            process.Refresh();
            var workingSet = process.WorkingSet64;
            if (workingSet <= 0 && !OperatingSystem.IsWindows())
                workingSet = TryReadUnixRssBytes(process.Id) ?? 0;
            return new ReferenceProcessResources(
                Math.Max(process.PeakWorkingSet64, workingSet) > 0 ? Math.Max(process.PeakWorkingSet64, workingSet) : null,
                (long)Math.Round(process.TotalProcessorTime.TotalMilliseconds));
        }
        catch (InvalidOperationException)
        {
            // Some platforms discard process accounting immediately on exit.
            return new ReferenceProcessResources(null, null);
        }
    }

    private static ReferenceProcessResources Merge(long peakWorkingSetBytes, long cpuMs, ReferenceProcessResources final)
        => new(
            Math.Max(peakWorkingSetBytes, final.PeakWorkingSetBytes ?? 0) is var peak && peak > 0 ? peak : null,
            Math.Max(cpuMs, final.CpuMs ?? 0) is var cpu && cpu > 0 ? cpu : null);

    private static long? TryReadUnixRssBytes(int processId)
    {
        try
        {
            using var ps = Process.Start(new ProcessStartInfo("ps")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "-o", "rss=", "-p", processId.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            });
            if (ps == null || !ps.WaitForExit(500) || ps.ExitCode != 0) return null;
            var output = ps.StandardOutput.ReadToEnd().Trim();
            return long.TryParse(output, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var kib) && kib > 0
                ? kib * 1024L : null;
        }
        catch
        {
            return null;
        }
    }
}
