using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Redaction.Recovery;
using Excise.TestSupport;
using Xunit;
using Budget = Excise.Core.Redaction.Recovery.RedactionFitAnalyzer.WidthBudget;

namespace Excise.Core.Tests.Redaction.Recovery;

/// <summary>
/// #1589 — the neighbour-shift half of the width channel (Bland et al., PETS
/// 2023). A redaction box is an upper bound on the removed text's width; the
/// surviving glyphs on either side are a second, independent bound on the same
/// span. Intersecting them is strictly tighter than either.
///
/// <para><b>Why this is the interesting half.</b> A pixel or OCR attacker can
/// measure the box. Only a tool reading the content stream can measure where
/// the next glyph actually starts, to the hundredth of a point, and tell
/// whether the space beside the removed word survived. That last distinction is
/// what turns an inequality into an equality.</para>
///
/// <para>The gap is corroborated against mutool's own glyph positions, never
/// against excise's second opinion: a width channel that trusted excise to say
/// where the glyphs are would be excise grading its own arithmetic.</para>
/// </summary>
public class NeighbourGapWidthBudgetTests
{
    private const string CorpusName = "redaction-synthetic";

    private static string? CorpusDir() =>
        TestRepoLayout.FindDirectory("test-pdfs", CorpusName);

    private static IReadOnlyList<(string Id, string Answer, string Font)> WidthPreservingCases(int take)
    {
        var dir = CorpusDir();
        if (dir == null) return Array.Empty<(string, string, string)>();
        var manifest = Path.Combine(dir, "manifest.jsonl");
        if (!File.Exists(manifest)) return Array.Empty<(string, string, string)>();

        var cases = new List<(string, string, string)>();
        foreach (var line in File.ReadAllLines(manifest))
        {
            if (line.Length == 0) continue;
            using var doc = System.Text.Json.JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.GetProperty("method").GetString() != "width-preserving") continue;
            if (root.GetProperty("dictionary").GetString() == "random") continue;  // the control
            var id = root.GetProperty("id").GetString()!;
            if (!File.Exists(Path.Combine(dir, id + ".pdf"))) continue;
            cases.Add((id, root.GetProperty("answer").GetString()!, root.GetProperty("font").GetString()!));
            if (cases.Count >= take) break;
        }
        return cases;
    }

    private static readonly string[] Dictionary =
        ("James John Robert Michael William David Richard Joseph Thomas Charles " +
         "Christopher Daniel Matthew Anthony Donald Mark Paul Steven Andrew Kenneth " +
         "Mary Patricia Jennifer Linda Elizabeth Barbara Susan Jessica Sarah Karen " +
         "Nancy Lisa Betty Margaret Sandra Ashley Kimberly Emily Donna Michelle " +
         "Louise Farrar Anne Dorothy Carol Amanda Melissa Deborah Stephanie").Split(' ');

    // ── the arithmetic, as a property ────────────────────────────────────────

    [Fact]
    public void ASurvivingSpaceOnBothSidesMakesTheGapAnEquality()
    {
        // The case the corpus is full of and the one that matters: the word was
        // deleted and the spaces around it were not, so the gap IS the removed
        // run's width. Charging a space allowance for a space that is visibly
        // still there would throw away the only exact measurement available.
        var closed = Budget.FromNeighbourGap(37.36, spaceAdvancePt: 3.34, openSides: 0);
        closed.UpperPt.Should().Be(37.36);
        closed.LowerPt.Should().Be(37.36);
        closed.PaddingPt.Should().Be(0);

        var oneOpen = Budget.FromNeighbourGap(37.36, spaceAdvancePt: 3.34, openSides: 1);
        oneOpen.LowerPt.Should().BeApproximately(34.02, 1e-9);

        var bothOpen = Budget.FromNeighbourGap(37.36, spaceAdvancePt: 3.34, openSides: 2);
        bothOpen.LowerPt.Should().BeApproximately(30.68, 1e-9);
    }

    [Fact]
    public void IntersectingTheBoxWithTheGapIsNeverLooserThanEither()
    {
        var box = Budget.FromMark(markWidthPt: 37.36, fontSizePt: 12);   // [31.36, 37.36]
        var gap = Budget.FromNeighbourGap(37.36, 3.34, openSides: 0);    // [37.36, 37.36]

        var both = Budget.Intersect(box, gap);
        both.UpperPt.Should().BeLessThanOrEqualTo(Math.Min(box.UpperPt, gap.UpperPt));
        both.LowerPt.Should().BeGreaterThanOrEqualTo(Math.Max(box.LowerPt, gap.LowerPt) - 1e-9);
        both.PaddingPt.Should().BeLessThan(box.PaddingPt, "the point of intersecting is to narrow");
        both.Basis.Should().Contain("∩");
    }

    [Fact]
    public void ACrossingSmallerThanEpsilonIsAgreement_NotContradiction()
    {
        // The box comes from a content-stream `re` and the gap from accumulated
        // glyph advances: the same edge by different arithmetic. Measured on
        // this corpus they land 0.04pt apart, and treating that as a
        // disagreement discarded the tightest bound exactly when it was right.
        var box = Budget.FromMark(37.36, 12);                        // upper 37.36
        var gap = Budget.FromNeighbourGap(37.40, 3.34, openSides: 0); // lower 37.40 — crosses by 0.04

        var both = Budget.Intersect(box, gap);
        both.Basis.Should().Contain("∩");
        both.UpperPt.Should().Be(37.36);
        both.PaddingPt.Should().Be(0, "the two agree on a point; the interval collapses onto it");
    }

    [Fact]
    public void ARealContradictionFallsBackToTheBoxAndSaysSo()
    {
        // A gap far wider than the box means the two are not describing the same
        // span — a mark that is not where the missing text was, or neighbours on
        // a different run. Narrowing to an interval derived from a contradiction
        // would silently drop the right answer, so the loose bound wins and the
        // basis records that it did.
        var box = Budget.FromMark(37.36, 12);
        var far = Budget.FromNeighbourGap(120.0, 3.34, openSides: 0);

        var both = Budget.Intersect(box, far);
        both.Basis.Should().Contain("disagrees");
        both.UpperPt.Should().Be(box.UpperPt);
        both.PaddingPt.Should().Be(box.PaddingPt);
    }

    // ── the effect on a real document ────────────────────────────────────────

    [Fact]
    public void OnTheCorpus_TheGapNarrowsTheLeak_AndNeverDropsTheTrueAnswer()
    {
        var dir = CorpusDir();
        Assert.SkipWhen(dir == null,
            TestRepoLayout.AbsenceReason("the constructed redaction corpus", "test-pdfs/" + CorpusName));

        var cases = WidthPreservingCases(12);
        Assert.SkipWhen(cases.Count == 0, "no width-preserving cases in the corpus manifest");

        var narrowed = 0;
        var kept = 0;
        foreach (var (id, answer, _) in cases)
        {
            var bytes = File.ReadAllBytes(Path.Combine(dir!, id + ".pdf"));
            using var document = PdfDocument.Open(bytes);
            var report = RecoveryScanner
                .ScanInto(document, new RecoveryReportBuilder(), TestContext.Current.CancellationToken, Dictionary)
                .Build();

            var fit = report.Marks.Select(m => m.Fit).FirstOrDefault(f => f != null);
            fit.Should().NotBeNull($"{id}: the mark must carry a width analysis");

            // SAFETY first. A tighter bound that excludes the truth is worse
            // than no bound at all: it reads as "nothing fits" and understates
            // the leak, which is the failure direction this whole file guards.
            fit!.Candidates.Select(c => c.Text)
                .Should().Contain(answer, $"{id}: narrowing must never exclude the true answer");
            kept++;

            if (fit.WidthBasis.Contains('∩', StringComparison.Ordinal)) narrowed++;
        }

        kept.Should().Be(cases.Count);
        narrowed.Should().BeGreaterThan(cases.Count / 2,
            "the neighbour gap should be usable on most width-preserving marks; " +
            "if it is not, the intersection is falling back and the channel is inert");
    }

    [Fact]
    public void TheGapAgreesWithMutoolsOwnGlyphPositions()
    {
        // NO SELF-ORACLE. excise must not be the only witness to where the
        // surviving glyphs are — the entire budget rests on those two edges.
        Assert.SkipUnless(MutoolStextOracle.IsAvailable, "mutool is not on PATH");

        var dir = CorpusDir();
        Assert.SkipWhen(dir == null,
            TestRepoLayout.AbsenceReason("the constructed redaction corpus", "test-pdfs/" + CorpusName));

        var cases = WidthPreservingCases(6);
        Assert.SkipWhen(cases.Count == 0, "no width-preserving cases in the corpus manifest");

        var compared = 0;
        foreach (var (id, _, _) in cases)
        {
            var bytes = File.ReadAllBytes(Path.Combine(dir!, id + ".pdf"));
            using var document = PdfDocument.Open(bytes);
            var page = document.GetPage(1);
            var report = RecoveryScanner
                .ScanInto(document, new RecoveryReportBuilder(), TestContext.Current.CancellationToken, Dictionary)
                .Build();

            var mark = report.Marks.FirstOrDefault(m => m.Fit != null);
            if (mark == null) continue;

            var rect = mark.Mark.Rect.Normalize();
            var midY = (rect.Bottom + rect.Top) / 2.0;

            var words = MutoolStextOracle.Words(bytes, 1, page.Height)
                .Where(w => w.Bottom <= rect.Top && w.Top >= rect.Bottom)
                .ToList();
            if (words.Count < 2) continue;

            // mutool merges a run into a word and drops the spaces, so its
            // bracketing edges are the INK either side of the gap. excise's gap
            // runs between the surviving space glyphs, which sit inside that.
            // The two therefore bound each other rather than being equal, and
            // that is the checkable relation.
            var inkLeft = words.Where(w => w.Right <= rect.Left + 0.5).Select(w => w.Right).DefaultIfEmpty(double.NaN).Max();
            var inkRight = words.Where(w => w.Left >= rect.Right - 0.5).Select(w => w.Left).DefaultIfEmpty(double.NaN).Min();
            if (double.IsNaN(inkLeft) || double.IsNaN(inkRight)) continue;

            var mutoolGap = inkRight - inkLeft;
            mutoolGap.Should().BeGreaterThan(0, $"{id}: mutool sees ink on both sides of the mark");

            mark.Fit!.WidthPt.Should().BeLessThanOrEqualTo(mutoolGap + 0.5,
                $"{id}: excise's budget cannot exceed the room mutool says is there");
            mark.Fit.WidthPt.Should().BeGreaterThan(mutoolGap * 0.5,
                $"{id}: nor can it be a small fraction of it — that would mean the two " +
                "are measuring different spans and the agreement above is vacuous");
            compared++;
        }

        compared.Should().BeGreaterThan(0, "at least one case must actually reach the comparison");
    }
}
