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
            FillFormCommand.Create(CreateIgnorePermissionsOption(), RunFillForm),
            CreateAddFieldCommand(),
            CreateAutodetectFieldsCommand(),
            AuditCommand.Create(),
            UnredactCommand.Create(),
            OcrCommand.Create(),
            CreateMakeSearchableCommand(),
            CreateEncryptCommand(),
            CreateDecryptCommand(),
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

    /// <summary>
    /// Core fill-form operation. Returns the number of fields that were
    /// successfully assigned. Throws InvalidOperationException if the
    /// document has no AcroForm or any --field token is malformed; throws
    /// KeyNotFoundException for an unknown field name.
    /// </summary>
    internal static int RunFillForm(string inputPath, string outputPath, string[] fields, bool flatten,
        bool ignorePermissions = false)
    {
        using var doc = PdfDocumentLifetime.OpenInputForOutput(inputPath, outputPath);
        RequireDocumentPermission(doc, DocumentAction.FillForms, "filling form fields", ignorePermissions);

        var form = doc.GetAcroForm()
            ?? throw new InvalidOperationException("Document has no /AcroForm — nothing to fill.");

        int set = 0;
        foreach (var raw in fields)
        {
            var eq = raw.IndexOf('=');
            if (eq <= 0)
                throw new InvalidOperationException(
                    $"Malformed --field '{raw}'. Expected 'FullName=Value'.");
            var name = raw.Substring(0, eq);
            var value = raw.Substring(eq + 1);

            var field = form.FindField(name)
                ?? throw new KeyNotFoundException($"Field '{name}' not found in document.");
            field.SetValue(value);
            set++;
        }

        if (flatten)
            doc.FlattenAcroForm();

        // #643: an encrypted source (empty user password — this command opens
        // without a password) saves encrypted with the same parameters.
        doc.Save(outputPath, doc.GetReEncryptionOptions(userPassword: null));
        return set;
    }

    /// <summary>
    /// excise add-field input output --type T --name N --page P --rect "x,y,w,h" [--value v] [--option o]...
    /// Add a new AcroForm field to an existing PDF.
    /// </summary>
    static Command CreateAddFieldCommand()
    {
        var inputArg = new Argument<FileInfo>("input") { Description = "Input PDF file" };
        var outputArg = new Argument<FileInfo>("output") { Description = "Output PDF path" };
        var typeOption = new Option<string>("--type")
        {
            Description = "Field type: Text, Checkbox, Choice, Signature",
            DefaultValueFactory = _ => "Text",
        };
        var nameOption = new Option<string>("--name")
        {
            Description = "Full field name",
            Required = true,
        };
        var pageOption = new Option<int>("--page")
        {
            Description = "1-based page number",
            DefaultValueFactory = _ => 1,
        };
        var rectOption = new Option<string>("--rect")
        {
            Description = "Rect in PDF points as 'left,bottom,right,top' (bottom-left origin)",
            Required = true,
        };
        var valueOption = new Option<string?>("--value")
        {
            Description = "Default value (Text/Choice) or 'Yes'/'Off' (Checkbox)",
        };
        var optionsOption = new Option<string[]>("--option")
        {
            Description = "Choice option (repeatable). At least one required for --type Choice.",
            AllowMultipleArgumentsPerToken = false,
        };

        var ignorePermissionsOption = CreateIgnorePermissionsOption();

        var command = new Command("add-field",
            "Add a new AcroForm field (Text/Checkbox/Choice/Signature) to a PDF")
        {
            inputArg, outputArg, typeOption, nameOption, pageOption, rectOption, valueOption, optionsOption,
            ignorePermissionsOption
        };

        command.SetAction(parseResult =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var output = parseResult.GetValue(outputArg)!;
            var type = parseResult.GetValue(typeOption)!;
            var name = parseResult.GetValue(nameOption)!;
            var page = parseResult.GetValue(pageOption);
            var rectStr = parseResult.GetValue(rectOption)!;
            var value = parseResult.GetValue(valueOption);
            var options = parseResult.GetValue(optionsOption) ?? Array.Empty<string>();

            if (!input.Exists)
            {
                Console.Error.WriteLine($"File not found: {input.FullName}");
                Environment.ExitCode = 1;
                return;
            }

            try
            {
                RunAddField(input.FullName, output.FullName, type, name, page, rectStr, value, options,
                    parseResult.GetValue(ignorePermissionsOption));
                Console.WriteLine($"Added {type} field '{name}' to page {page}");
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

    internal static void RunAddField(string inputPath, string outputPath,
        string type, string name, int page, string rectStr,
        string? value, string[] options, bool ignorePermissions = false)
    {
        var rect = ParseRect(rectStr);
        using var doc = PdfDocumentLifetime.OpenInputForOutput(inputPath, outputPath);
        RequireDocumentPermission(doc, DocumentAction.ModifyContents,
            "adding form fields", ignorePermissions);

        switch (type.ToLowerInvariant())
        {
            case "text":
                var t = doc.AddTextField(page, rect, name, defaultValue: value);
                break;
            case "checkbox":
            case "btn":
            case "button":
                doc.AddCheckBox(page, rect, name,
                    defaultChecked: string.Equals(value, "Yes", StringComparison.OrdinalIgnoreCase));
                break;
            case "choice":
            case "combo":
            case "dropdown":
                if (options.Length == 0)
                    throw new ArgumentException("--option is required at least once for --type Choice.");
                doc.AddChoiceField(page, rect, name, options, defaultValue: value);
                break;
            case "signature":
            case "sig":
                doc.AddSignatureField(page, rect, name);
                break;
            default:
                throw new ArgumentException(
                    $"Unknown field type '{type}'. Use Text, Checkbox, Choice, or Signature.");
        }

        // #643: preserve source encryption (empty-password sources only here).
        doc.Save(outputPath, doc.GetReEncryptionOptions(userPassword: null));
    }

    private static Excise.Core.Document.PdfRectangle ParseRect(string s)
    {
        var parts = s.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4)
            throw new ArgumentException($"Expected --rect 'left,bottom,right,top'; got '{s}'.");
        var nums = new double[4];
        for (int i = 0; i < 4; i++)
        {
            if (!double.TryParse(parts[i], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out nums[i]))
                throw new ArgumentException($"Bad number in --rect: '{parts[i]}'.");
        }
        return new Excise.Core.Document.PdfRectangle(nums[0], nums[1], nums[2], nums[3]);
    }

    /// <summary>
    /// excise autodetect-fields input [output] [--apply] [--json]
    /// Run heuristic auto-detection. Prints suggestions; with --apply,
    /// also materialises them into output.
    /// </summary>
    static Command CreateAutodetectFieldsCommand()
    {
        var inputArg = new Argument<FileInfo>("input") { Description = "Input PDF file" };
        var outputArg = new Argument<FileInfo?>("output")
        {
            Description = "Output PDF (required with --apply)",
            DefaultValueFactory = _ => null,
        };
        var applyOption = new Option<bool>("--apply")
        {
            Description = "Add the detected fields to the PDF and save to <output>",
            DefaultValueFactory = _ => false,
        };

        var ignorePermissionsOption = CreateIgnorePermissionsOption();

        var command = new Command("autodetect-fields",
            "Heuristically detect likely form-field locations on each page")
        {
            inputArg, outputArg, applyOption, ignorePermissionsOption
        };

        command.SetAction(parseResult =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var output = parseResult.GetValue(outputArg);
            var apply = parseResult.GetValue(applyOption);
            var ignorePermissions = parseResult.GetValue(ignorePermissionsOption);
            if (!input.Exists)
            {
                Console.Error.WriteLine($"File not found: {input.FullName}");
                Environment.ExitCode = 1;
                return;
            }
            if (apply && output == null)
            {
                Console.Error.WriteLine("--apply requires an <output> PDF path.");
                Environment.ExitCode = 1;
                return;
            }

            try
            {
                using var doc = apply
                    ? PdfDocumentLifetime.OpenInputForOutput(input.FullName, output!.FullName)
                    : PdfDocument.Open(input.FullName);
                if (apply)
                {
                    // Detection alone is read-only analysis; only --apply
                    // modifies the document and needs /P bit 4 (#642).
                    RequireDocumentPermission(doc, DocumentAction.ModifyContents,
                        "adding detected form fields (--apply)", ignorePermissions);
                }
                var sugg = PdfFormAutoDetector.Scan(doc);

                Console.WriteLine($"Detected {sugg.Count} field candidate(s):");
                foreach (var s in sugg)
                    Console.WriteLine(
                        $"  page {s.PageNumber}  {s.FieldType,-9}  " +
                        $"[{s.Rect.Left:0.#},{s.Rect.Bottom:0.#}-{s.Rect.Right:0.#},{s.Rect.Top:0.#}]  " +
                        $"{s.SuggestedName}  ({s.Reason})");

                if (apply)
                {
                    var n = PdfFormAutoDetector.Apply(doc, sugg);
                    // #643: preserve source encryption (empty-password sources only here).
                    doc.Save(output!.FullName, doc.GetReEncryptionOptions(userPassword: null));
                    Console.WriteLine($"Applied {n} field(s); wrote {output.FullName}");
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

    /// <summary>
    /// excise encrypt &lt;input&gt; &lt;output&gt; [--user-password] [--owner-password]
    /// [--permissions] [--algorithm aes256|aes128] [--no-encrypt-metadata] (#641)
    ///
    /// Writes a new, password-protected copy of an UNencrypted source using
    /// the already-verified Standard Security Handler writer (#639 AES-256
    /// R=6 / #640 AES-128 R=4 — see <see cref="Excise.Core.Security.PdfEncryptionOptions"/>
    /// and <see cref="Excise.Core.Writing.PdfDocumentWriter"/>). Deliberately
    /// does not accept an already-encrypted source (see the /Encrypt guard
    /// below): "change password" is `decrypt` then `encrypt` as two
    /// separate invocations rather than a combined verb, since a single
    /// command would have to juggle both an "open" password (to read the
    /// source) and a "new" password (to write the output) with no clear
    /// spec-driven shape for that — simpler to keep them as two commands
    /// that already exist.
    /// </summary>
    static Command CreateEncryptCommand()
    {
        var inputArg = new Argument<FileInfo>("input") { Description = "Input PDF file (must not already be encrypted)" };
        var outputArg = new Argument<FileInfo>("output") { Description = "Output PDF path" };
        var userPasswordOption = new Option<string?>("--user-password")
        {
            Description = "User (open) password. Omit for no password required to open the file.",
        };
        var ownerPasswordOption = new Option<string?>("--owner-password")
        {
            Description = "Owner (permissions) password. Omit for no owner password.",
        };
        var permissionsOption = new Option<long>("--permissions")
        {
            Description = "Raw /P permission bitmask (ISO 32000-2 Table 22). Default -4 grants every " +
                "permission bit — excise stores this value correctly but does not yet enforce permissions " +
                "on read (#642); this is a plumbing-only escape hatch, not a security control yet.",
            DefaultValueFactory = _ => -4L,
        };
        var algorithmOption = new Option<string>("--algorithm")
        {
            Description = "Encryption algorithm: 'aes256' (V=5 R=6, PDF 2.0 native, default) or " +
                "'aes128' (V=4 R=4, for readers that don't support PDF 2.0 encryption).",
            DefaultValueFactory = _ => "aes256",
        };
        var noEncryptMetadataOption = new Option<bool>("--no-encrypt-metadata")
        {
            Description = "Leave the XMP /Metadata stream unencrypted while encrypting everything else. " +
                "Default: metadata is encrypted too.",
            DefaultValueFactory = _ => false,
        };

        var command = new Command(
            "encrypt",
            "Write a password-protected copy of a PDF (AES-256 R=6 by default; AES-128 R=4 with --algorithm aes128)")
        {
            inputArg, outputArg, userPasswordOption, ownerPasswordOption, permissionsOption, algorithmOption, noEncryptMetadataOption,
        };

        command.SetAction(parseResult =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var output = parseResult.GetValue(outputArg)!;
            var userPassword = parseResult.GetValue(userPasswordOption);
            var ownerPassword = parseResult.GetValue(ownerPasswordOption);
            var permissions = parseResult.GetValue(permissionsOption);
            var algorithmText = parseResult.GetValue(algorithmOption)!;
            var noEncryptMetadata = parseResult.GetValue(noEncryptMetadataOption);

            if (!input.Exists)
            {
                Console.Error.WriteLine($"File not found: {input.FullName}");
                Environment.ExitCode = 1;
                return;
            }

            if (string.IsNullOrEmpty(userPassword) && string.IsNullOrEmpty(ownerPassword))
            {
                Console.Error.WriteLine(
                    "At least one of --user-password or --owner-password is required " +
                    "(otherwise there is nothing to protect).");
                Environment.ExitCode = 1;
                return;
            }

            Excise.Core.Security.PdfEncryptionAlgorithm algorithm;
            switch (algorithmText.Trim().ToLowerInvariant())
            {
                case "aes256": algorithm = Excise.Core.Security.PdfEncryptionAlgorithm.Aes256; break;
                case "aes128": algorithm = Excise.Core.Security.PdfEncryptionAlgorithm.Aes128; break;
                default:
                    Console.Error.WriteLine($"Unknown --algorithm '{algorithmText}'. Use 'aes256' or 'aes128'.");
                    Environment.ExitCode = 1;
                    return;
            }

            try
            {
                RunEncrypt(input.FullName, output.FullName, userPassword, ownerPassword,
                    permissions, algorithm, encryptMetadata: !noEncryptMetadata);

                Console.WriteLine($"Encrypted with {algorithmText} ({(algorithm == Excise.Core.Security.PdfEncryptionAlgorithm.Aes256 ? "V=5 R=6" : "V=4 R=4")}).");
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
    /// Core encrypt-a-file operation — open (must be unencrypted), write a
    /// password-protected copy via the #639/#640 Standard Security Handler
    /// writer. Exposed internally for tests, mirroring
    /// <see cref="RedactCommandHandler.Execute"/>.
    /// Throws <see cref="InvalidOperationException"/> for the
    /// already-encrypted guard.
    /// </summary>
    internal static void RunEncrypt(
        string inputPath, string outputPath, string? userPassword, string? ownerPassword,
        long permissions, Excise.Core.Security.PdfEncryptionAlgorithm algorithm, bool encryptMetadata)
    {
        const string alreadyEncrypted =
            "Source PDF is already encrypted. To change its password, run `excise decrypt` " +
            "first, then `excise encrypt` the result with the new password(s).";

        Excise.Core.Document.PdfDocument doc;
        try
        {
            doc = PdfDocumentLifetime.OpenInputForOutput(inputPath, outputPath);
        }
        catch (Excise.Core.Parsing.PdfEncryptionNotSupportedException)
        {
            // A password-protected source fails to OPEN here (empty password
            // rejected) before the IsEncrypted guard below can fire — and the
            // raw "password verification failed" message would misread as the
            // NEW password being wrong. Same guidance either way.
            throw new InvalidOperationException(alreadyEncrypted);
        }

        using var _ = doc;
        if (doc.IsEncrypted)
            throw new InvalidOperationException(alreadyEncrypted);

        var options = new Excise.Core.Security.PdfEncryptionOptions
        {
            UserPassword = userPassword,
            OwnerPassword = ownerPassword,
            Permissions = permissions,
            Algorithm = algorithm,
            EncryptMetadata = encryptMetadata,
        };

        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        new Excise.Core.Writing.PdfDocumentWriter(doc, options).Write(fs);
    }

    /// <summary>
    /// excise decrypt &lt;input&gt; &lt;output&gt; [--password] (#641)
    ///
    /// Writes an unencrypted copy of an encrypted source. Running this
    /// command IS the explicit, informed act of dropping protection — the
    /// same "informed acknowledgement" spirit as `redact --allow-decrypt`
    /// (#638), just via a dedicated verb whose entire purpose is decryption
    /// rather than a flag that overrides a fail-closed default on a command
    /// meant to do something else. <paramref name="passwordOption"/> is
    /// tried as a USER password only: excise's read-side Standard Security
    /// Handler does not yet support opening with only an owner (permissions)
    /// password (tracked as #324) — an owner-only password will fail to
    /// open here even though it independently verifies against qpdf.
    /// </summary>
    static Command CreateDecryptCommand()
    {
        var inputArg = new Argument<FileInfo>("input") { Description = "Input PDF file (must be encrypted)" };
        var outputArg = new Argument<FileInfo>("output") { Description = "Output PDF path (will NOT be password-protected)" };
        var passwordOption = new Option<string?>("--password")
        {
            Description = "Password to open the source PDF (tried as the USER/open password; an " +
                "owner-only password is not yet supported for opening, see #324). Omit for an empty password.",
        };

        var command = new Command("decrypt", "Write an unprotected copy of a password-protected PDF")
        {
            inputArg, outputArg, passwordOption,
        };

        command.SetAction(parseResult =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var output = parseResult.GetValue(outputArg)!;
            var password = parseResult.GetValue(passwordOption);

            if (!input.Exists)
            {
                Console.Error.WriteLine($"File not found: {input.FullName}");
                Environment.ExitCode = 1;
                return;
            }

            try
            {
                RunDecrypt(input.FullName, output.FullName, password);

                Console.WriteLine($"Decrypted. Output: {output.FullName}");
                Console.WriteLine("Warning: the output file is NOT password-protected.");
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
    /// Core decrypt-a-file operation — open with <paramref name="password"/>
    /// (tried as the USER password; owner-only opening is #324), write an
    /// unencrypted copy. Exposed internally for tests, mirroring
    /// <see cref="RedactCommandHandler.Execute"/>. Throws <see cref="InvalidOperationException"/>
    /// when the source isn't encrypted (nothing to decrypt), and lets
    /// <c>PdfDocument.Open</c>'s own password-verification exception
    /// propagate for a wrong password.
    /// </summary>
    internal static void RunDecrypt(string inputPath, string outputPath, string? password)
    {
        using var doc = PdfDocumentLifetime.OpenInputForOutput(inputPath, outputPath, password);
        if (!doc.IsEncrypted)
            throw new InvalidOperationException("Source PDF is not encrypted; nothing to decrypt.");

        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        new Excise.Core.Writing.PdfDocumentWriter(doc, encryptionOptions: null).Write(fs);
    }

}
