using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Excise.App.Services;
using Excise.App.ViewModels;
using System;
using System.IO;
using System.Text.Json;
using Xunit;

namespace Excise.App.Tests.Unit;

public class ResponsivenessReportTests
{
    [Fact]
    public void BuildDocumentOpenReport_ClassifiesPhaseBudgetsWithoutMislabelingExportState()
    {
        var timing = new DocumentOpenTiming(
            FilePath: "/tmp/large.pdf",
            PageCount: 455,
            DocumentInstancesLoadedElapsedMs: 1_500,
            FirstPageVisibleElapsedMs: 5_000,
            ThumbnailPlaceholdersReadyElapsedMs: 5_500,
            OutlineReadyElapsedMs: 5_700,
            SearchIndexStartedElapsedMs: 6_100,
            TotalLoadElapsedMs: 9_000);
        var report = ResponsivenessReportWriter.BuildDocumentOpenReport(timing);

        report.SchemaVersion.Should().Be(2);
        report.FileName.Should().Be("large.pdf");
        report.PageCount.Should().Be(455);
        report.OverallStatus.Should().Be("WARN");
        report.Phases.Should().Contain(p =>
            p.Workflow == "first_page_visible" &&
            p.Status == "WARN" &&
            p.ElapsedMs == 5_000);
    }

    [Fact]
    public void TryWriteDocumentOpenReportFromEnvironment_WritesSourceGeneratedJson()
    {
        var outputPath = Path.Combine(
            Path.GetTempPath(),
            "excise-responsiveness",
            $"{Guid.NewGuid():N}.json");
        var previous = Environment.GetEnvironmentVariable(ResponsivenessReportWriter.ReportPathEnvironmentVariable);
        Environment.SetEnvironmentVariable(ResponsivenessReportWriter.ReportPathEnvironmentVariable, outputPath);

        try
        {
            var timing = new DocumentOpenTiming(
                FilePath: "/tmp/small.pdf",
                PageCount: 1,
                DocumentInstancesLoadedElapsedMs: 10,
                FirstPageVisibleElapsedMs: 20,
                ThumbnailPlaceholdersReadyElapsedMs: 21,
                OutlineReadyElapsedMs: 22,
                SearchIndexStartedElapsedMs: 23,
                TotalLoadElapsedMs: 24);
            ResponsivenessReportWriter.TryWriteDocumentOpenReportFromEnvironment(
                timing,
                NullLogger.Instance);

            File.Exists(outputPath).Should().BeTrue();
            using var document = JsonDocument.Parse(File.ReadAllText(outputPath));
            document.RootElement.GetProperty("schemaVersion").GetInt32().Should().Be(2);
            document.RootElement.GetProperty("overallStatus").GetString().Should().Be("PASS");
            document.RootElement.TryGetProperty("renderCache", out _).Should().BeFalse(
                "the App export cache was never viewer telemetry");
        }
        finally
        {
            Environment.SetEnvironmentVariable(ResponsivenessReportWriter.ReportPathEnvironmentVariable, previous);
            if (File.Exists(outputPath))
                File.Delete(outputPath);
        }
    }

    [Fact]
    public void ConsumeOneShotReportRequest_ReadsAndDeletesRequestFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "excise-responsiveness-request", Guid.NewGuid().ToString("N"));
        AppPaths.OverrideForTests(root);
        var reportPath = Path.Combine(root, "report.json");

        try
        {
            File.WriteAllText(AppPaths.ResponsivenessReportRequestPath, reportPath + Environment.NewLine);

            var consumed = ResponsivenessReportWriter.ConsumeOneShotReportRequest(NullLogger.Instance);

            consumed.Should().Be(Path.GetFullPath(reportPath));
            File.Exists(AppPaths.ResponsivenessReportRequestPath).Should().BeFalse();
        }
        finally
        {
            AppPaths.OverrideForTests(null);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
