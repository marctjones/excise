using Excise.Core.Text.Segmentation;

namespace Excise.App.Services;

/// <summary>
/// Formats shared redacted-copy evidence for the desktop success dialog. This
/// is presentation only; scrub and audit decisions remain in Excise.Core.
/// </summary>
internal sealed class RedactedCopyDialogFormatter
{
    public string Format(string savedPath, RedactedCopySafetyReport report)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(savedPath);
        ArgumentNullException.ThrowIfNull(report);

        var lines = new List<string>
        {
            "Redacted PDF saved to:",
            savedPath,
            string.Empty,
            "Original file preserved. Document reloaded.",
            string.Empty,
            "Verification report:",
            $"- Content removal: {FormatContentVerification(report)}",
            $"- Metadata scrub: {FormatMetadataScrub(report)}",
            $"- Embedded files: {FormatEmbeddedFiles(report)}",
            $"- Hidden text audit: {FormatHiddenTextAudit(report)}",
            $"- Raster redaction audit: {FormatRasterRedactionAudit(report)}",
            string.Empty,
            "Removed text is not repeated in this report. Open Clipboard History only if you need to review captured selection previews."
        };

        if (report.Warnings.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Warnings:");
            lines.AddRange(report.Warnings.Select(warning => $"- {warning}"));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatContentVerification(RedactedCopySafetyReport report) =>
        report.ContentVerificationStatus switch
        {
            RedactedContentVerificationStatus.Verified =>
                $"verified {report.CheckedTermCount} captured selection preview(s) no longer appear in extracted text",
            RedactedContentVerificationStatus.Warning =>
                $"{report.RemainingTermCount} captured selection preview(s) still appear in extracted text",
            _ when report.RequestedTermCount == 0 =>
                "not checked; no captured selection previews were available",
            _ =>
                "not checked; captured previews were too short for reliable matching"
        };

    /// <summary>
    /// ⚠️ #1507 — say what actually happened. On a PDF/A document the scrub
    /// removes every property in the XMP packet EXCEPT the <c>pdfaid</c>
    /// identification, which PDF/A conformance requires the file to keep. A flat
    /// "XMP metadata removed" would then be a claim excise no longer meets, and
    /// a redaction dialog is the last place to be loosely worded. The report
    /// carries the fact (<c>PdfAIdentificationPreserved</c>) precisely so this
    /// line can be accurate rather than reassuring.
    /// </summary>
    private static string FormatMetadataScrub(RedactedCopySafetyReport report)
    {
        if (report.FailedStages.Contains(RedactedCopySafetyFailureStage.MetadataScrub))
            return "failed; see warnings";
        if (!report.MetadataScrubbed)
            return "not requested";

        var xmp = report switch
        {
            { PdfAIdentificationPreserved: true } =>
                "XMP metadata removed except the PDF/A identification (pdfaid), which is kept so " +
                "the file remains PDF/A",
            { HadXmpMetadata: true } => "XMP metadata removed",
            _ => "no XMP metadata found",
        };
        return $"{report.InfoFieldsScrubbed} Info field(s) removed; {xmp}";
    }

    private static string FormatEmbeddedFiles(RedactedCopySafetyReport report)
    {
        if (report.FailedStages.Contains(RedactedCopySafetyFailureStage.AttachmentInspection) ||
            report.FailedStages.Contains(RedactedCopySafetyFailureStage.AttachmentScrub) ||
            report.FailedStages.Contains(RedactedCopySafetyFailureStage.MetadataScrub))
        {
            return "not fully verified; see warnings";
        }
        if (!report.AttachmentsScrubbed)
            return "not requested";

        return report.EmbeddedFileCountBefore == 0
            ? "none found"
            : $"{report.EmbeddedFileCountBefore} removed";
    }

    private static string FormatHiddenTextAudit(RedactedCopySafetyReport report) =>
        report.HiddenTextAuditStatus switch
        {
            RedactedContentVerificationStatus.Verified => "no structurally hidden text found",
            RedactedContentVerificationStatus.Warning =>
                $"{report.HiddenTextFindingCount} finding(s) need manual review",
            _ => "not checked"
        };

    private static string FormatRasterRedactionAudit(RedactedCopySafetyReport report) =>
        report.RasterRedactionAuditStatus switch
        {
            RedactedContentVerificationStatus.Verified =>
                "no raster image content remains in redaction areas",
            RedactedContentVerificationStatus.Warning =>
                $"{report.RemainingRasterOverlapCount} raster image invocation(s) need manual review",
            _ => "not checked"
        };
}
