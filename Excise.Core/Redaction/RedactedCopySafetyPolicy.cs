using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Excise.Core.Content;
using Excise.Core.Document;
using Excise.Core.Operations;
using Excise.Core.Primitives;

namespace Excise.Core.Text.Segmentation;

/// <summary>
/// Applies delivery-neutral scrub and audit policy to an already-redacted
/// document. The caller still owns saving, encryption, presentation, and exit
/// behavior.
/// </summary>
public static class RedactedCopySafetyPolicy
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

    private const int MinimumTermLength = 3;
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>Evaluate and, where requested, scrub an in-memory document.</summary>
    public static RedactedCopySafetyReport Evaluate(
        PdfDocument document,
        RedactedCopySafetyRequest request)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.RedactionAreas);
        ArgumentNullException.ThrowIfNull(request.RequestedTerms);
        ArgumentNullException.ThrowIfNull(request.Options);

        var warnings = new List<string>();
        var failedStages = new List<RedactedCopySafetyFailureStage>();
        var options = request.Options;
        var terms = request.RedactionAreas
            .Select(area => area.CapturedText)
            .Concat(request.RequestedTerms)
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .Select(term => NormalizeForSearch(term!))
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (request.SkippedRedactionAreaCount > 0)
        {
            warnings.Add(
                $"{request.SkippedRedactionAreaCount} redaction area(s) were skipped because their page no longer exists.");
        }

        var infoFieldsBefore = options.ScrubMetadata
            ? CountScrubbableInfoFields(document)
            : 0;
        var hadXmpMetadata = options.ScrubMetadata &&
            HasXmpMetadata(document, warnings, failedStages);
        var embeddedFileCountBefore = options.ScrubAttachments
            ? CountEmbeddedFiles(document, warnings, failedStages)
            : 0;
        var metadataScrubbed = false;
        var attachmentsScrubbed = false;

        if (options.ScrubMetadata)
        {
            try
            {
                document.ScrubMetadata(scrubAttachments: options.ScrubAttachments);
                metadataScrubbed = true;
                attachmentsScrubbed = options.ScrubAttachments;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AddFailure(
                    RedactedCopySafetyFailureStage.MetadataScrub,
                    "Metadata scrub could not be completed.",
                    warnings,
                    failedStages);
            }
        }
        else if (options.ScrubAttachments)
        {
            try
            {
                document.ScrubEmbeddedFiles();
                attachmentsScrubbed = true;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AddFailure(
                    RedactedCopySafetyFailureStage.AttachmentScrub,
                    "Embedded-file scrub could not be completed.",
                    warnings,
                    failedStages);
            }
        }

        // Area redaction itself only has geometry, but interactive workflows
        // can capture the selected text before deleting it. Use only those
        // explicit terms for surgical positionless-carrier cleanup (#916/#943).
        if (options.ScrubRequestedTerms && terms.Length > 0)
        {
            try
            {
                PdfDocumentSanitizer.ScrubTerms(document, terms);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AddFailure(
                    RedactedCopySafetyFailureStage.RequestedTermScrub,
                    "Requested text could not be scrubbed from every document carrier.",
                    warnings,
                    failedStages);
            }
        }

        if (options.RunCarrierAudit)
        {
            // Runs after any surgical scrub so it reports what survived, not
            // what was present before the requested policy executed.
            var carrierAudit = RedactionCarrierAudit.Inspect(document, terms);
            if (carrierAudit.HasUnexaminedCarriers)
                warnings.AddRange(carrierAudit.Describe());
        }

        var contentStatus = VerifyRequestedTerms(
            document,
            terms,
            options,
            out var checkedTermCount,
            out var remainingTermCount,
            out var skippedShortTermCount,
            warnings,
            failedStages);
        var hiddenTextStatus = RunHiddenTextAudit(
            document,
            options,
            out var hiddenTextFindingCount,
            warnings,
            failedStages);
        var rasterAuditStatus = RunRasterRedactionAudit(
            document,
            request.RedactionAreas,
            options,
            out var remainingRasterOverlapCount,
            warnings,
            failedStages);

        return new RedactedCopySafetyReport(
            RedactionAreaCount: request.RedactionAreas.Count,
            SkippedRedactionAreaCount: request.SkippedRedactionAreaCount,
            RequestedTermCount: terms.Length,
            CheckedTermCount: checkedTermCount,
            RemainingTermCount: remainingTermCount,
            SkippedShortTermCount: skippedShortTermCount,
            ContentVerificationStatus: contentStatus,
            MetadataScrubbed: metadataScrubbed,
            InfoFieldsScrubbed: metadataScrubbed ? infoFieldsBefore : 0,
            HadXmpMetadata: hadXmpMetadata,
            AttachmentsScrubbed: attachmentsScrubbed,
            EmbeddedFileCountBefore: embeddedFileCountBefore,
            HiddenTextAuditStatus: hiddenTextStatus,
            HiddenTextFindingCount: hiddenTextFindingCount,
            RasterRedactionAuditStatus: rasterAuditStatus,
            RemainingRasterOverlapCount: remainingRasterOverlapCount,
            FailedStages: failedStages,
            Warnings: warnings);
    }

    private static RedactedContentVerificationStatus VerifyRequestedTerms(
        PdfDocument document,
        IReadOnlyList<string> terms,
        RedactedCopySafetyOptions options,
        out int checkedTermCount,
        out int remainingTermCount,
        out int skippedShortTermCount,
        List<string> warnings,
        List<RedactedCopySafetyFailureStage> failedStages)
    {
        checkedTermCount = 0;
        remainingTermCount = 0;
        skippedShortTermCount = 0;

        if (!options.VerifyRequestedTerms || terms.Count == 0)
            return RedactedContentVerificationStatus.NotChecked;

        var checkedTerms = terms
            .Where(term => term.Length >= MinimumTermLength)
            .ToArray();
        skippedShortTermCount = terms.Count - checkedTerms.Length;

        if (checkedTerms.Length == 0)
            return RedactedContentVerificationStatus.NotChecked;

        try
        {
            var documentText = NormalizeForSearch(ExtractDocumentText(document));
            checkedTermCount = checkedTerms.Length;
            remainingTermCount = checkedTerms.Count(term =>
                documentText.Contains(term, StringComparison.OrdinalIgnoreCase));

            if (remainingTermCount > 0)
            {
                warnings.Add(
                    $"{remainingTermCount} requested redaction term(s) still appear in extracted page text.");
                return RedactedContentVerificationStatus.Warning;
            }

            return RedactedContentVerificationStatus.Verified;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AddFailure(
                RedactedCopySafetyFailureStage.ContentVerification,
                "Removed-text verification could not be completed.",
                warnings,
                failedStages);
            return RedactedContentVerificationStatus.Warning;
        }
    }

    private static RedactedContentVerificationStatus RunHiddenTextAudit(
        PdfDocument document,
        RedactedCopySafetyOptions options,
        out int hiddenTextFindingCount,
        List<string> warnings,
        List<RedactedCopySafetyFailureStage> failedStages)
    {
        hiddenTextFindingCount = 0;
        if (!options.RunHiddenTextAudit)
            return RedactedContentVerificationStatus.NotChecked;

        try
        {
            hiddenTextFindingCount = HiddenTextDetector
                .Scan(document, includeVisibleFailedRedactions: true)
                .Count;
            if (hiddenTextFindingCount > 0)
            {
                warnings.Add(
                    $"{hiddenTextFindingCount} structurally hidden text finding(s) remain for manual review.");
                return RedactedContentVerificationStatus.Warning;
            }

            return RedactedContentVerificationStatus.Verified;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AddFailure(
                RedactedCopySafetyFailureStage.HiddenTextAudit,
                "Hidden-text audit could not be completed.",
                warnings,
                failedStages);
            return RedactedContentVerificationStatus.Warning;
        }
    }

    private static RedactedContentVerificationStatus RunRasterRedactionAudit(
        PdfDocument document,
        IReadOnlyList<RedactedCopySafetyArea> areas,
        RedactedCopySafetyOptions options,
        out int remainingRasterOverlapCount,
        List<string> warnings,
        List<RedactedCopySafetyFailureStage> failedStages)
    {
        remainingRasterOverlapCount = 0;
        if (!options.RunRasterRedactionAudit || areas.Count == 0)
            return RedactedContentVerificationStatus.NotChecked;

        try
        {
            foreach (var area in areas)
            {
                if (area.PageNumber < 1 || area.PageNumber > document.PageCount)
                    continue;

                var page = document.GetPage(area.PageNumber);
                var contentArea = PdfCoordinateMapper
                    .ToContentPoints(page, area.PageArea)
                    .ToPdfRectangle()
                    .Normalize();
                remainingRasterOverlapCount += CountRasterOverlaps(page, contentArea);
            }

            if (remainingRasterOverlapCount > 0)
            {
                warnings.Add(
                    $"{remainingRasterOverlapCount} raster image invocation(s) still overlap redaction area(s); manual review or raster redaction is required.");
                return RedactedContentVerificationStatus.Warning;
            }

            return RedactedContentVerificationStatus.Verified;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AddFailure(
                RedactedCopySafetyFailureStage.RasterRedactionAudit,
                "Raster redaction audit could not be completed.",
                warnings,
                failedStages);
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
                    if (ctmStack.Count > 0)
                        ctm = ctmStack.Pop();
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

    private static bool HasXmpMetadata(
        PdfDocument document,
        List<string> warnings,
        List<RedactedCopySafetyFailureStage> failedStages)
    {
        try
        {
            return document.GetXmpMetadata() != null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AddFailure(
                RedactedCopySafetyFailureStage.MetadataInspection,
                "XMP metadata could not be inspected before scrub.",
                warnings,
                failedStages);
            return false;
        }
    }

    private static int CountEmbeddedFiles(
        PdfDocument document,
        List<string> warnings,
        List<RedactedCopySafetyFailureStage> failedStages)
    {
        try
        {
            return document.GetEmbeddedFiles().Count;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AddFailure(
                RedactedCopySafetyFailureStage.AttachmentInspection,
                "Embedded files could not be inspected before scrub.",
                warnings,
                failedStages);
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

    private static void AddFailure(
        RedactedCopySafetyFailureStage stage,
        string warning,
        List<string> warnings,
        List<RedactedCopySafetyFailureStage> failedStages)
    {
        warnings.Add(warning);
        if (!failedStages.Contains(stage))
            failedStages.Add(stage);
    }

    private static PdfRectangle TransformedUnitSquareAabb(Matrix23 matrix)
    {
        var p00 = matrix.Transform(0, 0);
        var p10 = matrix.Transform(1, 0);
        var p01 = matrix.Transform(0, 1);
        var p11 = matrix.Transform(1, 1);
        var minX = Math.Min(Math.Min(p00.X, p10.X), Math.Min(p01.X, p11.X));
        var maxX = Math.Max(Math.Max(p00.X, p10.X), Math.Max(p01.X, p11.X));
        var minY = Math.Min(Math.Min(p00.Y, p10.Y), Math.Min(p01.Y, p11.Y));
        var maxY = Math.Max(Math.Max(p00.Y, p10.Y), Math.Max(p01.Y, p11.Y));
        return new PdfRectangle(minX, minY, maxX, maxY);
    }

    private readonly struct Matrix23
    {
        private readonly double _a;
        private readonly double _b;
        private readonly double _c;
        private readonly double _d;
        private readonly double _e;
        private readonly double _f;

        public Matrix23(double a, double b, double c, double d, double e, double f)
        {
            _a = a;
            _b = b;
            _c = c;
            _d = d;
            _e = e;
            _f = f;
        }

        public static Matrix23 Identity => new(1, 0, 0, 1, 0, 0);

        public (double X, double Y) Transform(double x, double y) =>
            (_a * x + _c * y + _e, _b * x + _d * y + _f);

        public Matrix23 Multiply(Matrix23 other) => new(
            _a * other._a + _b * other._c,
            _a * other._b + _b * other._d,
            _c * other._a + _d * other._c,
            _c * other._b + _d * other._d,
            _e * other._a + _f * other._c + other._e,
            _e * other._b + _f * other._d + other._f);
    }
}
