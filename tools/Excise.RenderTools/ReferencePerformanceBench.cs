using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Excise.Core.Document;
using Excise.Rendering;

namespace Excise.RenderTools;

partial class Program
{
    /// <summary>
    /// Matched cold-subprocess comparison for excise and the independent
    /// renderer CLIs. Every row launches a new process: timings and RSS are
    /// never recovered from cached images or a previous excise render.
    /// </summary>
    static Command CreateReferencePerformanceCommand()
    {
        var fixtureOption = new Option<FileInfo>("--fixtures")
        {
            Description = "JSON fixture manifest, relative paths resolved from the repository root.",
            DefaultValueFactory = _ => new FileInfo("tests/reference-performance/fixtures.json"),
        };
        var outputOption = new Option<DirectoryInfo>("--output-dir", "-o")
        {
            Description = "Directory for reference-performance.json and reference-performance.md.",
            DefaultValueFactory = _ => new DirectoryInfo("logs/reference-performance/latest"),
        };
        var runsOption = new Option<int>("--runs")
        {
            Description = "Fresh process runs per fixture (median is reported).",
            DefaultValueFactory = _ => 3,
        };
        var oraclesOption = new Option<string>("--oracles")
        {
            Description = "Reference renderers: none, mutool, pdftocairo, ghostscript, pdfbox, pdfium, or all.",
            DefaultValueFactory = _ => "all",
        };
        var timeoutOption = new Option<int>("--timeout-ms")
        {
            Description = "Per-render subprocess timeout.",
            DefaultValueFactory = _ => 120_000,
        };
        var includeHeavyOption = new Option<bool>("--include-heavy")
        {
            Description = "Include expensive prepress fixtures such as Altona.",
            DefaultValueFactory = _ => false,
        };
        var baselineOption = new Option<FileInfo?>("--baseline")
        {
            Description = "Prior reference-performance.json used for local regression comparison.",
        };
        var maxTimeRatioOption = new Option<double>("--max-excise-time-ratio")
        {
            Description = "Maximum current/baseline excise CLI median wall-time ratio.",
            DefaultValueFactory = _ => 1.50,
        };
        var maxRssRatioOption = new Option<double>("--max-excise-rss-ratio")
        {
            Description = "Maximum current/baseline excise CLI median peak-RSS ratio.",
            DefaultValueFactory = _ => 1.25,
        };
        var failOption = new Option<bool>("--fail-on-regression")
        {
            Description = "Return non-zero when a supplied baseline is exceeded.",
            DefaultValueFactory = _ => false,
        };

        var command = new Command("reference-performance",
            "Compare fresh excise CLI renders against independent renderer CLIs with wall, CPU, and RSS metrics")
        {
            fixtureOption, outputOption, runsOption, oraclesOption, timeoutOption,
            includeHeavyOption, baselineOption, maxTimeRatioOption, maxRssRatioOption, failOption,
        };
        command.SetAction(parseResult =>
        {
            var fixtureFile = parseResult.GetValue(fixtureOption)!;
            var output = parseResult.GetValue(outputOption)!;
            var oraclesRaw = parseResult.GetValue(oraclesOption) ?? "all";
            if (!TryParseBenchmarkOracles(oraclesRaw, out var selection, out var error))
            {
                Console.Error.WriteLine(error);
                Environment.ExitCode = 2;
                return;
            }

            try
            {
                var report = RunReferencePerformance(
                    fixtureFile.FullName,
                    output.FullName,
                    Math.Max(1, parseResult.GetValue(runsOption)),
                    selection,
                    Math.Max(1_000, parseResult.GetValue(timeoutOption)),
                    parseResult.GetValue(includeHeavyOption),
                    parseResult.GetValue(baselineOption)?.FullName,
                    parseResult.GetValue(maxTimeRatioOption),
                    parseResult.GetValue(maxRssRatioOption));
                WriteReferencePerformanceReport(report, output.FullName);
                Console.WriteLine($"Reference performance: {report.runs.Count} fresh runs; gate {(report.regressionGate.passed ? "PASS" : "FAIL")}");
                Console.WriteLine("Report: " + Path.Combine(Path.GetFullPath(output.FullName), "reference-performance.md"));
                if (parseResult.GetValue(failOption) && !report.regressionGate.passed)
                    Environment.ExitCode = 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Error: " + ex.Message);
                Environment.ExitCode = 1;
            }
        });
        return command;
    }

