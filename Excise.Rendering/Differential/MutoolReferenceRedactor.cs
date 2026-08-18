using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Excise.Rendering.Differential;

/// <summary>
/// Outcome of a reference redaction. <paramref name="HitsFound"/> is how many
/// occurrences the REFERENCE located — the number the caller must sanity-check
/// against an independent extractor before trusting anything else here.
/// </summary>
public sealed record ReferenceRedactionResult(int HitsFound, string? Failure)
{
    public bool Succeeded => Failure == null;
}

/// <summary>
/// Redacts a term using <b>MuPDF's own</b> redaction — search, cover each hit
/// with a <c>/Redact</c> annotation, then <c>applyRedactions()</c> — as a second
/// implementation to compare excise against (#1041).
///
/// <para>Invoked only as a subprocess (<c>mutool run</c>), never linked,
/// matching the AGPL posture documented for
/// <see cref="MutoolReferenceRenderer"/> and in LICENSES.md. The script below is
/// excise's own code; MuPDF executes it, which is still subprocess use.</para>
///
/// <para><b>MuPDF is a peer, not a gold standard.</b> Measured on
/// <c>cdc-vis-covid-19.pdf</c>, redacting <c>Vaccine</c>: MuPDF removed the term
/// and <b>240 characters of neighbouring text</b> with it (<c>"What You Need to
/// Know"</c> → <c>"What"</c>) where excise removed 1, because
/// <c>applyRedactions</c> deletes every glyph intersecting the search quad. On
/// <c>issue15629.pdf</c> the disagreement ran the other way and MuPDF was right.
/// So comparisons against it must stay ONE-DIRECTIONAL: excise may destroy less
/// than the reference, never more. A rule of "excise must match MuPDF" would
/// have failed excise for being better.</para>
///
/// <para>⚠️ <b>The API used here matters.</b> <c>page.search()</c> silently
/// returns 0 hits for every term on every document in mutool 1.27.2. The
/// working call is <c>page.toStructuredText().search()</c>. Getting this wrong
/// produces a reference that does nothing and writes a byte-identical file — 
/// which a comparison reads as "the reference removed nothing, so all of
/// excise's removal is over-removal". That is a confidently wrong verdict from
/// a reference that never ran, which is why <see cref="ReferenceRedactionResult.HitsFound"/>
/// is returned rather than hidden: <b>a reference that finds 0 hits for a term
/// an independent extractor can see is a BROKEN RUN, never a clean
/// baseline.</b></para>
/// </summary>
public static class MutoolReferenceRedactor
{
    public static bool IsAvailable => MutoolReferenceRenderer.IsAvailable;

    private const string RedactScript = """
        var doc = Document.openDocument(scriptArgs[0]);
        var needle = scriptArgs[1];
        var total = 0;
        for (var i = 0; i < doc.countPages(); i++) {
            var page = doc.loadPage(i);
            // NOT page.search() — see the class remarks; it returns 0 always.
            var hits = page.toStructuredText().search(needle);
            if (!hits || hits.length === 0) continue;
            for (var h = 0; h < hits.length; h++) {
                var annot = page.createAnnotation("Redact");
                annot.setQuadPoints(hits[h]);
                total++;
            }
            page.applyRedactions();
        }
        print("EXCISE_REF_HITS " + total);
        doc.save(scriptArgs[2], "garbage=compact");
        """;

    /// <summary>
    /// Redact every occurrence of <paramref name="term"/> from
    /// <paramref name="inputPath"/> into <paramref name="outputPath"/> using
    /// MuPDF. Never throws — a failure is returned as data, matching the other
    /// reference tools' convention.
    /// </summary>
    public static ReferenceRedactionResult Redact(
        string inputPath, string term, string outputPath, int timeoutMs = 120_000)
    {
        if (!IsAvailable)
            return new ReferenceRedactionResult(0, "mutool is not on PATH");

        var scriptPath = Path.Combine(Path.GetTempPath(), $"excise-ref-redact-{Guid.NewGuid():N}.js");
        try
        {
            File.WriteAllText(scriptPath, RedactScript);

            var psi = new ProcessStartInfo("mutool")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("run");
            psi.ArgumentList.Add(scriptPath);
            psi.ArgumentList.Add(inputPath);
            psi.ArgumentList.Add(term);
            psi.ArgumentList.Add(outputPath);

            using var proc = Process.Start(psi);
            if (proc == null) return new ReferenceRedactionResult(0, "could not start mutool");

            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();

            if (!proc.WaitForExit(timeoutMs))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return new ReferenceRedactionResult(0, $"mutool timed out after {timeoutMs}ms");
            }

            if (proc.ExitCode != 0)
                return new ReferenceRedactionResult(0, $"mutool exited {proc.ExitCode}: {stderr.Trim()}");

            var m = Regex.Match(stdout, @"EXCISE_REF_HITS\s+(\d+)");
            if (!m.Success)
                return new ReferenceRedactionResult(0, $"mutool produced no hit count. stdout: {stdout.Trim()}");

            if (!File.Exists(outputPath))
                return new ReferenceRedactionResult(0, "mutool wrote no output file");

            return new ReferenceRedactionResult(
                int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new ReferenceRedactionResult(0, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { /* best effort */ }
        }
    }
}
