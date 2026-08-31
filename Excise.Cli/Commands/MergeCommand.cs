using System.CommandLine;

namespace Excise.Cli.Commands;

internal static class MergeCommand
{
    internal static Command Create()
    {
        var inputOption = new Option<string[]>("--input", "-i")
        {
            Description = "Source PDF file to merge, in order. Repeat for multiple sources.",
            AllowMultipleArgumentsPerToken = false,
        };
        var outputOption = new Option<FileInfo>("--output", "-o")
        {
            Description = "Output PDF path",
            Required = true,
        };
        var ignorePermissionsOption = CliPermissionOptions.CreateIgnorePermissionsOption();

        var command = new Command(
            "merge",
            "Combine pages from multiple PDFs into a new document, preserving links, bookmarks, and form fields")
        {
            inputOption,
            outputOption,
            ignorePermissionsOption,
        };

        command.SetAction(parseResult =>
        {
            var inputs = parseResult.GetValue(inputOption);
            if (inputs == null || inputs.Length == 0)
            {
                Console.Error.WriteLine("At least one --input <file> is required.");
                return 1;
            }

            // Keep the established adapter-level diagnostic. The typed handler
            // also validates its request for non-CLI callers, but its exception
            // text is not the CLI's public text-output contract.
            foreach (var path in inputs)
            {
                if (!File.Exists(path))
                {
                    Console.Error.WriteLine($"File not found: {path}");
                    return 1;
                }
            }

            try
            {
                var result = DocumentAssemblyHandler.Merge(new MergeDocumentsRequest(
                    inputs,
                    parseResult.GetValue(outputOption)!.FullName,
                    parseResult.GetValue(ignorePermissionsOption)));
                Console.WriteLine(
                    $"Merged {result.InputPaths.Count} document(s), {result.PageCount} page(s) total");
                if (result.DroppedCatalogEntries.Count > 0)
                {
                    Console.WriteLine(
                        "  note: not conserved from the primary source's catalog: " +
                        string.Join(", ", result.DroppedCatalogEntries.Select(entry => "/" + entry)));
                }
                Console.WriteLine($"Output: {result.OutputPath}");
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
}