    internal static ReferencePerformanceReport RunReferencePerformance(
        string fixtureManifestPath, string outputDir, int runs, BenchmarkOracleSelection oracleSelection,
        int timeoutMs, bool includeHeavy, string? baselinePath, double maxTimeRatio, double maxRssRatio)
    {
        var manifest = JsonSerializer.Deserialize<ReferencePerformanceManifest>(File.ReadAllText(fixtureManifestPath), BenchmarkJsonOptions)
            ?? throw new InvalidDataException("Fixture manifest is empty or invalid.");
        if (manifest.schemaVersion != 1) throw new InvalidDataException($"Unsupported fixture manifest schema {manifest.schemaVersion}.");
        var root = FindRepositoryRoot() ?? Directory.GetCurrentDirectory();
        var fixtures = manifest.fixtures.Where(f => includeHeavy || !f.heavy).ToArray();
        var oracles = ResolveBenchmarkOracles(oracleSelection);
        var results = new List<ReferencePerformanceRun>();

        foreach (var fixture in fixtures)
        {
            var path = Path.IsPathRooted(fixture.path) ? fixture.path : Path.Combine(root, fixture.path);
            if (!File.Exists(path))
            {
                results.Add(new ReferencePerformanceRun { fixture = fixture.id, status = "MISSING_FIXTURE", path = path, pageNumber = fixture.page, dpi = fixture.dpi });
                continue;
            }
            for (var run = 1; run <= runs; run++)
                results.Add(MeasureReferencePerformanceFixture(fixture, path, run, timeoutMs, oracles));
        }

        var baseline = LoadReferencePerformanceBaseline(baselinePath);
        var gate = EvaluateReferencePerformanceGate(results, baseline, maxTimeRatio, maxRssRatio);
        return new ReferencePerformanceReport
        {
            schemaVersion = 1,
            generatedUtc = DateTimeOffset.UtcNow.ToString("O"),
            issues = new[] { "#1207", "#1208" },
            methodology = "Each result launches a fresh excise CLI or external renderer process. No timed render result is cached; PNGs exist only transiently for same-run fidelity comparison. CPU/RSS are OS-reported process figures and may be null on platforms that do not expose them after exit.",
            configuration = new ReferencePerformanceConfiguration
            {
                fixtureManifest = Path.GetFullPath(fixtureManifestPath), runs = runs, timeoutMs = timeoutMs,
                includeHeavy = includeHeavy, selectedOracles = oracles.Select(o => o.Name).ToArray(),
                baseline = baselinePath, maxExciseTimeRatio = maxTimeRatio, maxExciseRssRatio = maxRssRatio,
            },
            runs = results,
            regressionGate = gate,
        };
    }

    private static ReferencePerformanceRun MeasureReferencePerformanceFixture(
        ReferencePerformanceFixture fixture, string path, int run, int timeoutMs, IReadOnlyList<BenchmarkOracle> oracles)
    {
        try
        {
            using var doc = PdfDocument.Open(path);
            using var exciseBitmap = new SkiaRenderer().RenderPage(doc.GetPage(fixture.page), new RenderOptions { Dpi = fixture.dpi });
            var cli = BenchmarkCliRender(path, fixture.page, fixture.dpi, timeoutMs, exciseBitmap);
            var references = oracles.Select(o => BenchmarkReference(o, path, fixture.page, fixture.dpi, timeoutMs, exciseBitmap)).ToArray();
            return new ReferencePerformanceRun
            {
                fixture = fixture.id, path = path, pageNumber = fixture.page, dpi = fixture.dpi, run = run,
                status = cli.status == "OK" ? "OK" : "EXCISE_" + cli.status,
                exciseCli = cli, references = references,
            };
        }
        catch (Exception ex)
        {
            return new ReferencePerformanceRun { fixture = fixture.id, path = path, pageNumber = fixture.page, dpi = fixture.dpi, run = run, status = "EXCISE_ERROR", error = ex.Message };
        }
    }

