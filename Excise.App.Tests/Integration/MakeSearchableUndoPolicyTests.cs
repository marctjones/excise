using Excise.Core.Signatures;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Excise.App.Services;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.Ocr;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Excise.App.Tests.Integration;

/// <summary>
/// Make Searchable is the one in-place operation that cannot be undone (#1812), so it must (a) clear the
/// undo history rather than leave entries that no longer apply, and (b) leave the document reading as
/// CHANGED. It did not: a searchable copy that was never saved read as "no unsaved changes", and closing
/// it discarded the OCR layer without a prompt. A run cancelled after some pages were written is the
/// same situation (#1692).
/// </summary>
[Collection("AvaloniaTests")]
public class MakeSearchableUndoPolicyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"excise-searchpolicy-{Guid.NewGuid():N}");
    public MakeSearchableUndoPolicyTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private async Task<(MainWindowViewModel Vm, List<ToastService.ToastEventArgs> Toasts)> OpenWithOneUndoEntryAsync()
    {
        var path = Path.Combine(_dir, "doc.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 2);

        var toasts = new List<ToastService.ToastEventArgs>();
        var toastService = new ToastService();
        toastService.ToastRequested += (_, args) => toasts.Add(args);
        var loggerFactory = NullLoggerFactory.Instance;
        var vm = MainWindowViewModelTestFactory.Create(
            NullLogger<MainWindowViewModel>.Instance, loggerFactory,
            new PdfDocumentService(NullLogger<PdfDocumentService>.Instance),
            new RedactionService(NullLogger<RedactionService>.Instance, loggerFactory),
            new PdfTextExtractionService(NullLogger<PdfTextExtractionService>.Instance),
            new PdfSearchService(NullLogger<PdfSearchService>.Instance),
            new SignatureVerificationService(null),
            new FilenameSuggestionService(),
            toastService);
        await vm.LoadDocumentAsync(path);

        await vm.RotatePageRightCommand.Execute();            // one real undo entry
        vm.CanUndo.Should().BeTrue("precondition: there is an entry for the operation to clear");
        return (vm, toasts);
    }

    private static SearchableDocumentResult Wrote(int pages) =>
        new(pages, 0, pages * 10, 0, Array.Empty<SearchablePageResult>());

    [Fact]
    public async Task WritingATextLayer_ClearsTheUndoHistory_AndMarksTheDocumentChanged()
    {
        var (vm, _) = await OpenWithOneUndoEntryAsync();
        var dirtyBefore = vm.HasUnsavedDocumentChanges;

        await vm.OnMakeSearchableCompletedAsync(Wrote(pages: 2));

        vm.CanUndo.Should().BeFalse("the text layer cannot be undone, so stale entries must not linger");
        vm.HasUnsavedDocumentChanges.Should().BeTrue(
            "a document whose OCR layer was never saved must not read as unchanged (#1812)");
        dirtyBefore.Should().BeTrue("control: the rotation already made it dirty, so assert the count instead");
        vm.FileState.PageEditsCount.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task ANoOpRun_LeavesHistoryAndDirtyStateAlone()
    {
        var (vm, _) = await OpenWithOneUndoEntryAsync();
        var editsBefore = vm.FileState.PageEditsCount;

        await vm.OnMakeSearchableCompletedAsync(new SearchableDocumentResult(0, 2, 0, 0, Array.Empty<SearchablePageResult>()));

        vm.CanUndo.Should().BeTrue("nothing was written, so the earlier undo entry still applies");
        vm.FileState.PageEditsCount.Should().Be(editsBefore);
    }

    [Fact]
    public async Task ARunCancelledAfterSomePages_IsTreatedLikeAFinishedRun_AndSaysWhatWasLeftBehind()
    {
        var (vm, toasts) = await OpenWithOneUndoEntryAsync();
        var editsBefore = vm.FileState.PageEditsCount;

        await vm.OnMakeSearchableCancelledAsync(pagesDone: 3);

        vm.CanUndo.Should().BeFalse();
        vm.FileState.PageEditsCount.Should().Be(editsBefore + 1, "the partial layer is a real change to the document");
        toasts.Should().Contain(t => t.Message == "Make Searchable cancelled"
                && (t.Details ?? "").Contains("3 page(s) already have a searchable text layer"),
            "the user is told a cancel left pages written");
    }

    [Fact]
    public async Task ARunCancelledBeforeAnyPage_ChangesNothing()
    {
        var (vm, toasts) = await OpenWithOneUndoEntryAsync();
        var editsBefore = vm.FileState.PageEditsCount;

        await vm.OnMakeSearchableCancelledAsync(pagesDone: 0);

        vm.CanUndo.Should().BeTrue();
        vm.FileState.PageEditsCount.Should().Be(editsBefore);
        toasts.Should().NotContain(t => t.Message == "Make Searchable cancelled");
    }
}
