using System;
using System.Diagnostics;
using System.IO;
using SkiaSharp;

namespace Excise.Rendering.Differential;

/// <summary>
/// Shells out to Ghostscript to render a page, providing a third
/// independent reference renderer alongside <c>mutool draw</c> and
/// <c>pdftocairo</c>.
///
/// Why Ghostscript: when MuPDF and Poppler do not settle a page, we
/// need a third oracle before classifying the page as a excise bug or a
/// reference disagreement. Ghostscript is a long-lived PDF renderer and
/// gives us a practical third vote without pulling any rendering code
/// into excise itself.
///
/// Returns null on missing tool, timeout, non-zero exit, or decode
/// failure.
///
/// <para>⚠️ <b>Ghostscript renders for PAPER by default here, not for a
/// screen</b> (#1573). With a file output device (<c>png16m</c>) gs behaves as
/// <c>-dPrinted=true</c>, so an annotation's <c>/F</c> flags are judged by
/// §12.5.3's PRINT rule: nothing without the Print flag is drawn, and a
/// NoView+Print annotation IS. Measured 2026-09-17 on a four-annotation fixture
/// (no <c>/F</c>, NoView|Print, Print, Hidden|Print): gs default and
/// <c>-dPrinted=true</c> agree pixel-for-pixel, and <c>-dPrinted=false</c>
/// inverts the first two. Use <see cref="TryRenderPageForViewIntent"/> when the
/// excise side of a comparison is a VIEWER raster — ⚠️ the existing gs
/// differentials and the corpus scan's gs escalation do NOT do that yet, so
/// they compare a view raster against a print one on any annotated page whose
/// flags differ between the rules. #1611 tracks that sweep; changing the
/// DEFAULT instead of the callers would invalidate every cached oracle render
/// (<see cref="InvocationSignature"/> is the cache key).</para>
/// </summary>
internal static class GhostscriptReferenceRenderer
{
    private static readonly Lazy<string?> _commandName = new(() =>
    {
        var explicitCommand = Environment.GetEnvironmentVariable("EXCISE_GHOSTSCRIPT_COMMAND");
        var candidates = string.IsNullOrWhiteSpace(explicitCommand)
            ? new[] { "ghostpdf", "gpdf", "gs", "gswin64c", "gswin32c" }
            : new[] { explicitCommand };

        return candidates.FirstOrDefault(candidate => ReferenceProcess.IsLaunchable(candidate, 2000, "--version"));
    });

    public static bool IsAvailable => _commandName.Value != null;

    /// <summary>
    /// The static flags TryRenderPage invokes with (#1385) -- see
    /// MutoolReferenceRenderer for why this must stay in sync with the
    /// argument list below. Parameterized on the one flag that
    /// actually varies per call.
    /// </summary>
    public static string InvocationSignature(bool overprintSimulate, bool viewIntent = false) =>
        "-dBATCH -dNOPAUSE -dSAFER -dQUIET -sDEVICE=png16m -dUseCropBox " +
        "-dTextAlphaBits=4 -dGraphicsAlphaBits=4"
        + (overprintSimulate ? " -dOverprint=/simulate" : "")
        + (viewIntent ? " -dPrinted=false" : "");

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
        => TryRenderPage(pdfPath, pageNumber, dpi, timeoutMs, userPassword,
            overprintSimulate: false, viewIntent: false);

    /// <summary>
    /// Same as <see cref="TryRenderPage(string,int,int,int,string?)"/> but with
    /// <c>-dOverprint=/simulate</c> (Ghostscript ≥ 9.54), which makes Ghostscript
    /// simulate PDF overprint (/OP, /op, /OPM — ISO 32000-1 §8.6.7) on the RGB
    /// output device. This is the only reference renderer in the differential
    /// harness that simulates overprint in RGB output at all (mutool and
    /// pdftocairo only apply overprint when rendering to CMYK/spot targets), so
    /// it is the spec oracle for excise's overprint work (#634). Older
    /// Ghostscript versions reject the flag; callers get an EXIT_CODE failure
    /// and should skip.
    /// </summary>
    public static ReferenceRenderResult TryRenderPageWithOverprintSimulation(
        string pdfPath,
        int pageNumber,
        int dpi,
        int timeoutMs = 30_000)
        => TryRenderPage(pdfPath, pageNumber, dpi, timeoutMs, userPassword: null,
            overprintSimulate: true, viewIntent: false);

    /// <summary>
    /// Same, with <c>-dPrinted=false</c>, which makes Ghostscript judge an
    /// annotation's <c>/F</c> flags by §12.5.3's VIEW rule instead of its print
    /// rule (#1573) — Hidden and NoView suppressed, the Print flag irrelevant.
    /// </summary>
    /// <remarks>
    /// This is the variant to compare a excise VIEWER raster against. Without
    /// it gs is a print oracle (see the class remarks), so a fixture carrying a
    /// no-Print or NoView annotation puts a view raster against a print one and
    /// the disagreement is the harness's, not excise's.
    /// </remarks>
    public static ReferenceRenderResult TryRenderPageForViewIntent(
        string pdfPath,
        int pageNumber,
        int dpi,
        int timeoutMs = 30_000)
        => TryRenderPage(pdfPath, pageNumber, dpi, timeoutMs, userPassword: null,
            overprintSimulate: false, viewIntent: true);

    private static ReferenceRenderResult TryRenderPage(
        string pdfPath,
        int pageNumber,
        int dpi,
        int timeoutMs,
        string? userPassword,
        bool overprintSimulate,
        bool viewIntent)
    {
        var sw = Stopwatch.StartNew();
        var command = _commandName.Value;
        if (command == null)
            return new ReferenceRenderResult(null, "TOOL_UNAVAILABLE",
                "Ghostscript is not on PATH; set EXCISE_GHOSTSCRIPT_COMMAND or install gs/ghostpdf",
                sw.ElapsedMilliseconds);

        var outPath = Path.Combine(Path.GetTempPath(),
            $"excise-ghostscript-ref-{Guid.NewGuid():N}.png");

        try
        {
            var args = new List<string>
            {
                "-dBATCH", "-dNOPAUSE", "-dSAFER", "-dQUIET", "-sDEVICE=png16m",
                // #1380 — render the CropBox, not the MediaBox. Ghostscript, like
                // pdftocairo, defaults to the MediaBox; see the note in
                // PdftocairoReferenceRenderer for the measurement and the §7.7.3.3 basis.
                "-dUseCropBox",
                "-dTextAlphaBits=4", "-dGraphicsAlphaBits=4",
            };
            if (overprintSimulate)
                args.Add("-dOverprint=/simulate");
            // #1573: only the VIEW-intent variant passes anything. gs with a
            // file output device already behaves as -dPrinted=true, so the
            // default invocation (and its cached signature) is unchanged.
            if (viewIntent)
                args.Add("-dPrinted=false");
            args.Add($"-r{dpi.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            args.Add($"-dFirstPage={pageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            args.Add($"-dLastPage={pageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            if (userPassword != null)
                args.Add($"-sPDFPassword={userPassword}");
            args.Add($"-sOutputFile={outPath}");
            args.Add(pdfPath);

            return ReferenceProcess.RenderPng(sw, command, command, args, timeoutMs,
                () => File.Exists(outPath) ? outPath : null);
        }
        finally
        {
            try { File.Delete(outPath); } catch { }
        }
    }
}
