using Microsoft.Extensions.Logging;
using Pdfe.Core.Document;
using Pdfe.Core.Text.Segmentation;
using PdfEditor.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace PdfEditor.Services;

public enum RedactedContentVerificationStatus
{
    NotChecked,
    Verified,
    Warning
}

public sealed record RedactedCopySafetyOptions
{
    public bool ScrubMetadata { get; init; } = true;
    public bool ScrubAttachments { get; init; } = true;
    public bool VerifyCapturedSelectionText { get; init; } = true;
    public bool RunHiddenTextAudit { get; init; } = true;

    public static RedactedCopySafetyOptions Default { get; } = new();
}

public sealed record RedactedCopySafetyReport(
    int RedactionAreaCount,
    int SkippedRedactionAreaCount,
    int CapturedSelectionPreviewCount,
    int CheckedSelectionPreviewCount,
    int RemainingSelectionPreviewCount,
    int SkippedShortPreviewCount,
    RedactedContentVerificationStatus ContentVerificationStatus,
    bool MetadataScrubbed,
    int InfoFieldsScrubbed,
    bool HadXmpMetadata,
    bool AttachmentsScrubbed,
    int EmbeddedFileCountBefore,
    RedactedContentVerificationStatus HiddenTextAuditStatus,
    int HiddenTextFindingCount,
    IReadOnlyList<string> Warnings)
{
    public bool HasWarnings =>
        Warnings.Count > 0 ||
        ContentVerificationStatus == RedactedContentVerificationStatus.Warning ||
        HiddenTextAuditStatus == RedactedContentVerificationStatus.Warning;
}

public sealed class RedactedCopySafetyService
{
    private static readonly string[] InfoKeysToScrub =
    [
        "Title",
        "Author",
        "Subject",
        "Keywords",
        "Creator",
        "Producer",
        "CreationDate",
        "ModDate",
        "Trapped"
    ];

    private const int MinimumPreviewTextLength = 3;
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private readonly ILogger<RedactedCopySafetyService> _logger;

    public RedactedCopySafetyService(ILogger<RedactedCopySafetyService> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public RedactedCopySafetyReport PrepareRedactedCopy(
        PdfDocument document,
        IReadOnlyCollection<PendingRedaction> requestedRedactions,
        int skippedRedactionAreaCount = 0,
        RedactedCopySafetyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(requestedRedactions);

        options ??= RedactedCopySafetyOptions.Default;
        var warnings = new List<string>();

        var previewTerms = requestedRedactions
            .Select(r => r.PreviewText)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(NormalizeForSearch)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (skippedRedactionAreaCount > 0)
            warnings.Add($"{skippedRedactionAreaCount} redaction area(s) were skipped because their page no longer exists.");

        var infoFieldsBefore = CountScrubbableInfoFields(document);
        var hadXmpMetadata = HasXmpMetadata(document, warnings);
        var embeddedFileCountBefore = CountEmbeddedFiles(document, warnings);
        var metadataScrubbed = false;

        if (options.ScrubMetadata)
        {
            try
            {
                document.ScrubMetadata(scrubAttachments: options.ScrubAttachments);
                metadataScrubbed = true;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "Failed to scrub metadata from redacted copy.");
                warnings.Add("Metadata scrub could not be completed.");
            }
        }

        var contentStatus = VerifyCapturedSelectionText(document, previewTerms, options, out var checkedPreviewCount, out var remainingPreviewCount, out var skippedShortPreviewCount, warnings);
        var hiddenTextStatus = RunHiddenTextAudit(document, options, out var hiddenTextFindingCount, warnings);

        return new RedactedCopySafetyReport(
            RedactionAreaCount: requestedRedactions.Count,
            SkippedRedactionAreaCount: skippedRedactionAreaCount,
            CapturedSelectionPreviewCount: previewTerms.Length,
            CheckedSelectionPreviewCount: checkedPreviewCount,
            RemainingSelectionPreviewCount: remainingPreviewCount,
            SkippedShortPreviewCount: skippedShortPreviewCount,
            ContentVerificationStatus: contentStatus,
            MetadataScrubbed: metadataScrubbed,
            InfoFieldsScrubbed: metadataScrubbed ? infoFieldsBefore : 0,
            HadXmpMetadata: hadXmpMetadata,
            AttachmentsScrubbed: metadataScrubbed && options.ScrubAttachments,
            EmbeddedFileCountBefore: embeddedFileCountBefore,
            HiddenTextAuditStatus: hiddenTextStatus,
            HiddenTextFindingCount: hiddenTextFindingCount,
            Warnings: warnings);
    }

    public string FormatForDialog(string savedPath, RedactedCopySafetyReport report)
    {
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
            string.Empty,
            "Removed text is not repeated in this report. Open Clipboard History only if you need to review captured selection previews."
        };

