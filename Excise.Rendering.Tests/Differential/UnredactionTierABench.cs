using System;
using System.Collections.Generic;
using System.Linq;
using Excise.Core.Document;
using Excise.Core.Redaction.Recovery;
using Excise.Core.Tests.Redaction.Recovery;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1590 tier A — synthetic redactions with EXACT ground truth, one per failure
/// mode in the registry.
///
/// <para><b>Generated at run time, never stored.</b> Tier A needs no corpus and
/// no download: each fixture is built here with a planted answer, so the bench
/// runs identically on a cloud container and on a fully provisioned machine.
/// That is what makes it the regression tier — tiers B/C/D tell us whether the
/// failure matters on documents people have, tier A tells us whether the
/// mechanism still works at all.</para>
///
/// <para><b>Scoring happens in memory and the planted answer never leaves this
/// class (#1602).</b> A case yields a <see cref="UnredactionScorecard.Row"/>,
/// which carries a bool and a bit count and has no field a string could hide
/// in. The tier-A answers are invented ("MANAFORT", "HARPER") and carry no
/// risk themselves, but the harness that scores tier B must not have a
/// different shape from the one that scores tier A — a value-carrying row
/// added here would be inherited there.</para>
/// </summary>
internal static class UnredactionTierABench
{
    /// <param name="AxisId">The failure-mode id this case exercises.</param>
    /// <param name="Recovered">Did any channel return the planted answer.</param>
    /// <param name="Channel">Which channel found it, or null.</param>
    /// <param name="Buildable">
    /// False when no synthetic fixture exists for this mode yet. Distinct from
    /// "ran and found nothing": a mode we cannot even pose a case for is a hole
    /// in the BENCH, not a measurement of excise.
    /// </param>
    internal sealed record Outcome(string AxisId, bool Recovered, string? Channel, bool Buildable);

    /// <summary>Run every axis that has a fixture; report the ones that do not.</summary>
    public static IReadOnlyList<Outcome> Run()
    {
        var results = new List<Outcome>();
        foreach (var axis in UnredactionBenchAxes.All)
        {
            var fixture = FixtureFor(axis.Id);
            if (fixture == null)
            {
                results.Add(new Outcome(axis.Id, false, null, Buildable: false));
                continue;
            }

            var (bytes, answer) = fixture.Value;
            var (recovered, channel) = TryRecover(bytes, answer);
            results.Add(new Outcome(axis.Id, recovered, channel, Buildable: true));
        }
        return results;
    }

    /// <summary>Scorecard rows for the whole tier, one per axis.</summary>
    public static IReadOnlyList<UnredactionScorecard.Row> Score(IEnumerable<Outcome> outcomes)
        => outcomes
            .Where(o => o.Buildable)
            .Select(o => new UnredactionScorecard.Row(
                Channel: o.Channel ?? ChannelOf(o.AxisId) ?? "(none)",
                Stratum: o.AxisId,
                Tool: "excise",
                Recovered: o.Recovered))
            .ToList();

    private static string? ChannelOf(string axisId)
        => UnredactionBenchAxes.All.FirstOrDefault(a => a.Id == axisId)?.Channel;

