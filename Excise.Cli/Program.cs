using System.CommandLine;
using Excise.Cli.Commands;
using Excise.Core.Automation;
using Excise.Core.Document;
using Excise.Core.Operations;
using Excise.Core.Text.Segmentation;
using Excise.Ocr;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Excise.Cli.Tests")]

namespace Excise.Cli;

partial class Program
{
    static Task<int> Main(string[] args) => RunAsync(args);

    /// <summary>
    /// Build and invoke the root command. Exposed for tests so they can
    /// exercise the CLI parsing + handler pipeline without spawning a
    /// subprocess.
    /// </summary>
    internal static Task<int> RunAsync(string[] args)
    {
        Environment.ExitCode = 0;
        var rootCommand = new RootCommand("excise - PDF toolkit powered by Excise.Core")
        {
            CommandMetadataCommand.Create(),
            CreateBatchCommand(),
            InfoCommand.Create(),
            ValidateCommand.Create(),
            TextCommand.Create(),
            LettersCommand.Create(),
            RenderCommand.Create(),
            RedactCommand.Create(),
            MergeCommand.Create(),
            SplitCommand.Create(),
            FillFormCommand.Create(),
            AddFieldCommand.Create(),
            AutodetectFieldsCommand.Create(),
            AuditCommand.Create(),
            UnredactCommand.Create(),
            OcrCommand.Create(),
            CreateMakeSearchableCommand(),
            EncryptCommand.Create(),
            DecryptCommand.Create(),
            SaveSizeReportCommand.Create(),
        };

        // System.CommandLine 2.0 split parsing from invocation: build a
        // ParseResult first, then run its action. Wrap with Task.FromResult
        // because handlers are sync; if any command goes async later we'll
        // switch to Parse(args).InvokeAsync().
        var parserExitCode = rootCommand.Parse(args).Invoke();
        var handlerExitCode = Environment.ExitCode;
        return Task.FromResult(parserExitCode != 0 ? parserExitCode : handlerExitCode);
    }

    static Command CreateMakeSearchableCommand()
    {
        var inputArg = new Argument<FileInfo>("input") { Description = "Input PDF file" };
        var outputArg = new Argument<FileInfo>("output") { Description = "Output PDF path" };
        var pageOption = new Option<int?>("--page", "-p") { Description = "Page to process (1-based). Omit for all pages." };
        var dpiOption = new Option<int>("--dpi")
        {
            Description = "Render DPI for OCR (higher = slower, more accurate)",
            DefaultValueFactory = _ => 300,
        };
        var langOption = new Option<string>("--lang")
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
            inputArg, outputArg, pageOption, dpiOption, langOption, tessdataOption, forceOption,
        };

        command.SetAction(parseResult =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var output = parseResult.GetValue(outputArg)!;
            var page = parseResult.GetValue(pageOption);
            var dpi = parseResult.GetValue(dpiOption);
            var lang = parseResult.GetValue(langOption)!;
            var tessdata = parseResult.GetValue(tessdataOption);
            var force = parseResult.GetValue(forceOption);

            if (!input.Exists)
            {
                Console.Error.WriteLine($"File not found: {input.FullName}");
                Environment.ExitCode = 1;
                return;
            }

            var ocr = new PdfOcrService(language: lang, dpi: dpi, tessdataPrefix: tessdata);
            if (!ocr.IsAvailable())
            {
                Console.Error.WriteLine(
                    "tesseract CLI not found on PATH. Install with `apt install tesseract-ocr` " +
                    "(or your platform's equivalent).");
                Environment.ExitCode = 1;
                return;
            }

            try
            {
                // #918: read-only verb — stream, don't load resident.
                using var doc = PdfDocument.Open(input.FullName);
                int from = page.GetValueOrDefault(1);
                int to = page.HasValue ? page.Value : doc.PageCount;

                if (from < 1 || from > doc.PageCount || to < from || to > doc.PageCount)
                {
                    Console.Error.WriteLine($"Page out of range (document has {doc.PageCount} pages).");
                    Environment.ExitCode = 1;
                    return;
                }

                var converter = new PdfSearchableConverter(ocr);
                var pagesWithEncodingGaps = new List<(int Page, int Skipped)>();
                int wordsWritten = 0, pagesSkipped = 0, pagesProcessed = 0;

                for (int p = from; p <= to; p++)
                {
                    var result = converter.MakePageSearchable(doc.GetPage(p), force);
                    if (result.Skipped)
                    {
                        pagesSkipped++;
                        Console.WriteLine($"Page {p}/{to}: skipped (already has a text layer)");
                    }
                    else
                    {
                        pagesProcessed++;
                        wordsWritten += result.WordsWritten;
                        Console.WriteLine($"Page {p}/{to}: {result.WordsWritten} word(s) written");
                        if (result.WordsSkippedEncoding > 0)
                            pagesWithEncodingGaps.Add((p, result.WordsSkippedEncoding));
                    }
                }

                // #643: preserve source encryption (empty-password sources only here).
                doc.Save(output.FullName, doc.GetReEncryptionOptions(userPassword: null));

                Console.WriteLine($"Processed {pagesProcessed} page(s), skipped {pagesSkipped}, wrote {wordsWritten} word(s).");
                Console.WriteLine($"Output: {output.FullName}");

                if (pagesWithEncodingGaps.Count > 0)
                {
                    Console.Error.WriteLine(
                        "Warning: some recognized words were not written because they contain characters " +
                        "outside the supported font's range (non-Latin scripts aren't fully supported yet, " +
                        "see #627) — these pages are only partially searchable:");
                    foreach (var (p, skipped) in pagesWithEncodingGaps)
                        Console.Error.WriteLine($"  Page {p}: {skipped} word(s) skipped");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                Environment.ExitCode = 1;
            }
        });

        return command;
    }

}
