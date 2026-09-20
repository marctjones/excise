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

        // #1707 — refused, never defaulted. Silently treating a misspelt
        // threshold as "any" would make a caller's pipeline look stricter than
        // it is; treating it as "text" would make it silently laxer.
        if (!TryParseFailOn(input.FailOn, out var failOn))
            return UnredactCommandOutcome.Failure(2, "--fail-on must be any, text, constrained, or present");

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
            var present = new List<UnredactPresenceFinding>();
            var duplicates = new List<UnredactCertainFinding>();
            var certain = CollectCertain(
                input, mode, builder, present, duplicates, cancellationToken, out var certainError);
            if (certainError != null)
                return certainError;

            cancellationToken.ThrowIfCancellationRequested();
            var residue = CollectResidue(input, mode, builder, cancellationToken);
            DeclareUnrunChannels(input, mode, builder);

            var recoveryReport = builder.Build();
            var recovery = UnredactRecoveryMapper.Map(recoveryReport);

            UnredactRestoreResult? restore = null;
            if (input.RestorePath != null)
            {
                var restored = WriteRestoredCopy(input, recoveryReport, out var restoreError);
                if (restoreError != null) return restoreError;
                restore = restored;
            }
            var quantification = Quantify(mode, input.NoCorroboration, certain, residue, recovery);
            // ⚠️ #1690 — derived from the channels this run actually skipped,
            // never asserted. `--ocr` alone leaves the image channels deferred
            // and the OCR one not; a fixed sentence would have claimed a blind
            // spot this report does not have, which is the same species of
            // error as claiming coverage it does not have.
            //
            // ⚠️ Keyed on the REASON, not just the channel name. In
            // `--mode residue` every Tier 1 channel is skipped too, and the
            // deferred ones are skipped for THAT reason, not for #1690 —
            // telling the reader to "opt in with --include-deferred" there
            // would name a flag that changes nothing in that mode. That is the
            // same error this replaced the const string to remove: a report
            // describing a blind spot it does not have in the form stated. The
            // far larger Tier 1 hole in residue mode is already declared,
            // channel by channel, on the coverage line.
            var deferredSkips = recovery.ChannelsSkipped
                .Where(entry => RecoveryChannelTiers.IsDeferred(entry.Key) &&
                                entry.Value == RecoveryChannelTiers.DeferralReason(entry.Key))
                .Select(entry => entry.Key);
            var limitations = RecoveryChannelTiers.LimitationsFor(deferredSkips);
            var report = new UnredactReport(
                quantification, certain, residue, recovery, restore,
                present.Count > 0 ? present : null,
                input.IncludeVisibleCarriers ? duplicates : null,
                limitations.Count > 0 ? limitations : null);
            // The exit code reads EVERY channel, not just the two lists: a
            // model-only channel (a prior revision, an XFA value) is a recovery
            // and must not exit 0. See ExitCodeFor.
            return new UnredactCommandOutcome(
                ExitCodeFor(certain, residue, recovery, failOn), report, null);
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
        List<UnredactPresenceFinding> present,
        List<UnredactCertainFinding> duplicates,
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
        // #1589: --dictionary already exists for residue mode; the same word
        // list ranks candidates for every mark that held.
        var dictionary = LoadDictionary(input.DictionaryPath);
        // #1665: the bytes enable the prior-revision channel. Passing them here
        // is what makes the CLI and every library caller run the SAME set —
        // this handler used to hold the only correct call.
        byte[]? pdfBytes = null;
        try { pdfBytes = File.ReadAllBytes(input.FilePath); }
        catch { /* ScanInto declares the channel skipped when bytes are absent */ }
        // #1690: the TEXT focus. `--include-deferred` is what brings the image
        // channels back; without it ScanInto declares them skipped WITH their
        // reason, never silently absent.
        RecoveryScanner.ScanInto(
            document, builder, cancellationToken, dictionary, pdfBytes,
            input.IncludeDeferred
                ? RecoveryScanOptions.IncludingDeferred
                : RecoveryScanOptions.Default);

        foreach (var hit in HiddenTextDetector.Scan(document, includeVisibleFailedRedactions: true))
        {
            cancellationToken.ThrowIfCancellationRequested();
            findings.Add(new UnredactCertainFinding(
                hit.PageNumber, hit.Text, hit.HiddenBy,
                Math.Round(hit.BoundingBox.Left, 1),
                Math.Round(hit.BoundingBox.Bottom, 1),
                // This path predates the recovery model and has no mark link,
                // so the classifier is told so explicitly rather than guessing.
                Class: RecoveryFindingClassifier.Classify(
                    RecoveryScanner.Channels.HiddenText, hit.HiddenBy, isLinkedToAMark: false)));
        }

        // #1592: the prior-revision channel needs the file's literal bytes (it
        // truncates at an earlier %%EOF), which an open PdfDocument cannot give.

        // These carriers are physically present and therefore CERTAIN, not a
        // residue estimate (#1179). Only text a reader cannot already see is a
        // finding; a carrier restating visible text (a filled field's value, the
        // title) is a duplicate, listed only with --carriers all. Presence notes
        // (an opaque attachment, a thumbnail) are listed separately and never
        // counted as recovered text. The scan ranks hidden findings next to a
        // redaction mark first.
        foreach (var carrier in CarrierTextRecovery.Scan(document, cancellationToken))
        {
            int? obj = carrier.ObjectNumber > 0 ? carrier.ObjectNumber : null;
            var proximity = carrier.NearRedaction switch
            {
                CarrierTextRecovery.CarrierRedactionProximity.Overlapping => "overlaps redaction mark",
                CarrierTextRecovery.CarrierRedactionProximity.SamePage => "page has redaction marks",
                _ => null,
            };
            if (carrier.Kind == CarrierTextRecovery.CarrierFindingKind.Presence)
            {
                present.Add(new UnredactPresenceFinding(
                    carrier.PageNumber, carrier.Carrier, carrier.Text, obj, carrier.Location));
                continue;
            }
            var finding = new UnredactCertainFinding(
                carrier.PageNumber, carrier.Text, carrier.Carrier, 0, 0,
                Object: obj, Location: carrier.Location, Proximity: proximity,
                Class: RecoveryFindingClassifier.Classify(
                    RecoveryScanner.Channels.Carrier, carrier.Carrier, isLinkedToAMark: false))
            {
                FromCarrier = true,
            };
            if (carrier.VisibleElsewhere)
                duplicates.Add(finding);
            else
                findings.Add(finding);
        }

        if (!input.UseOcr)
        {
            // #1690: the OCR differential is DEFERRED, and the reason comes
            // from the tier authority so the CLI, the engine and the bench
            // quote the same sentence and the same flag.
            builder.ChannelSkipped(
                RecoveryScanner.Channels.OcrDifferential,
                RecoveryChannelTiers.DeferralReason(RecoveryScanner.Channels.OcrDifferential));
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
    /// #1587 — name the channels that did NOT run, and why. Without this a
    /// report over four channels reads exactly like one over nine, which is the
    /// overstatement the Coverage rule (#1181) exists to prevent.
    /// </summary>
    private static void DeclareUnrunChannels(
        UnredactCommandInput input, UnredactMode mode, RecoveryReportBuilder builder)
    {
        if (mode is not (UnredactMode.Certain or UnredactMode.Both))
        {
            // DERIVED, not hand-listed. This used to name six channels and
            // omit the other seven, so `--mode residue` produced a report that
            // declared six skips and silently ran none of the rest — a report
            // over one channel reading like one over seven. Adding a channel to
            // Channels.All is now enough.
            foreach (var channel in RecoveryScanner.Channels.All)
            {
                if (channel == RecoveryScanner.Channels.Residue) continue;
                builder.ChannelSkipped(channel, "--mode residue");
            }
        }

        if (mode is not (UnredactMode.Residue or UnredactMode.Both))
            builder.ChannelSkipped(RecoveryScanner.Channels.Residue, "--mode certain");
    }

    /// <summary>
    /// #1588 — write the rebuilt PDF. Refuses to overwrite the input: the whole
    /// value of a restored copy is that the original is still there to compare
    /// it against, and a tool that can destroy its own evidence is not one you
    /// hand a records officer.
    /// </summary>
    private static UnredactRestoreResult? WriteRestoredCopy(
        UnredactCommandInput input,
        Core.Redaction.Recovery.RecoveryReport report,
        out UnredactCommandOutcome? error)
    {
        error = null;
        var destination = Path.GetFullPath(input.RestorePath!);
        var source = Path.GetFullPath(input.FilePath);
        if (string.Equals(destination, source, StringComparison.OrdinalIgnoreCase))
        {
            error = UnredactCommandOutcome.Failure(
                2, "--restore must not overwrite the input; choose a different path.");
            return null;
        }

        try
        {
            // A SECOND open: Apply draws onto the document it is given, and the
            // scan's document has already been disposed. Re-opening also keeps
            // the analysed document pristine, so nothing the restore does can
            // feed back into what was reported.
            using var document = PdfDocument.Open(input.FilePath);
            var result = Core.Redaction.Recovery.RestoredCopyBuilder.Apply(document, report);
            document.Save(destination);
            return new UnredactRestoreResult(
                destination, result.ItemsDrawn, result.DocumentLevelItems, result.SummaryPageAdded, result.UndrawableCharacters);
        }
        catch (Exception ex)
        {
            error = UnredactCommandOutcome.Failure(1, $"--restore failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Word list for #1589 candidate ranking, or null when none was given.</summary>
    private static IReadOnlyList<string>? LoadDictionary(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            return File.ReadAllLines(path)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        catch { return null; }
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
    /// #1707 — the strongest thing this run established, as a classification.
    ///
    /// <para>The exit status is a function of (evidence kind × <b>mark link</b>),
    /// never of "is it text" and never of the channel's tier. Tier is a grading
    /// POLICY (#1690, #1708); a leak is a leak whether or not we grade the
    /// channel that found it.</para>
    ///
    /// <para><b>The defect this replaced.</b> The old rule was
    /// <c>all.Any(f =&gt; f.Confidence != "present-only") ? 4 : 0</c>, defending a
    /// real case — a page thumbnail is furniture, and failing a caller's
    /// pipeline over one would make the status useless — with a rule that could
    /// not tell furniture from a breach. An intact image under an opaque box,
    /// the commonest viewer-based "redaction", exited <b>0</b>. The
    /// discriminator was already in the model and was never read:
    /// <c>RecoveredFinding.MarkId</c>. Present-only with NO mark link still
    /// exits 0, which keeps the thumbnail reasoning and spends it only where it
    /// is true.</para>
    ///
    /// <para>⚠️ <b>This is a deliberate exception to #1187's "defaults
    /// reproduce prior behaviour".</b> Material surviving under a mark now
    /// exits 5 where it exited 0. That change IS the fix; a caller who wants
    /// the old threshold asks for it with <c>--fail-on text</c>.</para>
    /// </summary>
    internal enum UnredactBreach
    {
        /// <summary>Nothing survives under any mark. Exit 0.</summary>
        None = 0,

        /// <summary>
        /// Material survives under a mark and no channel decoded it — image
        /// pixels, a vector drawing, an opaque attachment. Exit 5.
        /// </summary>
        ContentUnderMark = 5,

        /// <summary>
        /// The value under a mark is CONSTRAINED — a width-residue candidate
        /// set, an OCR reading. Exit 4.
        /// </summary>
        ConstrainedUnderMark = 4,

        /// <summary>Verbatim text was read back. Exit 3.</summary>
        TextRecovered = 3,
    }

    /// <summary>
    /// How much evidence about the VALUE a caller is willing to tolerate before
    /// the run fails. The ladder is evidence strength about the value —
    /// <c>text</c> (a reading) &gt; <c>constrained</c> (a candidate set) &gt;
    /// <c>present</c> (intact but undecoded) — not a ranking of harm. An intact
    /// photograph under a box may well be the worse leak; that is why the
    /// DEFAULT is <see cref="Any"/> and a narrower threshold has to be asked
    /// for explicitly.
    /// </summary>
    internal enum UnredactFailOn
    {
        /// <summary>Any breach under a mark fails the run. The default.</summary>
        Any = 0,

        /// <summary>Only verbatim recovered text fails the run.</summary>
        Text,

        /// <summary>Recovered text, or a constrained candidate set under a mark.</summary>
        Constrained,

        /// <summary>Anything surviving under a mark, decoded or not. Same as <see cref="Any"/> today.</summary>
        Present,
    }

    /// <summary>
    /// Exit status over EVERY channel, not just the two legacy lists.
    ///
    /// <para>The channels added in #1587/#1592 — prior revision, marked
    /// content, form fields, thumbnails, attachments — report only into the
    /// recovery model, so a status computed from the legacy lists alone
    /// returned 0 over a document whose redacted name this tool had just
    /// recovered. A script checking the exit code would have called that file
    /// clean. Measured on an incremental-update fixture before the fix; pinned
    /// by <c>UnredactExitStatusTests</c>, and the mark-link half by
    /// <c>UnredactMarkBreachExitTests</c>.</para>
    ///
    /// <para><b>Exit 6</b> — page text held by a carrier with no mark breached
    /// — is specified in #1707 and deliberately NOT implemented here. It cannot
    /// be classified from this data: <c>UnredactCertainFinding</c> (the legacy
    /// list) carries no mark link, so a carrier-only document is
    /// indistinguishable from one whose mark was breached. It belongs to
    /// #1703's single-inventory refactor. The number is reserved rather than
    /// reused, so the contract does not shift under a caller when it lands.</para>
    /// </summary>
    private static int ExitCodeFor(
        IReadOnlyList<UnredactCertainFinding> certain,
        IReadOnlyList<UnredactResidueFinding> residue,
        UnredactRecoveryModel recovery,
        UnredactFailOn failOn)
    {
        var breach = Classify(certain, residue, recovery);
        return Tolerates(failOn, breach) ? 0 : (int)breach;
    }

    private static UnredactBreach Classify(
        IReadOnlyList<UnredactCertainFinding> certain,
        IReadOnlyList<UnredactResidueFinding> residue,
        UnredactRecoveryModel recovery)
    {
        var all = recovery.Linked.Concat(recovery.Unlinked).Concat(recovery.DocumentLevel).ToList();

        if (certain.Count > 0 || all.Any(f => f.Confidence == "certain"))
            return UnredactBreach.TextRecovered;

        // A width-residue gap IS the mark — an emptied region inferred from the
        // glyph advances — so it needs no separate link to count.
        if (residue.Count > 0 || all.Any(f => f.Confidence == "candidate"))
            return UnredactBreach.ConstrainedUnderMark;

        // The #1707 rung. PRESENT-ONLY IS NOT A RECOVERY — but present-only
        // UNDER A MARK is a breach, and that is a different statement. A
        // thumbnail or an attachment sitting elsewhere in the file has no mark
        // id and stays exit 0.
        if (all.Any(f => f.Confidence == "present-only" && !string.IsNullOrEmpty(f.MarkId)))
            return UnredactBreach.ContentUnderMark;

        return UnredactBreach.None;
    }

    internal static bool TryParseFailOn(string? value, out UnredactFailOn failOn)
    {
        failOn = UnredactFailOn.Any;
        if (string.IsNullOrWhiteSpace(value)) return true;
        switch (value.Trim().ToLowerInvariant())
        {
            case "any": failOn = UnredactFailOn.Any; return true;
            case "text": failOn = UnredactFailOn.Text; return true;
            case "constrained": failOn = UnredactFailOn.Constrained; return true;
            case "present": failOn = UnredactFailOn.Present; return true;
            default: return false;
        }
    }

    private static bool Tolerates(UnredactFailOn failOn, UnredactBreach breach) => breach switch
    {
        UnredactBreach.None => true,
        UnredactBreach.TextRecovered => false,
        UnredactBreach.ConstrainedUnderMark => failOn == UnredactFailOn.Text,
        UnredactBreach.ContentUnderMark =>
            failOn is UnredactFailOn.Text or UnredactFailOn.Constrained,
        _ => false,
    };

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

        // ⚠️ #1690 — a DEFERRED channel's finding is REPORTED but not GRADED.
        // It still appears in the model, in the per-mark summary and in the
        // finding lists; it just does not move the headline, because the
        // headline is the text-recovery score and an image reported
        // present-only is not a recovered value. Counting it there would let
        // `--include-deferred` inflate the same document's score with findings
        // that recovered no text.
        //
        // Both halves are filtered, not just the model-only set: an OCR
        // differential hit lands in the LEGACY `certain` list, where it was
        // being counted as "text present" with a recognition's error rate
        // attached.
        var gradedCertain = certain
            .Where(f => !RecoveryChannelTiers.IsDeferred(ChannelOfLegacyFinding(f)))
            .ToList();
        var modelCertain = modelOnly.Count(
            f => f.Confidence == "certain" && !RecoveryChannelTiers.IsDeferred(f.Channel));
        var residueBitsTotal = Math.Round(residue.Sum(finding => finding.ResidualEntropyBits), 2);
        var corroboration = mode is UnredactMode.Residue or UnredactMode.Both
            ? noCorroboration
                ? "off — uncorroborated width estimate"
                : "mutool (independent)"
            : "n/a (certain mode)";

        // Findings COUNTS everything the report lists, including the deferred
        // ones: that number answers "how much is in this report", and hiding
        // rows from it would make the lists below not add up.
        return new UnredactQuantification(
            certain.Count + residue.Count + modelOnly.Count,
            gradedCertain.Count + modelCertain,
            residue.Count,
            residueBitsTotal,
            gradedCertain.Count + uniqueRecoveries + modelCertain,
            corroboration);
    }

    /// <summary>
    /// The channel a LEGACY finding came from. The flat list predates the
    /// recovery model and carries no channel field, so the tier is read from
    /// <c>HiddenBy</c> — which the OCR path sets to the channel name verbatim.
    /// Everything else in that list is hidden text or a carrier, both Tier 1.
    /// </summary>
    private static string ChannelOfLegacyFinding(UnredactCertainFinding finding) =>
        finding.HiddenBy == RecoveryScanner.Channels.OcrDifferential
            ? RecoveryScanner.Channels.OcrDifferential
            : finding.FromCarrier
                ? RecoveryScanner.Channels.Carrier
                : RecoveryScanner.Channels.HiddenText;

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
