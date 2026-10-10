using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Excise.App.Services;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// A redaction mark drawn on a page must stay on THAT page when the user then changes the
/// page structure (delete, move, insert, undo of those) before pressing Apply. Each test
/// marks the secret line of the original page 5, performs the operation through the real
/// command, applies through the real command and reads the SAVED file with two
/// independent readers (the stream-decompressing leak scanner and mutool): the original
/// page 5 secret must be gone and every other surviving page's secret must still be there.
/// </summary>
[Collection("AvaloniaTests")]
public class PendingMarksVsPageOperationsTests : IDisposable
{
    private const int PageCount = 6;
    private const int MarkedOriginalPage = 5;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"excise-pendmarks-{Guid.NewGuid():N}");

    public PendingMarksVsPageOperationsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private sealed class RecordingDialogService : IUserDialogService
    {
        public System.Collections.Generic.List<string> Messages { get; } = new();
        public Task ShowMessageAsync(string title, string message) { Messages.Add(message); return Task.CompletedTask; }
        public Task<bool> ShowConfirmAsync(string title, string message) => Task.FromResult(true);
    }

    private readonly RecordingDialogService _dialog = new();

    private static string Secret(int originalPage) => $"Secret on Page {originalPage}";

    private async Task<(MainWindowViewModel Vm, string Output)> OpenAndMarkAsync(
        bool viewerDipsThroughTheMarkCommand = false, int markedPage = MarkedOriginalPage)
    {
        var source = Path.Combine(_dir, "source.pdf");
        TestPdfGenerator.CreateMultiPagePdf(source, PageCount);
        var vm = MainWindowViewModelTestFactory.Create(dialogService: _dialog);
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await vm.LoadDocumentAsync(source);

        var height = vm.PdfCoreDocument!.GetPage(markedPage).Height;
        // The secret line is drawn at x=100, baseline height-200 (TestPdfGenerator.CreateMultiPagePdf).
        vm.IsRedactionMode = true;
        if (viewerDipsThroughTheMarkCommand)
        {
            // Exactly what the mouse drag produces: viewer DIPs at 144 dpi on the displayed page.
            vm.CurrentPageIndex = markedPage - 1;
            vm.CurrentRedactionPageArea = PdfPageRect.ViewerDips(markedPage, 90 * 2, 180 * 2, 320 * 2, 35 * 2, renderDpi: 144);
            await vm.ApplyRedactionCommand.Execute();
        }
        else
        {
            vm.RedactionWorkflow.MarkArea(
                PdfPageRect.FromContentPoints(markedPage, new PdfRectangle(90, height - 215, 410, height - 180)),
                Secret(markedPage));
        }

        vm.FileState.PendingRedactionsCount = vm.RedactionWorkflow.PendingCount;
        vm.RedactionWorkflow.PendingCount.Should().Be(1);

        var output = Path.Combine(_dir, "redacted.pdf");
        vm.SetRedactedSavePathProviderForTests(_ => Task.FromResult<string?>(output));
        return (vm, output);
    }

    private static string Strip(string? s) => (s ?? string.Empty).Replace(" ", "").Replace("\n", "").Replace("\r", "");

    /// <param name="removedOriginalPages">original page numbers that no longer exist in the document</param>
    private static void AssertOnlyTheMarkedSecretIsGone(string output, params int[] removedOriginalPages)
    {
        var bytes = File.ReadAllBytes(output);
        SavedPdfLeakScanner.FindTerm(bytes, Secret(MarkedOriginalPage)).Should().BeEmpty(
            "the marked page's secret must be removed from the saved file's structure");
        var allText = string.Concat(Enumerable.Range(1, PageCount).Select(p => Strip(MutoolTextExtractor.ExtractPage(output, p))));
        allText.Should().NotContain(Strip(Secret(MarkedOriginalPage)), "an independent reader must not see it either");
        for (var original = 1; original <= PageCount; original++)
        {
            if (original == MarkedOriginalPage || removedOriginalPages.Contains(original))
                continue;
            allText.Should().Contain(Strip(Secret(original)),
                $"the mark was on page {MarkedOriginalPage}; page {original}'s text must be untouched");
        }
    }

    [FixedAvaloniaFact(Timeout = 120000)]
    public async Task Baseline_NoPageOperation_RemovesTheMarkedSecret()
    {
        var (vm, output) = await OpenAndMarkAsync();
        await vm.ApplyAllRedactionsCommand!.Execute();
        AssertOnlyTheMarkedSecretIsGone(output);
    }

    [FixedAvaloniaFact(Timeout = 120000)]
    public async Task Baseline_ViewerRectangleThroughTheMarkCommand_RemovesTheMarkedSecret()
    {
        var (vm, output) = await OpenAndMarkAsync(viewerDipsThroughTheMarkCommand: true);
        await vm.ApplyAllRedactionsCommand!.Execute();
        AssertOnlyTheMarkedSecretIsGone(output);
    }

    [FixedAvaloniaFact(Timeout = 120000)]
    public async Task TheMarkAndItsOutlineFollowThePage()
    {
        var (vm, _) = await OpenAndMarkAsync();
        vm.ContextMenuPageNumber = 2;
        await vm.RemoveCurrentPageCommand.Execute();
        vm.RedactionWorkflow.GetPendingForPage(MarkedOriginalPage - 1).Should().ContainSingle();
        vm.RedactionWorkflow.GetPendingForPage(MarkedOriginalPage).Should().BeEmpty();
        await vm.UndoCommand.Execute();
        vm.RedactionWorkflow.GetPendingForPage(MarkedOriginalPage).Should().ContainSingle();
    }

    [FixedAvaloniaFact(Timeout = 120000)]
    public async Task MarkOnADeletedPage_IsReportedAsSkipped_AndNothingElseIsRedacted()
    {
        var (vm, output) = await OpenAndMarkAsync(markedPage: 2);
        vm.ContextMenuPageNumber = 2;
        await vm.RemoveCurrentPageCommand.Execute();
        await vm.ApplyAllRedactionsCommand!.Execute();

        var text = string.Concat(Enumerable.Range(1, PageCount - 1).Select(p => Strip(MutoolTextExtractor.ExtractPage(output, p))));
        foreach (var kept in new[] { 1, 3, 4, 5, 6 })
            text.Should().Contain(Strip(Secret(kept)), "the mark belonged to the deleted page and must not hit another one");
        _dialog.Messages.Should().Contain(m => m.Contains("skipped") && m.Contains("no longer exists"),
            "the user is told a marked area was left out");
    }

    [FixedAvaloniaFact(Timeout = 120000)]
    public async Task DeleteAnEarlierPage_ThenApply_StillRemovesTheMarkedSecret()
    {
        var (vm, output) = await OpenAndMarkAsync();
        vm.ContextMenuPageNumber = 2;
        await vm.RemoveCurrentPageCommand.Execute();
        vm.TotalPages.Should().Be(PageCount - 1);
        await vm.ApplyAllRedactionsCommand!.Execute();
        AssertOnlyTheMarkedSecretIsGone(output, removedOriginalPages: 2);
    }

    [FixedAvaloniaFact(Timeout = 120000)]
    public async Task MovePageFromAfterToBeforeTheMark_ThenApply_StillRemovesTheMarkedSecret()
    {
        var (vm, output) = await OpenAndMarkAsync();
        await vm.MovePageAsync(5, 0); // original page 6 first; original 5 is now page 6
        await vm.ApplyAllRedactionsCommand!.Execute();
        AssertOnlyTheMarkedSecretIsGone(output);
    }

    [FixedAvaloniaFact(Timeout = 120000)]
    public async Task InsertPagesBeforeTheMark_ThenApply_StillRemovesTheMarkedSecret()
    {
        var (vm, output) = await OpenAndMarkAsync();
        var other = Path.Combine(_dir, "other.pdf");
        TestPdfGenerator.CreateSimpleTextPdf(other, "Inserted page");
        await vm.InsertPagesFromFileAsync(other, 0);
        vm.TotalPages.Should().Be(PageCount + 1);
        await vm.ApplyAllRedactionsCommand!.Execute();
        var bytes = File.ReadAllBytes(output);
        SavedPdfLeakScanner.FindTerm(bytes, Secret(MarkedOriginalPage)).Should().BeEmpty();
        var text = string.Concat(Enumerable.Range(1, PageCount + 1).Select(p => Strip(MutoolTextExtractor.ExtractPage(output, p))));
        text.Should().NotContain(Strip(Secret(MarkedOriginalPage)));
        text.Should().Contain(Strip(Secret(4))).And.Contain(Strip(Secret(6)));
    }

    [FixedAvaloniaFact(Timeout = 120000)]
    public async Task ExtractPages_ThenApply_IsUnaffected()
    {
        var (vm, output) = await OpenAndMarkAsync();
        await vm.ExtractPagesToFileAsync(Path.Combine(_dir, "extract.pdf"), new[] { 0, 1 });
        await vm.ApplyAllRedactionsCommand!.Execute();
        AssertOnlyTheMarkedSecretIsGone(output);
    }

    [FixedAvaloniaFact(Timeout = 120000)]
    public async Task RotateTheMarkedPage_ThenApply_StillRemovesTheMarkedSecret()
    {
        var (vm, output) = await OpenAndMarkAsync(viewerDipsThroughTheMarkCommand: true);
        vm.CurrentPageIndex = MarkedOriginalPage - 1;
        vm.ContextMenuPageNumber = MarkedOriginalPage;
        await vm.RotatePageRightCommand.Execute();
        await vm.ApplyAllRedactionsCommand!.Execute();
        AssertOnlyTheMarkedSecretIsGone(output);
    }

    [FixedAvaloniaFact(Timeout = 120000)]
    public async Task DeleteAnEarlierPage_ThenUndo_ThenApply_StillRemovesTheMarkedSecret()
    {
        var (vm, output) = await OpenAndMarkAsync();
        vm.ContextMenuPageNumber = 2;
        await vm.RemoveCurrentPageCommand.Execute();
        await vm.UndoCommand.Execute();
        vm.TotalPages.Should().Be(PageCount);
        await vm.ApplyAllRedactionsCommand!.Execute();
        AssertOnlyTheMarkedSecretIsGone(output);
    }

    [FixedAvaloniaFact(Timeout = 120000)]
    public async Task DeletePagesSoTheMarkedPageIsLastButOne_ThenApply_StillRemovesTheMarkedSecret()
    {
        var (vm, output) = await OpenAndMarkAsync();
        vm.ContextMenuPageNumber = 1;
        await vm.RemoveCurrentPageCommand.Execute();
        vm.ContextMenuPageNumber = 1;
        await vm.RemoveCurrentPageCommand.Execute();
        await vm.ApplyAllRedactionsCommand!.Execute();
        AssertOnlyTheMarkedSecretIsGone(output, 1, 2);
    }
}
