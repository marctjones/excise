using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Excise.Core.Redaction.Recovery;
using Excise.Core.Tests.Redaction.Recovery;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using static Excise.Rendering.Tests.Differential.UnredactionConfusionMatrix;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1645 — runs excise AND x-ray across the fixture corpus and the real-world
/// negatives, and produces the matrix. This is the part that turns typed rows
/// into numbers.
///
/// <para><b>Two populations, and the second is the one that was missing.</b>
/// The fixtures supply leaks with known ground truth. The RECAP negatives
/// supply documents that leak NOTHING — 113 real court filings the sweep
/// classified clean. Without them a tool that answers yes to everything scores
/// perfect recall, which is exactly how #1624 shipped.</para>
///
/// <para><b>Detection is "did it report anything on this document".</b> Not
/// "did it get the right string" — that is the recovery ladder's question, and
/// conflating the two is what makes a single number meaningless.</para>
/// </summary>
internal static class UnredactionMatrixRun
{
    internal sealed record Run(
        IReadOnlyList<Case> Cases,
        IReadOnlyList<ToolResult> Results,
        IReadOnlyList<string> NotMeasured);

    /// <summary>The negatives the RECAP sweep recorded, re-fetched if present.</summary>
    private static IReadOnlyList<string> NegativeFiles()
    {
        var dir = TestRepoLayout.FindDirectory(Path.Combine("test-pdfs", "recap", "negatives"));
        return dir == null
            ? Array.Empty<string>()
            : Directory.GetFiles(dir, "*.pdf").OrderBy(f => f, StringComparer.Ordinal).ToList();
    }

    public static Run Execute()
    {
        var cases = new List<Case>();
        var results = new List<ToolResult>();
        var notMeasured = new List<string>();

        var xrayAvailable = XRayBadRedactionDetector.IsAvailable;
        if (!xrayAvailable) notMeasured.Add("x-ray not installed — only excise scored");

        var corpusBacked = FailureModeFixtures.CorpusBackedModes();
        if (corpusBacked.Count > 0)
            notMeasured.Add($"{corpusBacked.Count} mode(s) live in the synthetic corpus and are " +
                            $"not built here: {string.Join(", ", corpusBacked)}");

        // ── the fixture corpus: leaks (and a few deliberate negatives) ──────
        foreach (var (modeId, variant) in FailureModeFixtures.Buildable())
        {
            var caseId = $"{modeId}/{variant.Name}";
            byte[] pdf;
            try { pdf = variant.Build!(); }
            catch (Exception ex)
            {
                // A fixture that will not build is a finding, not a silent zero.
                notMeasured.Add($"{caseId} failed to build: {ex.GetType().Name}");
                continue;
            }

            cases.Add(new Case(modeId, caseId, variant.ContainsLeak));
            results.Add(ScoreExcise(pdf, caseId, UnredactionBenchAxes.IsDeferredMode(modeId)));
            if (xrayAvailable) results.Add(ScoreXRay(pdf, caseId));
        }

        // ── the real-world negatives ────────────────────────────────────────
        var negatives = NegativeFiles();
        if (negatives.Count == 0)
        {
            notMeasured.Add(
                "NO REAL-WORLD NEGATIVES — run scripts/download-recap-corpus.sh --fetch-negatives. " +
                "Precision and specificity below rest on the fixture negatives alone, which test " +
                "what we already believe (#1624 came from an idiom nobody would synthesise)");
        }

        foreach (var path in negatives)
        {
            // Every real-world negative is filed under one synthetic MODE so it
            // lands in a cell: `real-world-negative`. Attributing them to the
            // mode a tool happened to fire on would let the tool choose its own
            // denominator.
            var caseId = NegativeMode + "/" + Path.GetFileNameWithoutExtension(path);
            cases.Add(new Case(NegativeMode, caseId, ContainsLeak: false));

            byte[] pdf;
            try { pdf = File.ReadAllBytes(path); } catch { continue; }
            // ⚠️ #1690 — the NEGATIVES are scored at the PRODUCT'S DEFAULT, never
            // with the deferred channels on. RecoveryReportBuilder.Outcome()
            // returns CandidatesOnly, not NotRecovered, for a mark holding ANY
            // finding — present-only included — so running the image channels
            // over 113 clean filings would manufacture false positives the
            // shipped command does not produce, and report excise's specificity
            // as worse than it is.
            results.Add(ScoreExcise(pdf, caseId, includeDeferred: false));
            if (xrayAvailable) results.Add(ScoreXRay(pdf, caseId));
        }

        return new Run(cases, results, notMeasured);
    }

