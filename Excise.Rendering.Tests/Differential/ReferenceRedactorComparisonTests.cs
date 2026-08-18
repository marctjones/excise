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
/// Compare excise's redaction against <b>a second implementation</b> — MuPDF's
/// own — on the same documents and terms (#1041).
///
/// <para>Why a second implementation at all, when
/// <see cref="RedactionCollateralHarness"/> already measures collateral against
/// mutool's extractor: that harness ratchets. It can say collateral got worse;
/// it cannot say whether 240 characters was ever acceptable. Its own docstring
/// makes the point — "the baseline records CURRENT behaviour, not good
/// behaviour". A peer implementation supplies the missing standard.</para>
///
/// <para><b>The comparison is ONE-DIRECTIONAL, and that is not caution — it is
/// measurement.</b> Redacting <c>Vaccine</c> from <c>cdc-vis-covid-19.pdf</c>,
/// MuPDF destroyed 240 characters of neighbouring text where excise destroyed
/// 1, because <c>applyRedactions</c> deletes every glyph intersecting the search
/// quad. A rule of "excise must match the reference" would have failed excise
/// for being better. So: <b>excise may destroy less than the reference, never
/// more; and excise may not leave a term the reference removed.</b></para>
///
/// <para>The disagreements run both ways, which is the whole value: MuPDF was
/// right on <c>issue15629.pdf</c> (#1040) and on <c>bug900822.pdf</c> (#1047),
/// and wrong on the CDC sheet.</para>
/// </summary>
public class ReferenceRedactorComparisonTests
{
    public static TheoryData<string> Fixtures()
    {
        var data = new TheoryData<string>();
        foreach (var f in EnumerateFixtures()) data.Add(Path.GetFileName(f));
        if (data.Count == 0) data.Add("(no corpus)");
        return data;
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void ExciseDestroysNoMoreThanTheReference_AndLeaksNothingItRemoved(string fixtureName)
    {
        Assert.SkipUnless(MutoolReferenceRedactor.IsAvailable, "mutool not installed");
        Assert.SkipWhen(fixtureName == "(no corpus)", "corpus not present");

        var path = EnumerateFixtures().FirstOrDefault(f => Path.GetFileName(f) == fixtureName);
        Assert.SkipWhen(path == null, "fixture not found");

        string before;
        try { before = ExtractAll(path!); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Assert.Skip($"excise cannot open {fixtureName}: {ex.GetType().Name}");
            return;
        }

        Assert.SkipWhen(before.Length < 200, "too little text to sample terms from");

        var failures = new List<string>();
        var sampled = 0;
        var compared = 0;

        foreach (var term in RedactionCollateralHarness.SampleTerms(before))
        {
            var occurrences = CountOccurrences(before, term);
            if (occurrences == 0) continue;
            sampled++;

            var refOut = TempPdf();
            var exOut = TempPdf();
            try
            {
                var reference = MutoolReferenceRedactor.Redact(path!, term, refOut);
                if (!reference.Succeeded)
                {
                    // The reference declining is data about the reference, not
                    // about excise. Nothing to compare; move on.
                    continue;
                }

                // ⚠️ THE CANARY. A reference that finds nothing writes a
                // byte-identical file, which a comparison would read as "the
                // reference removed nothing, so all of excise's removal is
                // over-removal" — a confident verdict from a run that never
                // happened. This is not hypothetical: the first version of the
                // reference script called page.search(), which returns 0 for
                // every term on every document in mutool 1.27.2.
                if (reference.HitsFound == 0)
                {
                    failures.Add(
                        $"'{term}': the REFERENCE found 0 hits, but an independent extractor " +
                        $"shows {occurrences}. That is a broken reference run, not a clean baseline.");
                    continue;
                }

                using (var doc = PdfDocument.Open(File.ReadAllBytes(path!)))
                {
                    doc.RedactText(term);
                    doc.Save(exOut);
                }

                compared++;

                var refAfter = ExtractAll(refOut);
                var exAfter = ExtractAll(exOut);

                var refLeft = CountOccurrences(refAfter, term);
                var exLeft = CountOccurrences(exAfter, term);

                // Direction one: excise may not leave what the reference removed.
                if (refLeft == 0 && exLeft > 0)
                    failures.Add(
                        $"'{term}': LEAK — excise left {exLeft} of {occurrences} occurrences " +
                        $"where MuPDF removed all {occurrences}.");

                // Direction two: excise may destroy less, never more.
                var refCollateral = Collateral(before, refAfter, term, occurrences);
                var exCollateral = Collateral(before, exAfter, term, occurrences);
                var ceiling = refCollateral + Math.Max(50, refCollateral / 10);

                if (exCollateral > ceiling)
                    failures.Add(
                        $"'{term}': excise destroyed {exCollateral} untargeted characters where " +
                        $"MuPDF destroyed {refCollateral} (ceiling {ceiling}).");
            }
            finally
            {
                TryDelete(refOut);
                TryDelete(exOut);
            }
        }

        // A harness that compared NOTHING must not report success. Every
        // `continue` above is a silent exit — the reference declining, a term
        // not present — and enough of them in a row turns a green run into a
        // statement about nothing at all. This is the same vacuous-green shape
        // as a --filter matching zero tests, and as the reference-finds-0
        // canary above.
        if (sampled > 0)
            compared.Should().BeGreaterThan(0,
                $"{fixtureName}: {sampled} term(s) were present in the document but NONE was " +
                "actually compared against the reference — a green run here would assert nothing");

        failures.Should().BeEmpty(
            $"{fixtureName}: excise must destroy no more than the reference and leak nothing " +
            "the reference removed.\n" + string.Join("\n", failures));
    }

    private static int Collateral(string before, string after, string term, int occurrences)
        => Math.Max(0, (Alnum(before) - Alnum(after)) - term.Length * occurrences);

    private static int Alnum(string s) => s.Count(char.IsLetterOrDigit);

    private static int CountOccurrences(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
             i >= 0;
             i = haystack.IndexOf(needle, i + 1, StringComparison.OrdinalIgnoreCase))
            n++;
        return n;
    }

    private static string TempPdf()
        => Path.Combine(Path.GetTempPath(), $"excise-refcmp-{Guid.NewGuid():N}.pdf");

    private static void TryDelete(string p)
    {
        try { File.Delete(p); } catch { /* best effort */ }
    }

    private static string ExtractAll(string pdfPath)
    {
        using var doc = PdfDocument.Open(File.ReadAllBytes(pdfPath));
        var pages = MutoolTextExtractor.ExtractAllPages(pdfPath, doc.PageCount);
        return pages == null ? "" : string.Join("\n", pages);
    }

    /// <summary>
    /// Deliberately the small, well-understood sets plus the known-bad files —
    /// NOT the whole corpus. This runs two redactors and three extractions per
    /// term; <see cref="RedactionCollateralHarness"/> is what sweeps 1,007
    /// documents. This one calibrates, it does not sweep.
    /// </summary>
    private static IEnumerable<string> EnumerateFixtures()
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

        foreach (var rel in new[]
                 {
                     "test-pdfs/pdfjs/issue15629.pdf",   // #1040
                     "test-pdfs/pdfjs/bug900822.pdf",    // #1047
                 })
        {
            var full = Resolve(rel);
            if (full != null && seen.Add(Path.GetFileName(full)))
                yield return full;
        }
    }

    private static string? Resolve(string rel)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir != null; i++)
        {
            var candidate = Path.Combine(dir, rel);
            if (File.Exists(candidate) || Directory.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}
