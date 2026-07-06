using System.Text.Json;
using AwesomeAssertions;
using Xunit;

using RenderProgram = Pdfe.RenderTools.Program;

namespace Pdfe.Cli.Tests;

public class BenchmarkSuiteTests
{
    [Fact]
    public void TryParseBenchmarkOracles_AcceptsAliasesAndRejectsUnknownTools()
    {
        RenderProgram.TryParseBenchmarkOracles(
                "mutool,poppler,gs,pdfbox,pdfium_test",
                out var selection,
                out var error)
            .Should().BeTrue();
        error.Should().BeNull();
        selection.Should().HaveFlag(RenderProgram.BenchmarkOracleSelection.Mutool);
        selection.Should().HaveFlag(RenderProgram.BenchmarkOracleSelection.Pdftocairo);
        selection.Should().HaveFlag(RenderProgram.BenchmarkOracleSelection.Ghostscript);
        selection.Should().HaveFlag(RenderProgram.BenchmarkOracleSelection.PdfBox);
        selection.Should().HaveFlag(RenderProgram.BenchmarkOracleSelection.Pdfium);

        RenderProgram.TryParseBenchmarkOracles("missingtool", out _, out error)
            .Should().BeFalse();
        error.Should().Contain("Bad --oracles");
    }

    [Fact]
    public async Task BenchmarkSuite_WritesJsonCsvMarkdownAndPassesSyntheticGate()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pdfe-benchmark-suite-" + Guid.NewGuid().ToString("N"));
        try
        {
            var exitCode = await RenderProgram.RunAsync(new[]
            {
                "benchmark-suite",
                "--oracles", "none",
                "--page-limit", "1",
                "--output-dir", dir,
                "--include-cli-render",
                "--fail-on-regression",
            });

            exitCode.Should().Be(0);
            File.Exists(Path.Combine(dir, "benchmark-report.json")).Should().BeTrue();
            File.Exists(Path.Combine(dir, "benchmark-pages.csv")).Should().BeTrue();
            File.Exists(Path.Combine(dir, "benchmark-hotpaths.json")).Should().BeTrue();
            File.Exists(Path.Combine(dir, "benchmark-report.md")).Should().BeTrue();

            using var stream = File.OpenRead(Path.Combine(dir, "benchmark-report.json"));
            var report = JsonSerializer.Deserialize<RenderProgram.BenchmarkSuiteReport>(stream);
            report.Should().NotBeNull();
            report!.schemaVersion.Should().Be(1);
            report.issues.Should().Contain(new[] { "#344", "#357", "#536", "#596", "#597", "#602" });
            report.configuration.includeCliRender.Should().BeTrue();
            report.summary.pageCount.Should().Be(1);
            report.summary.statusCounts.Should().ContainKey("NO_REFERENCES");
            report.summary.cliRenderPassRate.Should().Be(1);
            report.hotPaths.Should().Contain(h => h.name == "renderer.page-render" && h.scope == "pdfe-owned");
            report.hotPaths.Should().Contain(h => h.name == "parser.document-open" && h.issueRefs.Contains("#597", StringComparison.Ordinal));
            report.hotPaths.Should().Contain(h =>
                h.name == "cli.render-page-subprocess" &&
                h.workloadId == "cli.render-page" &&
                h.route == "cli" &&
                h.regressionPolicy == "gate");
            report.pages.Single().cliRender.Should().NotBeNull();
            report.pages.Single().cliRender!.status.Should().Be("OK");
            report.pages.Single().cliRender!.pass.Should().BeTrue();
            report.regressionGate.checks.Should().Contain(c => c.name == "pdfe-cli-render-pass-rate" && c.passed);
            report.redactionCompleteness.status.Should().Be("PASS");
            report.regressionGate.passed.Should().BeTrue();
            report.tools.Should().Contain(t => t.name == "pdfe" && t.kind == "in-process" && t.available);
            report.tools.Should().Contain(t => t.name == "pdfe-cli" && t.kind == "external-subprocess" && t.available && t.selected);
            report.licenseIsolation.policy.Should().Contain("external CLI subprocesses");
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task BenchmarkSuite_FailOnRegressionReturnsNonZeroExitCode()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pdfe-benchmark-suite-fail-" + Guid.NewGuid().ToString("N"));
        try
        {
            var exitCode = await RenderProgram.RunAsync(new[]
            {
                "benchmark-suite",
                "--oracles", "none",
                "--page-limit", "1",
                "--output-dir", dir,
                "--fail-on-regression",
                "--max-pdfe-render-ms", "0.001",
            });

            exitCode.Should().Be(1);
            using var stream = File.OpenRead(Path.Combine(dir, "benchmark-report.json"));
            var report = JsonSerializer.Deserialize<RenderProgram.BenchmarkSuiteReport>(stream);
            report.Should().NotBeNull();
            report!.regressionGate.passed.Should().BeFalse();
            report.regressionGate.checks.Should().Contain(c => c.name == "pdfe-render-average" && !c.passed);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void HotspotRegressionCatalog_ClassifiesBenchmarkGuiCorpusAndReferencePhases()
    {
        var definitions = RenderProgram.HotspotRegressionCatalog.Definitions;

        definitions.Should().Contain(d => d.workloadId == "core.document-open" && d.route == "library");
        definitions.Should().Contain(d => d.workloadId == "renderer.page-render" && d.route == "library");
        definitions.Should().Contain(d => d.workloadId == "cli.render-page" && d.route == "cli");
        definitions.Should().Contain(d => d.workloadId == "gui.document-open" && d.route == "gui");
        definitions.Should().Contain(d => d.workloadId == "gui.input" && d.route == "gui");
        definitions.Should().Contain(d => d.workloadId == "gui.display-render-capture" && d.route == "gui");
        definitions.Should().Contain(d => d.workloadId == "reference.external-render" && d.route == "external-cli");

        RenderProgram.HotspotRegressionCatalog.ForBenchmarkBucket("cli.render-page-subprocess")
            .workloadId.Should().Be("cli.render-page");
        RenderProgram.HotspotRegressionCatalog.ForPhase("gui.document-open.total")
            .workloadId.Should().Be("gui.document-open");
        RenderProgram.HotspotRegressionCatalog.ForPhase("gui.input.continuous-scroll-offset")
            .workloadId.Should().Be("gui.input");
        RenderProgram.HotspotRegressionCatalog.ForPhase("viewer-render-and-capture")
            .workloadId.Should().Be("gui.display-render-capture");
        RenderProgram.HotspotRegressionCatalog.ForPhase("reference-mutool-render")
            .workloadId.Should().Be("reference.external-render");
        RenderProgram.HotspotRegressionCatalog.ForPhase("compare-classify-and-overhead")
            .workloadId.Should().Be("corpus.compare-classify");
    }

    [Fact]
    public void GuiDisplayHotspots_EmitStructuredWorkloadMetadata()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pdfe-gui-hotspot-contract-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var reportPath = Path.Combine(dir, "gui-workflow-suite-synthetic.json");
            File.WriteAllText(reportPath, """
            {
              "schemaVersion": 1,
              "suite": "gui-workflow-common-interactions",
              "results": [
                {
                  "workflow": "document-open",
                  "status": "PASS",
                  "elapsedMs": 11,
                  "phaseElapsedMs": {
                    "gui.document-open.total": 11,
                    "gui.input.continuous-scroll-offset": 2,
                    "viewer-render-and-capture": 7
                  }
                }
              ]
            }
            """);

            var aggregate = RenderProgram.BuildGuiDisplayHotspotReport(
                new[] { new FileInfo(reportPath) },
                top: 0);

            aggregate.phases.Should().Contain(phase =>
                phase.phase == "gui.document-open.total" &&
                phase.workloadId == "gui.document-open" &&
                phase.route == "gui" &&
                phase.regressionPolicy == "gate");
            aggregate.phases.Should().Contain(phase =>
                phase.phase == "gui.input.continuous-scroll-offset" &&
                phase.workloadId == "gui.input" &&
                phase.route == "gui");
            aggregate.phases.Should().Contain(phase =>
                phase.phase == "viewer-render-and-capture" &&
                phase.workloadId == "gui.display-render-capture" &&
                phase.route == "gui");
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}
