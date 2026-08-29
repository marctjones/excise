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
    public static bool WaitForExitAndCapture(Process process, int timeoutMs, out ReferenceProcessResources resources)
    {
        var elapsed = Stopwatch.StartNew();
        long peakWorkingSetBytes = 0;
        long cpuMs = 0;
        while (elapsed.ElapsedMilliseconds < timeoutMs)
        {
            if (process.WaitForExit(100))
            {
                process.WaitForExit(); // flush redirected output before the caller reads it
                resources = Merge(peakWorkingSetBytes, cpuMs, Capture(process));
                return true;
            }

            var sample = Capture(process);
            peakWorkingSetBytes = Math.Max(peakWorkingSetBytes, sample.PeakWorkingSetBytes ?? 0);
            cpuMs = Math.Max(cpuMs, sample.CpuMs ?? 0);
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
