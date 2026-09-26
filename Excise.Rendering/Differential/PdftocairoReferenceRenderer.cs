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
internal static class PdftocairoReferenceRenderer
{
    private static readonly Lazy<bool> _available = new(() => ReferenceProcess.IsLaunchable("pdftocairo", 2000, "-v"));

    public static bool IsAvailable => _available.Value;

    /// <summary>
    /// The static flags TryRenderPage invokes with (#1385). Keep literally in
    /// sync with the argument list below -- see MutoolReferenceRenderer
    /// for why: #1380 added -cropbox here and the oracle render cache did not
    /// notice, silently reusing every pre-#1380 render.
    /// </summary>
    public const string InvocationSignature = "-png -singlefile -cropbox";

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
            var args = new List<string>
            {
                "-png", "-singlefile",
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
                "-cropbox",
                "-r", dpi.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-f", pageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-l", pageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            if (userPassword != null)
            {
                args.Add("-upw");
                args.Add(userPassword);
            }
            args.Add(pdfPath);
            args.Add(outPrefix);

            return ReferenceProcess.RenderPng(sw, "pdftocairo", "pdftocairo", args, timeoutMs,
                () => File.Exists(outPath) ? outPath : null);
        }
        finally
        {
            try { File.Delete(outPath); } catch { }
        }
    }
}
