using Excise.Core.Document;
using Excise.Core.Redaction.Recovery;
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
            // #1587: ONE recovery model, filled by every channel. The builder is
            // threaded through the collectors rather than reconstructed from
            // their output, because a finding's mark link and its location are
            // known at the point the channel produces it and are guesswork
            // afterwards.
            var builder = new RecoveryReportBuilder();

            cancellationToken.ThrowIfCancellationRequested();
            var certain = CollectCertain(input, mode, builder, cancellationToken, out var certainError);
            if (certainError != null)
                return certainError;

            cancellationToken.ThrowIfCancellationRequested();
            var residue = CollectResidue(input, mode, builder, cancellationToken);
            DeclareUnrunChannels(input, mode, builder);

            var recovery = UnredactRecoveryMapper.Map(builder.Build());
            var quantification = Quantify(mode, input.NoCorroboration, certain, residue, recovery);
            var report = new UnredactReport(quantification, certain, residue, recovery);
            return new UnredactCommandOutcome(ExitCodeFor(certain, residue, recovery), report, null);
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
        RecoveryReportBuilder builder,
        CancellationToken cancellationToken,
        out UnredactCommandOutcome? error)
    {
        error = null;
        var findings = new List<UnredactCertainFinding>();
        if (mode is not (UnredactMode.Certain or UnredactMode.Both))
            return findings;

        using var document = PdfDocument.Open(input.FilePath);

        // #1587: the document-only channels -- hidden text, carriers,
        // marked-content, covered image/vector, form fields -- plus the marks
        // they were found under. The legacy flat list below is rebuilt from the
        // same report so the two can never disagree.
        RecoveryScanner.ScanInto(document, builder, cancellationToken);

        foreach (var hit in HiddenTextDetector.Scan(document, includeVisibleFailedRedactions: true))
        {
            cancellationToken.ThrowIfCancellationRequested();
            findings.Add(new UnredactCertainFinding(
                hit.PageNumber, hit.Text, hit.HiddenBy,
                Math.Round(hit.BoundingBox.Left, 1),
                Math.Round(hit.BoundingBox.Bottom, 1)));
        }

        // #1592: the prior-revision channel needs the file's literal bytes (it
        // truncates at an earlier %%EOF), which an open PdfDocument cannot give.
        AddPriorRevision(input, builder, cancellationToken);

        // These carriers are physically present and therefore CERTAIN, not a
        // residue estimate (#1179).
        foreach (var carrier in CarrierTextRecovery.Scan(document))
        {
            cancellationToken.ThrowIfCancellationRequested();
            findings.Add(new UnredactCertainFinding(
                carrier.PageNumber, carrier.Text, carrier.Carrier, 0, 0));
        }

        if (!input.UseOcr)
        {
            builder.ChannelSkipped(
                RecoveryScanner.Channels.OcrDifferential,
                "not requested (--ocr)");
            return findings;
        }

        var ocr = new PdfOcrService(useNativeFastPath: true);
        if (!ocr.IsAvailable())
        {
            error = UnredactCommandOutcome.Failure(
                2,
                "--ocr needs tesseract on PATH (e.g. `brew install tesseract`).");
            return findings;
        }

        builder.ChannelRan(RecoveryScanner.Channels.OcrDifferential);
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

            // OCR read pixels, not bytes, so the text is a RECOGNITION: right
            // often enough to act on, wrong often enough that asserting it as
            // certain would be a lie with a confidence score attached. It is a
            // candidate of one, and the report says so.
            builder.AddFinding(RecoveredFinding.Candidate(
                RecoveryScanner.Channels.OcrDifferential,
                "OCR differential (obstruction stripped)",
                new[] { hit.Text },
                0,
                new RecoveryLocation(hit.PageNumber, hit.BoundingBox, "OCR word box")));
        }

        return findings;
    }

    /// <summary>
    /// #1592 — text earlier revisions of the file still hold. An incremental
    /// update leaves the pre-redaction document whole at the front of the file.
    /// </summary>
    private static void AddPriorRevision(
        UnredactCommandInput input, RecoveryReportBuilder builder, CancellationToken cancellationToken)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(input.FilePath); }
        catch
        {
            builder.ChannelSkipped(
                RecoveryScanner.Channels.PriorRevision, "could not re-read the file bytes");
            return;
        }

        var (findings, summary) = PriorRevisionRecovery.Scan(bytes, cancellationToken);
        builder.ChannelRan(RecoveryScanner.Channels.PriorRevision);

        // An earlier revision excise cannot open is NOT evidence that it is
        // clean -- another tool may well read it -- so the shortfall is
        // reported rather than swallowed.
        if (summary.RevisionsUnreadable > 0)
        {
            builder.ChannelSkipped(
                RecoveryScanner.Channels.PriorRevision + " (partial)",
                $"{summary.RevisionsUnreadable} of {summary.RevisionCount} revision(s) would not parse");
        }

        foreach (var finding in findings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.AddFinding(RecoveredFinding.Certain(
                RecoveryScanner.Channels.PriorRevision,
                $"revision {finding.RevisionIndex} of {summary.RevisionCount}",
                finding.Text,
                new RecoveryLocation(finding.PageNumber, finding.Rect, "prior-revision glyph boxes")));
        }
    }

    /// <summary>
    /// #1587 — name the channels that did NOT run, and why. Without this a
    /// report over four channels reads exactly like one over nine, which is the
    /// overstatement the Coverage rule (#1181) exists to prevent.
    /// </summary>
    private static void DeclareUnrunChannels(
        UnredactCommandInput input, UnredactMode mode, RecoveryReportBuilder builder)
    {
        if (mode is not (UnredactMode.Certain or UnredactMode.Both))
        {
            foreach (var channel in new[]
                     {
                         RecoveryScanner.Channels.HiddenText, RecoveryScanner.Channels.Carrier,
                         RecoveryScanner.Channels.MarkedContent, RecoveryScanner.Channels.CoveredImage,
                         RecoveryScanner.Channels.CoveredVector, RecoveryScanner.Channels.FormField,
                     })
            {
                builder.ChannelSkipped(channel, "--mode residue");
            }
        }

        if (mode is not (UnredactMode.Residue or UnredactMode.Both))
            builder.ChannelSkipped(RecoveryScanner.Channels.Residue, "--mode certain");

        // Not implemented yet, and saying so is the honest report. Silence here
        // would read as "this document has no XFA data", which is a claim
        // nothing in this run checked.
        builder.ChannelSkipped(RecoveryScanner.Channels.Xfa, "not implemented (#1609)");
    }

    private static List<UnredactResidueFinding> CollectResidue(
        UnredactCommandInput input,
        UnredactMode mode,
        RecoveryReportBuilder builder,
        CancellationToken cancellationToken)
    {
        var findings = new List<UnredactResidueFinding>();
        if (mode is not (UnredactMode.Residue or UnredactMode.Both))
            return findings;

        builder.ChannelRan(RecoveryScanner.Channels.Residue);

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

            // #1587: the gap into the shared model. The engine reports a span
            // on one baseline, not a rectangle, so the box is that span at the
            // anchoring glyph's line -- enough to link it to a mark and to
            // place a note in a restored copy, and no more than the channel
            // actually knows.
            var gapRect = new Core.Document.PdfRectangle(
                recovery.Gap.X0, 0, recovery.Gap.X1, recovery.Gap.SizePt);
            builder.AddFinding(
                RecoveredFinding.Candidate(
                    RecoveryScanner.Channels.Residue,
                    $"width residue ({recovery.Gap.Kind})",
                    recovery.CandidatesFit.Take(20).ToArray(),
                    recovery.ResidualEntropyBits,
                    new RecoveryLocation(recovery.Gap.Page, gapRect, "residue gap")),
                gapRect);
        }

        return findings;
    }

    /// <summary>
    /// Exit status over EVERY channel, not just the two legacy lists.
    ///
    /// <para>3 = text was recovered, 4 = something is constrained or present but
    /// nothing was read, 0 = nothing found. The channels added in #1587/#1592 —
    /// prior revision, marked content, form fields, thumbnails, attachments —
    /// report only into the recovery model, so a status computed from the legacy
    /// lists alone returned 0 over a document whose redacted name this tool had
    /// just recovered. A script checking the exit code would have called that
    /// file clean. Measured on an incremental-update fixture before the fix;
    /// pinned by <c>UnredactExitStatusTests</c>.</para>
    /// </summary>
    private static int ExitCodeFor(
        IReadOnlyList<UnredactCertainFinding> certain,
        IReadOnlyList<UnredactResidueFinding> residue,
        UnredactRecoveryModel recovery)
    {
        var all = recovery.Linked.Concat(recovery.Unlinked).Concat(recovery.DocumentLevel).ToList();
        if (certain.Count > 0 || all.Any(f => f.Confidence == "certain")) return 3;
        if (residue.Count > 0 || all.Count > 0) return 4;
        return 0;
    }

    /// <summary>
    /// The headline counts, over EVERY channel.
    ///
    /// <para>These used to count only the two legacy lists, which meant a
    /// document whose name the prior-revision or marked-content channel had
    /// recovered still printed "0 finding(s), 0 RECOVERED". The model-only
    /// channels are counted here so the headline cannot contradict the per-mark
    /// summary printed directly above it.</para>
    /// </summary>
    private static UnredactQuantification Quantify(
        UnredactMode mode,
        bool noCorroboration,
        IReadOnlyList<UnredactCertainFinding> certain,
        IReadOnlyList<UnredactResidueFinding> residue,
        UnredactRecoveryModel recovery)
    {
        var uniqueRecoveries = residue.Count(finding => finding.CandidatesFit == 1);

        // Channels that report ONLY into the model. The legacy lists already
        // hold hidden-text and carrier findings, so counting those again would
        // double them.
        var legacyChannels = new[]
        {
            RecoveryScanner.Channels.HiddenText,
            RecoveryScanner.Channels.Carrier,
            RecoveryScanner.Channels.Residue,
            RecoveryScanner.Channels.OcrDifferential,
        };
        var modelOnly = recovery.Linked
            .Concat(recovery.Unlinked)
            .Concat(recovery.DocumentLevel)
            .Where(f => !legacyChannels.Contains(f.Channel, StringComparer.Ordinal))
            .ToList();
        var modelCertain = modelOnly.Count(f => f.Confidence == "certain");
        var residueBitsTotal = Math.Round(residue.Sum(finding => finding.ResidualEntropyBits), 2);
        var corroboration = mode is UnredactMode.Residue or UnredactMode.Both
            ? noCorroboration
                ? "off — uncorroborated width estimate"
                : "mutool (independent)"
            : "n/a (certain mode)";

        return new UnredactQuantification(
            certain.Count + residue.Count + modelOnly.Count,
            certain.Count + modelCertain,
            residue.Count,
            residueBitsTotal,
            certain.Count + uniqueRecoveries + modelCertain,
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
