using System;
using System.Diagnostics;
using System.IO;
using SkiaSharp;

namespace Pdfe.Rendering.Differential;

/// <summary>
/// Shells out to Ghostscript to render a page, providing a third
/// independent reference renderer alongside <c>mutool draw</c> and
/// <c>pdftocairo</c>.
///
/// Why Ghostscript: when MuPDF and Poppler do not settle a page, we
/// need a third oracle before classifying the page as a pdfe bug or a
/// reference disagreement. Ghostscript is a long-lived PDF renderer and
/// gives us a practical third vote without pulling any rendering code
/// into pdfe itself.
///
/// Returns null on missing tool, timeout, non-zero exit, or decode
/// failure.
/// </summary>
public static class GhostscriptReferenceRenderer
{
    private static readonly Lazy<string?> _commandName = new(() =>
    {
        var explicitCommand = Environment.GetEnvironmentVariable("PDFE_GHOSTSCRIPT_COMMAND");
        var candidates = string.IsNullOrWhiteSpace(explicitCommand)
            ? new[] { "ghostpdf", "gpdf", "gs", "gswin64c", "gswin32c" }
            : new[] { explicitCommand };

        foreach (var candidate in candidates)
        {
            try
            {
                var psi = new ProcessStartInfo(candidate, "--version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p == null) continue;
                p.WaitForExit(2000);
                return candidate;
            }
            catch
            {
                // Try the next candidate.
            }
        }

        return null;
    });

    public static bool IsAvailable => _commandName.Value != null;

    /// <summary>
    /// Render <paramref name="pageNumber"/> (1-based) at <paramref name="dpi"/>
    /// via Ghostscript. Returns null on any failure.
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
        var command = _commandName.Value;
        if (command == null)
            return new ReferenceRenderResult(null, "TOOL_UNAVAILABLE",
                "Ghostscript is not on PATH; set PDFE_GHOSTSCRIPT_COMMAND or install gs/ghostpdf",
                sw.ElapsedMilliseconds);

        var outPath = Path.Combine(Path.GetTempPath(),
            $"pdfe-ghostscript-ref-{Guid.NewGuid():N}.png");

        try
        {
            var psi = new ProcessStartInfo(command)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-dBATCH");
            psi.ArgumentList.Add("-dNOPAUSE");
            psi.ArgumentList.Add("-dSAFER");
            psi.ArgumentList.Add("-dQUIET");
            psi.ArgumentList.Add("-sDEVICE=png16m");
            psi.ArgumentList.Add("-dTextAlphaBits=4");
            psi.ArgumentList.Add("-dGraphicsAlphaBits=4");
            psi.ArgumentList.Add($"-r{dpi.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            psi.ArgumentList.Add($"-dFirstPage={pageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            psi.ArgumentList.Add($"-dLastPage={pageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            if (userPassword != null)
                psi.ArgumentList.Add($"-sPDFPassword={userPassword}");
            psi.ArgumentList.Add($"-sOutputFile={outPath}");
            psi.ArgumentList.Add(pdfPath);

            using var p = Process.Start(psi);
            if (p == null)
                return new ReferenceRenderResult(null, "START_FAILED", "Process.Start returned null", sw.ElapsedMilliseconds);
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return new ReferenceRenderResult(null, "TIMEOUT", $"{command} exceeded {timeoutMs}ms", sw.ElapsedMilliseconds);
            }
            if (p.ExitCode != 0)
            {
                var stderr = p.StandardError.ReadToEnd();
                return new ReferenceRenderResult(null, "EXIT_CODE",
                    $"{command} exited {p.ExitCode}: {Trunc(stderr.Trim(), 200)}", sw.ElapsedMilliseconds);
            }
            if (!File.Exists(outPath))
                return new ReferenceRenderResult(null, "MISSING_OUTPUT", $"{command} did not write an output PNG", sw.ElapsedMilliseconds);

            var bitmap = SKBitmap.Decode(outPath);
            return bitmap == null
                ? new ReferenceRenderResult(null, "DECODE_ERROR", $"{command} output PNG could not be decoded", sw.ElapsedMilliseconds)
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
