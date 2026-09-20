using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

using Excise.Core.Redaction.Recovery;

namespace Excise.Cli.Commands;

/// <summary>CLI JSON and human presentation for a typed unredact report.</summary>
internal static class UnredactCommandOutput
{
    public static void Write(
        UnredactCommandOutcome outcome,
        bool json,
        TextWriter output,
        TextWriter error)
    {
        if (outcome.Error != null)
        {
            error.WriteLine(outcome.Error);
            return;
        }

        var report = outcome.Report!;
        if (json)
            output.WriteLine(JsonSerializer.Serialize(report, CliJsonContext.Default.UnredactReport));
        else
            WriteHuman(report, output);
    }

    /// <summary>
    /// The heading for one class. The wording is the point: a reader has to be
    /// able to tell why two sections exist without consulting the docs.
    /// </summary>
    private static string Heading(RecoveryFindingClass c, int n) => c switch
    {
        // ⚠️ Says "not visible", NOT "where a redaction was applied". The
        // class covers text under a mark AND invisible text with no mark, and
        // the second has no redaction to have been applied — the earlier
        // wording asserted something untrue of half the section.
        RecoveryFindingClass.RedactionResidue =>
            $"✗ HIDDEN — text present in the file but not visible on the page ({n}):",
        RecoveryFindingClass.ContentCarrier =>
            $"✗ RESTATED — a carrier still holds page content ({n}):",
        _ =>
            $"· metadata and navigation — present in most documents, redacted or not ({n}):",
    };

    /// <summary>
    /// A one-line caveat when one carrier dominates a section — ranking
    /// without hiding, which is the whole design of #1669.
    ///
    /// <para>A scanned filing produced <b>191</b> render-mode-3 findings and
    /// another produced 13,686. Every one is real: the text IS invisible and
    /// extractable. But an OCR layer is how scanning works, and a reader who
    /// is not told that reads 191 lines as 191 redaction failures. Saying so
    /// costs one line and is the difference between a report and a dump.</para>
    ///
    /// <para>⚠️ Nothing is removed. The count stays in the heading and every
    /// finding is still printed — a caveat that suppressed its subject would
    /// be the silent-leak trade this design exists to refuse.</para>
    /// </summary>
    private static IEnumerable<string> Caveats(IReadOnlyList<UnredactCertainFinding> group)
    {
        var ocr = group.Count(f => f.HiddenBy.Contains("render mode 3", StringComparison.Ordinal));
        if (ocr > 0 && ocr >= group.Count / 2)
            yield return
                $"  ⓘ {ocr} of these are an invisible text layer (render mode 3). That is how a " +
                "SCANNED page carries its text, and on a document nobody redacted it is normal — " +
                "but it is also exactly how text is hidden deliberately, so it is reported.";
    }

    /// <summary>
    /// #1690 — what this report does NOT cover, in the reader's own terms.
    /// Built by the engine from the channels actually skipped; the CLI only
    /// prints it, so the wording cannot drift from the tier decision.
    /// </summary>
    private static void WriteLimitations(IReadOnlyList<string>? limitations, TextWriter output)
    {
        if (limitations is not { Count: > 0 }) return;
        output.WriteLine("  ⚠ NOT COVERED BY THIS REPORT:");
        foreach (var line in limitations)
            output.WriteLine($"    • {line}");
    }

    private static string CarrierLine(UnredactCertainFinding finding) =>
        $"  {Where(finding.Page, finding.Object, finding.Location)} " +
        $"[{finding.HiddenBy}]" +
        (finding.Proximity is null ? "" : $" [{finding.Proximity}]") +
        $": \"{finding.Text}\"";

    private static string Where(int page, int? obj, string? location)
    {
        var where = page > 0 ? $"page {page}" : "document";
        if (obj is { } n) where += $" obj {n}";
        if (!string.IsNullOrEmpty(location)) where += $" ({location})";
        return where;
    }

