using System.CommandLine;
using System.Diagnostics;
using Pdfe.Core.Document;
using Pdfe.Core.Graphics;
using Pdfe.Core.Parsing;
using Pdfe.Core.Text.Segmentation;
using Pdfe.Ocr;
using Pdfe.Rendering;
using SkiaSharp;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Pdfe.Cli.Tests")]

namespace Pdfe.Cli;

class Program
{
    static Task<int> Main(string[] args) => RunAsync(args);

    /// <summary>
    /// Build and invoke the root command. Exposed for tests so they can
    /// exercise the CLI parsing + handler pipeline without spawning a
    /// subprocess.
    /// </summary>
    internal static Task<int> RunAsync(string[] args)
    {
        var rootCommand = new RootCommand("pdfe - PDF toolkit powered by Pdfe.Core")
        {
            CreateInfoCommand(),
            CreateTextCommand(),
            CreateLettersCommand(),
            CreateRenderCommand(),
            CreateDrawCommand(),
            CreateRedactCommand(),
            CreateFillFormCommand(),
            CreateAddFieldCommand(),
            CreateAutodetectFieldsCommand(),
            CreateAuditCommand(),
            CreateOcrCommand(),
            CreateDemoCommand(),
            CreateCorpusScanCommand(),
        };

        // System.CommandLine 2.0 split parsing from invocation: build a
        // ParseResult first, then run its action. Wrap with Task.FromResult
        // because handlers are sync; if any command goes async later we'll
        // switch to Parse(args).InvokeAsync().
        return Task.FromResult(rootCommand.Parse(args).Invoke());
    }

    /// <summary>
    /// pdfe info <file> - Show PDF document information
    /// </summary>
    static Command CreateInfoCommand()
    {
        var fileArg = new Argument<FileInfo>("file") { Description = "PDF file to analyze" };
        var command = new Command("info", "Show PDF document information")
        {
            fileArg
        };

        command.SetAction(parseResult =>
        {
            var file = parseResult.GetValue(fileArg)!;
            if (!file.Exists)
            {
                Console.Error.WriteLine($"File not found: {file.FullName}");
                return;
            }

            try
            {
                using var doc = PdfDocument.Open(file.FullName);

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
            }
        });

        return command;
    }

    /// <summary>
    /// pdfe text <file> [--page N] - Extract text from PDF
    /// </summary>
    static Command CreateTextCommand()
    {
        var fileArg = new Argument<FileInfo>("file") { Description = "PDF file" };
        var pageOption = new Option<int?>("--page", "-p") { Description = "Specific page number (1-based)" };

        var command = new Command("text", "Extract text from PDF")
        {
            fileArg,
            pageOption
        };

        command.SetAction(parseResult =>
        {
            var file = parseResult.GetValue(fileArg)!;
            var page = parseResult.GetValue(pageOption);
            if (!file.Exists)
            {
                Console.Error.WriteLine($"File not found: {file.FullName}");
                return;
            }

            try
            {
                using var doc = PdfDocument.Open(file.FullName);

                if (page.HasValue)
                {
                    if (page.Value < 1 || page.Value > doc.PageCount)
                    {
                        Console.Error.WriteLine($"Invalid page number. Document has {doc.PageCount} pages.");
                        return;
                    }
                    var p = doc.GetPage(page.Value);
                    Console.WriteLine($"=== Page {page.Value} ===");
                    Console.WriteLine(p.Text);
                }
                else
                {
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
            }
        });

        return command;
    }

    /// <summary>
    /// pdfe letters <file> --page N - Show letters with positions
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

        var command = new Command("letters", "Show letters with position information")
        {
            fileArg,
            pageOption,
            limitOption
        };

        command.SetAction(parseResult =>
        {
            var file = parseResult.GetValue(fileArg)!;
            var page = parseResult.GetValue(pageOption);
            var limit = parseResult.GetValue(limitOption);
            if (!file.Exists)
            {
                Console.Error.WriteLine($"File not found: {file.FullName}");
                return;
            }

            try
            {
                using var doc = PdfDocument.Open(file.FullName);

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
            }
        });

        return command;
    }

    /// <summary>
    /// pdfe render <file> -o <output.png> [--page N] [--dpi N]
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

        var command = new Command("render", "Render PDF page to image")
        {
            fileArg,
            outputOption,
            pageOption,
            dpiOption
        };

        command.SetAction(parseResult =>
        {
            var file = parseResult.GetValue(fileArg)!;
            var output = parseResult.GetValue(outputOption)!;
            var page = parseResult.GetValue(pageOption);
            var dpi = parseResult.GetValue(dpiOption);
            if (!file.Exists)
            {
                Console.Error.WriteLine($"File not found: {file.FullName}");
                return;
            }

            try
            {
                using var doc = PdfDocument.Open(file.FullName);

                if (page < 1 || page > doc.PageCount)
                {
                    Console.Error.WriteLine($"Invalid page number. Document has {doc.PageCount} pages.");
                    return;
                }

                var renderer = new SkiaRenderer();
                var options = new RenderOptions { Dpi = dpi };

                Console.WriteLine($"Rendering page {page} at {dpi} DPI...");
                using var bitmap = renderer.RenderPage(doc.GetPage(page), options);

                Console.WriteLine($"Output size: {bitmap.Width} x {bitmap.Height} pixels");

                using var image = SKImage.FromBitmap(bitmap);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                using var stream = File.OpenWrite(output.FullName);
                data.SaveTo(stream);

                Console.WriteLine($"Saved to: {output.FullName}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
            }
        });

        return command;
    }

