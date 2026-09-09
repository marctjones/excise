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
    RasterRedactionAudit
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

/// <summary>Which scrub and audit channels the shared policy should execute.</summary>
public sealed record RedactedCopySafetyOptions
{
    public bool ScrubMetadata { get; init; } = true;
    public bool ScrubAttachments { get; init; } = true;
    public bool ScrubRequestedTerms { get; init; } = true;
    public bool RunCarrierAudit { get; init; } = true;
    public bool VerifyRequestedTerms { get; init; } = true;
    public bool RunHiddenTextAudit { get; init; } = true;
    public bool RunRasterRedactionAudit { get; init; } = true;

    /// <summary>
    /// Per-carrier scrub MODE for the <see cref="ScrubRequestedTerms"/> pass
    /// (#1188/#1169). Default: <see cref="Operations.CarrierScrubMode.Strip"/>
    /// everywhere, unchanged from before the option existed.
    /// </summary>
    /// <remarks>
    /// This is the delivery surface #1169 asks for: on a carrier whose value is
    /// a KNOWN string (a URL, a templated metadata field), cutting the term out
    /// leaves a hole the surrounding text can be read around, so the user can
    /// choose to drop the whole value or be told and decide. A
    /// <see cref="Operations.CarrierScrubMode.ReportOnly"/> carrier that holds
    /// the term is surfaced in <see cref="RedactedCopySafetyReport.Warnings"/> —
    /// it must never pass silently.
    /// </remarks>
    public Operations.CarrierScrubPolicy CarrierPolicy { get; init; }
        = Operations.CarrierScrubPolicy.Default;

    /// <summary>
    /// Which carriers the term scrub examines at all (#1188). Orthogonal to
    /// <see cref="CarrierPolicy"/>: this is scope, that is mode.
    /// </summary>
    public Operations.RedactionCarriers Carriers { get; init; }
        = Operations.RedactionCarriers.All;

    /// <summary>
    /// Match whole words only in the term scrub (#1052). Default false —
    /// substring, the #1000 decision. Must agree with how the caller matched
    /// page content: two different rules in one redaction is the #896 failure.
    /// </summary>
    public bool WholeWord { get; init; }

    public static RedactedCopySafetyOptions Default { get; } = new();
}

/// <summary>
/// Delivery-neutral input for post-redaction scrub and audit policy. File I/O,
/// encryption, dialogs, console text, JSON, and exit codes stay with callers.
/// </summary>
public sealed record RedactedCopySafetyRequest(
    IReadOnlyList<RedactedCopySafetyArea> RedactionAreas,
    IReadOnlyList<string> RequestedTerms,
    int SkippedRedactionAreaCount,
    RedactedCopySafetyOptions Options)
{
    public static RedactedCopySafetyRequest ForAreas(
        IReadOnlyList<RedactedCopySafetyArea> areas,
        int skippedRedactionAreaCount = 0,
        RedactedCopySafetyOptions? options = null) =>
        new(
            areas,
            System.Array.Empty<string>(),
            skippedRedactionAreaCount,
            options ?? RedactedCopySafetyOptions.Default);

    public static RedactedCopySafetyRequest ForTerms(
        IReadOnlyList<string> terms,
        RedactedCopySafetyOptions? options = null) =>
        new(
            System.Array.Empty<RedactedCopySafetyArea>(),
            terms,
            0,
            options ?? RedactedCopySafetyOptions.Default);
}

/// <summary>Structured evidence produced before a redacted copy is saved.</summary>
public sealed record RedactedCopySafetyReport(
    int RedactionAreaCount,
    int SkippedRedactionAreaCount,
    int RequestedTermCount,
    int CheckedTermCount,
    int RemainingTermCount,
    int SkippedShortTermCount,
    RedactedContentVerificationStatus ContentVerificationStatus,
    bool MetadataScrubbed,
    int InfoFieldsScrubbed,
    bool HadXmpMetadata,
    bool AttachmentsScrubbed,
    int EmbeddedFileCountBefore,
    RedactedContentVerificationStatus HiddenTextAuditStatus,
    int HiddenTextFindingCount,
    RedactedContentVerificationStatus RasterRedactionAuditStatus,
    int RemainingRasterOverlapCount,
    IReadOnlyList<RedactedCopySafetyFailureStage> FailedStages,
    IReadOnlyList<string> Warnings)
{
    public bool HasWarnings =>
        Warnings.Count > 0 ||
        FailedStages.Count > 0 ||
        ContentVerificationStatus == RedactedContentVerificationStatus.Warning ||
        HiddenTextAuditStatus == RedactedContentVerificationStatus.Warning ||
        RasterRedactionAuditStatus == RedactedContentVerificationStatus.Warning;
}
