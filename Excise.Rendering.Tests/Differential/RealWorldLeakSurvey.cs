using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Excise.Core.Document;
using Excise.Core.Redaction.Recovery;
using Excise.Rendering.Differential;
using Excise.TestSupport;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1590/#1602/#1603 — the bench's REAL-WORLD driver: what a document's
/// redactions did, measured POSITIONALLY.
///
/// <para><b>Why this exists separately from the tier-A bench.</b> Tier A plants
/// an answer and scores recall against it. That is impossible here: a tier-C or
/// tier-D document has no published ground truth, and for tier D there must
/// never be one. So the question changes from "did we recover the right string"
/// to "<b>did this mark leak at all, and does an independent tool agree</b>" —
/// which needs no answer key and holds no values.</para>
///
/// <para><b>The data rule is structural, not a discipline (#1602).</b> A survey
/// row has NO field that can hold recovered text. <see cref="MarkRow"/> carries
/// counts, a class and a boolean; the recovered string is read inside
/// <see cref="Survey"/>, reduced to <c>Leaks</c>, and goes out of scope. There
/// is nothing to leak into a trx, a log or an assertion message because there
/// is nowhere to put it. That is the same shape as the scorecard's value-free
/// <c>Row</c>, and it is what makes a tier-D document safe to measure at all:
/// the redactions in the DOJ Epstein release cover victim names, and this
/// survey is designed so that scoring it cannot republish one.</para>
///
/// <para><b>What it cannot tell you.</b> Leak COUNTS are not recall. A document
/// where excise finds nothing may be properly redacted (Manafort 472) or may be
/// one excise cannot read (#637's bound: redaction completeness is bounded by
/// extraction coverage, and it reports success either way). That is exactly why
/// every row carries <see cref="MarkRow.XRayAgrees"/> — a second, independent
/// implementation, because excise confirming excise proves only that its bugs
/// are self-consistent.</para>
/// </summary>
internal static class RealWorldLeakSurvey
{
    /// <summary>Where the bench's real-world documents live once fetched.</summary>
    public static string? CorpusDirectory =>
        TestRepoLayout.FindDirectory(Path.Combine("test-pdfs", "unredaction-bench"));

    /// <summary>The tracked manifest — ids, tiers and vetting, never values.</summary>
    public static string? ManifestPath =>
        TestRepoLayout.FindFile("tests", "unredaction-bench", "manifest.tsv");

    /// <summary>A manifest row, restricted to the columns a survey needs.</summary>
    internal sealed record Document(string Id, string Tier, string Status)
    {
        /// <summary>The local file, or null when it has not been fetched.</summary>
        public string? Path(string corpusDirectory)
        {
            var p = System.IO.Path.Combine(corpusDirectory, Id + ".pdf");
            return File.Exists(p) ? p : null;
        }
    }

    /// <summary>
    /// One mark's verdict. ⚠️ Every field here is a count, a class or a flag.
    /// Adding a string field that can hold document text breaks #1602 and
    /// <c>SurveyRowsHoldNoRecoveredText</c> fails.
    /// </summary>
    internal sealed record MarkRow(
        string DocumentId,
        string Tier,
        string MarkId,
        int Page,
        RedactionMarkKind Kind,
        MarkRecoveryOutcome Outcome,
        bool Leaks,
        int FindingCount,
        int CertainFindingCount,
        IReadOnlyList<string> Channels,
        bool? XRayAgrees);

    /// <summary>Every vetted manifest row, whether or not it has been fetched.</summary>
    public static IReadOnlyList<Document> Documents()
    {
        var path = ManifestPath;
        if (path == null) return Array.Empty<Document>();

        var rows = new List<Document>();
        foreach (var line in File.ReadAllLines(path))
        {
            if (line.StartsWith('#') || line.Length == 0) continue;
            var f = line.Split('\t');
            if (f.Length < 9) continue;
            rows.Add(new Document(f[0], f[1], f[2]));
        }
        return rows;
    }

