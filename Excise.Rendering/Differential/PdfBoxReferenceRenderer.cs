using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using SkiaSharp;

namespace Excise.Rendering.Differential;

/// <summary>
/// Shells out to Apache PDFBox's command-line renderer. This is an optional
/// diagnostic oracle used when MuPDF/Poppler/Ghostscript do not explain a
/// rendering split.
/// </summary>
public static class PdfBoxReferenceRenderer
{
    private sealed record Invocation(string Command, string[] PrefixArgs, string Description);

    private static readonly Lazy<Invocation?> _invocation = new(ResolveInvocation);

    public static bool IsAvailable => _invocation.Value != null;

    public static SKBitmap? RenderPage(string pdfPath, int pageNumber, int dpi, int timeoutMs = 30_000)
        => TryRenderPage(pdfPath, pageNumber, dpi, timeoutMs).Bitmap;

    public static SKBitmap? RenderPage(string pdfPath, int pageNumber, int dpi, int timeoutMs, string? userPassword)
        => TryRenderPage(pdfPath, pageNumber, dpi, timeoutMs, userPassword).Bitmap;

    public static ReferenceRenderResult TryRenderPage(string pdfPath, int pageNumber, int dpi, int timeoutMs = 30_000)
        => TryRenderPage(pdfPath, pageNumber, dpi, timeoutMs, userPassword: null);

    public static ReferenceRenderResult TryRenderPage(
        string pdfPath,
        int pageNumber,
        int dpi,
        int timeoutMs,
        string? userPassword)
    {
        var sw = Stopwatch.StartNew();
        var invocation = _invocation.Value;
        if (invocation == null)
            return new ReferenceRenderResult(null, "TOOL_UNAVAILABLE",
                "PDFBox is not configured; set EXCISE_PDFBOX_JAR or EXCISE_PDFBOX_COMMAND",
                sw.ElapsedMilliseconds);

        var outPrefix = Path.Combine(Path.GetTempPath(), $"excise-pdfbox-ref-{Guid.NewGuid():N}");
        var outDir = Path.GetDirectoryName(outPrefix)!;
        var outName = Path.GetFileName(outPrefix);

        try
        {
            var args = invocation.PrefixArgs.Concat(new[]
            {
                "render",
                "-format=png",
                $"-dpi={dpi}",
                // -page alone does NOT restrict PDFBox 3.x output: it still
                // renders every page. Bound the range as well, or a 6-page PDF
                // yields six PNGs and the selection below has to guess.
                $"-page={pageNumber}",
                $"-startPage={pageNumber}",
                $"-endPage={pageNumber}",
                $"-i={pdfPath}",
                $"-prefix={outPrefix}",
            });
            if (userPassword != null)
                args = args.Append($"-password={userPassword}");

            var psi = CreateStartInfo(invocation.Command, args);

            using var p = Process.Start(psi);
            if (p == null)
                return new ReferenceRenderResult(null, "START_FAILED", "Process.Start returned null", sw.ElapsedMilliseconds);
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                var timeoutOutput = FormatCapturedOutput(
                    ReadCapturedOutput(stderrTask),
                    ReadCapturedOutput(stdoutTask));
                return new ReferenceRenderResult(null, "TIMEOUT",
                    AppendDetail($"{invocation.Description} exceeded {timeoutMs}ms", timeoutOutput),
                    sw.ElapsedMilliseconds);
            }

            var stdout = ReadCapturedOutput(stdoutTask);
            var stderr = ReadCapturedOutput(stderrTask);
            var capturedOutput = FormatCapturedOutput(stderr, stdout);
            if (p.ExitCode != 0)
            {
                return new ReferenceRenderResult(null, "EXIT_CODE",
                    AppendDetail($"{invocation.Description} exited {p.ExitCode}", capturedOutput),
                    sw.ElapsedMilliseconds);
            }

            // Pick the file for the page we ASKED for, not the newest one.
            // PDFBox writes pages in order, so "most recently written" is the
            // LAST page — asking for page 1 of irs-w9.pdf returned page 6
            // (ink 0.0360 instead of 0.0780). Because the renderer was
            // referenced by zero tests, that silently wrong page went unnoticed
            // (#868).
            var candidates = Directory
                .EnumerateFiles(outDir, outName + "*.png", SearchOption.TopDirectoryOnly)
                .ToList();
            var outPath = candidates.FirstOrDefault(f =>
                    Path.GetFileNameWithoutExtension(f)
                        .EndsWith($"-{pageNumber}", StringComparison.Ordinal))
                ?? (candidates.Count == 1 ? candidates[0] : null);
            if (outPath == null)
                return new ReferenceRenderResult(null, "MISSING_OUTPUT",
                    AppendDetail($"{invocation.Description} did not write an output PNG", capturedOutput),
                    sw.ElapsedMilliseconds);

            var bitmap = SKBitmap.Decode(outPath);
            return bitmap == null
                ? new ReferenceRenderResult(null, "DECODE_ERROR",
                    $"{invocation.Description} output PNG could not be decoded", sw.ElapsedMilliseconds)
                : new ReferenceRenderResult(bitmap, "OK", null, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return new ReferenceRenderResult(null, "ERROR", ex.Message, sw.ElapsedMilliseconds);
        }
        finally
        {
            foreach (var path in Directory.EnumerateFiles(outDir, outName + "*.png", SearchOption.TopDirectoryOnly))
            {
                try { File.Delete(path); } catch { }
            }
        }
    }

