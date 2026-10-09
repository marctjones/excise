using Excise.Core.Document;
using Excise.Core.Text.Segmentation;

namespace Excise.Ocr;

/// <summary>
/// What to redact, from which file, into which file (#1501). The redaction
/// itself is described by one <see cref="RedactionOptions"/>: every front end
/// builds that object from its own inputs (CLI flags, GUI preferences), so the
/// runner never re-derives a carrier policy from a profile label.
/// </summary>
public sealed record TermRedactionRequest(
    string InputPath,
    string OutputPath,
    string Text,
    RedactionOptions Options)
{
    /// <summary>The source's user password, when it has one.</summary>
    public string? Password { get; init; }

    /// <summary>Write an unprotected copy of an encrypted source (#643).</summary>
    public bool AllowDecrypt { get; init; }

    /// <summary>Refuse when no independent extraction oracle is installed (#650).</summary>
    public bool Strict { get; init; }

    /// <summary>Proceed when the confidence check says Severe (#650).</summary>
    public bool AllowLowConfidence { get; init; }

    /// <summary>Add a temporary OCR text layer before redacting (#1186).</summary>
    public bool OcrImageText { get; init; }

    /// <summary>Write an image-only, OCR-located copy instead of a structural redaction (#1186).</summary>
    public bool FlattenOcr { get; init; }
}

/// <summary>
/// Outcome of one <see cref="TermRedactionRunner.Execute"/>: the verified count,
/// every carrier note (survivors, wrapped candidates, NOT SCRUBBED carriers,
/// attachments, whole images) and the run's diagnostics.
/// </summary>
public sealed record TermRedactionResult(
    string InputPath,
    string OutputPath,
    string Text,
    int Count,
    bool Flattened,
    IReadOnlyList<string> CarrierNotes,
    IReadOnlyList<string> Diagnostics,
    bool WholeWord = false,   // #1052 — the match rule is part of the result
    IReadOnlyList<AttachmentRedactionResult>? AttachmentResults = null,   // #1572
    // #1586 — what the profile removed WHOLE, and whether the output is still
    // accessible. A removal made without a term match is destruction the user
    // is entitled to know about.
    IReadOnlyList<RedactedFeatureRemoval>? ProfileRemovals = null,
    bool AccessibilityRemoved = false,
    // #1750: a hyphen- or line-wrapped occurrence excise located but could not
    // structurally remove — still fully readable in the output. Distinct from
    // "excise removed it and a re-read still finds it" (that goes through
    // CarrierNotes' WARNING line); this is "excise never even formed the match".
    bool HasUnremovedWrappedOccurrence = false)
{
    /// <summary>Every attachment removed or kept (#1572); never null.</summary>
    public IReadOnlyList<AttachmentRedactionResult> Attachments =>
        AttachmentResults ?? Array.Empty<AttachmentRedactionResult>();

    /// <summary>What the output profile removed whole (#1586); never null.</summary>
    public IReadOnlyList<RedactedFeatureRemoval> Removals =>
        ProfileRemovals ?? Array.Empty<RedactedFeatureRemoval>();
}

/// <summary>
/// A typed refusal lets automation translate confidence failures without
/// matching exception text.
/// </summary>
public sealed class LowConfidenceExtractionException(string message)
    : InvalidOperationException(message);

/// <summary>
/// The one redact-a-file workflow (#1501): confidence gate, optional OCR
/// layer or image-only flattening, Core's term redaction, carrier reporting,
/// re-encryption and save. <c>excise redact</c>, automation batches and the
/// GUI scripting harness all call it, so what a scripted redaction reports is
/// exactly what the CLI reports.
/// </summary>
/// <remarks>
/// It lives here, not in Core, because the confidence gate needs the
/// independent oracles (mutool, tesseract) that Core's
/// <see cref="RedactionOptions"/> remark assigns to this layer. It throws
/// on failure; each front end maps exceptions to its own surface.
/// </remarks>
public static class TermRedactionRunner
{
    public static TermRedactionResult Execute(
        TermRedactionRequest request,
        Action<int, int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputPath);
        ArgumentException.ThrowIfNullOrEmpty(request.Text);
        ArgumentNullException.ThrowIfNull(request.Options);
        cancellationToken.ThrowIfCancellationRequested();

        var input = new FileInfo(request.InputPath);
        if (!input.Exists)
            throw new FileNotFoundException("The PDF input file does not exist.", input.FullName);

