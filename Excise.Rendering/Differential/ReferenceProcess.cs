using System.Diagnostics;
using SkiaSharp;

namespace Excise.Rendering.Differential;

/// <summary>
/// What one external reference-tool process did (#1757). <c>ExitCode</c> is -1 when it never
/// started or did not exit in time.
/// </summary>
internal readonly record struct ReferenceProcessRun(
    bool Started, bool TimedOut, int ExitCode, string Stdout, string Stderr, ReferenceProcessResources Resources);

/// <summary>
/// The one launch, drain and timeout path of every reference-oracle wrapper (#1757). A wrapper
/// keeps its argument list, its status strings and its messages; it does not keep a copy of this.
/// </summary>
internal static class ReferenceProcess
{
    // A grandchild that inherited a pipe keeps it open after the tool has exited (#1068), so the
    // read that follows the exit is bounded too.
    private const int DrainGraceMs = 2000;

    private static readonly ReferenceProcessRun NotStarted = new(false, false, -1, "", "", default);

    /// <summary>
    /// Starts <paramref name="command"/> with both output pipes redirected. Throws what
    /// <see cref="Process.Start(ProcessStartInfo)"/> throws (Win32Exception: not installed).
    /// </summary>
    internal static Process? Start(string command, IEnumerable<string> args,
        IEnumerable<KeyValuePair<string, string>>? environment = null)
    {
        var psi = new ProcessStartInfo(command)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        foreach (var (key, value) in environment ?? []) psi.Environment[key] = value;
        return Process.Start(psi);
    }

    /// <summary>
    /// Runs <paramref name="command"/> for at most <paramref name="timeoutMs"/> and kills its tree on
    /// timeout. Both pipes are drained from the start: a child that fills one 64 KB pipe while nobody
    /// reads it blocks forever, which read as a TIMEOUT verdict for a tool whose work was done (the
    /// same drain PdfOcrService does). Resources are sampled while it runs, whichever tool it is.
    /// </summary>
    internal static ReferenceProcessRun Run(string command, IEnumerable<string> args, int timeoutMs,
        IEnumerable<KeyValuePair<string, string>>? environment = null)
    {
        using var p = Start(command, args, environment);
        if (p == null) return NotStarted;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        var exited = ReferenceProcessResources.WaitForExitAndCapture(p, timeoutMs, out var resources);
        if (!exited)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
        }
        return new ReferenceProcessRun(true, !exited, exited ? p.ExitCode : -1,
            Drained(stdout), Drained(stderr), resources);
    }

    private static string Drained(Task<string> read)
    {
        try { return read.Wait(DrainGraceMs) ? read.Result : ""; }
        catch { return ""; }
    }

    private static ReferenceProcessRun? Probe(string command, int timeoutMs, string[] args)
    {
        try
        {
            var run = Run(command, args, timeoutMs);
            return run.Started ? run : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>True when the tool launches, whatever it exits with (some print usage and exit 1).</summary>
    internal static bool IsLaunchable(string command, int timeoutMs, params string[] args)
        => Probe(command, timeoutMs, args) != null;

    /// <summary>True when the tool launches and exits 0 within <paramref name="timeoutMs"/>.</summary>
    internal static bool ExitsZero(string command, int timeoutMs, params string[] args)
        => Probe(command, timeoutMs, args) is { ExitCode: 0 };

    /// <summary>
    /// Runs a tool that writes its result to <paramref name="outPath"/> and returns that file's text.
    /// Null is "no answer" (not installed, timeout, non-zero exit, nothing written), never an
    /// exception. Deletes <paramref name="outPath"/>.
    /// </summary>
    internal static string? RunToTextFile(string command, IEnumerable<string> args, string outPath, int timeoutMs)
    {
        try
        {
            var run = Run(command, args, timeoutMs);
            return run.ExitCode == 0 && File.Exists(outPath)
                ? File.ReadAllText(outPath)
                : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            try { File.Delete(outPath); } catch { }
        }
    }

    /// <summary>
    /// Runs a tool that renders one page to a PNG and maps every way that can end to its status.
    /// <paramref name="label"/> is what the messages call the tool; <paramref name="locateOutput"/>
    /// returns the PNG the tool wrote, or null when it wrote none.
    /// </summary>
    internal static ReferenceRenderResult RenderPng(Stopwatch sw, string label, string command,
        IEnumerable<string> args, int timeoutMs, Func<string?> locateOutput)
    {
        try
        {
            var run = Run(command, args, timeoutMs);
            if (!run.Started)
                return new ReferenceRenderResult(null, "START_FAILED", "Process.Start returned null", sw.ElapsedMilliseconds);
            if (run.TimedOut)
                return new ReferenceRenderResult(null, "TIMEOUT", $"{label} exceeded {timeoutMs}ms", sw.ElapsedMilliseconds);
            if (run.ExitCode != 0)
                return new ReferenceRenderResult(null, "EXIT_CODE",
                    $"{label} exited {run.ExitCode}: {Trunc(run.Stderr.Trim(), 200)}", sw.ElapsedMilliseconds);

            var outPath = locateOutput();
            if (outPath == null)
                return new ReferenceRenderResult(null, "MISSING_OUTPUT", $"{label} did not write an output PNG", sw.ElapsedMilliseconds);

            var bitmap = SKBitmap.Decode(outPath);
            return bitmap == null
                ? new ReferenceRenderResult(null, "DECODE_ERROR", $"{label} output PNG could not be decoded", sw.ElapsedMilliseconds)
                : new ReferenceRenderResult(bitmap, "OK", null, sw.ElapsedMilliseconds,
                    run.Resources.PeakWorkingSetBytes, run.Resources.CpuMs);
        }
        catch (Exception ex)
        {
            return new ReferenceRenderResult(null, "ERROR", ex.Message, sw.ElapsedMilliseconds);
        }
    }

    internal static string Trunc(string value, int length)
        => value.Length <= length ? value : value.Substring(0, length) + "…";
}