    private static void WriteHuman(UnredactReport report, TextWriter output)
    {
        var quantification = report.Quantification;
        WriteRecoveryModel(report.Recovery, output);

        // The all-clear must account for EVERY channel. The legacy Certain and
        // Residue lists predate #1587/#1592, and the channels added there report
        // only into the recovery model -- so this line printed the green tick
        // over a document whose redacted name the prior-revision channel had
        // just recovered. An all-clear that cannot see a channel is the exact
        // false reassurance this tool exists to remove.
        if (report.Restore is { } restore)
        {
            output.WriteLine(
                $"RESTORED → {restore.Path} — {restore.ItemsDrawn} item(s) drawn in place" +
                (restore.DocumentLevelItems > 0
                    ? $", {restore.DocumentLevelItems} document-level item(s) " +
                      (restore.SummaryIncluded ? "in a summary note" : "NOT included")
                    : ""));
            // ⚠️ #1644 — a lossy DRAWN layer must announce itself. The page's
            // font is WinAnsi and has no byte for CJK, Cyrillic, Greek or
            // U+0100+ Latin, so on those documents the reconstruction shows
            // substitutes. An artifact offered as evidence of what a redaction
            // leaked must not understate it quietly; the JSON and each
            // finding's annotation still carry the text verbatim.
            if (restore.UndrawableCharacters > 0)
                output.WriteLine(
                    $"  ⚠ {restore.UndrawableCharacters} character(s) could not be DRAWN with the " +
                    "page's own font and appear as '?'. The recovered text is intact in the JSON " +
                    "report and in each finding's annotation — only the drawn layer is lossy.");
            output.WriteLine(
                "  ⚠️ this file CONTAINS the recovered text by design, and is watermarked " +
                "as a reconstruction. Handle it as you would the unredacted original.");
            output.WriteLine();
        }

        var recovered = report.Recovery is { } model
            ? model.Linked.Count + model.Unlinked.Count + model.DocumentLevel.Count
            : 0;
        if (report.Certain.Count == 0 && report.Residue.Count == 0 && recovered == 0)
        {
            output.WriteLine(report.Present is { Count: > 0 }
                ? "✓ No recoverable text or measurable residue found (content listed below is present but was not decoded)."
                : "✓ No recoverable text or measurable residue found.");
        }
        else
        {
            output.WriteLine(
                $"QUANTIFICATION — {quantification.Findings} finding(s), " +
                $"{quantification.Recovered} RECOVERED: " +
                $"{quantification.FullyRecoverable} text present, " +
                $"{quantification.WidthResidueGaps} width-residue gap(s) leaking " +
                $"{quantification.WidthResidueBitsTotal} bits total.");
        }

        // ⚠️ #1690 — ADJACENT TO THE SCORE, and to the ALL-CLEAR above it,
        // which is the branch that matters most: a green tick over a document
        // whose only leak is under a deferred channel is precisely the false
        // reassurance this tool exists to remove. A footer would let a reader
        // stop at the tick.
        WriteLimitations(report.Limitations, output);

        if (report.Certain.Count > 0)
        {
            // ⚠️ THE ONE LINE A READER ACTS ON. Counting only the classes that
            // indicate a failed redaction: "191 findings" on a scanned filing
            // and "31 findings" on the Manafort leak looked identical before
            // this, and only one of them is news.
            //
            // The EXIT CODE deliberately does not use this split — it still
            // goes non-zero on anything, because #608 is a redacted term
            // leaking into XMP and a script that exited 0 on that would be the
            // silent leak this whole design refuses. The summary ranks; the
            // exit status stays paranoid.
            var meaningful = report.Certain.Count(
                f => RecoveryFindingClassifier.IndicatesAFailedRedaction(f.Class));
            var furniture = report.Certain.Count - meaningful;
            output.WriteLine(
                $"  → {meaningful} finding(s) indicate a failed redaction" +
                (furniture > 0 ? $"; {furniture} are ordinary document metadata" : "") + ".");

            // #1669 — GROUPED BY WHAT IT INDICATES, loudest first.
            //
            // Ungrouped, this list buried its own answer. Measured on 57 clean
            // court filings: 28 reported something, and three quarters of that
            // was link /URI targets and XMP metadata — one document produced
            // 13,686 findings from its OCR layer. A reader cannot act on a list
            // where "the name under the black box" and "dc:creator" are the
            // same shape of line.
            //
            // ⚠️ Furniture is still PRINTED, below the line and labelled. #608
            // is a redacted term leaking into XMP; suppressing it would trade a
            // flood for a silent leak, which is the worse of the two.
            foreach (var group in report.Certain.GroupBy(f => f.Class).OrderBy(g => (int)g.Key))
            {
                output.WriteLine(Heading(group.Key, group.Count()));
                foreach (var note in Caveats(group.ToList())) output.WriteLine(note);
                foreach (var finding in group)
                {
                    if (finding.FromCarrier)
                    {
                        output.WriteLine(CarrierLine(finding));
                        continue;
                    }
                    output.WriteLine(
                        $"  page {finding.Page} ({finding.X},{finding.Y}) " +
                        $"[{finding.HiddenBy}]: \"{finding.Text}\"");
                }
            }
        }

        if (report.VisibleDuplicates is { Count: > 0 } duplicates)
        {
            output.WriteLine($"= VISIBLE DUPLICATES — carriers restating text the reader already sees ({duplicates.Count}):");
            foreach (var finding in duplicates)
                output.WriteLine(CarrierLine(finding));
        }

        if (report.Present is { Count: > 0 } present)
        {
            output.WriteLine($"! PRESENT — content not decoded, inspect it yourself ({present.Count}):");
            foreach (var finding in present)
            {
                output.WriteLine(
                    $"  {Where(finding.Page, finding.Object, finding.Location)} " +
                    $"[{finding.Carrier}]: {finding.Description}");
            }
        }

        if (report.Residue.Count == 0)
            return;

        output.WriteLine($"~ RECOVERED from width leak ({report.Residue.Count} gap(s)):");
        foreach (var finding in report.Residue)
        {
            if (finding.CandidatesFit == 1 && finding.Candidates.Count == 1)
            {
                output.WriteLine(
                    $"  page {finding.Page} gap {finding.GapWidthPt}pt {finding.Font}: " +
                    $"RECOVERED \"{finding.Candidates[0]}\" " +
                    $"(unique width fit, {finding.ResidualEntropyBits} bits)");
            }
            else
            {
                output.WriteLine(
                    $"  page {finding.Page} gap {finding.GapWidthPt}pt {finding.Font}: " +
                    $"{finding.CandidatesFit} candidates, {finding.ResidualEntropyBits} bits -> " +
                    $"[{string.Join(", ", finding.Candidates)}]");
            }
        }
    }

