using System.Text.Json;
using System.Text.Json.Serialization;

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
        var recovered = report.Recovery is { } model
            ? model.Linked.Count + model.Unlinked.Count + model.DocumentLevel.Count
            : 0;
        if (report.Certain.Count == 0 && report.Residue.Count == 0 && recovered == 0)
        {
            output.WriteLine("✓ No recoverable text or measurable residue found.");
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

        if (report.Certain.Count > 0)
        {
            output.WriteLine($"✗ CERTAIN — text is actually present ({report.Certain.Count}):");
            foreach (var finding in report.Certain)
            {
                output.WriteLine(
                    $"  page {finding.Page} ({finding.X},{finding.Y}) " +
                    $"[{finding.HiddenBy}]: \"{finding.Text}\"");
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
                        $"      could fit: {fit.MinCharacters}-{fit.MaxCharacters} characters; {patterns}");
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
            output.WriteLine(
                "  ⚠ channels NOT run (this report does not cover them): " +
                string.Join(", ", recovery.ChannelsSkipped.Select(c => $"{c.Key} ({c.Value})")));
        }

        output.WriteLine();
    }
}
