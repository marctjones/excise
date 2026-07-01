using System.CommandLine;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Pdfe.Core.Document;
using Pdfe.Core.Filters.Jbig2;
using Pdfe.Core.Graphics;
using Pdfe.Core.Parsing;
using Pdfe.Core.Primitives;
using Pdfe.ImageInspection;
using Pdfe.Rendering;
using Pdfe.Rendering.Differential;
using SkiaSharp;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Pdfe.Cli.Tests")]

namespace Pdfe.RenderTools;

partial class Program
{
    private const long CorpusFallbackMaxPixelCount = 32L * 1024L * 1024L;
    private const long VisualVectorMaxPixelCount = 2L * 1024L * 1024L;

    static Task<int> Main(string[] args) => RunAsync(args);

    internal static Task<int> RunAsync(string[] args)
    {
        var rootCommand = new RootCommand("pdfe renderer/conformance utility commands")
        {
            CreateDrawCommand(),
            CreateDemoCommand(),
            CreateJbig2ClassifyCommand(),
            CreateCorpusScanCommand(),
            CreateRenderQualityScanCommand(),
            CreateRenderQualityClassifyCommand(),
        };

        return Task.FromResult(rootCommand.Parse(args).Invoke());
    }

    /// <summary>
    /// Pdfe.RenderTools draw <file> -o <output.pdf> - Add shapes to PDF (demo)
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
    /// Pdfe.RenderTools demo - Run interactive demos
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
            Console.WriteLine("  Pdfe.RenderTools draw <file.pdf> -o out.pdf - Add shapes");
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
    /// Pdfe.RenderTools jbig2-classify &lt;file-or-dir&gt; --output out.json
    ///
    /// Diagnostic helper for rendering-roadmap work: walks PDFs, finds
    /// JBIG2 image XObjects, and reports the JBIG2 feature buckets required
    /// by their segment metadata. It does not judge visual correctness; pair
    /// this with corpus-scan/differential output to correlate required codec
    /// features with rendered failures.
    /// </summary>
    static Command CreateJbig2ClassifyCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "PDF file or directory of PDFs to classify" };
        var outputOption = new Option<FileInfo>("--output")
        {
            Description = "Output JSON path",
            Required = true,
        };

        var command = new Command("jbig2-classify", "Classify JBIG2Decode feature buckets in PDFs")
        {
            inputArg,
            outputOption,
        };

        command.SetAction(parseResult =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var output = parseResult.GetValue(outputOption)!;

            try
            {
                var ok = RunJbig2CapabilityScan(input, output.FullName);
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

    internal static bool RunJbig2CapabilityScan(string inputPath, string outputPath)
    {
        var pdfs = ResolvePdfInputs(inputPath).ToList();
        if (pdfs.Count == 0)
        {
            Console.Error.WriteLine($"No PDFs found: {inputPath}");
            return false;
        }

        var rootDir = Directory.Exists(inputPath) ? Path.GetFullPath(inputPath) : null;
        var entries = new List<Jbig2ClassifyEntry>();
        int scanned = 0;

        foreach (var pdf in pdfs)
        {
            scanned++;
            var rel = rootDir != null
                ? Path.GetRelativePath(rootDir, pdf)
                : Path.GetFileName(pdf);

            Console.Out.WriteLine($"[{scanned}/{pdfs.Count}] {rel}");
            entries.AddRange(ClassifyJbig2StreamsInPdf(rel, pdf));
        }

        var featureCounts = CountFeatures(entries.SelectMany(e => e.features));
        var unsupportedFeatureCounts = CountFeatures(entries.SelectMany(e => e.unsupportedFeatures));
        var segmentTypeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            foreach (var kvp in entry.segmentTypeCounts)
            {
                segmentTypeCounts.TryGetValue(kvp.Key, out var count);
                segmentTypeCounts[kvp.Key] = count + kvp.Value;
            }
        }

        var counts = entries
            .GroupBy(e => e.status, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var report = new Jbig2ClassifyReport
        {
            generatedUtc = DateTime.UtcNow.ToString("o"),
            input = Path.GetFullPath(inputPath),
            pdfs = pdfs.Count,
            streams = entries.Count(e => e.status == "OK"),
            entries = entries
                .OrderBy(e => e.path, StringComparer.Ordinal)
                .ThenBy(e => e.pageNumber)
                .ThenBy(e => e.resourcePath, StringComparer.Ordinal)
                .ToList(),
            counts = counts,
            featureCounts = featureCounts,
            unsupportedFeatureCounts = unsupportedFeatureCounts,
            segmentTypeCounts = segmentTypeCounts
                .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal),
        };

        var json = System.Text.Json.JsonSerializer.Serialize(report,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, json);

        Console.Out.WriteLine();
        foreach (var kv in counts)
            Console.Out.WriteLine($"  {kv.Value,4}  {kv.Key}");
        Console.Out.WriteLine($"  JBIG2 streams: {report.streams}");
        Console.Out.WriteLine($"  wrote {outputPath}");
        return true;
    }

    private static IEnumerable<string> ResolvePdfInputs(string inputPath)
    {
        if (File.Exists(inputPath))
        {
            if (inputPath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                yield return Path.GetFullPath(inputPath);
            yield break;
        }

        if (Directory.Exists(inputPath))
        {
            foreach (var pdf in Directory.EnumerateFiles(inputPath, "*.pdf", SearchOption.AllDirectories)
                         .OrderBy(p => p, StringComparer.Ordinal))
            {
                yield return Path.GetFullPath(pdf);
            }
        }
    }

    private static IReadOnlyList<Jbig2ClassifyEntry> ClassifyJbig2StreamsInPdf(string relPath, string pdfPath)
    {
        try
        {
            using var doc = PdfDocument.Open(pdfPath);
            var entries = new List<Jbig2ClassifyEntry>();
            int streamIndex = 0;
            for (int pageNumber = 1; pageNumber <= doc.PageCount; pageNumber++)
            {
                var page = doc.GetPage(pageNumber);
                ClassifyJbig2StreamsInResources(
                    doc,
                    page.Resources,
                    relPath,
                    pageNumber,
                    "page",
                    entries,
                    ref streamIndex,
                    new HashSet<PdfStream>(),
                    depth: 0);
            }

            return entries;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new[]
            {
                new Jbig2ClassifyEntry
                {
                    path = relPath,
                    pageNumber = 0,
                    resourcePath = "",
                    status = "PDF_ERROR",
                    errorType = ex.GetType().Name,
                    errorMessage = Trunc(ex.Message, 240),
                },
            };
        }
    }

    private static void ClassifyJbig2StreamsInResources(
        PdfDocument doc,
        PdfDictionary? resources,
        string relPath,
        int pageNumber,
        string resourcePath,
        List<Jbig2ClassifyEntry> entries,
        ref int streamIndex,
        HashSet<PdfStream> visitedForms,
        int depth)
    {
        if (resources == null || depth > 64)
            return;

        var xobjectsObj = resources.GetOptional("XObject");
        if (xobjectsObj == null || doc.Resolve(xobjectsObj) is not PdfDictionary xobjects)
            return;

        foreach (var kvp in xobjects.OrderBy(k => k.Key.Value, StringComparer.Ordinal))
        {
            var name = kvp.Key.Value;
            var resolved = doc.Resolve(kvp.Value);
            if (resolved is not PdfStream stream)
                continue;

            var subtype = stream.GetNameOrNull("Subtype");
            var childPath = $"{resourcePath}/{name}";
            if (subtype == "Image" && TryGetJbig2FilterIndex(stream, out var filterIndex))
            {
                streamIndex++;
                entries.Add(ClassifyJbig2ImageStream(doc, stream, relPath, pageNumber, childPath, streamIndex, filterIndex));
            }
            else if (subtype == "Form" && visitedForms.Add(stream))
            {
                var formResourcesObj = stream.GetOptional("Resources");
                var formResources = formResourcesObj != null ? doc.Resolve(formResourcesObj) as PdfDictionary : null;
                ClassifyJbig2StreamsInResources(
                    doc,
                    formResources,
                    relPath,
                    pageNumber,
                    childPath,
                    entries,
                    ref streamIndex,
                    visitedForms,
                    depth + 1);
            }
        }
    }

    private static Jbig2ClassifyEntry ClassifyJbig2ImageStream(
        PdfDocument doc,
        PdfStream stream,
        string relPath,
        int pageNumber,
        string resourcePath,
        int streamIndex,
        int filterIndex)
    {
        var filters = stream.Filters.ToArray();
        var parms = GetResolvedDecodeParams(doc, stream);
        var entry = new Jbig2ClassifyEntry
        {
            path = relPath,
            pageNumber = pageNumber,
            resourcePath = resourcePath,
            streamIndex = streamIndex,
            width = stream.GetInt("Width", 0),
            height = stream.GetInt("Height", 0),
            filters = filters,
        };

        try
        {
            byte[] data = ExtractInputForFilter(stream, filters, parms, filterIndex);
            byte[]? globals = TryGetJbig2Globals(doc, filterIndex < parms.Count ? parms[filterIndex] : null);
            var report = Jbig2CapabilityClassifier.Analyze(data, globals);
            entry.status = report.Diagnostics.Count == 0 ? "OK" : "PARSE_WARNING";
            entry.features = report.Features.ToArray();
            entry.unsupportedFeatures = report.UnsupportedFeatures.ToArray();
            entry.segmentTypeCounts = report.SegmentTypeCounts
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal);
            entry.diagnostics = report.Diagnostics.ToArray();
            entry.segments = report.Segments.ToArray();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            entry.status = "CLASSIFY_ERROR";
            entry.errorType = ex.GetType().Name;
            entry.errorMessage = Trunc(ex.Message, 240);
        }

        return entry;
    }

    private static byte[] ExtractInputForFilter(
        PdfStream stream,
        IReadOnlyList<string> filters,
        IReadOnlyList<PdfDictionary?> parms,
        int filterIndex)
    {
        var data = stream.EncodedData;
        if (filterIndex == 0)
            return data;

        var decompressor = new StreamDecompressor();
        for (int i = 0; i < filterIndex; i++)
        {
            var filterParms = i < parms.Count ? parms[i] : null;
            data = decompressor.ApplyFilter(filters[i], data, filterParms);
        }

        return data;
    }

    private static bool TryGetJbig2FilterIndex(PdfStream stream, out int index)
    {
        var filters = stream.Filters;
        for (int i = 0; i < filters.Count; i++)
        {
            if (filters[i] is "JBIG2Decode" or "JBIG2")
            {
                index = i;
                return true;
            }
        }

        index = -1;
        return false;
    }

    private static IReadOnlyList<PdfDictionary?> GetResolvedDecodeParams(PdfDocument doc, PdfStream stream)
    {
        var parms = stream.GetOptional("DecodeParms") ?? stream.GetOptional("DP");
        return parms switch
        {
            PdfDictionary d => new[] { d },
            PdfReference r when doc.Resolve(r) is PdfDictionary d => new[] { d },
            PdfArray a => a.Select(item => doc.Resolve(item) as PdfDictionary).ToArray(),
            _ => Array.Empty<PdfDictionary?>(),
        };
    }

    private static byte[]? TryGetJbig2Globals(PdfDocument doc, PdfDictionary? decodeParms)
    {
        var globalsObj = decodeParms?.GetOptional("JBIG2Globals");
        if (globalsObj == null)
            return null;

        return doc.Resolve(globalsObj) is PdfStream globals
            ? globals.EncodedData
            : null;
    }

    private static Dictionary<string, int> CountFeatures(IEnumerable<string> features)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var feature in features)
        {
            counts.TryGetValue(feature, out var count);
            counts[feature] = count + 1;
        }

        return counts
            .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal);
    }

    /// <summary>
    /// Pdfe.RenderTools corpus-scan &lt;corpus-dir&gt; --output out.json
    ///                  [--chunk N] [--total M] [--dpi 150]
    ///                  [--page-manifest manifest.tsv]
    ///                  [--password-manifest passwords.tsv]
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
        var pageManifestOption = new Option<FileInfo?>("--page-manifest")
        {
            Description = "Optional TSV of exact corpus-relative PDF pages to scan. Columns: path<TAB>pageNumber; extra columns ignored.",
        };
        var passwordManifestOption = new Option<FileInfo?>("--password-manifest")
        {
            Description = "Optional TSV of corpus-relative PDF passwords. Columns: path<TAB>userPassword; extra columns ignored.",
        };
        var expectationManifestOption = new Option<FileInfo?>("--expectation-manifest")
        {
            Description = "Optional TSV of expected corpus outcomes. Columns: path<TAB>pageNumber<TAB>expectedStatus<TAB>expectedErrorContains<TAB>note[<TAB>resultStatus<TAB>resultCategory<TAB>resultReason].",
        };
        var extraOraclesOption = new Option<string>("--extra-oracles")
        {
            Description = "Optional escalation oracles: none, ghostscript, pdfbox, pdfium, or all (comma-separated).",
            DefaultValueFactory = _ => "ghostscript",
        };
        var oracleCacheDirOption = new Option<DirectoryInfo?>("--oracle-cache-dir")
        {
            Description = "Directory for cached third-party oracle PNGs. Defaults to $PDFE_ORACLE_CACHE_DIR or the system temp cache.",
        };
        var noOracleCacheOption = new Option<bool>("--no-oracle-cache")
        {
            Description = "Disable the third-party oracle render cache for cold timing runs.",
            DefaultValueFactory = _ => false,
        };
        var pdfeRenderCacheDirOption = new Option<DirectoryInfo?>("--pdfe-render-cache-dir")
        {
            Description = "Directory for cached pdfe-rendered PNGs. Disabled unless explicitly set.",
        };
        var progressIntervalOption = new Option<int>("--progress-interval-seconds")
        {
            Description = "Emit corpus-scan heartbeat progress every N seconds. 0 disables heartbeat output.",
            DefaultValueFactory = _ => 30,
        };
        var progressOutputOption = new Option<FileInfo?>("--progress-output")
        {
            Description = "Optional JSON sidecar for heartbeat progress. Defaults to <output>.progress.json when heartbeat is enabled.",
        };

        var command = new Command("corpus-scan",
            "Render corpus PDFs with pdfe + reference oracles, compute pixel-diff, write JSON report")
        {
            corpusArg, outputOption, chunkOption, totalOption,
            dpiOption, diffPctOption, maxMaeOption, parallelOption, perPdfTimeoutOption, pageModeOption,
            pageManifestOption, passwordManifestOption, expectationManifestOption, extraOraclesOption,
            oracleCacheDirOption, noOracleCacheOption, pdfeRenderCacheDirOption,
            progressIntervalOption, progressOutputOption,
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
            var pageManifestFile = parseResult.GetValue(pageManifestOption);
            var passwordManifestFile = parseResult.GetValue(passwordManifestOption);
            var expectationManifestFile = parseResult.GetValue(expectationManifestOption);
            var extraOraclesRaw = parseResult.GetValue(extraOraclesOption) ?? "ghostscript";
            var oracleCacheDir = parseResult.GetValue(oracleCacheDirOption);
            var noOracleCache = parseResult.GetValue(noOracleCacheOption);
            var pdfeRenderCacheDir = parseResult.GetValue(pdfeRenderCacheDirOption);
            var progressIntervalSeconds = parseResult.GetValue(progressIntervalOption);
            var progressOutput = parseResult.GetValue(progressOutputOption);

            if (parallel <= 0) parallel = Math.Max(1, Environment.ProcessorCount / 2);
            if (!TryParseCorpusPageMode(pageModeRaw, out var pageMode))
            {
                Console.Error.WriteLine($"Bad --page-mode '{pageModeRaw}'. Use first, sample, or all.");
                Environment.ExitCode = 1; return;
            }
            if (!TryParseCorpusExtraOracles(extraOraclesRaw, out var extraOracles, out var extraOracleError))
            {
                Console.Error.WriteLine(extraOracleError);
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
                var pageManifest = LoadCorpusPageManifest(pageManifestFile);
                var passwordManifest = LoadCorpusPasswordManifest(passwordManifestFile);
                var expectationManifest = LoadCorpusExpectationManifest(expectationManifestFile);
                var ok = RunCorpusScan(corpus.FullName, output.FullName,
                    chunk, total, dpi, diffPct, maxMae, parallel, pdfTimeoutMs, pageMode, extraOracles,
                    pageManifest, passwordManifest, expectationManifest,
                    noOracleCache ? null : ResolveCorpusOracleCacheDir(oracleCacheDir),
                    pdfeRenderCacheDir,
                    progressIntervalSeconds,
                    progressOutput?.FullName);
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
        int parallel, int pdfTimeoutMs, CorpusPageMode pageMode = CorpusPageMode.First,
        CorpusExtraOracles extraOracles = CorpusExtraOracles.Ghostscript,
        IReadOnlyDictionary<string, IReadOnlySet<int>>? pageManifest = null,
        IReadOnlyDictionary<string, string>? passwordManifest = null,
        IReadOnlyDictionary<CorpusPageKey, CorpusExpectedOutcome>? expectationManifest = null,
        DirectoryInfo? oracleCacheDir = null,
        DirectoryInfo? pdfeRenderCacheDir = null,
        int progressIntervalSeconds = 30,
        string? progressOutputPath = null)
    {
        var pdfs = DiscoverCorpusPdfs(corpusDir, chunkIndex, chunkTotal, pageManifest?.Keys);
        var oracleCache = CreateOracleRenderCache(oracleCacheDir);
        var pdfeRenderCache = CreatePdfeRenderCache(pdfeRenderCacheDir);

        Console.Out.WriteLine(
            $"chunk {chunkIndex + 1}/{chunkTotal}: scanning {pdfs.Count} PDFs in {corpusDir} " +
            $"({parallel}-way parallel, {pdfTimeoutMs}ms oracle timeout, page-mode={PageModeName(pageMode)}, " +
            $"page-manifest={(pageManifest is null ? "none" : pageManifest.Count.ToString())}, " +
            $"password-manifest={(passwordManifest is null ? "none" : passwordManifest.Count.ToString())}, " +
            $"expectation-manifest={(expectationManifest is null ? "none" : expectationManifest.Count.ToString())}, " +
            $"extra-oracles={ExtraOraclesName(extraOracles)}, " +
            $"oracle-cache={(oracleCache is null ? "off" : oracleCache.CacheDirectory)}, " +
            $"pdfe-cache={(pdfeRenderCache is null ? "off" : pdfeRenderCache.CacheDirectory)})");

        // Use a thread-safe collector. Order in the final JSON is
        // restored by sort-on-write since Parallel.ForEach completion
        // order is non-deterministic.
        var entries = new System.Collections.Concurrent.ConcurrentBag<CorpusScanEntry>();
        var activeProgress = new System.Collections.Concurrent.ConcurrentDictionary<string, CorpusScanProgress>(StringComparer.Ordinal);
        var scanStopwatch = Stopwatch.StartNew();
        long peakBytes = 0;
        int processed = 0;
        var resolvedProgressOutputPath = progressIntervalSeconds <= 0
            ? null
            : string.IsNullOrWhiteSpace(progressOutputPath)
                ? outputPath + ".progress.json"
                : progressOutputPath;
        using var progressReporter = StartCorpusScanProgressReporter(
            pdfs.Count,
            entries,
            activeProgress,
            () => System.Threading.Volatile.Read(ref processed),
            () => System.Threading.Volatile.Read(ref peakBytes),
            rss => ObservePeakBytes(ref peakBytes, rss),
            scanStopwatch,
            progressIntervalSeconds,
            resolvedProgressOutputPath,
            chunkIndex,
            chunkTotal,
            pageMode,
            extraOracles);

        var po = new ParallelOptions { MaxDegreeOfParallelism = parallel };
        Parallel.ForEach(pdfs, po, corpusPdf =>
        {
            var pdf = corpusPdf.FullPath;
            var rel = corpusPdf.RelativePath;
            IReadOnlyList<CorpusScanEntry> pdfEntries;
            var pdfStopwatch = Stopwatch.StartNew();
            var progress = new CorpusScanProgress(rel);
            activeProgress[rel] = progress;
            try
            {
                IReadOnlySet<int>? selectedPages = null;
                pageManifest?.TryGetValue(rel, out selectedPages);
                string? userPassword = null;
                if (passwordManifest != null)
                    TryGetCorpusPassword(passwordManifest, rel, out userPassword);
                var wallBudgetMs = ComputeCorpusScanWallBudgetMs(
                    pdfTimeoutMs, pageMode, extraOracles, selectedPages);
                var task = Task.Run(() => ScanOnePdf(rel, pdf, dpi, maxDiffFraction, maxMae, pdfTimeoutMs,
                    pageMode, extraOracles, selectedPages, progress, userPassword, oracleCache, pdfeRenderCache));
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
                            diagnostic = $"{snapshot.Path}: {snapshot.Detail}",
                            errorMessage = $"Per-PDF budget {wallBudgetMs}ms exceeded during {snapshot.Phase}: {snapshot.Path}: {snapshot.Detail}",
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
                ObservePeakBytes(ref peakBytes, rss);

                // Periodic GC to keep SkiaSharp's native memory bounded.
                // Less aggressive than the serial version because parallel
                // workers naturally interleave finalization.
                if (n % 20 == 0)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
            }
            finally
            {
                activeProgress.TryRemove(rel, out _);
            }
        });

        GC.Collect();
        GC.WaitForPendingFinalizers();

        // Sort entries by path so parallel completion order doesn't make
        // diffs noisy across runs, then annotate expectation-aware result
        // status before printing or serializing summaries.
        var sortedEntries = entries.OrderBy(e => e.path).ThenBy(e => e.pageNumber).ToList();
        ApplyCorpusExpectations(sortedEntries, expectationManifest);

        var counts = new Dictionary<string, int>();
        foreach (var e in sortedEntries)
        {
            counts.TryGetValue(e.status, out var c);
            counts[e.status] = c + 1;
        }

        Console.Out.WriteLine();
        foreach (var kv in counts.OrderByDescending(kv => kv.Value))
            Console.Out.WriteLine($"  {kv.Value,4}  {kv.Key}");
        if (expectationManifest is not null)
        {
            Console.Out.WriteLine("  result counts:");
            foreach (var kv in CountBy(sortedEntries.Select(entry => entry.resultStatus)).OrderByDescending(kv => kv.Value))
                Console.Out.WriteLine($"  {kv.Value,4}  {kv.Key}");
        }
        Console.Out.WriteLine($"  total processed: {entries.Count}");
        Console.Out.WriteLine($"  peak RSS: {peakBytes / 1024 / 1024} MB");
        var oracleCacheReport = oracleCache?.CreateReport() ?? CorpusOracleCacheReport.CreateDisabled();
        if (oracleCacheReport.enabled)
        {
            Console.Out.WriteLine(
                $"  oracle cache: {oracleCacheReport.hits} hits, {oracleCacheReport.misses} misses, " +
                $"{oracleCacheReport.writes} writes, {oracleCacheReport.errors} errors");
        }
        var pdfeRenderCacheReport = pdfeRenderCache?.CreateReport() ?? CorpusRenderCacheReport.CreateDisabled();
        if (pdfeRenderCacheReport.enabled)
        {
            Console.Out.WriteLine(
                $"  pdfe render cache: {pdfeRenderCacheReport.hits} hits, {pdfeRenderCacheReport.misses} misses, " +
                $"{pdfeRenderCacheReport.writes} writes, {pdfeRenderCacheReport.errors} errors");
        }

        var report = new
        {
            generatedUtc = DateTime.UtcNow.ToString("o"),
            corpus = corpusDir,
            chunkIndex,
            chunkTotal,
            pageMode = PageModeName(pageMode),
            pageManifest = pageManifest is null ? null : new { pdfs = pageManifest.Count },
            passwordManifest = passwordManifest is null ? null : new { pdfs = passwordManifest.Count },
            expectationManifest = expectationManifest is null ? null : new { entries = expectationManifest.Count },
            extraOracles = ExtraOraclesName(extraOracles),
            counts,
            resultCounts = CountBy(sortedEntries.Select(entry => entry.resultStatus)),
            resultCategoryCounts = CountBy(sortedEntries.Select(GetCorpusResultCategory)),
            summary = BuildCorpusScanSummary(sortedEntries),
            total = entries.Count,
            pdfs = pdfs.Count,
            peakRssBytes = peakBytes,
            oracleCache = oracleCacheReport,
            pdfeRenderCache = pdfeRenderCacheReport,
            entries = sortedEntries,
        };
        var json = System.Text.Json.JsonSerializer.Serialize(report,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, json);
        Console.Out.WriteLine($"  wrote {outputPath}");
        return true;
    }

    internal readonly record struct CorpusPdf(string FullPath, string RelativePath);

    internal readonly record struct CorpusPageKey(string Path, int PageNumber);

    internal readonly record struct CorpusExpectedOutcome(
        string ExpectedStatus,
        string ExpectedErrorContains,
        string Note,
        string ExpectedResultStatus = "",
        string ExpectedResultCategory = "",
        string ExpectedResultReason = "");

    internal static IReadOnlyList<CorpusPdf> DiscoverCorpusPdfs(
        string corpusDir,
        int chunkIndex,
        int chunkTotal,
        IEnumerable<string>? includeRelativePaths = null)
    {
        HashSet<string>? include = includeRelativePaths is null
            ? null
            : new HashSet<string>(
                includeRelativePaths.Select(NormalizeManifestPath),
                StringComparer.Ordinal);

        // Keep this ordinal order in sync with scripts/run-exploratory-corpus.sh
        // isolated recovery for the flat pdf.js corpus. Culture-sensitive sorting
        // can select a different chunk slice and silently duplicate/miss PDFs in
        // merged reports. Use relative paths so nested corpora such as veraPDF
        // and Isartor keep stable, unique report paths.
        return Directory.EnumerateFiles(corpusDir, "*.pdf", SearchOption.AllDirectories)
            .Select(path => new CorpusPdf(path, NormalizeCorpusRelativePath(corpusDir, path)))
            .Where(pdf => include is null || include.Contains(pdf.RelativePath))
            .OrderBy(p => p.RelativePath, StringComparer.Ordinal)
            .Select((pdf, idx) => (pdf, idx))
            .Where(t => t.idx % chunkTotal == chunkIndex)
            .Select(t => t.pdf)
            .ToList();
    }

    private static string NormalizeCorpusRelativePath(string corpusDir, string pdfPath)
    {
        var relative = Path.GetRelativePath(corpusDir, pdfPath);
        return NormalizeManifestPath(relative);
    }

    private static IReadOnlyList<CorpusScanEntry> ScanOnePdf(
        string relPath, string pdfPath, int dpi,
        double maxDiffFraction, double maxMae, int oracleTimeoutMs,
        CorpusPageMode pageMode,
        CorpusExtraOracles extraOracles,
        IReadOnlySet<int>? selectedPages = null,
        CorpusScanProgress? progress = null,
        string? userPassword = null,
        OracleRenderCache? oracleCache = null,
        PdfeRenderCache? pdfeRenderCache = null)
    {
        PdfDocument? doc = null;
        int pageCount;
        var pdfStopwatch = Stopwatch.StartNew();
        try
        {
            progress?.Update("open", 0, "PdfDocument.Open");
            doc = userPassword is null
                ? PdfDocument.Open(pdfPath)
                : PdfDocument.Open(pdfPath, userPassword);
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
            if (selectedPages is not null)
            {
                foreach (var invalidPage in selectedPages.Where(page => page > pageCount).OrderBy(page => page))
                {
                    entries.Add(new CorpusScanEntry
                    {
                        path = relPath,
                        pageNumber = invalidPage,
                        pageCount = pageCount,
                        status = "INVALID_PAGE_NUMBER",
                        errorPhase = "page-select",
                        errorType = "ManifestPageOutOfRange",
                        errorMessage =
                            $"Page manifest requested page {invalidPage}, but the document has {pageCount} page(s).",
                    });
                }
            }

            foreach (var pageNumber in SelectCorpusPages(pageCount, pageMode, selectedPages))
            {
                progress?.Update("page", pageNumber, $"page {pageNumber}/{pageCount}");
                entries.Add(ScanOnePage(relPath, pdfPath, doc, renderer, pageNumber, dpi,
                    maxDiffFraction, maxMae, oracleTimeoutMs, extraOracles, progress, userPassword, oracleCache,
                    pdfeRenderCache));
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
        CorpusExtraOracles extraOracles,
        CorpusScanProgress? progress = null,
        string? userPassword = null,
        OracleRenderCache? oracleCache = null,
        PdfeRenderCache? pdfeRenderCache = null)
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
        SkiaSharp.SKBitmap? ghostscriptBmp = null;
        SkiaSharp.SKBitmap? pdfboxBmp = null;
        SkiaSharp.SKBitmap? pdfiumBmp = null;
        var comparisonDpi = dpi;
        var renderDiagnostics = new List<string>();
        try
        {
            try
            {
                progress?.Update("render", pageNumber, $"pdfe render page {pageNumber}/{doc.PageCount}");
                renderDiagnostics.Clear();
                var pdfeOutcome = RenderPdfeWithCache(
                    pdfeRenderCache, pdfPath, pageNumber, dpi, userPassword,
                    () =>
                    {
                        var renderStopwatch = Stopwatch.StartNew();
                        var bitmap = renderer.RenderPage(
                            doc.GetPage(pageNumber),
                            new RenderOptions { Dpi = dpi, Diagnostics = renderDiagnostics });
                        renderStopwatch.Stop();
                        return new PdfeRenderResult(
                            bitmap,
                            "OK",
                            null,
                            renderStopwatch.ElapsedMilliseconds,
                            renderDiagnostics.ToArray());
                    });
                pdfeBmp = pdfeOutcome.Result.Bitmap;
                entry.renderMs = pdfeOutcome.Result.ElapsedMs;
                ApplyPdfeCacheFields(entry, pdfeOutcome);
                ApplyRenderDiagnostics(entry, pdfeOutcome.Result.Diagnostics);
            }
            catch (Exception ex)
            {
                if (ex is Pdfe.Rendering.RenderResourceLimitException
                    && TryComputeResourceSafeDpi(doc.GetPage(pageNumber), dpi, out var fallbackDpi))
                {
                    progress?.Update("render", pageNumber,
                        $"pdfe render page {pageNumber}/{doc.PageCount} at fallback {fallbackDpi} DPI");
                    try
                    {
                        renderDiagnostics.Clear();
                        var fallbackOutcome = RenderPdfeWithCache(
                            pdfeRenderCache, pdfPath, pageNumber, fallbackDpi, userPassword,
                            () =>
                            {
                                var fallbackStopwatch = Stopwatch.StartNew();
                                var bitmap = renderer.RenderPage(
                                    doc.GetPage(pageNumber),
                                    new RenderOptions { Dpi = fallbackDpi, Diagnostics = renderDiagnostics });
                                fallbackStopwatch.Stop();
                                return new PdfeRenderResult(
                                    bitmap,
                                    "OK",
                                    null,
                                    fallbackStopwatch.ElapsedMilliseconds,
                                    renderDiagnostics.ToArray());
                            });
                        pdfeBmp = fallbackOutcome.Result.Bitmap;
                        entry.renderMs = fallbackOutcome.Result.ElapsedMs;
                        ApplyPdfeCacheFields(entry, fallbackOutcome);
                        entry.effectiveDpi = fallbackDpi;
                        entry.diagnostic =
                            $"Requested {dpi} DPI exceeded the render pixel cap; compared at {fallbackDpi} DPI.";
                        ApplyRenderDiagnostics(entry, fallbackOutcome.Result.Diagnostics);
                        comparisonDpi = fallbackDpi;
                    }
                    catch (Exception fallbackEx)
                    {
                        pageStopwatch.Stop();
                        entry.status = ClassifyCorpusFailure(fallbackEx, CorpusFailurePhase.Render);
                        entry.errorPhase = "render";
                        entry.errorType = fallbackEx.GetType().Name;
                        entry.errorMessage = Trunc(fallbackEx.Message, 200);
                        entry.elapsedMs = pageStopwatch.ElapsedMilliseconds;
                        return entry;
                    }
                }
                else
                {
                    pageStopwatch.Stop();
                    entry.status = ClassifyCorpusFailure(ex, CorpusFailurePhase.Render);
                    entry.errorPhase = "render";
                    entry.errorType = ex.GetType().Name;
                    entry.errorMessage = Trunc(ex.Message, 200);
                    entry.elapsedMs = pageStopwatch.ElapsedMilliseconds;
                    return entry;
                }
            }

            if (pdfeBmp == null)
            {
                pageStopwatch.Stop();
                entry.status = "RENDER_NULL";
                entry.elapsedMs = pageStopwatch.ElapsedMilliseconds;
                return entry;
            }

            if (TryApplyRecoveredMalformedContentShortCircuit(entry, pageStopwatch))
                return entry;

            // Primary oracles: mutool (MuPDF) and pdftocairo (Poppler).
            // Optional escalation oracles run only when the primary pair
            // does not both agree, keeping passing pages cheap while giving
            // remaining DIFFs more evidence.
            progress?.Update("mutool", pageNumber, $"mutool render page {pageNumber}/{doc.PageCount}");
            var mutoolOutcome = RenderOracleWithCache(
                oracleCache, "mutool", pdfPath, pageNumber, comparisonDpi, userPassword,
                () => MutoolReferenceRenderer.TryRenderPage(
                    pdfPath, pageNumber, comparisonDpi, oracleTimeoutMs, userPassword));
            var mutoolResult = mutoolOutcome.Result;
            mutoolBmp = mutoolResult.Bitmap;
            entry.mutoolMs = mutoolResult.ElapsedMs;
            entry.mutoolStatus = mutoolResult.Status;
            entry.mutoolError = TruncNullable(mutoolResult.ErrorMessage, 200);
            ApplyOracleCacheFields(entry, "mutool", mutoolOutcome);

            progress?.Update("pdftocairo", pageNumber, $"pdftocairo render page {pageNumber}/{doc.PageCount}");
            var cairoOutcome = RenderOracleWithCache(
                oracleCache, "pdftocairo", pdfPath, pageNumber, comparisonDpi, userPassword,
                () => PdftocairoReferenceRenderer.TryRenderPage(
                    pdfPath, pageNumber, comparisonDpi, oracleTimeoutMs, userPassword));
            var cairoResult = cairoOutcome.Result;
            cairoBmp = cairoResult.Bitmap;
            entry.cairoMs = cairoResult.ElapsedMs;
            entry.cairoStatus = cairoResult.Status;
            entry.cairoError = TruncNullable(cairoResult.ErrorMessage, 200);
            ApplyOracleCacheFields(entry, "pdftocairo", cairoOutcome);

            var metrics = new List<(string Name, double Diff, double Mae)>();

            (double diff, double mae)? mutoolMetrics = null;
            (double diff, double mae)? cairoMetrics = null;
            (double diff, double mae)? ghostscriptMetrics = null;
            (double diff, double mae)? pdfboxMetrics = null;
            (double diff, double mae)? pdfiumMetrics = null;

            if (mutoolBmp != null)
            {
                progress?.Update("compare", pageNumber, $"compare pdfe vs mutool page {pageNumber}/{doc.PageCount}");
                var (a, b) = MatchAndCompare(pdfeBmp, mutoolBmp);
                mutoolMetrics = (a, b);
                entry.diffFractionMutool = a;
                entry.maeMutool = b;
                metrics.Add(("mutool", a, b));
            }
            if (cairoBmp != null)
            {
                progress?.Update("compare", pageNumber, $"compare pdfe vs pdftocairo page {pageNumber}/{doc.PageCount}");
                var (a, b) = MatchAndCompare(pdfeBmp, cairoBmp);
                cairoMetrics = (a, b);
                entry.diffFractionCairo = a;
                entry.maeCairo = b;
                metrics.Add(("pdftocairo", a, b));
            }

            bool passMutool = IsPassing(mutoolMetrics, maxDiffFraction, maxMae);
            bool passCairo = IsPassing(cairoMetrics, maxDiffFraction, maxMae);
            var shouldEscalate = extraOracles != CorpusExtraOracles.None && !(passMutool && passCairo);

            if (shouldEscalate && extraOracles.HasFlag(CorpusExtraOracles.Ghostscript))
            {
                progress?.Update("ghostscript", pageNumber, $"ghostscript render page {pageNumber}/{doc.PageCount}");
                var ghostscriptOutcome = RenderOracleWithCache(
                    oracleCache, "ghostscript", pdfPath, pageNumber, comparisonDpi, userPassword,
                    () => GhostscriptReferenceRenderer.TryRenderPage(
                        pdfPath, pageNumber, comparisonDpi, oracleTimeoutMs, userPassword));
                var ghostscriptResult = ghostscriptOutcome.Result;
                ghostscriptBmp = ghostscriptResult.Bitmap;
                entry.ghostscriptMs = ghostscriptResult.ElapsedMs;
                entry.ghostscriptStatus = ghostscriptResult.Status;
                entry.ghostscriptError = TruncNullable(ghostscriptResult.ErrorMessage, 200);
                ApplyOracleCacheFields(entry, "ghostscript", ghostscriptOutcome);
                if (ghostscriptBmp != null)
                {
                    progress?.Update("compare", pageNumber, $"compare pdfe vs ghostscript page {pageNumber}/{doc.PageCount}");
                    var (a, b) = MatchAndCompare(pdfeBmp, ghostscriptBmp);
                    ghostscriptMetrics = (a, b);
                    entry.diffFractionGhostscript = a;
                    entry.maeGhostscript = b;
                    metrics.Add(("ghostscript", a, b));
                }
            }

            if (shouldEscalate && extraOracles.HasFlag(CorpusExtraOracles.PdfBox))
            {
                progress?.Update("pdfbox", pageNumber, $"pdfbox render page {pageNumber}/{doc.PageCount}");
                var pdfboxOutcome = RenderOracleWithCache(
                    oracleCache, "pdfbox", pdfPath, pageNumber, comparisonDpi, userPassword,
                    () => PdfBoxReferenceRenderer.TryRenderPage(
                        pdfPath, pageNumber, comparisonDpi, oracleTimeoutMs, userPassword));
                var pdfboxResult = pdfboxOutcome.Result;
                pdfboxBmp = pdfboxResult.Bitmap;
                entry.pdfboxMs = pdfboxResult.ElapsedMs;
                entry.pdfboxStatus = pdfboxResult.Status;
                entry.pdfboxError = TruncNullable(pdfboxResult.ErrorMessage, 200);
                ApplyOracleCacheFields(entry, "pdfbox", pdfboxOutcome);
                if (pdfboxBmp != null)
                {
                    progress?.Update("compare", pageNumber, $"compare pdfe vs pdfbox page {pageNumber}/{doc.PageCount}");
                    var (a, b) = MatchAndCompare(pdfeBmp, pdfboxBmp);
                    pdfboxMetrics = (a, b);
                    entry.diffFractionPdfBox = a;
                    entry.maePdfBox = b;
                    metrics.Add(("pdfbox", a, b));
                }
            }

            if (shouldEscalate && extraOracles.HasFlag(CorpusExtraOracles.Pdfium))
            {
                progress?.Update("pdfium", pageNumber, $"pdfium_test render page {pageNumber}/{doc.PageCount}");
                var pdfiumOutcome = RenderOracleWithCache(
                    oracleCache, "pdfium", pdfPath, pageNumber, comparisonDpi, userPassword: null,
                    () => PdfiumReferenceRenderer.TryRenderPage(
                        pdfPath, pageNumber, comparisonDpi, oracleTimeoutMs));
                var pdfiumResult = pdfiumOutcome.Result;
                pdfiumBmp = pdfiumResult.Bitmap;
                entry.pdfiumMs = pdfiumResult.ElapsedMs;
                entry.pdfiumStatus = pdfiumResult.Status;
                entry.pdfiumError = TruncNullable(pdfiumResult.ErrorMessage, 200);
                ApplyOracleCacheFields(entry, "pdfium", pdfiumOutcome);
                if (pdfiumBmp != null)
                {
                    progress?.Update("compare", pageNumber, $"compare pdfe vs pdfium page {pageNumber}/{doc.PageCount}");
                    var (a, b) = MatchAndCompare(pdfeBmp, pdfiumBmp);
                    pdfiumMetrics = (a, b);
                    entry.diffFractionPdfium = a;
                    entry.maePdfium = b;
                    metrics.Add(("pdfium", a, b));
                }
            }

            if (metrics.Count == 0)
            {
                pageStopwatch.Stop();
                if (HasRecoveredMalformedContentDiagnostic(entry))
                {
                    entry.status = "RECOVERED_MALFORMED_CONTENT";
                    entry.errorPhase = "render";
                    entry.errorType = "RecoveredMalformedContent";
                }
                else
                {
                    entry.status = "ALL_ORACLES_REFUSED";
                    entry.errorPhase = "oracle";
                    entry.errorType = "AllOraclesRefused";
                }
                entry.diagnostic = AppendDiagnostic(entry.diagnostic, BuildOracleDiagnostic(entry));
                entry.elapsedMs = pageStopwatch.ElapsedMilliseconds;
                return entry;
            }

            ApplyOracleDisagreementMetrics(
                entry,
                new (string Name, SkiaSharp.SKBitmap? Bitmap)[]
                {
                    ("mutool", mutoolBmp),
                    ("pdftocairo", cairoBmp),
                    ("ghostscript", ghostscriptBmp),
                    ("pdfbox", pdfboxBmp),
                    ("pdfium", pdfiumBmp),
                },
                maxDiffFraction,
                maxMae);

            var best = metrics.OrderBy(m => m.Diff).First();
            entry.diffFraction = best.Diff;
            entry.mae = best.Mae;
            entry.bestOracle = best.Name;
            var bestReference = best.Name switch
            {
                "mutool" => mutoolBmp,
                "pdftocairo" => cairoBmp,
                "ghostscript" => ghostscriptBmp,
                "pdfbox" => pdfboxBmp,
                "pdfium" => pdfiumBmp,
                _ => null,
            };
            if (bestReference != null)
                ApplyCorpusVisualDiffClassification(entry, pdfeBmp, bestReference);

            var passGhostscript = IsPassing(ghostscriptMetrics, maxDiffFraction, maxMae);
            var passPdfBox = IsPassing(pdfboxMetrics, maxDiffFraction, maxMae);
            var passPdfium = IsPassing(pdfiumMetrics, maxDiffFraction, maxMae);
            entry.comparedOracles = metrics.Count;
            entry.agreeingOracles =
                (passMutool ? 1 : 0)
                + (passCairo ? 1 : 0)
                + (passGhostscript ? 1 : 0)
                + (passPdfBox ? 1 : 0)
                + (passPdfium ? 1 : 0);
            if (!(passMutool && passCairo))
            {
                ApplyReferenceCenterMetrics(
                    entry,
                    new (string Name, SkiaSharp.SKBitmap? Bitmap)[]
                    {
                        ("pdfe", pdfeBmp),
                        ("mutool", mutoolBmp),
                        ("pdftocairo", cairoBmp),
                        ("ghostscript", ghostscriptBmp),
                        ("pdfbox", pdfboxBmp),
                        ("pdfium", pdfiumBmp),
                    },
                    maxDiffFraction,
                    maxMae);
            }

            if (passMutool && passCairo)        entry.status = "PASS";
            else if (entry.agreeingOracles.GetValueOrDefault() > 0 ||
                     entry.referenceCenterAgreement == true) entry.status = "PASS_ONE";  // partial agreement
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
            ghostscriptBmp?.Dispose();
            pdfboxBmp?.Dispose();
            pdfiumBmp?.Dispose();
        }
        return entry;
    }

    private static OracleRenderOutcome RenderOracleWithCache(
        OracleRenderCache? cache,
        string oracleName,
        string pdfPath,
        int pageNumber,
        int dpi,
        string? userPassword,
        Func<ReferenceRenderResult> render)
    {
        return cache?.GetOrRender(oracleName, pdfPath, pageNumber, dpi, userPassword, render)
               ?? new OracleRenderOutcome(render(), CacheEnabled: false, CacheHit: false,
                   CachedRenderMs: null, CachedStatus: null, CachedErrorMessage: null);
    }

    private static PdfeRenderOutcome RenderPdfeWithCache(
        PdfeRenderCache? cache,
        string pdfPath,
        int pageNumber,
        int dpi,
        string? userPassword,
        Func<PdfeRenderResult> render)
    {
        return cache?.GetOrRender(pdfPath, pageNumber, dpi, userPassword, render)
               ?? new PdfeRenderOutcome(render(), CacheEnabled: false, CacheHit: false,
                   CachedRenderMs: null, CachedStatus: null, CachedErrorMessage: null);
    }

    private static void ApplyPdfeCacheFields(CorpusScanEntry entry, PdfeRenderOutcome outcome)
    {
        if (!outcome.CacheEnabled)
            return;

        entry.pdfeCacheHit = outcome.CacheHit;
        entry.pdfeCachedRenderMs = outcome.CachedRenderMs;
        entry.pdfeCachedStatus = outcome.CachedStatus;
        entry.pdfeCachedError = TruncNullable(outcome.CachedErrorMessage, 200);
    }

    private static void ApplyOracleCacheFields(
        CorpusScanEntry entry,
        string oracleName,
        OracleRenderOutcome outcome)
    {
        if (!outcome.CacheEnabled)
            return;

        switch (oracleName)
        {
            case "mutool":
                entry.mutoolCacheHit = outcome.CacheHit;
                entry.mutoolCachedRenderMs = outcome.CachedRenderMs;
                entry.mutoolCachedStatus = outcome.CachedStatus;
                entry.mutoolCachedError = TruncNullable(outcome.CachedErrorMessage, 200);
                break;
            case "pdftocairo":
                entry.cairoCacheHit = outcome.CacheHit;
                entry.cairoCachedRenderMs = outcome.CachedRenderMs;
                entry.cairoCachedStatus = outcome.CachedStatus;
                entry.cairoCachedError = TruncNullable(outcome.CachedErrorMessage, 200);
                break;
            case "ghostscript":
                entry.ghostscriptCacheHit = outcome.CacheHit;
                entry.ghostscriptCachedRenderMs = outcome.CachedRenderMs;
                entry.ghostscriptCachedStatus = outcome.CachedStatus;
                entry.ghostscriptCachedError = TruncNullable(outcome.CachedErrorMessage, 200);
                break;
            case "pdfbox":
                entry.pdfboxCacheHit = outcome.CacheHit;
                entry.pdfboxCachedRenderMs = outcome.CachedRenderMs;
                entry.pdfboxCachedStatus = outcome.CachedStatus;
                entry.pdfboxCachedError = TruncNullable(outcome.CachedErrorMessage, 200);
                break;
            case "pdfium":
                entry.pdfiumCacheHit = outcome.CacheHit;
                entry.pdfiumCachedRenderMs = outcome.CachedRenderMs;
                entry.pdfiumCachedStatus = outcome.CachedStatus;
                entry.pdfiumCachedError = TruncNullable(outcome.CachedErrorMessage, 200);
                break;
        }
    }

    private static DirectoryInfo ResolveCorpusOracleCacheDir(DirectoryInfo? explicitDir)
    {
        if (explicitDir != null)
            return explicitDir;

        var envDir = Environment.GetEnvironmentVariable("PDFE_ORACLE_CACHE_DIR");
        return !string.IsNullOrWhiteSpace(envDir)
            ? new DirectoryInfo(envDir)
            : new DirectoryInfo(Path.Combine(Path.GetTempPath(), "pdfe-oracle-cache-v1"));
    }

    private static OracleRenderCache? CreateOracleRenderCache(DirectoryInfo? cacheDir)
    {
        if (cacheDir == null)
            return null;

        try
        {
            return new OracleRenderCache(cacheDir.FullName);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"warning: oracle cache disabled: {ex.Message}");
            return null;
        }
    }

    private static PdfeRenderCache? CreatePdfeRenderCache(DirectoryInfo? cacheDir)
    {
        if (cacheDir == null)
            return null;

        try
        {
            return new PdfeRenderCache(cacheDir.FullName);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"warning: pdfe render cache disabled: {ex.Message}");
            return null;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private static string HashCacheText(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private sealed class PdfeRenderCache
    {
        private const string CacheVersion = "pdfe-v1";
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> _locks = new(StringComparer.Ordinal);
        private readonly string _rendererIdentity;
        private long _hits;
        private long _misses;
        private long _writes;
        private long _errors;

        public PdfeRenderCache(string cacheDirectory)
        {
            CacheDirectory = Path.GetFullPath(cacheDirectory);
            Directory.CreateDirectory(CacheDirectory);
            _rendererIdentity = BuildRendererIdentity();
        }

        public string CacheDirectory { get; }

        public PdfeRenderOutcome GetOrRender(
            string pdfPath,
            int pageNumber,
            int dpi,
            string? userPassword,
            Func<PdfeRenderResult> render)
        {
            var cachePath = GetCachePath(pdfPath, pageNumber, dpi, userPassword);
            var gate = _locks.GetOrAdd(cachePath, _ => new object());

            lock (gate)
            {
                if (TryDecode(cachePath, out var cachedBitmap, out var elapsedMs, out var metadata))
                {
                    System.Threading.Interlocked.Increment(ref _hits);
                    return new PdfeRenderOutcome(
                        new PdfeRenderResult(
                            cachedBitmap,
                            metadata?.status ?? "OK",
                            metadata?.errorMessage,
                            elapsedMs,
                            metadata?.diagnostics ?? Array.Empty<string>()),
                        CacheEnabled: true,
                        CacheHit: true,
                        CachedRenderMs: metadata?.elapsedMs,
                        CachedStatus: metadata?.status,
                        CachedErrorMessage: metadata?.errorMessage);
                }

                System.Threading.Interlocked.Increment(ref _misses);
                var result = render();
                if (result is { Status: "OK", Bitmap: not null })
                    TryWrite(cachePath, pageNumber, dpi, result);
                return new PdfeRenderOutcome(result, CacheEnabled: true, CacheHit: false,
                    CachedRenderMs: null, CachedStatus: null, CachedErrorMessage: null);
            }
        }

        public CorpusRenderCacheReport CreateReport() => new()
        {
            enabled = true,
            directory = CacheDirectory,
            hits = System.Threading.Interlocked.Read(ref _hits),
            misses = System.Threading.Interlocked.Read(ref _misses),
            writes = System.Threading.Interlocked.Read(ref _writes),
            errors = System.Threading.Interlocked.Read(ref _errors),
        };

        private string GetCachePath(
            string pdfPath,
            int pageNumber,
            int dpi,
            string? userPassword)
        {
            var fullPath = Path.GetFullPath(pdfPath);
            var info = new FileInfo(fullPath);
            var material = string.Join('\n',
                CacheVersion,
                _rendererIdentity,
                fullPath,
                info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                info.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
                pageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                dpi.ToString(System.Globalization.CultureInfo.InvariantCulture),
                userPassword == null ? "<none>" : HashCacheText(userPassword));
            var key = HashCacheText(material);
            return Path.Combine(CacheDirectory, key[..2], key + ".png");
        }

        private bool TryDecode(
            string cachePath,
            out SKBitmap? bitmap,
            out long elapsedMs,
            out PdfeRenderCacheMetadata? metadata)
        {
            var sw = Stopwatch.StartNew();
            bitmap = null;
            elapsedMs = 0;
            metadata = null;
            if (!File.Exists(cachePath))
                return false;

            try
            {
                bitmap = SKBitmap.Decode(cachePath);
                sw.Stop();
                elapsedMs = sw.ElapsedMilliseconds;
                if (bitmap != null)
                {
                    metadata = TryReadMetadata(GetMetadataPath(cachePath));
                    return true;
                }

                TryDeleteFile(cachePath);
                TryDeleteFile(GetMetadataPath(cachePath));
                System.Threading.Interlocked.Increment(ref _errors);
                return false;
            }
            catch
            {
                sw.Stop();
                elapsedMs = sw.ElapsedMilliseconds;
                TryDeleteFile(cachePath);
                TryDeleteFile(GetMetadataPath(cachePath));
                System.Threading.Interlocked.Increment(ref _errors);
                return false;
            }
        }

        private void TryWrite(
            string cachePath,
            int pageNumber,
            int dpi,
            PdfeRenderResult result)
        {
            var directory = Path.GetDirectoryName(cachePath);
            if (directory == null || result.Bitmap == null)
                return;

            var tempPath = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var metadataPath = GetMetadataPath(cachePath);
            var tempMetadataPath = metadataPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(directory);
                using var image = SKImage.FromBitmap(result.Bitmap);
                using var data = image.Encode(SKEncodedImageFormat.Png, quality: 100);
                if (data == null)
                    return;

                using (var stream = File.Create(tempPath))
                    data.SaveTo(stream);

                var metadata = new PdfeRenderCacheMetadata
                {
                    cacheVersion = CacheVersion,
                    rendererIdentity = _rendererIdentity,
                    pageNumber = pageNumber,
                    dpi = dpi,
                    status = result.Status,
                    errorMessage = result.ErrorMessage,
                    elapsedMs = result.ElapsedMs,
                    diagnostics = result.Diagnostics,
                    createdUtc = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                };
                var metadataJson = System.Text.Json.JsonSerializer.Serialize(metadata);
                File.WriteAllText(tempMetadataPath, metadataJson, Encoding.UTF8);

                File.Move(tempPath, cachePath, overwrite: true);
                File.Move(tempMetadataPath, metadataPath, overwrite: true);
                System.Threading.Interlocked.Increment(ref _writes);
            }
            catch
            {
                System.Threading.Interlocked.Increment(ref _errors);
            }
            finally
            {
                TryDeleteFile(tempPath);
                TryDeleteFile(tempMetadataPath);
            }
        }

        private PdfeRenderCacheMetadata? TryReadMetadata(string metadataPath)
        {
            if (!File.Exists(metadataPath))
                return null;

            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<PdfeRenderCacheMetadata>(
                    File.ReadAllText(metadataPath, Encoding.UTF8));
            }
            catch
            {
                System.Threading.Interlocked.Increment(ref _errors);
                return null;
            }
        }

        private static string BuildRendererIdentity()
        {
            static string DescribeAssembly(System.Reflection.Assembly assembly)
            {
                var location = assembly.Location;
                if (string.IsNullOrWhiteSpace(location) || !File.Exists(location))
                    return assembly.FullName ?? assembly.GetName().Name ?? "unknown";

                var info = new FileInfo(location);
                return string.Join('|',
                    Path.GetFullPath(location),
                    info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    info.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            return string.Join('\n',
                DescribeAssembly(typeof(Program).Assembly),
                DescribeAssembly(typeof(SkiaRenderer).Assembly),
                DescribeAssembly(typeof(PdfDocument).Assembly),
                DescribeAssembly(typeof(SKBitmap).Assembly));
        }

        private static string GetMetadataPath(string cachePath) => cachePath + ".json";
    }

    private sealed class OracleRenderCache
    {
        private const string CacheVersion = "v1";
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> _locks = new(StringComparer.Ordinal);
        private long _hits;
        private long _misses;
        private long _writes;
        private long _errors;

        public OracleRenderCache(string cacheDirectory)
        {
            CacheDirectory = Path.GetFullPath(cacheDirectory);
            Directory.CreateDirectory(CacheDirectory);
        }

        public string CacheDirectory { get; }

        public OracleRenderOutcome GetOrRender(
            string oracleName,
            string pdfPath,
            int pageNumber,
            int dpi,
            string? userPassword,
            Func<ReferenceRenderResult> render)
        {
            var cachePath = GetCachePath(oracleName, pdfPath, pageNumber, dpi, userPassword);
            var gate = _locks.GetOrAdd(cachePath, _ => new object());

            lock (gate)
            {
                if (TryDecode(cachePath, out var cachedBitmap, out var elapsedMs, out var metadata))
                {
                    System.Threading.Interlocked.Increment(ref _hits);
                    return new OracleRenderOutcome(
                        new ReferenceRenderResult(cachedBitmap, "OK", null, elapsedMs),
                        CacheEnabled: true,
                        CacheHit: true,
                        CachedRenderMs: metadata?.elapsedMs,
                        CachedStatus: metadata?.status,
                        CachedErrorMessage: metadata?.errorMessage);
                }

                System.Threading.Interlocked.Increment(ref _misses);
                var result = render();
                if (result is { Status: "OK", Bitmap: not null })
                    TryWrite(cachePath, oracleName, pageNumber, dpi, result);
                return new OracleRenderOutcome(result, CacheEnabled: true, CacheHit: false,
                    CachedRenderMs: null, CachedStatus: null, CachedErrorMessage: null);
            }
        }

        public CorpusOracleCacheReport CreateReport() => new()
        {
            enabled = true,
            directory = CacheDirectory,
            hits = System.Threading.Interlocked.Read(ref _hits),
            misses = System.Threading.Interlocked.Read(ref _misses),
            writes = System.Threading.Interlocked.Read(ref _writes),
            errors = System.Threading.Interlocked.Read(ref _errors),
        };

        private string GetCachePath(
            string oracleName,
            string pdfPath,
            int pageNumber,
            int dpi,
            string? userPassword)
        {
            var fullPath = Path.GetFullPath(pdfPath);
            var info = new FileInfo(fullPath);
            var material = string.Join('\n',
                CacheVersion,
                oracleName,
                fullPath,
                info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                info.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
                pageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                dpi.ToString(System.Globalization.CultureInfo.InvariantCulture),
                userPassword == null ? "<none>" : HashText(userPassword));
            var key = HashText(material);
            return Path.Combine(CacheDirectory, key[..2], key + ".png");
        }

        private bool TryDecode(
            string cachePath,
            out SKBitmap? bitmap,
            out long elapsedMs,
            out OracleCacheMetadata? metadata)
        {
            var sw = Stopwatch.StartNew();
            bitmap = null;
            elapsedMs = 0;
            metadata = null;
            if (!File.Exists(cachePath))
                return false;

            try
            {
                bitmap = SKBitmap.Decode(cachePath);
                sw.Stop();
                elapsedMs = sw.ElapsedMilliseconds;
                if (bitmap != null)
                {
                    metadata = TryReadMetadata(GetMetadataPath(cachePath));
                    return true;
                }

                TryDelete(cachePath);
                TryDelete(GetMetadataPath(cachePath));
                System.Threading.Interlocked.Increment(ref _errors);
                return false;
            }
            catch
            {
                sw.Stop();
                elapsedMs = sw.ElapsedMilliseconds;
                TryDelete(cachePath);
                TryDelete(GetMetadataPath(cachePath));
                System.Threading.Interlocked.Increment(ref _errors);
                return false;
            }
        }

        private void TryWrite(
            string cachePath,
            string oracleName,
            int pageNumber,
            int dpi,
            ReferenceRenderResult result)
        {
            var directory = Path.GetDirectoryName(cachePath);
            if (directory == null || result.Bitmap == null)
                return;

            var tempPath = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var metadataPath = GetMetadataPath(cachePath);
            var tempMetadataPath = metadataPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(directory);
                using var image = SKImage.FromBitmap(result.Bitmap);
                using var data = image.Encode(SKEncodedImageFormat.Png, quality: 100);
                if (data == null)
                    return;

                using (var stream = File.Create(tempPath))
                    data.SaveTo(stream);

                var metadata = new OracleCacheMetadata
                {
                    cacheVersion = CacheVersion,
                    oracleName = oracleName,
                    pageNumber = pageNumber,
                    dpi = dpi,
                    status = result.Status,
                    errorMessage = result.ErrorMessage,
                    elapsedMs = result.ElapsedMs,
                    createdUtc = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                };
                var metadataJson = System.Text.Json.JsonSerializer.Serialize(metadata);
                File.WriteAllText(tempMetadataPath, metadataJson, Encoding.UTF8);

                File.Move(tempPath, cachePath, overwrite: true);
                File.Move(tempMetadataPath, metadataPath, overwrite: true);
                System.Threading.Interlocked.Increment(ref _writes);
            }
            catch
            {
                System.Threading.Interlocked.Increment(ref _errors);
            }
            finally
            {
                TryDelete(tempPath);
                TryDelete(tempMetadataPath);
            }
        }

        private OracleCacheMetadata? TryReadMetadata(string metadataPath)
        {
            if (!File.Exists(metadataPath))
                return null;

            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<OracleCacheMetadata>(
                    File.ReadAllText(metadataPath, Encoding.UTF8));
            }
            catch
            {
                System.Threading.Interlocked.Increment(ref _errors);
                return null;
            }
        }

        private static string GetMetadataPath(string cachePath) => cachePath + ".json";

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }

        private static string HashText(string text)
            => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private sealed record OracleRenderOutcome(
        ReferenceRenderResult Result,
        bool CacheEnabled,
        bool CacheHit,
        long? CachedRenderMs,
        string? CachedStatus,
        string? CachedErrorMessage);

    private sealed record PdfeRenderResult(
        SKBitmap? Bitmap,
        string Status,
        string? ErrorMessage,
        long ElapsedMs,
        IReadOnlyList<string> Diagnostics);

    private sealed record PdfeRenderOutcome(
        PdfeRenderResult Result,
        bool CacheEnabled,
        bool CacheHit,
        long? CachedRenderMs,
        string? CachedStatus,
        string? CachedErrorMessage);

    private sealed class OracleCacheMetadata
    {
        public string cacheVersion { get; set; } = "";
        public string oracleName { get; set; } = "";
        public int pageNumber { get; set; }
        public int dpi { get; set; }
        public string status { get; set; } = "";
        public string? errorMessage { get; set; }
        public long elapsedMs { get; set; }
        public string createdUtc { get; set; } = "";
    }

    private sealed class PdfeRenderCacheMetadata
    {
        public string cacheVersion { get; set; } = "";
        public string rendererIdentity { get; set; } = "";
        public int pageNumber { get; set; }
        public int dpi { get; set; }
        public string status { get; set; } = "";
        public string? errorMessage { get; set; }
        public long elapsedMs { get; set; }
        public IReadOnlyList<string> diagnostics { get; set; } = Array.Empty<string>();
        public string createdUtc { get; set; } = "";
    }

    internal sealed class CorpusOracleCacheReport
    {
        public static CorpusOracleCacheReport CreateDisabled() => new();

        public bool enabled { get; set; }
        public string? directory { get; set; }
        public long hits { get; set; }
        public long misses { get; set; }
        public long writes { get; set; }
        public long errors { get; set; }
    }

    internal sealed class CorpusRenderCacheReport
    {
        public static CorpusRenderCacheReport CreateDisabled() => new();

        public bool enabled { get; set; }
        public string? directory { get; set; }
        public long hits { get; set; }
        public long misses { get; set; }
        public long writes { get; set; }
        public long errors { get; set; }
    }

    internal sealed class CorpusScanSummary
    {
        public Dictionary<string, int> statusCounts { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> resultStatusCounts { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> resultCategoryCounts { get; set; } = new(StringComparer.Ordinal);
        public int nonPassCount { get; set; }
        public int resultNonPassCount { get; set; }
        public int expectedPassCount { get; set; }
        public int expectedFailCount { get; set; }
        public int trueDiffCount { get; set; }
        public int passOneCount { get; set; }
        public Dictionary<string, int> nonPassVisualHumanImpactCounts { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> nonPassVisualCategoryCounts { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> oracleDisagreementBuckets { get; set; } = new(StringComparer.Ordinal);
        public IReadOnlyList<CorpusScanPriorityEntry> topNonPass { get; set; } = Array.Empty<CorpusScanPriorityEntry>();
    }

    internal sealed class CorpusScanPriorityEntry
    {
        public string path { get; set; } = "";
        public int pageNumber { get; set; }
        public string status { get; set; } = "";
        public string? visualHumanImpact { get; set; }
        public string? visualCategory { get; set; }
        public string? bestOracle { get; set; }
        public double diffFraction { get; set; }
        public double mae { get; set; }
        public int? oracleComparisonPairs { get; set; }
        public int? oracleDisagreeingPairs { get; set; }
        public string oracleDisagreementBucket { get; set; } = "unmeasured";
        public double? oracleMeanMae { get; set; }
        public bool? referenceCenterAgreement { get; set; }
        public double? pdfeReferenceCenterScore { get; set; }
        public double? oracleMeanCenterScore { get; set; }
        public int? pdfeReferenceCenterRank { get; set; }
    }

    internal enum CorpusPageMode
    {
        First,
        Sample,
        All,
    }

    [Flags]
    internal enum CorpusExtraOracles
    {
        None = 0,
        Ghostscript = 1,
        PdfBox = 2,
        Pdfium = 4,
        All = Ghostscript | PdfBox | Pdfium,
    }

    internal static int ComputeCorpusScanWallBudgetMs(
        int oracleTimeoutMs,
        CorpusPageMode pageMode,
        CorpusExtraOracles extraOracles,
        IReadOnlySet<int>? selectedPages = null)
    {
        var timeout = Math.Max(1_000, oracleTimeoutMs);
        var pages = EstimateCorpusScanPageCount(pageMode, selectedPages);

        // Each page always attempts pdfe, mutool, and pdftocairo. If the
        // primary references do not both pass, it can also run every enabled
        // escalation oracle. The outer budget is only a stuck-task backstop;
        // individual reference processes have their own stricter timeout and
        // should be allowed to return structured per-oracle TIMEOUT statuses.
        var renderSlotsPerPage = 3 + CountExtraOracles(extraOracles);
        var slackSlots = 2;
        var totalSlots = ((long)pages * renderSlotsPerPage) + slackSlots;
        var budget = totalSlots * timeout;
        return (int)Math.Clamp(budget, timeout * 2L, int.MaxValue);
    }

    private static int EstimateCorpusScanPageCount(
        CorpusPageMode pageMode,
        IReadOnlySet<int>? selectedPages)
    {
        if (selectedPages is not null)
        {
            var count = selectedPages.Count(page => page > 0);
            return Math.Max(1, count);
        }

        return pageMode switch
        {
            CorpusPageMode.Sample => 4,
            CorpusPageMode.All => 64,
            _ => 1,
        };
    }

    private static int CountExtraOracles(CorpusExtraOracles extraOracles)
    {
        var count = 0;
        if (extraOracles.HasFlag(CorpusExtraOracles.Ghostscript)) count++;
        if (extraOracles.HasFlag(CorpusExtraOracles.PdfBox)) count++;
        if (extraOracles.HasFlag(CorpusExtraOracles.Pdfium)) count++;
        return count;
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

    internal static bool TryParseCorpusExtraOracles(
        string value,
        out CorpusExtraOracles extraOracles,
        out string error)
    {
        extraOracles = CorpusExtraOracles.None;
        error = "";

        if (string.IsNullOrWhiteSpace(value))
            return true;

        foreach (var part in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "none":
                case "off":
                case "false":
                    extraOracles = CorpusExtraOracles.None;
                    break;
                case "ghostscript":
                case "ghostpdf":
                case "gs":
                    extraOracles |= CorpusExtraOracles.Ghostscript;
                    break;
                case "pdfbox":
                    extraOracles |= CorpusExtraOracles.PdfBox;
                    break;
                case "pdfium":
                case "pdfium_test":
                    extraOracles |= CorpusExtraOracles.Pdfium;
                    break;
                case "all":
                case "expanded":
                    extraOracles |= CorpusExtraOracles.All;
                    break;
                default:
                    error = $"Bad --extra-oracles '{value}'. Use none, ghostscript, pdfbox, pdfium, or all.";
                    return false;
            }
        }

        return true;
    }

    private static string ExtraOraclesName(CorpusExtraOracles extraOracles)
    {
        if (extraOracles == CorpusExtraOracles.None)
            return "none";
        if (extraOracles == CorpusExtraOracles.All)
            return "all";

        var names = new List<string>();
        if (extraOracles.HasFlag(CorpusExtraOracles.Ghostscript)) names.Add("ghostscript");
        if (extraOracles.HasFlag(CorpusExtraOracles.PdfBox)) names.Add("pdfbox");
        if (extraOracles.HasFlag(CorpusExtraOracles.Pdfium)) names.Add("pdfium");
        return string.Join(",", names);
    }

    internal static IReadOnlyDictionary<string, IReadOnlySet<int>>? LoadCorpusPageManifest(FileInfo? manifestFile)
    {
        if (manifestFile is null)
            return null;

        if (!manifestFile.Exists)
            throw new FileNotFoundException("Page manifest not found.", manifestFile.FullName);

        var pagesByPath = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        var lineNumber = 0;
        foreach (var rawLine in File.ReadLines(manifestFile.FullName))
        {
            lineNumber++;
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var columns = rawLine.Split('\t');
            if (columns.Length < 2)
                throw new InvalidDataException($"{manifestFile.FullName}:{lineNumber}: expected path<TAB>pageNumber");

            if (lineNumber == 1 && columns[0].Equals("path", StringComparison.OrdinalIgnoreCase))
                continue;

            var path = NormalizeManifestPath(columns[0].Trim());
            if (path.Length == 0)
                throw new InvalidDataException($"{manifestFile.FullName}:{lineNumber}: path is empty");

            if (!int.TryParse(columns[1].Trim(), out var pageNumber) || pageNumber < 0)
                throw new InvalidDataException($"{manifestFile.FullName}:{lineNumber}: pageNumber must be a non-negative integer");

            if (!pagesByPath.TryGetValue(path, out var pages))
            {
                pages = new HashSet<int>();
                pagesByPath[path] = pages;
            }
            pages.Add(pageNumber);
        }

        return pagesByPath.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlySet<int>)kvp.Value,
            StringComparer.Ordinal);
    }

    internal static IReadOnlyDictionary<string, string>? LoadCorpusPasswordManifest(FileInfo? manifestFile)
    {
        if (manifestFile is null)
            return null;

        if (!manifestFile.Exists)
            throw new FileNotFoundException("Password manifest not found.", manifestFile.FullName);

        var passwordsByPath = new Dictionary<string, string>(StringComparer.Ordinal);
        var lineNumber = 0;
        foreach (var rawLine in File.ReadLines(manifestFile.FullName))
        {
            lineNumber++;
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var columns = rawLine.Split('\t');
            if (columns.Length < 2)
                throw new InvalidDataException($"{manifestFile.FullName}:{lineNumber}: expected path<TAB>userPassword");

            if (lineNumber == 1 && columns[0].Equals("path", StringComparison.OrdinalIgnoreCase))
                continue;

            var path = NormalizeManifestPath(columns[0].Trim());
            if (path.Length == 0)
                throw new InvalidDataException($"{manifestFile.FullName}:{lineNumber}: path is empty");

            passwordsByPath[path] = columns[1];
        }

        return passwordsByPath;
    }

    internal static IReadOnlyDictionary<CorpusPageKey, CorpusExpectedOutcome>? LoadCorpusExpectationManifest(FileInfo? manifestFile)
    {
        if (manifestFile is null)
            return null;

        if (!manifestFile.Exists)
            throw new FileNotFoundException("Expectation manifest not found.", manifestFile.FullName);

        var expectations = new Dictionary<CorpusPageKey, CorpusExpectedOutcome>();
        var lineNumber = 0;
        foreach (var rawLine in File.ReadLines(manifestFile.FullName))
        {
            lineNumber++;
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var columns = rawLine.Split('\t');
            if (columns.Length < 3)
                throw new InvalidDataException($"{manifestFile.FullName}:{lineNumber}: expected path<TAB>pageNumber<TAB>expectedStatus");

            if (lineNumber == 1 && columns[0].Equals("path", StringComparison.OrdinalIgnoreCase))
                continue;

            var path = NormalizeManifestPath(columns[0].Trim());
            if (path.Length == 0)
                throw new InvalidDataException($"{manifestFile.FullName}:{lineNumber}: path is empty");

            if (!int.TryParse(columns[1].Trim(), out var pageNumber) || pageNumber < 0)
                throw new InvalidDataException($"{manifestFile.FullName}:{lineNumber}: pageNumber must be a non-negative integer");

            var expectedStatus = columns[2].Trim();
            if (expectedStatus.Length == 0)
                throw new InvalidDataException($"{manifestFile.FullName}:{lineNumber}: expectedStatus is empty");

            var expectedErrorContains = columns.Length > 3 ? columns[3].Trim() : string.Empty;
            var note = columns.Length > 4 ? columns[4].Trim() : string.Empty;
            var expectedResultStatus = columns.Length > 5 ? columns[5].Trim() : string.Empty;
            var expectedResultCategory = columns.Length > 6 ? columns[6].Trim() : string.Empty;
            var expectedResultReason = columns.Length > 7 ? columns[7].Trim() : string.Empty;
            expectations[new CorpusPageKey(path, pageNumber)] = new CorpusExpectedOutcome(
                expectedStatus,
                expectedErrorContains,
                note,
                expectedResultStatus,
                expectedResultCategory,
                expectedResultReason);
        }

        return expectations;
    }

    private static string NormalizeManifestPath(string path)
    {
        return path
            .Trim()
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
    }

    internal static IEnumerable<int> SelectCorpusPages(
        int pageCount,
        CorpusPageMode pageMode,
        IReadOnlySet<int>? selectedPages = null)
    {
        if (pageCount <= 0)
            yield break;

        if (selectedPages is not null)
        {
            var emitted = false;
            foreach (var page in selectedPages.Where(page => page > 0 && page <= pageCount).OrderBy(page => page))
            {
                emitted = true;
                yield return page;
            }

            // Page 0 records come from open-time failures or page-count probe
            // failures. If an all-page run can open such a file, render every
            // page so the coverage manifest stays complete; narrower modes still
            // render page 1 to advance focused parser-fix subsets.
            if (!emitted && selectedPages.Contains(0))
            {
                if (pageMode == CorpusPageMode.All)
                {
                    for (var page = 1; page <= pageCount; page++)
                        yield return page;
                }
                else
                {
                    yield return 1;
                }
            }

            yield break;
        }

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

    private static bool TryComputeResourceSafeDpi(PdfPage page, int requestedDpi, out int dpi)
    {
        dpi = requestedDpi;
        if (requestedDpi <= 1)
            return false;

        var rotation = ((page.Rotation % 360) + 360) % 360;
        var quarterTurn = rotation is 90 or 270;
        var widthPoints = Math.Max(1.0, quarterTurn ? page.Height : page.Width);
        var heightPoints = Math.Max(1.0, quarterTurn ? page.Width : page.Height);
        var pageAreaPoints = widthPoints * heightPoints;
        if (!double.IsFinite(pageAreaPoints) || pageAreaPoints <= 0)
            return false;

        var maxDpi = (int)Math.Floor(72.0 * Math.Sqrt(CorpusFallbackMaxPixelCount / pageAreaPoints));
        dpi = Math.Clamp(maxDpi, 1, requestedDpi - 1);
        return dpi < requestedDpi;
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

    internal static void ApplyOracleDisagreementMetrics(
        CorpusScanEntry entry,
        IReadOnlyList<(string Name, SkiaSharp.SKBitmap? Bitmap)> oracles,
        double maxDiffFraction,
        double maxMae)
    {
        var rendered = oracles
            .Where(o => o.Bitmap != null)
            .Select(o => (o.Name, Bitmap: o.Bitmap!))
            .ToArray();
        if (rendered.Length < 2)
            return;

        var pairs = 0;
        var disagreeingPairs = 0;
        double sumDiff = 0;
        double sumMae = 0;
        double maxDiff = 0;
        double maxPairMae = 0;

        for (var i = 0; i < rendered.Length; i++)
        {
            for (var j = i + 1; j < rendered.Length; j++)
            {
                var (diff, mae) = MatchAndCompare(rendered[i].Bitmap, rendered[j].Bitmap);
                pairs++;
                sumDiff += diff;
                sumMae += mae;
                maxDiff = Math.Max(maxDiff, diff);
                maxPairMae = Math.Max(maxPairMae, mae);
                if (!IsPassing((diff, mae), maxDiffFraction, maxMae))
                    disagreeingPairs++;
            }
        }

        entry.oracleComparisonPairs = pairs;
        entry.oracleDisagreeingPairs = disagreeingPairs;
        entry.oracleMaxDiffFraction = maxDiff;
        entry.oracleMaxMae = maxPairMae;
        entry.oracleMeanDiffFraction = sumDiff / pairs;
        entry.oracleMeanMae = sumMae / pairs;
    }

    internal static void ApplyReferenceCenterMetrics(
        CorpusScanEntry entry,
        IReadOnlyList<(string Name, SkiaSharp.SKBitmap? Bitmap)> renderers,
        double maxDiffFraction,
        double maxMae)
    {
        var rendered = renderers
            .Where(item => item.Bitmap != null)
            .Select(item => (item.Name, Bitmap: item.Bitmap!))
            .ToArray();
        if (rendered.Length < 3 || rendered.All(item => item.Name != "pdfe"))
            return;

        var pairs = new List<CorpusVisualVectorPair>();
        for (var i = 0; i < rendered.Length; i++)
        {
            for (var j = i + 1; j < rendered.Length; j++)
            {
                var metrics = MatchAndCompareVisualVector(rendered[i].Bitmap, rendered[j].Bitmap);
                pairs.Add(new CorpusVisualVectorPair
                {
                    a = rendered[i].Name,
                    b = rendered[j].Name,
                    diffFraction = metrics.diffFraction,
                    mae = metrics.mae,
                    rmse = metrics.rmse,
                    maxChannelDelta = metrics.maxChannelDelta,
                    meanDiffLuminance = metrics.meanDiffLuminance,
                    meanDiffChroma = metrics.meanDiffChroma,
                    darkPixelBalance = metrics.darkPixelBalance,
                    diffBoundsAreaFraction = metrics.diffBoundsAreaFraction,
                    score = metrics.score,
                });
            }
        }

        if (pairs.Count == 0)
            return;

        entry.visualVectorPairs = pairs;

        var pdfeReferencePairs = pairs
            .Where(pair => pair.a == "pdfe" ^ pair.b == "pdfe")
            .ToArray();
        if (pdfeReferencePairs.Length == 0)
            return;

        entry.pdfeReferenceCenterScore = pdfeReferencePairs.Average(pair => pair.score);

        var oracleNames = rendered
            .Select(item => item.Name)
            .Where(name => name != "pdfe")
            .ToArray();
        var oracleScores = new List<CorpusVisualCenterScore>();
        foreach (var oracle in oracleNames)
        {
            var oraclePairs = pairs
                .Where(pair => pair.a != "pdfe" && pair.b != "pdfe")
                .Where(pair => pair.a == oracle || pair.b == oracle)
                .ToArray();
            if (oraclePairs.Length == 0)
                continue;

            oracleScores.Add(new CorpusVisualCenterScore
            {
                name = oracle,
                score = oraclePairs.Average(pair => pair.score),
            });
        }

        if (oracleScores.Count == 0)
            return;

        var sortedOracleScores = oracleScores
            .OrderBy(score => score.score)
            .ThenBy(score => score.name, StringComparer.Ordinal)
            .ToArray();
        entry.visualCenterScores = sortedOracleScores;
        entry.oracleMeanCenterScore = sortedOracleScores.Average(score => score.score);
        entry.oracleMinCenterScore = sortedOracleScores.Min(score => score.score);
        entry.oracleMaxCenterScore = sortedOracleScores.Max(score => score.score);
        entry.pdfeReferenceCenterRank =
            1 + sortedOracleScores.Count(score => score.score < entry.pdfeReferenceCenterScore);

        entry.referenceCenterAgreement = IsReferenceCenterAgreement(
            entry,
            maxDiffFraction,
            maxMae);
    }

    private static bool IsReferenceCenterAgreement(
        CorpusScanEntry entry,
        double maxDiffFraction,
        double maxMae)
    {
        if (entry.comparedOracles is { } compared && compared < 3)
            return false;

        if (entry.oracleComparisonPairs is not { } oraclePairs || oraclePairs < 3)
            return false;

        if (entry.oracleDisagreeingPairs is not { } disagreeingPairs || disagreeingPairs == 0)
            return false;

        if (entry.pdfeReferenceCenterScore is not { } pdfeCenter ||
            entry.oracleMeanCenterScore is not { } oracleMeanCenter ||
            entry.oracleMeanDiffFraction is not { } oracleMeanDiff ||
            entry.oracleMeanMae is not { } oracleMeanMae)
        {
            return false;
        }

        // Keep this as a tolerance for renderer splits, not a way to hide
        // missing or badly wrong content. The best direct comparison still
        // needs acceptable average color error and must be no worse than the
        // reference renderers' own average disagreement.
        if (entry.mae > maxMae || entry.mae > oracleMeanMae)
            return false;

        if (entry.diffFraction > Math.Max(maxDiffFraction, oracleMeanDiff))
            return false;

        return pdfeCenter <= oracleMeanCenter;
    }

    private static CorpusVisualVectorMetrics MatchAndCompareVisualVector(
        SkiaSharp.SKBitmap actual,
        SkiaSharp.SKBitmap reference)
    {
        SkiaSharp.SKBitmap probe = actual;
        SkiaSharp.SKBitmap referenceProbe = reference;
        var disposables = new List<SkiaSharp.SKBitmap>();
        if (actual.Width != reference.Width || actual.Height != reference.Height)
        {
            probe = Pdfe.Rendering.Differential.DifferentialMetrics
                .ResizeMatch(actual, reference.Width, reference.Height).Copy();
            disposables.Add(probe);
        }

        var pixels = (long)referenceProbe.Width * referenceProbe.Height;
        if (pixels > VisualVectorMaxPixelCount)
        {
            var scale = Math.Sqrt(VisualVectorMaxPixelCount / (double)pixels);
            var targetWidth = Math.Max(1, (int)Math.Round(referenceProbe.Width * scale));
            var targetHeight = Math.Max(1, (int)Math.Round(referenceProbe.Height * scale));
            var scaledProbe = Pdfe.Rendering.Differential.DifferentialMetrics
                .ResizeMatch(probe, targetWidth, targetHeight);
            var scaledReference = Pdfe.Rendering.Differential.DifferentialMetrics
                .ResizeMatch(referenceProbe, targetWidth, targetHeight);
            disposables.Add(scaledProbe);
            disposables.Add(scaledReference);
            probe = scaledProbe;
            referenceProbe = scaledReference;
        }

        try
        {
            return ComputeVisualVectorMetrics(probe, referenceProbe);
        }
        finally
        {
            foreach (var disposable in disposables)
                disposable.Dispose();
        }
    }

    private static CorpusVisualVectorMetrics ComputeVisualVectorMetrics(
        SkiaSharp.SKBitmap actual,
        SkiaSharp.SKBitmap reference)
    {
        if (actual.Width != reference.Width || actual.Height != reference.Height)
            throw new ArgumentException("Images must have matching dimensions.");

        var width = actual.Width;
        var height = actual.Height;
        var pixelCount = checked(width * height);
        long differingPixels = 0;
        long sumAbs = 0;
        double sumSquared = 0;
        int maxChannelDelta = 0;
        double diffLuminance = 0;
        double diffChroma = 0;
        long actualDarkPixels = 0;
        long referenceDarkPixels = 0;
        var left = width;
        var top = height;
        var right = -1;
        var bottom = -1;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var actualPixel = actual.GetPixel(x, y);
                var referencePixel = reference.GetPixel(x, y);
                if (IsVisualVectorDarkPixel(actualPixel))
                    actualDarkPixels++;
                if (IsVisualVectorDarkPixel(referencePixel))
                    referenceDarkPixels++;

                var dR = Math.Abs(actualPixel.Red - referencePixel.Red);
                var dG = Math.Abs(actualPixel.Green - referencePixel.Green);
                var dB = Math.Abs(actualPixel.Blue - referencePixel.Blue);
                var worst = Math.Max(dR, Math.Max(dG, dB));

                sumAbs += dR + dG + dB;
                sumSquared += dR * dR + dG * dG + dB * dB;
                maxChannelDelta = Math.Max(maxChannelDelta, worst);

                if (worst <= VisualDiffAnalyzer.DefaultTolerance)
                    continue;

                differingPixels++;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x + 1);
                bottom = Math.Max(bottom, y + 1);
                diffLuminance += Math.Abs(VisualVectorLuminance(actualPixel) - VisualVectorLuminance(referencePixel));
                diffChroma += VisualVectorChromaDistance(actualPixel, referencePixel);
            }
        }

        var diffFraction = (double)differingPixels / pixelCount;
        var mae = (double)sumAbs / (pixelCount * 3.0);
        var rmse = Math.Sqrt(sumSquared / (pixelCount * 3.0));
        var meanDiffLuminance = differingPixels == 0 ? 0 : diffLuminance / differingPixels;
        var meanDiffChroma = differingPixels == 0 ? 0 : diffChroma / differingPixels;
        var darkPixelBalance = ComputeVisualVectorBalance(actualDarkPixels, referenceDarkPixels);
        var boundsAreaFraction = differingPixels == 0
            ? 0
            : ((right - left) * (double)(bottom - top)) / pixelCount;

        var score =
            (0.25 * diffFraction)
            + (0.25 * Math.Clamp(mae / 255.0, 0, 1))
            + (0.15 * Math.Clamp(rmse / 255.0, 0, 1))
            + (0.15 * Math.Clamp(meanDiffLuminance / 255.0, 0, 1))
            + (0.10 * Math.Clamp(meanDiffChroma / 255.0, 0, 1))
            + (0.05 * (1.0 - darkPixelBalance))
            + (0.05 * Math.Clamp(boundsAreaFraction, 0, 1));

        return new CorpusVisualVectorMetrics(
            diffFraction,
            mae,
            rmse,
            maxChannelDelta,
            meanDiffLuminance,
            meanDiffChroma,
            darkPixelBalance,
            boundsAreaFraction,
            score);
    }

    private static double VisualVectorLuminance(SKColor color)
        => 0.299 * color.Red + 0.587 * color.Green + 0.114 * color.Blue;

    private static bool IsVisualVectorDarkPixel(SKColor color)
        => VisualVectorLuminance(color) < 200;

    private static double ComputeVisualVectorBalance(long a, long b)
    {
        var max = Math.Max(a, b);
        return max == 0 ? 1 : (double)Math.Min(a, b) / max;
    }

    private static double VisualVectorChromaDistance(SKColor a, SKColor b)
    {
        var cbA = -0.168736 * a.Red - 0.331264 * a.Green + 0.5 * a.Blue;
        var crA = 0.5 * a.Red - 0.418688 * a.Green - 0.081312 * a.Blue;
        var cbB = -0.168736 * b.Red - 0.331264 * b.Green + 0.5 * b.Blue;
        var crB = 0.5 * b.Red - 0.418688 * b.Green - 0.081312 * b.Blue;
        return (Math.Abs(cbA - cbB) + Math.Abs(crA - crB)) / 2.0;
    }

    private static bool IsPassing(
        (double diff, double mae)? metrics,
        double maxDiffFraction,
        double maxMae)
    {
        if (metrics is not { } value)
            return false;

        return value.diff <= maxDiffFraction && value.mae <= maxMae;
    }

    private static string Trunc(string s, int n) =>
        s.Length <= n ? s : s.Substring(0, n) + "…";

    private static string? TruncNullable(string? value, int length)
        => string.IsNullOrEmpty(value) ? value : Trunc(value, length);

    internal static string BuildOracleDiagnostic(CorpusScanEntry entry)
    {
        var parts = new List<string>
        {
            FormatOracleDiagnostic("mutool", entry.mutoolStatus, entry.mutoolError),
            FormatOracleDiagnostic("pdftocairo", entry.cairoStatus, entry.cairoError),
        };
        if (entry.ghostscriptStatus != null)
            parts.Add(FormatOracleDiagnostic("ghostscript", entry.ghostscriptStatus, entry.ghostscriptError));
        if (entry.pdfboxStatus != null)
            parts.Add(FormatOracleDiagnostic("pdfbox", entry.pdfboxStatus, entry.pdfboxError));
        if (entry.pdfiumStatus != null)
            parts.Add(FormatOracleDiagnostic("pdfium", entry.pdfiumStatus, entry.pdfiumError));
        return string.Join("; ", parts);
    }

    private static void ApplyRenderDiagnostics(CorpusScanEntry entry, IReadOnlyList<string> diagnostics)
    {
        if (diagnostics.Count == 0)
            return;

        entry.diagnostic = AppendDiagnostic(
            entry.diagnostic,
            "pdfe=" + string.Join("; ", diagnostics.Select(d => Trunc(d, 200))));
    }

    private static bool HasRecoveredMalformedContentDiagnostic(CorpusScanEntry entry)
        => entry.diagnostic?.Contains(
            ContentStreamReadWarning.ImageOnlyFilterInContentStreamCode,
            StringComparison.Ordinal) == true;

    internal static bool TryApplyRecoveredMalformedContentShortCircuit(
        CorpusScanEntry entry,
        Stopwatch pageStopwatch)
    {
        if (!HasRecoveredMalformedContentDiagnostic(entry))
            return false;

        pageStopwatch.Stop();
        entry.status = "RECOVERED_MALFORMED_CONTENT";
        entry.errorPhase = "render";
        entry.errorType = "RecoveredMalformedContent";
        entry.diagnostic = AppendDiagnostic(
            entry.diagnostic,
            "Skipped reference oracles because pdfe recovered malformed page content.");
        entry.elapsedMs = pageStopwatch.ElapsedMilliseconds;
        return true;
    }

    private static string AppendDiagnostic(string? existing, string detail)
    {
        if (string.IsNullOrWhiteSpace(existing))
            return detail;
        if (string.IsNullOrWhiteSpace(detail))
            return existing;
        return $"{existing}; {detail}";
    }

    private static string FormatOracleDiagnostic(string name, string? status, string? error)
        => $"{name}={status ?? "UNKNOWN"}" + FormatOracleError(error);

    private static string FormatOracleError(string? error)
        => string.IsNullOrWhiteSpace(error) ? "" : $" ({error})";

    internal enum CorpusFailurePhase
    {
        Open,
        Render,
    }

    private sealed class CorpusScanProgress
    {
        private readonly object _gate = new();
        private readonly string _path;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private string _phase = "queued";
        private string _detail = "waiting to start";
        private int _pageNumber;
        private DateTime _updatedUtc = DateTime.UtcNow;

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
                _updatedUtc = DateTime.UtcNow;
            }
        }

        public ProgressSnapshot Snapshot()
        {
            lock (_gate)
            {
                return new ProgressSnapshot(
                    _path,
                    _phase,
                    _pageNumber,
                    _detail,
                    _updatedUtc.ToString("o"),
                    _stopwatch.ElapsedMilliseconds);
            }
        }

        public readonly record struct ProgressSnapshot(
            string Path,
            string Phase,
            int PageNumber,
            string Detail,
            string UpdatedUtc,
            long ElapsedMs);
    }

    private static IDisposable? StartCorpusScanProgressReporter(
        int totalPdfs,
        System.Collections.Concurrent.ConcurrentBag<CorpusScanEntry> entries,
        System.Collections.Concurrent.ConcurrentDictionary<string, CorpusScanProgress> activeProgress,
        Func<int> getProcessedPdfs,
        Func<long> getPeakRssBytes,
        Action<long> observePeakRssBytes,
        Stopwatch scanStopwatch,
        int intervalSeconds,
        string? progressOutputPath,
        int chunkIndex,
        int chunkTotal,
        CorpusPageMode pageMode,
        CorpusExtraOracles extraOracles)
    {
        if (intervalSeconds <= 0)
            return null;

        var gate = new object();
        void Tick()
        {
            if (!System.Threading.Monitor.TryEnter(gate))
                return;
            try
            {
                WriteCorpusScanProgressSnapshot(
                    totalPdfs,
                    entries,
                    activeProgress,
                    getProcessedPdfs(),
                    getPeakRssBytes(),
                    observePeakRssBytes,
                    scanStopwatch,
                    progressOutputPath,
                    chunkIndex,
                    chunkTotal,
                    pageMode,
                    extraOracles);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"warning: corpus progress heartbeat failed: {ex.Message}");
            }
            finally
            {
                System.Threading.Monitor.Exit(gate);
            }
        }

        return new System.Threading.Timer(
            _ => Tick(),
            null,
            TimeSpan.FromSeconds(intervalSeconds),
            TimeSpan.FromSeconds(intervalSeconds));
    }

    private static void WriteCorpusScanProgressSnapshot(
        int totalPdfs,
        System.Collections.Concurrent.ConcurrentBag<CorpusScanEntry> entries,
        System.Collections.Concurrent.ConcurrentDictionary<string, CorpusScanProgress> activeProgress,
        int processedPdfs,
        long peakRssBytes,
        Action<long> observePeakRssBytes,
        Stopwatch scanStopwatch,
        string? progressOutputPath,
        int chunkIndex,
        int chunkTotal,
        CorpusPageMode pageMode,
        CorpusExtraOracles extraOracles)
    {
        var currentRss = Environment.WorkingSet;
        observePeakRssBytes(currentRss);
        if (currentRss > peakRssBytes)
            peakRssBytes = currentRss;

        var snapshots = activeProgress.Values
            .Select(progress => progress.Snapshot())
            .OrderBy(snapshot => snapshot.Path, StringComparer.Ordinal)
            .ToArray();
        var completedEntries = entries.ToArray();
        var statusCounts = CountBy(completedEntries.Select(entry => entry.status));
        var elapsed = scanStopwatch.Elapsed;
        var activeText = snapshots.Length == 0
            ? "none"
            : string.Join("; ", snapshots.Select(snapshot =>
                $"{snapshot.Path} p{snapshot.PageNumber} {snapshot.Phase} ({snapshot.ElapsedMs / 1000}s)"));

        Console.Out.WriteLine(
            $"progress {processedPdfs}/{totalPdfs} PDFs, {completedEntries.Length} page results, " +
            $"active: {activeText}, elapsed {elapsed:hh\\:mm\\:ss}, peak RSS {peakRssBytes / 1024 / 1024} MB");
        Console.Out.Flush();

        if (string.IsNullOrWhiteSpace(progressOutputPath))
            return;

        var report = new
        {
            generatedUtc = DateTime.UtcNow.ToString("o"),
            chunkIndex,
            chunkTotal,
            pageMode = PageModeName(pageMode),
            extraOracles = ExtraOraclesName(extraOracles),
            elapsedMs = scanStopwatch.ElapsedMilliseconds,
            totalPdfs,
            processedPdfs,
            activePdfs = snapshots.Length,
            pageResults = completedEntries.Length,
            peakRssBytes,
            statusCounts,
            active = snapshots.Select(snapshot => new
            {
                path = snapshot.Path,
                pageNumber = snapshot.PageNumber,
                phase = snapshot.Phase,
                detail = snapshot.Detail,
                updatedUtc = snapshot.UpdatedUtc,
                elapsedMs = snapshot.ElapsedMs,
            }).ToArray(),
        };
        WriteJsonAtomically(progressOutputPath, report);
    }

    private static void WriteJsonAtomically(string outputPath, object report)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var tempPath = outputPath + ".tmp";
        var json = System.Text.Json.JsonSerializer.Serialize(report,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, outputPath, overwrite: true);
    }

    private static void ObservePeakBytes(ref long peakBytes, long observedBytes)
    {
        while (true)
        {
            var current = System.Threading.Interlocked.Read(ref peakBytes);
            if (observedBytes <= current)
                return;

            if (System.Threading.Interlocked.CompareExchange(ref peakBytes, observedBytes, current) == current)
                return;
        }
    }

    internal static string ClassifyCorpusFailure(Exception ex, CorpusFailurePhase phase)
    {
        if (ex is Pdfe.Rendering.InvalidPageGeometryException)
            return "INVALID_PAGE_GEOMETRY";

        if (ex is Pdfe.Rendering.RenderResourceLimitException)
            return "RESOURCE_LIMIT";

        if (phase == CorpusFailurePhase.Open)
        {
            if (ex is PdfEncryptionNotSupportedException)
                return IsPasswordRequiredFailure(ex) ? "PASSWORD_REQUIRED" : "UNSUPPORTED_ENCRYPTED";

            if (ex is InvalidDataException && IsCompressionFailure(ex))
                return "UNSUPPORTED_COMPRESSION";

            if (ex is PdfParseException or InvalidDataException or OverflowException or FormatException)
                return "MALFORMED_PDF";

            return "PARSE_ERROR";
        }

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

    private static bool IsCompressionFailure(Exception ex)
    {
        for (Exception? current = ex; current != null; current = current.InnerException)
        {
            var message = current.Message;
            if (message.Contains("compression", StringComparison.OrdinalIgnoreCase)
                || message.Contains("compressed", StringComparison.OrdinalIgnoreCase)
                || message.Contains("deflate", StringComparison.OrdinalIgnoreCase)
                || message.Contains("zlib", StringComparison.OrdinalIgnoreCase)
                || message.Contains("unsupported filter", StringComparison.OrdinalIgnoreCase)
                || message.Contains("unsupported stream", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPasswordRequiredFailure(Exception ex)
    {
        for (Exception? current = ex; current != null; current = current.InnerException)
        {
            var message = current.Message;
            if (message.Contains("password verification failed", StringComparison.OrdinalIgnoreCase)
                || message.Contains("requires a non-empty user password", StringComparison.OrdinalIgnoreCase)
                || message.Contains("requiring a non-empty user password", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    internal static void ApplyCorpusExpectations(
        IReadOnlyList<CorpusScanEntry> entries,
        IReadOnlyDictionary<CorpusPageKey, CorpusExpectedOutcome>? expectations)
    {
        foreach (var entry in entries)
        {
            entry.resultStatus = IsPassingRawStatus(entry.status) ? "PASS" : entry.status;

            if (expectations is null ||
                !TryGetCorpusExpectation(expectations, entry, out var expectation))
            {
                continue;
            }

            entry.expectedStatus = expectation.ExpectedStatus;
            entry.expectedErrorContains = expectation.ExpectedErrorContains;
            entry.expectedNote = expectation.Note;
            entry.expectedResultStatus = expectation.ExpectedResultStatus;
            entry.expectedResultCategory = expectation.ExpectedResultCategory;
            entry.expectedResultReason = expectation.ExpectedResultReason;

            if (!ExpectedStatusMatches(entry.status, expectation.ExpectedStatus))
            {
                entry.expectationResult = "FAIL";
                entry.expectationFailure = $"Expected status {expectation.ExpectedStatus}, got {entry.status}.";
                continue;
            }

            if (!ExpectedErrorMatches(entry, expectation.ExpectedErrorContains))
            {
                entry.expectationResult = "FAIL";
                entry.expectationFailure = $"Expected error text containing '{expectation.ExpectedErrorContains}'.";
                continue;
            }

            entry.expectationResult = "PASS";
            entry.resultStatus = string.IsNullOrWhiteSpace(expectation.ExpectedResultStatus)
                ? "PASS"
                : expectation.ExpectedResultStatus;
            entry.resultCategory = string.IsNullOrWhiteSpace(expectation.ExpectedResultCategory)
                ? InferCorpusResultCategory(entry)
                : expectation.ExpectedResultCategory;
            entry.resultReason = string.IsNullOrWhiteSpace(expectation.ExpectedResultReason)
                ? (string.IsNullOrWhiteSpace(expectation.Note) ? null : expectation.Note)
                : expectation.ExpectedResultReason;
        }
    }

    private static bool ExpectedStatusMatches(string actualStatus, string expectedStatus)
        => string.Equals(expectedStatus, "*", StringComparison.Ordinal) ||
           string.Equals(actualStatus, expectedStatus, StringComparison.Ordinal);

    private static bool TryGetCorpusExpectation(
        IReadOnlyDictionary<CorpusPageKey, CorpusExpectedOutcome> expectations,
        CorpusScanEntry entry,
        out CorpusExpectedOutcome expectation)
    {
        if (expectations.TryGetValue(new CorpusPageKey(entry.path, entry.pageNumber), out expectation))
            return true;

        if (!entry.path.Contains('/', StringComparison.Ordinal) &&
            expectations.TryGetValue(new CorpusPageKey("pdfjs/" + entry.path, entry.pageNumber), out expectation))
        {
            return true;
        }

        const string pdfjsPrefix = "pdfjs/";
        if (entry.path.StartsWith(pdfjsPrefix, StringComparison.Ordinal) &&
            expectations.TryGetValue(new CorpusPageKey(entry.path[pdfjsPrefix.Length..], entry.pageNumber), out expectation))
        {
            return true;
        }

        expectation = default;
        return false;
    }

    internal static bool TryGetCorpusPassword(
        IReadOnlyDictionary<string, string> passwords,
        string path,
        out string password)
    {
        if (passwords.TryGetValue(path, out password!))
            return true;

        if (!path.Contains('/', StringComparison.Ordinal) &&
            passwords.TryGetValue("pdfjs/" + path, out password!))
        {
            return true;
        }

        const string pdfjsPrefix = "pdfjs/";
        if (path.StartsWith(pdfjsPrefix, StringComparison.Ordinal) &&
            passwords.TryGetValue(path[pdfjsPrefix.Length..], out password!))
        {
            return true;
        }

        password = string.Empty;
        return false;
    }

    private static bool IsPassingRawStatus(string status)
        => string.Equals(status, "PASS", StringComparison.Ordinal)
           || string.Equals(status, "PASS_ONE", StringComparison.Ordinal);

    private static bool ExpectedErrorMatches(CorpusScanEntry entry, string expectedErrorContains)
    {
        if (string.IsNullOrWhiteSpace(expectedErrorContains))
            return true;

        return (entry.errorMessage?.Contains(expectedErrorContains, StringComparison.Ordinal) ?? false)
               || (entry.diagnostic?.Contains(expectedErrorContains, StringComparison.Ordinal) ?? false);
    }

    private static string InferCorpusResultCategory(CorpusScanEntry entry)
    {
        if (string.Equals(entry.status, "PASS_ONE", StringComparison.Ordinal))
            return "PASS_ONE_SEMANTIC_OK";

        if (IsPassingRawStatus(entry.status))
            return "PASS";

        return "ACCEPTED_DEGENERATE_INPUT";
    }

    internal static CorpusScanSummary BuildCorpusScanSummary(IReadOnlyList<CorpusScanEntry> entries)
    {
        var nonPass = entries
            .Where(entry => !string.Equals(entry.status, "PASS", StringComparison.Ordinal))
            .ToArray();

        return new CorpusScanSummary
        {
            statusCounts = CountBy(entries.Select(entry => entry.status)),
            resultStatusCounts = CountBy(entries.Select(GetCorpusResultStatus)),
            resultCategoryCounts = CountBy(entries.Select(GetCorpusResultCategory)),
            nonPassCount = nonPass.Length,
            resultNonPassCount = entries.Count(entry => !string.Equals(GetCorpusResultStatus(entry), "PASS", StringComparison.Ordinal)),
            expectedPassCount = entries.Count(entry => string.Equals(entry.expectationResult, "PASS", StringComparison.Ordinal)),
            expectedFailCount = entries.Count(entry => string.Equals(entry.expectationResult, "FAIL", StringComparison.Ordinal)),
            trueDiffCount = nonPass.Count(entry => string.Equals(entry.status, "DIFF", StringComparison.Ordinal)),
            passOneCount = nonPass.Count(entry => string.Equals(entry.status, "PASS_ONE", StringComparison.Ordinal)),
            nonPassVisualHumanImpactCounts = CountBy(nonPass.Select(entry => NormalizeSummaryValue(entry.visualHumanImpact))),
            nonPassVisualCategoryCounts = CountBy(nonPass.Select(entry => NormalizeSummaryValue(entry.visualCategory))),
            oracleDisagreementBuckets = CountBy(entries.Select(GetOracleDisagreementBucket)),
            topNonPass = nonPass
                .OrderBy(GetCorpusScanPriorityRank)
                .ThenBy(entry => entry.path, StringComparer.Ordinal)
                .ThenBy(entry => entry.pageNumber)
                .Take(50)
                .Select(ToPriorityEntry)
                .ToArray(),
        };
    }

    private static Dictionary<string, int> CountBy(IEnumerable<string> values)
    {
        return values
            .GroupBy(value => value, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
    }

    private static string NormalizeSummaryValue(string? value)
        => string.IsNullOrWhiteSpace(value) ? "unclassified" : value;

    private static string GetCorpusResultStatus(CorpusScanEntry entry)
        => string.IsNullOrWhiteSpace(entry.resultStatus) || string.Equals(entry.resultStatus, "UNKNOWN", StringComparison.Ordinal)
            ? (IsPassingRawStatus(entry.status) ? "PASS" : entry.status)
            : entry.resultStatus;

    private static string GetCorpusResultCategory(CorpusScanEntry entry)
        => string.IsNullOrWhiteSpace(entry.resultCategory) ? "unclassified" : entry.resultCategory;

    private static string GetOracleDisagreementBucket(CorpusScanEntry entry)
    {
        if (entry.oracleComparisonPairs is not { } pairs || pairs <= 0
            || entry.oracleDisagreeingPairs is not { } disagreeing)
            return "unmeasured";

        if (disagreeing <= 0)
            return "none";

        return disagreeing >= pairs ? "all" : "some";
    }

    private static int GetCorpusScanPriorityRank(CorpusScanEntry entry)
    {
        var impactRank = entry.visualHumanImpact switch
        {
            "high" => 0,
            "medium" => 1,
            "low" => 2,
            "none" => 3,
            _ => StatusFallbackPriorityRank(entry.status),
        };

        var statusRank = entry.status switch
        {
            "DIFF" => 0,
            "TIMEOUT" => 1,
            "RESOURCE_LIMIT" => 1,
            "INVALID_PAGE_GEOMETRY" => 1,
            "MALFORMED_PDF" => 2,
            "DECODE_ERROR" => 2,
            "RENDER_ERROR" => 2,
            "PASS_ONE" => 3,
            _ => 4,
        };

        return impactRank * 10 + statusRank;
    }

    private static int StatusFallbackPriorityRank(string? status) => status switch
    {
        "TIMEOUT" => 0,
        "RESOURCE_LIMIT" => 0,
        "INVALID_PAGE_GEOMETRY" => 0,
        "MALFORMED_PDF" => 1,
        "DECODE_ERROR" => 1,
        "RENDER_ERROR" => 1,
        "PASS_ONE" => 3,
        _ => 2,
    };

    private static CorpusScanPriorityEntry ToPriorityEntry(CorpusScanEntry entry)
        => new()
        {
            path = entry.path,
            pageNumber = entry.pageNumber,
            status = entry.status,
            visualHumanImpact = entry.visualHumanImpact,
            visualCategory = entry.visualCategory,
            bestOracle = entry.bestOracle,
            diffFraction = entry.diffFraction,
            mae = entry.mae,
            oracleComparisonPairs = entry.oracleComparisonPairs,
            oracleDisagreeingPairs = entry.oracleDisagreeingPairs,
            oracleDisagreementBucket = GetOracleDisagreementBucket(entry),
            oracleMeanMae = entry.oracleMeanMae,
            referenceCenterAgreement = entry.referenceCenterAgreement,
            pdfeReferenceCenterScore = entry.pdfeReferenceCenterScore,
            oracleMeanCenterScore = entry.oracleMeanCenterScore,
            pdfeReferenceCenterRank = entry.pdfeReferenceCenterRank,
        };

    internal sealed class Jbig2ClassifyReport
    {
        public string generatedUtc { get; set; } = "";
        public string input { get; set; } = "";
        public int pdfs { get; set; }
        public int streams { get; set; }
        public Dictionary<string, int> counts { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> featureCounts { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> unsupportedFeatureCounts { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> segmentTypeCounts { get; set; } = new(StringComparer.Ordinal);
        public IReadOnlyList<Jbig2ClassifyEntry> entries { get; set; } = Array.Empty<Jbig2ClassifyEntry>();
    }

    internal sealed class Jbig2ClassifyEntry
    {
        public string path { get; set; } = "";
        public int pageNumber { get; set; }
        public string resourcePath { get; set; } = "";
        public int streamIndex { get; set; }
        public string status { get; set; } = "UNKNOWN";
        public int width { get; set; }
        public int height { get; set; }
        public IReadOnlyList<string> filters { get; set; } = Array.Empty<string>();
        public IReadOnlyList<string> features { get; set; } = Array.Empty<string>();
        public IReadOnlyList<string> unsupportedFeatures { get; set; } = Array.Empty<string>();
        public Dictionary<string, int> segmentTypeCounts { get; set; } = new(StringComparer.Ordinal);
        public IReadOnlyList<string> diagnostics { get; set; } = Array.Empty<string>();
        public IReadOnlyList<Jbig2CapabilitySegment> segments { get; set; } = Array.Empty<Jbig2CapabilitySegment>();
        public string? errorType { get; set; }
        public string? errorMessage { get; set; }
    }

    internal sealed class CorpusScanEntry
    {
        public string path { get; set; } = "";
        public int pageNumber { get; set; } = 1;
        public string status { get; set; } = "UNKNOWN";
        public string resultStatus { get; set; } = "UNKNOWN";
        public string? resultCategory { get; set; }
        public string? resultReason { get; set; }
        public string? releaseStatus { get; set; }
        public string? qualityStatus { get; set; }
        public string? pixelAgreement { get; set; }
        public string? referenceSituation { get; set; }
        public string? targetBasis { get; set; }
        public string? targetRenderer { get; set; }
        public string? rootCause { get; set; }
        public string? improvementPriority { get; set; }
        public string? confidence { get; set; }
        public string? passOneReviewStatus { get; set; }
        public string? trackedBy { get; set; }
        public string? qualityReason { get; set; }
        public string? contractStatus { get; set; }
        public string? expectedStatus { get; set; }
        public string? expectedErrorContains { get; set; }
        public string? expectedResultStatus { get; set; }
        public string? expectedResultCategory { get; set; }
        public string? expectedResultReason { get; set; }
        public string? expectationResult { get; set; }
        public string? expectationFailure { get; set; }
        public string? expectedNote { get; set; }
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
        public double? diffFractionGhostscript { get; set; }
        public double? maeGhostscript { get; set; }
        public double? diffFractionPdfBox { get; set; }
        public double? maePdfBox { get; set; }
        public double? diffFractionPdfium { get; set; }
        public double? maePdfium { get; set; }
        public string? bestOracle { get; set; }
        public string? visualCategory { get; set; }
        public string? visualHumanImpact { get; set; }
        public VisualDiffBounds? visualDiffBounds { get; set; }
        public IReadOnlyList<VisualDiffRegion>? visualTopRegions { get; set; }
        public int? comparedOracles { get; set; }
        public int? agreeingOracles { get; set; }
        public int? oracleComparisonPairs { get; set; }
        public int? oracleDisagreeingPairs { get; set; }
        public double? oracleMaxDiffFraction { get; set; }
        public double? oracleMaxMae { get; set; }
        public double? oracleMeanDiffFraction { get; set; }
        public double? oracleMeanMae { get; set; }
        public bool? referenceCenterAgreement { get; set; }
        public double? pdfeReferenceCenterScore { get; set; }
        public double? oracleMinCenterScore { get; set; }
        public double? oracleMeanCenterScore { get; set; }
        public double? oracleMaxCenterScore { get; set; }
        public int? pdfeReferenceCenterRank { get; set; }
        public IReadOnlyList<CorpusVisualVectorPair>? visualVectorPairs { get; set; }
        public IReadOnlyList<CorpusVisualCenterScore>? visualCenterScores { get; set; }
        public long? elapsedMs { get; set; }
        public long? pdfElapsedMs { get; set; }
        public long? renderMs { get; set; }
        public int? effectiveDpi { get; set; }
        public long? mutoolMs { get; set; }
        public long? cairoMs { get; set; }
        public long? ghostscriptMs { get; set; }
        public long? pdfboxMs { get; set; }
        public long? pdfiumMs { get; set; }
        public bool? pdfeCacheHit { get; set; }
        public bool? mutoolCacheHit { get; set; }
        public bool? cairoCacheHit { get; set; }
        public bool? ghostscriptCacheHit { get; set; }
        public bool? pdfboxCacheHit { get; set; }
        public bool? pdfiumCacheHit { get; set; }
        public long? pdfeCachedRenderMs { get; set; }
        public long? mutoolCachedRenderMs { get; set; }
        public long? cairoCachedRenderMs { get; set; }
        public long? ghostscriptCachedRenderMs { get; set; }
        public long? pdfboxCachedRenderMs { get; set; }
        public long? pdfiumCachedRenderMs { get; set; }
        public string? pdfeCachedStatus { get; set; }
        public string? mutoolCachedStatus { get; set; }
        public string? cairoCachedStatus { get; set; }
        public string? ghostscriptCachedStatus { get; set; }
        public string? pdfboxCachedStatus { get; set; }
        public string? pdfiumCachedStatus { get; set; }
        public string? pdfeCachedError { get; set; }
        public string? mutoolCachedError { get; set; }
        public string? cairoCachedError { get; set; }
        public string? ghostscriptCachedError { get; set; }
        public string? pdfboxCachedError { get; set; }
        public string? pdfiumCachedError { get; set; }
        public string? mutoolStatus { get; set; }
        public string? cairoStatus { get; set; }
        public string? ghostscriptStatus { get; set; }
        public string? pdfboxStatus { get; set; }
        public string? pdfiumStatus { get; set; }
        public string? mutoolError { get; set; }
        public string? cairoError { get; set; }
        public string? ghostscriptError { get; set; }
        public string? pdfboxError { get; set; }
        public string? pdfiumError { get; set; }
        public long? timeoutMs { get; set; }
        public string? diagnostic { get; set; }
        public string? errorPhase { get; set; }
        public string? errorType { get; set; }
        public string? errorMessage { get; set; }
    }

    internal sealed class CorpusVisualVectorPair
    {
        public string a { get; set; } = "";
        public string b { get; set; } = "";
        public double diffFraction { get; set; }
        public double mae { get; set; }
        public double rmse { get; set; }
        public int maxChannelDelta { get; set; }
        public double meanDiffLuminance { get; set; }
        public double meanDiffChroma { get; set; }
        public double darkPixelBalance { get; set; }
        public double diffBoundsAreaFraction { get; set; }
        public double score { get; set; }
    }

    internal sealed class CorpusVisualCenterScore
    {
        public string name { get; set; } = "";
        public double score { get; set; }
    }

    private readonly record struct CorpusVisualVectorMetrics(
        double diffFraction,
        double mae,
        double rmse,
        int maxChannelDelta,
        double meanDiffLuminance,
        double meanDiffChroma,
        double darkPixelBalance,
        double diffBoundsAreaFraction,
        double score);
}