        var outputPath = Path.GetFullPath(request.OutputPath);
        var guardedProgress = CreateProgressCallback(progress, cancellationToken);
        if (request.FlattenOcr)
        {
            var count = new PdfRasterRedactionConverter(new PdfOcrService()).RedactToImageOnly(
                input.FullName,
                outputPath,
                request.Text,
                request.Options.CaseSensitive,
                request.Password,
                request.AllowDecrypt,
                guardedProgress);
            cancellationToken.ThrowIfCancellationRequested();
            // A flattened copy is a fresh image-only PDF: no attachment of the
            // source is carried into it.
            return new TermRedactionResult(
                input.FullName,
                outputPath,
                request.Text,
                count,
                Flattened: true,
                CarrierNotes: [],
                Diagnostics: []);
        }

        using var document = PdfDocumentLifetime.OpenInputForOutput(
            input.FullName,
            outputPath,
            request.Password);

        var diagnostics = new List<string>();
        var reEncryption = request.AllowDecrypt
            ? null
            : document.GetReEncryptionOptions(request.Password);
        if (document.IsEncrypted && request.AllowDecrypt)
        {
            diagnostics.Add(
                "Warning: --allow-decrypt was passed — output will NOT be encrypted, even though " +
                "the source was. Anyone with the file can read it without a password.");
        }
        else if (reEncryption != null)
        {
            diagnostics.Add(
                "Note: source is encrypted; output is re-encrypted with the same permissions and " +
                "the same password (#643). Pass --allow-decrypt to write an unprotected copy instead.");
        }

        var confidence = new RedactionConfidenceChecker().CheckDocument(
            document,
            sourceFilePath: input.FullName);
        diagnostics.AddRange(RedactionConfidencePolicy.Enforce(
            confidence,
            request.Strict,
            request.AllowLowConfidence));
        cancellationToken.ThrowIfCancellationRequested();

        SearchableDocumentResult? ocrResult = null;
        if (request.OcrImageText)
        {
            // #1186: a secret painted into a scan has no PDF text carrier, so
            // normal term redaction cannot locate it. Add a temporary invisible
            // OCR layer, then send those located boxes through the same glyph and
            // image-redaction pipeline.
            var ocr = new PdfOcrService();
            if (!ocr.IsAvailable())
            {
                throw new InvalidOperationException(
                    "--ocr-image-text requires the tesseract CLI. Install tesseract or redact the image area manually.");
            }

            ocrResult = new PdfSearchableConverter(ocr).MakeSearchable(document, force: true);
            cancellationToken.ThrowIfCancellationRequested();
        }

        // #2028, decision 17: AcroForm widgets excise generated for a dynamic XFA form are baked into
        // their pages first, so no hidden or duplicate widget keeps a value the redaction removes.
        var generatedXfaFlattened = Excise.Core.Xfa.PdfXfaLayout.FlattenGeneratedXfaFields(document);

        // #1089/#1187: report verified removals and use the unified Core
        // redaction surface.
        var options = request.Options;
        var redaction = document.RedactText(request.Text, options, guardedProgress);

