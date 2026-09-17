using System.Collections.Generic;
using System.Linq;
using Excise.Core.Document;

namespace Excise.Core.Redaction.Recovery;

/// <summary>
/// #1587 — how strongly a finding asserts the redacted material. The
/// distinction is the whole safety property of this tool: an audit that
/// cannot tell "the bytes say SMITH" from "SMITH is one of fourteen
/// surnames that fit the gap" is a rumour generator, not a measurement.
/// </summary>
public enum RecoveryConfidence
{
    /// <summary>The text is physically present in the file and read verbatim.</summary>
    Certain,

    /// <summary>A ranked set constrained by evidence (width residue, OCR). Never asserted.</summary>
    Candidate,

    /// <summary>
    /// Material survives at this location but this channel did not turn it into
    /// text — image pixels under a box, a vector drawing, an opaque attachment.
    /// The leak is real; the value is not claimed.
    /// </summary>
    PresentOnly,
}

/// <summary>What kind of redaction mark was found on the page (#1587).</summary>
public enum RedactionMarkKind
{
    /// <summary>An opaque dark filled rectangle painted in the content stream.</summary>
    FilledBox,

    /// <summary>A <c>/Redact</c> annotation (§12.5.6.23) — applied or, more often, not.</summary>
    RedactAnnotation,

    /// <summary>A <c>/Square</c> (or other shape) annotation with an opaque dark interior.</summary>
    ShapeAnnotation,

    /// <summary>
    /// A dark filled rectangle inside a Form XObject the page invokes with
    /// <c>Do</c> (#1606). Distinguished from <see cref="FilledBox"/> because
    /// the hidden-text detector walks the PAGE content stream only, so text
    /// under this kind of box is nobody else's to recover.
    /// </summary>
    FormXObjectBox,

    /// <summary>
    /// A hole where content used to be: a run of missing glyphs between
    /// surviving ones, with no box drawn over it. Inferred from a channel
    /// (the width residue), not painted on the page.
    /// </summary>
    EmptiedRegion,
}

/// <summary>How much of one mark came back (#1587).</summary>
public enum MarkRecoveryOutcome
{
    /// <summary>At least one CERTAIN finding covers this mark: the text is back, verbatim.</summary>
    Recovered,

    /// <summary>
    /// Certain findings exist but do not account for the whole mark — some of its
    /// span is covered by certain text and some is not.
    /// </summary>
    PartiallyRecovered,

    /// <summary>Only candidates or present-only evidence. Nothing asserted.</summary>
    CandidatesOnly,

    /// <summary>No channel produced anything for this mark.</summary>
    NotRecovered,
}

/// <summary>
/// Where a finding sits. Null on a <see cref="RecoveredFinding"/> means the
/// material is document-level (metadata, an attachment, a prior revision with
/// no surviving geometry) and belongs in the document-level list, not on a page.
/// </summary>
/// <param name="PageNumber">1-based.</param>
/// <param name="Rect">Page space, bottom-left origin.</param>
/// <param name="Provenance">
/// How the rectangle was obtained — "glyph boxes", "widget /Rect",
/// "MCID content", "annotation /Rect", "residue gap". Recorded because a
/// location derived from an MCID lookup is weaker evidence than one read off
/// the glyphs themselves, and the report must not flatten the two.
/// </param>
public sealed record RecoveryLocation(int PageNumber, PdfRectangle Rect, string Provenance);

