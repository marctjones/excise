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
            results.Add(ScoreExcise(pdf, caseId));
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
            results.Add(ScoreExcise(pdf, caseId));
            if (xrayAvailable) results.Add(ScoreXRay(pdf, caseId));
        }

        return new Run(cases, results, notMeasured);
    }

    /// <summary>
    /// ⚠️ WHAT COUNTS AS A DETECTION, and getting this wrong cost a run.
    ///
    /// <para>The first version of this method counted ANY finding. On the
    /// real-world negatives that scored excise at <b>42.1% specificity</b> — 33
    /// "false positives" of 57 — which looked like a serious defect and was my
    /// measurement error. Two things fire on ordinary documents that have no
    /// redaction in them at all:</para>
    /// <list type="bullet">
    ///   <item><b>/Info metadata.</b> Author, CreationDate, Keywords, Subject.
    ///   Every PDF has them, so they are excluded — unambiguous.</item>
    ///   <item><b>OCR layers.</b> One scanned filing produced 191
    ///   <c>render mode 3</c> findings. ⚠️ These are NOT excluded, and the
    ///   second attempt to exclude them is why. Gating on "no mark on the page"
    ///   took <c>text-render-mode-3</c> from 3/3 to 1/3 — that mode IS invisible
    ///   text with no mark, so the heuristic ate the thing it was meant to
    ///   distinguish from.</item>
    /// </list>
    ///
    /// <para><b>There is no reliable discriminator, and the registry already
    /// says so</b> — <c>ocr-layer-left-in-place</c> is `partial` because
    /// judging legitimate-OCR against leak is the reader's call. So the bench
    /// does not invent one. OCR-driven false positives are COUNTED and then
    /// broken out by cause in the report, because "excise flags every OCR
    /// layer" is a true and useful thing to publish, and hiding it behind a
    /// heuristic would be the bench grading around a known limitation.</para>
    ///
    /// <para>This narrows the BENCH, not excise. `excise unredact` still reports
    /// all of it, which is right — the tool tells its user everything it found
    /// and the bench asks a sharper question.</para>
    /// </summary>
    private static ToolResult ScoreExcise(byte[] pdf, string caseId)
    {
        RecoveryReport report;
        try { report = RecoveryScanner.Scan(pdf); }
        catch { return new ToolResult("excise", caseId, false); }

        var leakedMarks = report.Marks.Where(m => m.Outcome != MarkRecoveryOutcome.NotRecovered).ToList();

        // Ordinary document metadata is on every PDF ever written. This is the
        // ONLY exclusion; see the summary for the one that was tried and undone.
        static bool CountsAsALeak(RecoveredFinding f) =>
            !f.Carrier.StartsWith("/Info ", StringComparison.Ordinal);

        var counted = report.AllFindings.Where(CountsAsALeak).ToList();
        if (leakedMarks.Count == 0 && counted.Count == 0)
            return new ToolResult("excise", caseId, false);

        var best = leakedMarks.Count > 0
            ? leakedMarks.Select(m => m.Outcome).Min()   // enum order: Recovered is lowest
            : MarkRecoveryOutcome.Recovered;             // a document-level carrier is a reading

        var candidates = counted.Sum(f => f.Candidates.Count);
        var bits = counted.Where(f => f.ResidualBits > 0).Select(f => f.ResidualBits).ToList();

        return new ToolResult("excise", caseId, true, best,
            bits.Count == 0 ? 0 : bits.Average(), candidates,
            DominantCarrier(counted));
    }

    /// <summary>
    /// x-ray's verdict. Exact-or-nothing by construction — it reads characters
    /// PyMuPDF already extracted and never infers, so it has no candidate rung
    /// and the matrix must not score the absence as a weakness.
    /// </summary>
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