    /// <summary>
    /// ⚠️ WHAT COUNTS AS A DETECTION — and this now asks the PRODUCT, not the
    /// bench.
    ///
    /// <para>Detection is "did excise report something that indicates a FAILED
    /// REDACTION", which <see cref="RecoveryFindingClassifier"/> decides.
    /// Furniture — /Info, XMP, link /URI targets, /PieceInfo, an OCR layer on a
    /// page with no mark — is still REPORTED by the tool and is not counted
    /// here.</para>
    ///
    /// <para><b>The history matters, because the bench nearly grew its own
    /// answer.</b> Counting any finding scored 42.1% specificity. Excluding
    /// /Info by hand got 50.9%. Then excluding OCR-with-no-mark by hand took
    /// <c>text-render-mode-3</c> from 3/3 to 1/3 — the heuristic ate the mode it
    /// was meant to distinguish from. At that point the bench was accumulating
    /// private judgements about what counts as a leak, which is exactly the
    /// thing a bench must not do: it would have been grading excise against
    /// rules excise does not follow.</para>
    ///
    /// <para>So the judgement moved into the product (#1669) where a user
    /// benefits from it, and the bench reads it. If the classification is
    /// wrong, the tool is wrong and the tool's tests say so — rather than the
    /// bench quietly disagreeing with the thing it measures.</para>
    /// </summary>
    /// <param name="includeDeferred">
    /// #1690 — run the Tier 2 channels for this case. True only for a case
    /// whose own failure MODE is deferred: that mode has to keep being
    /// measured (a permanent zero is indistinguishable from a regression), and
    /// the confusion matrix keeps its row out of the graded total. Every other
    /// case — and every real-world negative — is scored at the product's
    /// default, because that is what the command a user runs actually does.
    /// </param>
    private static ToolResult ScoreExcise(byte[] pdf, string caseId, bool includeDeferred)
    {
        RecoveryReport report;
        try
        {
            report = RecoveryScanner.Scan(
                pdf,
                options: includeDeferred
                    ? RecoveryScanOptions.IncludingDeferred
                    : RecoveryScanOptions.Default);
        }
        catch { return new ToolResult("excise", caseId, false); }

        var counted = report.AllFindings
            .Where(f => RecoveryFindingClassifier.IndicatesAFailedRedaction(
                RecoveryFindingClassifier.Classify(f)))
            .ToList();
        var leakedMarks = report.Marks.Where(m => m.Outcome != MarkRecoveryOutcome.NotRecovered).ToList();

        if (leakedMarks.Count == 0 && counted.Count == 0)
            return new ToolResult("excise", caseId, false);

        var best = leakedMarks.Count > 0
            ? leakedMarks.Select(m => m.Outcome).Min()   // enum order: Recovered is lowest
            : MarkRecoveryOutcome.Recovered;             // a content carrier is a reading

        var candidates = counted.Sum(f => f.Candidates.Count);
        var bits = counted.Where(f => f.ResidualBits > 0).Select(f => f.ResidualBits).ToList();

        return new ToolResult("excise", caseId, true, best,
            bits.Count == 0 ? 0 : bits.Average(), candidates,
            DominantCarrier(counted));
    }

    /// <summary>
    /// The carrier most of a document's findings came through — what a reader
    /// would blame. Reported so a false-positive column can be broken down by
    /// CAUSE instead of being one discouraging number.
    /// </summary>
    private static string? DominantCarrier(IReadOnlyList<RecoveredFinding> findings)
    {
        if (findings.Count == 0) return null;
        var top = findings.GroupBy(f =>
                f.Carrier.Contains("render mode 3", StringComparison.Ordinal) ? "OCR layer (render mode 3)"
                : f.Carrier.StartsWith("low-contrast", StringComparison.Ordinal) ? "low-contrast text"
                : f.Channel)
            .OrderByDescending(g => g.Count()).First();
        return $"{top.Key} ({top.Count()})";
    }

    private static ToolResult ScoreXRay(byte[] pdf, string caseId)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"excise-matrix-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(tmp, pdf);
            var hits = XRayBadRedactionDetector.Inspect(tmp);
            return hits is { Count: > 0 }
                ? new ToolResult("xray", caseId, true, MarkRecoveryOutcome.Recovered)
                : new ToolResult("xray", caseId, false);
        }
        catch { return new ToolResult("xray", caseId, false); }
        finally { try { File.Delete(tmp); } catch { /* best effort */ } }
    }
}
