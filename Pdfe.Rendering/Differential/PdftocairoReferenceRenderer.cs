using System;
using System.Diagnostics;
using System.IO;
using SkiaSharp;

namespace Pdfe.Rendering.Differential;

/// <summary>
/// Shells out to <c>pdftocairo</c> (Poppler) to render a page,
/// providing the first escalation reference alongside <c>mutool draw</c>.
///
/// Why Poppler first: when pdfe disagrees with mutool, we can't tell
/// whether pdfe is wrong or mutool is wrong. Poppler/Cairo is the first
/// independent engine we ask for a second opinion. If that still does
/// not settle the page, the harness escalates to Ghostscript for a third
/// vote before calling the page a real bug.
///
/// Mutool is GPL/AGPL; Poppler is GPL. Both are CLI subprocesses, so
/// the licensing stays out of pdfe's binary — same model as mutool.
///
/// Returns null on missing tool, timeout, non-zero exit, or decode
/// failure — same contract as <see cref="MutoolReferenceRenderer"/>.
/// </summary>
public static class PdftocairoReferenceRenderer
{
    private static readonly Lazy<bool> _available = new(() =>
    {
        try
        {
            var psi = new ProcessStartInfo("pdftocairo", "-v")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit(2000);
            return true; // any exit code means it's installed
        }
        catch
        {
            return false;
        }
    });

    public static bool IsAvailable => _available.Value;

    /// <summary>
    /// Render <paramref name="pageNumber"/> (1-based) at <paramref name="dpi"/>
    /// via <c>pdftocairo -png -singlefile</c>. Returns null on any failure.
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
            return new ReferenceRenderResult(null, "TOOL_UNAVAILABLE", "pdftocairo is not on PATH", sw.ElapsedMilliseconds);

        // pdftocairo's -singlefile mode emits exactly <prefix>.png with
        // no further suffix — easier to predict than the multi-file mode.
        var outPrefix = Path.Combine(Path.GetTempPath(),
            $"pdfe-pdftocairo-ref-{Guid.NewGuid():N}");
        var outPath = outPrefix + ".png";

        try
        {
            var psi = new ProcessStartInfo("pdftocairo")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-png");
            psi.ArgumentList.Add("-singlefile");
            psi.ArgumentList.Add("-r");
            psi.ArgumentList.Add(dpi.ToString(System.Globalization.CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add(pageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("-l");
            psi.ArgumentList.Add(pageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (userPassword != null)
            {
                psi.ArgumentList.Add("-upw");
                psi.ArgumentList.Add(userPassword);
            }
            psi.ArgumentList.Add(pdfPath);
            psi.ArgumentList.Add(outPrefix);

            using var p = Process.Start(psi);
            if (p == null)
                return new ReferenceRenderResult(null, "START_FAILED", "Process.Start returned null", sw.ElapsedMilliseconds);
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return new ReferenceRenderResult(null, "TIMEOUT", $"pdftocairo exceeded {timeoutMs}ms", sw.ElapsedMilliseconds);
            }
            if (p.ExitCode != 0)
            {
                var stderr = p.StandardError.ReadToEnd();
                return new ReferenceRenderResult(null, "EXIT_CODE",
                    $"pdftocairo exited {p.ExitCode}: {Trunc(stderr.Trim(), 200)}", sw.ElapsedMilliseconds);
            }
            if (!File.Exists(outPath))
                return new ReferenceRenderResult(null, "MISSING_OUTPUT", "pdftocairo did not write an output PNG", sw.ElapsedMilliseconds);

            var bitmap = SKBitmap.Decode(outPath);
            return bitmap == null
                ? new ReferenceRenderResult(null, "DECODE_ERROR", "pdftocairo output PNG could not be decoded", sw.ElapsedMilliseconds)
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
