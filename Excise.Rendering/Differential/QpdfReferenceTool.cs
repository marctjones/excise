using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Excise.Rendering.Differential;

/// <summary>
/// What <c>qpdf --requires-password</c> reported about a file's password
/// status, mapped 1:1 from its documented exit codes (0/2/3; exit code 1 is
/// unused by qpdf for this flag). See
/// <see cref="QpdfReferenceTool.RequiresPassword"/>.
/// </summary>
/// <summary>
/// One annotation as qpdf's independent parser sees it (#933). Rect is
/// normalized so a comparison never fails merely because the two tools ordered
/// the corners differently.
/// </summary>
public sealed record QpdfAnnotation(
    string Subtype,
    double Left,
    double Bottom,
    double Right,
    double Top,
    int? VertexCount,
    int? InkStrokeCount,
    string? EndLineEnding);

public enum QpdfPasswordStatus
{
    /// <summary>Exit 0: a password, other than as supplied, is required — i.e. the supplied (or absent) password was REJECTED.</summary>
    PasswordRequired = 0,

    /// <summary>Exit 2: the file is not encrypted at all.</summary>
    NotEncrypted = 2,

    /// <summary>Exit 3: the file is encrypted, and the correct password (if any) has been supplied.</summary>
    PasswordCorrect = 3,
}

