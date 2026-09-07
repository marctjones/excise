using System;
using System.Diagnostics;
using System.IO;
using SkiaSharp;

namespace Excise.Rendering.Differential;

/// <summary>
/// Shells out to <c>pdftocairo</c> (Poppler) to render a page,
/// providing the first escalation reference alongside <c>mutool draw</c>.
///
/// Why Poppler first: when excise disagrees with mutool, we can't tell
/// whether excise is wrong or mutool is wrong. Poppler/Cairo is the first
/// independent engine we ask for a second opinion. If that still does
/// not settle the page, the harness escalates to Ghostscript for a third
/// vote before calling the page a real bug.
///
/// Mutool is GPL/AGPL; Poppler is GPL. Both are CLI subprocesses, so
/// the licensing stays out of excise's binary — same model as mutool.
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
            $"excise-pdftocairo-ref-{Guid.NewGuid():N}");
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
            // #1380 — render the CropBox, not the MediaBox.
            //
            // pdftocairo defaults to the MediaBox; excise, mutool, PDFium and PDFBox all
            // display the CropBox, which is what §7.7.3.3 requires ("the region to which
            // the contents of the page shall be clipped when displayed or printed").
            // Without this flag every page whose CropBox differs from its MediaBox is
            // rasterised at a different size over a different region, and the harness
            // scores the mismatch as excise disagreeing with the reference.
            //
            // Measured at the scan's own 150 dpi, excise-vs-pdftocairo diffFraction:
            //   bug1802506 0.1328 -> 0.0041   issue2884_reduced 0.1988 -> 0.0246
            //   bug1922766 0.1625 -> 0.0367   copy_paste_ligatures 0.2482 -> 0.0474
            //   issue4402  0.3106 -> 0.0799   issue16316 0.4784 -> 0.1420
            //   issue2177  0.5912 -> 0.1653
            // Page dimensions match excise exactly once it is passed.
            psi.ArgumentList.Add("-cropbox");
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
            if (!ReferenceProcessResources.WaitForExitAndCapture(p, timeoutMs, out var resources))
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
                : new ReferenceRenderResult(bitmap, "OK", null, sw.ElapsedMilliseconds,
                    resources.PeakWorkingSetBytes, resources.CpuMs);
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
