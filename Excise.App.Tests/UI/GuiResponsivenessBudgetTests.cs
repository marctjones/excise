using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Xunit;

namespace Excise.App.Tests.UI;

[Collection("AvaloniaTests")]
public class GuiResponsivenessBudgetTests
{
    private static readonly TimeSpan DirectInputBudget = TimeSpan.FromMilliseconds(150);

    [FixedAvaloniaFact]
    public async Task DocumentOpen_PrioritizesFirstPageBeforeBackgroundWork()
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-open-budget-{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 20);

        try
        {
            var vm = new MainWindowViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();

            await vm.LoadDocumentAsync(path);

            var timing = vm.LastDocumentOpenTiming;
            timing.Should().NotBeNull("document-open responsiveness timing should be recorded");
            timing!.DocumentInstancesLoadedElapsedMs.Should().BeLessThanOrEqualTo(timing.FirstPageVisibleElapsedMs);
            timing.FirstPageVisibleElapsedMs.Should().BeLessThanOrEqualTo(timing.ThumbnailPlaceholdersReadyElapsedMs);
            timing.FirstPageVisibleElapsedMs.Should().BeLessThanOrEqualTo(timing.SearchIndexStartedElapsedMs);
            timing.FirstPageVisibleElapsedMs.Should().BeLessThan(4_000,
                "first-page-visible must stay inside the release responsiveness budget");
        }
        finally
        {
            TestPdfGenerator.CleanupTestFile(path);
        }
    }

    [FixedAvaloniaFact]
    public async Task CommonInteractionHandlers_ReturnWithinDirectInputBudget()
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-input-budget-{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 5);

        try
        {
            var vm = new MainWindowViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            await vm.LoadDocumentAsync(path);

            Measure(() => vm.SearchText = "Page")
                .Should().BeLessThan(DirectInputBudget,
                    "typing in search should schedule debounce/background work without blocking input");

            Measure(() =>
                {
                    vm.IsTextSelectionMode = true;
                    vm.CurrentTextSelectionArea = new Rect(10, 20, 120, 24);
                    vm.CurrentTextSelectionPageArea = PdfPageRect.ViewerDips(1, 10, 20, 120, 24, 120);
                    vm.SelectedText = "Page 1";
                })
                .Should().BeLessThan(DirectInputBudget,
                    "text selection feedback should be direct state publication, not document analysis");

            Measure(() =>
                {
                    vm.IsRedactionMode = true;
                    vm.CurrentRedactionArea = new Rect(20, 40, 160, 48);
                })
                .Should().BeLessThan(DirectInputBudget,
                    "redaction preview rectangle updates should not wait for redaction application");

            Measure(() =>
                {
                    vm.IsFormAuthoringMode = true;
                    vm.FormAuthoringFieldType = PdfFieldType.Text;
                    vm.OnFormFieldRectDrawn(new PdfRectangle(72, 700, 300, 720), pageNumber: 1);
                })
                .Should().BeLessThan(DirectInputBudget,
                    "form authoring should create the field and update overlays without a page rerender");

            Measure(() => vm.OnFormFieldEdited("Text1", "Alice"))
                .Should().BeLessThan(DirectInputBudget,
                    "form edits should mark state dirty without blocking on save or flattening");

            vm.SearchText = string.Empty;
        }
        finally
        {
            TestPdfGenerator.CleanupTestFile(path);
        }
    }

    private static TimeSpan Measure(Action action)
    {
        var sw = Stopwatch.StartNew();
        action();
        sw.Stop();
        return sw.Elapsed;
    }
}