    private static Invocation? ResolveInvocation()
    {
        var explicitCommand = Environment.GetEnvironmentVariable("EXCISE_PDFBOX_COMMAND");
        if (!string.IsNullOrWhiteSpace(explicitCommand) && CanStart(explicitCommand, "--help"))
            return new Invocation(explicitCommand, Array.Empty<string>(), explicitCommand);

        var jarPath = Environment.GetEnvironmentVariable("EXCISE_PDFBOX_JAR")
            ?? Environment.GetEnvironmentVariable("PDFBOX_APP_JAR")
            // Fall back to the jar scripts/download-pdfbox.sh drops in
            // tools/vendor/. Without this, running that script was not enough:
            // the tests still skipped unless the caller also exported
            // EXCISE_PDFBOX_JAR, which is the kind of hidden second step that
            // leaves a suite quietly skipping (see the zero-skip work in
            // ca82e76a). Explicit env vars still win.
            ?? FindVendoredPdfBoxJar();
        var javaCommand = ResolveJavaCommand();
        if (!string.IsNullOrWhiteSpace(jarPath) && File.Exists(jarPath) && javaCommand != null)
            return new Invocation(javaCommand, new[] { "-Djava.awt.headless=true", "-jar", jarPath }, $"PDFBox {Path.GetFileName(jarPath)}");

        foreach (var command in new[] { "pdfbox", "pdfbox-app" })
        {
            if (CanStart(command, "--help"))
                return new Invocation(command, Array.Empty<string>(), command);
        }

        return null;
    }

    private static string? ResolveJavaCommand()
    {
        var explicitJava = Environment.GetEnvironmentVariable("EXCISE_JAVA_COMMAND");
        var candidates = string.IsNullOrWhiteSpace(explicitJava)
            ? new[] { "/opt/homebrew/opt/openjdk/bin/java", "java" }
            : new[] { explicitJava };

        return candidates.FirstOrDefault(candidate => CanStartSuccessfully(candidate, "-version"));
    }

    private static bool CanStart(string command, params string[] args)
    {
        try
        {
            var psi = CreateStartInfo(command, args);
            using var p = Process.Start(psi);
            if (p == null) return false;
            if (!p.WaitForExit(2000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool CanStartSuccessfully(string command, params string[] args)
    {
        try
        {
            var psi = CreateStartInfo(command, args);
            using var p = Process.Start(psi);
            if (p == null) return false;
            if (!p.WaitForExit(2000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static ProcessStartInfo CreateStartInfo(string command, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(command)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        return psi;
    }

    private static string ReadCapturedOutput(System.Threading.Tasks.Task<string> readTask)
    {
        try
        {
            return readTask.GetAwaiter().GetResult();
        }
        catch
        {
            return "";
        }
    }

    private static string? FormatCapturedOutput(string stderr, string stdout)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(stderr))
            parts.Add("stderr: " + stderr.Trim());
        if (!string.IsNullOrWhiteSpace(stdout))
            parts.Add("stdout: " + stdout.Trim());

        return parts.Count == 0 ? null : Trunc(string.Join("; ", parts), 200);
    }

    private static string AppendDetail(string message, string? detail)
        => string.IsNullOrWhiteSpace(detail) ? message : $"{message}: {detail}";

    private static string Trunc(string value, int length)
        => value.Length <= length ? value : value.Substring(0, length) + "…";

    /// <summary>
    /// Locates tools/vendor/pdfbox-app-*.jar by walking up from the test
    /// assembly. Returns the highest version present so a stale jar left behind
    /// by an older download does not shadow a newer one.
    /// </summary>
    private static string? FindVendoredPdfBoxJar()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var vendor = Path.Combine(dir.FullName, "tools", "vendor");
            if (Directory.Exists(vendor))
            {
                var jar = Directory
                    .EnumerateFiles(vendor, "pdfbox-app-*.jar", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(f => f, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (jar != null) return jar;
            }
            dir = dir.Parent;
        }
        return null;
    }
}
