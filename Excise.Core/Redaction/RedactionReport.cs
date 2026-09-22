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

/// <summary>
/// One thing the output profile REMOVED WHOLE, rather than scrubbing a term out
/// of it (#1586). Reported because a user who does not know their JavaScript,
/// hidden layers or thumbnails are gone cannot tell a redaction from a
/// mangling — and because "we removed it" is the only evidence that a carrier
/// which cannot be scrubbed selectively was dealt with at all.
/// </summary>
/// <param name="Feature">
/// What was removed, in the words a person would use ("JavaScript actions",
/// "hidden optional-content layers").
/// </param>
/// <param name="Count">How many of them. Zero rows are not reported.</param>
/// <param name="Detail">
/// Optional extra a reviewer might need — e.g. that the XMP packet went but
/// the PDF/A identification stayed.
/// </param>
public sealed record RedactedFeatureRemoval(string Feature, int Count, string? Detail = null)
{
    /// <inheritdoc/>
    public override string ToString() =>
        Detail == null ? $"{Count} × {Feature}" : $"{Count} × {Feature} ({Detail})";
}

/// <summary>Per-page detail.</summary>
public sealed record PageRedactionResult(
    int PageNumber,
    int MatchesLocated,
    int OccurrencesRemainingAfter,
    RedactionOutcome Outcome);

/// <summary>
/// An occurrence of the term that a line-end hyphen splits across two lines —
/// the page really reads <c>Ander-</c> / <c>son</c> — so excise never forms a
/// match and never removes it (#1372).
/// </summary>
/// <remarks>
/// <para><b>Reported, never silently removed.</b> Matching across the hyphen is
/// easy and was tried; the removal geometry for a match spanning two lines then
/// covers everything between the end of one line and the start of the next, and
/// that regressed 7 <c>RedactionCollateralHarness</c> fixtures plus
/// <c>RedactingATerm_DestroysNothingRemoteFromAnyMatch</c> — #942, the defect
/// that destroyed 5–36% of a document per term. Trading a missed word for
/// destroyed content is strictly worse, so the join was reverted. A real fix
/// needs a wrapped match to produce TWO removal boxes, one per line, which is a
/// change to how a match's geometry is built and is feature-sized.</para>
///
/// <para>Until then this is the project's "surface, don't guess" carrier policy
/// applied to a matcher gap: the reviewer is TOLD the occurrence is there and
/// still present, rather than excise reporting success over it. That silence is
/// what let this class of leak sit undetected — excise and mutool both keep the
/// real hyphen and never form the match, so a single-extractor check called the
/// document clean while Poppler's de-hyphenating reflow read the term straight
/// out of it.</para>
///
/// <para>A hyphen INSIDE a line is content, not a wrap: <c>well-known</c> must
/// never be reported as <c>wellknown</c>.</para>
/// </remarks>
public sealed record HyphenatedTermCandidate(
    int PageNumber,
    string BeforeBreak,
    string AfterBreak)
{
    /// <summary>How the page reads, e.g. <c>"Ander-" / "son"</c>.</summary>
    public override string ToString() => $"\"{BeforeBreak}-\" / \"{AfterBreak}\"";
}

