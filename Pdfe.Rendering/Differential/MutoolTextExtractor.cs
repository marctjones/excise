using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace Pdfe.Rendering.Differential;

/// <summary>
/// Shells out to <c>mutool draw -F txt</c> to extract PDF page text — an
/// independent oracle for pdfe's own extraction. Only ever invoked as a
/// subprocess (never linked), matching the AGPL posture documented for
/// <see cref="MutoolReferenceRenderer"/> and in the repo's LICENSES.md.
///
/// Never throws: returns null when mutool isn't available or refuses
/// (timeout / non-zero exit / missing output), matching
/// <see cref="MutoolReferenceRenderer"/>'s convention so callers can treat
/// "no answer" as data, not an exception to catch.
/// </summary>
public static class MutoolTextExtractor
{
    /// <summary>Extract text from a single page (1-based). Returns null when mutool isn't available or refuses.</summary>
    public static string? ExtractPage(string pdfPath, int pageNumber, int timeoutMs = 30_000)
        => ExtractRange(pdfPath, pageNumber.ToString(CultureInfo.InvariantCulture), timeoutMs);

    /// <summary>
    /// Extract every page in one mutool invocation instead of one process
    /// per page — measured ~9.6s vs. ~35s sequential (and ~14s even with
    /// 24-way parallel per-page calls) on a 126-page real-world document.
    /// Per-process spawn overhead, not compute, dominates at that page
    /// count, so one process for the whole document beats parallelizing
    /// many small ones. Returns null when mutool isn't available or
    /// refuses, matching <see cref="ExtractPage"/>. The result has exactly
    /// <paramref name="pageCount"/> entries (index 0 = page 1), even if
    /// mutool emitted warnings for individual pages along the way.
    /// </summary>
    public static string[]? ExtractAllPages(string pdfPath, int pageCount, int timeoutMs = 120_000)
    {
        if (pageCount <= 0) return Array.Empty<string>();

        var combined = ExtractRange(pdfPath, $"1-{pageCount.ToString(CultureInfo.InvariantCulture)}", timeoutMs);
        if (combined == null) return null;

        // mutool separates pages with a form-feed (0x0c); splitting yields
        // pageCount + 1 parts (trailing part after the last page's
        // form-feed is empty/whitespace) — verified directly against a
        // real 126-page fixture.
        var parts = combined.Split('\f');
        var pages = new string[pageCount];
        for (int i = 0; i < pageCount; i++)
            pages[i] = i < parts.Length ? parts[i] : "";
        return pages;
    }

    private static string? ExtractRange(string pdfPath, string pageSpec, int timeoutMs)
    {
        if (!MutoolReferenceRenderer.IsAvailable) return null;

        var outPath = Path.Combine(Path.GetTempPath(),
            $"pdfe-mutool-text-{Guid.NewGuid():N}.txt");
        try
        {
            var psi = new ProcessStartInfo("mutool")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            // -F txt makes mutool emit plain UTF-8 text (extracted by its
            // own text-merge engine, which is independent of pdfe).
            psi.ArgumentList.Add("draw");
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add(outPath);
            psi.ArgumentList.Add("-F");
            psi.ArgumentList.Add("txt");
            psi.ArgumentList.Add(pdfPath);
            psi.ArgumentList.Add(pageSpec);

            using var p = Process.Start(psi);
            if (p == null) return null;
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            if (p.ExitCode != 0) return null;
            if (!File.Exists(outPath)) return null;

            return File.ReadAllText(outPath);
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
