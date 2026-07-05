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
            report.summary.pageCount.Should().Be(1);
            report.summary.statusCounts.Should().ContainKey("NO_REFERENCES");
            report.hotPaths.Should().Contain(h => h.name == "renderer.page-render" && h.scope == "pdfe-owned");
            report.hotPaths.Should().Contain(h => h.name == "parser.document-open" && h.issueRefs.Contains("#597", StringComparison.Ordinal));
            report.redactionCompleteness.status.Should().Be("PASS");
            report.regressionGate.passed.Should().BeTrue();
            report.tools.Should().Contain(t => t.name == "pdfe" && t.kind == "in-process" && t.available);
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
}
