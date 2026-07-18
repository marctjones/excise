using System;
using System.Collections.Generic;
using System.IO;
using Excise.Core.Document;
using Excise.Rendering.Differential;

namespace Excise.Ocr;

/// <summary>Per-page or whole-document verdict from <see cref="RedactionConfidenceChecker"/>.</summary>
public enum RedactionConfidenceTier
{
    /// <summary>Oracle agrees closely with excise's own extraction (or nothing to compare).</summary>
    Ok,

    /// <summary>Oracle disagrees somewhat — proceed, but the caller should surface a warning.</summary>
    Degraded,

    /// <summary>
    /// Oracle finds substantially more/different text than excise extracted —
    /// the same signature as a real redaction leak (#637's shape). Callers
    /// should refuse by default and require an explicit override.
    /// </summary>
    Severe,

    /// <summary>
    /// No independent oracle (mutool, tesseract) was available, or this
    /// specific page's oracle call failed — could not verify. Not itself
    /// evidence of a problem, but must never be silently reported as if it
    /// were a verified "Ok".
    /// </summary>
    Unverified,
}

/// <summary>Confidence result for one page.</summary>
public sealed record RedactionConfidencePageResult(
    int PageNumber,
    RedactionConfidenceTier Tier,
    double? CoverageRatio,
    double? Similarity);

/// <summary>Whole-document confidence report: the worst page decides the overall tier.</summary>
public sealed record RedactionConfidenceReport(
    RedactionConfidenceTier Tier,
    string? Oracle,
    IReadOnlyList<RedactionConfidencePageResult> Pages)
{
    public bool ShouldWarn => Tier is RedactionConfidenceTier.Degraded or RedactionConfidenceTier.Unverified;
    public bool ShouldRefuse => Tier == RedactionConfidenceTier.Severe;
}

/// <summary>
/// Compares excise's own text extraction against an independent oracle
/// (mutool if on PATH, else tesseract OCR if on PATH) for every page of a
/// document, before <c>RedactText</c> mutates it (#650). This is the
/// runtime counterpart to the release-time corpus gate in
/// <c>ExtractionParityTests</c> — same math (<see cref="TextSimilarity"/>),
/// applied to one specific document instead of a fixed corpus baseline.
/// </summary>
/// <remarks>
/// Neither oracle can be a hard requirement for ordinary redaction: mutool
/// is AGPL and can only ever be invoked as an external subprocess (see
/// LICENSES.md — never bundled), and tesseract is a genuinely optional
/// install most users don't have. When neither is available the whole
/// document reports <see cref="RedactionConfidenceTier.Unverified"/> rather
/// than throwing — callers decide what to do with that (the GUI/CLI
/// default is "proceed, but say so"; an explicit strict mode can refuse).
/// </remarks>
public sealed class RedactionConfidenceChecker
{
    /// <summary>Below this on either metric: same "catastrophic under-extraction" bar #651 uses for the corpus gate.</summary>
    public const double SevereThreshold = 0.5;

    /// <summary>At or above this on both metrics: no warning needed.</summary>
    public const double OkThreshold = 0.9;

    /// <summary>Oracle text shorter than this (letters/digits) is too noisy to classify — mirrors <c>ExtractionParityTests.MinReferenceLength</c>.</summary>
    public const int MinOracleLength = 32;

    private readonly PdfOcrService _ocr;

    public RedactionConfidenceChecker(PdfOcrService? ocr = null)
    {
        _ocr = ocr ?? new PdfOcrService();
    }

