using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Excise.App.Services.Printing;

/// <summary>
/// What one CUPS command-line invocation did (#1710).
/// </summary>
/// <param name="Executed">
/// False when the process never ran or never finished — the binary is not on
/// PATH, or it exceeded its timeout. <paramref name="Failure"/> then says
/// which, and <paramref name="ExitCode"/> is meaningless.
/// </param>
/// <param name="ExitCode">The process exit code. 0 is success for every CUPS tool used here.</param>
/// <param name="StandardOutput">Everything the process wrote to stdout.</param>
/// <param name="StandardError">Everything the process wrote to stderr.</param>
/// <param name="Failure">Why the process did not run to completion, or null when it did.</param>
internal readonly record struct CupsProcessResult(
    bool Executed,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    string? Failure = null)
{
    /// <summary>The process ran and reported success.</summary>
    internal bool Succeeded => Executed && ExitCode == 0;

    internal static CupsProcessResult NotRun(string failure) =>
        new(false, -1, string.Empty, string.Empty, failure);

    internal static CupsProcessResult Ran(int exitCode, string stdout, string stderr) =>
        new(true, exitCode, stdout, stderr);

    /// <summary>
    /// The most useful line of diagnostics: stderr when there is any, else
    /// stdout, else the failure reason. Trimmed and collapsed onto one line so
    /// it can go straight into a user-visible message.
    /// </summary>
    internal string Diagnostics()
    {
        string text = !string.IsNullOrWhiteSpace(StandardError) ? StandardError
            : !string.IsNullOrWhiteSpace(StandardOutput) ? StandardOutput
            : Failure ?? string.Empty;
        return string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}

/// <summary>
/// Runs one CUPS command-line tool (<c>lp</c>, <c>lpstat</c>) and waits for it
/// (#1710). The seam exists so <see cref="LinuxCupsDocumentPrinter"/> is
/// testable on any OS with a fake, the same way the Windows printer fakes its
/// two Win32 halves.
/// </summary>
internal interface ICupsProcessRunner
{
    /// <summary>
    /// Run <paramref name="fileName"/> with <paramref name="arguments"/> and
    /// return when it has exited or <paramref name="timeout"/> has passed.
    /// Never throws for a missing binary or a timeout: both come back as
    /// <see cref="CupsProcessResult.Executed"/> false (CLAUDE.md Pitfall 3 —
    /// a PDF tool without a timeout hangs the caller forever).
    /// </summary>
    CupsProcessResult Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout);
}

/// <summary>
/// The production <see cref="ICupsProcessRunner"/>: a real child process
/// (#1710).
/// </summary>
/// <remarks>
/// <para>
/// <b>The environment is forced to the C locale.</b> <c>lpstat</c>'s output is
/// translated, so "system default destination:" is not what a French or German
/// desktop prints, and parsing it under the user's locale would silently find
/// no default. Every CUPS invocation here is machine-readable output, never
/// something the user sees, so pinning the locale costs nothing.
/// </para>
/// <para>
/// Both streams are drained on background threads before the wait, which is
/// the documented way to avoid the deadlock a full pipe buffer causes when a
/// child writes more than the OS buffer while the parent waits for exit.
/// </para>
/// </remarks>
internal sealed class CupsProcessRunner : ICupsProcessRunner
{
    public CupsProcessResult Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        startInfo.Environment["LC_ALL"] = "C";
        startInfo.Environment["LANG"] = "C";

        Process? process = null;
        try
        {
            process = Process.Start(startInfo);
            if (process == null)
                return CupsProcessResult.NotRun($"{fileName} could not be started.");
        }
        catch (Win32Exception ex)
        {
            // ENOENT and friends: the CUPS client tools are not installed.
            return CupsProcessResult.NotRun($"{fileName} could not be started: {ex.Message}");
        }
        catch (SystemException ex)
        {
            return CupsProcessResult.NotRun($"{fileName} could not be started: {ex.Message}");
        }

        using (process)
        {
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            // Nothing is piped in; close the pipe so a tool that reads stdin
            // (lp with no file argument would) sees EOF instead of blocking.
            try { process.StandardInput.Close(); } catch (System.IO.IOException) { }

            if (!process.WaitForExit((int)Math.Clamp(timeout.TotalMilliseconds, 1, int.MaxValue)))
            {
                try { process.Kill(entireProcessTree: true); } catch (SystemException) { }
                return CupsProcessResult.NotRun(
                    $"{fileName} did not finish within {timeout.TotalSeconds:0.#} seconds.");
            }

            // WaitForExit(int) does not guarantee the async readers have
            // drained; the parameterless overload does.
            process.WaitForExit();
            return CupsProcessResult.Ran(process.ExitCode, stdout.ToString(), stderr.ToString());
        }
    }
}
