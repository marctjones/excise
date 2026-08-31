using System.CommandLine;

namespace Excise.Cli.Commands;

internal static class MakeSearchableCommand
{
    internal static Command Create()
    {
        var inputArgument = new Argument<FileInfo>("input") { Description = "Input PDF file" };
        var outputArgument = new Argument<FileInfo>("output") { Description = "Output PDF path" };
        var pageOption = new Option<int?>("--page", "-p")
        {
            Description = "Page to process (1-based). Omit for all pages.",
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
        var forceOption = new Option<bool>("--force")
        {
            Description = "OCR and overlay even pages that already have an extractable text layer. " +
                "Default: such pages are left untouched.",
            DefaultValueFactory = _ => false,
        };

        var command = new Command(
            "make-searchable",
            "OCR a scanned PDF and write the recognized text back as an invisible, searchable text layer")
        {
            inputArgument,
            outputArgument,
            pageOption,
            dpiOption,
            languageOption,
            tessdataOption,
            forceOption,
        };

        command.SetAction(parseResult =>
        {
            var input = parseResult.GetValue(inputArgument)!;
            if (!input.Exists)
            {
                Console.Error.WriteLine($"File not found: {input.FullName}");
                return 1;
            }

            try
            {
                var result = MakeSearchableCommandHandler.Execute(new MakeSearchableCommandRequest(
                    input.FullName,
                    parseResult.GetValue(outputArgument)!.FullName,
                    parseResult.GetValue(pageOption),
                    parseResult.GetValue(dpiOption),
                    parseResult.GetValue(languageOption)!,
                    parseResult.GetValue(tessdataOption),
                    parseResult.GetValue(forceOption)));
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

    private static void WriteHuman(MakeSearchableCommandResult result)
    {
        foreach (var page in result.Pages)
        {
            if (page.Skipped)
                Console.WriteLine($"Page {page.PageNumber}/{result.LastPage}: skipped (already has a text layer)");
            else
                Console.WriteLine($"Page {page.PageNumber}/{result.LastPage}: {page.WordsWritten} word(s) written");
        }

        Console.WriteLine(
            $"Processed {result.PagesProcessed} page(s), skipped {result.PagesSkipped}, " +
            $"wrote {result.TotalWordsWritten} word(s).");
        Console.WriteLine($"Output: {result.OutputPath}");

        if (result.EncodingGaps.Count == 0)
            return;

        Console.Error.WriteLine(
            "Warning: some recognized words were not written because they contain characters " +
            "outside the supported font's range (non-Latin scripts aren't fully supported yet, " +
            "see #627) — these pages are only partially searchable:");
        foreach (var gap in result.EncodingGaps)
            Console.Error.WriteLine($"  Page {gap.PageNumber}: {gap.WordsSkippedEncoding} word(s) skipped");
    }
}
