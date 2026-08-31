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
            CreateMergeCommand(),
            CreateSplitCommand(),
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

    /// <summary>
    /// excise merge --input a.pdf --input b.pdf --output out.pdf
    /// Combine every page of each source PDF, in order, into a new
    /// document — preserving per-source internal links, splicing each
    /// source's outline (bookmarks), and merging AcroForm fields with
    /// collision-safe renaming (see <see cref="PdfDocumentMerger"/>).
    /// </summary>
    static Command CreateMergeCommand()
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
        var ignorePermissionsOption = CreateIgnorePermissionsOption();

        var command = new Command(
            "merge",
            "Combine pages from multiple PDFs into a new document, preserving links, bookmarks, and form fields")
        {
            inputOption, outputOption, ignorePermissionsOption
        };

        command.SetAction(parseResult =>
        {
            var inputs = parseResult.GetValue(inputOption);
            var output = parseResult.GetValue(outputOption)!;
            var ignorePermissions = parseResult.GetValue(ignorePermissionsOption);

            if (inputs == null || inputs.Length == 0)
            {
                Console.Error.WriteLine("At least one --input <file> is required.");
                Environment.ExitCode = 1;
                return;
            }

            foreach (var path in inputs)
            {
                if (!File.Exists(path))
                {
                    Console.Error.WriteLine($"File not found: {path}");
                    Environment.ExitCode = 1;
                    return;
                }
            }

            try
            {
                int pageCount = RunMerge(inputs, output.FullName, ignorePermissions);
                Console.WriteLine($"Merged {inputs.Length} document(s), {pageCount} page(s) total");
                // #1058: name the primary source's catalog entries that assembly
                // does NOT conserve, so the loss is reported rather than silent.
                using (var primary = PdfDocument.Open(inputs[0]))
                {
                    var dropped = PdfDocumentMerger.CatalogEntriesNotConserved(primary);
                    if (dropped.Count > 0)
                        Console.WriteLine($"  note: not conserved from the primary source's catalog: "
                            + string.Join(", ", dropped.Select(d => "/" + d)));
                }
                Console.WriteLine($"Output: {output.FullName}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                Environment.ExitCode = 1;
            }
        });

        return command;
    }

    /// <summary>
    /// Core merge operation. Opens every source, merges all their pages via
    /// <see cref="PdfDocumentMerger"/>, saves to <paramref name="outputPath"/>,
    /// and returns the merged page count. Exposed internally for tests.
    /// </summary>
    internal static int RunMerge(string[] inputPaths, string outputPath, bool ignorePermissions = false)
    {
        var opened = new List<PdfDocument>();
        try
        {
            var sources = new List<(PdfDocument Document, IReadOnlyList<int> PageIndices)>();
            foreach (var path in inputPaths)
            {
                // #918: merge never writes its sources — stream them instead
                // of holding every input fully resident (measured ~24 MB per
                // 4 MB source, linear in source count).
                var doc = PdfDocument.Open(path);
                opened.Add(doc);
                // #677: assembling a source's pages into a new document requires
                // that source's page-assembly permission (/P bit 11).
                RequireDocumentPermission(doc, DocumentAction.AssembleDocument,
                    $"merging pages from '{Path.GetFileName(path)}'", ignorePermissions);
                sources.Add((doc, Enumerable.Range(0, doc.PageCount).ToList()));
            }

            using var merged = PdfDocumentMerger.Merge(sources);
            merged.Save(outputPath);
            return merged.PageCount;
        }
        finally
        {
            foreach (var doc in opened)
                doc.Dispose();
        }
    }

    /// <summary>
    /// excise split &lt;input&gt; --output &lt;folder&gt; (--every N | --single | --bookmarks | --boundaries "1,5,10")
    /// Split a PDF into multiple documents by exactly one policy. Does not
    /// splice outlines/AcroForm per fragment — see <see cref="PdfDocumentSplitter"/>.
    /// </summary>
    static Command CreateSplitCommand()
    {
        var inputArg = new Argument<FileInfo>("input") { Description = "Input PDF file" };
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
        var ignorePermissionsOption = CreateIgnorePermissionsOption();

        var command = new Command(
            "split",
            "Split a PDF into multiple documents by page count, boundaries, or bookmarks")
        {
            inputArg, outputOption, everyOption, singleOption, bookmarksOption, boundariesOption, ignorePermissionsOption
        };

        command.SetAction(parseResult =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var output = parseResult.GetValue(outputOption)!;
            var every = parseResult.GetValue(everyOption);
            var single = parseResult.GetValue(singleOption);
            var bookmarks = parseResult.GetValue(bookmarksOption);
            var boundariesRaw = parseResult.GetValue(boundariesOption);
            var ignorePermissions = parseResult.GetValue(ignorePermissionsOption);

            if (!input.Exists)
            {
                Console.Error.WriteLine($"File not found: {input.FullName}");
                Environment.ExitCode = 1;
                return;
            }

            var modesSelected = new[] { every.HasValue, single, bookmarks, boundariesRaw != null }.Count(selected => selected);
            if (modesSelected == 0)
            {
                Console.Error.WriteLine("Choose exactly one split mode: --every N, --single, --bookmarks, or --boundaries.");
                Environment.ExitCode = 1;
                return;
            }
            if (modesSelected > 1)
            {
                Console.Error.WriteLine("Choose only one of --every, --single, --bookmarks, --boundaries.");
                Environment.ExitCode = 1;
                return;
            }

            try
            {
                IReadOnlyList<string> written;
                if (every.HasValue)
                {
                    if (every.Value < 1)
                    {
                        Console.Error.WriteLine("--every must be at least 1.");
                        Environment.ExitCode = 1;
                        return;
                    }
                    written = RunSplit(input.FullName, output.FullName, doc => PdfDocumentSplitter.SplitEveryNPages(doc, every.Value), ignorePermissions);
                }
                else if (single)
                {
                    written = RunSplit(input.FullName, output.FullName, PdfDocumentSplitter.SplitToSinglePages, ignorePermissions);
                }
                else if (bookmarks)
                {
                    written = RunSplit(input.FullName, output.FullName, PdfDocumentSplitter.SplitAtBookmarks, ignorePermissions);
                }
                else
                {
                    var boundaries = boundariesRaw!
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(s => int.TryParse(s, out var n) ? n - 1 : -1)
                        .Where(n => n >= 0)
                        .ToList();
                    if (boundaries.Count == 0)
                    {
                        Console.Error.WriteLine($"Could not parse any page numbers from --boundaries '{boundariesRaw}'.");
                        Environment.ExitCode = 1;
                        return;
                    }
                    written = RunSplit(input.FullName, output.FullName, doc => PdfDocumentSplitter.SplitAtPageBoundaries(doc, boundaries), ignorePermissions);
                }

                Console.WriteLine($"Split into {written.Count} file(s)");
                foreach (var path in written)
                    Console.WriteLine($"  {path}");
                // #1059/#1058: name the source catalog entries not conserved into
                // the split outputs (e.g. /StructTreeRoot — accessibility — or a
                // /Dests that would dangle onto a removed page).
                using (var src = PdfDocument.Open(input.FullName))
                {
                    var dropped = PdfDocumentMerger.CatalogEntriesNotConserved(src);
                    if (dropped.Count > 0)
                        Console.WriteLine($"  note: not conserved into the split outputs: "
                            + string.Join(", ", dropped.Select(d => "/" + d)));
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

    /// <summary>
    /// Core split operation. Opens <paramref name="inputPath"/>, applies
    /// <paramref name="split"/> to get the page-group fragments, saves each
    /// under <paramref name="outputFolder"/>, and returns the written paths
    /// in order. Exposed internally for tests.
    /// </summary>
    internal static IReadOnlyList<string> RunSplit(
        string inputPath,
        string outputFolder,
        Func<PdfDocument, IReadOnlyList<PdfDocument>> split,
        bool ignorePermissions = false)
    {
        using var doc = PdfDocument.Open(inputPath);

        // #677: splitting a document into fragment PDFs is a page-assembly
        // operation, governed by the source's /P bit 11.
        RequireDocumentPermission(doc, DocumentAction.AssembleDocument,
            "splitting this document", ignorePermissions);

        Directory.CreateDirectory(outputFolder);
        var fragments = split(doc);

        var baseName = Path.GetFileNameWithoutExtension(inputPath);
        var digits = fragments.Count.ToString().Length;
        var paths = new List<string>();
        try
        {
            for (int i = 0; i < fragments.Count; i++)
            {
                var path = Path.Combine(outputFolder, $"{baseName}_{(i + 1).ToString().PadLeft(digits, '0')}.pdf");
                fragments[i].Save(path);
                paths.Add(path);
            }
        }
        finally
        {
            foreach (var fragment in fragments)
                fragment.Dispose();
        }

        return paths;
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
