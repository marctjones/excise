using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Excise.Rendering.Differential;

/// <summary>
/// #1042 — a SECOND reference redactor, genuinely independent of the MuPDF one.
/// <see cref="MutoolReferenceRedactor"/> shares MuPDF's blind spots (on #1040's
/// document MuPDF's own search found 0 hits for text its extractor plainly
/// shows); PyMuPDF would be the same C library and add zero independence.
/// PDFBox is a different language and a different engine — two agreeing
/// implementations are signal, one is an opinion.
///
/// <para>PDFBox ships no redaction command, so <c>scripts/PdfBoxRedactor.java</c>
/// is a small driver run as a single-file source launch (never linked): it
/// tokenises each page, decodes every show-text operator through the current
/// font, and drops the whole operator when its text contains the term.
/// Whole-operator removal is COARSER than excise's glyph-level path — which is
/// the point: the comparison assertion is one-directional, <b>excise may remove
/// LESS collateral than this reference, never more</b>. "excise must match
/// PDFBox" would elect a renderer, exactly what #1015/#932 forbid.</para>
///
/// <para>A reference that finds 0 hits for a term an independent extractor can
/// see is a BROKEN RUN, never a clean baseline — so <see cref="ReferenceRedactionResult.HitsFound"/>
/// is returned, not hidden (the #1041 lesson).</para>
/// </summary>
public static class PdfBoxReferenceRedactor
{
    public static bool IsAvailable =>
        ResolveJava() != null && FindJar() != null && FindDriver() != null;

    /// <summary>
    /// Redact every occurrence of <paramref name="term"/> from
    /// <paramref name="inputPath"/> into <paramref name="outputPath"/> with
    /// PDFBox. Never throws — a failure is returned as data.
    /// </summary>
    public static ReferenceRedactionResult Redact(
        string inputPath, string term, string outputPath, int timeoutMs = 120_000)
    {
        var java = ResolveJava();
        var jar = FindJar();
        var driver = FindDriver();
        if (java == null) return new ReferenceRedactionResult(0, "no working java found (see #1009)");
        if (jar == null) return new ReferenceRedactionResult(0, "pdfbox jar not found (scripts/download-pdfbox.sh)");
        if (driver == null) return new ReferenceRedactionResult(0, "scripts/PdfBoxRedactor.java not found");

        try
        {
            var psi = new ProcessStartInfo(java)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--class-path");
            psi.ArgumentList.Add(jar);
            psi.ArgumentList.Add(driver);
            psi.ArgumentList.Add(inputPath);
            psi.ArgumentList.Add(outputPath);
            psi.ArgumentList.Add(term);

            using var proc = Process.Start(psi);
            if (proc == null) return new ReferenceRedactionResult(0, "could not start java");

            var outT = proc.StandardOutput.ReadToEndAsync();
            var errT = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(timeoutMs))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return new ReferenceRedactionResult(0, $"pdfbox driver timed out after {timeoutMs}ms");
            }

            var stdout = outT.GetAwaiter().GetResult();
            var stderr = errT.GetAwaiter().GetResult();

            if (proc.ExitCode != 0)
                return new ReferenceRedactionResult(0, $"java exited {proc.ExitCode}: {stderr.Trim()}");

            // The driver prints the hit count as the last integer on stdout
            // (PDFBox logging noise may precede it on stderr, not stdout).
            var m = Regex.Matches(stdout, @"\b(\d+)\b").Cast<Match>().LastOrDefault();
            if (m == null)
                return new ReferenceRedactionResult(0, $"pdfbox driver produced no hit count. stdout: {stdout.Trim()}");
            if (!File.Exists(outputPath))
                return new ReferenceRedactionResult(0, "pdfbox driver wrote no output file");

            return new ReferenceRedactionResult(
                int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new ReferenceRedactionResult(0, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string? ResolveJava()
    {
        var explicitJava = Environment.GetEnvironmentVariable("EXCISE_JAVA_COMMAND");
        var candidates = string.IsNullOrWhiteSpace(explicitJava)
            ? new[] { "/opt/homebrew/opt/openjdk/bin/java", "java" }
            : new[] { explicitJava };
        foreach (var c in candidates)
        {
            try
            {
                // #1009: /usr/bin/java is a macOS stub that "works" but is not a
                // JRE. Confirm a real version banner before trusting it.
                var psi = new ProcessStartInfo(c, "-version")
                {
                    RedirectStandardError = true, RedirectStandardOutput = true,
                    UseShellExecute = false, CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p == null) continue;
                var banner = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
                p.WaitForExit(10_000);
                if (p.ExitCode == 0 && Regex.IsMatch(banner, "openjdk|java version", RegexOptions.IgnoreCase))
                    return c;
            }
            catch { /* try next */ }
        }
        return null;
    }

    private static string? FindJar()
    {
        var env = Environment.GetEnvironmentVariable("EXCISE_PDFBOX_JAR");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
        var vendor = FindUp("tools/vendor");
        if (vendor == null) return null;
        return Directory.EnumerateFiles(vendor, "pdfbox-app-*.jar", SearchOption.TopDirectoryOnly)
            .OrderByDescending(f => f, StringComparer.Ordinal).FirstOrDefault();
    }

    private static string? FindDriver() => FindUp("scripts/PdfBoxRedactor.java");

    /// <summary>Walk up from the assembly location to find a repo-relative path.</summary>
    private static string? FindUp(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate) || Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
