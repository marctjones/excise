using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #944 — collateral split into the two classes with opposite correct
/// responses, because one number conflates defect with physics:
///
///   BOUNDARY — a destroyed character whose glyph touches a term occurrence.
///   Irreducible at the edges (partial glyph coverage, ligature atomicity) and
///   already a chosen policy: GlyphRemovalStrategy.AnyOverlap errs toward
///   removal because leaking half a glyph of the secret is worse than losing
///   half a glyph of a neighbour. Measured in glyphs, bounded by perimeter.
///
///   REMOTE — a destroyed character whose glyph touches NO occurrence. There
///   is no physical reason for it to disappear; every #942 failure was this
///   class at 100-1000x the boundary band. The target is ZERO, and a ratchet
///   here would just be a defect budget wearing a policy costume.
///
/// INDEPENDENT EXPECTATIONS, the #942-oracle lesson applied: term occurrences
/// are located from mutool's OWN character positions (stext quads), never from
/// excise's matches — a broken redaction corrupts excise's geometry too, and
/// comparing qpdf's answer against excise's own rect once passed a 50pt shift.
///
/// DESTRUCTION IS A NET COUNT, not a position mismatch. GlyphRemover rewrites
/// the surviving neighbours of a hit, and mutool then reports them a few
/// (sometimes many) points away. Treating "not at the same quad" as deleted
/// counted reconstruction jitter as remote — on the W-9 that invented 1,870
/// remote characters of which zero had actually left the page. Per (page,
/// character) we charge <c>max(0, before − after)</c> deletions, assigned
/// in-occurrence first, then boundary, then remote, so a moved neighbour
/// cannot masquerade as a remote defect.
/// </summary>
public class RedactionRemoteCollateralTests
{
    /// <summary>
    /// Per-fixture remote ceiling. Zero is the target and the default; every
    /// entry here is a named residual, not tolerance. A rise is a new defect.
    /// No fixture is remote-zero after the §9.4.2 fix — the exception list
    /// *is* the defect inventory (#944). Attribution of the booklet-scale
    /// rows stays on #942.
    /// </summary>
    private static readonly Dictionary<string, int> KnownRemoteCeilings = new(StringComparer.OrdinalIgnoreCase)
    {
        // Measured 2026-08-14 with the net-count classifier (mutool stext).
        // No fixture is remote-zero yet. These are named residuals after the
        // §9.4.2 fix, not tolerance — a rise is a new defect. Attribution of
        // the booklet-scale rows stays on #942; do not add headroom.
        ["cdc-vis-covid-19.pdf|COVID"] = 286,
        ["cms-40b-medicare-part-b.pdf|information"] = 527,
        ["irs-1040-instructions.pdf|your"] = 21663,
        ["irs-1040.pdf|line"] = 16,
        ["irs-pub509-2026.pdf|Form"] = 6330,
        ["irs-w4.pdf|your"] = 261,
        ["irs-w9.pdf|Form"] = 171,
        ["scotus-trump-v-anderson.pdf|that"] = 496,
        ["scotus-trump-v-us.pdf|that"] = 2685,
        ["state-ds11-passport.pdf|your"] = 114,
        ["state-ds82-passport-renewal.pdf|your"] = 62,
        ["uscis-i-9.pdf|Name"] = 792,
    };

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var f in Fixtures())
            data.Add(Path.GetFileName(f));
        if (data.Count == 0) data.Add("(no corpus)");
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void RedactingATerm_DestroysNothingRemoteFromAnyMatch(string fixtureName)
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        Assert.SkipWhen(fixtureName == "(no corpus)", "corpus not present");
        var path = Fixtures().FirstOrDefault(f => Path.GetFileName(f) == fixtureName);
        Assert.SkipWhen(path == null, "fixture not found");

        var before = MutoolStext.ExtractChars(path!);
        Assert.SkipWhen(before == null || before.Count < 200, "fixture has too little text");

        var term = MostFrequentTerm(before!);
        Assert.SkipWhen(term == null, "no repeated term to sample");

        // Occurrences from mutool's own geometry.
        var occurrences = FindOccurrences(before!, term!);
        occurrences.Should().NotBeEmpty("fixture sanity — the sampled term must occur");

        var output = Path.Combine(Path.GetTempPath(), $"excise-remote-{Guid.NewGuid():N}.pdf");
        try
        {
            using (var doc = PdfDocument.Open(File.ReadAllBytes(path!)))
            {
                doc.RedactText(term!);
                doc.Save(output);
            }

            var after = MutoolStext.ExtractChars(output);
            after.Should().NotBeNull();

            PartitionDestroyed(before!, after!, occurrences, TouchBandPt,
                out var inOccurrence, out var boundary, out var remote);

            var ceiling = KnownRemoteCeilings.TryGetValue($"{fixtureName}|{term}", out var v) ? v : 0;
            var sample = string.Join("", remote.Take(60).Select(c => c.C));
            var pages = string.Join(",", remote.Select(c => c.Page).Distinct().OrderBy(p => p).Take(8));
            // xunit swallows Console.WriteLine under the VSTest logger.
            var reportPath = Environment.GetEnvironmentVariable("REDACTION_REMOTE_REPORT_PATH");
            if (!string.IsNullOrEmpty(reportPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
                File.AppendAllText(reportPath,
                    $"{fixtureName}\t{term}\t{inOccurrence.Count}\t{boundary.Count}\t{remote.Count}\t{pages}\t{sample}{Environment.NewLine}");
            }
            if (Environment.GetEnvironmentVariable("REDACTION_REMOTE_REPORT") == "1")
                return;

            remote.Count.Should().BeLessThanOrEqualTo(ceiling,
                $"{fixtureName}: redacting '{term}' destroyed {remote.Count} characters that touch NO " +
                $"occurrence of the term (boundary-adjacent: {boundary.Count}, a policy, not counted here). " +
                $"Remote destruction is always a defect (#944). First destroyed remote text: " +
                $"\"{string.Join("", remote.Take(40).Select(c => c.C))}\" on page {remote.FirstOrDefault().Page}");
        }
        finally { try { File.Delete(output); } catch { /* best effort */ } }
    }

    [Fact]
    public void NetCount_IgnoresNeighboursThatOnlyMoved()
    {
        // "cat" at the origin is the term. The neighbour "Z" stays on the
        // page but reconstruction reports it 20pt to the right. "Q" on the
        // far side of the page is actually gone. Only Q is remote.
        var before = new List<MutoolStext.Char>
        {
            new(1, "c", 0, 0, 10, 10),
            new(1, "a", 10, 0, 20, 10),
            new(1, "t", 20, 0, 30, 10),
            new(1, "Z", 40, 0, 50, 10),
            new(1, "Q", 200, 0, 210, 10),
        };
        var after = new List<MutoolStext.Char>
        {
            new(1, "Z", 60, 0, 70, 10),
        };
        var occ = FindOccurrences(before, "cat");
        PartitionDestroyed(before, after, occ, TouchBandPt,
            out var inOcc, out var boundary, out var remote);
        inOcc.Select(c => c.C).Should().Equal("c", "a", "t");
        boundary.Should().BeEmpty();
        remote.Select(c => c.C).Should().Equal("Q");
    }

    [Fact]
    public void MutoolStext_KeepsControlCharactersXmlWouldReject()
    {
        // mutool writes PDF string bytes as numeric character references.
        // U+0007 is legal in a PDF and illegal in XML 1.0 — the CDC VIS
        // fixture emits one, and a strict parse drops the whole document.
        const string xml =
            "<document><page id=\"page0\">" +
            "<char quad=\"0 10 1 10 0 0 1 0\" c=\"&#x7;\"/>" +
            "<char quad=\"1 10 2 10 1 0 2 0\" c=\"A\"/>" +
            "</page></document>";
        var chars = MutoolStext.Parse(xml);
        chars.Should().HaveCount(2);
        chars[0].C.Should().Be("\u0007");
        chars[1].C.Should().Be("A");
        chars[1].X0.Should().Be(1);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private const double TouchBandPt = 3.0;

    private readonly record struct Box(int Page, double X0, double Y0, double X1, double Y1);

    private enum Band { InOccurrence = 0, Boundary = 1, Remote = 2 }

    /// <summary>
    /// Charge <c>max(0, before − after)</c> deletions per (page, character),
    /// in-occurrence first so reconstruction jitter cannot spend the loss
    /// budget on a neighbour that is still on the page.
    /// </summary>
    private static void PartitionDestroyed(
        List<MutoolStext.Char> before,
        List<MutoolStext.Char> after,
        List<Box> occurrences,
        double touch,
        out List<MutoolStext.Char> inOccurrence,
        out List<MutoolStext.Char> boundary,
        out List<MutoolStext.Char> remote)
    {
        inOccurrence = new List<MutoolStext.Char>();
        boundary = new List<MutoolStext.Char>();
        remote = new List<MutoolStext.Char>();

        static bool Alnum(MutoolStext.Char c) =>
            c.C.Length > 0 && char.IsLetterOrDigit(c.C[0]);

        var afterCount = new Dictionary<(int, string), int>();
        foreach (var c in after.Where(Alnum))
        {
            var k = (c.Page, c.C);
            afterCount[k] = afterCount.TryGetValue(k, out var n) ? n + 1 : 1;
        }

        var groups = new Dictionary<(int, string), List<MutoolStext.Char>>();
        foreach (var c in before.Where(Alnum))
        {
            var k = (c.Page, c.C);
            if (!groups.TryGetValue(k, out var list))
                groups[k] = list = new List<MutoolStext.Char>();
            list.Add(c);
        }

        foreach (var (key, group) in groups)
        {
            afterCount.TryGetValue(key, out var remain);
            var loss = Math.Max(0, group.Count - remain);
            if (loss == 0) continue;
            group.Sort((a, b) => BandOf(a, occurrences, touch).CompareTo(BandOf(b, occurrences, touch)));
            foreach (var c in group.Take(loss))
            {
                switch (BandOf(c, occurrences, touch))
                {
                    case Band.InOccurrence: inOccurrence.Add(c); break;
                    case Band.Boundary: boundary.Add(c); break;
                    default: remote.Add(c); break;
                }
            }
        }
    }

    private static Band BandOf(MutoolStext.Char c, List<Box> occurrences, double touch)
    {
        foreach (var o in occurrences)
        {
            if (o.Page != c.Page) continue;
            if (c.X0 >= o.X0 - 0.5 && c.X1 <= o.X1 + 0.5 &&
                c.Y0 >= o.Y0 - 0.5 && c.Y1 <= o.Y1 + 0.5)
                return Band.InOccurrence;
        }
        foreach (var o in occurrences)
        {
            if (o.Page != c.Page) continue;
            if (c.X1 >= o.X0 - touch && c.X0 <= o.X1 + touch &&
                c.Y1 >= o.Y0 - touch && c.Y0 <= o.Y1 + touch)
                return Band.Boundary;
        }
        return Band.Remote;
    }

    /// <summary>Occurrence boxes of <paramref name="term"/> from mutool's char stream.</summary>
    private static List<Box> FindOccurrences(List<MutoolStext.Char> chars, string term)
    {
        var boxes = new List<Box>();
        var t = term.ToLowerInvariant();
        for (var i = 0; i + t.Length <= chars.Count; i++)
        {
            var ok = true;
            for (var j = 0; j < t.Length; j++)
            {
                var c = chars[i + j];
                if (c.Page != chars[i].Page ||
                    char.ToLowerInvariant(c.C.Length > 0 ? c.C[0] : ' ') != t[j]) { ok = false; break; }
            }
            if (!ok) continue;
            var run = chars.Skip(i).Take(t.Length).ToList();
            boxes.Add(new Box(run[0].Page,
                run.Min(c => c.X0), run.Min(c => c.Y0),
                run.Max(c => c.X1), run.Max(c => c.Y1)));
        }
        return boxes;
    }

    private static string? MostFrequentTerm(List<MutoolStext.Char> chars)
    {
        var text = string.Concat(chars.Select(c => c.C));
        var freq = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var raw in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var w = new string(raw.Where(char.IsLetter).ToArray());
            if (w.Length is < 4 or > 12) continue;
            freq[w] = freq.TryGetValue(w, out var n) ? n + 1 : 1;
        }
        return freq.Where(kv => kv.Value >= 2)
                   .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                   .Select(kv => kv.Key).FirstOrDefault();
    }

    private static IEnumerable<string> Fixtures()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in new[] { "test-pdfs/smoke", "test-pdfs/federal" })
        {
            var full = Resolve(dir);
            if (full == null) continue;
            foreach (var f in Directory.EnumerateFiles(full, "*.pdf").OrderBy(x => x, StringComparer.Ordinal))
                if (seen.Add(Path.GetFileName(f)))
                    yield return f;
        }
    }

    private static string? Resolve(string rel)
    {
        for (var up = 0; up < 6; up++)
        {
            var p = Path.GetFullPath(Path.Combine(Enumerable.Repeat("..", up).DefaultIfEmpty(".").Aggregate(Path.Combine), rel));
            if (Directory.Exists(p)) return p;
        }
        return null;
    }
}
