using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pdfe.Core.Document;
using Pdfe.Core.Text.Segmentation;
using Pdfe.Rendering;
using Pdfe.Rendering.Differential;
using SkiaSharp;

namespace Pdfe.RenderTools;

partial class Program
{
    private static readonly JsonSerializerOptions BenchmarkJsonOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    static Command CreateBenchmarkSuiteCommand()
    {
        var outputOption = new Option<DirectoryInfo>("--output-dir", "-o")
        {
            Description = "Directory for benchmark-report.json, benchmark-pages.csv, and benchmark-report.md.",
            DefaultValueFactory = _ => new DirectoryInfo("logs/benchmarks/latest"),
        };
        var corpusOption = new Option<DirectoryInfo?>("--corpus")
        {
            Description = "Optional PDF corpus directory. If absent or empty, a synthetic fallback fixture is generated.",
        };
        var pageLimitOption = new Option<int>("--page-limit")
        {
            Description = "Maximum pages to benchmark across the selected inputs.",
            DefaultValueFactory = _ => 8,
        };
        var dpiOption = new Option<int>("--dpi")
        {
            Description = "Render DPI.",
            DefaultValueFactory = _ => 96,
        };
        var timeoutOption = new Option<int>("--timeout-ms")
        {
            Description = "Per-render reference tool timeout in milliseconds.",
            DefaultValueFactory = _ => 20_000,
        };
        var oraclesOption = new Option<string>("--oracles")
        {
            Description = "Reference renderers: none, mutool, pdftocairo, ghostscript, pdfbox, pdfium, or all.",
            DefaultValueFactory = _ => "all",
        };
        var failOnRegressionOption = new Option<bool>("--fail-on-regression")
        {
            Description = "Return non-zero if deterministic benchmark gates fail.",
            DefaultValueFactory = _ => false,
        };
        var maxPdfeRenderMsOption = new Option<double>("--max-pdfe-render-ms")
        {
            Description = "Regression gate: maximum average pdfe render time per page.",
            DefaultValueFactory = _ => 2_500,
        };
        var maxPdfeParseMsOption = new Option<double>("--max-pdfe-parse-ms")
        {
            Description = "Regression gate: maximum average pdfe open/parse time per document.",
            DefaultValueFactory = _ => 750,
        };
        var minReferencePassRateOption = new Option<double>("--min-reference-pass-rate")
        {
            Description = "Regression gate: minimum pdfe-vs-reference page pass rate when at least one reference renderer is available.",
            DefaultValueFactory = _ => 0.60,
        };
        var includeCliRenderOption = new Option<bool>("--include-cli-render")
        {
            Description = "Also render each page through the pdfe CLI subprocess and compare it to the in-process renderer.",
            DefaultValueFactory = _ => false,
        };

        var command = new Command(
            "benchmark-suite",
            "Run speed, fidelity, text, robustness, and redaction-completeness benchmark reports")
        {
            outputOption,
            corpusOption,
            pageLimitOption,
            dpiOption,
            timeoutOption,
            oraclesOption,
            failOnRegressionOption,
            maxPdfeRenderMsOption,
            maxPdfeParseMsOption,
            minReferencePassRateOption,
            includeCliRenderOption,
        };

        command.SetAction(parseResult =>
        {
            var output = parseResult.GetValue(outputOption)!;
            var corpus = parseResult.GetValue(corpusOption);
            var pageLimit = parseResult.GetValue(pageLimitOption);
            var dpi = parseResult.GetValue(dpiOption);
            var timeoutMs = parseResult.GetValue(timeoutOption);
            var oraclesRaw = parseResult.GetValue(oraclesOption) ?? "all";
            var failOnRegression = parseResult.GetValue(failOnRegressionOption);
            var maxPdfeRenderMs = parseResult.GetValue(maxPdfeRenderMsOption);
            var maxPdfeParseMs = parseResult.GetValue(maxPdfeParseMsOption);
            var minReferencePassRate = parseResult.GetValue(minReferencePassRateOption);
            var includeCliRender = parseResult.GetValue(includeCliRenderOption);

            try
            {
                if (!TryParseBenchmarkOracles(oraclesRaw, out var oracles, out var oracleError))
                {
                    Console.Error.WriteLine(oracleError);
                    Environment.ExitCode = 2;
                    return;
                }

                var report = RunBenchmarkSuite(
                    output.FullName,
                    corpus?.FullName,
                    Math.Max(1, pageLimit),
                    Math.Max(36, dpi),
                    Math.Max(1_000, timeoutMs),
                    oracles,
                    new BenchmarkGateConfig(
                        maxPdfeRenderMs,
                        maxPdfeParseMs,
                        minReferencePassRate),
                    includeCliRender);

                WriteBenchmarkSuiteReport(report, output.FullName);
                PrintBenchmarkSuiteSummary(report);

                if (failOnRegression && !report.regressionGate.passed)
                    Environment.ExitCode = 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                Environment.ExitCode = 1;
            }
        });

        return command;
    }

