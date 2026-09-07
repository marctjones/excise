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
        // #1386 — the dev loop. A full pass is 12 fixtures x 3 runs x 5 oracles, ~5 minutes;
        // nobody iterates on a render optimisation at that cadence, so in practice the bench
        // gets run once at the end and is not used to steer the work at all. Narrowing to one
        // fixture and one oracle turns it into a ~10 second loop:
        //     --fixture irs-w9-form --runs 3 --oracles mutool
        // A filtered run is deliberately still gated the same way: the ratio is per fixture,
        // so the checks that CAN be computed are exactly as meaningful, there are just fewer.
        var fixtureFilterOption = new Option<string[]>("--fixture")
        {
            Description = "Only run these fixture names (repeatable). Default: every fixture in the manifest.",
            AllowMultipleArgumentsPerToken = true,
            DefaultValueFactory = _ => Array.Empty<string>(),
        };
        var cohortFilterOption = new Option<string[]>("--cohort")
        {
            Description = "Only run fixtures in these cohorts: typical, tail-flat, tail-scaling, tail-colour (repeatable).",
            AllowMultipleArgumentsPerToken = true,
            DefaultValueFactory = _ => Array.Empty<string>(),
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
            includeHeavyOption, fixtureFilterOption, cohortFilterOption, baselineOption, maxTimeRatioOption, maxRssRatioOption, failOption,
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
                    parseResult.GetValue(fixtureFilterOption) ?? Array.Empty<string>(),
                    parseResult.GetValue(cohortFilterOption) ?? Array.Empty<string>(),
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
        int timeoutMs, bool includeHeavy, IReadOnlyList<string> fixtureFilter, IReadOnlyList<string> cohortFilter,
        string? baselinePath, double maxTimeRatio, double maxRssRatio)
    {
        var manifest = JsonSerializer.Deserialize<ReferencePerformanceManifest>(File.ReadAllText(fixtureManifestPath), BenchmarkJsonOptions)
            ?? throw new InvalidDataException("Fixture manifest is empty or invalid.");
        if (manifest.schemaVersion != 1) throw new InvalidDataException($"Unsupported fixture manifest schema {manifest.schemaVersion}.");
        var root = FindRepositoryRoot() ?? Directory.GetCurrentDirectory();
        var fixtures = manifest.fixtures.Where(f => includeHeavy || !f.heavy).ToArray();
        if (fixtureFilter.Count > 0)
        {
            // A name that matches nothing is a TYPO, not "run everything" — silently running
            // the full 5-minute set when the user asked for one fixture wastes the loop this
            // flag exists to create, and silently running NOTHING would report a vacuous pass.
            var unknown = fixtureFilter.Where(n => !manifest.fixtures.Any(f => string.Equals(f.id, n, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (unknown.Length > 0)
                throw new InvalidDataException(
                    $"No fixture named {string.Join(", ", unknown.Select(u => "'" + u + "'"))} in {Path.GetFileName(fixtureManifestPath)}. "
                    + $"Available: {string.Join(", ", manifest.fixtures.Select(f => f.id))}");
            fixtures = fixtures.Where(f => fixtureFilter.Contains(f.id, StringComparer.OrdinalIgnoreCase)).ToArray();
        }
        if (cohortFilter.Count > 0)
        {
            var known = manifest.fixtures.Select(f => f.cohort).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var unknown = cohortFilter.Where(c => !known.Contains(c, StringComparer.OrdinalIgnoreCase)).ToArray();
            if (unknown.Length > 0)
                throw new InvalidDataException(
                    $"No cohort named {string.Join(", ", unknown.Select(u => "'" + u + "'"))}. Available: {string.Join(", ", known)}");
            fixtures = fixtures.Where(f => cohortFilter.Contains(f.cohort, StringComparer.OrdinalIgnoreCase)).ToArray();
        }
        if (fixtures.Length == 0)
            throw new InvalidDataException(
                "Fixture selection matched nothing to run. A bench run with no fixtures would report a vacuous pass, so this is an error.");
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
                includeHeavy = includeHeavy,
                // #1389 — WHICH .NET produced these numbers. runtimeMode (jit/aot) is not
                // enough: two SDKs can report the same runtime version and still generate
                // different native code and link different libraries. Homebrew's build of
                // 10.0.11 linked Apple's zlib where Microsoft's links the vendored zlib-ng,
                // which is a real difference in the compression path a PDF writer exercises.
                // Record it so a baseline says what produced it instead of implying every
                // .NET 10.0.11 is the same .NET 10.0.11.
                runtimeDescription = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? "(unset; resolved from PATH)",
                fixtureFilter = fixtureFilter, cohortFilter = cohortFilter,
                selectedOracles = oracles.Select(o => o.Name).ToArray(),
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
                cohort = fixture.cohort,
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

    // internal, not private: Excise.Cli.Tests pins the two refuse-to-compare rules below
    // without launching a single render (#1389).
    internal static ReferencePerformanceGate EvaluateReferencePerformanceGate(
        IReadOnlyList<ReferencePerformanceRun> current, ReferencePerformanceReport? baseline, double maxTimeRatio, double maxRssRatio)
    {
        var checks = new List<ReferencePerformanceGateCheck>();
        if (baseline is null) return new ReferencePerformanceGate { passed = true, checks = checks, note = "No baseline supplied; report captured for future comparison." };

        // #1389 — REFUSE TO COMPARE ACROSS CODEGEN MODES, rather than comparing and being
        // wrong. Native AOT and the JIT generate different code from the same source: AOT
        // has no tiered recompilation and no dynamic PGO, so on a long render the JIT can
        // reach a faster steady state while on a short one AOT avoids the JIT cost
        // entirely. A renderMs from one is not the same measurement as a renderMs from the
        // other, and every ratio below would silently fold that in.
        //
        // This fails LOUDLY instead of scoring, because the alternative is a verdict whose
        // meaning depends on whether somebody happened to run scripts/build-aot-cli.sh that
        // week — a hidden variable in a gate, which is exactly the failure mode a bench is
        // supposed to remove. Re-baseline after switching modes; do not reinterpret.
        // Restrict the ratio's denominator to the oracles both sides measured (see
        // ComparableOracles). A dev-loop run with --oracles mutool then compares mutool to
        // mutool rather than mutool to the median of five.
        var comparableOracles = ComparableOracles(current, baseline.runs);
        if (comparableOracles.Count == 0)
            return new ReferencePerformanceGate
            {
                passed = false,
                checks = checks,
                note = "No oracle produced a timing in BOTH this run and the baseline, so excise cannot be "
                     + "compared to anything measured under matching conditions. Run with an oracle the "
                     + "baseline also used, or re-record the baseline.",
            };

        var nowMode = DominantRuntimeMode(current);
        var beforeMode = DominantRuntimeMode(baseline.runs);
        if (nowMode is not null && beforeMode is not null && !string.Equals(nowMode, beforeMode, StringComparison.Ordinal))
        {
            return new ReferencePerformanceGate
            {
                passed = false,
                checks = checks,
                note = $"MODE MISMATCH — this run used the '{nowMode}' CLI, the baseline was recorded with '{beforeMode}'. "
                     + "Native AOT and JIT generate different code, so these render times are not comparable and no "
                     + "ratio below would mean anything. Re-record the baseline with the binary you intend to measure "
                     + "(scripts/build-aot-cli.sh builds the AOT one), or point the bench back at the other binary.",
            };
        }

        foreach (var fixture in current.Select(r => r.fixture).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
        {
            // GATED: excise's in-process render time RELATIVE TO THE ORACLES IN THE SAME
            // RUN, compared against that same relative figure in the baseline.
            //
            // Absolute milliseconds are not gateable here and the numbers say so. Two
            // 3-run passes over identical code, minutes apart on an unloaded-looking
            // machine, moved EVERY fixture in the same direction by 1.27x-2.24x — the
            // machine, not the code. But the oracles moved with it, so the excise/oracle
            // ratio stayed put: tail-colour 1.5x -> 1.8x, tail-flat 2.0x -> 2.1x,
            // tail-scaling 7.5x -> 6.5x, typical 0.5x -> 0.6x. Roughly +-20% on the ratio
            // against +-50% on the absolutes.
            //
            // This is the no-self-oracle rule applied to speed: judge against tools
            // measured under the same conditions, not against our own past stopwatch.
            var nowRatio = ExciseToOracleRatio(current, fixture, comparableOracles);
            var beforeRatio = ExciseToOracleRatio(baseline.runs, fixture, comparableOracles);
            AddRatioCheck(checks, fixture + ".excise-cli.render-vs-oracles", nowRatio, beforeRatio, maxTimeRatio, "ratio", gated: true);

            // REPORTED, NOT GATED: the absolute render time, so a real speed-up is still
            // visible in the report even though it cannot be gated on.
            var nowRender = MedianOf(current.Where(r => r.fixture == fixture).Select(r => r.exciseCli?.renderMs));
            var beforeRender = MedianOf(baseline.runs.Where(r => r.fixture == fixture).Select(r => r.exciseCli?.renderMs));
            AddRatioCheck(checks, fixture + ".excise-cli.render-abs", nowRender, beforeRender, maxTimeRatio, "ratio", gated: false);

            // REPORTED, NOT GATED: whole-process wall clock carries ~60-95 ms of .NET
            // startup and JIT plus whatever else the machine is doing. On the same
            // fixture whose render time moved under 1%, wall moved 456 -> 715 ms and
            // tripped a 1.5x threshold — a false regression. A gate that cries wolf is a
            // gate people stop reading, so wall and RSS are recorded for context and the
            // pass/fail decision rests on the stable signal.
            var now = Median(current.Where(r => r.fixture == fixture).Select(r => r.exciseCli?.elapsedMs));
            var before = Median(baseline.runs.Where(r => r.fixture == fixture).Select(r => r.exciseCli?.elapsedMs));
            AddRatioCheck(checks, fixture + ".excise-cli.wall", now, before, maxTimeRatio, "ratio", gated: false);
            var nowRss = Median(current.Where(r => r.fixture == fixture).Select(r => r.exciseCli?.peakWorkingSetBytes));
            var beforeRss = Median(baseline.runs.Where(r => r.fixture == fixture).Select(r => r.exciseCli?.peakWorkingSetBytes));
            AddRatioCheck(checks, fixture + ".excise-cli.rss", nowRss, beforeRss, maxRssRatio, "ratio", gated: false);
        }
        return new ReferencePerformanceGate
        {
            passed = checks.Where(c => c.gated).All(c => c.passed),
            checks = checks,
            note = "Gated on excise in-process render ms RELATIVE TO THE ORACLES IN THE SAME RUN. "
                 + "Absolute ms, wall and RSS are reported but not gated: on unchanged code two passes "
                 + "minutes apart moved render-abs 0.88x-3.26x (five false regressions) while "
                 + "render-vs-oracles stayed 0.68x-1.33x (#1387).",
        };
    }

    /// <summary>
    /// The codegen mode these runs were produced by, or null when no run reported one (a
    /// binary predating the field). A mixed set returns "mixed", which will never equal the
    /// baseline's mode and so trips the mismatch guard — the right outcome, because a run
    /// that switched binaries midway is not a measurement of either.
    /// </summary>
    private static string? DominantRuntimeMode(IEnumerable<ReferencePerformanceRun> runs)
    {
        var modes = runs.Select(r => r.exciseCli?.runtimeMode)
                        .Where(m => !string.IsNullOrEmpty(m))
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
        return modes.Length switch { 0 => null, 1 => modes[0], _ => "mixed" };
    }

    private static void AddRatioCheck(List<ReferencePerformanceGateCheck> checks, string name, long? current, long? baseline, double threshold, string unit, bool gated = true)
        => AddRatioCheck(checks, name, (double?)current, (double?)baseline, threshold, unit, gated);

    private static void AddRatioCheck(List<ReferencePerformanceGateCheck> checks, string name, double? current, double? baseline, double threshold, string unit, bool gated = true)
    {
        if (!current.HasValue || !baseline.HasValue || baseline.Value <= 0) return;
        var ratio = current.Value / baseline.Value;
        checks.Add(new ReferencePerformanceGateCheck { name = name, actual = ratio, threshold = threshold, passed = ratio <= threshold, unit = unit, gated = gated });
    }

    /// <summary>
    /// excise's median in-process render ms divided by the median oracle wall ms measured
    /// in the SAME run. Self-normalising: when the machine slows, both move together.
    /// Null when either side has no usable measurement, which makes the check skip rather
    /// than invent a verdict.
    /// </summary>
    private static double? ExciseToOracleRatio(
        IEnumerable<ReferencePerformanceRun> runs, string fixture, IReadOnlySet<string>? onlyOracles = null)
    {
        var forFixture = runs.Where(r => r.fixture == fixture && r.status == "OK").ToArray();
        var render = MedianOf(forFixture.Select(r => r.exciseCli?.renderMs));
        var oracle = MedianOf(forFixture
            .SelectMany(r => r.references)
            .Where(x => x.status == "OK" && x.elapsedMs is not null)
            .Where(x => onlyOracles is null || onlyOracles.Contains(x.name))
            .Select(x => (double?)x.elapsedMs!.Value));
        return render is > 0 && oracle is > 0 ? render / oracle : null;
    }

    /// <summary>
    /// The oracles that actually produced a timing on BOTH sides, so the ratio's denominator
    /// is the same population in each.
    ///
    /// <para>Without this, narrowing the oracle set silently rewrites the metric. Measured:
    /// a <c>--oracles mutool</c> dev-loop run of <c>irs-w9-form</c> reported
    /// <c>render-vs-oracles</c> 3.489x against a baseline recorded with all five — a
    /// three-and-a-half-fold "regression" on a fixture whose absolute render time had moved
    /// 0.981x. mutool is the fastest oracle, so dividing by it alone instead of by the median
    /// of five produced the entire apparent change. Intersecting makes a filtered run
    /// comparable instead of merely fast, which is the difference between a dev loop and a
    /// misleading one.</para>
    /// </summary>
    private static IReadOnlySet<string> ComparableOracles(
        IEnumerable<ReferencePerformanceRun> current, IEnumerable<ReferencePerformanceRun> baseline)
    {
        static HashSet<string> Names(IEnumerable<ReferencePerformanceRun> runs) =>
            runs.SelectMany(r => r.references)
                .Where(x => x.status == "OK" && x.elapsedMs is not null)
                .Select(x => x.name)
                .ToHashSet(StringComparer.Ordinal);
        var shared = Names(current);
        shared.IntersectWith(Names(baseline));
        return shared;
    }

    private static double? MedianOf(IEnumerable<double?> values)
    {
        var sorted = values.Where(v => v.HasValue).Select(v => v!.Value).OrderBy(v => v).ToArray();
        return sorted.Length == 0 ? null : sorted[(sorted.Length - 1) / 2];
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
        // State the codegen mode on the face of the report. Every render ms below depends on
        // it, and a reader comparing two reports has no other way to know they were produced
        // by differently-compiled binaries (#1389).
        var mode = DominantRuntimeMode(report.runs);
        sb.AppendLine("- .NET: `" + report.configuration.runtimeDescription + "` from `"
            + report.configuration.dotnetRoot + "`");
        sb.AppendLine("- excise CLI codegen: `" + (mode ?? "unknown (binary predates runtimeMode)") + "`"
            + (mode == "aot"
                ? " — Native AOT, built by `scripts/build-aot-cli.sh`. No tiered JIT and no dynamic PGO."
                : mode == "jit" ? " — JIT/ReadyToRun, the default `dotnet build` output." : ""));
        sb.AppendLine("- Gate: `" + (report.regressionGate.passed ? "PASS" : "FAIL") + "` — " + report.regressionGate.note);
        sb.AppendLine();
        sb.AppendLine("| Fixture | Run | Renderer | Status | Wall ms | Render ms | Startup ms | CPU ms | Peak RSS MiB | Fidelity |");
        sb.AppendLine("|---|---:|---|---|---:|---:|---:|---:|---:|---|");
        foreach (var row in report.runs)
        {
            if (row.status != "OK")
            {
                sb.AppendLine($"| `{row.fixture}` | {row.run} | excise-cli | {row.status} |  |  |  |  |  | {row.error ?? ""} |");
                continue;
            }
            AppendPerformanceRow(sb, row.fixture, row.run, row.exciseCli);
            foreach (var reference in row.references) AppendPerformanceRow(sb, row.fixture, row.run, reference);
        }
        sb.AppendLine();
        AppendCohortSummary(sb, report);
        sb.AppendLine("## Regression checks\n\n| Check | Actual | Threshold | Result |\n|---|---:|---:|---|");
        foreach (var check in report.regressionGate.checks)
            sb.AppendLine($"| {check.name} | {check.actual:0.###} | {check.threshold:0.###} | {(check.gated ? (check.passed ? "PASS" : "FAIL") : (check.passed ? "ok (not gated)" : "over (not gated)"))} |");
        File.WriteAllText(Path.Combine(outputDir, "reference-performance.md"), sb.ToString());
    }

    private static void AppendPerformanceRow(StringBuilder sb, string fixture, int run, BenchmarkCliRenderResult? result)
    {
        if (result is null) return;
        sb.AppendLine($"| `{fixture}` | {run} | {result.name} | {result.status} | {result.elapsedMs?.ToString() ?? ""} | {Fmt(result.renderMs)} | {Fmt(result.startupOverheadMs)} | {result.cpuMs?.ToString() ?? ""} | {ToMib(result.peakWorkingSetBytes)} | {result.pass?.ToString() ?? ""} |");
    }

    // A reference renderer has no phase breakdown to report: it is a native binary we
    // time from the outside. The blank cells are the honest answer, not a gap.
    private static void AppendPerformanceRow(StringBuilder sb, string fixture, int run, BenchmarkReferenceResult result)
        => sb.AppendLine($"| `{fixture}` | {run} | {result.name} | {result.status} | {result.elapsedMs?.ToString() ?? ""} |  |  | {result.cpuMs?.ToString() ?? ""} | {ToMib(result.peakWorkingSetBytes)} | {result.pass?.ToString() ?? ""} |");

    /// <summary>
    /// Median excise render-ms against the median oracle wall-ms, grouped by cohort
    /// (#1386). The tail and typical cohorts answer different questions and a single
    /// median over all fixtures answers neither: 1.4% of corpus pages carry 58% of render
    /// time, so a tail-weighted figure hides a regression in the common case and a
    /// typical-weighted one hides the tail.
    /// </summary>
    private static void AppendCohortSummary(StringBuilder sb, ReferencePerformanceReport report)
    {
        var byCohort = report.runs
            .Where(r => r.status == "OK" && r.exciseCli?.renderMs is not null)
            .GroupBy(r => string.IsNullOrWhiteSpace(r.cohort) ? "typical" : r.cohort)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToArray();
        if (byCohort.Length == 0) return;

        sb.AppendLine("## By cohort");
        sb.AppendLine();
        sb.AppendLine("`render ms` is excise's in-process raster time, reported by the CLI itself (#1387).");
        sb.AppendLine("`wall ms` additionally includes .NET startup and JIT, which a native reference does not");
        sb.AppendLine("pay. Ratios are against the MEDIAN oracle, never the fastest.");
        sb.AppendLine();
        sb.AppendLine("| Cohort | Fixtures | excise render ms | excise wall ms | oracle wall ms | render/oracle | wall/oracle |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
        foreach (var group in byCohort)
        {
            var render = Median(group.Select(r => r.exciseCli!.renderMs!.Value));
            var wall = Median(group.Select(r => (double)(r.exciseCli!.elapsedMs ?? 0)));
            var oracle = Median(group.SelectMany(r => r.references)
                .Where(x => x.status == "OK" && x.elapsedMs is not null)
                .Select(x => (double)x.elapsedMs!.Value));
            string Ratio(double? value) => value is > 0 && oracle is > 0
                ? (value.Value / oracle!.Value).ToString("0.0", CultureInfo.InvariantCulture) + "x"
                : "";
            sb.AppendLine($"| {group.Key} | {group.Select(g => g.fixture).Distinct().Count()} | {Fmt(render)} | {Fmt(wall)} | {Fmt(oracle)} | {Ratio(render)} | {Ratio(wall)} |");
        }
        sb.AppendLine();
    }

    private static double? Median(IEnumerable<double> values)
    {
        var sorted = values.Where(v => v > 0).OrderBy(v => v).ToArray();
        if (sorted.Length == 0) return null;
        return sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2.0;
    }

    private static string Fmt(double? value)
        => value.HasValue ? value.Value.ToString("0", CultureInfo.InvariantCulture) : "";

    private static string ToMib(long? bytes) => bytes.HasValue ? (bytes.Value / 1024d / 1024d).ToString("0.0", CultureInfo.InvariantCulture) : "";

    internal sealed class ReferencePerformanceManifest { public int schemaVersion { get; set; } public IReadOnlyList<ReferencePerformanceFixture> fixtures { get; set; } = Array.Empty<ReferencePerformanceFixture>(); }
    internal sealed class ReferencePerformanceFixture { public string id { get; set; } = ""; public string path { get; set; } = ""; public int page { get; set; } = 1; public int dpi { get; set; } = 150; public bool heavy { get; set; } public string cohort { get; set; } = "typical"; public string why { get; set; } = ""; }
    internal sealed class ReferencePerformanceReport { public int schemaVersion { get; set; } public string generatedUtc { get; set; } = ""; public string[] issues { get; set; } = Array.Empty<string>(); public string methodology { get; set; } = ""; public ReferencePerformanceConfiguration configuration { get; set; } = new(); public IReadOnlyList<ReferencePerformanceRun> runs { get; set; } = Array.Empty<ReferencePerformanceRun>(); public ReferencePerformanceGate regressionGate { get; set; } = new(); }
    internal sealed class ReferencePerformanceConfiguration { public string fixtureManifest { get; set; } = ""; public int runs { get; set; } public int timeoutMs { get; set; } public bool includeHeavy { get; set; } public string runtimeDescription { get; set; } = ""; public string dotnetRoot { get; set; } = ""; public IReadOnlyList<string> fixtureFilter { get; set; } = Array.Empty<string>(); public IReadOnlyList<string> cohortFilter { get; set; } = Array.Empty<string>(); public IReadOnlyList<string> selectedOracles { get; set; } = Array.Empty<string>(); public string? baseline { get; set; } public double maxExciseTimeRatio { get; set; } public double maxExciseRssRatio { get; set; } }
    internal sealed class ReferencePerformanceRun { public string fixture { get; set; } = ""; public string path { get; set; } = ""; public int pageNumber { get; set; } public int dpi { get; set; } public int run { get; set; } public string cohort { get; set; } = "typical"; public string status { get; set; } = ""; public string? error { get; set; } public BenchmarkCliRenderResult? exciseCli { get; set; } public IReadOnlyList<BenchmarkReferenceResult> references { get; set; } = Array.Empty<BenchmarkReferenceResult>(); }
    internal sealed class ReferencePerformanceGate { public bool passed { get; set; } public IReadOnlyList<ReferencePerformanceGateCheck> checks { get; set; } = Array.Empty<ReferencePerformanceGateCheck>(); public string note { get; set; } = ""; }
    internal sealed class ReferencePerformanceGateCheck { public string name { get; set; } = ""; public double actual { get; set; } public double threshold { get; set; } public bool passed { get; set; } public string unit { get; set; } = ""; /* gated=false: reported for context only, excluded from the pass/fail decision (#1387). */ public bool gated { get; set; } = true; }
}
