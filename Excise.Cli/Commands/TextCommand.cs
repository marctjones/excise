using System.CommandLine;
using Excise.Core.Automation;

namespace Excise.Cli.Commands;

internal static class TextCommand
{
    internal static Command Create()
    {
        var fileArgument = new Argument<FileInfo>("file") { Description = "PDF file" };
        var pageOption = new Option<int?>("--page", "-p")
        {
            Description = "Specific page number (1-based)",
        };
        var jsonOption = new Option<bool>("--json")
        {
            Description = "Write extracted text as JSON",
            DefaultValueFactory = _ => false,
        };
        var passwordOption = new Option<string?>("--password")
        {
            Description = "User password for encrypted PDFs",
        };
        var ignorePermissionsOption = CliPermissionOptions.CreateIgnorePermissionsOption();
        var forAccessibilityOption = CliPermissionOptions.CreateForAccessibilityOption();

        var command = new Command("text", "Extract text from PDF")
        {
            fileArgument,
            pageOption,
            jsonOption,
            passwordOption,
            ignorePermissionsOption,
            forAccessibilityOption,
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
                var result = TextInspectionHandler.Execute(new TextInspectionRequest(
                    file.FullName,
                    parseResult.GetValue(passwordOption),
                    parseResult.GetValue(pageOption),
                    parseResult.GetValue(ignorePermissionsOption),
                    parseResult.GetValue(forAccessibilityOption)));

                if (parseResult.GetValue(jsonOption))
                    WriteJson(result);
                else
                    WriteHuman(result);
                return 0;
            }
            catch (DocumentPageOutOfRangeException ex)
            {
                Console.Error.WriteLine($"Invalid page number. Document has {ex.PageCount} pages.");
                return 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 1;
            }
        });

        return command;
    }

    private static void WriteJson(TextInspectionResult result)
    {
        var report = new TextInspectionJsonReport(
            SchemaVersion: 1,
            Command: PdfCommandIds.ExtractText,
            Status: "PASS",
            File: result.FilePath,
            PageCount: result.PageCount,
            Pages: result.Pages);
        Console.WriteLine(CliJson.Serialize(report));
    }

    private static void WriteHuman(TextInspectionResult result)
    {
        for (var index = 0; index < result.Pages.Count; index++)
        {
            var page = result.Pages[index];
            if (result.SelectedPageNumber.HasValue || result.PageCount > 1)
                Console.WriteLine($"=== Page {page.PageNumber} ===");
            Console.WriteLine(page.Text);
            if (index < result.Pages.Count - 1)
                Console.WriteLine();
        }
    }

    private sealed record TextInspectionJsonReport(
        int SchemaVersion,
        string Command,
        string Status,
        string File,
        int PageCount,
        IReadOnlyList<TextPageResult> Pages);
}
