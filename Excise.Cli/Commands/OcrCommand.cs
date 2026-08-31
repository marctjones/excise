using System.CommandLine;

namespace Excise.Cli.Commands;

internal static class OcrCommand
{
    internal static Command Create()
    {
        var fileArgument = new Argument<FileInfo>("file") { Description = "PDF file to OCR" };
        var pageOption = new Option<int?>("--page", "-p")
        {
            Description = "Page to OCR (1-based). Omit for all pages.",
        };
        var dpiOption = new Option<int>("--dpi")
        {
            Description = "Render DPI for OCR (higher = slower, more accurate)",
            DefaultValueFactory = _ => 300,
        };
        var languageOption = new Option<string>("--lang")
        {
            Description = "Tesseract language code (e.g. eng, deu, eng+spa)",
            DefaultValueFactory = _ => "eng",
        };
        var tessdataOption = new Option<string?>("--tessdata")
        {
            Description = "Path to a directory containing <lang>.traineddata. Defaults to TESSDATA_PREFIX.",
        };
        var ignorePermissionsOption = CliPermissionOptions.CreateIgnorePermissionsOption();
        var forAccessibilityOption = CliPermissionOptions.CreateForAccessibilityOption();

        var command = new Command("ocr", "Render and OCR a PDF page via tesseract")
        {
            fileArgument,
            pageOption,
            dpiOption,
            languageOption,
            tessdataOption,
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
                var result = OcrInspectionHandler.Execute(new OcrInspectionRequest(
                    file.FullName,
                    parseResult.GetValue(pageOption),
                    parseResult.GetValue(dpiOption),
                    parseResult.GetValue(languageOption)!,
                    parseResult.GetValue(tessdataOption),
                    parseResult.GetValue(ignorePermissionsOption),
                    parseResult.GetValue(forAccessibilityOption)));
                WriteHuman(result);
                return 0;
            }
            catch (OcrUnavailableException)
            {
                Console.Error.WriteLine(
                    "tesseract CLI not found on PATH. Install with `apt install tesseract-ocr` " +
                    "(or your platform's equivalent).");
                return 1;
            }
            catch (DocumentPageOutOfRangeException ex)
            {
                Console.Error.WriteLine($"Page out of range (document has {ex.PageCount} pages).");
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

    private static void WriteHuman(OcrInspectionResult result)
    {
        foreach (var page in result.Pages)
        {
            if (result.PageCount > 1)
                Console.WriteLine($"=== Page {page.PageNumber} ===");
            Console.WriteLine(page.Text);
        }
    }
}