        // #916/#905: collect carriers the surgical term policy could not
        // examine before saving, while the document still reflects the output.
        // RedactText already scrubbed, verified and handled attachments.
        var carrierNotes = RedactedCopySafetyPolicy.Evaluate(document,
            RedactedCopySafetyRequest.ForTerms(new[] { request.Text }, options, new RedactedCopySafetyOptions
            {
                ScrubMetadata = false,
                ScrubRequestedTerms = false,
                VerifyRequestedTerms = false,
                RunHiddenTextAudit = false,
                RunRasterRedactionAudit = false,
                InspectKeptAttachments = false,
            })).Warnings.ToList();
        if (generatedXfaFlattened != null)
            carrierNotes.Add($"XFA: {generatedXfaFlattened}.");
        if (ocrResult != null)
        {
            carrierNotes.Add(
                $"OCR IMAGE TEXT: added {ocrResult.TotalWordsWritten} invisible OCR word(s) before redaction; " +
                $"{ocrResult.TotalWordsSkippedEncoding} word(s) could not be represented in the OCR layer.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        document.Save(outputPath, reEncryption);

        if (redaction.Survived > 0)
        {
            carrierNotes.Add(
                $"WARNING: {redaction.Survived} occurrence(s) of '{request.Text}' are STILL PRESENT " +
                "after redaction. excise located them and the removal did not land. " +
                "Do not treat this file as redacted.");
        }

        // #1372: occurrences split across a line by a hyphen are structurally
        // invisible to the matcher, so they are STILL PRESENT in the output.
        // Saying so is the point — the reason this leak class went unnoticed is
        // that excise reported plain success over it.
        foreach (var candidate in redaction.HyphenatedCandidates)
        {
            carrierNotes.Add(
                $"NOT REMOVED (hyphen-wrapped): page {candidate.PageNumber} reads {candidate} — " +
                $"'{request.Text}' is split across a line break, so excise could not match it. " +
                "It is still readable in the output by tools that rejoin hyphenated words.");
        }

        // #1750/#1791: a multi-word term continued somewhere other than the
        // next line of its block (another column) is not joined, so this also
        // does not print a bare "0 occurrence(s)" success.
        foreach (var candidate in redaction.WordWrapCandidates)
        {
            carrierNotes.Add(
                $"NOT REMOVED (line-wrapped): page {candidate.PageNumber} reads {candidate} — " +
                $"'{request.Text}' continues across a line break that is not the next line of " +
                "its block, so excise did not match it. It is still fully readable in the output.");
        }

        foreach (var carrier in redaction.Carriers)
        {
            if (!carrier.Scrubbed)
            {
                carrierNotes.Add(
                    $"NOT SCRUBBED: {carrier.Carrier} -- {carrier.RefusedReason ?? "no reason recorded"}");
            }
        }

        // #1572: every attachment is named — removed ones so the user knows
        // what the output no longer carries, kept ones with what was checked.
        foreach (var attachment in redaction.Attachments)
        {
            carrierNotes.Add(attachment.Disposition == AttachmentDisposition.Removed
                ? $"ATTACHMENT REMOVED: {attachment}"
                : $"ATTACHMENT KEPT: {attachment}");
        }

        // #1187/#1195: fail-closed whole-image removal is secure but destructive
        // collateral and therefore must be explicit in the typed outcome.
        if (redaction.ImagesDroppedWhole > 0)
        {
            carrierNotes.Add(
                $"WHOLE IMAGE REMOVED: {redaction.ImagesDroppedWhole} image(s) were deleted " +
                "entirely because region-level redaction is not available for their encoding " +
                "(e.g. JBIG2). The term is gone, but so is the surrounding image content.");
        }

        if (!redaction.IsCleanSuccess)
        {
            carrierNotes.Add(
                "This redaction was NOT clean -- see the notes above. Review the output " +
                "before treating the term as removed.");
        }

        return new TermRedactionResult(
            input.FullName,
            outputPath,
            request.Text,
            redaction.VerifiedRemovals,
            Flattened: false,
            carrierNotes,
            diagnostics,
            redaction.WholeWord,
            redaction.Attachments,
            redaction.Removals,
            redaction.AccessibilityAndInteractivityRemoved,
            // #1750: a term excise located and structurally could not remove —
            // still fully readable in the output — must not be indistinguishable
            // from a clean run at the exit-code level, or a script sees success.
            HasUnremovedWrappedOccurrence:
                redaction.HyphenatedCandidates.Count > 0 || redaction.WordWrapCandidates.Count > 0);
    }

    private static Action<int, int>? CreateProgressCallback(
        Action<int, int>? progress,
        CancellationToken cancellationToken)
    {
        if (progress == null && !cancellationToken.CanBeCanceled)
            return null;

        return (completed, total) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Invoke(completed, total);
        };
    }
}

internal static class RedactionConfidencePolicy
{
    /// <summary>
    /// Decide what #650's confidence check means for this redaction: refuse,
    /// warn, or proceed silently. This is pure policy and has no PDF/oracle I/O.
    /// </summary>
    internal static IReadOnlyList<string> Enforce(
        RedactionConfidenceReport confidence,
        bool strict,
        bool allowLowConfidence)
    {
        if (confidence.Oracle == null)
        {
            if (strict)
            {
                throw new LowConfidenceExtractionException(
                    "--strict requires an independent extraction-confidence check, but neither mutool " +
                    "nor tesseract is on PATH. Install one of them, or drop --strict to proceed unverified.");
            }

            return
            [
                "Warning: redaction could not be independently verified — neither mutool nor tesseract " +
                "is installed. excise's own extraction was used as-is.",
            ];
        }

        if (confidence.ShouldRefuse)
        {
            if (!allowLowConfidence)
            {
                throw new LowConfidenceExtractionException(
                    "excise's own text extraction disagrees sharply with an independent check " +
                    $"({confidence.Oracle}) on this document — the same signature as a real redaction " +
                    "leak. This may be a false alarm, but pass --allow-low-confidence to proceed anyway.");
            }

            return
            [
                $"Warning: proceeding despite a low-confidence extraction check ({confidence.Oracle} " +
                "disagrees sharply with excise's own extraction) — --allow-low-confidence was passed.",
            ];
        }

        if (confidence.ShouldWarn)
        {
            return
            [
                $"Warning: excise's extraction differs somewhat from an independent check ({confidence.Oracle}) " +
                "on one or more pages of this document. Review the result before relying on it.",
            ];
        }

        return [];
    }
}
