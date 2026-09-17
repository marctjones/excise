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
    double? Confidence = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? Object = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Location = null)
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

internal sealed record UnredactReport(
    UnredactQuantification Quantification,
    IReadOnlyList<UnredactCertainFinding> Certain,
    IReadOnlyList<UnredactResidueFinding> Residue,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<UnredactPresenceFinding>? Present = null);

internal sealed record UnredactCommandOutcome(
    int ExitCode,
    UnredactReport? Report,
    string? Error)
{
    public static UnredactCommandOutcome Failure(int exitCode, string error) =>
        new(exitCode, null, error);
}
