using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using SkiaSharp;

namespace Pdfe.Rendering.Differential;

/// <summary>
/// Shells out to PDFium's standalone <c>pdfium_test</c> sample renderer.
/// This is an optional browser-engine oracle for diagnostic corpus runs.
/// </summary>
public static class PdfiumReferenceRenderer
{
    private static readonly Lazy<string?> _commandName = new(() =>
    {
        var explicitCommand = Environment.GetEnvironmentVariable("PDFE_PDFIUM_TEST");
        if (!string.IsNullOrWhiteSpace(explicitCommand) && CanStart(explicitCommand, "--help"))
            return explicitCommand;

        return CanStart("pdfium_test", "--help") ? "pdfium_test" : null;
    });

    public static bool IsAvailable => _commandName.Value != null;

    public static SKBitmap? RenderPage(string pdfPath, int pageNumber, int dpi, int timeoutMs = 30_000)
        => TryRenderPage(pdfPath, pageNumber, dpi, timeoutMs).Bitmap;

    public static ReferenceRenderResult TryRenderPage(string pdfPath, int pageNumber, int dpi, int timeoutMs = 30_000)
    {
        var sw = Stopwatch.StartNew();
        var command = _commandName.Value;
        if (command == null)
            return new ReferenceRenderResult(null, "TOOL_UNAVAILABLE",
                "pdfium_test is not on PATH; set PDFE_PDFIUM_TEST to the standalone renderer",
                sw.ElapsedMilliseconds);

        var tempDir = Path.Combine(Path.GetTempPath(), $"pdfe-pdfium-ref-{Guid.NewGuid():N}");
        var tempPdf = Path.Combine(tempDir, "input.pdf");
        var zeroBasedPage = checked(pageNumber - 1);
        var expectedPng = tempPdf + "." + zeroBasedPage + ".png";
        var scale = Math.Max(1.0 / 72.0, dpi / 72.0);

        try
        {
            Directory.CreateDirectory(tempDir);
            File.Copy(pdfPath, tempPdf, overwrite: true);

            var psi = new ProcessStartInfo(command)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--png");
            psi.ArgumentList.Add($"--pages={zeroBasedPage}");
            psi.ArgumentList.Add($"--scale={scale.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            psi.ArgumentList.Add(tempPdf);

            using var p = Process.Start(psi);
            if (p == null)
                return new ReferenceRenderResult(null, "START_FAILED", "Process.Start returned null", sw.ElapsedMilliseconds);
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return new ReferenceRenderResult(null, "TIMEOUT", $"pdfium_test exceeded {timeoutMs}ms", sw.ElapsedMilliseconds);
            }
            if (p.ExitCode != 0)
            {
                var stderr = p.StandardError.ReadToEnd();
                return new ReferenceRenderResult(null, "EXIT_CODE",
                    $"pdfium_test exited {p.ExitCode}: {Trunc(stderr.Trim(), 200)}", sw.ElapsedMilliseconds);
            }

            var outPath = File.Exists(expectedPng)
                ? expectedPng
                : Directory.GetFiles(tempDir, "input.pdf.*.png", SearchOption.TopDirectoryOnly)
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .FirstOrDefault();
            if (outPath == null)
                return new ReferenceRenderResult(null, "MISSING_OUTPUT", "pdfium_test did not write an output PNG", sw.ElapsedMilliseconds);

            var bitmap = SKBitmap.Decode(outPath);
            return bitmap == null
                ? new ReferenceRenderResult(null, "DECODE_ERROR", "pdfium_test output PNG could not be decoded", sw.ElapsedMilliseconds)
                : new ReferenceRenderResult(bitmap, "OK", null, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return new ReferenceRenderResult(null, "ERROR", ex.Message, sw.ElapsedMilliseconds);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static bool CanStart(string command, params string[] args)
    {
        try
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

    private static string Trunc(string value, int length)
        => value.Length <= length ? value : value.Substring(0, length) + "…";
}
