using System;
using System.Diagnostics;
using System.Text;

namespace Pdfe.Rendering.Differential;

/// <summary>
/// Shells out to <c>qpdf</c> — an independent, non-pdfe oracle for PDF
/// structural validity and encryption metadata. Unlike the other tools in
/// this namespace (Ghostscript, mutool, pdfium, pdftocairo), qpdf has no
/// rasterizer: it cannot render a page to pixels, so it complements rather
/// than substitutes for <see cref="GhostscriptReferenceRenderer"/> /
/// <see cref="MutoolReferenceRenderer"/> in redaction/rendering
/// verification. Its value is specific: <c>--show-encryption</c> reports
/// the R/V value, key length, permission bits, and AES variant qpdf's own
/// independent parser found in a file's <c>/Encrypt</c> dictionary, and
/// <c>--check</c>/<c>--decrypt</c> confirm a reader other than pdfe can
/// actually open and validate what pdfe wrote — the same no-self-oracle
/// principle CLAUDE.md documents for redaction, applied to encryption.
///
/// Apache-2.0 licensed; invoked only as a subprocess (never linked), same
/// posture as the AGPL-licensed mutool CLI documented in
/// <see cref="MutoolReferenceRenderer"/>.
///
/// All methods return null (or false, for boolean queries) when qpdf isn't
/// available, so tests can degrade to Skipped rather than fail in
/// environments without it — matching every other tool in this namespace.
/// </summary>
public static class QpdfReferenceTool
{
    private static readonly Lazy<bool> _available = new(() =>
    {
        try
        {
            var psi = new ProcessStartInfo("qpdf", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    });

    /// <summary>True when the <c>qpdf</c> CLI is launchable on PATH.</summary>
    public static bool IsAvailable => _available.Value;

    /// <summary>
    /// Runs <c>qpdf --show-encryption</c> and returns its raw stdout: R/V,
    /// key length, permission bits (allowed/disallowed per capability),
    /// and stream/string/file encryption method (e.g. AESv3), all read by
    /// qpdf's own independent parser of the <c>/Encrypt</c> dictionary —
    /// not pdfe's. Returns null when qpdf is unavailable, the process
    /// doesn't exit within <paramref name="timeoutMs"/>, or it can't open
    /// the file at all (a wrong password on a file that still HAS a
    /// non-empty user password produces "Incorrect password supplied" on
    /// stderr but qpdf still reports what it can from the dictionary
    /// itself — that partial info is still returned, not treated as
    /// failure, since it's independently useful. Pass the correct
    /// password when you need qpdf to fully open the file rather than
    /// just parse the dictionary).
    /// </summary>
    public static string? ShowEncryption(string pdfPath, string? password = null, int timeoutMs = 15_000)
        => Run(BuildArgs("--show-encryption", pdfPath, password), timeoutMs)?.Output;

    /// <summary>
    /// Runs <c>qpdf --check</c> (structural validity, including
    /// encryption-aware checks) and returns (success, combined output).
    /// <paramref name="password"/> is required to fully check an
    /// encrypted file's cross-reference/object streams; without it qpdf
    /// can only confirm the file parses as encrypted.
    /// </summary>
    public static (bool Success, string Output)? Check(string pdfPath, string? password = null, int timeoutMs = 30_000)
    {
        var result = Run(BuildArgs("--check", pdfPath, password), timeoutMs);
        if (result == null) return null;
        // qpdf --check exits non-zero on warnings too, not just hard
        // errors — treat "no error-looking line" as the practical signal,
        // matching how callers actually want to use this (a stray warning
        // about, say, a non-standard xref stream shouldn't read as
        // "encryption is broken").
        var success = result.ExitCode == 0 || !result.Output.Contains("error:", StringComparison.OrdinalIgnoreCase);
        return (success, result.Output);
    }

    /// <summary>
    /// Silently tests whether qpdf's independent parser considers the file
    /// encrypted at all (<c>--is-encrypted</c>, exit code only — no
    /// stdout to parse). Returns null when qpdf is unavailable.
    /// </summary>
    public static bool? IsEncrypted(string pdfPath, int timeoutMs = 10_000)
    {
        var result = Run(new[] { "--is-encrypted", pdfPath }, timeoutMs);
        return result?.ExitCode == 0;
    }

    /// <summary>
    /// Decrypts <paramref name="pdfPath"/> to <paramref name="outputPath"/>
    /// using qpdf's own independent AES/RC4 implementation and key
    /// derivation — a successful decrypt is direct evidence the
    /// <c>/Encrypt</c> dictionary and per-object encryption pdfe wrote are
    /// spec-correct enough for a reader that isn't pdfe to derive the same
    /// key and recover the original bytes. Returns false (not an
    /// exception) on any failure so callers can assert on it directly.
    /// </summary>
    public static bool Decrypt(string pdfPath, string outputPath, string? password = null, int timeoutMs = 30_000)
    {
        var args = new System.Collections.Generic.List<string> { "--decrypt" };
        if (!string.IsNullOrEmpty(password)) args.Add($"--password={password}");
        args.Add(pdfPath);
        args.Add(outputPath);

        var result = Run(args.ToArray(), timeoutMs);
        return result?.ExitCode == 0;
    }

    private static string[] BuildArgs(string command, string pdfPath, string? password)
    {
        var args = new System.Collections.Generic.List<string> { command };
        if (!string.IsNullOrEmpty(password)) args.Add($"--password={password}");
        args.Add(pdfPath);
        return args.ToArray();
    }

    private sealed record ProcessResult(int ExitCode, string Output);

    private static ProcessResult? Run(string[] args, int timeoutMs)
    {
        if (!IsAvailable) return null;

        try
        {
            var psi = new ProcessStartInfo("qpdf")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p == null) return null;

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            p.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            // WaitForExit(int) returning true only means the process itself
            // exited — it does NOT guarantee the async OutputDataReceived/
            // ErrorDataReceived callbacks have finished delivering already-
            // buffered lines (a well-known .NET Process race). Without this,
            // stdout/stderr below can be read before qpdf's final lines have
            // been appended, silently truncating (sometimes to empty)
            // output that was actually produced. The parameterless overload
            // blocks until the redirected-stream pump threads complete.
            p.WaitForExit();

            // qpdf writes --show-encryption's actual info to stdout and
            // warnings ("Incorrect password supplied") to stderr — combine
            // so callers see both without having to know which stream
            // qpdf chose for a given message.
            var combined = stdout.ToString();
            if (stderr.Length > 0) combined += stderr.ToString();

            return new ProcessResult(p.ExitCode, combined);
        }
        catch
        {
            return null;
        }
    }
}