/// <summary>
/// An occurrence of a MULTI-WORD term that an ordinary (non-hyphenated) line
/// wrap splits across two lines — the page really reads <c>…signed by Betty</c>
/// / <c>Mary on behalf of…</c> for the term "Betty Mary" — so excise never
/// forms a match and never removes it (#1750).
/// </summary>
/// <remarks>
/// <para>The generalization of <see cref="HyphenatedTermCandidate"/> to a wrap
/// with no hyphen: <see cref="PdfDocumentRedactionExtensions.FindTextMatches"/>
/// never inserts a space at a line wrap (a hyphen-continued word must not gain
/// an invented space), so a multi-word needle with a space in it cannot match
/// text that wraps mid-phrase. Before this was reported, redacting such a term
/// printed "Redacted 0 occurrence(s)" and exited 0 — a silent false success:
/// the occurrence was still fully readable and nothing said so.</para>
///
/// <para><b>Reported, never silently removed</b> — same reasoning as the
/// hyphen case: a match spanning two lines needs a removal box PER LINE, a
/// change to match geometry that is feature-sized (#942 is the lesson for why
/// one box covering both lines is not an acceptable substitute). Until that
/// exists, the reviewer is TOLD the occurrence is there rather than handed a
/// report that calls the redaction clean.</para>
/// </remarks>
public sealed record WordWrapTermCandidate(
    int PageNumber,
    string BeforeBreak,
    string AfterBreak)
{
    /// <summary>How the page reads, e.g. <c>"...Betty" / "Mary..."</c>.</summary>
    public override string ToString() => $"\"{BeforeBreak}\" / \"{AfterBreak}\"";
}

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
    /// Occurrences split across a line by a hyphen, which excise did NOT match
    /// and did NOT remove (#1372). Surfaced so a reviewer is not told the
    /// document is clean when a readable occurrence remains — see
    /// <see cref="HyphenatedTermCandidate"/> for why these are reported rather
    /// than joined.
    /// </summary>
    public IReadOnlyList<HyphenatedTermCandidate> HyphenatedCandidates { get; init; }
        = Array.Empty<HyphenatedTermCandidate>();

    /// <summary>
    /// Occurrences of a multi-word term split across an ORDINARY line wrap
    /// (no hyphen), which excise did NOT match and did NOT remove (#1750).
    /// Surfaced for the same reason as <see cref="HyphenatedCandidates"/>: a
    /// reviewer must not be told the document is clean when a readable
    /// occurrence remains. See <see cref="WordWrapTermCandidate"/>.
    /// </summary>
    public IReadOnlyList<WordWrapTermCandidate> WordWrapCandidates { get; init; }
        = Array.Empty<WordWrapTermCandidate>();

    /// <summary>
    /// Whether this run matched WHOLE WORDS only (#1052). Recorded because the
    /// rule that ran is part of the result: a user who does not know whether
    /// "Lee" could have matched inside "Sleeman" cannot reason about what was
    /// left behind — the same principle as #1045's width decision.
    /// </summary>
    public bool WholeWord { get; init; }

    /// <summary>
    /// The output profile this run was asked for (#1586). Recorded for the same
    /// reason as <see cref="WholeWord"/>: the policy that ran is part of the
    /// result.
    /// </summary>
    public RedactionProfile Profile { get; init; } = RedactionProfile.Standard;

    /// <summary>
    /// What the profile removed WHOLE, feature by feature (#1586). Empty when
    /// the document had none of it.
    /// </summary>
    /// <remarks>
    /// ⚠️ These are removals the redaction made on the user's behalf WITHOUT a
    /// term match — a JavaScript action, a hidden layer, a thumbnail. They are
    /// not failures, and they do not make <see cref="IsCleanSuccess"/> false;
    /// they are destruction a reviewer is entitled to know about.
    /// </remarks>
    public IReadOnlyList<RedactedFeatureRemoval> Removals { get; init; }
        = Array.Empty<RedactedFeatureRemoval>();

    /// <summary>
    /// True when this run removed the document's accessibility and interactive
    /// structure — <see cref="RedactionProfile.Maximum"/> (#1586). The output
    /// will not pass PDF/UA, will not submit, and will not read correctly to a
    /// screen reader.
    /// </summary>
    /// <remarks>
    /// Stated as a property rather than left for the caller to infer from
    /// <see cref="Profile"/>, because a front end must be able to SAY it — and
    /// because the individual flags, not the profile label, are what the engine
    /// honours, so a hand-built option set can trip this too.
    /// </remarks>
    public bool AccessibilityAndInteractivityRemoved { get; init; }

    /// <summary>
    /// Images whose term region was blacked out in place, preserving the rest of
    /// the image (#1195). Informational.
    /// </summary>
    public int ImageRegionsRedacted { get; init; }

    /// <summary>
    /// Images dropped WHOLESALE because region-level redaction could not run
    /// (a filter excise cannot yet re-encode faithfully — JBIG2/DCT/JPX, #1197).
    /// A term redaction that deletes a whole image is destructive collateral the
    /// user should know about, so it is surfaced here rather than hidden (the
    /// carrier policy: report, do not silently guess).
    /// </summary>
    public int ImagesDroppedWhole { get; init; }

    /// <summary>
    /// Every embedded file this redaction removed or kept (#1572), with its
    /// name and size. A kept file excise could not check, or whose own
    /// redaction was not clean, makes <see cref="IsCleanSuccess"/> false.
    /// </summary>
    public IReadOnlyList<Excise.Core.Document.AttachmentRedactionResult> Attachments { get; init; }
        = Array.Empty<Excise.Core.Document.AttachmentRedactionResult>();

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
    /// True when everything located was verified gone, no carrier was
    /// refused, and no kept attachment is unchecked or unclean. Anything else
    /// needs a human to read the detail.
    /// </summary>
    public bool IsCleanSuccess =>
        Survived == 0 &&
        Carriers.All(c => c.RefusedReason == null) &&
        HyphenatedCandidates.Count == 0 &&
        WordWrapCandidates.Count == 0 &&
        Attachments.All(a => a.IsClean);

    /// <summary>A one-line summary safe to print. States the gap when there is one.</summary>
    public override string ToString()
    {
        var parts = new List<string> { $"{VerifiedRemovals} removed" };
        if (WholeWord) parts.Add("whole-word matching");
        if (Survived > 0) parts.Add($"{Survived} STILL PRESENT");
        if (ImagesDroppedWhole > 0)
            parts.Add($"{ImagesDroppedWhole} whole image(s) removed (region redaction unavailable)");
        foreach (var c in Carriers.Where(c => c.RefusedReason != null))
            parts.Add($"{c.Carrier} NOT scrubbed ({c.RefusedReason})");
        if (HyphenatedCandidates.Count > 0)
            parts.Add($"{HyphenatedCandidates.Count} hyphen-wrapped occurrence(s) NOT removed");
        if (WordWrapCandidates.Count > 0)
            parts.Add($"{WordWrapCandidates.Count} line-wrapped occurrence(s) NOT removed");
        var removedFiles = Attachments.Count(a => a.Disposition == Excise.Core.Document.AttachmentDisposition.Removed);
        if (removedFiles > 0)
            parts.Add($"{removedFiles} attachment(s) removed");
        foreach (var r in Removals) parts.Add(r.ToString());
        if (AccessibilityAndInteractivityRemoved)
            parts.Add("output is NO LONGER accessible or interactive (maximum profile)");
        foreach (var a in Attachments.Where(a => !a.IsClean))
            parts.Add($"attachment {a}");
        return string.Join("; ", parts);
    }
}
