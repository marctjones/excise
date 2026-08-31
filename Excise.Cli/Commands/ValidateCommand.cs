using System.CommandLine;
using Excise.Core.Authoring;

namespace Excise.Cli.Commands;

internal static class ValidateCommand
{
    private const string ScopeNote =
        "Bounded structural subset checker — not a full ISO conformance verdict. " +
        "Use veraPDF for authoritative validation.";

    internal static Command Create()
    {
        var fileArgument = new Argument<FileInfo>("file") { Description = "PDF file to validate" };
        var pdfaOption = new Option<string?>("--pdfa")
        {
            Description = "Also run the PDF/A structural check for the given level (1b or 2b)",
        };
        var jsonOption = new Option<bool>("--json")
        {
            Description = "Write the conformance report as JSON",
            DefaultValueFactory = _ => false,
        };
        var passwordOption = new Option<string?>("--password")
        {
            Description = "User password for encrypted PDFs",
        };

        var command = new Command(
            "validate",
            "Check PDF/UA-1 (and optionally PDF/A) conformance — bounded structural subset, not a full ISO validator")
        {
            fileArgument,
            pdfaOption,
            jsonOption,
            passwordOption,
        };

        command.SetAction(parseResult =>
        {
            var file = parseResult.GetValue(fileArgument)!;
            if (!file.Exists)
            {
                Console.Error.WriteLine($"File not found: {file.FullName}");
                return 1;
            }

            try
            {
                var result = ValidationHandler.Execute(new ValidationRequest(
                    file.FullName,
                    parseResult.GetValue(passwordOption),
                    ParsePdfAConformance(parseResult.GetValue(pdfaOption))));

                if (parseResult.GetValue(jsonOption))
                    WriteJson(result);
                else
                    WriteHuman(result);
                return result.CheckedSubsetConformant ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 1;
            }
        });

        return command;
    }

    private static PdfAConformance? ParsePdfAConformance(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            null or "" => null,
            "1b" or "pdfa1b" => PdfAConformance.PdfA1B,
            "2b" or "pdfa2b" => PdfAConformance.PdfA2B,
            _ => throw new ArgumentException("invalid --pdfa level"),
        };

    private static void WriteJson(ValidationResult result)
    {
        var reports = result.Reports.Select(report => new ValidationReportJson(
            Standard: report.Standard.ToString(),
            CheckedSubsetConformant: report.CheckedSubsetConformant,
            UncoveredCheckpoints: report.UncoveredCheckpoints,
            Results: report.Results.Select(item => new ValidationItemJson(
                RuleId: item.RuleId,
                Status: item.Status.ToString(),
                Severity: item.Severity.ToString(),
                Description: item.Description,
                Location: item.Location,
                Reference: item.Reference)).ToArray())).ToArray();
        var json = new ValidationJsonReport(
            SchemaVersion: 1,
            Command: "validate",
            Status: result.CheckedSubsetConformant ? "PASS" : "FAIL",
            File: result.FilePath,
            Note: ScopeNote,
            Reports: reports);
        Console.WriteLine(CliJson.Serialize(json));
    }

    private static void WriteHuman(ValidationResult result)
    {
        Console.WriteLine($"File: {result.FileName}");
        Console.WriteLine(
            "Bounded structural conformance check (NOT a full ISO verdict — use veraPDF for that).");
        foreach (var report in result.Reports)
        {
            Console.WriteLine();
            Console.WriteLine(
                $"=== {report.Standard} — CheckedSubsetConformant={report.CheckedSubsetConformant} ===");
            foreach (var item in report.Results)
            {
                var location = item.Location is null ? string.Empty : $" @ {item.Location}";
                Console.WriteLine(
                    $"  [{item.Status}] {item.RuleId} ({item.Severity}){location}: {item.Description}");
            }
            Console.WriteLine("  NOT checked:");
            foreach (var checkpoint in report.UncoveredCheckpoints)
                Console.WriteLine($"    - {checkpoint}");
        }
    }

    private sealed record ValidationJsonReport(
        int SchemaVersion,
        string Command,
        string Status,
        string File,
        string Note,
        IReadOnlyList<ValidationReportJson> Reports);

    private sealed record ValidationReportJson(
        string Standard,
        bool CheckedSubsetConformant,
        IReadOnlyList<string> UncoveredCheckpoints,
        IReadOnlyList<ValidationItemJson> Results);

    private sealed record ValidationItemJson(
        string RuleId,
        string Status,
        string Severity,
        string Description,
        string? Location,
        string? Reference);
}
