using Microsoft.Extensions.Logging;
using Pdfe.Core.Document;
using Pdfe.Core.Primitives;
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
    public bool RunRasterRedactionAudit { get; init; } = true;

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
    RedactedContentVerificationStatus RasterRedactionAuditStatus,
    int RemainingRasterOverlapCount,
    IReadOnlyList<string> Warnings)
{
    public bool HasWarnings =>
        Warnings.Count > 0 ||
        ContentVerificationStatus == RedactedContentVerificationStatus.Warning ||
        HiddenTextAuditStatus == RedactedContentVerificationStatus.Warning ||
        RasterRedactionAuditStatus == RedactedContentVerificationStatus.Warning;
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
        var rasterAuditStatus = RunRasterRedactionAudit(document, requestedRedactions, options, out var remainingRasterOverlapCount, warnings);

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
            RasterRedactionAuditStatus: rasterAuditStatus,
            RemainingRasterOverlapCount: remainingRasterOverlapCount,
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
            $"- Raster redaction audit: {FormatRasterRedactionAudit(report)}",
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

    private RedactedContentVerificationStatus RunRasterRedactionAudit(
        PdfDocument document,
        IReadOnlyCollection<PendingRedaction> requestedRedactions,
        RedactedCopySafetyOptions options,
        out int remainingRasterOverlapCount,
        List<string> warnings)
    {
        remainingRasterOverlapCount = 0;

        if (!options.RunRasterRedactionAudit)
            return RedactedContentVerificationStatus.NotChecked;

        if (requestedRedactions.Count == 0)
            return RedactedContentVerificationStatus.NotChecked;

        try
        {
            foreach (var redaction in requestedRedactions)
            {
                if (redaction.PageNumber < 1 || redaction.PageNumber > document.PageCount)
                    continue;

                var page = document.GetPage(redaction.PageNumber);
                var contentArea = PdfCoordinateMapper
                    .ToContentPoints(page, redaction.PageArea)
                    .ToPdfRectangle()
                    .Normalize();

                remainingRasterOverlapCount += CountRasterOverlaps(page, contentArea);
            }

            if (remainingRasterOverlapCount > 0)
            {
                warnings.Add($"{remainingRasterOverlapCount} raster image invocation(s) still overlap redaction area(s); manual review or raster redaction is required.");
                return RedactedContentVerificationStatus.Warning;
            }

            return RedactedContentVerificationStatus.Verified;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Failed to run raster redaction audit on redacted copy.");
            warnings.Add("Raster redaction audit could not be completed.");
            return RedactedContentVerificationStatus.Warning;
        }
    }

    private static int CountRasterOverlaps(PdfPage page, PdfRectangle redactionArea)
    {
        var count = 0;
        var ctm = Matrix23.Identity;
        var ctmStack = new Stack<Matrix23>();

        foreach (var op in page.GetContentStream().Operators)
        {
            switch (op.Name)
            {
                case "q":
                    ctmStack.Push(ctm);
                    break;
                case "Q":
                    if (ctmStack.Count > 0) ctm = ctmStack.Pop();
                    break;
                case "cm":
                    if (op.Operands.Count >= 6)
                    {
                        var local = new Matrix23(
                            op.GetNumber(0), op.GetNumber(1),
                            op.GetNumber(2), op.GetNumber(3),
                            op.GetNumber(4), op.GetNumber(5));
                        ctm = local.Multiply(ctm);
                    }
                    break;
                case "Do":
                    if (op.Operands.Count == 0)
                        break;

                    var name = op.GetName(0);
                    if (string.IsNullOrEmpty(name))
                        break;

                    if (page.GetXObject(name) is PdfStream stream &&
                        string.Equals(stream.GetNameOrNull("Subtype"), "Image", StringComparison.Ordinal) &&
                        TransformedUnitSquareAabb(ctm).IntersectsWith(redactionArea))
                    {
                        count++;
                    }
                    break;
                case "BI":
                    if (TransformedUnitSquareAabb(ctm).IntersectsWith(redactionArea))
                        count++;
                    break;
            }
        }

        return count;
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

    private static string FormatRasterRedactionAudit(RedactedCopySafetyReport report) =>
        report.RasterRedactionAuditStatus switch
        {
            RedactedContentVerificationStatus.Verified => "no raster image content remains in redaction areas",
            RedactedContentVerificationStatus.Warning => $"{report.RemainingRasterOverlapCount} raster image invocation(s) need manual review",
            _ => "not checked"
        };

    private static PdfRectangle TransformedUnitSquareAabb(Matrix23 m)
    {
        var p00 = m.Transform(0, 0);
        var p10 = m.Transform(1, 0);
        var p01 = m.Transform(0, 1);
        var p11 = m.Transform(1, 1);

        double minX = Math.Min(Math.Min(p00.x, p10.x), Math.Min(p01.x, p11.x));
        double maxX = Math.Max(Math.Max(p00.x, p10.x), Math.Max(p01.x, p11.x));
        double minY = Math.Min(Math.Min(p00.y, p10.y), Math.Min(p01.y, p11.y));
        double maxY = Math.Max(Math.Max(p00.y, p10.y), Math.Max(p01.y, p11.y));

        return new PdfRectangle(minX, minY, maxX, maxY);
    }

    private readonly struct Matrix23
    {
        public readonly double A, B, C, D, E, F;

        public Matrix23(double a, double b, double c, double d, double e, double f)
        {
            A = a;
            B = b;
            C = c;
            D = d;
            E = e;
            F = f;
        }

        public static Matrix23 Identity => new(1, 0, 0, 1, 0, 0);

        public (double x, double y) Transform(double x, double y)
            => (A * x + C * y + E, B * x + D * y + F);

        public Matrix23 Multiply(Matrix23 o) => new(
            A * o.A + B * o.C,
            A * o.B + B * o.D,
            C * o.A + D * o.C,
            C * o.B + D * o.D,
            E * o.A + F * o.C + o.E,
            E * o.B + F * o.D + o.F);
    }
}
