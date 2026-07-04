using Microsoft.Extensions.Logging;
using PdfEditor.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PdfEditor.Services;

internal static class ResponsivenessReportWriter
{
    public const string ReportPathEnvironmentVariable = "PDFE_RESPONSIVENESS_REPORT";
    public const string ReportPathArgument = "--responsiveness-report";

    private static readonly ResponsivenessBudget DocumentInstancesLoadedBudget = new(
        "document_instances_loaded",
        PassBudgetMs: 2_000,
        WarnBudgetMs: 8_000,
        "PDF parse and document service instances are ready.");

    private static readonly ResponsivenessBudget FirstPageVisibleBudget = new(
        "first_page_visible",
        PassBudgetMs: 4_000,
        WarnBudgetMs: 15_000,
        "The first page bitmap has been rendered and assigned to the viewer.");

    private static readonly ResponsivenessBudget BackgroundStartupBudget = new(
        "background_work_started",
        PassBudgetMs: 6_000,
        WarnBudgetMs: 20_000,
        "Thumbnail placeholders, outline parsing, and search indexing have started without blocking first page display.");

    private static readonly ResponsivenessBudget TotalLoadBudget = new(
        "total_load",
        PassBudgetMs: 8_000,
        WarnBudgetMs: 25_000,
        "Document open workflow completed after first visible page and background startup.");

    public static DocumentOpenResponsivenessReport BuildDocumentOpenReport(
        DocumentOpenTiming timing,
        PdfRenderService.CacheStatistics renderCache)
    {
        var phases = new List<ResponsivenessTimingPhase>
        {
            Evaluate(DocumentInstancesLoadedBudget, timing.DocumentInstancesLoadedElapsedMs),
            Evaluate(FirstPageVisibleBudget, timing.FirstPageVisibleElapsedMs),
            Evaluate(BackgroundStartupBudget with { Workflow = "thumbnail_placeholders_ready" }, timing.ThumbnailPlaceholdersReadyElapsedMs),
            Evaluate(BackgroundStartupBudget with { Workflow = "outline_ready" }, timing.OutlineReadyElapsedMs),
            Evaluate(BackgroundStartupBudget with { Workflow = "search_index_started" }, timing.SearchIndexStartedElapsedMs),
            Evaluate(TotalLoadBudget, timing.TotalLoadElapsedMs)
        };

        var overallStatus = phases.Any(p => p.Status == ResponsivenessStatus.Fail.ToWireValue())
            ? ResponsivenessStatus.Fail
            : phases.Any(p => p.Status == ResponsivenessStatus.Warn.ToWireValue())
                ? ResponsivenessStatus.Warn
                : ResponsivenessStatus.Pass;

        var cacheSnapshot = new ResponsivenessCacheStatistics(
            Count: renderCache.Count,
            MaxEntries: renderCache.MaxEntries,
            Hits: renderCache.Hits,
            Misses: renderCache.Misses,
            CurrentBytes: renderCache.CurrentBytes,
            MaxBytes: renderCache.MaxBytes,
            HitRate: renderCache.HitRate);

        return new DocumentOpenResponsivenessReport(
            SchemaVersion: 1,
            GeneratedUtc: DateTime.UtcNow,
            FilePath: timing.FilePath,
            FileName: Path.GetFileName(timing.FilePath),
            PageCount: timing.PageCount,
            OverallStatus: overallStatus.ToWireValue(),
            Phases: phases,
            RenderCache: cacheSnapshot);
    }

    public static void TryWriteDocumentOpenReportFromEnvironment(
        DocumentOpenTiming timing,
        PdfRenderService.CacheStatistics renderCache,
        ILogger logger)
    {
        var reportPath = Environment.GetEnvironmentVariable(ReportPathEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(reportPath))
            return;

        try
        {
            var fullPath = Path.GetFullPath(reportPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var report = BuildDocumentOpenReport(timing, renderCache);
            var json = JsonSerializer.Serialize(report, PdfeJsonContext.Default.DocumentOpenResponsivenessReport);
            File.WriteAllText(fullPath, json);
            logger.LogInformation("Wrote PDFE responsiveness report: {ReportPath}", fullPath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write PDFE responsiveness report");
        }
    }

    public static string? ConsumeOneShotReportRequest(ILogger logger)
    {
        try
        {
            var requestPath = AppPaths.ResponsivenessReportRequestPath;
            if (!File.Exists(requestPath))
                return null;

            var reportPath = File.ReadLines(requestPath).FirstOrDefault();
            File.Delete(requestPath);
            if (string.IsNullOrWhiteSpace(reportPath))
                return null;

            var fullPath = Path.GetFullPath(reportPath);
            logger.LogInformation("Consumed one-shot responsiveness report request: {Path}", fullPath);
            return fullPath;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to consume one-shot responsiveness report request");
            return null;
        }
    }

    private static ResponsivenessTimingPhase Evaluate(ResponsivenessBudget budget, long elapsedMs)
    {
        var status = elapsedMs <= budget.PassBudgetMs
            ? ResponsivenessStatus.Pass
            : elapsedMs <= budget.WarnBudgetMs
                ? ResponsivenessStatus.Warn
                : ResponsivenessStatus.Fail;

        return new ResponsivenessTimingPhase(
            Workflow: budget.Workflow,
            ElapsedMs: elapsedMs,
            PassBudgetMs: budget.PassBudgetMs,
            WarnBudgetMs: budget.WarnBudgetMs,
            Status: status.ToWireValue(),
            Detail: budget.Detail);
    }

    private static string ToWireValue(this ResponsivenessStatus status) => status switch
    {
        ResponsivenessStatus.Pass => "PASS",
        ResponsivenessStatus.Warn => "WARN",
        ResponsivenessStatus.Fail => "FAIL",
        _ => "FAIL"
    };
}

internal sealed record ResponsivenessBudget(
    string Workflow,
    long PassBudgetMs,
    long WarnBudgetMs,
    string Detail);

internal sealed record ResponsivenessTimingPhase(
    [property: JsonPropertyName("workflow")] string Workflow,
    [property: JsonPropertyName("elapsedMs")] long ElapsedMs,
    [property: JsonPropertyName("passBudgetMs")] long PassBudgetMs,
    [property: JsonPropertyName("warnBudgetMs")] long WarnBudgetMs,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("detail")] string Detail);

internal sealed record ResponsivenessCacheStatistics(
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("maxEntries")] int MaxEntries,
    [property: JsonPropertyName("hits")] long Hits,
    [property: JsonPropertyName("misses")] long Misses,
    [property: JsonPropertyName("currentBytes")] long CurrentBytes,
    [property: JsonPropertyName("maxBytes")] long MaxBytes,
    [property: JsonPropertyName("hitRate")] double HitRate);

internal sealed record DocumentOpenResponsivenessReport(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("generatedUtc")] DateTime GeneratedUtc,
    [property: JsonPropertyName("filePath")] string FilePath,
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("pageCount")] int PageCount,
    [property: JsonPropertyName("overallStatus")] string OverallStatus,
    [property: JsonPropertyName("phases")] List<ResponsivenessTimingPhase> Phases,
    [property: JsonPropertyName("renderCache")] ResponsivenessCacheStatistics RenderCache);

internal enum ResponsivenessStatus
{
    Pass,
    Warn,
    Fail
}
