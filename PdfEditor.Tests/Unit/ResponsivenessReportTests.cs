using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PdfEditor.Services;
using PdfEditor.ViewModels;
using System;
using System.IO;
using System.Text.Json;
using Xunit;

namespace PdfEditor.Tests.Unit;

public class ResponsivenessReportTests
{
    [Fact]
    public void BuildDocumentOpenReport_ClassifiesPhaseBudgetsAndCacheStats()
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
        var cache = new PdfRenderService.CacheStatistics(
            Count: 1,
            MaxEntries: 20,
            Hits: 2,
            Misses: 3,
            CurrentBytes: 1234,
            MaxBytes: 100 * 1024 * 1024,
            HitRate: 0.4);

        var report = ResponsivenessReportWriter.BuildDocumentOpenReport(timing, cache);

        report.SchemaVersion.Should().Be(1);
        report.FileName.Should().Be("large.pdf");
        report.PageCount.Should().Be(455);
        report.OverallStatus.Should().Be("WARN");
        report.Phases.Should().Contain(p =>
            p.Workflow == "first_page_visible" &&
            p.Status == "WARN" &&
            p.ElapsedMs == 5_000);
        report.RenderCache.HitRate.Should().Be(0.4);
    }

    [Fact]
    public void TryWriteDocumentOpenReportFromEnvironment_WritesSourceGeneratedJson()
    {
        var outputPath = Path.Combine(
            Path.GetTempPath(),
            "pdfe-responsiveness",
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
            var cache = new PdfRenderService.CacheStatistics(1, 20, 0, 1, 512, 100 * 1024 * 1024, 0);

            ResponsivenessReportWriter.TryWriteDocumentOpenReportFromEnvironment(
                timing,
                cache,
                NullLogger.Instance);

            File.Exists(outputPath).Should().BeTrue();
            using var document = JsonDocument.Parse(File.ReadAllText(outputPath));
            document.RootElement.GetProperty("overallStatus").GetString().Should().Be("PASS");
            document.RootElement.GetProperty("renderCache").GetProperty("misses").GetInt64().Should().Be(1);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ResponsivenessReportWriter.ReportPathEnvironmentVariable, previous);
            if (File.Exists(outputPath))
                File.Delete(outputPath);
        }
    }
}
