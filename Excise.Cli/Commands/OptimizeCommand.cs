using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Excise.Core.Writing;

namespace Excise.Cli.Commands;

/// <summary>
/// <c>excise optimize &lt;input&gt; &lt;output&gt; [--preset …]</c> — Reduce File Size (#1550).
/// </summary>
internal static class OptimizeCommand
{
    internal static Command Create()
    {
        var inputArgument = new Argument<FileInfo>("input") { Description = "Input PDF file" };
        var outputArgument = new Argument<FileInfo>("output")
        {
            Description = "Output PDF path. Must differ from the input: the original is never overwritten.",
        };
        var presetOption = new Option<string>("--preset")
        {
            Description = "lossless (default: recompress, deduplicate, drop thumbnails and private data; " +
                "pages render identically), high (images above 375 dpi to 300 dpi), " +
                "standard (above 188 dpi to 150 dpi), screen (above 120 dpi to 96 dpi). " +
                "The last three re-encode downsampled images, which is visible.",
            DefaultValueFactory = _ => "lossless",
        };
        var passwordOption = new Option<string?>("--password")
        {
            Description = "User password for encrypted PDFs. The output is re-encrypted with this " +
                "password by default (see --allow-decrypt).",
        };
        var allowDecryptOption = new Option<bool>("--allow-decrypt")
        {
            Description = "Write UNENCRYPTED output from an encrypted source. By default (#643) an " +
                "encrypted source is re-encrypted with the same algorithm, permissions and password.",
            DefaultValueFactory = _ => false,
        };
        var jsonOption = new Option<bool>("--json")
        {
            Description = "Write the result as JSON",
            DefaultValueFactory = _ => false,
        };

        var command = new Command("optimize", "Write a smaller copy of a PDF (Reduce File Size)")
        {
            inputArgument,
            outputArgument,
            presetOption,
            passwordOption,
            allowDecryptOption,
            jsonOption,
        };

        command.SetAction(parseResult =>
        {
            var input = parseResult.GetValue(inputArgument)!;
            if (!input.Exists)
            {
                Console.Error.WriteLine($"File not found: {input.FullName}");
                return 1;
            }

            var presetText = parseResult.GetValue(presetOption);
            if (!PdfOptimizationOptions.TryParsePreset(presetText, out var preset))
            {
                Console.Error.WriteLine(
                    $"Unknown preset '{presetText}'. Use lossless, high, standard or screen.");
                return 1;
            }

            try
            {
                var result = OptimizeCommandHandler.Execute(new OptimizeCommandRequest(
                    input.FullName,
                    parseResult.GetValue(outputArgument)!.FullName,
                    preset,
                    parseResult.GetValue(passwordOption),
                    parseResult.GetValue(allowDecryptOption)));

                if (parseResult.GetValue(jsonOption))
                {
                    Console.WriteLine(JsonSerializer.Serialize(
                        ToJson(result), CliJsonContext.Default.OptimizeJsonReport));
                }
                else
                {
                    WriteHuman(result);
                }

                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 1;
            }
        });

        return command;
    }

    internal static void WriteHuman(OptimizeCommandResult result)
    {
        var optimization = result.Optimization;
        Console.WriteLine($"Preset: {optimization.Preset.ToString().ToLowerInvariant()}");
        Console.WriteLine(
            $"Size: {FormatBytes(result.InputSizeBytes)} -> {FormatBytes(result.OutputSizeBytes)} " +
            $"({FormatChange(result.Ratio)})");
        Console.WriteLine(
            $"Streams recompressed: {optimization.StreamsRecompressed}; " +
            $"duplicates merged: {optimization.StreamsDeduplicated}; " +
            $"images downsampled: {optimization.ImagesDownsampled}");
        if (optimization.ThumbnailsRemoved + optimization.PrivateDataEntriesRemoved > 0)
        {
            Console.WriteLine(
                $"Removed: {optimization.ThumbnailsRemoved} page thumbnail(s), " +
                $"{optimization.PrivateDataEntriesRemoved} private-data entr(ies)");
        }

        foreach (var (reason, count) in optimization.ImagesSkipped.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            Console.WriteLine($"Images left unchanged ({reason}): {count}");
        if (result.Ratio >= 1.0)
            Console.WriteLine("Note: the copy is not smaller than the input.");
        Console.WriteLine($"Output: {result.OutputPath}");
        foreach (var diagnostic in result.Diagnostics)
            Console.Error.WriteLine(diagnostic);
    }

    internal static OptimizeJsonReport ToJson(OptimizeCommandResult result)
    {
        var optimization = result.Optimization;
        return new OptimizeJsonReport(
            result.InputPath,
            result.OutputPath,
            optimization.Preset.ToString().ToLowerInvariant(),
            result.InputSizeBytes,
            result.OutputSizeBytes,
            Math.Round(result.Ratio, 4),
            optimization.StreamsRecompressed,
            optimization.StreamsDeduplicated,
            optimization.ThumbnailsRemoved,
            optimization.PrivateDataEntriesRemoved,
            optimization.ImagesDownsampled,
            optimization.ImagesSkipped.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
            result.Diagnostics);
    }

    internal static string FormatBytes(long bytes)
    {
        var culture = CultureInfo.InvariantCulture;
        return bytes switch
        {
            >= 1024 * 1024 => string.Format(culture, "{0:0.0} MB", bytes / (1024.0 * 1024.0)),
            >= 1024 => string.Format(culture, "{0:0.0} KB", bytes / 1024.0),
            _ => string.Format(culture, "{0} B", bytes),
        };
    }

    internal static string FormatChange(double ratio)
    {
        var percent = (ratio - 1.0) * 100.0;
        return string.Format(CultureInfo.InvariantCulture, "{0:+0.0;-0.0;0.0}%", percent);
    }

    internal sealed record OptimizeJsonReport(
        string Input,
        string Output,
        string Preset,
        long InputSizeBytes,
        long OutputSizeBytes,
        double SizeRatio,
        int StreamsRecompressed,
        int StreamsDeduplicated,
        int ThumbnailsRemoved,
        int PrivateDataEntriesRemoved,
        int ImagesDownsampled,
        Dictionary<string, int> ImagesSkipped,
        IReadOnlyList<string> Diagnostics);
}
