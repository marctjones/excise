using System;
using System.Diagnostics;
using System.IO;
using SkiaSharp;

namespace Excise.Rendering.Differential;

/// <summary>
/// Shells out to <c>mutool draw</c> (MuPDF) to render a page, providing a
/// reference rendering we can diff our own SkiaRenderer output against.
///
/// Why mutool: it's the most rigorously-tested OSS PDF renderer (Apache-2.0
/// CLI; the engine is AGPL-3.0 but we only invoke the CLI as a subprocess,
/// which doesn't propagate the AGPL into our own code). Crucially, it's
/// independent of our codebase — when its output differs from ours, the
/// disagreement is informative.
///
/// All methods return null when mutool isn't available so tests can degrade
/// to Skipped rather than fail in environments without it (CI windows-latest,
/// for example).
/// </summary>
public static class MutoolReferenceRenderer
{
    private static readonly Lazy<bool> _available = new(() =>
    {
        // mutool is one of those CLIs that exits non-zero when invoked
        // without a real command — even `mutool --version` returns 1.
        // So instead of a version probe, just see if it's launchable.
        // ProcessStartInfo.Start throws Win32Exception when the file
        // can't be found, which is the only failure we care about here.
        try
        {
            var psi = new ProcessStartInfo("mutool", "draw")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit(2000);
            return true; // launched successfully — any exit code means it's installed
        }
        catch
        {
            return false;
        }
    });

    /// <summary>True when <c>mutool</c> is on PATH and responds to --version.</summary>
    public static bool IsAvailable => _available.Value;

    /// <summary>
    /// Render <paramref name="pageNumber"/> (1-based) of <paramref name="pdfPath"/>
    /// at <paramref name="dpi"/> via <c>mutool draw -F png</c>. Returns null on
    /// any failure (timeout, non-zero exit, missing output, decode failure).
    /// </summary>
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
        if (!IsAvailable)
            return new ReferenceRenderResult(null, "TOOL_UNAVAILABLE", "mutool is not on PATH", sw.ElapsedMilliseconds);

        var outPath = Path.Combine(Path.GetTempPath(),
            $"excise-mutool-ref-{Guid.NewGuid():N}.png");

        try
        {
            var psi = new ProcessStartInfo("mutool")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("draw");
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add(outPath);
            psi.ArgumentList.Add("-F");
            psi.ArgumentList.Add("png");
            psi.ArgumentList.Add("-r");
            psi.ArgumentList.Add(dpi.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (userPassword != null)
            {
                psi.ArgumentList.Add("-p");
                psi.ArgumentList.Add(userPassword);
            }
            psi.ArgumentList.Add(pdfPath);
            psi.ArgumentList.Add(pageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));

            using var p = Process.Start(psi);
            if (p == null)
                return new ReferenceRenderResult(null, "START_FAILED", "Process.Start returned null", sw.ElapsedMilliseconds);
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return new ReferenceRenderResult(null, "TIMEOUT", $"mutool exceeded {timeoutMs}ms", sw.ElapsedMilliseconds);
            }
            if (p.ExitCode != 0)
            {
                var stderr = p.StandardError.ReadToEnd();
                return new ReferenceRenderResult(null, "EXIT_CODE",
                    $"mutool exited {p.ExitCode}: {Trunc(stderr.Trim(), 200)}", sw.ElapsedMilliseconds);
            }
            if (!File.Exists(outPath))
                return new ReferenceRenderResult(null, "MISSING_OUTPUT", "mutool did not write an output PNG", sw.ElapsedMilliseconds);

            var bitmap = SKBitmap.Decode(outPath);
            return bitmap == null
                ? new ReferenceRenderResult(null, "DECODE_ERROR", "mutool output PNG could not be decoded", sw.ElapsedMilliseconds)
                : new ReferenceRenderResult(bitmap, "OK", null, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return new ReferenceRenderResult(null, "ERROR", ex.Message, sw.ElapsedMilliseconds);
        }
        finally
        {
            try { File.Delete(outPath); } catch { }
        }
    }

    private static string Trunc(string value, int length)
        => value.Length <= length ? value : value.Substring(0, length) + "…";
}
