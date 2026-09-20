using System.Globalization;
using Excise.Core.Redaction.Recovery;

namespace Excise.Cli.Commands;

/// <summary>
/// #1587 — projects the engine's <see cref="RecoveryReport"/> onto the CLI's
/// delivery models. The engine keeps its own typed evidence and acquires no
/// JSON or console policy; the CLI keeps its own shapes and acquires no
/// recovery logic.
/// </summary>
internal static class UnredactRecoveryMapper
{
    public static UnredactRecoveryModel Map(RecoveryReport report)
    {
        var markSummaries = new List<UnredactMarkSummary>(report.Marks.Count);
        var linked = new List<UnredactModelFinding>();
        foreach (var summary in report.Marks)
        {
            markSummaries.Add(new UnredactMarkSummary(
                summary.Mark.Id,
                summary.Mark.PageNumber,
                KindOf(summary.Mark.Kind),
                summary.Mark.Description,
                Rect(summary.Mark.Rect),
                OutcomeOf(summary.Outcome),
                summary.Findings.Count,
                summary.Findings.Count(f => f.Confidence == RecoveryConfidence.Certain),
                summary.Fit is { } fit
                    ? new UnredactMarkFit(
                        fit.WidthPt, fit.WidthBasis,
                        fit.MinCharacters, fit.MaxCharacters, fit.PatternClasses,
                        fit.Candidates.Take(10).Select(c => c.Text).ToList(),
                        fit.Candidates.Count, fit.CandidatesConsidered,
                        fit.BitsLeaked, fit.Confidence, fit.MetricNote)
                    : null));
            linked.AddRange(summary.Findings.Select(Finding));
        }

        return new UnredactRecoveryModel(
            report.MarkCount,
            report.MarksRecovered,
            report.MarksPartial,
            report.MarksCandidatesOnly,
            report.MarksContentSurvives,
            report.MarksNotRecovered,
            markSummaries,
            linked,
            report.Unlinked.Select(Finding).ToList(),
            report.DocumentLevel.Select(Finding).ToList(),
            report.ChannelsRun,
            report.ChannelsSkipped);
    }

    private static UnredactModelFinding Finding(RecoveredFinding finding) => new(
        finding.Channel,
        TierOf(finding.Channel),
        finding.Carrier,
        ConfidenceOf(finding.Confidence),
        finding.Text,
        finding.Candidates,
        Math.Round(finding.ResidualBits, 2),
        finding.Location?.PageNumber,
        finding.Location is { } location ? Rect(location.Rect) : null,
        finding.Location?.Provenance,
        finding.MarkId);

    private static IReadOnlyList<double> Rect(Core.Document.PdfRectangle rect)
    {
        var r = rect.Normalize();
        return new[]
        {
            Math.Round(r.Left, 2), Math.Round(r.Bottom, 2),
            Math.Round(r.Right, 2), Math.Round(r.Top, 2),
        };
    }

    // Stable wire vocabulary. Deliberately not ToString() on the enum: renaming
    // a C# member must not silently change a JSON contract someone scripts
    // against.
    internal static string ConfidenceOf(RecoveryConfidence confidence) => confidence switch
    {
        RecoveryConfidence.Certain => "certain",
        RecoveryConfidence.Candidate => "candidate",
        RecoveryConfidence.PresentOnly => "present-only",
        _ => "unknown",
    };

    /// <summary>
    /// #1690 — the finding's tier on the wire. Read from the one authority
    /// (<see cref="RecoveryChannelTiers"/>) rather than a list here, so a
    /// channel cannot be Tier 1 to the engine and Tier 2 to the report.
    /// </summary>
    internal static string TierOf(string channel) =>
        RecoveryChannelTiers.TierOf(channel) switch
        {
            RecoveryTier.Deferred => "deferred",
            _ => "text",
        };

    internal static string OutcomeOf(MarkRecoveryOutcome outcome) => outcome switch
    {
        MarkRecoveryOutcome.Recovered => "recovered",
        MarkRecoveryOutcome.PartiallyRecovered => "partially-recovered",
        MarkRecoveryOutcome.CandidatesOnly => "candidates-only",
        MarkRecoveryOutcome.ContentSurvives => "content-survives",
        MarkRecoveryOutcome.NotRecovered => "not-recovered",
        _ => "unknown",
    };

    internal static string KindOf(RedactionMarkKind kind) => kind switch
    {
        RedactionMarkKind.FilledBox => "filled-box",
        RedactionMarkKind.RedactAnnotation => "redact-annotation",
        RedactionMarkKind.ShapeAnnotation => "shape-annotation",
        RedactionMarkKind.FormXObjectBox => "form-xobject-box",
        RedactionMarkKind.EmptiedRegion => "emptied-region",
        _ => "unknown",
    };
}
