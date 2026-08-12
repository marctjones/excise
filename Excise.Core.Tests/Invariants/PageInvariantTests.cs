using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Xunit;

namespace Excise.Core.Tests.Invariants;

/// <summary>
/// Cross-pipeline invariants that need NO ORACLE (#904).
///
/// WHY THIS EXISTS
///
/// The suite is strong at regression and weak at first discovery. Two real
/// defects were found by rendering a page and looking at it, not by ~8,000
/// tests: every unchecked box on a blank W-9 rendered as ticked, and 22% of a
/// page's letters landing outside the page box (#899). Both are things a user
/// notices in ten seconds.
///
/// The reason the existing oracles missed them is structural: a reference
/// renderer can only test the pipeline you point it at, and nothing was pointed
/// at the extractor's geometry. These invariants need no reference at all —
/// they are properties excise's own output must satisfy to be self-consistent,
/// so they run anywhere, cost nothing, and cannot be fooled by an oracle
/// disagreeing for its own reasons.
///
/// MEASURED BEFORE BEING ASSERTED
///
/// Every threshold here was measured across ~950 readable documents (pdf20,
/// generated-regressions, sample-pdfs, smoke, pdfium, pdf.js) before being
/// written down. The four zero-tolerance invariants had ZERO violations
/// everywhere; they are not aspirations.
/// </summary>
public class PageInvariantTests
{
    /// <summary>
    /// Fixtures checked into the repo, so this runs on CI rather than only on a
    /// machine with the gitignored corpora.
    /// </summary>
    public static TheoryData<string> CheckedInFixtures()
    {
        var data = new TheoryData<string>();
        foreach (var dir in new[] { "pdf20", "generated-regressions", "sample-pdfs" })
        {
            var path = FindCorpus(dir);
            if (path == null) continue;
            foreach (var pdf in Directory.GetFiles(path, "*.pdf").OrderBy(f => f))
                data.Add(pdf);
        }
        if (data.Count == 0) data.Add(string.Empty);   // keeps the theory enumerable
        return data;
    }

    /// <summary>
    /// `page.Text` must never contain a character the letter stream does not.
    ///
    /// Text is DERIVED from letters, so a character appearing in the string but
    /// not in the glyphs it was built from means the assembler invented content
    /// — the over-extraction shape that #649 was filed for. Measured at zero
    /// across every corpus, so any violation is a genuine regression.
    /// </summary>
    [Theory]
    [MemberData(nameof(CheckedInFixtures))]
    public void PageText_NeverContainsCharactersTheLettersLack(string pdf)
    {
        Assert.SkipWhen(string.IsNullOrEmpty(pdf), "no checked-in fixtures found");

        ForEachPage(pdf, (name, pageNo, page) =>
        {
            var fromLetters = Histogram(page.Letters.Select(l => l.Value).SelectMany(s => s ?? ""));
            var fromText = Histogram(page.Text ?? "");

            foreach (var (ch, count) in fromText)
            {
                fromLetters.TryGetValue(ch, out var available);
                count.Should().BeLessThanOrEqualTo(available,
                    $"{name} p{pageNo}: page.Text contains {count} '{ch}' but the letter stream " +
                    $"has {available}. Text is derived from letters, so this means the assembler " +
                    "produced content no glyph accounts for");
            }
        });
    }

    /// <summary>
    /// Extraction must be deterministic: opening the same file twice must yield
    /// the same letters.
    ///
    /// Cheap, and it catches state leaking between documents — shared caches,
    /// static font tables, anything order-dependent. A non-deterministic
    /// extractor makes every other assertion in the suite conditional on run
    /// order, including the redaction ones.
    /// </summary>
    [Theory]
    [MemberData(nameof(CheckedInFixtures))]
    public void Extraction_IsDeterministicAcrossTwoOpens(string pdf)
    {
        Assert.SkipWhen(string.IsNullOrEmpty(pdf), "no checked-in fixtures found");

        using var a = PdfDocument.Open(pdf);
        using var b = PdfDocument.Open(pdf);
        a.PageCount.Should().Be(b.PageCount);

        for (int p = 1; p <= Math.Min(a.PageCount, MaxPages); p++)
        {
            b.GetPage(p).Letters.Count.Should().Be(a.GetPage(p).Letters.Count,
                $"{Path.GetFileName(pdf)} p{p}: two independent opens of the same bytes must " +
                "extract the same letters, or every other assertion becomes run-order dependent");
        }
    }