    /// <summary>
    /// Scan one document and reduce each mark to a value-free verdict.
    ///
    /// <para><paramref name="xray"/> supplies the independent opinion: the
    /// boxes x-ray reports as bad redactions. A mark counts as corroborated
    /// when one of them overlaps it. Null when x-ray is unavailable, which is
    /// reported as "not corroborated", never as agreement.</para>
    /// </summary>
    public static IReadOnlyList<MarkRow> Survey(Document document, string pdfPath)
    {
        // #1665: the BYTE overload, so the prior-revision channel actually runs.
        // This used to call Scan(PdfDocument), which silently omitted it — the
        // bench's own real-world driver was blind to a registered mode.
        var report = RecoveryScanner.Scan(File.ReadAllBytes(pdfPath));

        var xrayHits = XRayBadRedactionDetector.Inspect(pdfPath);

        var rows = new List<MarkRow>(report.Marks.Count);
        foreach (var mark in report.Marks)
        {
            // The ONLY place a recovered string is touched. It is tested for
            // existence and discarded; nothing below this line can see it.
            var leaks = mark.Findings.Count > 0;

            rows.Add(new MarkRow(
                document.Id,
                document.Tier,
                mark.Mark.Id,
                mark.Mark.PageNumber,
                mark.Mark.Kind,
                mark.Outcome,
                leaks,
                mark.Findings.Count,
                mark.Findings.Count(f => f.Confidence == RecoveryConfidence.Certain),
                mark.Findings.Select(f => f.Channel).Distinct().OrderBy(c => c, StringComparer.Ordinal).ToList(),
                xrayHits == null ? null : xrayHits.Any(h => Overlaps(h, mark.Mark))));
        }
        return rows;
    }

    /// <summary>
    /// Do an x-ray hit and a excise mark refer to the same place on the page?
    ///
    /// <para>Deliberately generous: the two tools box different things — x-ray
    /// reports the TEXT it can read under a covering, excise reports the
    /// COVERING — so requiring tight agreement would score a convention
    /// difference as a disagreement. Any overlap on the same page counts.</para>
    /// </summary>
    private static bool Overlaps(XRayBadRedactionDetector.BadRedaction hit, RedactionMark mark)
    {
        if (hit.Page != mark.PageNumber) return false;

        var (l, r) = (Math.Min(hit.X0, hit.X1), Math.Max(hit.X0, hit.X1));
        var (b, t) = (Math.Min(hit.Y0, hit.Y1), Math.Max(hit.Y0, hit.Y1));
        var rect = mark.Rect.Normalize();

        return l <= rect.Right && r >= rect.Left && b <= rect.Top && t >= rect.Bottom;
    }

    /// <summary>
    /// The survey as text: leak class distribution and excise-vs-x-ray
    /// agreement. Mirrors the tier-A scorecard's habit of naming what was NOT
    /// measured, so a survey over one document cannot read like a corpus.
    /// </summary>
    public static string Render(IReadOnlyList<MarkRow> rows, IReadOnlyList<string> notMeasured)
    {
        var sb = new StringBuilder();
        sb.AppendLine("═══ REAL-WORLD LEAK SURVEY (#1590) — positional: does the mark leak, never what ═══");

        if (notMeasured.Count > 0)
            sb.AppendLine("⚠ NOT measured: " + string.Join("; ", notMeasured));

        if (rows.Count == 0)
        {
            sb.AppendLine("  no documents present — nothing measured");
            return sb.ToString();
        }

        foreach (var doc in rows.GroupBy(r => (r.DocumentId, r.Tier)).OrderBy(g => g.Key.DocumentId, StringComparer.Ordinal))
        {
            var marks = doc.ToList();
            var leaking = marks.Count(m => m.Leaks);
            sb.AppendLine();
            sb.AppendLine($"  {doc.Key.DocumentId}  [tier {doc.Key.Tier}]");
            sb.AppendLine($"    marks {marks.Count}   leaking {leaking}   held {marks.Count - leaking}");

            foreach (var kind in marks.GroupBy(m => m.Kind).OrderBy(g => g.Key.ToString(), StringComparer.Ordinal))
                sb.AppendLine($"      kind {kind.Key,-22} {kind.Count(m => m.Leaks)}/{kind.Count()} leaking");

            var channels = marks.SelectMany(m => m.Channels).GroupBy(c => c)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal);
            foreach (var channel in channels)
                sb.AppendLine($"      channel {channel.Key,-19} {channel.Count()} findings");

            var corroborated = marks.Where(m => m.XRayAgrees != null).ToList();
            sb.AppendLine(corroborated.Count == 0
                ? "      x-ray: not available — NOT corroborated"
                : $"      x-ray agrees on {corroborated.Count(m => m.XRayAgrees == true)}/{corroborated.Count} marks");
        }

        return sb.ToString();
    }
}