/// <summary>
/// #1587 — one recovered (or constrained) piece of redacted material, from any
/// channel. Every channel produces this type, so the report, the scorecard and
/// <c>--restore</c> all read one model rather than five shapes.
/// </summary>
/// <param name="Channel">
/// The evidence channel: "hidden-text", "carrier", "marked-content",
/// "covered-image", "covered-vector", "form-field", "residue",
/// "ocr-differential".
/// </param>
/// <param name="Carrier">
/// The specific carrier within the channel — "structure-tree /ActualText",
/// "annotation /Contents", "black filled rectangle". Free text, because the
/// carrier vocabulary grows with every producer surveyed (#1592).
/// </param>
/// <param name="Text">
/// The recovered text for a <see cref="RecoveryConfidence.Certain"/> finding.
/// Null for candidate and present-only findings — a candidate's content lives
/// in <paramref name="Candidates"/> so nothing downstream can mistake a guess
/// for a reading.
/// </param>
/// <param name="Candidates">Ranked, best first. Empty unless Confidence is Candidate.</param>
/// <param name="ResidualBits">
/// log2 of the admissible set for a candidate finding: how much uncertainty is
/// LEFT, so 0 bits means one candidate fits. Zero for certain findings, where
/// there is no set to be uncertain about.
/// </param>
/// <param name="MarkId">The <see cref="RedactionMark.Id"/> this overlaps, or null.</param>
public sealed record RecoveredFinding(
    string Channel,
    string Carrier,
    RecoveryConfidence Confidence,
    string? Text,
    IReadOnlyList<string> Candidates,
    double ResidualBits,
    RecoveryLocation? Location,
    string? MarkId = null,
    double? Score = null)
{
    /// <summary>A certain reading: the bytes say this.</summary>
    public static RecoveredFinding Certain(
        string channel, string carrier, string text, RecoveryLocation? location, double? score = null)
        => new(channel, carrier, RecoveryConfidence.Certain, text,
               System.Array.Empty<string>(), 0, location, null, score);

    /// <summary>A constrained guess: these fit, with this much uncertainty left.</summary>
    public static RecoveredFinding Candidate(
        string channel, string carrier, IReadOnlyList<string> candidates,
        double residualBits, RecoveryLocation? location)
        => new(channel, carrier, RecoveryConfidence.Candidate, null,
               candidates, residualBits, location);

    /// <summary>Material survives here; this channel does not turn it into text.</summary>
    public static RecoveredFinding PresentOnly(
        string channel, string carrier, RecoveryLocation? location)
        => new(channel, carrier, RecoveryConfidence.PresentOnly, null,
               System.Array.Empty<string>(), 0, location);

    /// <summary>Same finding, attributed to a mark.</summary>
    public RecoveredFinding LinkedTo(string markId) => this with { MarkId = markId };
}

/// <summary>
/// #1587 — a redaction the document ADMITS to: the black box, the
/// <c>/Redact</c> annotation, the hole. Marks are enumerated independently of
/// the recovery channels, so a mark nothing recovered is still reported. That
/// asymmetry is the point: "we recovered 3 things" says nothing about coverage;
/// "3 of 11 marks came back" does.
/// </summary>
/// <param name="Id">Stable within one report: "p3m2" = page 3, mark 2.</param>
public sealed record RedactionMark(
    string Id,
    int PageNumber,
    PdfRectangle Rect,
    RedactionMarkKind Kind,
    string Description);

/// <summary>One mark and everything any channel found under it (#1587).</summary>
/// <param name="Fit">
/// #1589 — what could fit this mark: character range, pattern classes, ranked
/// dictionary candidates and bits leaked. Computed only for marks NO channel
/// recovered text from, because that is where the question matters: for a mark
/// whose text came back the constraint is redundant, and for one that held it
/// is the only thing left to say.
/// </param>
public sealed record MarkSummary(
    RedactionMark Mark,
    MarkRecoveryOutcome Outcome,
    IReadOnlyList<RecoveredFinding> Findings,
    RedactionFitAnalyzer.FitReport? Fit = null)
{
    public bool HasCertain => Findings.Any(f => f.Confidence == RecoveryConfidence.Certain);
}

/// <summary>
/// #1587 — the whole answer for one document: what the redactions were, what
/// came back, and what did not.
/// </summary>
/// <param name="Marks">Every detected mark, with its outcome. Includes marks nothing recovered.</param>
/// <param name="Unlinked">
/// Page-located findings that overlap NO mark. Not an error: a carrier leak
/// often sits where no box was drawn (the redactor rewrote the page and forgot
/// the structure tree), and dropping those would hide the commonest real leak.
/// </param>
/// <param name="DocumentLevel">Findings with no page location at all.</param>
/// <param name="ChannelsRun">
/// Which channels actually ran. A report over three channels must not read like
/// one over eight — same rule as the scorecard's Coverage (#1181).
/// </param>
/// <param name="ChannelsSkipped">Channel name → why it did not run (tool missing, not requested).</param>
public sealed record RecoveryReport(
    IReadOnlyList<MarkSummary> Marks,
    IReadOnlyList<RecoveredFinding> Unlinked,
    IReadOnlyList<RecoveredFinding> DocumentLevel,
    IReadOnlyList<string> ChannelsRun,
    IReadOnlyDictionary<string, string> ChannelsSkipped)
{
    /// <summary>Every finding, wherever it was filed.</summary>
    public IEnumerable<RecoveredFinding> AllFindings =>
        Marks.SelectMany(m => m.Findings).Concat(Unlinked).Concat(DocumentLevel);

    public int MarkCount => Marks.Count;
    public int MarksRecovered => Marks.Count(m => m.Outcome == MarkRecoveryOutcome.Recovered);
    public int MarksPartial => Marks.Count(m => m.Outcome == MarkRecoveryOutcome.PartiallyRecovered);
    public int MarksCandidatesOnly => Marks.Count(m => m.Outcome == MarkRecoveryOutcome.CandidatesOnly);
    public int MarksNotRecovered => Marks.Count(m => m.Outcome == MarkRecoveryOutcome.NotRecovered);
}
