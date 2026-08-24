using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1136 — rate excise's redaction against other tools by ATTACKING their
/// output. For each unredacted original (known answer, #1134), redact it with
/// each tool for real, then run the residue engine (#1133) on the result. The
/// residue engine's recall on a tool's output IS that tool's leak: low recall =
/// a redaction that resists recovery.
///
/// <para>This is the benchmark's point — not "is excise perfect" (it is not and
/// need not be), but "how does excise's resistance compare, across a range of
/// difficulty". Reported as recall-per-tool-per-band so a difference attaches to
/// a cause.</para>
///
/// <para>Scored against the constructed answer, never against a tool's own
/// claim. The tool redacts; the manifest says what was there; the residue
/// engine tries to get it back; ground truth says whether it succeeded.</para>
/// </summary>
public sealed class ToolResistanceComparisonTests
{
    private readonly ITestOutputHelper _out;
    public ToolResistanceComparisonTests(ITestOutputHelper o) { _out = o; }

    private static readonly string[] Names =
        ("James John Robert Michael William David Richard Joseph Thomas Charles " +
         "Christopher Daniel Matthew Anthony Donald Mark Paul Steven Andrew Kenneth " +
         "Mary Patricia Jennifer Linda Elizabeth Barbara Susan Jessica Sarah Karen " +
         "Nancy Lisa Betty Margaret Sandra Ashley Kimberly Emily Donna Michelle " +
         "Louise Farrar Anne Dorothy Carol Amanda Melissa Deborah Stephanie").Split(' ');
    private static readonly string[] Dates =
        { "01/15/1987","12/03/1992","07/22/1975","09/30/2001","03/11/1968","11/08/1954","06/19/1983","02/27/1990" };
    private static readonly string[] Digits =
        { "4012884012","5555341220","6011000990","3782822463","8842019375","1029384756","9998887776","4444333322" };

    private static IReadOnlyList<string> DictFor(string kind) => kind switch
    {
        "date" => Dates, "digits" => Digits, _ => Names,
    };

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, ".git"))) d = d.Parent;
        return d?.FullName ?? AppContext.BaseDirectory;
    }

    private static string? PyMuPdfPython()
    {
        var p = Path.Combine(RepoRoot(), "tools", "vendor", "xray-venv", "bin", "python");
        return File.Exists(p) ? p : null;
    }

    /// <summary>Redact with a tool; return the output path, or null on failure.</summary>
    private static bool RedactWith(string tool, string src, string dst, string term)
    {
        if (tool == "excise")
        {
            try
            {
                using var doc = PdfDocument.Open(src);
                doc.RedactText(term);
                doc.Save(dst);
                return true;
            }
            catch { return false; }
        }

        var py = PyMuPdfPython();
        var script = Path.Combine(RepoRoot(), "scripts", "benchmark-adapters", "redact-pymupdf.py");
        if (py == null || !File.Exists(script)) return false;
        try
        {
            var psi = new ProcessStartInfo(py) { RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { script, src, dst, term }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var outT = p.StandardOutput.ReadToEndAsync();  // #1083: drain before wait
            var errT = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(60_000)) { try { p.Kill(true); } catch { } return false; }
            outT.GetAwaiter().GetResult(); errT.GetAwaiter().GetResult();
            return p.ExitCode == 0 && File.Exists(dst);
        }
        catch { return false; }
    }

    private static int RecoverRank(string pdf, string answer, IReadOnlyList<string> dict)
    {
        var recs = ResidueRecoveryEngine.Recover(pdf, dict,
            new ResidueRecoveryEngine.Options(RequireMutoolCorroboration: false));
        var best = 0;
        foreach (var r in recs)
        {
            var idx = r.CandidatesFit.Select((w, i) => (w, i))
                .Where(t => string.Equals(t.w, answer, StringComparison.Ordinal))
                .Select(t => t.i + 1).DefaultIfEmpty(0).First();
            if (idx > 0 && (best == 0 || idx < best)) best = idx;
        }
        return best;
    }

    [Fact]
    public void ResistancePerBand_ExciseVsPyMuPdf()
    {
        var corpus = Path.Combine(RepoRoot(), "test-pdfs", "redaction-synthetic");
        var manifest = Path.Combine(corpus, "manifest.jsonl");
        Assert.SkipUnless(File.Exists(manifest),
            "run scripts/gen-redaction-corpus.py first [requires: corpus:redaction-synthetic]");
        Assert.SkipUnless(PyMuPdfPython() != null,
            "needs the PyMuPDF venv (scripts/download-xray.sh) [requires: file:tools/vendor/xray-venv/bin/python]");

        var originals = File.ReadAllLines(manifest).Where(l => l.Length > 0)
            .Select(l => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(l)!)
            .Where(m => m["method"].GetString() == "original")
            .ToList();

        var tools = new[] { "excise", "pymupdf" };
        // band -> tool -> list of recovery ranks (0 = not recovered)
        var data = new Dictionary<string, Dictionary<string, List<int>>>();

        foreach (var m in originals)
        {
            var id = m["id"].GetString()!;
            var band = m["band"].GetString()!;
            var answer = m["answer"].GetString()!;
            var kind = m["dictionary"].GetString()!;
            var src = Path.Combine(corpus, id + ".pdf");
            if (!File.Exists(src)) continue;
            var dict = DictFor(kind);

            foreach (var tool in tools)
            {
                var outp = Path.Combine(Path.GetTempPath(), $"resist-{tool}-{Guid.NewGuid():N}.pdf");
                try
                {
                    if (!RedactWith(tool, src, outp, answer)) continue;
                    var rank = RecoverRank(outp, answer, dict);
                    data.TryAdd(band, new());
                    data[band].TryAdd(tool, new());
                    data[band][tool].Add(rank);
                }
                finally { try { File.Delete(outp); } catch { } }
            }
        }

        data.Should().NotBeEmpty("originals must be redactable and scorable");

        // ── the comparison table ──────────────────────────────────────────
        _out.WriteLine("RECALL@5 after redaction -- LOWER IS BETTER REDACTION (harder to unredact)");
        _out.WriteLine($"{"band",-6} {"excise",18} {"pymupdf",18}");
        foreach (var band in data.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            string cell(string tool)
            {
                if (!data[band].TryGetValue(tool, out var ranks) || ranks.Count == 0) return "(no data)";
                var at5 = (double)ranks.Count(r => r is >= 1 and <= 5) / ranks.Count;
                return $"{at5,6:P0} ({ranks.Count} cases)";
            }
            _out.WriteLine($"{band,-6} {cell("excise"),18} {cell("pymupdf"),18}");
        }

        _out.WriteLine("");
        foreach (var tool in tools)
        {
            var all = data.Values.SelectMany(d => d.TryGetValue(tool, out var r) ? r : new List<int>()).ToList();
            if (all.Count == 0) continue;
            var leak = (double)all.Count(r => r is >= 1 and <= 5) / all.Count;
            _out.WriteLine($"{tool}: overall recall@5 = {leak:P0} over {all.Count} redactions " +
                           $"(lower = harder to unredact)");
        }
    }
}
