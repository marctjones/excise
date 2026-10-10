using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Excise.TestSupport;

/// <summary>
/// Independent list of the images MuPDF finds on a page (<c>mutool info -I</c>):
/// filter and pixel size per image. Used to prove that an image a redaction
/// replaced is no longer reachable from the page, without asking excise.
/// </summary>
internal static partial class MutoolImageOracle
{
    private static readonly string? Executable = FindOnPath("mutool");

    public static bool IsAvailable => Executable is not null;

    internal readonly record struct PageImage(string Filter, int Width, int Height);

    public static IReadOnlyList<PageImage> Images(byte[] pdf, int pageNumber)
    {
        if (Executable is null)
            throw new InvalidOperationException("mutool is not available on PATH");

        var path = Path.Combine(Path.GetTempPath(), $"excise-mutool-img-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdf);
        try
        {
            var start = new ProcessStartInfo(Executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in new[] { "info", "-I", path, pageNumber.ToString() })
                start.ArgumentList.Add(argument);

            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("mutool did not start");
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30_000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("mutool info exceeded 30 seconds");
            }
            var stdout = stdoutTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"mutool info exited {process.ExitCode}: {stderrTask.GetAwaiter().GetResult()}");
            return ImageLine().Matches(stdout)
                .Select(m => new PageImage(m.Groups[1].Value.Trim(),
                    int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)))
                .ToList();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [GeneratedRegex(@"\[\s*([^\]]+?)\s*\]\s+(\d+)x(\d+)")]
    private static partial Regex ImageLine();

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
