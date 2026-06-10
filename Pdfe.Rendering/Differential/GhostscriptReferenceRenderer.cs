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
internal static class GhostscriptReferenceRenderer
{
    private static readonly Lazy<string?> _commandName = new(() =>
    {
        foreach (var candidate in new[] { "gs", "gswin64c", "gswin32c" })
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
    {
        var command = _commandName.Value;
        if (command == null)
            return null;

        var outPath = Path.Combine(Path.GetTempPath(),
            $"pdfe-ghostscript-ref-{Guid.NewGuid():N}.png");

        try
        {
            var psi = new ProcessStartInfo(command,
                $"-dBATCH -dNOPAUSE -dSAFER -dQUIET -sDEVICE=png16m " +
                $"-dTextAlphaBits=4 -dGraphicsAlphaBits=4 -r{dpi} " +
                $"-dFirstPage={pageNumber} -dLastPage={pageNumber} " +
                $"-sOutputFile=\"{outPath}\" \"{pdfPath}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            if (p.ExitCode != 0) return null;
            if (!File.Exists(outPath)) return null;

            return SKBitmap.Decode(outPath);
        }
        catch
        {
            return null;
        }
        finally
        {
            try { File.Delete(outPath); } catch { }
        }
    }
}