    /// <summary>
    /// #1587 — the per-mark summary, printed FIRST and deliberately so. The
    /// finding lists below answer "what did we get"; this answers "out of how
    /// much", and a reader who sees only the former will overestimate the
    /// recovery every time.
    /// </summary>
    private static void WriteRecoveryModel(UnredactRecoveryModel? recovery, TextWriter output)
    {
        if (recovery == null) return;

        if (recovery.Marks == 0)
        {
            output.WriteLine("MARKS — no redaction mark found (no box, /Redact annotation or emptied region).");
        }
        else
        {
            output.WriteLine(
                $"MARKS — {recovery.Marks} redaction mark(s): " +
                $"{recovery.MarksRecovered} recovered, " +
                $"{recovery.MarksPartiallyRecovered} partially recovered, " +
                $"{recovery.MarksCandidatesOnly} candidates only, " +
                $"{recovery.MarksNotRecovered} nothing recovered.");

            foreach (var mark in recovery.MarkSummaries)
            {
                var symbol = mark.Outcome switch
                {
                    "recovered" => "✗",              // the redaction failed completely
                    "partially-recovered" => "✗",
                    "candidates-only" => "~",
                    _ => "✓",                        // nothing came back: the redaction held
                };
                output.WriteLine(
                    $"  {symbol} {mark.Id} page {mark.Page} [{mark.Kind}] " +
                    $"({mark.Rect[0]},{mark.Rect[1]})-({mark.Rect[2]},{mark.Rect[3]}): " +
                    $"{mark.Outcome}" +
                    (mark.Findings > 0
                        ? $" — {mark.Findings} finding(s), {mark.CertainFindings} certain"
                        : ""));

                // #1589: for a mark that HELD, the width budget is the only
                // thing left to say about what was under it.
                if (mark.Fit is { } fit)
                {
                    var patterns = fit.PatternClasses.Count > 0
                        ? string.Join("; ", fit.PatternClasses)
                        : "no named pattern fits";
                    output.WriteLine(
                        $"      could fit: {fit.MinCharacters}-{fit.MaxCharacters} characters; {patterns}" +
                        $" (budget {fit.WidthPt}pt from {fit.WidthBasis})");
                    if (fit.CandidatesConsidered > 0)
                    {
                        output.WriteLine(
                            $"      {fit.CandidatesFit} of {fit.CandidatesConsidered} dictionary word(s) fit " +
                            $"— {fit.BitsLeaked} bits, {fit.Confidence}" +
                            (fit.TopCandidates.Count > 0
                                ? $", top: {string.Join(", ", fit.TopCandidates.Take(5))}"
                                : ""));
                    }
                    else if (fit.Note != null)
                    {
                        output.WriteLine($"      {fit.Note}");
                    }
                }
            }
        }

        if (recovery.Unlinked.Count > 0)
        {
            // Not an error and not noise: a carrier the redactor never scrubbed,
            // on a page where no box was drawn, is the commonest real leak.
            output.WriteLine(
                $"  … plus {recovery.Unlinked.Count} located finding(s) under NO mark " +
                "(a leak where nothing was redacted on the page).");
        }

        if (recovery.DocumentLevel.Count > 0)
        {
            output.WriteLine(
                $"  … plus {recovery.DocumentLevel.Count} document-level finding(s) with no page location.");
        }

        output.WriteLine($"  channels run: {string.Join(", ", recovery.ChannelsRun)}");
        if (recovery.ChannelsSkipped.Count > 0)
        {
            // A DEFERRED channel's full reason is a sentence and a half, and it
            // is printed in full under NOT COVERED below. Repeating it here
            // pushed the coverage line past four hundred characters on an
            // ordinary form, which buries the channels skipped for some OTHER
            // reason — the ones this line exists to surface.
            output.WriteLine(
                "  ⚠ channels NOT run (this report does not cover them): " +
                string.Join(", ", recovery.ChannelsSkipped.Select(c =>
                    RecoveryChannelTiers.IsDeferred(c.Key)
                        ? $"{c.Key} (deferred #1690 — see NOT COVERED below)"
                        : $"{c.Key} ({c.Value})")));
        }

        output.WriteLine();
    }
}
