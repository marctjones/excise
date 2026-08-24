using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1104 — per-glyph ADVANCE PARITY against mutool. The general instrument
/// behind the font cluster: extraction-parity checks HOW MANY characters
/// excise reads; this checks WHERE they are, which a font-metrics defect
/// corrupts and a character-count comparison cannot see (#1100 placed every
/// glyph on a line off the page while extracting 100% of the characters).
///
/// <para>This is the ruler that must exist BEFORE #1102 (embedded font
/// widths) changes glyph geometry: it proves the change is right, not merely
/// not-catastrophic. Font-agnostic — the oracle is mutool's own rendered glyph
/// positions, so it needs no per-font knowledge.</para>
///
/// <para>Ratchets against a per-page baseline, like extraction-parity. A
/// green run means "no worse than the checked-in drift", never "aligned".</para>
/// </summary>
public sealed class AdvanceParityTests
{
    private readonly ITestOutputHelper _out;
    public AdvanceParityTests(ITestOutputHelper o) { _out = o; }

    private const string BaselinePath = "tests/advance-parity/baseline.json";

    // Same curated corpus as extraction-parity: real documents, no tiled
    // shared-content weirdness (#1101) that would make glyph counts diverge
    // for reasons unrelated to metric drift.
    // smoke/sample exercise EMBEDDED fonts (/Widths); redaction-synthetic
    // originals exercise the NON-EMBEDDED standard-14 path (#1100/#1102) that
    // the real corpus does not -- without them a standard-14 metric break is
    // invisible to this ruler.
    private static readonly string[] CorpusDirs =
        { "test-pdfs/smoke", "test-pdfs/sample-pdfs", "test-pdfs/redaction-synthetic" };

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, ".git"))) d = d.Parent;
        return d?.FullName ?? AppContext.BaseDirectory;
    }

    private sealed record PageDrift(string Key, int Aligned, double MeanDriftPt, double MaxDriftPt, string Status);

    [Fact]
    public void PerGlyphAdvanceParity_AgainstMutool()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "needs mutool [requires: tool:mutool]");
        var root = RepoRoot();

        var pdfs = CorpusDirs
            .Select(d => Path.Combine(root, d))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.GetFiles(d, "*.pdf"))
            .Where(f => !f.Contains("redaction-synthetic") || Path.GetFileName(f).Contains("-original-"))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        Assert.SkipWhen(pdfs.Count == 0, "corpus absent [requires: corpus:smoke]");

        var drifts = new List<PageDrift>();
        foreach (var pdf in pdfs)
        {
            int pageCount;
            try { using var doc = PdfDocument.Open(pdf); pageCount = doc.PageCount; }
            catch { continue; }

            var rel = Path.GetRelativePath(root, pdf).Replace('\\', '/');
            for (var page = 1; page <= pageCount; page++)
                drifts.Add(MeasurePage(pdf, rel, page));
        }

        var aligned = drifts.Where(d => d.Status == "aligned").ToList();
        aligned.Should().NotBeEmpty("at least some pages must align, or the measurement is vacuous");

        // ── report ────────────────────────────────────────────────────────
        var worst = aligned.OrderByDescending(d => d.MaxDriftPt).Take(10).ToList();
        _out.WriteLine($"pages: {drifts.Count}  aligned: {aligned.Count}  " +
                       $"count-mismatch: {drifts.Count(d => d.Status == "count-mismatch")}");
        _out.WriteLine($"aggregate mean drift: {aligned.Average(d => d.MeanDriftPt):F3}pt");
        foreach (var d in worst)
            _out.WriteLine($"  worst {d.Key}: max={d.MaxDriftPt:F2}pt mean={d.MeanDriftPt:F2}pt ({d.Aligned} glyphs)");

        var baseline = LoadBaseline(root);

        if (Environment.GetEnvironmentVariable("ADVANCE_PARITY_UPDATE") == "1")
        {
            WriteBaseline(root, aligned);
            _out.WriteLine("baseline updated");
            return;
        }

        // ── ratchet ───────────────────────────────────────────────────────
        var failures = new List<string>();
        foreach (var d in aligned)
        {
            if (!baseline.TryGetValue(d.Key, out var floor)) continue;   // new page: not gated
            var ceiling = floor + Math.Max(1.0, floor * 0.10);           // headroom for jitter
            if (d.MaxDriftPt > ceiling)
                failures.Add($"{d.Key}: max drift {d.MaxDriftPt:F2}pt exceeds baseline {floor:F2}pt (ceiling {ceiling:F2})");
        }

        failures.Should().BeEmpty(
            "per-glyph positions drifted further from mutool than the checked-in floor -- " +
            "a font-metrics regression the character-count gate cannot see (#1104):\n" +
            string.Join("\n", failures) +
            "\n\nIf deliberate (e.g. #1102 improved widths), re-run with ADVANCE_PARITY_UPDATE=1 " +
            "and review that the drift went DOWN.");
    }

    private static PageDrift MeasurePage(string pdf, string rel, int page)
    {
        var key = $"{rel}#{page}";

        List<(string Ch, double X)> ex;
        try
        {
            using var doc = PdfDocument.Open(pdf);
            ex = doc.GetPage(page).Letters
                .Where(l => l.Value.Length == 1 && char.IsLetterOrDigit(l.Value[0]))
                .Select(l => (l.Value, l.GlyphRectangle.Left))
                .ToList();
        }
        catch { return new PageDrift(key, 0, 0, 0, "excise-error"); }

        var mu = MutoolGlyphPositions.ExtractPage(pdf, page);
        if (mu == null) return new PageDrift(key, 0, 0, 0, "no-oracle");
        var muChars = mu.Where(g => g.Char.Length == 1 && char.IsLetterOrDigit(g.Char[0]))
                        .Select(g => (Ch: g.Char, X: g.X)).ToList();

        if (ex.Count == 0 || muChars.Count == 0) return new PageDrift(key, 0, 0, 0, "empty");

        // Counts must be comparable; a large mismatch is a #1101-class signal
        // (tiled/unclipped content), not metric drift -- do not gate drift on it.
        var ratio = (double)Math.Min(ex.Count, muChars.Count) / Math.Max(ex.Count, muChars.Count);
        if (ratio < 0.8) return new PageDrift(key, 0, 0, 0, "count-mismatch");

        // Align by sequence order (both are content/render order). Match only
        // where the character agrees, so a single skipped glyph re-syncs on the
        // next match rather than corrupting the whole tail.
        var diffs = new List<double>();
        int i = 0, j = 0;
        var exs = ex.OrderBy(e => e.X).ToList();
        var mus = muChars.OrderBy(m => m.X).ToList();
        while (i < exs.Count && j < mus.Count)
        {
            if (exs[i].Ch == mus[j].Ch) { diffs.Add(Math.Abs(exs[i].X - mus[j].X)); i++; j++; }
            else if (exs.Count - i > mus.Count - j) i++;   // excise has extra: advance it
            else j++;
        }

        if (diffs.Count < 5 || (double)diffs.Count / Math.Min(exs.Count, mus.Count) < 0.6)
            return new PageDrift(key, diffs.Count, 0, 0, "unaligned");

        return new PageDrift(key, diffs.Count, diffs.Average(), diffs.Max(), "aligned");
    }

    private static Dictionary<string, double> LoadBaseline(string root)
    {
        var path = Path.Combine(root, BaselinePath);
        if (!File.Exists(path)) return new();
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var d = new Dictionary<string, double>(StringComparer.Ordinal);
        if (doc.RootElement.TryGetProperty("pages", out var pages))
            foreach (var p in pages.EnumerateObject())
                d[p.Name] = p.Value.GetProperty("maxDriftFloorPt").GetDouble();
        return d;
    }

    private void WriteBaseline(string root, List<PageDrift> aligned)
    {
        var pages = aligned.OrderBy(d => d.Key, StringComparer.Ordinal)
            .ToDictionary(d => d.Key, d => new { maxDriftFloorPt = Math.Round(d.MaxDriftPt, 2) });
        var report = new
        {
            generatedUtc = "pinned",   // Date.Now unavailable in this context is fine; keep stable
            pageCount = aligned.Count,
            aggregateMeanDriftPt = Math.Round(aligned.Average(d => d.MeanDriftPt), 3),
            pages,
        };
        var path = Path.Combine(root, BaselinePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(report,
            new JsonSerializerOptions { WriteIndented = true }));
    }
}
