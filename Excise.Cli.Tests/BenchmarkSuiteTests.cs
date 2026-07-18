using System.Text.Json;
using AwesomeAssertions;
using Xunit;

using RenderProgram = Excise.RenderTools.Program;

namespace Excise.Cli.Tests;

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
        var dir = Path.Combine(Path.GetTempPath(), "excise-benchmark-suite-" + Guid.NewGuid().ToString("N"));
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
            report.hotPaths.Should().Contain(h => h.name == "renderer.page-render" && h.scope == "excise-owned");
            report.hotPaths.Should().Contain(h => h.name == "parser.document-open" && h.issueRefs.Contains("#597", StringComparison.Ordinal));
            report.hotPaths.Should().Contain(h =>
                h.name == "cli.render-page-subprocess" &&
                h.workloadId == "cli.render-page" &&
                h.route == "cli" &&
                h.regressionPolicy == "gate");
            report.pages.Single().cliRender.Should().NotBeNull();
            report.pages.Single().cliRender!.status.Should().Be("OK");
            report.pages.Single().cliRender!.pass.Should().BeTrue();
            report.regressionGate.checks.Should().Contain(c => c.name == "excise-cli-render-pass-rate" && c.passed);
            report.redactionCompleteness.status.Should().Be("PASS");
            report.regressionGate.passed.Should().BeTrue();
            report.tools.Should().Contain(t => t.name == "excise" && t.kind == "in-process" && t.available);
            report.tools.Should().Contain(t => t.name == "excise-cli" && t.kind == "external-subprocess" && t.available && t.selected);
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
        var dir = Path.Combine(Path.GetTempPath(), "excise-benchmark-suite-fail-" + Guid.NewGuid().ToString("N"));
        try
        {
            var exitCode = await RenderProgram.RunAsync(new[]
            {
                "benchmark-suite",
                "--oracles", "none",
                "--page-limit", "1",
                "--output-dir", dir,
                "--fail-on-regression",
                "--max-excise-render-ms", "-1",
            });

            exitCode.Should().Be(1);
            using var stream = File.OpenRead(Path.Combine(dir, "benchmark-report.json"));
            var report = JsonSerializer.Deserialize<RenderProgram.BenchmarkSuiteReport>(stream);
            report.Should().NotBeNull();
            report!.regressionGate.passed.Should().BeFalse();
            report.regressionGate.checks.Should().Contain(c => c.name == "excise-render-average" && !c.passed);
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
        definitions.Should().Contain(d => d.workloadId == "gui.render" && d.route == "gui");
        definitions.Should().Contain(d => d.workloadId == "gui.search" && d.route == "gui");
        definitions.Should().Contain(d => d.workloadId == "gui.annotation" && d.route == "gui");
        definitions.Should().Contain(d => d.workloadId == "gui.form" && d.route == "gui");
        definitions.Should().Contain(d => d.workloadId == "gui.page-organization" && d.route == "gui");
        definitions.Should().Contain(d => d.workloadId == "gui.redaction" && d.route == "gui");
        definitions.Should().Contain(d => d.workloadId == "gui.save" && d.route == "gui");
        definitions.Should().Contain(d => d.workloadId == "gui.close" && d.route == "gui");
        definitions.Should().Contain(d => d.workloadId == "gui.display-render-capture" && d.route == "gui");
        definitions.Should().Contain(d => d.workloadId == "reference.external-render" && d.route == "external-cli");

        RenderProgram.HotspotRegressionCatalog.ForBenchmarkBucket("cli.render-page-subprocess")
            .workloadId.Should().Be("cli.render-page");
        RenderProgram.HotspotRegressionCatalog.ForPhase("gui.document-open.total")
            .workloadId.Should().Be("gui.document-open");
        RenderProgram.HotspotRegressionCatalog.ForPhase("gui.input.continuous-scroll-offset")
            .workloadId.Should().Be("gui.input");
        RenderProgram.HotspotRegressionCatalog.ForPhase("gui.render.acc-compensation-continuous-scroll-settle")
            .workloadId.Should().Be("gui.render");
        RenderProgram.HotspotRegressionCatalog.ForPhase("gui.search.complete")
            .workloadId.Should().Be("gui.search");
        RenderProgram.HotspotRegressionCatalog.ForPhase("gui.annotation.highlight-from-selection")
            .workloadId.Should().Be("gui.annotation");
        RenderProgram.HotspotRegressionCatalog.ForPhase("gui.form.author-and-edit")
            .workloadId.Should().Be("gui.form");
        RenderProgram.HotspotRegressionCatalog.ForPhase("gui.page-organization.move-page")
            .workloadId.Should().Be("gui.page-organization");
        RenderProgram.HotspotRegressionCatalog.ForPhase("gui.redaction.preview-state")
            .workloadId.Should().Be("gui.redaction");
        RenderProgram.HotspotRegressionCatalog.ForPhase("gui.save.save-as-after-edits")
            .workloadId.Should().Be("gui.save");
        RenderProgram.HotspotRegressionCatalog.ForPhase("gui.close.document")
            .workloadId.Should().Be("gui.close");
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
        var dir = Path.Combine(Path.GetTempPath(), "excise-gui-hotspot-contract-" + Guid.NewGuid().ToString("N"));
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
                    "gui.render.acc-compensation-continuous-scroll-settle": 13,
                    "gui.search.complete": 8,
                    "gui.annotation.highlight-from-selection": 5,
                    "gui.form.author-and-edit": 4,
                    "gui.page-organization.move-page": 9,
                    "gui.redaction.preview-state": 3,
                    "gui.save.save-as-after-edits": 10,
                    "gui.close.document": 1,
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
                phase.phase == "gui.render.acc-compensation-continuous-scroll-settle" &&
                phase.workloadId == "gui.render" &&
                phase.route == "gui");
            aggregate.phases.Should().Contain(phase =>
                phase.phase == "gui.search.complete" &&
                phase.workloadId == "gui.search" &&
                phase.route == "gui");
            aggregate.phases.Should().Contain(phase =>
                phase.phase == "gui.annotation.highlight-from-selection" &&
                phase.workloadId == "gui.annotation" &&
                phase.route == "gui");
            aggregate.phases.Should().Contain(phase =>
                phase.phase == "gui.form.author-and-edit" &&
                phase.workloadId == "gui.form" &&
                phase.route == "gui");
            aggregate.phases.Should().Contain(phase =>
                phase.phase == "gui.page-organization.move-page" &&
                phase.workloadId == "gui.page-organization" &&
                phase.route == "gui");
            aggregate.phases.Should().Contain(phase =>
                phase.phase == "gui.redaction.preview-state" &&
                phase.workloadId == "gui.redaction" &&
                phase.route == "gui");
            aggregate.phases.Should().Contain(phase =>
                phase.phase == "gui.save.save-as-after-edits" &&
                phase.workloadId == "gui.save" &&
                phase.route == "gui");
            aggregate.phases.Should().Contain(phase =>
                phase.phase == "gui.close.document" &&
                phase.workloadId == "gui.close" &&
                phase.route == "gui");
            aggregate.phases.Should().Contain(phase =>
                phase.phase == "viewer-render-and-capture" &&
                phase.workloadId == "gui.display-render-capture" &&
                phase.route == "gui");
            aggregate.phases.Should().OnlyContain(phase =>
                phase.workloadId != "unknown",
                "GUI workflow phases used by responsiveness regression tests must be first-class catalog entries");
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}
