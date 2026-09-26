using System.Collections.Generic;
using Excise.Core.Document;

namespace Excise.Core.Text.Segmentation;

/// <summary>The outcome of one redacted-copy verification channel.</summary>
public enum RedactedContentVerificationStatus
{
    NotChecked,
    Verified,
    Warning
}

/// <summary>
/// A safety stage that could not complete. These values let delivery surfaces
/// distinguish a disabled check from a partial failure without depending on
/// exception text or logging infrastructure.
/// </summary>
public enum RedactedCopySafetyFailureStage
{
    MetadataInspection,
    AttachmentInspection,
    MetadataScrub,
    AttachmentScrub,
    RequestedTermScrub,
    ContentVerification,
    HiddenTextAudit,
    RasterRedactionAudit,

    /// <summary>
    /// Incoming <c>/Redact</c> annotations could not be inspected, so whether
    /// the document carries unresolved review marks is UNKNOWN. Surfaced
    /// rather than assumed either way (#1430).
    /// </summary>
    UnresolvedRedactAnnotationInspection
}

/// <summary>
/// Thrown instead of producing a safe-redacted copy of a document that still
/// carries unresolved incoming <c>/Redact</c> annotations (#1430).
///
/// <para>A <c>/Redact</c> annotation is a reviewer's PROPOSAL: it marks content
/// for removal and, until applied, that content is still entirely present. The
/// product policy (<c>redactionReviewDrafts.safeRedactedCopy</c>) is therefore
/// "refuse … rather than silently applying or deleting them", and the
/// capability's stated rationale is that a review draft must be *impossible to
/// confuse with a safe-redacted copy*. Applying someone else's marks is
/// destructive and irreversible; deleting them destroys the review; producing
/// the copy anyway ships a file labelled safe whose flagged content is intact.
/// Refusing is the only option that loses nothing.</para>
///
/// <para>This is a THROW rather than a flag on the report deliberately. The
/// three delivery surfaces (GUI redacted copy, scripting, CLI) all route
/// through <c>RedactedCopySafetyPolicy.Evaluate</c>, and a flag would have to
/// be re-checked by each of them — so a surface added later that forgot the
/// check would fail OPEN, silently producing exactly the output this rule
/// exists to prevent.</para>
/// </summary>
public sealed class UnresolvedRedactAnnotationsException : System.InvalidOperationException
{
    public UnresolvedRedactAnnotationsException(int annotationCount)
        : base($"This document contains {annotationCount} unresolved redaction mark" +
               (annotationCount == 1 ? "" : "s") +
               " (/Redact annotations). excise will not produce a safe-redacted copy " +
               "from it, because the marked content has NOT been removed. Apply or " +
               "remove the marks deliberately first, or save an ordinary copy instead.")
        => AnnotationCount = annotationCount;

    /// <summary>How many unresolved <c>/Redact</c> annotations were found.</summary>
    public int AnnotationCount { get; }
}

/// <summary>
/// One redaction area expressed entirely in Core document coordinates. A
/// delivery surface may include captured text when it has it, but the shared
/// policy never depends on a UI selection or ViewModel type.
/// </summary>
public sealed record RedactedCopySafetyArea(
    int PageNumber,
    PdfPageRect PageArea,
    string? CapturedText = null);

/// <summary>
/// Which scrub and audit stages the shared policy runs. What they remove is
/// the caller's <see cref="RedactionOptions"/> on the request (#1830).
/// </summary>
public sealed record RedactedCopySafetyOptions
{
    public bool ScrubMetadata { get; init; } = true;
    public bool ScrubRequestedTerms { get; init; } = true;
    public bool RunCarrierAudit { get; init; } = true;
    public bool VerifyRequestedTerms { get; init; } = true;
    public bool RunHiddenTextAudit { get; init; } = true;
    public bool RunRasterRedactionAudit { get; init; } = true;

    /// <summary>
    /// Refuse to produce a safe-redacted copy of a document carrying unresolved
    /// incoming <c>/Redact</c> annotations (#1430). On by default: this is a
    /// safety rule, so opting out has to be a deliberate act by a caller that
    /// is NOT claiming its output is safely redacted.
    /// </summary>
    public bool RefuseOnUnresolvedRedactAnnotations { get; init; } = true;

    /// <summary>
    /// When <see cref="RedactionOptions.KeepAttachments"/> is set, redact or
    /// report every attachment the copy keeps (#1572): text files have the
    /// terms cut out, nested PDFs are redacted, anything else — and everything
    /// when there is no term — is reported as not checked. On by default; a
    /// caller that already did this (the CLI, after <c>RedactText</c>) turns it off.
    /// </summary>
    public bool InspectKeptAttachments { get; init; } = true;

    public static RedactedCopySafetyOptions Default { get; } = new();
}

