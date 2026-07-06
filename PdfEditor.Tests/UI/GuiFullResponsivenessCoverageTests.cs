using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using AwesomeAssertions;
using Pdfe.Avalonia.Controls;
using Pdfe.Core.Document;
using PdfEditor.Tests.Utilities;
using PdfEditor.ViewModels;
using PdfEditor.Views;
using Xunit;

namespace PdfEditor.Tests.UI;

[Collection("AvaloniaTests")]
public class GuiFullResponsivenessCoverageTests
{
    private const int LongDocumentPageCount = 160;

    [FixedAvaloniaFact(Timeout = 120_000)]
    public async Task LongDocumentContinuousScroll_StaysResponsiveAndWritesHotspotReport()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pdfe-long-scroll-{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: LongDocumentPageCount);
        var results = new List<GuiWorkflowResult>();

        try
        {
            var vm = new MainWindowViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();

            var openElapsedMs = await MeasureAsync(() => vm.LoadDocumentAsync(path));
            var timing = vm.LastDocumentOpenTiming;
            timing.Should().NotBeNull("long-document open timing is required for GUI responsiveness reports");
            vm.TotalPages.Should().Be(LongDocumentPageCount);
            AddResult(
                results,
                "long-document-open",
                openElapsedMs,
                passMs: 6_000,
                warnMs: 12_000,
                new Dictionary<string, long>(StringComparer.Ordinal)
                {
                    ["gui.document-open.instances-loaded"] = timing!.DocumentInstancesLoadedElapsedMs,
                    ["gui.document-open.first-page-scheduled"] = timing.FirstPageVisibleElapsedMs,
                    ["gui.document-open.thumbnail-placeholders"] = timing.ThumbnailPlaceholdersReadyElapsedMs,
                    ["gui.document-open.outline-ready"] = timing.OutlineReadyElapsedMs,
                    ["gui.document-open.search-index-started"] = timing.SearchIndexStartedElapsedMs,
                    ["gui.document-open.total"] = timing.TotalLoadElapsedMs,
                });

            var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl");
            viewer.Should().NotBeNull("the main window should host the reusable viewer control");
            await WaitForIdleLayout(window);

            AddResult(
                results,
                "long-document-continuous-view-toggle",
                Measure(() => vm.ViewMode = PdfViewMode.Continuous),
                passMs: 150,
                warnMs: 500,
                phase: "gui.input.continuous-view-toggle");
            await WaitForIdleLayout(window);

            var scroll = viewer!.FindControl<ScrollViewer>("ContinuousScrollViewer");
            scroll.Should().NotBeNull("continuous mode should expose a scroll viewer for long-document testing");

            var scrollElapsedMs = Measure(() =>
            {
                for (var i = 0; i < 48; i++)
                {
                    var y = i * 950;
                    scroll!.Offset = new Vector(0, y);
                }
            });
            AddResult(
                results,
                "long-document-continuous-scroll",
                scrollElapsedMs,
                passMs: 150,
                warnMs: 750,
                phase: "gui.input.long-document-continuous-scroll");

            var jumpElapsedMs = Measure(() =>
            {
                foreach (var pageIndex in new[] { 0, 40, 80, 120, LongDocumentPageCount - 1, 0 })
                    vm.CurrentPageIndex = pageIndex;
            });
            AddResult(
                results,
                "long-document-page-jumps",
                jumpElapsedMs,
                passMs: 150,
                warnMs: 750,
                phase: "gui.input.long-document-page-jumps");

            var thumbnailElapsedMs = await MeasureAsync(async () =>
            {
                await vm.EnsureThumbnailLoadedAsync(0);
                await vm.EnsureThumbnailLoadedAsync(LongDocumentPageCount / 2);
                await vm.EnsureThumbnailLoadedAsync(LongDocumentPageCount - 1);
            });
            AddResult(
                results,
                "long-document-visible-thumbnails",
                thumbnailElapsedMs,
                passMs: 4_000,
                warnMs: 8_000,
                phase: "gui.thumbnail.long-document-visible-pages");

            AssertNoHardFailures(results);
            WriteReport("gui-workflow-suite-long-document-responsiveness.json", "gui-workflow-long-document-responsiveness", results);
            window.Close();
        }
        finally
        {
            TestPdfGenerator.CleanupTestFile(path);
        }
    }

    [FixedAvaloniaFact(Timeout = 120_000)]
    public async Task BroadGuiWorkflows_StayWithinResponsivenessBudgetsAndWriteHotspotReport()
    {
        var inputPath = Path.Combine(Path.GetTempPath(), $"pdfe-broad-gui-{Guid.NewGuid():N}.pdf");
        var outputPath = Path.Combine(Path.GetTempPath(), $"pdfe-broad-gui-output-{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateMultiPagePdf(inputPath, pageCount: 40);
        var results = new List<GuiWorkflowResult>();

        try
        {
            var vm = new MainWindowViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();

            var openElapsedMs = await MeasureAsync(() => vm.LoadDocumentAsync(inputPath));
            AddResult(results, "realistic-document-open", openElapsedMs, 6_000, 12_000, "gui.document-open.total");

            var searchScheduleMs = Measure(() => vm.SearchText = "Secret");
            AddResult(results, "search-type-schedule", searchScheduleMs, 150, 500, "gui.search.type-schedule");

            var searchCompleteMs = await MeasureAsync(async () =>
            {
                vm.FindNow();
                await WaitForAsync(() => vm.SearchMatches.Count >= 40 && !vm.IsSearching, TimeSpan.FromSeconds(8));
            });
            AddResult(results, "search-complete-large-document", searchCompleteMs, 3_000, 8_000, "gui.search.complete");
            vm.SearchMatches.Should().HaveCountGreaterThanOrEqualTo(40);

            AddResult(
                results,
                "search-result-navigation",
                Measure(() => vm.JumpToSearchMatch(vm.SearchMatches.Last())),
                passMs: 150,
                warnMs: 500,
                phase: "gui.search.result-navigation");

            vm.CurrentTextSelectionPageArea = PdfPageRect.ViewerDips(
                pageNumber: Math.Max(1, vm.CurrentPage),
                x: 100,
                y: 100,
                width: 180,
                height: 28,
                renderDpi: MainWindowViewModel.DefaultViewerRenderDpi);
            vm.SelectedText = "Selected clause";
            var annotationMs = await MeasureAsync(() => vm.AddHighlightAnnotationFromSelectionAsync());
            AddResult(results, "annotation-highlight-from-selection", annotationMs, 1_000, 3_000, "gui.annotation.highlight-from-selection");

            var formAuthoringMs = Measure(() =>
            {
                vm.IsFormAuthoringMode = true;
                vm.FormAuthoringFieldType = PdfFieldType.Text;
                vm.OnFormFieldRectDrawn(new PdfRectangle(72, 700, 300, 720), pageNumber: 1);
                vm.OnFormFieldEdited("Text1", "Alice");
            });
            AddResult(results, "form-authoring-and-edit", formAuthoringMs, 150, 750, "gui.form.author-and-edit");

            var redactionPreviewMs = Measure(() =>
            {
                vm.IsRedactionMode = true;
                vm.CurrentRedactionArea = new Rect(80, 120, 220, 40);
                vm.RedactionWorkflow.MarkArea(
                    pageNumber: Math.Max(1, vm.CurrentPage),
                    area: new Rect(80, 120, 220, 40),
                    previewText: "Secret");
            });
            AddResult(results, "redaction-preview-state", redactionPreviewMs, 150, 750, "gui.redaction.preview-state");

            var pageOrganizationMs = await MeasureAsync(() => vm.MovePageAsync(fromIndex: 0, toIndex: 5));
            AddResult(results, "page-organization-move-page", pageOrganizationMs, 3_000, 8_000, "gui.page-organization.move-page");

            var saveMs = await MeasureAsync(() => vm.SaveFileAsAsync(outputPath));
            AddResult(results, "save-as-after-edits", saveMs, 5_000, 12_000, "gui.save.save-as-after-edits");
            File.Exists(outputPath).Should().BeTrue("save-as workflow should write the edited document");

            var closeMs = Measure(() => vm.CloseDocumentCommand.Execute().Subscribe());
            AddResult(results, "close-document", closeMs, 150, 500, "gui.close.document");
            await WaitForIdleLayout(window, iterations: 2);
            vm.IsDocumentLoaded.Should().BeFalse();

            AssertNoHardFailures(results);
            WriteReport("gui-workflow-suite-broad-responsiveness.json", "gui-workflow-broad-responsiveness", results);
            window.Close();
        }
        finally
        {
            TestPdfGenerator.CleanupTestFile(inputPath);
            TestPdfGenerator.CleanupTestFile(outputPath);
        }
    }

    private static void AddResult(
        List<GuiWorkflowResult> results,
        string workflow,
        long elapsedMs,
        long passMs,
        long warnMs,
        string phase)
    {
        AddResult(
            results,
            workflow,
            elapsedMs,
            passMs,
            warnMs,
            new Dictionary<string, long>(StringComparer.Ordinal)
            {
                [phase] = elapsedMs,
            });
    }

    private static void AddResult(
        List<GuiWorkflowResult> results,
        string workflow,
        long elapsedMs,
        long passMs,
        long warnMs,
        Dictionary<string, long> phaseElapsedMs)
    {
        results.Add(new GuiWorkflowResult(
            workflow,
            Status(elapsedMs, passMs, warnMs),
            elapsedMs,
            passMs,
            warnMs,
            phaseElapsedMs));
    }

    private static void AssertNoHardFailures(IReadOnlyCollection<GuiWorkflowResult> results)
    {
        results.Where(result => result.status == "FAIL")
            .Should().BeEmpty("GUI responsiveness tests should fail when a workflow crosses the human-visible hard budget");
        results.Should().OnlyContain(result => result.phaseElapsedMs.Count > 0);
    }

    private static async Task<long> MeasureAsync(Func<Task> action)
    {
        var sw = Stopwatch.StartNew();
        await action();
        sw.Stop();
        return sw.ElapsedMilliseconds;
    }

    private static long Measure(Action action)
    {
        var sw = Stopwatch.StartNew();
        action();
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

    private static async Task WaitForIdleLayout(Window window, int iterations = 4)
    {
        for (var i = 0; i < iterations; i++)
        {
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(25);
        }
    }

    private static string Status(long elapsedMs, long passMs, long warnMs) =>
        elapsedMs <= passMs ? "PASS" :
        elapsedMs <= warnMs ? "WARN" :
        "FAIL";

    private static void WriteReport(string fileName, string suite, IReadOnlyList<GuiWorkflowResult> results)
    {
        var outputDir = Path.Combine(AppContext.BaseDirectory, "UI", "test-output");
        Directory.CreateDirectory(outputDir);
        var path = Path.Combine(outputDir, fileName);
        var report = new
        {
            schemaVersion = 1,
            generatedUtc = DateTimeOffset.UtcNow,
            suite,
            results,
        };

        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed record GuiWorkflowResult(
        string workflow,
        string status,
        long elapsedMs,
        long passMs,
        long warnMs,
        Dictionary<string, long> phaseElapsedMs);
}
