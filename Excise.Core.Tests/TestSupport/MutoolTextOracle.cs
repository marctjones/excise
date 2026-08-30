using System.Diagnostics;

namespace Excise.TestSupport;

/// <summary>
/// Independent saved-PDF text extraction through MuPDF's <c>mutool</c>.
/// Test code uses this instead of duplicating process discovery and invocation
/// in every corpus regression that needs a non-Excise oracle.
/// </summary>
internal static class MutoolTextOracle
{
    private static readonly string? Executable = FindOnPath("mutool");

    public static bool IsAvailable => Executable is not null;

    public static string ExtractAllPages(byte[] pdf)
    {
        if (Executable is null)
            throw new InvalidOperationException("mutool is not available on PATH");

        var path = Path.Combine(Path.GetTempPath(), $"excise-mutool-text-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdf);
        try
        {
            var start = new ProcessStartInfo(Executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in new[] { "draw", "-F", "txt", "-o", "-", path })
                start.ArgumentList.Add(argument);

            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("mutool did not start");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(30_000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("mutool text extraction exceeded 30 seconds");
            }
            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"mutool text extraction exited {process.ExitCode}: {stderr}");
            return stdout;
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string? FindOnPath(string executable)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            var candidate = Path.Combine(directory, executable);
            if (File.Exists(candidate)) return candidate;
            if (File.Exists(candidate + ".exe")) return candidate + ".exe";
        }
        return null;
    }
}