/// <summary>
/// Delivery-neutral input for post-redaction scrub and audit policy. File I/O,
/// encryption, dialogs, console text, JSON, and exit codes stay with callers.
/// </summary>
/// <param name="Redaction">
/// The options the engine pass ran with. This pass applies them again —
/// idempotently, since every step is a removal — so the report can account for
/// what went (#1586). Required: a default here would silently undo Maximum (#1830).
/// </param>
public sealed record RedactedCopySafetyRequest(
    IReadOnlyList<RedactedCopySafetyArea> RedactionAreas,
    IReadOnlyList<string> RequestedTerms,
    int SkippedRedactionAreaCount,
    RedactionOptions Redaction,
    RedactedCopySafetyOptions Options)
{
    public static RedactedCopySafetyRequest ForAreas(
        IReadOnlyList<RedactedCopySafetyArea> areas,
        RedactionOptions redaction,
        int skippedRedactionAreaCount = 0,
        RedactedCopySafetyOptions? options = null) =>
        new(
            areas,
            System.Array.Empty<string>(),
            skippedRedactionAreaCount,
            redaction,
            options ?? RedactedCopySafetyOptions.Default);

    public static RedactedCopySafetyRequest ForTerms(
        IReadOnlyList<string> terms,
        RedactionOptions redaction,
        RedactedCopySafetyOptions? options = null) =>
        new(
            System.Array.Empty<RedactedCopySafetyArea>(),
            terms,
            0,
            redaction,
            options ?? RedactedCopySafetyOptions.Default);
}

/// <summary>Structured evidence produced before a redacted copy is saved.</summary>
public sealed record RedactedCopySafetyReport
{
    public required int RedactionAreaCount { get; init; }
    public required int SkippedRedactionAreaCount { get; init; }
    public required int RequestedTermCount { get; init; }
    public required int CheckedTermCount { get; init; }
    public required int RemainingTermCount { get; init; }
    public required int SkippedShortTermCount { get; init; }
    public required RedactedContentVerificationStatus ContentVerificationStatus { get; init; }
    public required bool MetadataScrubbed { get; init; }
    public required int InfoFieldsScrubbed { get; init; }
    public required bool HadXmpMetadata { get; init; }
    public required bool AttachmentsScrubbed { get; init; }
    // Since #1572: the number of attachments this redaction REMOVED, including
    // those the area pass removed before this report ran. (The name predates
    // that; before, it counted what the scrub could see, which missed page
    // annotation attachments and anything the area pass had already taken.)
    public required int EmbeddedFileCountBefore { get; init; }
    public required RedactedContentVerificationStatus HiddenTextAuditStatus { get; init; }
    public required int HiddenTextFindingCount { get; init; }
    public required RedactedContentVerificationStatus RasterRedactionAuditStatus { get; init; }
    public required int RemainingRasterOverlapCount { get; init; }
    public required IReadOnlyList<RedactedCopySafetyFailureStage> FailedStages { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public int UnresolvedRedactAnnotationCount { get; init; }
    // #1507 — the document arrived identifying itself as PDF/A and the metadata
    // scrub KEPT that identification (pdfaid part/conformance/rev) while
    // removing everything else in the XMP packet. Reported rather than left
    // implicit: a caller telling a user "XMP metadata removed" would be
    // overstating what happened, since a small packet survives by design
    // because PDF/A conformance requires it. Overstating a scrub is the same
    // class of problem as a carrier that silently keeps a term.
    public bool PdfAIdentificationPreserved { get; init; }
    // #1572 — every attachment removed or kept, with name and size.
    public IReadOnlyList<AttachmentRedactionResult>? Attachments { get; init; }
    // #1574 — the XFA form(s) the redaction removed whole, as carrier rows.
    public IReadOnlyList<string>? XfaRemovals { get; init; }
    // #1586 — the output profile that ran, what it removed WHOLE, and whether
    // the copy is still accessible and interactive. A dialog that calls a copy
    // safe has to be able to say what is no longer in it.
    public RedactionProfile Profile { get; init; } = RedactionProfile.Standard;
    public IReadOnlyList<RedactedFeatureRemoval>? ProfileRemovals { get; init; }
    public bool AccessibilityAndInteractivityRemoved { get; init; }

    internal RedactedCopySafetyReport() { }

    /// <summary>Attachments removed or kept (#1572); never null.</summary>
    public IReadOnlyList<AttachmentRedactionResult> AttachmentResults =>
        Attachments ?? System.Array.Empty<AttachmentRedactionResult>();

    /// <summary>What the output profile removed whole (#1586); never null.</summary>
    public IReadOnlyList<RedactedFeatureRemoval> Removals =>
        ProfileRemovals ?? System.Array.Empty<RedactedFeatureRemoval>();

    public bool HasWarnings =>
        Warnings.Count > 0 ||
        AttachmentResults.Any(a => !a.IsClean) ||
        FailedStages.Count > 0 ||
        ContentVerificationStatus == RedactedContentVerificationStatus.Warning ||
        HiddenTextAuditStatus == RedactedContentVerificationStatus.Warning ||
        RasterRedactionAuditStatus == RedactedContentVerificationStatus.Warning;
}
