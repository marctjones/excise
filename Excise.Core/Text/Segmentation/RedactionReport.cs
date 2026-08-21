using System;
using System.Collections.Generic;
using System.Linq;

namespace Excise.Core.Text.Segmentation;

/// <summary>What happened to a term on one page.</summary>
public enum RedactionOutcome
{
    /// <summary>
    /// The glyphs were removed AND excise re-read the page afterwards and
    /// confirmed they are gone. The only outcome that may be called success.
    /// </summary>
    RemovedVerified,

    /// <summary>
    /// Matches were located and rewritten, but a re-read still finds the term.
    /// The engine did work and the work did not land — a leak, reported as one.
    /// </summary>
    RemovalUnverified,

    /// <summary>excise located nothing to remove on this page.</summary>
    NothingToRemove,
}

/// <summary>
/// A carrier outside page content — the four #608 was filed for — and whether
/// this redaction actually touched it.
/// </summary>
public sealed record CarrierResult(string Carrier, bool Scrubbed, string? RefusedReason);

/// <summary>Per-page detail.</summary>
public sealed record PageRedactionResult(
    int PageNumber,
    int MatchesLocated,
    int OccurrencesRemainingAfter,
    RedactionOutcome Outcome);

/// <summary>
/// The result of <c>RedactText</c> — #1089.
///
/// <para><b>Why this replaced an <c>int</c>.</b> The old return counted matches
/// LOCATED per pass. It could not express the difference between removed,
/// smeared, survived and deliberately skipped, so it reported all four as
/// success. That one defect produced #1043 (one occurrence reported as three),
/// #1038 (silent destruction of a whole line), #999 (page scrubbed, carriers
/// not, with no way to say so) and the "reported success anyway" half of #1040
/// — a real name left in a real document behind a black box.</para>
///
/// <para><b>Verified means verified.</b> <see cref="VerifiedRemovals"/> counts
/// only occurrences excise re-read the page and confirmed gone. It is not an
/// attempt counter. A tool whose success number counts its own intentions is
/// the shape that shipped three leaks past a green suite.</para>
///
/// <para>⚠️ This is still excise checking excise, and deliberately not enough
/// on its own: the re-read uses the same extractor whose blind spots bound
/// redaction completeness (CLAUDE.md Limitations #1). It catches removal that
/// did not land — a common failure. It cannot catch text excise could never
/// see. Corroboration by a non-excise oracle is #1094, and this type does not
/// pretend to replace it.</para>
/// </summary>
public sealed class RedactionReport
{
    /// <summary>The term this report is about.</summary>
    public required string Term { get; init; }

    /// <summary>Per-page detail, in page order.</summary>
    public required IReadOnlyList<PageRedactionResult> Pages { get; init; }

    /// <summary>Document-level carriers and what happened to each.</summary>
    public required IReadOnlyList<CarrierResult> Carriers { get; init; }

    /// <summary>
    /// Occurrences excise located. NOT a success count — kept because the gap
    /// between this and <see cref="VerifiedRemovals"/> is the signal that
    /// something went wrong.
    /// </summary>
    public int MatchesLocated => Pages.Sum(p => p.MatchesLocated);

    /// <summary>
    /// Occurrences confirmed gone by re-reading the page. <b>This is the number
    /// a user may act on.</b>
    /// </summary>
    public int VerifiedRemovals =>
        Pages.Sum(p => Math.Max(0, p.MatchesLocated - p.OccurrencesRemainingAfter));

    /// <summary>Occurrences still findable after redaction finished.</summary>
    public int Survived => Pages.Sum(p => p.OccurrencesRemainingAfter);

    /// <summary>
    /// True when everything located was verified gone and no carrier was
    /// refused. Anything else needs a human to read the detail.
    /// </summary>
    public bool IsCleanSuccess =>
        Survived == 0 && Carriers.All(c => c.RefusedReason == null);

    /// <summary>A one-line summary safe to print. States the gap when there is one.</summary>
    public override string ToString()
    {
        var parts = new List<string> { $"{VerifiedRemovals} removed" };
        if (Survived > 0) parts.Add($"{Survived} STILL PRESENT");
        foreach (var c in Carriers.Where(c => c.RefusedReason != null))
            parts.Add($"{c.Carrier} NOT scrubbed ({c.RefusedReason})");
        return string.Join("; ", parts);
    }
}
