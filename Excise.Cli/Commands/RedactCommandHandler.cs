using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.Ocr;

namespace Excise.Cli.Commands;

/// <summary>
/// Owns the security-sensitive redact-a-file workflow independently of CLI
/// parsing and presentation. Both the interactive command and automation
/// batches call this boundary so their mutation behavior cannot drift.
/// </summary>
internal static class RedactCommandHandler
{
    internal static RedactCommandResult Execute(
        RedactCommandRequest request,
        Action<int, int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Validate(request);
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
                request.CaseSensitive,
                request.Password,
                request.AllowDecrypt,
                guardedProgress);
            cancellationToken.ThrowIfCancellationRequested();
            return new RedactCommandResult(
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

        // #1089/#1187: report verified removals and use the unified Core
        // redaction surface. The confidence oracle remains outside Core because
        // it depends on the optional OCR package.
        var redaction = document.RedactText(request.Text, new RedactionOptions
        {
            CaseSensitive = request.CaseSensitive,
            DrawBox = request.DrawBox,
            Width = request.CloseWidth
                ? WidthPolicy.CloseGap
                : WidthPolicy.CollapsePreserveLayout,
            BoxColor = request.BoxColor,
        }, guardedProgress);

        // #916/#905: collect carriers the surgical CLI term policy could not
        // examine before saving, while the document still reflects the output.
        var carrierNotes = CliRedactedCopySafetyAdapter
            .AuditTermRedaction(document, request.Text)
            .ToList();
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

        foreach (var carrier in redaction.Carriers)
        {
            if (!carrier.Scrubbed)
            {
                carrierNotes.Add(
                    $"NOT SCRUBBED: {carrier.Carrier} -- {carrier.RefusedReason ?? "no reason recorded"}");
            }
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

        return new RedactCommandResult(
            input.FullName,
            outputPath,
            request.Text,
            redaction.VerifiedRemovals,
            Flattened: false,
            carrierNotes,
            diagnostics);
    }

    private static void Validate(RedactCommandRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputPath);
        ArgumentException.ThrowIfNullOrEmpty(request.Text);

        if (!request.DrawBox && request.BoxColor != null)
        {
            throw new ArgumentException(
                "--no-box and --box-color are mutually exclusive: --no-box draws no box to colour.");
        }

        if (request.FlattenOcr &&
            (request.OcrImageText || !request.DrawBox || request.BoxColor != null ||
             request.CloseWidth || request.Strict || request.AllowLowConfidence))
        {
            throw new ArgumentException(
                "--flatten-ocr cannot be combined with structural-redaction box, width, confidence, or OCR-layer options.");
        }
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

internal readonly record struct RedactCommandRequest(
    string InputPath,
    string OutputPath,
    string Text,
    bool CaseSensitive = false,
    bool AllowDecrypt = false,
    bool Strict = false,
    bool AllowLowConfidence = false,
    string? Password = null,
    bool CloseWidth = false,
    bool DrawBox = true,
    (double R, double G, double B)? BoxColor = null,
    bool OcrImageText = false,
    bool FlattenOcr = false);

internal sealed record RedactCommandResult(
    string InputPath,
    string OutputPath,
    string Text,
    int Count,
    bool Flattened,
    IReadOnlyList<string> CarrierNotes,
    IReadOnlyList<string> Diagnostics);

/// <summary>
/// A typed refusal lets automation translate confidence failures without
/// matching exception text.
/// </summary>
internal sealed class LowConfidenceExtractionException(string message)
    : InvalidOperationException(message);

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