    /// <summary>
    /// A save-and-reload round trip must preserve the page count and page 1's
    /// letter count.
    ///
    /// This is the writer's own self-consistency check and needs no reference
    /// renderer: whatever excise wrote, excise must be able to read back
    /// unchanged. It is the cheapest guard that exists against a writer change
    /// dropping content — directly relevant to #923, which will rewrite how
    /// objects are serialised.
    /// </summary>
    [Theory]
    [MemberData(nameof(CheckedInFixtures))]
    public void SaveAndReload_PreservesPageCountAndLetters(string pdf)
    {
        Assert.SkipWhen(string.IsNullOrEmpty(pdf), "no checked-in fixtures found");

        using var doc = PdfDocument.Open(pdf);
        var pagesBefore = doc.PageCount;
        var lettersBefore = pagesBefore > 0 ? doc.GetPage(1).Letters.Count : 0;

        var tmp = Path.Combine(Path.GetTempPath(), $"excise-inv-{Guid.NewGuid():N}.pdf");
        try
        {
            doc.Save(tmp);
            using var reloaded = PdfDocument.Open(tmp);

            reloaded.PageCount.Should().Be(pagesBefore,
                $"{Path.GetFileName(pdf)}: saving and reopening must not change the page count");
            if (pagesBefore > 0)
            {
                reloaded.GetPage(1).Letters.Count.Should().Be(lettersBefore,
                    $"{Path.GetFileName(pdf)}: saving and reopening must not change page 1's " +
                    "letter count — whatever excise wrote, excise must read back unchanged");
            }
        }
        finally { try { File.Delete(tmp); } catch { /* best effort */ } }
    }

    /// <summary>
    /// Letters must land ON the page.
    ///
    /// THIS IS THE ONE THAT WOULD HAVE CAUGHT #899. A glyph positioned outside
    /// the MediaBox is invisible and is silently filtered out of
    /// <see cref="PdfPage.Text"/> — so text vanishes with no error anywhere.
    /// Measured on page 117 of irs-1040-instructions.pdf: 3268 non-whitespace
    /// letters, 879 of them outside a MediaBox covering the whole page, and
    /// `page.Text` returning exactly the 2389 that remain. The filtering is
    /// correct; the POSITIONS are wrong.
    ///
    /// Distribution measured across the corpora:
    ///
    ///     pdf20, generated-regressions   0 pages affected
    ///     pdfium (251 docs)              1 doc over 5%
    ///     smoke (real-world, all pages)  89 pages over 5%, 8 over 20%
    ///
    /// Clean on spec fixtures, rare on pdfium, common on dense multi-column
    /// government documents — the profile of a real defect, not a threshold
    /// artefact.
    ///
    /// Baselined rather than asserted at zero, because #899 is open and this
    /// must land green. The baseline is a RATCHET: fixing #899 should empty it.
    /// </summary>
    [Theory]
    [MemberData(nameof(CheckedInFixtures))]
    public void Letters_LandOnThePage(string pdf)
    {
        Assert.SkipWhen(string.IsNullOrEmpty(pdf), "no checked-in fixtures found");

        var name = Path.GetFileName(pdf);
        var allowed = KnownOffPageFixtures.TryGetValue(name, out var v) ? v : 0.0;

        ForEachPage(pdf, (file, pageNo, page) =>
        {
            var box = page.MediaBox.Normalize();
            var glyphs = page.Letters.Where(l => !string.IsNullOrWhiteSpace(l.Value)).ToList();
            if (glyphs.Count == 0) return;

            var offPage = glyphs.Count(l => !l.GlyphRectangle.Normalize().IntersectsWith(box));
            var fraction = (double)offPage / glyphs.Count;

            fraction.Should().BeLessThanOrEqualTo(allowed,
                $"{file} p{pageNo}: {offPage} of {glyphs.Count} glyphs are positioned outside " +
                "the page box. They are invisible AND silently dropped from page.Text, so text " +
                "disappears with no error (#899). If this is a new fixture with legitimately " +
                "off-page content, add it to KnownOffPageFixtures with a reason");
        });
    }

    /// <summary>
    /// Fixtures with legitimately (or knowingly) off-page glyphs, with the
    /// tolerated fraction. Keep this SHORT — every entry is a page where text
    /// silently vanishes, and a long list means the invariant has been
    /// negotiated away rather than satisfied.
    /// </summary>
    private static readonly Dictionary<string, double> KnownOffPageFixtures = new(StringComparer.OrdinalIgnoreCase)
    {
        // Deliberately scrambled glyph order — the fixture exists to exercise
        // out-of-order/off-position content, so off-page glyphs are the point.
        ["birth-certificate-request-scrambled.pdf"] = 1.0,
    };

    // ── helpers ──────────────────────────────────────────────────────────────

    private const int MaxPages = 5;

    private static void ForEachPage(string pdf, Action<string, int, PdfPage> check)
    {
        using var doc = PdfDocument.Open(pdf);
        var name = Path.GetFileName(pdf);
        for (int p = 1; p <= Math.Min(doc.PageCount, MaxPages); p++)
        {
            var page = doc.GetPage(p);
            if (page.Letters.Count == 0) continue;
            check(name, p, page);
        }
    }

    private static Dictionary<char, int> Histogram(IEnumerable<char> chars)
    {
        var h = new Dictionary<char, int>();
        foreach (var c in chars)
        {
            if (char.IsWhiteSpace(c)) continue;
            h.TryGetValue(c, out var n);
            h[c] = n + 1;
        }
        return h;
    }

    private static string? FindCorpus(string name)
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "test-pdfs", name);
            if (Directory.Exists(candidate) && Directory.GetFiles(candidate, "*.pdf").Length > 0)
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
