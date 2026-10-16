using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Excise.Cli;

/// <summary>
/// What one CLI process cost, as the process itself saw it: set <c>EXCISE_PERF_REPORT=&lt;file&gt;</c> and the CLI
/// writes one JSON object there when the command finishes. It exists because managed allocation, GC count and GC
/// pause cannot be read from outside a process, and the engine workflow budgets that read them run only on the
/// JIT (<c>Excise.RenderTools</c> is not Native AOT). <c>scripts/aot-perf-harness.py</c> reads it for every build
/// shape. Peak RSS is not reported here (Process.PeakWorkingSet64 reads 0 on macOS); the harness takes it from `/usr/bin/time -l`.
/// Hand-written JSON so it adds no serializer to the Native AOT binary.
/// </summary>
internal static class ProcessPerfReport
{
    internal const string EnvironmentVariable = "EXCISE_PERF_REPORT";

    /// <summary>Write the report for a finished command. Never throws: a measurement must not fail a command.</summary>
    internal static void TryWrite(string path, string[] args, int exitCode, TimeSpan wall)
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            File.WriteAllText(path, Format(
                command: args.Length > 0 ? args[0] : "",
                exitCode: exitCode,
                wallMs: wall.TotalMilliseconds,
                cpuMs: process.TotalProcessorTime.TotalMilliseconds,
                allocatedBytes: GC.GetTotalAllocatedBytes(precise: false),
                gen0: GC.CollectionCount(0), gen1: GC.CollectionCount(1), gen2: GC.CollectionCount(2),
                gcPauseMs: GC.GetTotalPauseDuration().TotalMilliseconds,
                dynamicCode: RuntimeFeature.IsDynamicCodeSupported));
        }
        catch (Exception)
        {
            // Deliberately silent: see the summary.
        }
    }

    /// <summary>The JSON text. Public inputs only, so tests need no process.</summary>
    internal static string Format(string command, int exitCode, double wallMs, double cpuMs,
        long allocatedBytes, int gen0, int gen1, int gen2, double gcPauseMs, bool dynamicCode)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder("{");
        sb.Append("\"command\":\"").Append(Escape(command)).Append('"');
        sb.Append(",\"exitCode\":").Append(exitCode.ToString(inv));
        sb.Append(",\"wallMs\":").Append(wallMs.ToString("F1", inv));
        sb.Append(",\"cpuMs\":").Append(cpuMs.ToString("F1", inv));
        sb.Append(",\"allocatedBytes\":").Append(allocatedBytes.ToString(inv));
        sb.Append(",\"gcCollections\":[").Append(gen0.ToString(inv)).Append(',').Append(gen1.ToString(inv)).Append(',').Append(gen2.ToString(inv)).Append(']');
        sb.Append(",\"gcPauseMs\":").Append(gcPauseMs.ToString("F1", inv));
        // Native AOT cannot generate code at run time; the JIT and ReadyToRun can. Tells AOT from the other two only.
        sb.Append(",\"nativeAot\":").Append(dynamicCode ? "false" : "true");
        return sb.Append('}').ToString();
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