    private static ReferencePerformanceReport? LoadReferencePerformanceBaseline(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        return JsonSerializer.Deserialize<ReferencePerformanceReport>(File.ReadAllText(path), BenchmarkJsonOptions);
    }

    private static ReferencePerformanceGate EvaluateReferencePerformanceGate(
        IReadOnlyList<ReferencePerformanceRun> current, ReferencePerformanceReport? baseline, double maxTimeRatio, double maxRssRatio)
    {
        var checks = new List<ReferencePerformanceGateCheck>();
        if (baseline is null) return new ReferencePerformanceGate { passed = true, checks = checks, note = "No baseline supplied; report captured for future comparison." };
        foreach (var fixture in current.Select(r => r.fixture).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
        {
            var now = Median(current.Where(r => r.fixture == fixture).Select(r => r.exciseCli?.elapsedMs));
            var before = Median(baseline.runs.Where(r => r.fixture == fixture).Select(r => r.exciseCli?.elapsedMs));
            AddRatioCheck(checks, fixture + ".excise-cli.wall", now, before, maxTimeRatio, "ratio");
            var nowRss = Median(current.Where(r => r.fixture == fixture).Select(r => r.exciseCli?.peakWorkingSetBytes));
            var beforeRss = Median(baseline.runs.Where(r => r.fixture == fixture).Select(r => r.exciseCli?.peakWorkingSetBytes));
            AddRatioCheck(checks, fixture + ".excise-cli.rss", nowRss, beforeRss, maxRssRatio, "ratio");
        }
        return new ReferencePerformanceGate { passed = checks.All(c => c.passed), checks = checks, note = "Only matched fixture measurements with available baseline metrics are gated." };
    }

    private static void AddRatioCheck(List<ReferencePerformanceGateCheck> checks, string name, long? current, long? baseline, double threshold, string unit)
    {
        if (!current.HasValue || !baseline.HasValue || baseline.Value <= 0) return;
        var ratio = current.Value / (double)baseline.Value;
        checks.Add(new ReferencePerformanceGateCheck { name = name, actual = ratio, threshold = threshold, passed = ratio <= threshold, unit = unit });
    }

    private static long? Median(IEnumerable<long?> values)
    {
        var sorted = values.Where(v => v.HasValue).Select(v => v!.Value).OrderBy(v => v).ToArray();
        return sorted.Length == 0 ? null : sorted[(sorted.Length - 1) / 2];
    }

    private static void WriteReferencePerformanceReport(ReferencePerformanceReport report, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        File.WriteAllText(Path.Combine(outputDir, "reference-performance.json"), JsonSerializer.Serialize(report, BenchmarkJsonOptions));
        var sb = new StringBuilder("# Reference Renderer Performance\n\n");
        sb.AppendLine("- " + report.methodology);
        sb.AppendLine("- Gate: `" + (report.regressionGate.passed ? "PASS" : "FAIL") + "` — " + report.regressionGate.note);
        sb.AppendLine();
        sb.AppendLine("| Fixture | Run | Renderer | Status | Wall ms | CPU ms | Peak RSS MiB | Fidelity |\n|---|---:|---|---|---:|---:|---:|---|");
        foreach (var row in report.runs)
        {
            if (row.status != "OK")
            {
                sb.AppendLine($"| `{row.fixture}` | {row.run} | excise-cli | {row.status} |  |  |  | {row.error ?? ""} |");
                continue;
            }
            AppendPerformanceRow(sb, row.fixture, row.run, row.exciseCli);
            foreach (var reference in row.references) AppendPerformanceRow(sb, row.fixture, row.run, reference);
        }
        sb.AppendLine();
        sb.AppendLine("## Regression checks\n\n| Check | Actual | Threshold | Result |\n|---|---:|---:|---|");
        foreach (var check in report.regressionGate.checks)
            sb.AppendLine($"| {check.name} | {check.actual:0.###} | {check.threshold:0.###} | {(check.passed ? "PASS" : "FAIL")} |");
        File.WriteAllText(Path.Combine(outputDir, "reference-performance.md"), sb.ToString());
    }

    private static void AppendPerformanceRow(StringBuilder sb, string fixture, int run, BenchmarkCliRenderResult? result)
    {
        if (result is null) return;
        sb.AppendLine($"| `{fixture}` | {run} | {result.name} | {result.status} | {result.elapsedMs?.ToString() ?? ""} | {result.cpuMs?.ToString() ?? ""} | {ToMib(result.peakWorkingSetBytes)} | {result.pass?.ToString() ?? ""} |");
    }

    private static void AppendPerformanceRow(StringBuilder sb, string fixture, int run, BenchmarkReferenceResult result)
        => sb.AppendLine($"| `{fixture}` | {run} | {result.name} | {result.status} | {result.elapsedMs?.ToString() ?? ""} | {result.cpuMs?.ToString() ?? ""} | {ToMib(result.peakWorkingSetBytes)} | {result.pass?.ToString() ?? ""} |");

    private static string ToMib(long? bytes) => bytes.HasValue ? (bytes.Value / 1024d / 1024d).ToString("0.0", CultureInfo.InvariantCulture) : "";

    internal sealed class ReferencePerformanceManifest { public int schemaVersion { get; set; } public IReadOnlyList<ReferencePerformanceFixture> fixtures { get; set; } = Array.Empty<ReferencePerformanceFixture>(); }
    internal sealed class ReferencePerformanceFixture { public string id { get; set; } = ""; public string path { get; set; } = ""; public int page { get; set; } = 1; public int dpi { get; set; } = 150; public bool heavy { get; set; } }
    internal sealed class ReferencePerformanceReport { public int schemaVersion { get; set; } public string generatedUtc { get; set; } = ""; public string[] issues { get; set; } = Array.Empty<string>(); public string methodology { get; set; } = ""; public ReferencePerformanceConfiguration configuration { get; set; } = new(); public IReadOnlyList<ReferencePerformanceRun> runs { get; set; } = Array.Empty<ReferencePerformanceRun>(); public ReferencePerformanceGate regressionGate { get; set; } = new(); }
    internal sealed class ReferencePerformanceConfiguration { public string fixtureManifest { get; set; } = ""; public int runs { get; set; } public int timeoutMs { get; set; } public bool includeHeavy { get; set; } public IReadOnlyList<string> selectedOracles { get; set; } = Array.Empty<string>(); public string? baseline { get; set; } public double maxExciseTimeRatio { get; set; } public double maxExciseRssRatio { get; set; } }
    internal sealed class ReferencePerformanceRun { public string fixture { get; set; } = ""; public string path { get; set; } = ""; public int pageNumber { get; set; } public int dpi { get; set; } public int run { get; set; } public string status { get; set; } = ""; public string? error { get; set; } public BenchmarkCliRenderResult? exciseCli { get; set; } public IReadOnlyList<BenchmarkReferenceResult> references { get; set; } = Array.Empty<BenchmarkReferenceResult>(); }
    internal sealed class ReferencePerformanceGate { public bool passed { get; set; } public IReadOnlyList<ReferencePerformanceGateCheck> checks { get; set; } = Array.Empty<ReferencePerformanceGateCheck>(); public string note { get; set; } = ""; }
    internal sealed class ReferencePerformanceGateCheck { public string name { get; set; } = ""; public double actual { get; set; } public double threshold { get; set; } public bool passed { get; set; } public string unit { get; set; } = ""; }
}
