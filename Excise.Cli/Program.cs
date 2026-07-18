using System.CommandLine;
using System.Text.Json;
using Excise.Core.Automation;
using Excise.Core.Document;
using Excise.Core.Operations;
using Excise.Core.Text.Segmentation;
using Excise.Ocr;
using Excise.Rendering;
using SkiaSharp;

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
            CreateCommandsCommand(),
            CreateBatchCommand(),
            CreateInfoCommand(),
            CreateTextCommand(),
            CreateLettersCommand(),
            CreateRenderCommand(),
            CreateRedactCommand(),
            CreateMergeCommand(),
            CreateSplitCommand(),
            CreateFillFormCommand(),
            CreateAddFieldCommand(),
            CreateAutodetectFieldsCommand(),
            CreateAuditCommand(),
            CreateOcrCommand(),
            CreateMakeSearchableCommand(),
            CreateEncryptCommand(),
            CreateDecryptCommand(),
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
    /// excise commands [id] [--json] - Show stable semantic command metadata.
    /// </summary>
    static Command CreateCommandsCommand()
    {
        var idArg = new Argument<string?>("id")
        {
            Description = "Optional semantic command id to inspect",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var jsonOption = new Option<bool>("--json")
        {
            Description = "Write command metadata as JSON",
            DefaultValueFactory = _ => false,
        };

        var command = new Command("commands", "Show stable excise command metadata for automation and accessibility")
        {
            idArg,
            jsonOption
        };

        command.SetAction(parseResult =>
        {
            var id = parseResult.GetValue(idArg);
            var json = parseResult.GetValue(jsonOption);

            IReadOnlyList<PdfCommandMetadata> commands;
            if (string.IsNullOrWhiteSpace(id))
            {
                commands = PdfCommandRegistry.All;
            }
            else if (PdfCommandRegistry.TryGet(id, out var single))
            {
                commands = [single];
            }
            else
            {
                Console.Error.WriteLine($"Unknown command id: {id}");
                Environment.ExitCode = 1;
                return;
            }

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(commands, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true,
                }));
                return;
            }

            foreach (var metadata in commands)
            {
                var shortcut = string.IsNullOrWhiteSpace(metadata.Shortcut)
                    ? string.Empty
                    : $" [{metadata.Shortcut}]";
                var cli = string.IsNullOrWhiteSpace(metadata.CliCommand)
                    ? string.Empty
                    : $" cli: {metadata.CliCommand}";
                Console.WriteLine($"{metadata.Id} - {metadata.Label}{shortcut}{cli}");
                Console.WriteLine($"  {metadata.Description}");
                if (metadata.IsSecuritySensitive)
                    Console.WriteLine("  Security-sensitive: true");
                if (metadata.IsDestructive)
                    Console.WriteLine("  Destructive: true");
            }
        });

        return command;
    }

    /// <summary>
    /// excise info <file> - Show PDF document information
    /// </summary>
    static Command CreateInfoCommand()
    {
        var fileArg = new Argument<FileInfo>("file") { Description = "PDF file to analyze" };
        var jsonOption = new Option<bool>("--json")
        {
            Description = "Write document information as JSON",
            DefaultValueFactory = _ => false,
        };
        var passwordOption = new Option<string?>("--password")
        {
            Description = "User password for encrypted PDFs",
        };
        var command = new Command("info", "Show PDF document information")
        {
            fileArg,
            jsonOption,
            passwordOption,
        };

        command.SetAction(parseResult =>
        {
            var file = parseResult.GetValue(fileArg)!;
            var json = parseResult.GetValue(jsonOption);
            var password = parseResult.GetValue(passwordOption);
            if (!file.Exists)
            {
                Console.Error.WriteLine($"File not found: {file.FullName}");
                Environment.ExitCode = 1;
                return;
            }

            try
            {
                using var doc = OpenPdfDocument(file.FullName, password);

                if (json)
                {
                    WriteJson(new
                    {
                        schemaVersion = 1,
                        command = PdfCommandIds.DocumentInfo,
                        status = "PASS",
                        file = file.FullName,
                        sizeBytes = file.Length,
                        version = doc.Version,
                        pageCount = doc.PageCount,
                        encrypted = doc.IsEncrypted,
                        metadata = new
                        {
                            doc.Title,
                            doc.Author,
                            doc.Subject,
                            doc.Creator,
                            doc.Producer,
                        },
                        pages = Enumerable.Range(1, Math.Min(doc.PageCount, 10))
                            .Select(pageNumber =>
                            {
                                var page = doc.GetPage(pageNumber);
                                return new
                                {
                                    pageNumber,
                                    width = page.Width,
                                    height = page.Height,
                                };
                            })
                            .ToArray(),
                    });
                    return;
                }

                Console.WriteLine($"File: {file.Name}");
                Console.WriteLine($"Size: {file.Length:N0} bytes");
                Console.WriteLine();
                Console.WriteLine("=== Document Info ===");
                Console.WriteLine($"PDF Version: {doc.Version}");
                Console.WriteLine($"Page Count: {doc.PageCount}");
                Console.WriteLine($"Encrypted: {doc.IsEncrypted}");
                Console.WriteLine();

                if (doc.Title != null) Console.WriteLine($"Title: {doc.Title}");
                if (doc.Author != null) Console.WriteLine($"Author: {doc.Author}");
                if (doc.Subject != null) Console.WriteLine($"Subject: {doc.Subject}");
                if (doc.Creator != null) Console.WriteLine($"Creator: {doc.Creator}");
                if (doc.Producer != null) Console.WriteLine($"Producer: {doc.Producer}");

                Console.WriteLine();
                Console.WriteLine("=== Pages ===");
                for (int i = 1; i <= Math.Min(doc.PageCount, 10); i++)
                {
                    var page = doc.GetPage(i);
                    Console.WriteLine($"  Page {i}: {page.Width:F0} x {page.Height:F0} pts ({page.Width / 72:F1}\" x {page.Height / 72:F1}\")");
                }
                if (doc.PageCount > 10)
                    Console.WriteLine($"  ... and {doc.PageCount - 10} more pages");
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
    /// excise text <file> [--page N] - Extract text from PDF
    /// </summary>
    static Command CreateTextCommand()
    {
        var fileArg = new Argument<FileInfo>("file") { Description = "PDF file" };
        var pageOption = new Option<int?>("--page", "-p") { Description = "Specific page number (1-based)" };
        var jsonOption = new Option<bool>("--json")
        {
            Description = "Write extracted text as JSON",
            DefaultValueFactory = _ => false,
        };
        var passwordOption = new Option<string?>("--password")
        {
            Description = "User password for encrypted PDFs",
        };
        var ignorePermissionsOption = CreateIgnorePermissionsOption();
        var forAccessibilityOption = CreateForAccessibilityOption();

        var command = new Command("text", "Extract text from PDF")
        {
            fileArg,
            pageOption,
            jsonOption,
            passwordOption,
            ignorePermissionsOption,
            forAccessibilityOption,
        };

        command.SetAction(parseResult =>
        {
            var file = parseResult.GetValue(fileArg)!;
            var page = parseResult.GetValue(pageOption);
            var json = parseResult.GetValue(jsonOption);
            var password = parseResult.GetValue(passwordOption);
            var ignorePermissions = parseResult.GetValue(ignorePermissionsOption);
            var forAccessibility = parseResult.GetValue(forAccessibilityOption);
            if (!file.Exists)
            {
                Console.Error.WriteLine($"File not found: {file.FullName}");
                Environment.ExitCode = 1;
                return;
            }

            try
            {
                using var doc = OpenPdfDocument(file.FullName, password);
                RequireDocumentPermission(doc, DocumentAction.Extract, "text extraction",
                    ignorePermissions, forAccessibility, accessibilityHint: "--for-accessibility");

                if (page.HasValue)
                {
                    if (page.Value < 1 || page.Value > doc.PageCount)
                    {
                        Console.Error.WriteLine($"Invalid page number. Document has {doc.PageCount} pages.");
                        Environment.ExitCode = 1;
                        return;
                    }
                    var p = doc.GetPage(page.Value);
                    if (json)
                    {
                        WriteTextJson(file.FullName, doc.PageCount, [new(page.Value, p.Text)]);
                        return;
                    }
                    Console.WriteLine($"=== Page {page.Value} ===");
                    Console.WriteLine(p.Text);
                }
                else
                {
                    if (json)
                    {
                        var pages = Enumerable.Range(1, doc.PageCount)
                            .Select(pageNumber => new TextPageResult(pageNumber, doc.GetPage(pageNumber).Text))
                            .ToArray();
                        WriteTextJson(file.FullName, doc.PageCount, pages);
                        return;
                    }

                    for (int i = 1; i <= doc.PageCount; i++)
                    {
                        var p = doc.GetPage(i);
                        if (doc.PageCount > 1)
                            Console.WriteLine($"=== Page {i} ===");
                        Console.WriteLine(p.Text);
                        if (i < doc.PageCount)
                            Console.WriteLine();
                    }
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
    /// excise letters <file> --page N - Show letters with positions
    /// </summary>
    static Command CreateLettersCommand()
    {
        var fileArg = new Argument<FileInfo>("file") { Description = "PDF file" };
        var pageOption = new Option<int>("--page", "-p")
        {
            Description = "Page number (1-based)",
            DefaultValueFactory = _ => 1,
        };
        var limitOption = new Option<int>("--limit", "-n")
        {
            Description = "Maximum letters to show",
            DefaultValueFactory = _ => 50,
        };

        var ignorePermissionsOption = CreateIgnorePermissionsOption();
        var forAccessibilityOption = CreateForAccessibilityOption();

        var command = new Command("letters", "Show letters with position information")
        {
            fileArg,
            pageOption,
            limitOption,
            ignorePermissionsOption,
            forAccessibilityOption,
        };

        command.SetAction(parseResult =>
        {
            var file = parseResult.GetValue(fileArg)!;
            var page = parseResult.GetValue(pageOption);
            var limit = parseResult.GetValue(limitOption);
            var ignorePermissions = parseResult.GetValue(ignorePermissionsOption);
            var forAccessibility = parseResult.GetValue(forAccessibilityOption);
            if (!file.Exists)
            {
                Console.Error.WriteLine($"File not found: {file.FullName}");
                return;
            }

            try
            {
                using var doc = PdfDocument.Open(file.FullName);
                RequireDocumentPermission(doc, DocumentAction.Extract, "letter/text extraction",
                    ignorePermissions, forAccessibility, accessibilityHint: "--for-accessibility");

                if (page < 1 || page > doc.PageCount)
                {
                    Console.Error.WriteLine($"Invalid page number. Document has {doc.PageCount} pages.");
                    return;
                }

                var p = doc.GetPage(page);
                var letters = p.Letters;

                Console.WriteLine($"Page {page}: {letters.Count} letters");
                Console.WriteLine();
                Console.WriteLine("Char  X       Y       Width   Font");
                Console.WriteLine("----  ------  ------  ------  ----");

                foreach (var letter in letters.Take(limit))
                {
                    var ch = letter.Value.Length == 1 && char.IsControl(letter.Value[0])
                        ? $"\\x{(int)letter.Value[0]:X2}"
                        : letter.Value;
                    Console.WriteLine($"{ch,-4}  {letter.StartX,6:F1}  {letter.StartY,6:F1}  {letter.Width,6:F1}  {letter.FontName}");
                }

                if (letters.Count > limit)
                    Console.WriteLine($"... and {letters.Count - limit} more letters");
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
    /// excise render <file> -o <output.png> [--page N] [--dpi N] [--password P]
    /// </summary>
    static Command CreateRenderCommand()
    {
        var fileArg = new Argument<FileInfo>("file") { Description = "PDF file" };
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

        var ignorePermissionsOption = CreateIgnorePermissionsOption();

        var command = new Command("render", "Render PDF page to image")
        {
            fileArg,
            outputOption,
            pageOption,
            dpiOption,
            passwordOption,
            jsonOption,
            ignorePermissionsOption,
        };

        command.SetAction(parseResult =>
        {
            var file = parseResult.GetValue(fileArg)!;
            var output = parseResult.GetValue(outputOption)!;
            var page = parseResult.GetValue(pageOption);
            var dpi = parseResult.GetValue(dpiOption);
            var password = parseResult.GetValue(passwordOption);
            var json = parseResult.GetValue(jsonOption);
            var ignorePermissions = parseResult.GetValue(ignorePermissionsOption);
            if (!file.Exists)
            {
                Console.Error.WriteLine($"File not found: {file.FullName}");
                Environment.ExitCode = 1;
                return;
            }

            try
            {
                using var doc = OpenPdfDocument(file.FullName, password);
                RequireDocumentPermission(doc, DocumentAction.Extract,
                    "page image export (render)", ignorePermissions);

                if (page < 1 || page > doc.PageCount)
                {
                    Console.Error.WriteLine($"Invalid page number. Document has {doc.PageCount} pages.");
                    Environment.ExitCode = 1;
                    return;
                }

                if (!json)
                    Console.WriteLine($"Rendering page {page} at {dpi} DPI...");

                var result = RenderPageToPng(doc, page, dpi, output.FullName);

                if (json)
                {
                    WriteJson(new
                    {
                        schemaVersion = 1,
                        command = PdfCommandIds.RenderPage,
                        status = "PASS",
                        inputPath = file.FullName,
                        outputPath = output.FullName,
                        pageNumber = page,
                        dpi,
                        result.Width,
                        result.Height,
                    });
                    return;
                }

                Console.WriteLine($"Output size: {result.Width} x {result.Height} pixels");
                Console.WriteLine($"Saved to: {output.FullName}");
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
    /// excise redact &lt;input&gt; &lt;output&gt; &lt;text&gt; [--case-sensitive]
    /// Remove every occurrence of a text string from a PDF at the
    /// content-stream level (glyph removal, not visual overlay), then
    /// save the result to a new file.
    /// </summary>
    static Command CreateRedactCommand()
    {
        var inputArg = new Argument<FileInfo>("input") { Description = "Input PDF file" };
        var outputArg = new Argument<FileInfo>("output") { Description = "Output PDF path" };
        var textArg = new Argument<string>("text") { Description = "Text to remove (all occurrences)" };
        var caseSensitiveOption = new Option<bool>("--case-sensitive")
        {
            Description = "Match case exactly (default: case-insensitive)",
            DefaultValueFactory = _ => false,
        };
        var passwordOption = new Option<string?>("--password")
        {
            Description = "User password for encrypted PDFs. The output is re-encrypted with this " +
                "password by default (see --allow-decrypt).",
        };
        var allowDecryptOption = new Option<bool>("--allow-decrypt")
        {
            Description = "Write UNENCRYPTED output from an encrypted source. By default (#643) an " +
                "encrypted source is re-encrypted with the same algorithm and permissions (RC4 " +
                "sources are upgraded to AES-256) and the same password it was opened with; this " +
                "flag is the explicit opt-out that drops the protection instead.",
            DefaultValueFactory = _ => false,
        };
        var strictOption = new Option<bool>("--strict")
        {
            Description = "Require an independent extraction-confidence check (mutool or tesseract) " +
                "to run at all — fail rather than proceed unverified when neither is on PATH. " +
                "Mirrors `audit --deep`'s posture.",
            DefaultValueFactory = _ => false,
        };
        var allowLowConfidenceOption = new Option<bool>("--allow-low-confidence")
        {
            Description = "Proceed even when the extraction-confidence check (#650) finds excise's own " +
                "text extraction disagrees sharply with an independent oracle on this document — the " +
                "same signature as a real redaction leak. Without this flag, that case fails closed.",
            DefaultValueFactory = _ => false,
        };

        var command = new Command(
            "redact",
            "Remove text from a PDF (glyph-level removal; text extraction will not find it)")
        {
            inputArg, outputArg, textArg, caseSensitiveOption, passwordOption, allowDecryptOption, strictOption, allowLowConfidenceOption
        };

        command.SetAction(parseResult =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var output = parseResult.GetValue(outputArg)!;
            var text = parseResult.GetValue(textArg)!;
            var caseSensitive = parseResult.GetValue(caseSensitiveOption);
            var password = parseResult.GetValue(passwordOption);
            var allowDecrypt = parseResult.GetValue(allowDecryptOption);
            var strict = parseResult.GetValue(strictOption);
            var allowLowConfidence = parseResult.GetValue(allowLowConfidenceOption);
            if (!input.Exists)
            {
                Console.Error.WriteLine($"File not found: {input.FullName}");
                Environment.ExitCode = 1;
                return;
            }
            if (string.IsNullOrEmpty(text))
            {
                Console.Error.WriteLine("Redaction text must not be empty.");
                Environment.ExitCode = 1;
                return;
            }

            try
            {
                int count = RunRedact(input.FullName, output.FullName, text, caseSensitive, allowDecrypt, strict, allowLowConfidence, password);
                Console.WriteLine($"Redacted {count} occurrence(s) of '{text}'");
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
    /// Thrown when the #650 extraction-confidence check refuses: either the
    /// oracle disagreed severely with excise's own extraction (same signature
    /// as a real redaction leak) and <c>--allow-low-confidence</c> wasn't
    /// passed, or <c>--strict</c> was passed and no oracle was available at
    /// all to check against. A dedicated type — rather than a bare
    /// <see cref="InvalidOperationException"/> with a matched message — lets
    /// callers like <c>AutomationBatch</c> catch this specific failure and
    /// translate it into their own error contract.
    /// </summary>
    internal sealed class LowConfidenceExtractionException(string message) : InvalidOperationException(message);

    /// <summary>
    /// Core redact-a-file operation — open, call Excise.Core's text
    /// redaction, save. Exposed internally for tests.
    ///
    /// Encrypted sources (#643): the DEFAULT is to re-encrypt the output
    /// with the same algorithm and permissions the source was opened with
    /// (RC4 sources are upgraded to AES-256 — see
    /// <see cref="PdfDocument.GetReEncryptionOptions"/>) and the supplied
    /// <paramref name="password"/>. <paramref name="allowDecrypt"/> is the
    /// explicit opt-out that writes an unprotected copy instead — under
    /// #638 it was the opt-in to proceed at all; with re-encryption as the
    /// default there is no longer a fail-closed case here.
    /// </summary>
    internal static int RunRedact(
        string inputPath, string outputPath, string text, bool caseSensitive,
        bool allowDecrypt = false, bool strict = false, bool allowLowConfidence = false,
        string? password = null)
    {
        var bytes = File.ReadAllBytes(inputPath);
        using var doc = PdfDocument.Open(bytes, password);

        var reEncryption = allowDecrypt ? null : doc.GetReEncryptionOptions(password);
        if (doc.IsEncrypted && allowDecrypt)
        {
            Console.Error.WriteLine(
                "Warning: --allow-decrypt was passed — output will NOT be encrypted, even though " +
                "the source was. Anyone with the file can read it without a password.");
        }
        else if (reEncryption != null)
        {
            Console.Error.WriteLine(
                "Note: source is encrypted; output is re-encrypted with the same permissions and " +
                "the same password (#643). Pass --allow-decrypt to write an unprotected copy instead.");
        }

        var confidence = new Excise.Ocr.RedactionConfidenceChecker().CheckDocument(doc, sourceFilePath: inputPath);
        foreach (var line in EnforceConfidencePolicy(confidence, strict, allowLowConfidence))
            Console.Error.WriteLine(line);

        var count = doc.RedactText(text, caseSensitive);
        doc.Save(outputPath, reEncryption);
        return count;
    }

    /// <summary>
    /// Decide what #650's confidence check means for this redaction: throw
    /// <see cref="LowConfidenceExtractionException"/> to refuse, or return
    /// warning lines to print (empty when the result is clean). Pure — no
    /// PDF/oracle I/O — so the policy itself (refuse vs. warn vs. proceed
    /// silently, and how <c>--strict</c>/<c>--allow-low-confidence</c>
    /// change that) is directly testable without a real SEVERE fixture.
    /// </summary>
    internal static IReadOnlyList<string> EnforceConfidencePolicy(
        Excise.Ocr.RedactionConfidenceReport confidence, bool strict, bool allowLowConfidence)
    {
        if (confidence.Oracle == null)
        {
            if (strict)
            {
                throw new LowConfidenceExtractionException(
                    "--strict requires an independent extraction-confidence check, but neither mutool " +
                    "nor tesseract is on PATH. Install one of them, or drop --strict to proceed unverified.");
            }
            return new[]
            {
                "Warning: redaction could not be independently verified — neither mutool nor tesseract " +
                "is installed. excise's own extraction was used as-is.",
            };
        }

        if (confidence.ShouldRefuse)
        {
            if (!allowLowConfidence)
            {
                throw new LowConfidenceExtractionException(
                    $"excise's own text extraction disagrees sharply with an independent check " +
                    $"({confidence.Oracle}) on this document — the same signature as a real redaction " +
                    "leak. This may be a false alarm, but pass --allow-low-confidence to proceed anyway.");
            }
            return new[]
            {
                $"Warning: proceeding despite a low-confidence extraction check ({confidence.Oracle} " +
                "disagrees sharply with excise's own extraction) — --allow-low-confidence was passed.",
            };
        }

        if (confidence.ShouldWarn)
        {
            return new[]
            {
                $"Warning: excise's extraction differs somewhat from an independent check ({confidence.Oracle}) " +
                "on one or more pages of this document. Review the result before relying on it.",
            };
        }

        return Array.Empty<string>();
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
                var doc = PdfDocument.Open(File.ReadAllBytes(path));
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
        var bytes = File.ReadAllBytes(inputPath);
        using var doc = PdfDocument.Open(bytes);

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
    /// excise fill-form &lt;input&gt; &lt;output&gt; --field name=value [--field name=value]... [--flatten]
    /// Set AcroForm field values on a PDF and save. With --flatten, the
    /// values are baked into the page content streams and the form is
    /// removed (no longer interactive).
    /// </summary>
    static Command CreateFillFormCommand()
    {
        var inputArg = new Argument<FileInfo>("input") { Description = "Input PDF file" };
        var outputArg = new Argument<FileInfo>("output") { Description = "Output PDF path" };
        var fieldOption = new Option<string[]>("--field", "-f")
        {
            Description = "Field assignment in the form 'FullName=Value'. May be repeated for multiple fields.",
            AllowMultipleArgumentsPerToken = false,
        };
        var flattenOption = new Option<bool>("--flatten")
        {
            Description = "Bake values into page content and remove the form (non-interactive output)",
            DefaultValueFactory = _ => false,
        };

        var ignorePermissionsOption = CreateIgnorePermissionsOption();

        var command = new Command(
            "fill-form",
            "Set AcroForm field values and save (optionally flatten to baked content)")
        {
            inputArg, outputArg, fieldOption, flattenOption, ignorePermissionsOption
        };

        command.SetAction(parseResult =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var output = parseResult.GetValue(outputArg)!;
            var fields = parseResult.GetValue(fieldOption);
            var flatten = parseResult.GetValue(flattenOption);
            var ignorePermissions = parseResult.GetValue(ignorePermissionsOption);
            if (!input.Exists)
            {
                Console.Error.WriteLine($"File not found: {input.FullName}");
                Environment.ExitCode = 1;
                return;
            }

            if (fields == null || fields.Length == 0)
            {
                Console.Error.WriteLine("At least one --field name=value assignment is required.");
                Environment.ExitCode = 1;
                return;
            }

            try
            {
                int set = RunFillForm(input.FullName, output.FullName, fields, flatten, ignorePermissions);
                Console.WriteLine($"Set {set} field value(s){(flatten ? " (flattened)" : "")}");
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
    /// Core fill-form operation. Returns the number of fields that were
    /// successfully assigned. Throws InvalidOperationException if the
    /// document has no AcroForm or any --field token is malformed; throws
    /// KeyNotFoundException for an unknown field name.
    /// </summary>
    internal static int RunFillForm(string inputPath, string outputPath, string[] fields, bool flatten,
        bool ignorePermissions = false)
    {
        var bytes = File.ReadAllBytes(inputPath);
        using var doc = PdfDocument.Open(bytes);
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
        var bytes = File.ReadAllBytes(inputPath);
        using var doc = PdfDocument.Open(bytes);
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
                var bytes = File.ReadAllBytes(input.FullName);
                using var doc = PdfDocument.Open(bytes);
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

    /// <summary>
    /// excise audit &lt;file&gt; [--json]
    /// Report text that is present in the PDF content stream but
    /// visually occluded by a later-drawn opaque object ("redaction
    /// by black box" style). Exits with a non-zero status when leaks
    /// are found, so CI can gate on a clean audit.
    /// </summary>
    static Command CreateAuditCommand()
    {
        var fileArg = new Argument<FileInfo>("file") { Description = "PDF file to audit" };
        var jsonOption = new Option<bool>("--json")
        {
            Description = "Emit machine-readable JSON instead of the human-readable report",
            DefaultValueFactory = _ => false,
        };
        var deepOption = new Option<bool>("--deep")
        {
            Description =
                "Also run differential OCR: render the page twice (with and " +
                "without overlays stripped), OCR both, and report words " +
                "recoverable from the underlying image but hidden in the " +
                "displayed render. Catches rasterized-leak cases the " +
                "structural detector can't see. Requires `tesseract` on PATH.",
            DefaultValueFactory = _ => false,
        };

        var command = new Command(
            "audit",
            "Detect text hidden behind opaque overlays (black-box redaction audit)")
        {
            fileArg, jsonOption, deepOption,
        };

        command.SetAction(parseResult =>
        {
            var file = parseResult.GetValue(fileArg)!;
            var json = parseResult.GetValue(jsonOption);
            var deep = parseResult.GetValue(deepOption);
            if (!file.Exists)
            {
                Console.Error.WriteLine($"File not found: {file.FullName}");
                Environment.ExitCode = 1;
                return;
            }

            try
            {
                var bytes = File.ReadAllBytes(file.FullName);
                using var doc = PdfDocument.Open(bytes);
                var structuralHits = HiddenTextDetector.Scan(doc);

                IReadOnlyList<DifferentialOcrHit> ocrHits = Array.Empty<DifferentialOcrHit>();
                if (deep)
                {
                    var ocr = new PdfOcrService();
                    if (!ocr.IsAvailable())
                    {
                        Console.Error.WriteLine(
                            "--deep requires tesseract on PATH. Install with " +
                            "`apt install tesseract-ocr`.");
                        Environment.ExitCode = 1;
                        return;
                    }
                    var auditor = new DifferentialOcrAuditor(ocr);
                    ocrHits = auditor.Scan(bytes);
                }

                if (json)
                {
                    PrintAuditJson(structuralHits, ocrHits);
                }
                else
                {
                    PrintAuditHuman(structuralHits, ocrHits, deep);
                }

                // Exit non-zero on any hits.
                Environment.ExitCode = (structuralHits.Count + ocrHits.Count) == 0 ? 0 : 2;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                Environment.ExitCode = 1;
            }
        });

        return command;
    }

    private static void PrintAuditHuman(
        IReadOnlyList<HiddenTextRecord> structural,
        IReadOnlyList<DifferentialOcrHit> ocr,
        bool deepRun)
    {
        if (structural.Count == 0 && ocr.Count == 0)
        {
            Console.WriteLine(deepRun
                ? "✓ No hidden text detected (structural + differential OCR clean)."
                : "✓ No hidden text detected.");
            return;
        }

        if (structural.Count > 0)
        {
            Console.WriteLine($"✗ {structural.Count} structural hidden-text leak(s):");
            foreach (var h in structural)
            {
                Console.WriteLine(
                    $"  Page {h.PageNumber} at ({h.BoundingBox.Left:F1}, {h.BoundingBox.Bottom:F1}): " +
                    $"\"{h.Text}\" covered by {h.HiddenBy}");
            }
        }
        if (ocr.Count > 0)
        {
            Console.WriteLine($"✗ {ocr.Count} differential-OCR leak(s) " +
                $"(text in raster, hidden by overlay):");
            foreach (var h in ocr)
            {
                Console.WriteLine(
                    $"  Page {h.PageNumber} at ({h.BoundingBox.Left:F1}, {h.BoundingBox.Bottom:F1}) " +
                    $"[conf {h.Confidence:F2}]: \"{h.Text}\"");
            }
        }
    }

    private static void PrintAuditJson(
        IReadOnlyList<HiddenTextRecord> structural,
        IReadOnlyList<DifferentialOcrHit> ocr)
    {
        Console.WriteLine("{");
        Console.WriteLine("  \"structural\": [");
        for (int i = 0; i < structural.Count; i++)
        {
            var h = structural[i];
            var sep = i + 1 < structural.Count ? "," : "";
            Console.WriteLine(
                $"    {{ \"page\": {h.PageNumber}, " +
                $"\"text\": \"{Esc(h.Text)}\", " +
                $"\"bbox\": [{h.BoundingBox.Left:F2}, {h.BoundingBox.Bottom:F2}, " +
                $"{h.BoundingBox.Right:F2}, {h.BoundingBox.Top:F2}], " +
                $"\"hidden_by\": \"{Esc(h.HiddenBy)}\" }}{sep}");
        }
        Console.WriteLine("  ],");
        Console.WriteLine("  \"differential_ocr\": [");
        for (int i = 0; i < ocr.Count; i++)
        {
            var h = ocr[i];
            var sep = i + 1 < ocr.Count ? "," : "";
            Console.WriteLine(
                $"    {{ \"page\": {h.PageNumber}, " +
                $"\"text\": \"{Esc(h.Text)}\", " +
                $"\"bbox\": [{h.BoundingBox.Left:F2}, {h.BoundingBox.Bottom:F2}, " +
                $"{h.BoundingBox.Right:F2}, {h.BoundingBox.Top:F2}], " +
                $"\"confidence\": {h.Confidence:F3} }}{sep}");
        }
        Console.WriteLine("  ]");
        Console.WriteLine("}");
    }

    private static string Esc(string s)
        => s.Replace("\\", "\\\\").Replace("\"", "\\\"")
            .Replace("\n", "\\n").Replace("\r", "\\r");


    /// <summary>
    /// excise ocr &lt;file&gt; [--page N] [--dpi 300] [--lang eng]
    /// OCR a PDF page (or all pages) using the system tesseract CLI.
    /// </summary>
    static Command CreateOcrCommand()
    {
        var fileArg = new Argument<FileInfo>("file") { Description = "PDF file to OCR" };
        var pageOption = new Option<int?>("--page", "-p") { Description = "Page to OCR (1-based). Omit for all pages." };
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

        var ignorePermissionsOption = CreateIgnorePermissionsOption();
        var forAccessibilityOption = CreateForAccessibilityOption();

        var command = new Command("ocr", "Render and OCR a PDF page via tesseract")
        {
            fileArg, pageOption, dpiOption, langOption, tessdataOption,
            ignorePermissionsOption, forAccessibilityOption,
        };

        command.SetAction(parseResult =>
        {
            var file = parseResult.GetValue(fileArg)!;
            var page = parseResult.GetValue(pageOption);
            var dpi = parseResult.GetValue(dpiOption);
            var lang = parseResult.GetValue(langOption)!;
            var tessdata = parseResult.GetValue(tessdataOption);
            var ignorePermissions = parseResult.GetValue(ignorePermissionsOption);
            var forAccessibility = parseResult.GetValue(forAccessibilityOption);
            if (!file.Exists)
            {
                Console.Error.WriteLine($"File not found: {file.FullName}");
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
                using var doc = PdfDocument.Open(File.ReadAllBytes(file.FullName));
                RequireDocumentPermission(doc, DocumentAction.Extract, "OCR text extraction",
                    ignorePermissions, forAccessibility, accessibilityHint: "--for-accessibility");
                int from = page.GetValueOrDefault(1);
                int to   = page.HasValue ? page.Value : doc.PageCount;

                if (from < 1 || from > doc.PageCount || to < from || to > doc.PageCount)
                {
                    Console.Error.WriteLine($"Page out of range (document has {doc.PageCount} pages).");
                    Environment.ExitCode = 1;
                    return;
                }

                for (int p = from; p <= to; p++)
                {
                    if (doc.PageCount > 1)
                        Console.WriteLine($"=== Page {p} ===");
                    var result = ocr.RecognizePage(doc.GetPage(p));
                    Console.WriteLine(result.Text);
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
                using var doc = PdfDocument.Open(File.ReadAllBytes(input.FullName));
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
    /// writer. Exposed internally for tests, mirroring <see cref="RunRedact"/>.
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
            doc = Excise.Core.Document.PdfDocument.Open(File.ReadAllBytes(inputPath));
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
    /// <see cref="RunRedact"/>. Throws <see cref="InvalidOperationException"/>
    /// when the source isn't encrypted (nothing to decrypt), and lets
    /// <c>PdfDocument.Open</c>'s own password-verification exception
    /// propagate for a wrong password.
    /// </summary>
    internal static void RunDecrypt(string inputPath, string outputPath, string? password)
    {
        using var doc = Excise.Core.Document.PdfDocument.Open(File.ReadAllBytes(inputPath), password);
        if (!doc.IsEncrypted)
            throw new InvalidOperationException("Source PDF is not encrypted; nothing to decrypt.");

        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        new Excise.Core.Writing.PdfDocumentWriter(doc, encryptionOptions: null).Write(fs);
    }
}
