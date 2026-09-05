using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace Excise.Rendering.Differential;

/// <summary>
/// Shells out to Poppler's <c>pdftotext</c> — a SECOND independent text oracle,
/// on a different engine from <see cref="MutoolTextExtractor"/>.
///
/// <para><b>Why a second extractor exists.</b> The redaction bench's security
/// verdict is "can any independent tool still read the term". Until this class
/// that question had exactly one answer, mutool's, and mutool is MuPDF — the
/// same engine as PyMuPDF, one of the redactors the bench measures. MuPDF
/// grading MuPDF's own redaction shares its blind spots, which is the
/// self-oracle failure the project's rule exists to prevent, one step removed.
/// Poppler is a different codebase, a different text-merge implementation and a
/// different font stack, so the two disagree where one is blind.</para>
///
/// <para>A term is treated as leaked when EITHER extractor can read it. That is
/// deliberately the conservative direction: for a security property, two
/// oracles must agree that the text is GONE, not merely one.</para>
///
/// <para>Only ever invoked as a subprocess (never linked), matching the posture
/// documented for the other reference tools and in LICENSES.md. Poppler is
/// GPL; excise ships none of it.</para>
///
/// <para>Never throws: returns null when pdftotext isn't available or refuses
/// (timeout / non-zero exit / missing output), matching
/// <see cref="MutoolTextExtractor"/>'s convention, so callers treat "no answer"
/// as data rather than an exception.</para>
/// </summary>
public static class PdftotextTextExtractor
{
    private static readonly Lazy<bool> Available = new(() =>
    {
        try
        {
            var psi = new ProcessStartInfo("pdftotext")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-v");
            using var p = Process.Start(psi);
            if (p == null) return false;
            if (!p.WaitForExit(10_000)) { try { p.Kill(entireProcessTree: true); } catch { } return false; }
            // pdftotext -v prints its banner to stderr and exits non-zero on
            // some builds; the banner is the signal, not the exit code.
            return true;
        }
        catch
        {
            return false;
        }
    });

    public static bool IsAvailable => Available.Value;

    /// <summary>Extract text from a single page (1-based). Null when pdftotext isn't available or refuses.</summary>
    public static string? ExtractPage(string pdfPath, int pageNumber, int timeoutMs = 30_000)
        => ExtractRange(pdfPath, pageNumber, pageNumber, timeoutMs, password: null);

    /// <summary>
    /// Extract a single page of a password-protected PDF using Poppler's own
    /// decryption (<c>-upw</c>), not excise's.
    /// </summary>
    public static string? ExtractPage(string pdfPath, int pageNumber, string? password, int timeoutMs = 30_000)
        => ExtractRange(pdfPath, pageNumber, pageNumber, timeoutMs, password);

    /// <summary>
    /// Every page in one invocation, for the same reason
    /// <see cref="MutoolTextExtractor.ExtractAllPages"/> does it: process spawn
    /// dominates at real page counts. Result has exactly
    /// <paramref name="pageCount"/> entries (index 0 = page 1).
    /// </summary>
    public static string[]? ExtractAllPages(string pdfPath, int pageCount, int timeoutMs = 120_000)
    {
        if (pageCount <= 0) return Array.Empty<string>();

        var combined = ExtractRange(pdfPath, 1, pageCount, timeoutMs, password: null);
        if (combined == null) return null;

        // pdftotext separates pages with a form-feed (0x0c), same as mutool.
        var parts = combined.Split('\f');
        var pages = new string[pageCount];
        for (int i = 0; i < pageCount; i++)
            pages[i] = i < parts.Length ? parts[i] : "";
        return pages;
    }

    private static string? ExtractRange(string pdfPath, int firstPage, int lastPage, int timeoutMs, string? password)
    {
        if (!IsAvailable) return null;

        var outPath = Path.Combine(Path.GetTempPath(),
            $"excise-pdftotext-{Guid.NewGuid():N}.txt");
        try
        {
            var psi = new ProcessStartInfo("pdftotext")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            if (!string.IsNullOrEmpty(password))
            {
                psi.ArgumentList.Add("-upw");
                psi.ArgumentList.Add(password);
            }
            if (firstPage > 0)
            {
                psi.ArgumentList.Add("-f");
                psi.ArgumentList.Add(firstPage.ToString(CultureInfo.InvariantCulture));
                psi.ArgumentList.Add("-l");
                psi.ArgumentList.Add(lastPage.ToString(CultureInfo.InvariantCulture));
            }
            psi.ArgumentList.Add(pdfPath);
            psi.ArgumentList.Add(outPath);

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
