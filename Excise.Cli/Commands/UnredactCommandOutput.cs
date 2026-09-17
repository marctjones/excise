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
        if (report.Certain.Count == 0 && report.Residue.Count == 0)
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

        if (report.Certain.Count > 0)
        {
            output.WriteLine($"✗ CERTAIN — text is actually present ({report.Certain.Count}):");
            foreach (var finding in report.Certain)
            {
                if (finding.FromCarrier)
                {
                    output.WriteLine(
                        $"  {Where(finding.Page, finding.Object, finding.Location)} " +
                        $"[{finding.HiddenBy}]: \"{finding.Text}\"");
                    continue;
                }
                output.WriteLine(
                    $"  page {finding.Page} ({finding.X},{finding.Y}) " +
                    $"[{finding.HiddenBy}]: \"{finding.Text}\"");
            }
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
}
