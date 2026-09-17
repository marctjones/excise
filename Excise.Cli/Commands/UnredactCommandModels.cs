using System.Text.Json.Serialization;

namespace Excise.Cli.Commands;

/// <summary>
/// CLI-local delivery models. Engine assemblies retain their own evidence
/// types and do not acquire console, JSON, or System.CommandLine policy.
/// </summary>
internal sealed record UnredactCommandInput(
    string FilePath,
    string Mode,
    string? DictionaryPath,
    double Tolerance,
    int MaxCandidates,
    bool UseOcr,
    bool NoCorroboration);

internal enum UnredactMode { Certain, Residue, Both }

internal sealed record UnredactCertainFinding(
    int Page,
    string Text,
    string HiddenBy,
    double X,
    double Y,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    double? Confidence = null);

internal sealed record UnredactResidueFinding(
    int Page,
    double GapWidthPt,
    string Font,
    double SizePt,
    string MetricSource,
    int CandidatesFit,
    double ResidualEntropyBits,
    double ContextAdjustedBits,
    IReadOnlyList<string> Candidates,
    string Status);

internal sealed record UnredactQuantification(
    int Findings,
    int FullyRecoverable,
    int WidthResidueGaps,
    double WidthResidueBitsTotal,
    int Recovered,
    string Corroboration);

/// <summary>
/// #1587 — one redaction mark and how much of it came back. The DENOMINATOR of
/// the report: "12 findings" says nothing about coverage, "3 of 11 marks
/// recovered" does, and a mark nothing recovered only appears here because
/// marks are counted independently of what the channels found.
/// </summary>
internal sealed record UnredactMarkSummary(
    string Id,
    int Page,
    string Kind,
    string Description,
    IReadOnlyList<double> Rect,
    /// <summary>recovered | partially-recovered | candidates-only | not-recovered.</summary>
    string Outcome,
    int Findings,
    int CertainFindings);

/// <summary>
/// #1587 — one finding from any channel, in the shared recovery model. Kept
/// alongside the legacy Certain/Residue lists rather than replacing them: the
/// automation surface and the GUI read those, and a report is not the place to
/// break a consumer.
/// </summary>
internal sealed record UnredactModelFinding(
    string Channel,
    string Carrier,
    /// <summary>certain | candidate | present-only.</summary>
    string Confidence,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Text,
    IReadOnlyList<string> Candidates,
    double ResidualBits,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? Page,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<double>? Rect,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? LocationProvenance,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? MarkId);

/// <summary>
/// #1587 — the recovery model's view of the document: every mark with its
/// outcome, the findings that matched no mark, the ones with no page at all,
/// and which channels actually ran.
/// </summary>
/// <param name="ChannelsSkipped">
/// Channel → why it did not run. Present so a report over four channels cannot
/// be read as one over nine (the #1181 Coverage rule).
/// </param>
internal sealed record UnredactRecoveryModel(
    int Marks,
    int MarksRecovered,
    int MarksPartiallyRecovered,
    int MarksCandidatesOnly,
    int MarksNotRecovered,
    IReadOnlyList<UnredactMarkSummary> MarkSummaries,
    IReadOnlyList<UnredactModelFinding> Linked,
    IReadOnlyList<UnredactModelFinding> Unlinked,
    IReadOnlyList<UnredactModelFinding> DocumentLevel,
    IReadOnlyList<string> ChannelsRun,
    IReadOnlyDictionary<string, string> ChannelsSkipped);

internal sealed record UnredactReport(
    UnredactQuantification Quantification,
    IReadOnlyList<UnredactCertainFinding> Certain,
    IReadOnlyList<UnredactResidueFinding> Residue,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    UnredactRecoveryModel? Recovery = null);

internal sealed record UnredactCommandOutcome(
    int ExitCode,
    UnredactReport? Report,
    string? Error)
{
    public static UnredactCommandOutcome Failure(int exitCode, string error) =>
        new(exitCode, null, error);
}
