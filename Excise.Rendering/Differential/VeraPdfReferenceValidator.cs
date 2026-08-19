using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace Excise.Rendering.Differential;

/// <summary>
/// Outcome of an external validation. <paramref name="Flavour"/> is the profile
/// veraPDF DETECTED from the file's own XMP (<c>4</c>, <c>2b</c>, <c>1b</c>…),
/// not one we asked for — a change in it between input and output is itself a
/// finding.
/// </summary>
public sealed record VeraPdfResult(bool Passed, string Flavour, string? Failure)
{
    public bool Ran => Failure == null;
}

/// <summary>
/// Validates a PDF against PDF/A and PDF/UA using <b>veraPDF</b> — the PDF
/// Association's reference validator, and the first oracle here that judges
/// excise's OUTPUT rather than its rendering.
///
/// <para><b>What this can and cannot answer.</b> There is no such thing as
/// validating "PDF 2.0 conformance": ISO 32000-2 is a format specification, not
/// a conformance profile, and no validator exists for it. The conformance
/// profiles are PDF/A and PDF/UA, and <b>PDF/A-4 is built on PDF 2.0</b> — so
/// veraPDF validating a file as PDF/A-4 is the closest externally-checkable
/// statement about PDF 2.0 correctness available. That is a narrower claim than
/// "excise is PDF 2.0 conformant", and it is the one worth making because
/// somebody other than excise is making it.</para>
///
/// <para><b>Why it is worth the JVM startup.</b> On its first real use it found
/// #1056: <c>excise merge</c> silently strips XMP <c>pdfaid</c>, so a valid
/// PDF/A-4 document came out conforming to nothing. Nothing else in this repo
/// noticed — the file opens, renders identically, and passes
/// <c>qpdf --check</c>.</para>
///
/// <para>Invoked only as a subprocess, never linked (veraPDF is GPL/MPL).
/// Never throws: a failure is returned as data, matching the other reference
/// tools.</para>
/// </summary>
public static class VeraPdfReferenceValidator
{
    private static readonly Lazy<bool> _available = new(() =>
    {
        try
        {
            var psi = new ProcessStartInfo("verapdf")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--version");
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            return p.WaitForExit(20_000) && p.ExitCode == 0;
        }
        catch { return false; }
    });

    public static bool IsAvailable => _available.Value;

    /// <summary>
    /// Validate <paramref name="pdfPath"/>, letting veraPDF auto-detect the
    /// flavour from the file's metadata. Returns null when veraPDF is absent.
    /// </summary>
    public static VeraPdfResult? Validate(string pdfPath, int timeoutMs = 120_000)
    {
        if (!IsAvailable) return null;

        try
        {
            var psi = new ProcessStartInfo("verapdf")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--format");
            psi.ArgumentList.Add("text");
            psi.ArgumentList.Add(pdfPath);

            using var proc = Process.Start(psi);
            if (proc == null) return new VeraPdfResult(false, "", "could not start verapdf");

            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(timeoutMs))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return new VeraPdfResult(false, "", $"verapdf timed out after {timeoutMs}ms");
            }

            // The JVM prints reflection warnings to stdout on some builds; the
            // verdict line is "PASS <path> <flavour>" or "FAIL <path> <flavour>".
            foreach (var line in stdout.Split('\n'))
            {
                var m = Regex.Match(line.Trim(), @"^(PASS|FAIL)\s+.*\s+(\S+)$");
                if (!m.Success) continue;
                return new VeraPdfResult(m.Groups[1].Value == "PASS", m.Groups[2].Value, null);
            }

            return new VeraPdfResult(false, "", $"no verdict line. stderr: {stderr.Trim()}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new VeraPdfResult(false, "", $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
