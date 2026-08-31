using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.Ocr;
using Excise.Rendering.Differential;

namespace Excise.Cli.Commands;

/// <summary>
/// Owns unredact validation, evidence-channel orchestration, resource lifetime,
/// cancellation checkpoints, typed result construction, and exit status.
/// </summary>
internal static class UnredactCommandHandler
{
    public static UnredactCommandOutcome Execute(
        UnredactCommandInput input,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(input.FilePath))
            return UnredactCommandOutcome.Failure(1, $"File not found: {Path.GetFullPath(input.FilePath)}");

        if (!TryParseMode(input.Mode, out var mode))
            return UnredactCommandOutcome.Failure(2, "--mode must be certain, residue, or both");

        if (mode is UnredactMode.Residue or UnredactMode.Both &&
            (input.DictionaryPath == null || !File.Exists(input.DictionaryPath)))
        {
            return UnredactCommandOutcome.Failure(2, "residue mode needs --dictionary <wordlist>");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var certain = CollectCertain(input, mode, cancellationToken, out var certainError);
            if (certainError != null)
                return certainError;

            cancellationToken.ThrowIfCancellationRequested();
            var residue = CollectResidue(input, mode, cancellationToken);
            var quantification = Quantify(mode, input.NoCorroboration, certain, residue);
            var report = new UnredactReport(quantification, certain, residue);
            var exitCode = certain.Count > 0 ? 3 : residue.Count > 0 ? 4 : 0;
            return new UnredactCommandOutcome(exitCode, report, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return UnredactCommandOutcome.Failure(1, "Operation cancelled.");
        }
        catch (Exception ex)
        {
            return UnredactCommandOutcome.Failure(1, $"Error: {ex.Message}");
        }
    }

    private static List<UnredactCertainFinding> CollectCertain(
        UnredactCommandInput input,
        UnredactMode mode,
        CancellationToken cancellationToken,
        out UnredactCommandOutcome? error)
    {
        error = null;
        var findings = new List<UnredactCertainFinding>();
        if (mode is not (UnredactMode.Certain or UnredactMode.Both))
            return findings;

        using var document = PdfDocument.Open(input.FilePath);
        foreach (var hit in HiddenTextDetector.Scan(document, includeVisibleFailedRedactions: true))
        {
            cancellationToken.ThrowIfCancellationRequested();
            findings.Add(new UnredactCertainFinding(
                hit.PageNumber, hit.Text, hit.HiddenBy,
                Math.Round(hit.BoundingBox.Left, 1),
                Math.Round(hit.BoundingBox.Bottom, 1)));
        }

        // These carriers are physically present and therefore CERTAIN, not a
        // residue estimate (#1179).
        foreach (var carrier in CarrierTextRecovery.Scan(document))
        {
            cancellationToken.ThrowIfCancellationRequested();
            findings.Add(new UnredactCertainFinding(
                carrier.PageNumber, carrier.Text, carrier.Carrier, 0, 0));
        }

        if (!input.UseOcr)
            return findings;

        var ocr = new PdfOcrService(useNativeFastPath: true);
        if (!ocr.IsAvailable())
        {
            error = UnredactCommandOutcome.Failure(
                2,
                "--ocr needs tesseract on PATH (e.g. `brew install tesseract`).");
            return findings;
        }

        var bytes = File.ReadAllBytes(input.FilePath);
        foreach (var hit in new DifferentialOcrAuditor(ocr).Scan(bytes))
        {
            cancellationToken.ThrowIfCancellationRequested();
            findings.Add(new UnredactCertainFinding(
                hit.PageNumber,
                hit.Text,
                "ocr-differential",
                Math.Round(hit.BoundingBox.Left, 1),
                Math.Round(hit.BoundingBox.Bottom, 1),
                Math.Round(hit.Confidence, 1)));
        }

        return findings;
    }

    private static List<UnredactResidueFinding> CollectResidue(
        UnredactCommandInput input,
        UnredactMode mode,
        CancellationToken cancellationToken)
    {
        var findings = new List<UnredactResidueFinding>();
        if (mode is not (UnredactMode.Residue or UnredactMode.Both))
            return findings;

        var dictionary = File.ReadAllLines(input.DictionaryPath!)
            .Select(word => word.Trim())
            .Where(word => word.Length > 0)
            .Distinct()
            .ToList();
        var recoveries = ResidueRecoveryEngine.Recover(
            input.FilePath,
            dictionary,
            new ResidueRecoveryEngine.Options(
                ExactTolerancePt: input.Tolerance,
                MaxCandidates: input.MaxCandidates,
                RequireMutoolCorroboration: !input.NoCorroboration));

        IReadOnlyList<string>? ocrContext = null;
        if (input.UseOcr)
        {
            var ocr = new PdfOcrService(useNativeFastPath: true);
            if (ocr.IsAvailable())
            {
                using var document = PdfDocument.Open(input.FilePath);
                ocrContext = ocr.RecognizePage(document.GetPage(1)).Words
                    .Select(word => word.Text)
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .ToList();
            }
        }

        foreach (var raw in recoveries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var recovery = ocrContext != null
                ? ResidueRecoveryEngine.ApplyContextPrior(raw, ocrContext)
                : raw;
            findings.Add(new UnredactResidueFinding(
                recovery.Gap.Page,
                Math.Round(recovery.Gap.GapWidthPt, 2),
                recovery.Gap.Font,
                recovery.Gap.SizePt,
                recovery.Gap.MetricSource.ToString(),
                recovery.CandidatesFit.Count,
                Math.Round(recovery.ResidualEntropyBits, 2),
                Math.Round(recovery.ContextAdjustedBits, 2),
                recovery.CandidatesFit.Take(20).ToArray(),
                recovery.Status));
        }

        return findings;
    }

    private static UnredactQuantification Quantify(
        UnredactMode mode,
        bool noCorroboration,
        IReadOnlyList<UnredactCertainFinding> certain,
        IReadOnlyList<UnredactResidueFinding> residue)
    {
        var uniqueRecoveries = residue.Count(finding => finding.CandidatesFit == 1);
        var residueBitsTotal = Math.Round(residue.Sum(finding => finding.ResidualEntropyBits), 2);
        var corroboration = mode is UnredactMode.Residue or UnredactMode.Both
            ? noCorroboration
                ? "off — uncorroborated width estimate"
                : "mutool (independent)"
            : "n/a (certain mode)";

        return new UnredactQuantification(
            certain.Count + residue.Count,
            certain.Count,
            residue.Count,
            residueBitsTotal,
            certain.Count + uniqueRecoveries,
            corroboration);
    }

    private static bool TryParseMode(string mode, out UnredactMode parsed)
    {
        switch (mode.ToLowerInvariant())
        {
            case "certain": parsed = UnredactMode.Certain; return true;
            case "residue": parsed = UnredactMode.Residue; return true;
            case "both": parsed = UnredactMode.Both; return true;
            default: parsed = default; return false;
        }
    }
}
