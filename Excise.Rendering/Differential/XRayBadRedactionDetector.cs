using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Excise.Rendering.Differential;

/// <summary>
/// Shells out to the Free Law Project's <c>x-ray</c> to detect BAD REDACTIONS —
/// text that is still selectable underneath an opaque rectangle.
///
/// <para><b>Why this oracle and not another.</b> The existing oracles
/// (mutool, poppler, PDFBox, Ghostscript) answer "what text is in this file".
/// x-ray answers a different question by a genuinely different method: find
/// rectangles, find letters colocated with them, render the rectangle, and
/// check whether it is a single flat colour. A tool that draws a black box
/// over text it did not remove passes every extraction-based check we have,
/// because the text IS supposed to still be extractable from a normal
/// document — the leak is the *combination* of text and covering box.</para>
///
/// <para>It is also an independent IMPLEMENTATION of the property excise's own
/// <c>HiddenTextDetector</c> / <c>excise audit</c> claims, which until now was
/// verified only by excise. A tool must not be its own oracle for the property
/// it exists to guarantee.</para>
///
/// <para><b>Licence posture.</b> x-ray is invoked as a subprocess, never
/// linked. It depends on PyMuPDF (AGPL), the same posture already documented
/// for mutool and Ghostscript in LICENSES.md.</para>
///
/// <para>Never throws: returns null when x-ray is unavailable or refuses, so
/// callers treat "no answer" as data. ⚠️ Callers must NOT read null as
/// "clean" — a null and an empty list mean opposite things, and conflating
/// them is how an absent oracle turns into a passing gate.</para>
/// </summary>
public static class XRayBadRedactionDetector
{
    /// <summary>One bad redaction: where it is, and what is readable under it.</summary>
    public sealed record BadRedaction(int Page, double X0, double Y0, double X1, double Y1, string Text);

    private static readonly Lazy<string?> _python = new(FindPython);

    /// <summary>
    /// A python that can <c>import xray</c>, or null. Honours
    /// <c>EXCISE_XRAY_PYTHON</c> first so a venv can be pointed at explicitly
    /// — x-ray is rarely installed into a system python.
    /// </summary>
    private static string? FindPython()
    {
        var candidates = new List<string>();
        var configured = Environment.GetEnvironmentVariable("EXCISE_XRAY_PYTHON");
        if (!string.IsNullOrWhiteSpace(configured)) candidates.Add(configured);
        // The venv scripts/download-xray.sh creates, so a developer who ran
        // the setup script needs no environment variable.
        var repoVenv = FindRepoVenv();
        if (repoVenv != null) candidates.Add(repoVenv);

        candidates.Add("python3");
        candidates.Add("python");

        foreach (var candidate in candidates)
        {
            try
            {
                // Probe by IMPORTING, not by "does a binary exist". A python
                // without the module is not an oracle, and reporting it as one
                // makes every later call fail confusingly.
                using var p = Start(candidate, "-c \"import xray\"");
                if (p == null) continue;
                if (!p.WaitForExit(15_000)) { TryKill(p); continue; }
                if (p.ExitCode == 0) return candidate;
            }
            catch { /* try the next candidate */ }
        }
        return null;
    }

    /// <summary>
    /// <c>tools/vendor/xray-venv/bin/python</c>, walking up from the test
    /// binary to the repo root, or null when it is not there.
    /// </summary>
    private static string? FindRepoVenv()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        if (dir == null) return null;

        var candidate = Path.Combine(dir.FullName, "tools", "vendor", "xray-venv", "bin", "python");
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>True when an x-ray-capable python was found.</summary>
    public static bool IsAvailable => _python.Value != null;

    /// <summary>
    /// Every bad redaction x-ray finds, or null when x-ray is unavailable or
    /// refused. An EMPTY list means "x-ray ran and found none" — a real
    /// verdict, unlike null.
    /// </summary>
    public static IReadOnlyList<BadRedaction>? Inspect(string pdfPath, int timeoutMs = 60_000)
    {
        var python = _python.Value;
        if (python == null) return null;
        if (!File.Exists(pdfPath)) return null;

        // x-ray's own __main__ prints a python repr, not JSON, so drive the
        // library directly and serialise it ourselves.
        var script =
            "import json,sys,xray;" +
            "print('<<<XRAY>>>' + json.dumps(xray.inspect(sys.argv[1]), default=str))";

        string stdout;
        try
        {
            using var p = Start(python, $"-c \"{script}\" \"{pdfPath}\"");
            if (p == null) return null;
            // #1083: drain BOTH pipes concurrently and bound the wait. Reading
            // stdout to end BEFORE WaitForExit (the previous shape) hangs if the
            // child never closes stdout, and blocks stderr from draining so a
            // chatty child can pipe-deadlock. Same fix as PdfOcrService.
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { TryKill(p); return null; }
            stdout = outTask.GetAwaiter().GetResult();
            errTask.GetAwaiter().GetResult();
            if (p.ExitCode != 0) return null;
        }
        catch { return null; }

        // PyMuPDF prints deprecation chatter to stdout on some builds, so
        // anchor on our own marker rather than assuming the whole of stdout
        // is JSON.
        var at = stdout.IndexOf("<<<XRAY>>>", StringComparison.Ordinal);
        if (at < 0) return null;
        var json = stdout[(at + "<<<XRAY>>>".Length)..].Trim();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var found = new List<BadRedaction>();
            foreach (var page in doc.RootElement.EnumerateObject())
            {
                if (!int.TryParse(page.Name, out var pageNumber)) continue;
                foreach (var item in page.Value.EnumerateArray())
                {
                    var bbox = item.GetProperty("bbox").EnumerateArray()
                                   .Select(v => v.GetDouble()).ToArray();
                    if (bbox.Length != 4) continue;
                    found.Add(new BadRedaction(
                        pageNumber, bbox[0], bbox[1], bbox[2], bbox[3],
                        item.GetProperty("text").GetString() ?? string.Empty));
                }
            }
            return found;
        }
        catch (JsonException) { return null; }
    }

    private static Process? Start(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        return Process.Start(psi);
    }

    private static void TryKill(Process p)
    {
        try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
    }
}