        if (report.Warnings.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Warnings:");
            lines.AddRange(report.Warnings.Select(w => $"- {w}"));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private RedactedContentVerificationStatus VerifyCapturedSelectionText(
        PdfDocument document,
        IReadOnlyList<string> previewTerms,
        RedactedCopySafetyOptions options,
        out int checkedPreviewCount,
        out int remainingPreviewCount,
        out int skippedShortPreviewCount,
        List<string> warnings)
    {
        checkedPreviewCount = 0;
        remainingPreviewCount = 0;
        skippedShortPreviewCount = 0;

        if (!options.VerifyCapturedSelectionText)
            return RedactedContentVerificationStatus.NotChecked;

        if (previewTerms.Count == 0)
            return RedactedContentVerificationStatus.NotChecked;

        var checkedTerms = previewTerms
            .Where(t => t.Length >= MinimumPreviewTextLength)
            .ToArray();
        skippedShortPreviewCount = previewTerms.Count - checkedTerms.Length;

        if (checkedTerms.Length == 0)
            return RedactedContentVerificationStatus.NotChecked;

        try
        {
            var documentText = NormalizeForSearch(ExtractDocumentText(document));
            checkedPreviewCount = checkedTerms.Length;
            remainingPreviewCount = checkedTerms.Count(term =>
                documentText.Contains(term, StringComparison.OrdinalIgnoreCase));

            if (remainingPreviewCount > 0)
            {
                warnings.Add($"{remainingPreviewCount} captured selection preview(s) still appear in extracted page text.");
                return RedactedContentVerificationStatus.Warning;
            }

            return RedactedContentVerificationStatus.Verified;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Failed to verify redacted selection text.");
            warnings.Add("Removed-text verification could not be completed.");
            return RedactedContentVerificationStatus.Warning;
        }
    }

    private RedactedContentVerificationStatus RunHiddenTextAudit(
        PdfDocument document,
        RedactedCopySafetyOptions options,
        out int hiddenTextFindingCount,
        List<string> warnings)
    {
        hiddenTextFindingCount = 0;

        if (!options.RunHiddenTextAudit)
            return RedactedContentVerificationStatus.NotChecked;

        try
        {
            hiddenTextFindingCount = HiddenTextDetector.Scan(document).Count;
            if (hiddenTextFindingCount > 0)
            {
                warnings.Add($"{hiddenTextFindingCount} structurally hidden text finding(s) remain for manual review.");
                return RedactedContentVerificationStatus.Warning;
            }

            return RedactedContentVerificationStatus.Verified;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Failed to run hidden text audit on redacted copy.");
            warnings.Add("Hidden-text audit could not be completed.");
            return RedactedContentVerificationStatus.Warning;
        }
    }

    private static int CountScrubbableInfoFields(PdfDocument document) =>
        document.Info == null
            ? 0
            : InfoKeysToScrub.Count(key => document.Info.ContainsKey(key));

    private static bool HasXmpMetadata(PdfDocument document, List<string> warnings)
    {
        try
        {
            return document.GetXmpMetadata() != null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            warnings.Add("XMP metadata could not be inspected before scrub.");
            return false;
        }
    }

    private static int CountEmbeddedFiles(PdfDocument document, List<string> warnings)
    {
        try
        {
            return document.GetEmbeddedFiles().Count;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            warnings.Add("Embedded files could not be inspected before scrub.");
            return 0;
        }
    }

    private static string ExtractDocumentText(PdfDocument document)
    {
        var parts = new List<string>();
        for (var pageNumber = 1; pageNumber <= document.PageCount; pageNumber++)
            parts.Add(document.GetPage(pageNumber).Text);
        return string.Join(" ", parts);
    }

    private static string NormalizeForSearch(string value) =>
        Whitespace.Replace(value.Trim(), " ");

    private static string FormatContentVerification(RedactedCopySafetyReport report) =>
        report.ContentVerificationStatus switch
        {
            RedactedContentVerificationStatus.Verified =>
                $"verified {report.CheckedSelectionPreviewCount} captured selection preview(s) no longer appear in extracted text",
            RedactedContentVerificationStatus.Warning =>
                $"{report.RemainingSelectionPreviewCount} captured selection preview(s) still appear in extracted text",
            _ when report.CapturedSelectionPreviewCount == 0 =>
                "not checked; no captured selection previews were available",
            _ =>
                "not checked; captured previews were too short for reliable matching"
        };

    private static string FormatMetadataScrub(RedactedCopySafetyReport report)
    {
        if (!report.MetadataScrubbed)
            return "not requested";

        var xmp = report.HadXmpMetadata ? "XMP metadata removed" : "no XMP metadata found";
        return $"{report.InfoFieldsScrubbed} Info field(s) removed; {xmp}";
    }

    private static string FormatEmbeddedFiles(RedactedCopySafetyReport report)
    {
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
            RedactedContentVerificationStatus.Warning => $"{report.HiddenTextFindingCount} finding(s) need manual review",
            _ => "not checked"
        };
}