    /// <summary>
    /// Check every page of <paramref name="document"/> against the best
    /// available oracle. Must be called before any page is mutated — excise's
    /// own extraction (<see cref="PdfPage.Text"/>) is read live from the
    /// document, and a mutool oracle needs a byte snapshot from before any
    /// mutation.
    /// </summary>
    /// <param name="document">The document to check, not yet mutated.</param>
    /// <param name="sourceFilePath">
    /// Path to the exact, unmutated bytes <paramref name="document"/> was
    /// opened from, if any — both real call sites (GUI and CLI) always
    /// have this, since <c>RedactText</c> is invoked with an input file
    /// path before any redaction touches the document. When given, this is
    /// handed straight to mutool instead of re-serializing the document via
    /// <see cref="PdfDocument.SaveToBytes"/>, which measured 3-7s alone on
    /// a 126-page real-world document — pure waste when the original bytes
    /// are already sitting on disk. Falls back to a fresh
    /// <see cref="PdfDocument.SaveToBytes"/> snapshot when null (e.g. a
    /// document built or already mutated in memory with no backing file).
    /// </param>
    public RedactionConfidenceReport CheckDocument(PdfDocument document, string? sourceFilePath = null)
    {
        if (document == null) throw new ArgumentNullException(nameof(document));

        var useMutool = MutoolReferenceRenderer.IsAvailable;
        var useOcr = !useMutool && _ocr.IsAvailable();

        if (!useMutool && !useOcr)
            return new RedactionConfidenceReport(RedactionConfidenceTier.Unverified, Oracle: null, Pages: Array.Empty<RedactionConfidencePageResult>());

        var pageCount = document.PageCount;

        // excise's own extraction reads through the shared PdfDocument/page
        // cache — collected sequentially, on this thread, before any
        // parallel oracle work starts. Cheap relative to the oracle call
        // (no subprocess), and avoids any question of whether PdfDocument
        // is safe to touch from multiple threads at once.
        var exciseTexts = new string[pageCount];
        for (int i = 0; i < pageCount; i++)
        {
            try { exciseTexts[i] = document.GetPage(i + 1).Text ?? ""; }
            catch { exciseTexts[i] = ""; }
        }

        string?[] oracleTexts;
        string? tempPdfPath = null;
        try
        {
            if (useMutool)
            {
                // Prefer the caller's own unmutated file over re-saving —
                // see the sourceFilePath doc comment above for why this
                // matters. Only fall back to a fresh snapshot when no
                // on-disk original is available.
                var mutoolInputPath = sourceFilePath;
                if (mutoolInputPath == null)
                {
                    tempPdfPath = Path.Combine(Path.GetTempPath(), $"excise-confidence-{Guid.NewGuid():N}.pdf");
                    File.WriteAllBytes(tempPdfPath, document.SaveToBytes());
                    mutoolInputPath = tempPdfPath;
                }

                // One mutool invocation for the whole page range — not one
                // process per page. Per-process spawn overhead dominates
                // at realistic page counts (measured ~8.7s total for a
                // 126-page document vs. ~35s spawning one process per
                // page), so this beats parallelizing many small calls too.
                oracleTexts = MutoolTextExtractor.ExtractAllPages(mutoolInputPath, pageCount) ?? new string?[pageCount];
            }
            else
            {
                // Tesseract has no equivalent "whole document in one call"
                // mode (it OCRs one rendered raster at a time) and each
                // call renders through the live PdfDocument — sequential,
                // for a fallback path that's rarer and already the slower
                // of the two oracles per page.
                oracleTexts = new string?[pageCount];
                for (int i = 0; i < pageCount; i++)
                    oracleTexts[i] = TryOcr(document.GetPage(i + 1));
            }
        }
        finally
        {
            if (tempPdfPath != null)
            {
                try { File.Delete(tempPdfPath); } catch { }
            }
        }

        var pages = new List<RedactionConfidencePageResult>(pageCount);
        for (int i = 0; i < pageCount; i++)
            pages.Add(ClassifyPage(i + 1, exciseTexts[i], oracleTexts[i]));

        var overall = WorstTier(pages);
        return new RedactionConfidenceReport(overall, useMutool ? "mutool" : "tesseract", pages);
    }

    private string? TryOcr(PdfPage page)
    {
        try { return _ocr.RecognizePage(page).Text; }
        catch { return null; } // a single page's OCR failure shouldn't take down the whole check
    }

    /// <summary>
    /// The whole-document verdict from a set of per-page results: worst
    /// wins, ranked Severe &gt; Degraded &gt; Unverified &gt; Ok. A page this
    /// document's oracle call itself failed on (per-page Unverified) is not
    /// silently folded into an otherwise-clean Ok result — "couldn't check
    /// this page" is itself something the caller should see, same family as
    /// "couldn't check anything." Exposed publicly for direct testing.
    /// </summary>
    public static RedactionConfidenceTier WorstTier(IEnumerable<RedactionConfidencePageResult> pages)
    {
        var worst = RedactionConfidenceTier.Ok;
        foreach (var p in pages)
        {
            if (Rank(p.Tier) > Rank(worst)) worst = p.Tier;
        }
        return worst;

        static int Rank(RedactionConfidenceTier t) => t switch
        {
            RedactionConfidenceTier.Severe => 3,
            RedactionConfidenceTier.Degraded => 2,
            RedactionConfidenceTier.Unverified => 1,
            _ => 0,
        };
    }

    /// <summary>
    /// Pure classification: given excise's own extraction and an oracle's for
    /// the same page, decide the tier. Exposed publicly (no PDF/oracle I/O)
    /// so callers and tests can classify without going through
    /// <see cref="CheckDocument"/>'s full pipeline.
    /// </summary>
    public static RedactionConfidencePageResult ClassifyPage(int pageNumber, string exciseText, string? oracleText)
    {
        if (oracleText == null)
            return new RedactionConfidencePageResult(pageNumber, RedactionConfidenceTier.Unverified, null, null);

        var oracleLength = TextSimilarity.Normalize(oracleText).Length;
        if (oracleLength < MinOracleLength)
            return new RedactionConfidencePageResult(pageNumber, RedactionConfidenceTier.Ok, null, null);

        var coverage = TextSimilarity.CoverageRatio(exciseText, oracleText);
        var similarity = TextSimilarity.BigramJaccard(exciseText, oracleText);
        var worst = Math.Min(coverage, similarity);

        var tier = worst < SevereThreshold ? RedactionConfidenceTier.Severe
            : worst < OkThreshold ? RedactionConfidenceTier.Degraded
            : RedactionConfidenceTier.Ok;

        return new RedactionConfidencePageResult(pageNumber, tier, coverage, similarity);
    }
}
