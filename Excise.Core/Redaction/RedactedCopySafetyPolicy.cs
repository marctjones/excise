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

        // #1430 — FIRST, before anything below mutates the document.
        //
        // Ordering is the whole point, not a style choice: Evaluate scrubs
        // metadata and embedded files in place. A refusal raised after that
        // would have already destroyed the /Info dictionary and the
        // attachments of a document we are declining to process — "refused"
        // and "modified anyway" at the same time. The product policy
        // (redactionReviewDrafts.safeRedactedCopy) says refuse RATHER THAN
        // silently applying or deleting the marks, so nothing may have
        // happened to the document when we do.
        var unresolvedRedactMarks =
            CountUnresolvedRedactAnnotations(document, warnings, failedStages);
        if (options.RefuseOnUnresolvedRedactAnnotations && unresolvedRedactMarks > 0)
            throw new UnresolvedRedactAnnotationsException(unresolvedRedactMarks);

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

        // #1572: the attachment decisions that can REFUSE run before anything
        // below mutates the document, for the same reason as #1430 above.
        // Removal refuses a portfolio; keeping redacts each kept file with
        // the captured/requested terms, and refuses a nested PDF it cannot
        // redact.
        List<(Excise.Core.Document.PdfAttachmentGraph.Found File, AttachmentRedactionResult Result)>? keptAttachments = null;
        if (options.ScrubAttachments)
        {
            Excise.Core.Document.PdfAttachmentGraph.ThrowIfPortfolio(document);
        }
        else if (options.InspectKeptAttachments)
        {
            keptAttachments = AttachmentCarrierScrubber.RedactKept(
                document, terms, caseSensitive: false, options.WholeWord, depth: 0,
                (nested, term) => nested.RedactText(term, new RedactionOptions
                {
                    WholeWord = options.WholeWord,
                    Carriers = options.Carriers,
                    CarrierPolicy = options.CarrierPolicy,
                    KeepAttachments = true,
                }));
        }

        // #1586: the profile's non-metadata removals. The engine pass already
        // did this, so every call here is a no-op on a document that went
        // through it — which is the point: the rows let the safety report say
        // what a redacted copy no longer contains, and the area path had no
        // other way to say it.
        var profileOptions = RedactionOptions.ForProfile(options.Profile) with
        {
            CarrierPolicy = options.CarrierPolicy,
            Carriers = options.Carriers,
            WholeWord = options.WholeWord,
            KeepAttachments = !options.ScrubAttachments,
        };
        var profileRemovals = RedactionFeatureStripper.Apply(document, profileOptions);

        var infoFieldsBefore = options.ScrubMetadata
            ? CountScrubbableInfoFields(document)
            : 0;
        var hadXmpMetadata = options.ScrubMetadata &&
            HasXmpMetadata(document, warnings, failedStages);
        var metadataScrubbed = false;
        var attachmentsScrubbed = false;
        var pdfAIdentificationPreserved = false;
        var removedAttachments = new List<AttachmentRedactionResult>();

        if (options.ScrubMetadata)
        {
            try
            {
                // #1507: the same wholesale strip, except that a document which
                // arrived identifying itself as PDF/A keeps that identification.
                // Without this the GUI's redacted-copy flow undid the fix at the
                // engine level — RedactArea preserves the identification and
                // this pass, which runs after it and is on by default, deleted
                // the packet again. Every text carrier still goes; what survives
                // is at most pdfaid part/conformance/rev — and the report says
                // so, because "XMP metadata removed" would then overstate what
                // this pass did.
                pdfAIdentificationPreserved =
                    document.ScrubMetadataPreservingPdfAIdentity(scrubAttachments: false);
                metadataScrubbed = true;
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

        // #1572: whatever an earlier area pass removed is on the ledger, and is
        // reported whatever this copy's own attachment choice is — a removal
        // the caller did not expect must still be named.
        removedAttachments.AddRange(document.RedactionLedger.RemovedAttachments);
        if (options.ScrubAttachments)
        {
            // Every attachment, by every route, each one named.
            try
            {
                removedAttachments.AddRange(Excise.Core.Document.PdfAttachmentGraph.RemoveAll(document));
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
                var outcome = PdfDocumentSanitizer.ScrubTerms(
                    document, terms, caseSensitive: false, options.Carriers, options.CarrierPolicy,
                    options.WholeWord);

                // #1169: a carrier the user set to ReportOnly still holds the
                // term, and a refused mode did nothing at all. Both are the
                // user's decision to make and neither may pass silently — the
                // whole point of the option is that the human is told.
                foreach (var row in outcome.NeedingAttention)
                {
                    warnings.Add(row.RefusedReason != null
                        ? $"Carrier {row.Carrier}: {row.RefusedReason} — the redacted text was NOT removed from it."
                        : $"Carrier {row.Carrier}: set to {row.Mode} and it CONTAINS the redacted text — " +
                          "left unchanged on purpose; review it before sharing this copy.");
                }
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

        // #1572: kept attachments — reconciled after the term scrub, which
        // drops a file whose name or description holds a term. A kept file
        // that is still there and unchecked or unclean must not pass silently.
        IReadOnlyList<AttachmentRedactionResult> attachmentResults = removedAttachments;
        if (keptAttachments != null)
        {
            var kept = AttachmentCarrierScrubber.Reconcile(document, keptAttachments);
            attachmentResults = removedAttachments.Concat(kept).ToList();
            foreach (var file in kept.Where(k => !k.IsClean))
                warnings.Add($"Kept attachment {file}.");
        }

        // #1586: an /Alt or /ActualText describing an image an AREA redaction
        // blacked out, with no /MCID link and no removed text to match on, so
        // NEITHER structure-tree pass could check it. Read off the ledger
        // because the GUI calls the void RedactArea overload and drops the
        // engine's report — see PdfDocumentRedactionLedger. Standard keeps the
        // carrier and REPORTS it; a report nobody receives is not one.
        var uncheckableAlt = document.RedactionLedger.UncheckableAlternateText;
        if (uncheckableAlt > 0)
        {
            warnings.Add(
                $"Carrier structure-tree /Alt: {uncheckableAlt} alternate-text element(s) " +
                "describe redacted image content but have no link to it, so they could NOT be " +
                "checked — read them before sharing this copy, or use the maximum output " +
                "profile to drop them.");
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

        // #1586: Maximum destroys the document's accessibility and
        // interactivity, and the issue requires the report to SAY so. A warning,
        // not a quiet field: the user chose a destructive profile and the copy
        // they are about to ship will not read correctly to a screen reader,
        // will not submit, and has no bookmarks, links or comments.
        if (RedactionFeatureStripper.DestroysAccessibility(profileOptions))
        {
            warnings.Add(
                "Maximum profile: this copy is NO LONGER accessible or interactive. Forms and " +
                "annotations are flattened, and bookmarks, links, comments, field names and " +
                "alternate text have been removed. It will not pass PDF/UA and will not read " +
                "correctly to a screen reader.");
        }

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
            EmbeddedFileCountBefore: removedAttachments.Count,
            HiddenTextAuditStatus: hiddenTextStatus,
            HiddenTextFindingCount: hiddenTextFindingCount,
            RasterRedactionAuditStatus: rasterAuditStatus,
            RemainingRasterOverlapCount: remainingRasterOverlapCount,
            FailedStages: failedStages,
            Warnings: warnings,
            UnresolvedRedactAnnotationCount: unresolvedRedactMarks,
            PdfAIdentificationPreserved: pdfAIdentificationPreserved,   // #1507
            Attachments: attachmentResults,                              // #1572
            XfaRemovals: document.RedactionLedger.XfaRemovals.ToList(), // #1574
            Profile: options.Profile,                                    // #1586
            ProfileRemovals: profileRemovals,
            AccessibilityAndInteractivityRemoved:
                RedactionFeatureStripper.DestroysAccessibility(profileOptions));
    }

    /// <summary>
    /// Count incoming <c>/Redact</c> annotations (§12.5.6.23) — reviewer marks
    /// proposing removal, whose content is still entirely present until
    /// applied (#1430).
    /// </summary>
    /// <remarks>
    /// An inspection failure is REPORTED, not treated as zero and not treated
    /// as a refusal. Returning 0 would silently claim the document is clean on
    /// exactly the malformed files where that is least knowable; refusing on
    /// any unreadable annotation would block ordinary saves of documents that
    /// have no marks at all. Surfacing it is the project's "surface, don't
    /// guess" carrier policy, and it reaches the user because a failed stage
    /// sets <see cref="RedactedCopySafetyReport.HasWarnings"/>.
    /// </remarks>
    private static int CountUnresolvedRedactAnnotations(
        PdfDocument document,
        List<string> warnings,
        List<RedactedCopySafetyFailureStage> failedStages)
    {
        try
        {
            var count = 0;
            for (var page = 1; page <= document.PageCount; page++)
                foreach (var annotation in document.GetPage(page).GetAnnotations())
                    if (annotation.Subtype == PdfAnnotationSubtype.Redact)
                        count++;
            return count;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AddFailure(
                RedactedCopySafetyFailureStage.UnresolvedRedactAnnotationInspection,
                "Incoming /Redact annotations could not be inspected, so whether this " +
                "document carries unresolved redaction marks is unknown.",
                warnings,
                failedStages);
            return 0;
        }
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
        var ctm = ContentTransform.Identity;
        var ctmStack = new Stack<ContentTransform>();

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
                        ctm = ContentTransform.FromOperands(op).Multiply(ctm);
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
                        ctm.UnitSquareBounds().IntersectsWith(redactionArea))
                    {
                        count++;
                    }
                    break;
                case "BI":
                    if (ctm.UnitSquareBounds().IntersectsWith(redactionArea))
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
}
