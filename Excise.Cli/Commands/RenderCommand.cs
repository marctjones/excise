using System.CommandLine;
using Excise.Core.Automation;

namespace Excise.Cli.Commands;

internal static class RenderCommand
{
    internal static Command Create()
    {
        var fileArgument = new Argument<FileInfo>("file") { Description = "PDF file" };
        var outputOption = new Option<FileInfo>("--output", "-o")
        {
            Description = "Output image file (PNG)",
            Required = true,
        };
        var pageOption = new Option<int>("--page", "-p")
        {
            Description = "Page number (1-based)",
            DefaultValueFactory = _ => 1,
        };
        var dpiOption = new Option<int>("--dpi")
        {
            Description = "Resolution in DPI",
            DefaultValueFactory = _ => 150,
        };
        var passwordOption = new Option<string?>("--password")
        {
            Description = "User password for encrypted PDFs",
        };
        var jsonOption = new Option<bool>("--json")
        {
            Description = "Write render result as JSON",
            DefaultValueFactory = _ => false,
        };
        var ignorePermissionsOption = CliPermissionOptions.CreateIgnorePermissionsOption();

        var command = new Command("render", "Render PDF page to image")
        {
            fileArgument,
            outputOption,
            pageOption,
            dpiOption,
            passwordOption,
            jsonOption,
            ignorePermissionsOption,
        };

        command.SetAction(parseResult =>
        {
            var file = parseResult.GetValue(fileArgument)!;
            if (!file.Exists)
            {
                Console.Error.WriteLine($"File not found: {file.FullName}");
                return 1;
            }

            var output = parseResult.GetValue(outputOption)!;
            var page = parseResult.GetValue(pageOption);
            var dpi = parseResult.GetValue(dpiOption);
            var json = parseResult.GetValue(jsonOption);

            try
            {
                if (!json)
                    Console.WriteLine($"Rendering page {page} at {dpi} DPI...");

                var result = RenderPageHandler.Execute(new RenderPageRequest(
                    file.FullName,
                    output.FullName,
                    parseResult.GetValue(passwordOption),
                    page,
                    dpi,
                    parseResult.GetValue(ignorePermissionsOption)));

                if (json)
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

    private static void WriteJson(RenderPageResult result)
    {
        var report = new RenderPageJsonReport(
            SchemaVersion: 1,
            Command: PdfCommandIds.RenderPage,
            Status: "PASS",
            InputPath: result.InputPath,
            OutputPath: result.OutputPath,
            PageNumber: result.PageNumber,
            Dpi: result.Dpi,
            Width: result.Width,
            Height: result.Height);
        Console.WriteLine(CliJson.Serialize(report));
    }

    private static void WriteHuman(RenderPageResult result)
    {
        Console.WriteLine($"Output size: {result.Width} x {result.Height} pixels");
        Console.WriteLine($"Saved to: {result.OutputPath}");
    }

    private sealed record RenderPageJsonReport(
        int SchemaVersion,
        string Command,
        string Status,
        string InputPath,
        string OutputPath,
        int PageNumber,
        int Dpi,
        int Width,
        int Height);
}
