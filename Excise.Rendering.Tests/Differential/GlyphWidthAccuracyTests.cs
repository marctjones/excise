using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #839 — a glyph-WIDTH accuracy gate, independent of excise.
///
/// Extraction-parity (#645) counts characters; it says nothing about whether a
/// glyph's measured width is right. Two width regressions were invisible to it
/// and both feed the redaction bounding-box / black-box path directly:
///
///   #833 — the text-matrix scale was dropped, so unit-Tf glyphs measured a
///          ~0.3pt sliver instead of their real width;
///   #843 — an indirect <c>/Widths</c> reference went unresolved, so every glyph
///          took the flat 600/1000 default regardless of the real font metrics.
///
/// This gate compares excise's per-glyph box widths to mutool's stext quad
/// widths — an INDEPENDENT oracle — over real corpus pages, at the distribution
/// level so no fragile per-glyph alignment is needed:
///
///   * MEDIAN width ratio must sit near 1 — catches #833's global shrink and any
///     systematic mis-scale.
///   * excise's width SPREAD (coefficient of variation) must track mutool's —
///     catches #843, where flattening every width to one value collapses the
///     spread even if the median happens to land right.
/// </summary>
public class GlyphWidthAccuracyTests
{
    public static IEnumerable<object[]> Cases() => new[]
    {
        // file, page, kind — a clean proportional font and a unit-Tf page.
        new object[] { "test-pdfs/local-real-world/producingoss.pdf", 1, "proportional Type1" },
        new object[] { "test-pdfs/federal/scotus-trump-v-us.pdf", 3, "unit-Tf TrueType" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void ExciseGlyphWidths_MatchAnIndependentOracle_InMedianAndSpread(string relPath, int page, string kind)
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        var path = Path.Combine(RepoRoot(), relPath);
        Assert.SkipUnless(File.Exists(path), $"fixture not downloaded: {relPath}");

        var mutool = MutoolCharWidthsByValue(path, page);
        Assert.SkipUnless(mutool.Count > 20, "mutool produced too few measurable glyphs");

        using var doc = PdfDocument.Open(File.ReadAllBytes(path));
        var excise = doc.GetPage(page).Letters
            .Where(l => !string.IsNullOrEmpty(l.Value) && char.IsLetterOrDigit(l.Value[0]))
            .Where(l => l.GlyphRectangle.Width > 0)
            .GroupBy(l => l.Value[0])
            .ToDictionary(g => g.Key, g => Median(g.Select(l => l.GlyphRectangle.Width)));

        // Compare only characters both tools measured — a per-character median
        // ratio, robust to reading-order and to either tool skipping a glyph.
        var ratios = new List<double>();
        foreach (var (ch, ew) in excise)
            if (mutool.TryGetValue(ch, out var mw) && mw > 0.1)
                ratios.Add(ew / mw);

        ratios.Count.Should().BeGreaterThan(15,
            $"{kind}: too few shared glyphs to judge width accuracy");

        // MEDIAN near 1 — a global shrink (#833: median ~0.02) or a systematic
        // scale error moves this off 1.
        var medianRatio = Median(ratios);
        medianRatio.Should().BeInRange(0.80, 1.25,
            $"{kind}: excise glyph box widths are off by more than 25% vs mutool " +
            $"(median ratio {medianRatio:0.000}); a value near 0 is the #833 matrix-scale drop");

        // SPREAD tracks the oracle — flattening every width to one value (#843's
        // 600-default) collapses excise's coefficient of variation even when the
        // median lands right. Real proportional fonts vary (i vs m); a flat model
        // does not.
        var exciseCov = CoV(excise.Values);
        var mutoolCov = CoV(mutool.Where(kv => excise.ContainsKey(kv.Key)).Select(kv => kv.Value));
        mutoolCov.Should().BeGreaterThan(0.10, $"{kind}: oracle sanity — a real font's widths vary");
        exciseCov.Should().BeGreaterThan(0.5 * mutoolCov,
            $"{kind}: excise's width spread ({exciseCov:0.000}) has collapsed relative to the oracle " +
            $"({mutoolCov:0.000}) — the signature of every glyph taking one flat width (#843)");
    }

    /// <summary>Median glyph box width per character value, from mutool stext quads.</summary>
    private static Dictionary<char, double> MutoolCharWidthsByValue(string path, int page)
    {
        var xml = RunMutoolStext(path, page);
        var byChar = new Dictionary<char, List<double>>();

        // <char quad="ulx uly urx ury llx lly lrx lry" ... c="X"/>
        foreach (Match m in Regex.Matches(xml, "<char quad=\"([^\"]+)\"[^>]*c=\"([^\"]*)\""))
        {
            var c = m.Groups[2].Value;
            if (c.Length != 1 || !char.IsLetterOrDigit(c[0])) continue;
            var q = m.Groups[1].Value.Split(' ');
            if (q.Length < 3) continue;
            var ulx = double.Parse(q[0], CultureInfo.InvariantCulture);
            var urx = double.Parse(q[2], CultureInfo.InvariantCulture);
            var w = Math.Abs(urx - ulx);
            if (w <= 0) continue;
            (byChar.TryGetValue(c[0], out var list) ? list : byChar[c[0]] = new List<double>()).Add(w);
        }

        return byChar.ToDictionary(kv => kv.Key, kv => Median(kv.Value));
    }

    private static string RunMutoolStext(string path, int page)
    {
        var psi = new ProcessStartInfo("mutool", $"draw -F stext -o - \"{path}\" {page}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi)!;
        // Drain both pipes concurrently: reading stdout to end while nothing
        // drains stderr deadlocks once the child fills that buffer (~64 KB).
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(30_000))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
        }
        var outp = outTask.GetAwaiter().GetResult();
        _ = errTask.GetAwaiter().GetResult();
        return outp;
    }

    private static double Median(IEnumerable<double> xs)
    {
        var s = xs.OrderBy(x => x).ToList();
        if (s.Count == 0) return 0;
        return s.Count % 2 == 1 ? s[s.Count / 2] : 0.5 * (s[s.Count / 2 - 1] + s[s.Count / 2]);
    }

    private static double CoV(IEnumerable<double> xs)
    {
        var s = xs.ToList();
        if (s.Count < 2) return 0;
        var mean = s.Average();
        if (mean <= 0) return 0;
        var variance = s.Sum(x => (x - mean) * (x - mean)) / s.Count;
        return Math.Sqrt(variance) / mean;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}