    /// <summary>
    /// Run every document-only channel and say whether the planted answer came
    /// back. Deliberately asks "did ANY channel find it" rather than "did the
    /// expected channel find it": a mode recovered by an unexpected channel is
    /// still recovered, and pinning the channel here would make the bench a
    /// test of our own routing instead of a measurement of recovery.
    /// </summary>
    private static (bool Recovered, string? Channel) TryRecover(byte[] bytes, string answer)
    {
        // The prior-revision channel needs the file's literal bytes, so it runs
        // separately from the document-only scan.
        try
        {
            var (priorFindings, _) = PriorRevisionRecovery.Scan(bytes);
            if (priorFindings.Any(f => f.Text.Contains(answer, StringComparison.Ordinal)))
                return (true, RecoveryScanner.Channels.PriorRevision);
        }
        catch { /* a fixture that will not re-parse scores as not recovered */ }

        try
        {
            using var document = PdfDocument.Open(bytes);
            // #1690: the bench MEASURES every channel, including the deferred
            // ones — deferring them from the product's headline must not stop
            // them being exercised, or they rot. The tier split happens in the
            // scorecard's rendering, where the graded total is computed; a
            // scan that omitted them would leave the Tier 2 axes unable to
            // score anything but zero, which is indistinguishable from a
            // regression.
            var report = RecoveryScanner.Scan(document, options: RecoveryScanOptions.IncludingDeferred);
            var hit = report.AllFindings.FirstOrDefault(f =>
                (f.Text != null && f.Text.Contains(answer, StringComparison.Ordinal)) ||
                f.Candidates.Any(c => c.Contains(answer, StringComparison.Ordinal)) ||
                f.Carrier.Contains(answer, StringComparison.Ordinal));
            return hit != null ? (true, hit.Channel) : (false, null);
        }
        catch { return (false, null); }
    }

    /// <summary>
    /// The synthetic case for one failure mode: the bytes, and the answer a
    /// working channel must return. Null where no fixture exists yet — the
    /// bench reports that as a bench hole rather than a zero score.
    /// </summary>
    private static (byte[] Bytes, string Answer)? FixtureFor(string axisId) => axisId switch
    {
        "box-over-intact-text" =>
            (RecoveryFixtureBuilder.TextUnderBox("MANAFORT"), "MANAFORT"),

        "box-drawn-by-annotation" =>
            (RecoveryFixtureBuilder.TextUnderSquareAnnotation("ANNOTCOVERED"), "ANNOTCOVERED"),

        "text-render-mode-3" =>
            (RecoveryFixtureBuilder.InvisibleText("INVISIBLESECRET"), "INVISIBLESECRET"),

        "redact-annotation-unapplied" =>
            (RecoveryFixtureBuilder.UnappliedRedactAnnotation("CONFIDENTIAL"), "CONFIDENTIAL"),

        "structure-tree-carrier" or "marked-content-carrier" =>
            (RecoveryFixtureBuilder.MarkedContentCarrierUnderBox("HARPER"), "HARPER"),

        "leftover-form-value" =>
            (RecoveryFixtureBuilder.FormFieldValueUnderBox("ssn", "123-45-6789"), "123-45-6789"),

        "leftover-page-thumbnail" =>
            (RecoveryFixtureBuilder.PageWithThumbnail(), "/Thumb"),

        "leftover-embedded-file" =>
            (RecoveryFixtureBuilder.PageWithAttachment("secret-notes.txt"), "secret-notes.txt"),

        "incremental-update-prior-revision" =>
            (RecoveryFixtureBuilder.IncrementalUpdate(
                 RecoveryFixtureBuilder.TextUnderBox("PRIORSECRET", drawBox: false),
                 "BT /F1 14 Tf 72 700 Td (REDACTED) Tj ET\n"),
             "PRIORSECRET"),

        // Modes with no synthetic fixture yet. Listed explicitly rather than
        // falling through, so adding a mode to the registry forces a decision
        // here instead of silently landing in this bucket.
        "box-inside-form-xobject" => null,          // #1606
        "box-light-or-low-contrast" => null,
        "ocr-layer-left-in-place" => null,          // needs a raster + tesseract
        "partial-glyph-removal-kerning" => null,    // needs the residue engine; #1589
        "image-covered-only" => null,               // covered, but the fixture asserts presence not text
        "vector-covered-only" => null,
        "image-original-object-retained" => null,   // #1608
        "image-smask-trick" => null,                // #1608
        "leftover-xfa" => null,                     // #1609
        "leftover-document-metadata" => null,       // deliberate non-coverage
        _ => null,
    };
}
