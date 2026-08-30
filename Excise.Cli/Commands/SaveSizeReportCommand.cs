using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;

namespace Excise.Cli.Commands;

/// <summary>
/// Builds and executes the save-size regression report command.
/// </summary>
internal static class SaveSizeReportCommand
{
    /// <summary>
    /// Creates <c>excise save-size-report</c>, including its stable JSON contract
    /// and exit-code policy.
    /// </summary>
    internal static Command Create()
    {
        var filesArg = new Argument<FileInfo[]>("files")
        {
            Description = "PDF files to open and save in memory",
            Arity = ArgumentArity.OneOrMore,
        };
        var outputOption = new Option<FileInfo?>("--output", "-o")
        {
            Description = "Optional JSON report path. The same JSON is always written to stdout.",
        };
        var maxRatioOption = new Option<double>("--max-ratio")
        {
            Description = "Maximum allowed saved/original size ratio before the entry is marked FAIL",
            DefaultValueFactory = _ => 1.20,
        };

        var command = new Command("save-size-report",
            "Measure open/save size ratios and latency for PDF writer regression tracking")
        {
            filesArg,
            outputOption,
            maxRatioOption,
        };

        command.SetAction(parseResult =>
        {
            var files = parseResult.GetValue(filesArg)!;
            var output = parseResult.GetValue(outputOption);
            var maxRatio = parseResult.GetValue(maxRatioOption);

            try
            {
                var report = BuildReport(files, maxRatio);
                var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true,
                });

                if (output is not null)
                {
                    if (output.DirectoryName is { Length: > 0 })
                        Directory.CreateDirectory(output.DirectoryName);
                    File.WriteAllText(output.FullName, json);
                }

                Console.WriteLine(json);
                Environment.ExitCode = report.OverallStatus == "PASS" ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"save-size-report failed: {ex.Message}");
                Environment.ExitCode = 1;
            }
        });

        return command;
    }

    private static SaveSizeReport BuildReport(IReadOnlyList<FileInfo> files, double maxRatio)
    {
        if (maxRatio <= 0 || double.IsNaN(maxRatio) || double.IsInfinity(maxRatio))
            throw new ArgumentOutOfRangeException(nameof(maxRatio), "maxRatio must be a positive finite number.");

        var entries = files.Select(file =>
        {
            if (!file.Exists)
            {
                return new SaveSizeReportEntry(
                    file.FullName,
                    "FAIL",
                    "File not found",
                    0,
                    0,
                    0,
                    0,
                    0,
                    null,
                    0);
            }

            var openTimer = Stopwatch.StartNew();
            using var doc = Excise.Core.Document.PdfDocument.Open(file.FullName);
            openTimer.Stop();

            var saveTimer = Stopwatch.StartNew();
            var saved = doc.SaveToBytes();
            saveTimer.Stop();

            var ratio = saved.Length / (double)file.Length;
            return new SaveSizeReportEntry(
                file.FullName,
                ratio <= maxRatio ? "PASS" : "FAIL",
                null,
                file.Length,
                saved.Length,
                ratio,
                openTimer.Elapsed.TotalMilliseconds,
                saveTimer.Elapsed.TotalMilliseconds,
                doc.Version,
                doc.PageCount);
        }).ToArray();

        return new SaveSizeReport(
            1,
            "save-size-report",
            DateTimeOffset.UtcNow,
            maxRatio,
            entries.All(e => e.Status == "PASS") ? "PASS" : "FAIL",
            entries);
    }

    private sealed record SaveSizeReport(
        int SchemaVersion,
        string Command,
        DateTimeOffset GeneratedAtUtc,
        double MaxRatio,
        string OverallStatus,
        IReadOnlyList<SaveSizeReportEntry> Files);

    private sealed record SaveSizeReportEntry(
        string File,
        string Status,
        string? Error,
        long OriginalSizeBytes,
        int SavedSizeBytes,
        double SizeRatio,
        double OpenMilliseconds,
        double SaveMilliseconds,
        string? PdfVersion,
        int PageCount);
}
