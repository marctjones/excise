using System.Collections.Generic;
using System.Linq;

namespace Excise.Core.Validation;

/// <summary>
/// The conformance standard a <see cref="ValidationReport"/> was produced for.
/// </summary>
public enum ConformanceStandard
{
    /// <summary>PDF/UA-1 (ISO 14289-1) accessibility.</summary>
    PdfUA1,

    /// <summary>PDF/A-1b (ISO 19005-1) archival, level B.</summary>
    PdfA1B,

    /// <summary>PDF/A-2b (ISO 19005-2) archival, level B.</summary>
    PdfA2B,
}

/// <summary>The outcome of a single conformance rule.</summary>
public enum RuleStatus
{
    /// <summary>The rule was evaluated and the document satisfies it.</summary>
    Pass,

    /// <summary>The rule was evaluated and the document violates it.</summary>
    Fail,

    /// <summary>The rule does not apply to this document (nothing to check).</summary>
    NotApplicable,

    /// <summary>
    /// The rule is part of the standard but excise does not (yet) check it.
    /// Present so a report never implies a checkpoint passed when it was never
    /// looked at.
    /// </summary>
    NotChecked,
}

/// <summary>How much a <see cref="RuleStatus.Fail"/> matters.</summary>
public enum RuleSeverity
{
    /// <summary>A hard conformance violation — blocks <see cref="ValidationReport.CheckedSubsetConformant"/>.</summary>
    Error,

    /// <summary>A likely problem or a recommended-not-required construct; does not block conformance.</summary>
    Warning,

    /// <summary>Informational only.</summary>
    Info,
}

/// <summary>
/// The result of evaluating one conformance rule against a document.
/// </summary>
public sealed class ValidationResult
{
    /// <summary>Stable, machine-readable rule id (e.g. <c>UA-Tagged</c>).</summary>
    public string RuleId { get; }

    /// <summary>Human-readable description of what the rule requires.</summary>
    public string Description { get; }

    /// <summary>How much a failure of this rule matters.</summary>
    public RuleSeverity Severity { get; }

    /// <summary>Whether the document passed, failed, or the rule was skipped.</summary>
    public RuleStatus Status { get; }

    /// <summary>
    /// Where the problem is (page number, struct-element type, catalog key…),
    /// or null when not applicable / the whole document.
    /// </summary>
    public string? Location { get; }

    /// <summary>
    /// A pointer into the source standard (an ISO clause or Matterhorn
    /// checkpoint) for the reader who wants the authoritative text.
    /// </summary>
    public string? Reference { get; }

    public ValidationResult(
        string ruleId,
        string description,
        RuleSeverity severity,
        RuleStatus status,
        string? location = null,
        string? reference = null)
    {
        RuleId = ruleId;
        Description = description;
        Severity = severity;
        Status = status;
        Location = location;
        Reference = reference;
    }

    public override string ToString() =>
        $"[{Status}] {RuleId} ({Severity}){(Location is null ? "" : $" @ {Location}")}: {Description}";
}

/// <summary>
/// The outcome of running a conformance validator over a document: the per-rule
/// results plus an explicit, honest statement of what was NOT checked.
///
/// <para><b>This is a bounded, structural subset checker, not a full ISO
/// validator.</b> <see cref="CheckedSubsetConformant"/> means only that no
/// <see cref="RuleSeverity.Error"/> rule that excise actually evaluated failed —
/// it is NOT a claim of full PDF/UA or PDF/A conformance. Read
/// <see cref="UncoveredCheckpoints"/> for the checkpoints outside excise's
/// scope; for an authoritative verdict use a reference validator such as
/// veraPDF.</para>
/// </summary>
public sealed class ValidationReport
{
    /// <summary>The standard this report was produced for.</summary>
    public ConformanceStandard Standard { get; }

    /// <summary>Every rule excise evaluated, in evaluation order.</summary>
    public IReadOnlyList<ValidationResult> Results { get; }

    /// <summary>
    /// The categories of ISO / Matterhorn checkpoints this validator does NOT
    /// cover. Present so a caller can never mistake a green report for full
    /// conformance.
    /// </summary>
    public IReadOnlyList<string> UncoveredCheckpoints { get; }

    public ValidationReport(
        ConformanceStandard standard,
        IReadOnlyList<ValidationResult> results,
        IReadOnlyList<string> uncoveredCheckpoints)
    {
        Standard = standard;
        Results = results;
        UncoveredCheckpoints = uncoveredCheckpoints;
    }

    /// <summary>All rules that failed (any severity).</summary>
    public IEnumerable<ValidationResult> Failures =>
        Results.Where(r => r.Status == RuleStatus.Fail);

    /// <summary>Failed rules with <see cref="RuleSeverity.Error"/> severity.</summary>
    public IEnumerable<ValidationResult> Errors =>
        Results.Where(r => r.Status == RuleStatus.Fail && r.Severity == RuleSeverity.Error);

    /// <summary>Failed rules with <see cref="RuleSeverity.Warning"/> severity.</summary>
    public IEnumerable<ValidationResult> Warnings =>
        Results.Where(r => r.Status == RuleStatus.Fail && r.Severity == RuleSeverity.Warning);

    /// <summary>
    /// True when no <see cref="RuleSeverity.Error"/> rule that was actually
    /// evaluated failed. <b>Bounded meaning:</b> conformant with respect to the
    /// checked subset only — see <see cref="UncoveredCheckpoints"/>. Never treat
    /// this as a full-conformance guarantee.
    /// </summary>
    public bool CheckedSubsetConformant => !Errors.Any();

    public override string ToString()
    {
        var lines = new List<string>
        {
            $"{Standard} structural check — CheckedSubsetConformant={CheckedSubsetConformant} " +
            $"(bounded subset; not a full ISO verdict)",
        };
        lines.AddRange(Results.Select(r => "  " + r));
        lines.Add($"  NOT checked: {string.Join("; ", UncoveredCheckpoints)}");
        return string.Join("\n", lines);
    }
}