    internal static BenchmarkSuiteReport RunBenchmarkSuite(
        string outputDir,
        string? corpusDir,
        int pageLimit,
        int dpi,
        int timeoutMs,
        BenchmarkOracleSelection oracleSelection,
        BenchmarkGateConfig gateConfig,
        bool includeCliRender = false)
    {
        Directory.CreateDirectory(outputDir);
        var inputs = ResolveBenchmarkInputs(outputDir, corpusDir, pageLimit);
        var selectedOracles = ResolveBenchmarkOracles(oracleSelection);
        var tools = BuildBenchmarkToolInventory(selectedOracles, includeCliRender);
        var pages = new List<BenchmarkPageResult>();

        foreach (var input in inputs)
        {
            var parseSw = Stopwatch.StartNew();
            using var doc = PdfDocument.Open(input.Path);
            parseSw.Stop();

            var pagesToRun = Math.Min(doc.PageCount, Math.Max(0, pageLimit - pages.Count));
            for (var pageNumber = 1; pageNumber <= pagesToRun; pageNumber++)
            {
                pages.Add(BenchmarkPage(input, doc, pageNumber, dpi, timeoutMs, selectedOracles, parseSw.ElapsedMilliseconds, includeCliRender));
                if (pages.Count >= pageLimit)
                    break;
            }

            if (pages.Count >= pageLimit)
                break;
        }

        var redaction = RunSyntheticRedactionBenchmark(outputDir);
        var summary = BuildBenchmarkSummary(pages, redaction);
        var hotPaths = BuildBenchmarkHotPaths(pages, redaction);
        var gate = EvaluateBenchmarkGate(summary, gateConfig);
        return new BenchmarkSuiteReport
        {
            schemaVersion = 1,
            generatedUtc = DateTimeOffset.UtcNow.ToString("O"),
            issues = new[] { "#344", "#357", "#536", "#596", "#597", "#602" },
            licenseIsolation = new BenchmarkLicenseIsolation
            {
                policy = "Copyleft or AGPL/GPL reference renderers are invoked only as external CLI subprocesses; no MuPDF, Poppler, Ghostscript, or PDFBox libraries are referenced by pdfe.",
                shippableGraph = "The benchmark suite lives under tools/Pdfe.RenderTools and scripts/run-benchmarks.sh, outside the desktop app dependency graph.",
                permissiveInProcess = "pdfe's own parser/renderer run in-process. Optional permissive library benchmarks remain isolated to dev tooling.",
            },
            configuration = new BenchmarkSuiteConfiguration
            {
                outputDir = Path.GetFullPath(outputDir),
                corpusDir = corpusDir is null ? null : Path.GetFullPath(corpusDir),
                pageLimit = pageLimit,
                dpi = dpi,
                timeoutMs = timeoutMs,
                selectedOracles = selectedOracles.Select(o => o.Name).ToArray(),
                includeCliRender = includeCliRender,
            },
            tools = tools,
            summary = summary,
            hotPaths = hotPaths,
            regressionGate = gate,
            pages = pages.ToArray(),
            redactionCompleteness = redaction,
        };
    }

    private static BenchmarkPageResult BenchmarkPage(
        BenchmarkInput input,
        PdfDocument doc,
        int pageNumber,
        int dpi,
        int timeoutMs,
        IReadOnlyList<BenchmarkOracle> oracles,
        long parseMs,
        bool includeCliRender)
    {
        SKBitmap? pdfeBitmap = null;
        var text = "";
        var textSw = Stopwatch.StartNew();
        try
        {
            text = doc.GetPage(pageNumber).Text ?? "";
        }
        finally
        {
            textSw.Stop();
        }

        var renderSw = Stopwatch.StartNew();
        try
        {
            var renderer = new SkiaRenderer();
            pdfeBitmap = renderer.RenderPage(doc.GetPage(pageNumber), new RenderOptions { Dpi = dpi });
            renderSw.Stop();

            var references = new List<BenchmarkReferenceResult>();
            foreach (var oracle in oracles)
            {
                var reference = BenchmarkReference(oracle, input.Path, pageNumber, dpi, timeoutMs, pdfeBitmap);
                references.Add(reference);
            }

            var cliRender = includeCliRender
                ? BenchmarkCliRender(input.Path, pageNumber, dpi, timeoutMs, pdfeBitmap)
                : null;

            var okReferences = references.Where(r => r.status == "OK").ToArray();
            var passingReferences = okReferences.Count(IsBenchmarkReferencePass);
            return new BenchmarkPageResult
            {
                path = input.RelativePath,
                pageNumber = pageNumber,
                source = input.Source,
                parseMs = parseMs,
                pdfeRenderMs = renderSw.ElapsedMilliseconds,
                pdfeTextExtractMs = textSw.ElapsedMilliseconds,
                pdfeTextLength = text.Length,
                pdfeWidth = pdfeBitmap.Width,
                pdfeHeight = pdfeBitmap.Height,
                status = okReferences.Length == 0
                    ? "NO_REFERENCES"
                    : passingReferences > 0 ? "PASS_REFERENCE" : "DIFF_REFERENCE",
                referenceCount = okReferences.Length,
                passingReferenceCount = passingReferences,
                references = references.ToArray(),
                cliRender = cliRender,
            };
        }
        catch (Exception ex)
        {
            renderSw.Stop();
            return new BenchmarkPageResult
            {
                path = input.RelativePath,
                pageNumber = pageNumber,
                source = input.Source,
                parseMs = parseMs,
                pdfeRenderMs = renderSw.ElapsedMilliseconds,
                pdfeTextExtractMs = textSw.ElapsedMilliseconds,
                pdfeTextLength = text.Length,
                status = "PDFE_ERROR",
                error = ex.Message,
                references = Array.Empty<BenchmarkReferenceResult>(),
                cliRender = null,
            };
        }
        finally
        {
            pdfeBitmap?.Dispose();
        }
    }

