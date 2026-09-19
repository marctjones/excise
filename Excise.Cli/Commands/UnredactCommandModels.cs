using System.Text.Json.Serialization;

using Excise.Core.Redaction.Recovery;

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
    bool NoCorroboration,
    // Last, with defaults: this is a positional record and every existing
    // caller constructs it positionally.
    string? RestorePath = null,
    bool IncludeVisibleCarriers = false);

internal enum UnredactMode { Certain, Residue, Both }

internal sealed record UnredactCertainFinding(
    int Page,
    string Text,
    string HiddenBy,
    double X,
    double Y,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    double? Confidence = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? Object = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Location = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Proximity = null,
    /// <summary>
    /// #1669 — what this finding INDICATES. A link's /URI and text under a
    /// black box are both "text a reader cannot see"; only one of them means a
    /// redaction failed, and a report that lists them at the same volume
    /// buries the second in the first. Measured: 28 of 57 clean court filings
    /// reported something, and metadata plus link targets were 3/4 of it.
    /// </summary>
    RecoveryFindingClass Class = RecoveryFindingClass.RedactionResidue)
{
    /// <summary>
    /// A document carrier finding: it has no page position, so the human
    /// output names the object and location instead of (0,0).
    /// </summary>
    [JsonIgnore]
    public bool FromCarrier { get; init; }
}

/// <summary>
/// Content a carrier holds that the scan did not decode into text (an opaque
/// attachment, a thumbnail, an unreadable packet). Reported, never counted as
/// recovered text.
/// </summary>
internal sealed record UnredactPresenceFinding(
    int Page,
    string Carrier,
    string Description,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? Object = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Location = null);

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
/// <summary>
/// #1589 — what could fit a mark nothing recovered. Absent for a mark whose
/// text came back: a candidate list beside a recovered value invites reading
/// the list as competing answers.
/// </summary>
internal sealed record UnredactMarkFit(
    double WidthPt,
    string WidthBasis,
    int MinCharacters,
    int MaxCharacters,
    IReadOnlyList<string> PatternClasses,
    IReadOnlyList<string> TopCandidates,
    int CandidatesFit,
    int CandidatesConsidered,
    double BitsLeaked,
    string Confidence,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Note);

internal sealed record UnredactMarkSummary(
    string Id,
    int Page,
    string Kind,
    string Description,
    IReadOnlyList<double> Rect,
    /// <summary>recovered | partially-recovered | candidates-only | not-recovered.</summary>
    string Outcome,
    int Findings,
    int CertainFindings,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    UnredactMarkFit? Fit = null);

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

/// <summary>#1588 — what `--restore` wrote.</summary>
internal sealed record UnredactRestoreResult(
    string Path,
    int ItemsDrawn,
    int DocumentLevelItems,
    bool SummaryIncluded,
    /// <summary>
    /// ⚠️ #1644 — characters the DRAWN layer could not represent. The page's
    /// own font is WinAnsi; CJK, Cyrillic, Greek and U+0100+ Latin have no byte
    /// in it. Non-zero means the reconstruction understates what was recovered,
    /// and an artifact offered as evidence must not do that silently.
    /// </summary>
    int UndrawableCharacters = 0);

internal sealed record UnredactReport(
    UnredactQuantification Quantification,
    IReadOnlyList<UnredactCertainFinding> Certain,
    IReadOnlyList<UnredactResidueFinding> Residue,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    UnredactRecoveryModel? Recovery = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    UnredactRestoreResult? Restore = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<UnredactPresenceFinding>? Present = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<UnredactCertainFinding>? VisibleDuplicates = null);

internal sealed record UnredactCommandOutcome(
    int ExitCode,
    UnredactReport? Report,
    string? Error)
{
    public static UnredactCommandOutcome Failure(int exitCode, string error) =>
        new(exitCode, null, error);
}