/// <summary>
/// Shells out to <c>qpdf</c> — an independent, non-excise oracle for PDF
/// structural validity and encryption metadata. Unlike the other tools in
/// this namespace (Ghostscript, mutool, pdfium, pdftocairo), qpdf has no
/// rasterizer: it cannot render a page to pixels, so it complements rather
/// than substitutes for <see cref="GhostscriptReferenceRenderer"/> /
/// <see cref="MutoolReferenceRenderer"/> in redaction/rendering
/// verification. Its value is specific: <c>--show-encryption</c> reports
/// the R/V value, key length, permission bits, and AES variant qpdf's own
/// independent parser found in a file's <c>/Encrypt</c> dictionary, and
/// <c>--check</c>/<c>--decrypt</c> confirm a reader other than excise can
/// actually open and validate what excise wrote — the same no-self-oracle
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
    /// not excise's. Returns null when qpdf is unavailable, the process
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
    /// Page count according to qpdf's own parser (<c>--show-npages</c>).
    /// Returns null when qpdf is unavailable, times out, or the output is not
    /// a bare integer.
    ///
    /// This is the ONLY trustworthy independent page-count signal we have, and
    /// the reason it exists: <c>mutool draw</c> CLAMPS an out-of-range page to
    /// the last page and exits 0 (verified — asking for page 99999 of a 2-page
    /// file renders page 2 and succeeds). So "render pages until one fails" can
    /// never establish how many pages a file has, and any page-organization
    /// test built on that idea silently over-counts. Use this instead.
    /// </summary>
    public static int? PageCount(string pdfPath, string? password = null, int timeoutMs = 15_000)
    {
        var result = Run(BuildArgs("--show-npages", pdfPath, password), timeoutMs);
        if (result == null || result.ExitCode != 0) return null;
        return int.TryParse(result.Output.Trim(), out var n) ? n : null;
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
    /// Runs <c>qpdf --requires-password</c> and maps its documented exit
    /// codes onto <see cref="QpdfPasswordStatus"/>. This — not
    /// <see cref="Check"/> — is the right primitive for asserting a wrong
    /// or missing password is REJECTED: qpdf 12's <c>--check</c> reports a
    /// wrong password as <c>"invalid password"</c> with no
    /// <c>"error:"</c> substring, so <see cref="Check"/>'s warning-tolerant
    /// success heuristic would read the rejection as success (verified
    /// against qpdf 12.3.2 while building the #644 gate).
    /// Returns null when qpdf is unavailable, times out, or exits with a
    /// code outside its documented 0/2/3 set.
    /// </summary>
    public static QpdfPasswordStatus? RequiresPassword(string pdfPath, string? password = null, int timeoutMs = 10_000)
    {
        var result = Run(BuildArgs("--requires-password", pdfPath, password), timeoutMs);
        return result?.ExitCode switch
        {
            0 => QpdfPasswordStatus.PasswordRequired,
            2 => QpdfPasswordStatus.NotEncrypted,
            3 => QpdfPasswordStatus.PasswordCorrect,
            _ => null,
        };
    }

    /// <summary>
    /// Decrypts <paramref name="pdfPath"/> to <paramref name="outputPath"/>
    /// using qpdf's own independent AES/RC4 implementation and key
    /// derivation — a successful decrypt is direct evidence the
    /// <c>/Encrypt</c> dictionary and per-object encryption excise wrote are
    /// spec-correct enough for a reader that isn't excise to derive the same
    /// key and recover the original bytes. Returns false (not an
    /// exception) on any failure so callers can assert on it directly.
    /// </summary>
    /// <param name="uncompressStreams">
    /// Also pass <c>--stream-data=uncompress</c> so every stream in the
    /// output is stored raw. Redaction-leak byte scans need this: without it
    /// a Flate-compressed content stream would hide a leaked secret from a
    /// substring search over the decrypted bytes (#643).
    /// </param>
    public static bool Decrypt(string pdfPath, string outputPath, string? password = null, int timeoutMs = 30_000,
        bool uncompressStreams = false)
    {
        var args = new System.Collections.Generic.List<string> { "--decrypt" };
        if (uncompressStreams) args.Add("--stream-data=uncompress");
        if (!string.IsNullOrEmpty(password)) args.Add($"--password={password}");
        args.Add(pdfPath);
        args.Add(outputPath);

        var result = Run(args.ToArray(), timeoutMs);
        return result?.ExitCode == 0;
    }

    /// <summary>
    /// Uses qpdf's own (non-excise) writer to produce a V=4 R=4 AES-128
    /// encrypted file — the reverse-direction oracle for excise's R=4 reader.
    /// Where every other method in this class asks qpdf to validate
    /// something excise wrote, this asks excise to open something qpdf wrote:
    /// the only check that actually exercises excise's shared key-derivation
    /// helpers (<c>PdfStandardSecurityHandler.DeriveFileKey</c>,
    /// <c>ComputeObjectKey</c>, the R&gt;=3 RC4 chain) against an /O, /U,
    /// and per-object ciphertext excise had zero part in producing — a bug
    /// shared between excise's own encrypt and decrypt implementations of
    /// those helpers would not be caught by excise round-tripping its own
    /// output, but would be caught here. Returns false (not an exception)
    /// on any failure so callers can assert on it directly.
    /// </summary>
    public static bool EncryptR4(
        string inputPath, string outputPath, string userPassword, string ownerPassword, int timeoutMs = 30_000)
    {
        var args = new[]
        {
            "--encrypt", userPassword, ownerPassword, "128", "--use-aes=y", "--",
            inputPath, outputPath
        };
        var result = Run(args, timeoutMs);
        return result?.ExitCode == 0 && System.IO.File.Exists(outputPath);
    }

    /// <summary>
    /// Every annotation qpdf's OWN parser finds in the file, with the fields a
    /// structural check needs (#933).
    ///
    /// THE POINT OF THIS METHOD: everything else that verifies an annotation in
    /// this repo asks excise what excise wrote. That proves the writer and the
    /// reader agree, which they would even if both were wrong in the same way —
    /// they share a codebase. qpdf parses the bytes independently, so agreement
    /// here is evidence about the FILE rather than about excise's internal
    /// consistency. CLAUDE.md's rule, applied to structure instead of pixels:
    /// a tool must not be its own oracle for the property it exists to
    /// guarantee.
    ///
    /// Returns null when qpdf is unavailable, times out, or emits JSON this
    /// cannot read — never an empty list for those cases, because "qpdf found
    /// no annotations" and "qpdf could not be asked" must not look the same to
    /// a caller. An empty list means qpdf genuinely found none.
    /// </summary>
    public static IReadOnlyList<QpdfAnnotation>? ListAnnotations(
        string pdfPath, string? password = null, int timeoutMs = 30_000)
    {
        // --json=1 is required: the "objects" key qpdf exposes the raw object
        // graph through is only valid for JSON version 1. Later versions
        // report pages and acroform but not the annotation dictionaries.
        var args = new List<string> { "--json=1", "--json-key=objects" };
        if (!string.IsNullOrEmpty(password)) args.Add($"--password={password}");
        args.Add(pdfPath);

        var result = Run(args.ToArray(), timeoutMs);
        if (result == null || result.ExitCode != 0) return null;

        try
        {
            using var doc = JsonDocument.Parse(result.Output);
            if (!doc.RootElement.TryGetProperty("objects", out var objects))
                return null;

            var found = new List<QpdfAnnotation>();
            foreach (var entry in objects.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object) continue;
                if (!entry.Value.TryGetProperty("/Subtype", out var subtypeEl)) continue;

                // /Type is absent on some conforming annotations, so key off
                // the presence of /Rect + /Subtype rather than requiring it.
                if (!entry.Value.TryGetProperty("/Rect", out var rectEl)) continue;
                if (rectEl.ValueKind != JsonValueKind.Array || rectEl.GetArrayLength() < 4) continue;

                var subtype = (subtypeEl.GetString() ?? "").TrimStart('/');
                if (subtype.Length == 0) continue;

                var r = new double[4];
                var ok = true;
                for (var i = 0; i < 4; i++)
                {
                    if (!rectEl[i].TryGetDouble(out r[i])) { ok = false; break; }
                }
                if (!ok) continue;

                int? vertexCount = null;
                if (entry.Value.TryGetProperty("/Vertices", out var vEl) &&
                    vEl.ValueKind == JsonValueKind.Array)
                {
                    vertexCount = vEl.GetArrayLength() / 2;
                }

                int? inkStrokeCount = null;
                if (entry.Value.TryGetProperty("/InkList", out var iEl) &&
                    iEl.ValueKind == JsonValueKind.Array)
                {
                    inkStrokeCount = iEl.GetArrayLength();
                }

                string? lineEnding = null;
                if (entry.Value.TryGetProperty("/LE", out var leEl) &&
                    leEl.ValueKind == JsonValueKind.Array && leEl.GetArrayLength() >= 2)
                {
                    lineEnding = (leEl[1].GetString() ?? "").TrimStart('/');
                }

                found.Add(new QpdfAnnotation(
                    subtype,
                    Math.Min(r[0], r[2]), Math.Min(r[1], r[3]),
                    Math.Max(r[0], r[2]), Math.Max(r[1], r[3]),
                    vertexCount, inkStrokeCount, lineEnding));
            }

            return found;
        }
        catch (JsonException)
        {
            return null;
        }
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