    private static BenchmarkCliRenderResult BenchmarkCliRender(
        string pdfPath,
        int pageNumber,
        int dpi,
        int timeoutMs,
        SKBitmap pdfeBitmap)
    {
        var outputPath = Path.Combine(
            Path.GetTempPath(),
            $"pdfe-cli-benchmark-{Environment.ProcessId}-{Guid.NewGuid():N}.png");
        var invocation = ResolvePdfeCliInvocation();
        if (invocation is null)
        {
            return new BenchmarkCliRenderResult
            {
                name = "pdfe-cli",
                kind = "external-subprocess",
                status = "MISSING",
                error = "Could not locate Pdfe.Cli project or built assembly.",
            };
        }

        var args = new List<string>(invocation.Value.Arguments)
        {
            "render",
            pdfPath,
            "--page",
            pageNumber.ToString(CultureInfo.InvariantCulture),
            "--dpi",
            dpi.ToString(CultureInfo.InvariantCulture),
            "--output",
            outputPath,
        };

        var sw = Stopwatch.StartNew();
        try
        {
            var result = RunProcess(invocation.Value.FileName, args, timeoutMs);
            sw.Stop();
            if (result.ExitCode != 0)
            {
                return new BenchmarkCliRenderResult
                {
                    name = "pdfe-cli",
                    kind = "external-subprocess",
                    status = result.TimedOut ? "TIMEOUT" : "ERROR",
                    elapsedMs = sw.ElapsedMilliseconds,
                    error = string.IsNullOrWhiteSpace(result.StandardError)
                        ? result.StandardOutput
                        : result.StandardError,
                };
            }

            using var cliBitmap = SKBitmap.Decode(outputPath);
            if (cliBitmap is null)
            {
                return new BenchmarkCliRenderResult
                {
                    name = "pdfe-cli",
                    kind = "external-subprocess",
                    status = "ERROR",
                    elapsedMs = sw.ElapsedMilliseconds,
                    error = "CLI render completed but did not write a readable PNG.",
                };
            }

            using var normalized = DifferentialMetrics.ResizeMatch(cliBitmap, pdfeBitmap.Width, pdfeBitmap.Height);
            var diff = DifferentialMetrics.Compare(pdfeBitmap, normalized);
            return new BenchmarkCliRenderResult
            {
                name = "pdfe-cli",
                kind = "external-subprocess",
                status = "OK",
                elapsedMs = sw.ElapsedMilliseconds,
                width = cliBitmap.Width,
                height = cliBitmap.Height,
                diffFraction = diff.DifferingPixelFraction,
                meanAbsoluteError = diff.MeanAbsoluteError,
                pass = diff.DifferingPixelFraction <= 0.001 && diff.MeanAbsoluteError <= 1.0,
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new BenchmarkCliRenderResult
            {
                name = "pdfe-cli",
                kind = "external-subprocess",
                status = "ERROR",
                elapsedMs = sw.ElapsedMilliseconds,
                error = ex.Message,
            };
        }
        finally
        {
            try
            {
                if (File.Exists(outputPath))
                    File.Delete(outputPath);
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }
    }

    private static (string FileName, IReadOnlyList<string> Arguments)? ResolvePdfeCliInvocation()
    {
        var overrideCommand = Environment.GetEnvironmentVariable("PDFE_BENCHMARK_CLI_COMMAND");
        if (!string.IsNullOrWhiteSpace(overrideCommand))
            return (overrideCommand, Array.Empty<string>());

        var root = FindRepositoryRoot();
        if (root is null)
            return null;

        var configuration = Environment.GetEnvironmentVariable("CONFIG") ??
#if DEBUG
            "Debug";
#else
            "Release";
#endif
        var outputDir = Path.Combine(root, "Pdfe.Cli", "bin", configuration, "net10.0");
        var executable = Path.Combine(outputDir, OperatingSystem.IsWindows() ? "pdfe.exe" : "pdfe");
        if (File.Exists(executable))
            return (executable, Array.Empty<string>());

        foreach (var candidate in new[]
                 {
                     Path.Combine(outputDir, "pdfe.dll"),
                     Path.Combine(outputDir, "Pdfe.Cli.dll"),
                 })
        {
            if (File.Exists(candidate))
                return ("dotnet", new[] { candidate });
        }

        var project = Path.Combine(root, "Pdfe.Cli", "Pdfe.Cli.csproj");
        if (!File.Exists(project))
            return null;

        return ("dotnet", new[]
        {
            "run",
            "--project",
            project,
            "-c",
            configuration,
            "--",
        });
    }

    private static string? FindRepositoryRoot()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "pdfe.sln")) &&
                File.Exists(Path.Combine(current.FullName, "Pdfe.Cli", "Pdfe.Cli.csproj")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "pdfe.sln")) &&
                File.Exists(Path.Combine(current.FullName, "Pdfe.Cli", "Pdfe.Cli.csproj")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }

    private static ProcessResult RunProcess(
        string fileName,
        IReadOnlyList<string> arguments,
        int timeoutMs)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) stdout.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) stderr.AppendLine(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(entireProcessTree: true); }
            catch { }
            return new ProcessResult(-1, stdout.ToString(), stderr.ToString(), TimedOut: true);
        }

        process.WaitForExit();
        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString(), TimedOut: false);
    }

    private static BenchmarkReferenceResult BenchmarkReference(
        BenchmarkOracle oracle,
        string pdfPath,
        int pageNumber,
        int dpi,
        int timeoutMs,
        SKBitmap pdfeBitmap)
    {
        ReferenceRenderResult result;
        try
        {
            result = oracle.Render(pdfPath, pageNumber, dpi, timeoutMs);
        }
        catch (Exception ex)
        {
            return new BenchmarkReferenceResult
            {
                name = oracle.Name,
                kind = "external-cli",
                status = "ERROR",
                error = ex.Message,
            };
        }

        using var referenceBitmap = result.Bitmap;
        if (referenceBitmap is null)
        {
            return new BenchmarkReferenceResult
            {
                name = oracle.Name,
                kind = "external-cli",
                status = result.Status,
                error = result.ErrorMessage,
                elapsedMs = result.ElapsedMs,
            };
        }

        using var normalizedReference = DifferentialMetrics.ResizeMatch(referenceBitmap, pdfeBitmap.Width, pdfeBitmap.Height);
        var diff = DifferentialMetrics.Compare(pdfeBitmap, normalizedReference);
        var rmse = CalculateRmse(pdfeBitmap, normalizedReference);
        var ssim = CalculateLuminanceSsim(pdfeBitmap, normalizedReference);
        return new BenchmarkReferenceResult
        {
            name = oracle.Name,
            kind = "external-cli",
            status = result.Status,
            elapsedMs = result.ElapsedMs,
            width = referenceBitmap.Width,
            height = referenceBitmap.Height,
            diffFraction = diff.DifferingPixelFraction,
            meanAbsoluteError = diff.MeanAbsoluteError,
            rmse = rmse,
            ssim = ssim,
            pass = diff.DifferingPixelFraction <= 0.10 && diff.MeanAbsoluteError <= 32.0,
        };
    }

    private static BenchmarkRedactionResult RunSyntheticRedactionBenchmark(string outputDir)
    {
        var source = Path.Combine(outputDir, "synthetic-redaction-fixture.pdf");
        File.WriteAllBytes(source, CreateSamplePdf());
        var sw = Stopwatch.StartNew();
        try
        {
            using var doc = PdfDocument.Open(source);
            var page = doc.GetPage(1);
            var letters = page.Letters;
            var textBefore = page.Text ?? "";
            var target = "Pdfe.Core";
            var index = textBefore.IndexOf(target, StringComparison.Ordinal);
            if (index < 0 || letters.Count < index + target.Length)
            {
                return new BenchmarkRedactionResult
                {
                    status = "SKIPPED",
                    elapsedMs = sw.ElapsedMilliseconds,
                    reason = "Synthetic target text was not extractable.",
                };
            }

            var targetLetters = letters.Skip(index).Take(target.Length).ToArray();
            var area = new PdfRectangle(
                targetLetters.Min(l => l.GlyphRectangle.Left) - 1,
                targetLetters.Min(l => l.GlyphRectangle.Bottom) - 1,
                targetLetters.Max(l => l.GlyphRectangle.Right) + 1,
                targetLetters.Max(l => l.GlyphRectangle.Top) + 1);

            page.RedactArea(area);
            var saved = doc.SaveToBytes();
            using var reopened = PdfDocument.Open(saved);
            var textAfter = reopened.GetPage(1).Text ?? "";
            sw.Stop();

            return new BenchmarkRedactionResult
            {
                status = !textAfter.Contains(target, StringComparison.Ordinal) ? "PASS" : "FAIL",
                elapsedMs = sw.ElapsedMilliseconds,
                target = target,
                textBeforeLength = textBefore.Length,
                textAfterLength = textAfter.Length,
                reason = !textAfter.Contains(target, StringComparison.Ordinal)
                    ? "Synthetic glyph-level redaction removed the target from extracted text."
                    : "Synthetic glyph-level redaction left target text extractable.",
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new BenchmarkRedactionResult
            {
                status = "ERROR",
                elapsedMs = sw.ElapsedMilliseconds,
                reason = ex.Message,
            };
        }
    }

    private static BenchmarkSummary BuildBenchmarkSummary(
        IReadOnlyList<BenchmarkPageResult> pages,
        BenchmarkRedactionResult redaction)
    {
        var pdfeRender = pages.Select(p => p.pdfeRenderMs).Where(v => v >= 0).OrderBy(v => v).ToArray();
        var pdfeText = pages.Select(p => p.pdfeTextExtractMs).Where(v => v >= 0).OrderBy(v => v).ToArray();
        var parse = pages.Select(p => p.parseMs).Where(v => v >= 0).OrderBy(v => v).ToArray();
        var references = pages.SelectMany(p => p.references).Where(r => r.status == "OK").ToArray();
        var cliRenders = pages
            .Select(p => p.cliRender)
            .Where(r => r?.status == "OK" && r.elapsedMs.HasValue)
            .Select(r => r!)
            .ToArray();
        var cliRenderDurations = cliRenders.Select(r => r.elapsedMs!.Value).OrderBy(v => v).ToArray();
        var cliRenderAttempted = pages.Count(p => p.cliRender is not null);
        var cliRenderPassed = pages.Count(p => p.cliRender?.pass == true);
        var passedPages = pages.Count(p => p.status is "PASS_REFERENCE" or "NO_REFERENCES");
        var comparedPages = pages.Count(p => p.referenceCount > 0);
        var referencePassPages = pages.Count(p => p.referenceCount > 0 && p.passingReferenceCount > 0);

        return new BenchmarkSummary
        {
            pdfCount = pages.Select(p => p.path).Distinct(StringComparer.Ordinal).Count(),
            pageCount = pages.Count,
            comparedPageCount = comparedPages,
            statusCounts = pages
                .GroupBy(p => p.status, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            pdfeParseAverageMs = Average(parse),
            pdfeParseP95Ms = PercentileDouble(parse, 0.95),
            pdfeRenderAverageMs = Average(pdfeRender),
            pdfeRenderP50Ms = PercentileDouble(pdfeRender, 0.50),
            pdfeRenderP95Ms = PercentileDouble(pdfeRender, 0.95),
            pdfeTextExtractAverageMs = Average(pdfeText),
            cliRenderAverageMs = Average(cliRenderDurations),
            cliRenderP95Ms = PercentileDouble(cliRenderDurations, 0.95),
            cliRenderPassRate = cliRenderAttempted == 0 ? null : cliRenderPassed / (double)cliRenderAttempted,
            referenceRenderAverageMs = Average(references.Select(r => r.elapsedMs ?? 0).OrderBy(v => v).ToArray()),
            referencePassRate = comparedPages == 0 ? null : referencePassPages / (double)comparedPages,
            averageRmse = Average(references.Where(r => r.rmse.HasValue).Select(r => r.rmse!.Value).OrderBy(v => v).ToArray()),
            averageSsim = Average(references.Where(r => r.ssim.HasValue).Select(r => r.ssim!.Value).OrderBy(v => v).ToArray()),
            redactionStatus = redaction.status,
        };
    }

    private static BenchmarkRegressionGate EvaluateBenchmarkGate(BenchmarkSummary summary, BenchmarkGateConfig config)
    {
        var checks = new List<BenchmarkGateCheck>
        {
            new()
            {
                name = "pdfe-render-average",
                actual = summary.pdfeRenderAverageMs,
                threshold = config.MaxPdfeRenderMs,
                passed = summary.pdfeRenderAverageMs <= config.MaxPdfeRenderMs,
                unit = "ms",
            },
            new()
            {
                name = "pdfe-parse-average",
                actual = summary.pdfeParseAverageMs,
                threshold = config.MaxPdfeParseMs,
                passed = summary.pdfeParseAverageMs <= config.MaxPdfeParseMs,
                unit = "ms",
            },
            new()
            {
                name = "synthetic-redaction-completeness",
                actual = summary.redactionStatus == "PASS" ? 1 : 0,
                threshold = 1,
                passed = summary.redactionStatus == "PASS",
                unit = "pass",
            },
        };

        if (summary.referencePassRate.HasValue)
        {
            checks.Add(new BenchmarkGateCheck
            {
                name = "reference-fidelity-pass-rate",
                actual = summary.referencePassRate.Value,
                threshold = config.MinReferencePassRate,
                passed = summary.referencePassRate.Value >= config.MinReferencePassRate,
                unit = "ratio",
            });
        }

        if (summary.cliRenderPassRate.HasValue)
        {
            checks.Add(new BenchmarkGateCheck
            {
                name = "pdfe-cli-render-pass-rate",
                actual = summary.cliRenderPassRate.Value,
                threshold = 1.0,
                passed = summary.cliRenderPassRate.Value >= 1.0,
                unit = "ratio",
            });
        }

        return new BenchmarkRegressionGate
        {
            passed = checks.All(c => c.passed),
            checks = checks.ToArray(),
        };
    }

    private static IReadOnlyList<BenchmarkHotPathBucket> BuildBenchmarkHotPaths(
        IReadOnlyList<BenchmarkPageResult> pages,
        BenchmarkRedactionResult redaction)
    {
        var buckets = new List<BenchmarkHotPathBucket>();
        AddBenchmarkHotPath(
            buckets,
            "renderer.page-render",
            "Pdfe.Rendering page rasterization",
            pages.Select(p => p.pdfeRenderMs),
            "pdfe-owned",
            "#598 #599");
        AddBenchmarkHotPath(
            buckets,
            "text.extract-search-input",
            "Pdfe.Core text extraction and text-path preparation",
            pages.Select(p => p.pdfeTextExtractMs),
            "pdfe-owned",
            "#600");
        AddBenchmarkHotPath(
            buckets,
            "parser.document-open",
            "Pdfe.Core document open/parse time",
            pages
                .GroupBy(p => p.path, StringComparer.Ordinal)
                .Select(g => g.First().parseMs),
            "pdfe-owned",
            "#597");
        AddBenchmarkHotPath(
            buckets,
            "cli.render-page-subprocess",
            "Pdfe.Cli render command subprocess, PNG write, and output comparison",
            pages
                .Select(page => page.cliRender?.elapsedMs)
                .Where(value => value.HasValue)
                .Select(value => value!.Value),
            "pdfe-owned",
            "#596 #597");
        AddBenchmarkHotPath(
            buckets,
            "redaction.synthetic-save",
            "Synthetic glyph-level redaction and save/reopen verification",
            new[] { redaction.elapsedMs },
            "pdfe-owned-security-critical",
            "#597 #602");
        AddBenchmarkHotPath(
            buckets,
            "reference.external-render",
            "External reference renderer subprocess time; reported for comparison, not optimized by pdfe",
            pages.SelectMany(p => p.references).Where(r => r.elapsedMs.HasValue).Select(r => r.elapsedMs!.Value),
            "external-reference",
            "#597");

        return buckets
            .OrderByDescending(bucket => bucket.scope == "pdfe-owned" || bucket.scope == "pdfe-owned-security-critical")
            .ThenByDescending(bucket => bucket.totalMs)
            .ThenBy(bucket => bucket.name, StringComparer.Ordinal)
            .ToArray();
    }

    private static void AddBenchmarkHotPath(
        List<BenchmarkHotPathBucket> buckets,
        string name,
        string description,
        IEnumerable<long> rawDurations,
        string scope,
        string issueRefs)
    {
        var durations = rawDurations
            .Where(value => value >= 0)
            .OrderBy(value => value)
            .ToArray();
        if (durations.Length == 0)
            return;

        var definition = HotspotRegressionCatalog.ForBenchmarkBucket(name);
        buckets.Add(new BenchmarkHotPathBucket
        {
            name = name,
            workloadId = definition.workloadId,
            component = definition.component,
            route = definition.route,
            category = definition.category,
            description = description,
            scope = scope,
            regressionPolicy = definition.regressionPolicy,
            issueRefs = issueRefs,
            count = durations.Length,
            totalMs = durations.Sum(),
            averageMs = durations.Average(),
            p50Ms = PercentileDouble(durations, 0.50),
            p95Ms = PercentileDouble(durations, 0.95),
            maxMs = durations[^1],
        });
    }

    private static IReadOnlyList<BenchmarkInput> ResolveBenchmarkInputs(string outputDir, string? corpusDir, int pageLimit)
    {
        if (!string.IsNullOrWhiteSpace(corpusDir) && Directory.Exists(corpusDir))
        {
            var pdfs = Directory.EnumerateFiles(corpusDir, "*.pdf", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.Ordinal)
                .Take(Math.Max(1, pageLimit))
                .Select(path => new BenchmarkInput(
                    Path.GetFullPath(path),
                    Path.GetRelativePath(corpusDir, path).Replace(Path.DirectorySeparatorChar, '/'),
                    "corpus"))
                .ToArray();
            if (pdfs.Length > 0)
                return pdfs;
        }

        var syntheticDir = Path.Combine(outputDir, "synthetic-inputs");
        Directory.CreateDirectory(syntheticDir);
        var syntheticPath = Path.Combine(syntheticDir, "pdfe-benchmark-synthetic.pdf");
        File.WriteAllBytes(syntheticPath, CreateSamplePdf());
        return new[]
        {
            new BenchmarkInput(syntheticPath, "synthetic/pdfe-benchmark-synthetic.pdf", "synthetic"),
        };
    }

    private static IReadOnlyList<BenchmarkOracle> ResolveBenchmarkOracles(BenchmarkOracleSelection selection)
    {
        var oracles = new List<BenchmarkOracle>();
        if (selection.HasFlag(BenchmarkOracleSelection.Mutool))
        {
            oracles.Add(new BenchmarkOracle(
                "mutool",
                () => MutoolReferenceRenderer.IsAvailable,
                (path, page, dpi, timeout) => MutoolReferenceRenderer.TryRenderPage(path, page, dpi, timeout)));
        }
        if (selection.HasFlag(BenchmarkOracleSelection.Pdftocairo))
        {
            oracles.Add(new BenchmarkOracle(
                "pdftocairo",
                () => PdftocairoReferenceRenderer.IsAvailable,
                (path, page, dpi, timeout) => PdftocairoReferenceRenderer.TryRenderPage(path, page, dpi, timeout)));
        }
        if (selection.HasFlag(BenchmarkOracleSelection.Ghostscript))
        {
            oracles.Add(new BenchmarkOracle(
                "ghostscript",
                () => GhostscriptReferenceRenderer.IsAvailable,
                (path, page, dpi, timeout) => GhostscriptReferenceRenderer.TryRenderPage(path, page, dpi, timeout)));
        }
        if (selection.HasFlag(BenchmarkOracleSelection.PdfBox))
        {
            oracles.Add(new BenchmarkOracle(
                "pdfbox",
                () => PdfBoxReferenceRenderer.IsAvailable,
                (path, page, dpi, timeout) => PdfBoxReferenceRenderer.TryRenderPage(path, page, dpi, timeout)));
        }
        if (selection.HasFlag(BenchmarkOracleSelection.Pdfium))
        {
            oracles.Add(new BenchmarkOracle(
                "pdfium",
                () => PdfiumReferenceRenderer.IsAvailable,
                (path, page, dpi, timeout) => PdfiumReferenceRenderer.TryRenderPage(path, page, dpi, timeout)));
        }

        return oracles.Where(o => o.IsAvailable()).ToArray();
    }

    private static IReadOnlyList<BenchmarkToolStatus> BuildBenchmarkToolInventory(
        IReadOnlyList<BenchmarkOracle> selectedOracles,
        bool includeCliRender)
    {
        var selected = selectedOracles.Select(o => o.Name).ToHashSet(StringComparer.Ordinal);
        return new[]
        {
            Tool("pdfe", "in-process", true, true, "MIT project code"),
            Tool("pdfe-cli", "external-subprocess", ResolvePdfeCliInvocation() is not null, includeCliRender, "pdfe CLI invoked as a subprocess to test the shipped command route"),
            Tool("mutool", "external-cli", MutoolReferenceRenderer.IsAvailable, selected.Contains("mutool"), "AGPL/GPL-family renderer invoked only as subprocess"),
            Tool("pdftocairo", "external-cli", PdftocairoReferenceRenderer.IsAvailable, selected.Contains("pdftocairo"), "GPL Poppler renderer invoked only as subprocess"),
            Tool("ghostscript", "external-cli", GhostscriptReferenceRenderer.IsAvailable, selected.Contains("ghostscript"), "AGPL Ghostscript renderer invoked only as subprocess"),
            Tool("pdfbox", "external-cli", PdfBoxReferenceRenderer.IsAvailable, selected.Contains("pdfbox"), "Apache PDFBox command invoked only as subprocess"),
            Tool("pdfium", "external-cli", PdfiumReferenceRenderer.IsAvailable, selected.Contains("pdfium"), "BSD PDFium pdfium_test invoked only as subprocess"),
        };
    }

    private static BenchmarkToolStatus Tool(string name, string kind, bool available, bool selected, string licensePolicy)
        => new()
        {
            name = name,
            kind = kind,
            available = available,
            selected = selected,
            licensePolicy = licensePolicy,
        };

    private static void WriteBenchmarkSuiteReport(BenchmarkSuiteReport report, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        File.WriteAllText(
            Path.Combine(outputDir, "benchmark-report.json"),
            JsonSerializer.Serialize(report, BenchmarkJsonOptions));
        File.WriteAllText(
            Path.Combine(outputDir, "benchmark-pages.csv"),
            BuildBenchmarkCsv(report));
        File.WriteAllText(
            Path.Combine(outputDir, "benchmark-hotpaths.json"),
            JsonSerializer.Serialize(report.hotPaths, BenchmarkJsonOptions));
        File.WriteAllText(
            Path.Combine(outputDir, "benchmark-report.md"),
            BuildBenchmarkMarkdown(report));
    }

    private static string BuildBenchmarkCsv(BenchmarkSuiteReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("path,page,status,pdfeRenderMs,pdfeTextExtractMs,cliRenderStatus,cliRenderMs,cliRenderPass,cliDiffFraction,referenceCount,passingReferenceCount,bestReference,bestDiffFraction,bestMae,bestRmse,bestSsim");
        foreach (var page in report.pages)
        {
            var best = page.references
                .Where(r => r.status == "OK")
                .OrderBy(r => r.diffFraction ?? double.MaxValue)
                .ThenBy(r => r.meanAbsoluteError ?? double.MaxValue)
                .FirstOrDefault();
            sb.AppendLine(string.Join(",",
                Csv(page.path),
                page.pageNumber.ToString(CultureInfo.InvariantCulture),
                Csv(page.status),
                page.pdfeRenderMs.ToString(CultureInfo.InvariantCulture),
                page.pdfeTextExtractMs.ToString(CultureInfo.InvariantCulture),
                Csv(page.cliRender?.status ?? ""),
                page.cliRender?.elapsedMs?.ToString(CultureInfo.InvariantCulture) ?? "",
                page.cliRender?.pass?.ToString() ?? "",
                CsvNumber(page.cliRender?.diffFraction),
                page.referenceCount.ToString(CultureInfo.InvariantCulture),
                page.passingReferenceCount.ToString(CultureInfo.InvariantCulture),
                Csv(best?.name ?? ""),
                CsvNumber(best?.diffFraction),
                CsvNumber(best?.meanAbsoluteError),
                CsvNumber(best?.rmse),
                CsvNumber(best?.ssim)));
        }

        return sb.ToString();
    }

    private static string BuildBenchmarkMarkdown(BenchmarkSuiteReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# pdfe Benchmark Report");
        sb.AppendLine();
        sb.AppendLine($"- Generated: `{report.generatedUtc}`");
        sb.AppendLine($"- Pages: `{report.summary.pageCount}`");
        sb.AppendLine($"- Regression gate: `{(report.regressionGate.passed ? "PASS" : "FAIL")}`");
        sb.AppendLine($"- pdfe render average: `{report.summary.pdfeRenderAverageMs:F1} ms`");
        sb.AppendLine($"- pdfe render p95: `{report.summary.pdfeRenderP95Ms:F1} ms`");
        if (report.summary.cliRenderPassRate.HasValue)
        {
            sb.AppendLine($"- pdfe CLI render average: `{report.summary.cliRenderAverageMs:F1} ms`");
            sb.AppendLine($"- pdfe CLI render pass rate: `{report.summary.cliRenderPassRate.Value:P1}`");
        }
        sb.AppendLine($"- Reference pass rate: `{(report.summary.referencePassRate?.ToString("P1", CultureInfo.InvariantCulture) ?? "n/a")}`");
        sb.AppendLine($"- Redaction completeness: `{report.redactionCompleteness.status}`");
        sb.AppendLine();
        sb.AppendLine("## Tool Isolation");
        sb.AppendLine();
        foreach (var tool in report.tools)
            sb.AppendLine($"- `{tool.name}`: {tool.kind}, available={tool.available}, selected={tool.selected}, {tool.licensePolicy}");
        sb.AppendLine();
        sb.AppendLine("## Gate Checks");
        sb.AppendLine();
        sb.AppendLine("| Check | Actual | Threshold | Result |");
        sb.AppendLine("|---|---:|---:|---|");
        foreach (var check in report.regressionGate.checks)
        {
            sb.AppendLine(
                $"| {check.name} | {check.actual:0.###} {check.unit} | {check.threshold:0.###} {check.unit} | {(check.passed ? "PASS" : "FAIL")} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Hot Path Buckets");
        sb.AppendLine();
        sb.AppendLine("| Bucket | Workload | Route | Scope | Count | Total ms | Avg ms | P95 ms | Issues |");
        sb.AppendLine("|---|---|---|---|---:|---:|---:|---:|---|");
        foreach (var bucket in report.hotPaths)
        {
            sb.AppendLine(
                $"| `{bucket.name}` | {bucket.workloadId} | {bucket.route} | {bucket.scope} | {bucket.count} | {bucket.totalMs} | {bucket.averageMs:0.0} | {bucket.p95Ms:0.0} | {bucket.issueRefs} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Page Results");
        sb.AppendLine();
        sb.AppendLine("| PDF | Page | Status | pdfe render ms | CLI | References |");
        sb.AppendLine("|---|---:|---|---:|---|---:|");
        foreach (var page in report.pages)
        {
            var cli = page.cliRender is null
                ? "not run"
                : $"{page.cliRender.status} {page.cliRender.elapsedMs?.ToString(CultureInfo.InvariantCulture) ?? ""}ms";
            sb.AppendLine($"| `{page.path}` | {page.pageNumber} | {page.status} | {page.pdfeRenderMs} | {cli} | {page.passingReferenceCount}/{page.referenceCount} |");
        }

        return sb.ToString();
    }

    private static void PrintBenchmarkSuiteSummary(BenchmarkSuiteReport report)
    {
        Console.Out.WriteLine(
            $"Benchmark suite: {report.summary.pageCount} page(s), " +
            $"pdfe render avg {report.summary.pdfeRenderAverageMs:F1}ms, " +
            $"gate {(report.regressionGate.passed ? "PASS" : "FAIL")}");
        Console.Out.WriteLine("Tools: " + string.Join(", ", report.tools.Select(t => $"{t.name}={(t.available ? "available" : "missing")}")));
        var topPdfeHotPath = report.hotPaths.FirstOrDefault(h => h.scope.StartsWith("pdfe-owned", StringComparison.Ordinal));
        if (topPdfeHotPath is not null)
            Console.Out.WriteLine($"Top pdfe-owned hot path: {topPdfeHotPath.name} ({topPdfeHotPath.totalMs}ms total)");
        Console.Out.WriteLine("Report: " + Path.Combine(report.configuration.outputDir, "benchmark-report.md"));
    }

    private static bool IsBenchmarkReferencePass(BenchmarkReferenceResult reference)
        => reference.pass == true;

    private static double CalculateRmse(SKBitmap a, SKBitmap b)
    {
        double sum = 0;
        long count = (long)a.Width * a.Height * 3;
        for (var y = 0; y < a.Height; y++)
        {
            for (var x = 0; x < a.Width; x++)
            {
                var pa = a.GetPixel(x, y);
                var pb = b.GetPixel(x, y);
                sum += Square(pa.Red - pb.Red);
                sum += Square(pa.Green - pb.Green);
                sum += Square(pa.Blue - pb.Blue);
            }
        }

        return Math.Sqrt(sum / count);
    }

    private static double CalculateLuminanceSsim(SKBitmap a, SKBitmap b)
    {
        long count = (long)a.Width * a.Height;
        if (count == 0)
            return 1;

        double meanA = 0;
        double meanB = 0;
        for (var y = 0; y < a.Height; y++)
        {
            for (var x = 0; x < a.Width; x++)
            {
                meanA += Luma(a.GetPixel(x, y));
                meanB += Luma(b.GetPixel(x, y));
            }
        }
        meanA /= count;
        meanB /= count;

        double varA = 0;
        double varB = 0;
        double cov = 0;
        for (var y = 0; y < a.Height; y++)
        {
            for (var x = 0; x < a.Width; x++)
            {
                var da = Luma(a.GetPixel(x, y)) - meanA;
                var db = Luma(b.GetPixel(x, y)) - meanB;
                varA += da * da;
                varB += db * db;
                cov += da * db;
            }
        }

        var denominatorCount = Math.Max(1, count - 1);
        varA /= denominatorCount;
        varB /= denominatorCount;
        cov /= denominatorCount;

        const double c1 = 6.5025;
        const double c2 = 58.5225;
        return ((2 * meanA * meanB + c1) * (2 * cov + c2)) /
               ((meanA * meanA + meanB * meanB + c1) * (varA + varB + c2));
    }

    private static double Luma(SKColor c) => 0.2126 * c.Red + 0.7152 * c.Green + 0.0722 * c.Blue;

    private static double Square(double value) => value * value;

    private static double Average(IReadOnlyCollection<long> values)
        => values.Count == 0 ? 0 : values.Average();

    private static double Average(IReadOnlyCollection<double> values)
        => values.Count == 0 ? 0 : values.Average();

    private static double PercentileDouble(IReadOnlyList<long> sortedValues, double percentile)
        => sortedValues.Count == 0 ? 0 : sortedValues[Math.Clamp((int)Math.Ceiling(sortedValues.Count * percentile) - 1, 0, sortedValues.Count - 1)];

    private static string Csv(string value)
        => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string CsvNumber(double? value)
        => value?.ToString("0.######", CultureInfo.InvariantCulture) ?? "";

    private sealed record ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError,
        bool TimedOut);

    internal static bool TryParseBenchmarkOracles(
        string value,
        out BenchmarkOracleSelection selection,
        out string? error)
    {
        selection = BenchmarkOracleSelection.None;
        error = null;
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "none":
                    selection = BenchmarkOracleSelection.None;
                    break;
                case "all":
                    selection |= BenchmarkOracleSelection.All;
                    break;
                case "mutool":
                    selection |= BenchmarkOracleSelection.Mutool;
                    break;
                case "pdftocairo":
                case "poppler":
                    selection |= BenchmarkOracleSelection.Pdftocairo;
                    break;
                case "ghostscript":
                case "gs":
                    selection |= BenchmarkOracleSelection.Ghostscript;
                    break;
                case "pdfbox":
                    selection |= BenchmarkOracleSelection.PdfBox;
                    break;
                case "pdfium":
                case "pdfium_test":
                    selection |= BenchmarkOracleSelection.Pdfium;
                    break;
                default:
                    error = $"Bad --oracles '{value}'. Use none, mutool, pdftocairo, ghostscript, pdfbox, pdfium, or all.";
                    return false;
            }
        }

        return true;
    }

    private sealed record BenchmarkInput(string Path, string RelativePath, string Source);

    private sealed record BenchmarkOracle(
        string Name,
        Func<bool> IsAvailable,
        Func<string, int, int, int, ReferenceRenderResult> Render);

    [Flags]
    internal enum BenchmarkOracleSelection
    {
        None = 0,
        Mutool = 1 << 0,
        Pdftocairo = 1 << 1,
        Ghostscript = 1 << 2,
        PdfBox = 1 << 3,
        Pdfium = 1 << 4,
        All = Mutool | Pdftocairo | Ghostscript | PdfBox | Pdfium,
    }

    internal sealed record BenchmarkGateConfig(
        double MaxPdfeRenderMs,
        double MaxPdfeParseMs,
        double MinReferencePassRate);

    internal sealed class BenchmarkSuiteReport
    {
        public int schemaVersion { get; set; }
        public string generatedUtc { get; set; } = "";
        public string[] issues { get; set; } = Array.Empty<string>();
        public BenchmarkLicenseIsolation licenseIsolation { get; set; } = new();
        public BenchmarkSuiteConfiguration configuration { get; set; } = new();
        public IReadOnlyList<BenchmarkToolStatus> tools { get; set; } = Array.Empty<BenchmarkToolStatus>();
        public BenchmarkSummary summary { get; set; } = new();
        public IReadOnlyList<BenchmarkHotPathBucket> hotPaths { get; set; } = Array.Empty<BenchmarkHotPathBucket>();
        public BenchmarkRegressionGate regressionGate { get; set; } = new();
        public IReadOnlyList<BenchmarkPageResult> pages { get; set; } = Array.Empty<BenchmarkPageResult>();
        public BenchmarkRedactionResult redactionCompleteness { get; set; } = new();
    }

    internal sealed class BenchmarkLicenseIsolation
    {
        public string policy { get; set; } = "";
        public string shippableGraph { get; set; } = "";
        public string permissiveInProcess { get; set; } = "";
    }

    internal sealed class BenchmarkSuiteConfiguration
    {
        public string outputDir { get; set; } = "";
        public string? corpusDir { get; set; }
        public int pageLimit { get; set; }
        public int dpi { get; set; }
        public int timeoutMs { get; set; }
        public IReadOnlyList<string> selectedOracles { get; set; } = Array.Empty<string>();
        public bool includeCliRender { get; set; }
    }

    internal sealed class BenchmarkToolStatus
    {
        public string name { get; set; } = "";
        public string kind { get; set; } = "";
        public bool available { get; set; }
        public bool selected { get; set; }
        public string licensePolicy { get; set; } = "";
    }

    internal sealed class BenchmarkSummary
    {
        public int pdfCount { get; set; }
        public int pageCount { get; set; }
        public int comparedPageCount { get; set; }
        public IReadOnlyDictionary<string, int> statusCounts { get; set; } = new Dictionary<string, int>();
        public double pdfeParseAverageMs { get; set; }
        public double pdfeParseP95Ms { get; set; }
        public double pdfeRenderAverageMs { get; set; }
        public double pdfeRenderP50Ms { get; set; }
        public double pdfeRenderP95Ms { get; set; }
        public double pdfeTextExtractAverageMs { get; set; }
        public double cliRenderAverageMs { get; set; }
        public double cliRenderP95Ms { get; set; }
        public double? cliRenderPassRate { get; set; }
        public double referenceRenderAverageMs { get; set; }
        public double? referencePassRate { get; set; }
        public double averageRmse { get; set; }
        public double averageSsim { get; set; }
        public string redactionStatus { get; set; } = "";
    }

    internal sealed class BenchmarkRegressionGate
    {
        public bool passed { get; set; }
        public IReadOnlyList<BenchmarkGateCheck> checks { get; set; } = Array.Empty<BenchmarkGateCheck>();
    }

    internal sealed class BenchmarkHotPathBucket
    {
        public string name { get; set; } = "";
        public string workloadId { get; set; } = "";
        public string component { get; set; } = "";
        public string route { get; set; } = "";
        public string category { get; set; } = "";
        public string description { get; set; } = "";
        public string scope { get; set; } = "";
        public string regressionPolicy { get; set; } = "";
        public string issueRefs { get; set; } = "";
        public int count { get; set; }
        public long totalMs { get; set; }
        public double averageMs { get; set; }
        public double p50Ms { get; set; }
        public double p95Ms { get; set; }
        public long maxMs { get; set; }
    }

    internal sealed class BenchmarkGateCheck
    {
        public string name { get; set; } = "";
        public double actual { get; set; }
        public double threshold { get; set; }
        public bool passed { get; set; }
        public string unit { get; set; } = "";
    }

    internal sealed class BenchmarkPageResult
    {
        public string path { get; set; } = "";
        public int pageNumber { get; set; }
        public string source { get; set; } = "";
        public string status { get; set; } = "";
        public string? error { get; set; }
        public long parseMs { get; set; }
        public long pdfeRenderMs { get; set; }
        public long pdfeTextExtractMs { get; set; }
        public int pdfeTextLength { get; set; }
        public int? pdfeWidth { get; set; }
        public int? pdfeHeight { get; set; }
        public int referenceCount { get; set; }
        public int passingReferenceCount { get; set; }
        public IReadOnlyList<BenchmarkReferenceResult> references { get; set; } = Array.Empty<BenchmarkReferenceResult>();
        public BenchmarkCliRenderResult? cliRender { get; set; }
    }

    internal sealed class BenchmarkReferenceResult
    {
        public string name { get; set; } = "";
        public string kind { get; set; } = "";
        public string status { get; set; } = "";
        public string? error { get; set; }
        public long? elapsedMs { get; set; }
        public int? width { get; set; }
        public int? height { get; set; }
        public double? diffFraction { get; set; }
        public double? meanAbsoluteError { get; set; }
        public double? rmse { get; set; }
        public double? ssim { get; set; }
        public bool? pass { get; set; }
    }

    internal sealed class BenchmarkCliRenderResult
    {
        public string name { get; set; } = "";
        public string kind { get; set; } = "";
        public string status { get; set; } = "";
        public string? error { get; set; }
        public long? elapsedMs { get; set; }
        public int? width { get; set; }
        public int? height { get; set; }
        public double? diffFraction { get; set; }
        public double? meanAbsoluteError { get; set; }
        public bool? pass { get; set; }
    }

    internal sealed class BenchmarkRedactionResult
    {
        public string status { get; set; } = "";
        public long elapsedMs { get; set; }
        public string? target { get; set; }
        public int? textBeforeLength { get; set; }
        public int? textAfterLength { get; set; }
        public string? reason { get; set; }
    }
}