    /// <summary>
    /// pdfe draw <file> -o <output.pdf> - Add shapes to PDF (demo)
    /// </summary>
    static Command CreateDrawCommand()
    {
        var fileArg = new Argument<FileInfo>("file") { Description = "PDF file" };
        var outputOption = new Option<FileInfo>("--output", "-o")
        {
            Description = "Output PDF file",
            Required = true,
        };
        var rectOption = new Option<string?>("--rect") { Description = "Add rectangle: x,y,w,h (in points)" };
        var colorOption = new Option<string>("--color")
        {
            Description = "Fill color: black, red, green, blue",
            DefaultValueFactory = _ => "black",
        };

        var command = new Command("draw", "Add shapes to PDF (demo of graphics API)")
        {
            fileArg,
            outputOption,
            rectOption,
            colorOption
        };

        command.SetAction(parseResult =>
        {
            var file = parseResult.GetValue(fileArg)!;
            var output = parseResult.GetValue(outputOption)!;
            var rect = parseResult.GetValue(rectOption);
            var color = parseResult.GetValue(colorOption)!;
            if (!file.Exists)
            {
                Console.Error.WriteLine($"File not found: {file.FullName}");
                return;
            }

            try
            {
                using var doc = PdfDocument.Open(file.FullName);
                var page = doc.GetPage(1);

                using var graphics = page.GetGraphics();

                var brush = color.ToLower() switch
                {
                    "red" => PdfBrush.Red,
                    "green" => PdfBrush.Green,
                    "blue" => PdfBrush.Blue,
                    "white" => PdfBrush.White,
                    _ => PdfBrush.Black
                };

                if (rect != null)
                {
                    var parts = rect.Split(',');
                    if (parts.Length == 4 &&
                        double.TryParse(parts[0], out var x) &&
                        double.TryParse(parts[1], out var y) &&
                        double.TryParse(parts[2], out var w) &&
                        double.TryParse(parts[3], out var h))
                    {
                        Console.WriteLine($"Drawing rectangle at ({x}, {y}) size {w}x{h} with {color} fill");
                        graphics.DrawRectangle(x, y, w, h, brush);
                    }
                    else
                    {
                        Console.Error.WriteLine("Invalid rectangle format. Use: x,y,w,h");
                        return;
                    }
                }
                else
                {
                    // Default: draw a sample rectangle
                    Console.WriteLine("Drawing sample rectangle at (100, 100) size 200x100");
                    graphics.DrawRectangle(100, 100, 200, 100, brush);
                }

                graphics.Flush();
                doc.Save(output.FullName);
                Console.WriteLine($"Saved to: {output.FullName}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
            }
        });

        return command;
    }

    /// <summary>
    /// pdfe redact &lt;input&gt; &lt;output&gt; &lt;text&gt; [--case-sensitive]
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

        var command = new Command(
            "redact",
            "Remove text from a PDF (glyph-level removal; text extraction will not find it)")
        {
            inputArg, outputArg, textArg, caseSensitiveOption
        };

        command.SetAction(parseResult =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var output = parseResult.GetValue(outputArg)!;
            var text = parseResult.GetValue(textArg)!;
            var caseSensitive = parseResult.GetValue(caseSensitiveOption);
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
                int count = RunRedact(input.FullName, output.FullName, text, caseSensitive);
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
    /// Core redact-a-file operation — open, call Pdfe.Core's text
    /// redaction, save. Exposed internally for tests.
    /// </summary>
    internal static int RunRedact(string inputPath, string outputPath, string text, bool caseSensitive)
    {
        var bytes = File.ReadAllBytes(inputPath);
        using var doc = PdfDocument.Open(bytes);
        var count = doc.RedactText(text, caseSensitive);
        doc.Save(outputPath);
        return count;
    }

    /// <summary>
    /// pdfe fill-form &lt;input&gt; &lt;output&gt; --field name=value [--field name=value]... [--flatten]
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

        var command = new Command(
            "fill-form",
            "Set AcroForm field values and save (optionally flatten to baked content)")
        {
            inputArg, outputArg, fieldOption, flattenOption
        };

        command.SetAction(parseResult =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var output = parseResult.GetValue(outputArg)!;
            var fields = parseResult.GetValue(fieldOption);
            var flatten = parseResult.GetValue(flattenOption);
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
                int set = RunFillForm(input.FullName, output.FullName, fields, flatten);
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
    internal static int RunFillForm(string inputPath, string outputPath, string[] fields, bool flatten)
    {
        var bytes = File.ReadAllBytes(inputPath);
        using var doc = PdfDocument.Open(bytes);

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

        doc.Save(outputPath);
        return set;
    }

    /// <summary>
    /// pdfe add-field input output --type T --name N --page P --rect "x,y,w,h" [--value v] [--option o]...
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

        var command = new Command("add-field",
            "Add a new AcroForm field (Text/Checkbox/Choice/Signature) to a PDF")
        {
            inputArg, outputArg, typeOption, nameOption, pageOption, rectOption, valueOption, optionsOption
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
                RunAddField(input.FullName, output.FullName, type, name, page, rectStr, value, options);
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
        string? value, string[] options)
    {
        var rect = ParseRect(rectStr);
        var bytes = File.ReadAllBytes(inputPath);
        using var doc = PdfDocument.Open(bytes);

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

        doc.Save(outputPath);
    }

    private static Pdfe.Core.Document.PdfRectangle ParseRect(string s)
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
        return new Pdfe.Core.Document.PdfRectangle(nums[0], nums[1], nums[2], nums[3]);
    }

    /// <summary>
    /// pdfe autodetect-fields input [output] [--apply] [--json]
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

        var command = new Command("autodetect-fields",
            "Heuristically detect likely form-field locations on each page")
        {
            inputArg, outputArg, applyOption
        };

        command.SetAction(parseResult =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var output = parseResult.GetValue(outputArg);
            var apply = parseResult.GetValue(applyOption);
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
                    doc.Save(output!.FullName);
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
    /// pdfe audit &lt;file&gt; [--json]
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
    /// pdfe ocr &lt;file&gt; [--page N] [--dpi 300] [--lang eng]
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

        var command = new Command("ocr", "Render and OCR a PDF page via tesseract")
        {
            fileArg, pageOption, dpiOption, langOption, tessdataOption,
        };

        command.SetAction(parseResult =>
        {
            var file = parseResult.GetValue(fileArg)!;
            var page = parseResult.GetValue(pageOption);
            var dpi = parseResult.GetValue(dpiOption);
            var lang = parseResult.GetValue(langOption)!;
            var tessdata = parseResult.GetValue(tessdataOption);
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

    /// <summary>
    /// pdfe demo - Run interactive demos
    /// </summary>
    static Command CreateDemoCommand()
    {
        var command = new Command("demo", "Run interactive demos of Pdfe.Core capabilities");

        command.SetAction(_ =>
        {
            Console.WriteLine("=== Pdfe.Core Demo ===");
            Console.WriteLine();

            // Demo 1: Create PDF in memory
            Console.WriteLine("1. Creating PDF in memory...");
            var pdfBytes = CreateSamplePdf();
            Console.WriteLine($"   Created {pdfBytes.Length} byte PDF");

            // Demo 2: Parse and extract info
            Console.WriteLine();
            Console.WriteLine("2. Parsing PDF structure...");
            using var doc = PdfDocument.Open(pdfBytes);
            Console.WriteLine($"   Version: {doc.Version}");
            Console.WriteLine($"   Pages: {doc.PageCount}");

            // Demo 3: Extract text
            Console.WriteLine();
            Console.WriteLine("3. Extracting text...");
            var text = doc.GetPage(1).Text;
            Console.WriteLine($"   Text: \"{text.Trim()}\"");

            // Demo 4: Get letter positions
            Console.WriteLine();
            Console.WriteLine("4. Letter positions (first 5)...");
            var letters = doc.GetPage(1).Letters;
            foreach (var letter in letters.Take(5))
            {
                Console.WriteLine($"   '{letter.Value}' at ({letter.StartX:F1}, {letter.StartY:F1})");
            }

            // Demo 5: Add graphics
            Console.WriteLine();
            Console.WriteLine("5. Adding graphics...");
            var page = doc.GetPage(1);
            using var g = page.GetGraphics();
            g.DrawRectangle(50, 50, 100, 50, PdfBrush.Red);
            g.DrawLine(50, 50, 150, 100, PdfPen.Black);
            g.Flush();
            Console.WriteLine("   Added red rectangle and black line");

            // Demo 6: Save modified PDF
            Console.WriteLine();
            Console.WriteLine("6. Saving modified PDF...");
            var modifiedBytes = doc.SaveToBytes();
            Console.WriteLine($"   Modified PDF: {modifiedBytes.Length} bytes");

            // Demo 7: Render to image
            Console.WriteLine();
            Console.WriteLine("7. Rendering to image...");
            var renderer = new SkiaRenderer();
            using var bitmap = renderer.RenderPage(doc.GetPage(1), new RenderOptions { Dpi = 72 });
            Console.WriteLine($"   Bitmap: {bitmap.Width} x {bitmap.Height} pixels");

            Console.WriteLine();
            Console.WriteLine("=== Demo Complete ===");
            Console.WriteLine();
            Console.WriteLine("Try these commands:");
            Console.WriteLine("  pdfe info <file.pdf>              - Show PDF info");
            Console.WriteLine("  pdfe text <file.pdf>              - Extract text");
            Console.WriteLine("  pdfe letters <file.pdf> -p 1      - Show letter positions");
            Console.WriteLine("  pdfe render <file.pdf> -o out.png - Render to image");
            Console.WriteLine("  pdfe draw <file.pdf> -o out.pdf   - Add shapes");
        });

        return command;
    }

    static byte[] CreateSamplePdf()
    {
        var content = "BT /F1 24 Tf 100 700 Td (Hello from Pdfe.Core!) Tj ET";

        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[6];

        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[2] = ms.Position;
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[3] = ms.Position;
        writer.WriteLine("3 0 obj");
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine($"<< /Length {content.Length} >>");
        writer.WriteLine("stream");
        writer.Write(content);
        writer.WriteLine();
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[5] = ms.Position;
        writer.WriteLine("5 0 obj");
        writer.WriteLine("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        writer.WriteLine("endobj");
        writer.Flush();

        long xrefPos = ms.Position;
        writer.WriteLine("xref");
        writer.WriteLine("0 6");
        writer.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 5; i++)
            writer.WriteLine($"{offsets[i]:D10} 00000 n ");
        writer.Flush();

        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Size 6 >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    /// <summary>
    /// pdfe corpus-scan &lt;corpus-dir&gt; --output out.json
    ///                  [--chunk N] [--total M] [--dpi 150]
    ///
    /// Internal-use subcommand that powers the chunked exploratory
    /// differential harness without the overhead of `dotnet test`.
    /// Replaces the per-chunk dotnet test invocation
    /// (~3 min startup × 14 chunks = 40+ min wasted) with a published
    /// pdfe binary call (~500 ms startup × 14 = 7 sec).
    ///
    /// Renders selected pages from each PDF in the corpus's chunk-slice
    /// with pdfe's SkiaRenderer and with reference renderers, computes
    /// diff metrics, and writes a per-chunk JSON. The shell driver
    /// scripts/run-exploratory-corpus.sh merges the slices.
    ///
    /// Memory budget: chunk index `i` mod total `M` selects the
    /// chunk's PDFs; one process per chunk keeps SkiaSharp's native
    /// allocations bounded by process exit.
    /// </summary>
    static Command CreateCorpusScanCommand()
    {
        var corpusArg = new Argument<DirectoryInfo>("corpus") { Description = "Directory of PDFs to scan" };
        var outputOption = new Option<FileInfo>("--output")
        {
            Description = "Output JSON path",
            Required = true,
        };
        var chunkOption = new Option<int>("--chunk")
        {
            Description = "0-based chunk index",
            DefaultValueFactory = _ => 0,
        };
        var totalOption = new Option<int>("--total")
        {
            Description = "Total number of chunks",
            DefaultValueFactory = _ => 1,
        };
        var dpiOption = new Option<int>("--dpi")
        {
            Description = "Render DPI",
            DefaultValueFactory = _ => 150,
        };
        var diffPctOption = new Option<double>("--max-diff-fraction")
        {
            Description = "Pass-fail threshold for differing-pixel fraction",
            DefaultValueFactory = _ => 0.10,
        };
        var maxMaeOption = new Option<double>("--max-mae")
        {
            Description = "Pass-fail threshold for mean-absolute-error per channel",
            DefaultValueFactory = _ => 32.0,
        };
        var parallelOption = new Option<int>("--parallel")
        {
            Description = "Concurrent PDFs within this chunk. 0 = auto (ProcessorCount/2).",
            DefaultValueFactory = _ => 0,
        };
        var perPdfTimeoutOption = new Option<int>("--pdf-timeout-ms")
        {
            Description = "Mutool timeout per PDF render. Lower = skip slow PDFs faster.",
            DefaultValueFactory = _ => 15_000,
        };
        var pageModeOption = new Option<string>("--page-mode")
        {
            Description = "Pages to compare: first, sample, or all.",
            DefaultValueFactory = _ => "first",
        };

        var command = new Command("corpus-scan",
            "Render corpus PDFs with pdfe + reference oracles, compute pixel-diff, write JSON report")
        {
            corpusArg, outputOption, chunkOption, totalOption,
            dpiOption, diffPctOption, maxMaeOption, parallelOption, perPdfTimeoutOption, pageModeOption,
        };

        command.SetAction(parseResult =>
        {
            var corpus = parseResult.GetValue(corpusArg)!;
            var output = parseResult.GetValue(outputOption)!;
            var chunk = parseResult.GetValue(chunkOption);
            var total = parseResult.GetValue(totalOption);
            var dpi = parseResult.GetValue(dpiOption);
            var diffPct = parseResult.GetValue(diffPctOption);
            var maxMae = parseResult.GetValue(maxMaeOption);
            var parallel = parseResult.GetValue(parallelOption);
            var pdfTimeoutMs = parseResult.GetValue(perPdfTimeoutOption);
            var pageModeRaw = parseResult.GetValue(pageModeOption) ?? "first";

            if (parallel <= 0) parallel = Math.Max(1, Environment.ProcessorCount / 2);
            if (!TryParseCorpusPageMode(pageModeRaw, out var pageMode))
            {
                Console.Error.WriteLine($"Bad --page-mode '{pageModeRaw}'. Use first, sample, or all.");
                Environment.ExitCode = 1; return;
            }

            if (!corpus.Exists)
            {
                Console.Error.WriteLine($"Corpus not found: {corpus.FullName}");
                Environment.ExitCode = 1; return;
            }
            if (total < 1 || chunk < 0 || chunk >= total)
            {
                Console.Error.WriteLine($"Bad chunk: --chunk {chunk} --total {total}");
                Environment.ExitCode = 1; return;
            }
            if (!Pdfe.Rendering.Differential.MutoolReferenceRenderer.IsAvailable)
            {
                Console.Error.WriteLine("mutool not on PATH — install mupdf-tools");
                Environment.ExitCode = 2; return;
            }

            try
            {
                var ok = RunCorpusScan(corpus.FullName, output.FullName,
                    chunk, total, dpi, diffPct, maxMae, parallel, pdfTimeoutMs, pageMode);
                Environment.ExitCode = ok ? 0 : 1;
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
    /// Walk the corpus's chunk-slice, render each PDF with pdfe and
    /// mutool, diff, and write a single JSON file. Mirrors
    /// ExploratoryDifferentialTests.GenerateCorpusReportChunk minus
    /// xunit overhead.
    /// </summary>
    internal static bool RunCorpusScan(
        string corpusDir, string outputPath,
        int chunkIndex, int chunkTotal,
        int dpi, double maxDiffFraction, double maxMae,
        int parallel, int pdfTimeoutMs, CorpusPageMode pageMode = CorpusPageMode.First)
    {
        var pdfs = Directory.EnumerateFiles(corpusDir, "*.pdf")
            .OrderBy(p => p)
            .Select((path, idx) => (path, idx))
            .Where(t => t.idx % chunkTotal == chunkIndex)
            .Select(t => t.path)
            .ToList();

        Console.Out.WriteLine(
            $"chunk {chunkIndex + 1}/{chunkTotal}: scanning {pdfs.Count} PDFs in {corpusDir} " +
            $"({parallel}-way parallel, {pdfTimeoutMs}ms oracle timeout, page-mode={PageModeName(pageMode)})");

        // Use a thread-safe collector. Order in the final JSON is
        // restored by sort-on-write since Parallel.ForEach completion
        // order is non-deterministic.
        var entries = new System.Collections.Concurrent.ConcurrentBag<CorpusScanEntry>();
        long peakBytes = 0;
        int processed = 0;

        var po = new ParallelOptions { MaxDegreeOfParallelism = parallel };
        Parallel.ForEach(pdfs, po, pdf =>
        {
            var rel = Path.GetFileName(pdf);
            // Wall-clock budget covers pdfe's render (no internal
            // timeout) AND mutool's render together. Some pdf.js
            // fixtures have pathological pdfe-side rendering that
            // would otherwise grind chunks for minutes per PDF.
            // Budget is 2× the mutool timeout — generous for normal
            // PDFs, hard cap on the bad ones.
            var wallBudgetMs = pdfTimeoutMs * 2;
            IReadOnlyList<CorpusScanEntry> pdfEntries;
            var pdfStopwatch = Stopwatch.StartNew();
            var progress = new CorpusScanProgress(rel);
            var task = Task.Run(() => ScanOnePdf(rel, pdf, dpi, maxDiffFraction, maxMae, pdfTimeoutMs, pageMode, progress));
            if (task.Wait(wallBudgetMs))
            {
                pdfEntries = task.Result;
            }
            else
            {
                pdfStopwatch.Stop();
                var snapshot = progress.Snapshot();
                pdfEntries = new[]
                {
                    new CorpusScanEntry
                    {
                        path = rel,
                        pageNumber = snapshot.PageNumber,
                        status = "TIMEOUT",
                        errorPhase = snapshot.Phase,
                        errorType = "WallClockTimeout",
                        elapsedMs = pdfStopwatch.ElapsedMilliseconds,
                        timeoutMs = wallBudgetMs,
                        diagnostic = snapshot.Detail,
                        errorMessage = $"Per-PDF budget {wallBudgetMs}ms exceeded during {snapshot.Phase}: {snapshot.Detail}",
                    },
                };
                // Note: the underlying ScanOne task may keep running
                // until the process exits. That's acceptable here:
                // we're not sharing state, and process exit at chunk
                // end reaps the orphan.
            }
            foreach (var entry in pdfEntries)
                entries.Add(entry);
            int n = System.Threading.Interlocked.Increment(ref processed);

            // Cheap RSS sample.
            var rss = Environment.WorkingSet;
            if (rss > System.Threading.Interlocked.Read(ref peakBytes))
                System.Threading.Interlocked.Exchange(ref peakBytes, rss);

            // Periodic GC to keep SkiaSharp's native memory bounded.
            // Less aggressive than the serial version because parallel
            // workers naturally interleave finalization.
            if (n % 20 == 0)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        });

        GC.Collect();
        GC.WaitForPendingFinalizers();

        var counts = new Dictionary<string, int>();
        foreach (var e in entries)
        {
            counts.TryGetValue(e.status, out var c);
            counts[e.status] = c + 1;
        }

        Console.Out.WriteLine();
        foreach (var kv in counts.OrderByDescending(kv => kv.Value))
            Console.Out.WriteLine($"  {kv.Value,4}  {kv.Key}");
        Console.Out.WriteLine($"  total processed: {entries.Count}");
        Console.Out.WriteLine($"  peak RSS: {peakBytes / 1024 / 1024} MB");

        // Sort entries by path so parallel completion order doesn't make
        // diffs noisy across runs.
        var sortedEntries = entries.OrderBy(e => e.path).ToList();
        var report = new
        {
            generatedUtc = DateTime.UtcNow.ToString("o"),
            corpus = corpusDir,
            chunkIndex,
            chunkTotal,
            pageMode = PageModeName(pageMode),
            counts,
            total = entries.Count,
            pdfs = pdfs.Count,
            peakRssBytes = peakBytes,
            entries = sortedEntries,
        };
        var json = System.Text.Json.JsonSerializer.Serialize(report,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, json);
        Console.Out.WriteLine($"  wrote {outputPath}");
        return true;
    }

    private static IReadOnlyList<CorpusScanEntry> ScanOnePdf(
        string relPath, string pdfPath, int dpi,
        double maxDiffFraction, double maxMae, int oracleTimeoutMs,
        CorpusPageMode pageMode,
        CorpusScanProgress? progress = null)
    {
        PdfDocument? doc = null;
        int pageCount;
        var pdfStopwatch = Stopwatch.StartNew();
        try
        {
            progress?.Update("open", 0, "PdfDocument.Open");
            doc = PdfDocument.Open(pdfPath);
            pageCount = doc.PageCount;
            if (pageCount == 0)
            {
                pdfStopwatch.Stop();
                return new[]
                {
                    new CorpusScanEntry
                    {
                        path = relPath,
                        pageNumber = 0,
                        pageCount = 0,
                        status = "EMPTY_DOC",
                        elapsedMs = pdfStopwatch.ElapsedMilliseconds,
                    },
                };
            }
        }
        catch (Exception ex)
        {
            return new[]
            {
                new CorpusScanEntry
                {
                    path = relPath,
                    pageNumber = 0,
                    status = ClassifyCorpusFailure(ex, CorpusFailurePhase.Open),
                    errorPhase = "open",
                    errorType = ex.GetType().Name,
                    errorMessage = Trunc(ex.Message, 200),
                    elapsedMs = pdfStopwatch.ElapsedMilliseconds,
                },
            };
        }

        using (doc)
        {
            var renderer = new SkiaRenderer();
            var entries = new List<CorpusScanEntry>();
            foreach (var pageNumber in SelectCorpusPages(pageCount, pageMode))
            {
                progress?.Update("page", pageNumber, $"page {pageNumber}/{pageCount}");
                entries.Add(ScanOnePage(relPath, pdfPath, doc, renderer, pageNumber, dpi,
                    maxDiffFraction, maxMae, oracleTimeoutMs, progress));
            }
            pdfStopwatch.Stop();
            foreach (var entry in entries)
                entry.pdfElapsedMs = pdfStopwatch.ElapsedMilliseconds;
            return entries;
        }
    }

    private static CorpusScanEntry ScanOnePage(
        string relPath, string pdfPath, PdfDocument doc, SkiaRenderer renderer,
        int pageNumber, int dpi,
        double maxDiffFraction, double maxMae, int oracleTimeoutMs,
        CorpusScanProgress? progress = null)
    {
        var pageStopwatch = Stopwatch.StartNew();
        var entry = new CorpusScanEntry
        {
            path = relPath,
            pageNumber = pageNumber,
            pageCount = doc.PageCount,
        };

        SkiaSharp.SKBitmap? pdfeBmp = null;
        SkiaSharp.SKBitmap? mutoolBmp = null;
        SkiaSharp.SKBitmap? cairoBmp = null;
        try
        {
            try
            {
                progress?.Update("render", pageNumber, $"pdfe render page {pageNumber}/{doc.PageCount}");
                var renderStopwatch = Stopwatch.StartNew();
                pdfeBmp = renderer.RenderPage(doc.GetPage(pageNumber), new RenderOptions { Dpi = dpi });
                renderStopwatch.Stop();
                entry.renderMs = renderStopwatch.ElapsedMilliseconds;
            }
            catch (Exception ex)
            {
                pageStopwatch.Stop();
                entry.status = ClassifyCorpusFailure(ex, CorpusFailurePhase.Render);
                entry.errorPhase = "render";
                entry.errorType = ex.GetType().Name;
                entry.errorMessage = Trunc(ex.Message, 200);
                entry.elapsedMs = pageStopwatch.ElapsedMilliseconds;
                return entry;
            }

            if (pdfeBmp == null)
            {
                pageStopwatch.Stop();
                entry.status = "RENDER_NULL";
                entry.elapsedMs = pageStopwatch.ElapsedMilliseconds;
                return entry;
            }

            // Two oracles: mutool (MuPDF) and pdftocairo (Poppler). pdfe
            // is judged correct if it agrees with EITHER. A disagreement
            // with both is a real bug; a disagreement with one but not
            // the other is more likely a viewer-specific quirk.
            progress?.Update("mutool", pageNumber, $"mutool render page {pageNumber}/{doc.PageCount}");
            var mutoolStopwatch = Stopwatch.StartNew();
            mutoolBmp = Pdfe.Rendering.Differential.MutoolReferenceRenderer.RenderPage(
                pdfPath, pageNumber, dpi, oracleTimeoutMs);
            mutoolStopwatch.Stop();
            entry.mutoolMs = mutoolStopwatch.ElapsedMilliseconds;

            progress?.Update("pdftocairo", pageNumber, $"pdftocairo render page {pageNumber}/{doc.PageCount}");
            var cairoStopwatch = Stopwatch.StartNew();
            cairoBmp = Pdfe.Rendering.Differential.PdftocairoReferenceRenderer.RenderPage(
                pdfPath, pageNumber, dpi, oracleTimeoutMs);
            cairoStopwatch.Stop();
            entry.cairoMs = cairoStopwatch.ElapsedMilliseconds;

            if (mutoolBmp == null && cairoBmp == null)
            {
                pageStopwatch.Stop();
                entry.status = "ALL_ORACLES_REFUSED";
                entry.elapsedMs = pageStopwatch.ElapsedMilliseconds;
                return entry;
            }

            // Compute pdfe-vs-mutool and pdfe-vs-cairo separately,
            // then take the BEST agreement (lowest diff). This is
            // "best of two oracles" voting.
            (double diff, double mae)? mutoolMetrics = null;
            (double diff, double mae)? cairoMetrics = null;

            if (mutoolBmp != null)
            {
                progress?.Update("compare", pageNumber, $"compare pdfe vs mutool page {pageNumber}/{doc.PageCount}");
                var (a, b) = MatchAndCompare(pdfeBmp, mutoolBmp);
                mutoolMetrics = (a, b);
                entry.diffFractionMutool = a;
                entry.maeMutool = b;
            }
            if (cairoBmp != null)
            {
                progress?.Update("compare", pageNumber, $"compare pdfe vs pdftocairo page {pageNumber}/{doc.PageCount}");
                var (a, b) = MatchAndCompare(pdfeBmp, cairoBmp);
                cairoMetrics = (a, b);
                entry.diffFractionCairo = a;
                entry.maeCairo = b;
            }

            // Best-of-two: if pdfe agrees with either oracle within
            // thresholds, that's a PASS. Take the better of the two
            // for the headline diffFraction/mae fields.
            double bestDiff = double.MaxValue, bestMae = double.MaxValue;
            if (mutoolMetrics is var (md, mm)) { if (md < bestDiff) { bestDiff = md; bestMae = mm; } }
            if (cairoMetrics  is var (cd, cm)) { if (cd < bestDiff) { bestDiff = cd; bestMae = cm; } }
            entry.diffFraction = bestDiff;
            entry.mae = bestMae;

            bool passMutool = mutoolMetrics is var (md2, mm2)
                && md2 <= maxDiffFraction && mm2 <= maxMae;
            bool passCairo = cairoMetrics is var (cd2, cm2)
                && cd2 <= maxDiffFraction && cm2 <= maxMae;

            if (passMutool && passCairo)        entry.status = "PASS";
            else if (passMutool || passCairo)   entry.status = "PASS_ONE";  // partial agreement
            else                                entry.status = "DIFF";
            pageStopwatch.Stop();
            entry.elapsedMs = pageStopwatch.ElapsedMilliseconds;
        }
        catch (Exception ex)
        {
            pageStopwatch.Stop();
            entry.status = "COMPARE_ERROR";
            entry.errorPhase = "compare";
            entry.errorType = ex.GetType().Name;
            entry.errorMessage = Trunc(ex.Message, 200);
            entry.elapsedMs = pageStopwatch.ElapsedMilliseconds;
        }
        finally
        {
            pdfeBmp?.Dispose();
            mutoolBmp?.Dispose();
            cairoBmp?.Dispose();
        }
        return entry;
    }

    internal enum CorpusPageMode
    {
        First,
        Sample,
        All,
    }

    private static bool TryParseCorpusPageMode(string value, out CorpusPageMode mode)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "first":
            case "page1":
            case "page-1":
                mode = CorpusPageMode.First;
                return true;
            case "sample":
            case "sampled":
                mode = CorpusPageMode.Sample;
                return true;
            case "all":
            case "all-pages":
                mode = CorpusPageMode.All;
                return true;
            default:
                mode = CorpusPageMode.First;
                return false;
        }
    }

    private static string PageModeName(CorpusPageMode mode) => mode switch
    {
        CorpusPageMode.First => "first",
        CorpusPageMode.Sample => "sample",
        CorpusPageMode.All => "all",
        _ => "first",
    };

    private static IEnumerable<int> SelectCorpusPages(int pageCount, CorpusPageMode pageMode)
    {
        if (pageCount <= 0)
            yield break;

        if (pageMode == CorpusPageMode.All)
        {
            for (var page = 1; page <= pageCount; page++)
                yield return page;
            yield break;
        }

        if (pageMode == CorpusPageMode.Sample)
        {
            foreach (var page in new[] { 1, 2, 5, 20 })
            {
                if (page <= pageCount)
                    yield return page;
            }
            yield break;
        }

        yield return 1;
    }

    /// <summary>
    /// Resize the first bitmap to match the second (if dimensions
    /// disagree) and compute the diff metrics. Returns
    /// (differingPixelFraction, meanAbsoluteError).
    /// </summary>
    private static (double diffFraction, double mae) MatchAndCompare(
        SkiaSharp.SKBitmap pdfeBmp, SkiaSharp.SKBitmap reference)
    {
        SkiaSharp.SKBitmap probe = pdfeBmp;
        bool needDispose = false;
        if (pdfeBmp.Width != reference.Width || pdfeBmp.Height != reference.Height)
        {
            probe = Pdfe.Rendering.Differential.DifferentialMetrics
                .ResizeMatch(pdfeBmp, reference.Width, reference.Height).Copy();
            needDispose = true;
        }
        try
        {
            var report = Pdfe.Rendering.Differential.DifferentialMetrics.Compare(probe, reference);
            return (report.DifferingPixelFraction, report.MeanAbsoluteError);
        }
        finally
        {
            if (needDispose) probe.Dispose();
        }
    }

    private static string Trunc(string s, int n) =>
        s.Length <= n ? s : s.Substring(0, n) + "…";

    internal enum CorpusFailurePhase
    {
        Open,
        Render,
    }

    private sealed class CorpusScanProgress
    {
        private readonly object _gate = new();
        private readonly string _path;
        private string _phase = "queued";
        private string _detail = "waiting to start";
        private int _pageNumber;

        public CorpusScanProgress(string path)
        {
            _path = path;
        }

        public void Update(string phase, int pageNumber, string detail)
        {
            lock (_gate)
            {
                _phase = phase;
                _pageNumber = pageNumber;
                _detail = detail;
            }
        }

        public ProgressSnapshot Snapshot()
        {
            lock (_gate)
            {
                return new ProgressSnapshot(_phase, _pageNumber, $"{_path}: {_detail}");
            }
        }

        public readonly record struct ProgressSnapshot(string Phase, int PageNumber, string Detail);
    }

    internal static string ClassifyCorpusFailure(Exception ex, CorpusFailurePhase phase)
    {
        if (phase == CorpusFailurePhase.Open)
            return "PARSE_ERROR";

        if (IsDecodeFailure(ex))
            return "DECODE_ERROR";

        return "RENDER_ERROR";
    }

    private static bool IsDecodeFailure(Exception ex)
    {
        for (Exception? current = ex; current != null; current = current.InnerException)
        {
            if (current is PdfParseException or InvalidDataException)
                return true;

            if (current is NotSupportedException &&
                current.Message.Contains("filter", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        var stack = ex.StackTrace ?? string.Empty;
        return stack.Contains("StreamDecompressor", StringComparison.Ordinal)
            || stack.Contains(".Filters.", StringComparison.Ordinal);
    }

    internal sealed class CorpusScanEntry
    {
        public string path { get; set; } = "";
        public int pageNumber { get; set; } = 1;
        public string status { get; set; } = "UNKNOWN";
        public int pageCount { get; set; }
        // Best-of-two oracle metrics (pdfe vs whichever oracle pdfe
        // agrees with most closely). Used by the gating logic.
        public double diffFraction { get; set; }
        public double mae { get; set; }
        // Per-oracle metrics — null when that oracle refused. The
        // distinction between PASS (both agree) and PASS_ONE (one
        // agrees) lives here.
        public double? diffFractionMutool { get; set; }
        public double? maeMutool { get; set; }
        public double? diffFractionCairo { get; set; }
        public double? maeCairo { get; set; }
        public long? elapsedMs { get; set; }
        public long? pdfElapsedMs { get; set; }
        public long? renderMs { get; set; }
        public long? mutoolMs { get; set; }
        public long? cairoMs { get; set; }
        public long? timeoutMs { get; set; }
        public string? diagnostic { get; set; }
        public string? errorPhase { get; set; }
        public string? errorType { get; set; }
        public string? errorMessage { get; set; }
    }
}
