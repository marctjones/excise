using System;
using System.Diagnostics;
using System.IO;
using SkiaSharp;

namespace Excise.Rendering.Differential;

/// <summary>
/// Shells out to <c>pdftoppm</c> (Poppler) to render a page. Poppler is
/// already represented in this namespace by
/// <see cref="PdftocairoReferenceRenderer"/> (the Cairo backend); pdftoppm
/// uses Poppler's Splash backend and, more importantly for the encryption
/// interop gate (#644), exposes BOTH password authorities on its command
/// line: <c>-upw</c> (user password) and <c>-opw</c> (owner password).
/// That makes it the one subprocess oracle here that can directly answer
/// "does the OWNER password open this file?" for a renderer, not just for
/// qpdf's metadata parser.
///
/// Poppler is GPL; invoked only as a CLI subprocess (never linked), the
/// same licensing posture as mutool/pdftocairo documented in
/// <see cref="PdftocairoReferenceRenderer"/> and LICENSES.md.
///
/// Returns null on missing tool, timeout, non-zero exit (pdftoppm exits 1
/// with "Command Line Error: Incorrect password" on a wrong or missing
/// password — verified against Poppler 26.06.0), or decode failure — same
/// contract as every other renderer in this namespace, so tests can treat
/// "no answer" as data.
/// </summary>
internal static class PdftoppmReferenceRenderer
{
    private static readonly Lazy<bool> _available = new(() => ReferenceProcess.IsLaunchable("pdftoppm", 2000, "-v"));

    /// <summary>True when the <c>pdftoppm</c> CLI is launchable on PATH.</summary>
    public static bool IsAvailable => _available.Value;

    /// <summary>
    /// Render <paramref name="pageNumber"/> (1-based) at <paramref name="dpi"/>
    /// via <c>pdftoppm -png -singlefile</c>. Returns null on any failure.
    /// </summary>
    public static SKBitmap? RenderPage(string pdfPath, int pageNumber, int dpi, int timeoutMs = 30_000)
        => TryRenderPage(pdfPath, pageNumber, dpi, timeoutMs).Bitmap;

    /// <summary>
    /// Render with explicit password authority: <paramref name="userPassword"/>
    /// maps to <c>-upw</c>, <paramref name="ownerPassword"/> to <c>-opw</c>.
    /// Either may be null to omit that flag. Returns null on any failure,
    /// including password rejection.
    /// </summary>
    public static SKBitmap? RenderPage(
        string pdfPath, int pageNumber, int dpi, int timeoutMs, string? userPassword, string? ownerPassword = null)
        => TryRenderPage(pdfPath, pageNumber, dpi, timeoutMs, userPassword, ownerPassword).Bitmap;

    public static ReferenceRenderResult TryRenderPage(string pdfPath, int pageNumber, int dpi, int timeoutMs = 30_000)
        => TryRenderPage(pdfPath, pageNumber, dpi, timeoutMs, userPassword: null, ownerPassword: null);

    public static ReferenceRenderResult TryRenderPage(
        string pdfPath,
        int pageNumber,
        int dpi,
        int timeoutMs,
        string? userPassword,
        string? ownerPassword = null)
    {
        var sw = Stopwatch.StartNew();
        if (!IsAvailable)
            return new ReferenceRenderResult(null, "TOOL_UNAVAILABLE", "pdftoppm is not on PATH", sw.ElapsedMilliseconds);

        // -singlefile emits exactly <prefix>.png with no page-number suffix —
        // same predictable-output choice PdftocairoReferenceRenderer makes.
        var outPrefix = Path.Combine(Path.GetTempPath(),
            $"excise-pdftoppm-ref-{Guid.NewGuid():N}");
        var outPath = outPrefix + ".png";

        try
        {
            var args = new List<string>
            {
                "-png", "-singlefile",
                "-r", dpi.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-f", pageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-l", pageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            if (userPassword != null)
            {
                args.Add("-upw");
                args.Add(userPassword);
            }
            if (ownerPassword != null)
            {
                args.Add("-opw");
                args.Add(ownerPassword);
            }
            args.Add(pdfPath);
            args.Add(outPrefix);

            return ReferenceProcess.RenderPng(sw, "pdftoppm", "pdftoppm", args, timeoutMs,
                () => File.Exists(outPath) ? outPath : null);
        }
        finally
        {
            try { File.Delete(outPath); } catch { }
        }
    }
}
