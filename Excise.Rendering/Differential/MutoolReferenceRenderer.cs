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
internal static class MutoolReferenceRenderer
{
    // mutool exits non-zero when invoked without a real command (even --version returns 1), so the
    // probe is "does it launch", not "does it succeed".
    private static readonly Lazy<bool> _available = new(() => ReferenceProcess.IsLaunchable("mutool", 2000, "draw"));

    /// <summary>True when <c>mutool</c> is on PATH and responds to --version.</summary>
    public static bool IsAvailable => _available.Value;

    /// <summary>
    /// The static flags TryRenderPage invokes with (#1385) -- everything
    /// about this call the oracle render cache's key does NOT already cover
    /// via (oracle name, path, page, dpi, password). Keep this literally in
    /// sync with the argument list below: a flag added there and not
    /// here is a stale cache waiting to happen, exactly what #1380 hit.
    /// </summary>
    public const string InvocationSignature = "draw -F png";

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
            var args = new List<string>
            {
                "draw", "-o", outPath, "-F", "png", "-r", dpi.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            if (userPassword != null)
            {
                args.Add("-p");
                args.Add(userPassword);
            }
            args.Add(pdfPath);
            args.Add(pageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));

            return ReferenceProcess.RenderPng(sw, "mutool", "mutool", args, timeoutMs,
                () => File.Exists(outPath) ? outPath : null);
        }
        finally
        {
            try { File.Delete(outPath); } catch { }
        }
    }
}
