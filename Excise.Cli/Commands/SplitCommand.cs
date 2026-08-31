using System.CommandLine;

namespace Excise.Cli.Commands;

internal static class SplitCommand
{
    internal static Command Create()
    {
        var inputArgument = new Argument<FileInfo>("input") { Description = "Input PDF file" };
        var outputOption = new Option<DirectoryInfo>("--output", "-o")
        {
            Description = "Output folder for split PDFs",
            Required = true,
        };
        var everyOption = new Option<int?>("--every")
        {
            Description = "Split into fixed-size chunks of N pages each (last chunk may be smaller)",
        };
        var singleOption = new Option<bool>("--single")
        {
            Description = "Split into one PDF per page",
            DefaultValueFactory = _ => false,
        };
        var bookmarksOption = new Option<bool>("--bookmarks")
        {
            Description = "Split at each root-level bookmark destination",
            DefaultValueFactory = _ => false,
        };
        var boundariesOption = new Option<string?>("--boundaries")
        {
            Description = "Comma-separated 1-based page numbers where a new output file starts, e.g. '1,5,10'",
        };
        var ignorePermissionsOption = CliPermissionOptions.CreateIgnorePermissionsOption();

        var command = new Command(
            "split",
            "Split a PDF into multiple documents by page count, boundaries, or bookmarks")
        {
            inputArgument,
            outputOption,
            everyOption,
            singleOption,
            bookmarksOption,
            boundariesOption,
            ignorePermissionsOption,
        };

        command.SetAction(parseResult =>
        {
            var input = parseResult.GetValue(inputArgument)!;
            var every = parseResult.GetValue(everyOption);
            var single = parseResult.GetValue(singleOption);
            var bookmarks = parseResult.GetValue(bookmarksOption);
            var boundariesText = parseResult.GetValue(boundariesOption);

            if (!input.Exists)
            {
                Console.Error.WriteLine($"File not found: {input.FullName}");
                return 1;
            }

            var selectedModes = new[]
            {
                every.HasValue,
                single,
                bookmarks,
                boundariesText != null,
            }.Count(selected => selected);
            if (selectedModes == 0)
            {
                Console.Error.WriteLine(
                    "Choose exactly one split mode: --every N, --single, --bookmarks, or --boundaries.");
                return 1;
            }
            if (selectedModes > 1)
            {
                Console.Error.WriteLine(
                    "Choose only one of --every, --single, --bookmarks, --boundaries.");
                return 1;
            }
            if (every is < 1)
            {
                Console.Error.WriteLine("--every must be at least 1.");
                return 1;
            }

            var boundaries = ParseBoundaries(boundariesText);
            if (boundariesText != null && boundaries.Count == 0)
            {
                Console.Error.WriteLine(
                    $"Could not parse any page numbers from --boundaries '{boundariesText}'.");
                return 1;
            }

            try
            {
                var mode = every.HasValue
                    ? SplitDocumentMode.Every
                    : single
                        ? SplitDocumentMode.Single
                        : bookmarks
                            ? SplitDocumentMode.Bookmarks
                            : SplitDocumentMode.Boundaries;
                var result = DocumentAssemblyHandler.Split(new SplitDocumentRequest(
                    input.FullName,
                    parseResult.GetValue(outputOption)!.FullName,
                    mode,
                    every,
                    boundaries,
                    parseResult.GetValue(ignorePermissionsOption)));

                Console.WriteLine($"Split into {result.WrittenPaths.Count} file(s)");
                foreach (var path in result.WrittenPaths)
                    Console.WriteLine($"  {path}");
                if (result.DroppedCatalogEntries.Count > 0)
                {
                    Console.WriteLine(
                        "  note: not conserved into the split outputs: " +
                        string.Join(", ", result.DroppedCatalogEntries.Select(entry => "/" + entry)));
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

    internal static IReadOnlyList<int> ParseBoundaries(string? value)
    {
        if (value == null)
            return [];
        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.TryParse(part, out var pageNumber) ? pageNumber - 1 : -1)
            .Where(index => index >= 0)
            .ToArray();
    }
}
