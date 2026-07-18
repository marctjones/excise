using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using AwesomeAssertions;
using Excise.Avalonia.Controls;
using Excise.Core.Document;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;

namespace Excise.App.Tests.UI;

[Collection("AvaloniaTests")]
public class GuiWorkflowPerformanceReportTests
{
    [FixedAvaloniaFact]
    public async Task CommonGuiWorkflows_WritePhaseHotspotReport()
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-gui-workflow-perf-{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 12);

        try
        {
            var results = new List<GuiWorkflowResult>();
            var vm = new MainWindowViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();

            var openElapsedMs = await MeasureAsync(() => vm.LoadDocumentAsync(path));
            var openTiming = vm.LastDocumentOpenTiming;
            openTiming.Should().NotBeNull("document-open timing should be available for workflow reports");
            results.Add(new GuiWorkflowResult(
                workflow: "document-open",
                status: Status(openElapsedMs, passMs: 4_000, warnMs: 12_000),
                elapsedMs: openElapsedMs,
                phaseElapsedMs: new Dictionary<string, long>(StringComparer.Ordinal)
                {
                    ["gui.document-open.instances-loaded"] = openTiming!.DocumentInstancesLoadedElapsedMs,
                    ["gui.document-open.first-page-scheduled"] = openTiming.FirstPageVisibleElapsedMs,
                    ["gui.document-open.thumbnail-placeholders"] = openTiming.ThumbnailPlaceholdersReadyElapsedMs,
                    ["gui.document-open.outline-ready"] = openTiming.OutlineReadyElapsedMs,
                    ["gui.document-open.search-index-started"] = openTiming.SearchIndexStartedElapsedMs,
                    ["gui.document-open.total"] = openTiming.TotalLoadElapsedMs,
                }));

            results.Add(MeasureInput("page-navigation-index-change", () => vm.CurrentPageIndex = 4));
            results.Add(MeasureInput("page-navigation-command", () => vm.NextPageCommand.Execute().Subscribe()));
            results.Add(MeasureInput("zoom-in-command", () => vm.ZoomInCommand.Execute().Subscribe()));
            results.Add(MeasureInput("zoom-fit-width-command", () => vm.ZoomFitWidthCommand.Execute().Subscribe()));
            results.Add(MeasureInput("continuous-view-toggle", () => vm.ToggleContinuousViewCommand.Execute().Subscribe()));
            results.Add(MeasureInput("continuous-scroll-offset", () =>
            {
                vm.ViewMode = PdfViewMode.Continuous;
                var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl");
                var scroll = viewer?.FindControl<ScrollViewer>("ContinuousScrollViewer");
                scroll.Should().NotBeNull("continuous view should expose its scroll viewer for workflow timing");
                scroll!.Offset = new Vector(0, 1_800);
                scroll.Offset = new Vector(0, 3_600);
            }));
            results.Add(MeasureInput("redaction-preview-state", () =>
            {
                vm.IsRedactionMode = true;
                vm.CurrentRedactionArea = new Rect(20, 40, 160, 48);
                vm.CurrentRedactionPageArea = PdfPageRect.ViewerDips(vm.CurrentPage, 20, 40, 160, 48, MainWindowViewModel.DefaultViewerRenderDpi);
            }));

            var thumbnailElapsedMs = await MeasureAsync(() => vm.EnsureThumbnailLoadedAsync(0));
            results.Add(new GuiWorkflowResult(
                workflow: "thumbnail-load-visible-page",
                status: Status(thumbnailElapsedMs, passMs: 1_000, warnMs: 3_000),
                elapsedMs: thumbnailElapsedMs,
                phaseElapsedMs: new Dictionary<string, long>(StringComparer.Ordinal)
                {
                    ["gui.thumbnail.load-visible-page"] = thumbnailElapsedMs,
                }));

            var searchSchedule = Measure(() => vm.SearchText = "Page");
            results.Add(new GuiWorkflowResult(
                workflow: "search-type-schedule",
                status: Status(searchSchedule, passMs: 150, warnMs: 500),
                elapsedMs: searchSchedule,
                phaseElapsedMs: new Dictionary<string, long>(StringComparer.Ordinal)
                {
                    ["gui.input.search-type-schedule"] = searchSchedule,
                }));

            var searchCompleteElapsedMs = await MeasureAsync(async () =>
            {
                vm.FindNow();
                await WaitForAsync(() => vm.SearchMatches.Count > 0 && !vm.IsSearching, TimeSpan.FromSeconds(5));
            });
            results.Add(new GuiWorkflowResult(
                workflow: "search-complete",
                status: Status(searchCompleteElapsedMs, passMs: 2_000, warnMs: 6_000),
                elapsedMs: searchCompleteElapsedMs,
                phaseElapsedMs: new Dictionary<string, long>(StringComparer.Ordinal)
                {
                    ["gui.search.complete"] = searchCompleteElapsedMs,
                    ["gui.search.worker"] = vm.LastSearchWorkerElapsedMs,
                    ["gui.search.ui-queue"] = vm.LastSearchUiQueueElapsedMs,
                    ["gui.search.ui-publish"] = vm.LastSearchUiPublishElapsedMs,
                    ["gui.search.total"] = vm.LastSearchTotalElapsedMs,
                }));

            var reportPath = WriteReport(results);
            File.Exists(reportPath).Should().BeTrue("benchmark aggregation should have a GUI workflow timing artifact to consume");
            results.Should().OnlyContain(result => result.phaseElapsedMs.Count > 0);
        }
        finally
        {
            TestPdfGenerator.CleanupTestFile(path);
        }
    }

    private static GuiWorkflowResult MeasureInput(string workflow, Action action)
    {
        var elapsedMs = Measure(action);
        return new GuiWorkflowResult(
            workflow,
            Status(elapsedMs, passMs: 150, warnMs: 500),
            elapsedMs,
            new Dictionary<string, long>(StringComparer.Ordinal)
            {
                [$"gui.input.{workflow}"] = elapsedMs,
            });
    }

    private static long Measure(Action action)
    {
        var sw = Stopwatch.StartNew();
        action();
        sw.Stop();
        return sw.ElapsedMilliseconds;
    }

    private static async Task<long> MeasureAsync(Func<Task> action)
    {
        var sw = Stopwatch.StartNew();
        await action();
        sw.Stop();
        return sw.ElapsedMilliseconds;
    }

    private static async Task WaitForAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        while (!predicate())
        {
            if (deadline.Elapsed > timeout)
                throw new TimeoutException($"Condition was not met within {timeout.TotalSeconds:0.0}s.");

            await Task.Delay(25);
        }
    }

    private static string WriteReport(IReadOnlyList<GuiWorkflowResult> results)
    {
        var outputDir = Path.Combine(AppContext.BaseDirectory, "UI", "test-output");
        Directory.CreateDirectory(outputDir);
        var path = Path.Combine(outputDir, "gui-workflow-suite-common-interactions.json");
        var report = new
        {
            schemaVersion = 1,
            generatedUtc = DateTimeOffset.UtcNow,
            suite = "gui-workflow-common-interactions",
            results,
        };

        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    private static string Status(long elapsedMs, long passMs, long warnMs) =>
        elapsedMs <= passMs ? "PASS" :
        elapsedMs <= warnMs ? "WARN" :
        "FAIL";

    private sealed record GuiWorkflowResult(
        string workflow,
        string status,
        long elapsedMs,
        Dictionary<string, long> phaseElapsedMs);
}
