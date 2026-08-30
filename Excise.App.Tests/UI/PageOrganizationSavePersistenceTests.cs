using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// Does a page edit made through the GUI actually SURVIVE a save?
///
/// PageOrganizationCommandTests executes all 19 real page-organization
/// commands, but contains ZERO save calls — every assertion reads
/// <c>vm.PdfCoreDocument</c>, i.e. the in-memory document. A bug where an
/// insert/remove/rotate looks correct in the open document but is not written
/// to the file would have passed that entire file. Only MOVE had a
/// GUI-level save→reopen round-trip (PageOrganizationWorkflowTests); add,
/// insert, remove-via-command, undo-of-remove and rotate had none.
///
/// So every test here is: drive the REAL command → save through the REAL save
/// command → reopen the file from disk → assert on the REOPENED document.
///
/// Two deliberate choices:
///
///  * Saving goes through <c>vm.SaveAsCommand</c> with
///    <c>PickSavePdfPathOverride</c>, not a direct <c>SaveFileAsAsync</c> call.
///    Other suites (TypewriterWorkflowTests) call the method directly; this
///    file follows PageOrganizationCommandTests' stricter rule instead — "a
///    button mis-wired to the wrong method would have passed" applies just as
///    much to Save as to Rotate.
///  * Assertions read the reopened <see cref="PdfDocument"/>, never the
///    view-model. Reading the view-model after a save proves only that memory
///    still holds what it held before.
///
/// Most assertions use excise's own extractor, which is a self-oracle. One
/// test has a mutool-gated sibling that re-reads the saved bytes with a tool
/// that is not excise — the RedactionAndSearchCommandTests pattern: an
/// always-on assertion so CI still verifies something, plus a tool-gated
/// independent confirmation.
/// </summary>
[Collection("AvaloniaTests")]
public class PageOrganizationSavePersistenceTests
{
    // ── add / insert ─────────────────────────────────────────────────────────

    [FixedAvaloniaFact]
    public async Task AddPagesCommand_ThenSaveAs_AppendedPageSurvivesReopen()
    {
        var (dir, src) = NewDir("add-persist");
        TestPdfGenerator.CreateMultiPagePdf(src, pageCount: 3);
        var insertPath = Path.Combine(dir, "insert.pdf");
        TestPdfGenerator.CreateSimpleTextPdf(insertPath, "INSERTED_MARKER");
        var (vm, window) = await OpenAsync(src);
        vm.PickPdfFilesOverride = _ => Task.FromResult<IReadOnlyList<string>>(new[] { insertPath });

        await vm.AddPagesCommand.Execute();
        var saved = await SaveAsAsync(vm, dir, "added.pdf");

        using var reopened = PdfDocument.Open(File.ReadAllBytes(saved));
        reopened.PageCount.Should().Be(4, "the appended page must be written to the file, not just held in memory");
        reopened.GetPage(4).Text.Should().Contain("INSERTED_MARKER");
        reopened.GetPage(1).Text.Should().Contain("Page 1 Content", "the original pages must survive alongside it");

        Close(window, dir);
    }

    [FixedAvaloniaFact]
    public async Task InsertPagesBeforeCurrentCommand_ThenSaveAs_InsertedPageSurvivesReopenAtItsIndex()
    {
        var (dir, src) = NewDir("insert-persist");
        TestPdfGenerator.CreateMultiPagePdf(src, pageCount: 3);
        var insertPath = Path.Combine(dir, "insert.pdf");
        TestPdfGenerator.CreateSimpleTextPdf(insertPath, "INSERTED_MARKER");
        var (vm, window) = await OpenAsync(src);
        vm.CurrentPageIndex = 1;
        vm.PickPdfFilesOverride = _ => Task.FromResult<IReadOnlyList<string>>(new[] { insertPath });

        await vm.InsertPagesBeforeCurrentCommand.Execute();
        var saved = await SaveAsAsync(vm, dir, "inserted.pdf");

        using var reopened = PdfDocument.Open(File.ReadAllBytes(saved));
        reopened.PageCount.Should().Be(4);
        // [P1, INSERTED, P2, P3] — position matters, not just presence.
        reopened.GetPage(1).Text.Should().Contain("Page 1 Content");
        reopened.GetPage(2).Text.Should().Contain("INSERTED_MARKER");
        reopened.GetPage(3).Text.Should().Contain("Page 2 Content");
        reopened.GetPage(4).Text.Should().Contain("Page 3 Content");

        Close(window, dir);
    }

    // ── remove ───────────────────────────────────────────────────────────────

    [FixedAvaloniaFact]
    public async Task RemoveSelectedPagesCommand_ThenSaveAs_RemovedPageIsGoneOnReopen()
    {
        var (dir, src) = NewDir("remove-persist");
        TestPdfGenerator.CreateMultiPagePdf(src, pageCount: 4);
        var (vm, window) = await OpenAsync(src);
        Select(vm, 1); // mark Page 2

        await vm.RemoveSelectedPagesCommand.Execute();
        var saved = await SaveAsAsync(vm, dir, "removed.pdf");

        using var reopened = PdfDocument.Open(File.ReadAllBytes(saved));
        reopened.PageCount.Should().Be(3, "the removal must be written to the file");
        AllTextOf(reopened).Should().NotContain("Page 2 Content",
            "a page deleted in the GUI must not come back when the file is reopened");
        reopened.GetPage(2).Text.Should().Contain("Page 3 Content", "the survivors must close the gap in order");

        Close(window, dir);
    }

    [FixedAvaloniaFact]
    public async Task RemoveSelectedPagesCommand_ThenSaveAs_RemovedPageIsGoneAccordingToAnIndependentExtractor()
    {
        // Both tools, because this test uses both: qpdf for the page count and
        // mutool for the text. The guard must match the allowlist's
        // [requires: ...] marker exactly, or the marker is a lie.
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");

        var (dir, src) = NewDir("remove-oracle");
        TestPdfGenerator.CreateMultiPagePdf(src, pageCount: 4);
        var (vm, window) = await OpenAsync(src);
        Select(vm, 1);

        await vm.RemoveSelectedPagesCommand.Execute();
        var saved = await SaveAsAsync(vm, dir, "removed-oracle.pdf");

        // excise confirming excise removed the page proves only that its bugs
        // are self-consistent (CLAUDE.md). Ask a reader that is not excise.
        QpdfReferenceTool.PageCount(saved).Should().Be(3,
            "an independent parser must agree the page is gone from the saved file");

        var everyPage = string.Join("|", Enumerable.Range(1, 3)
            .Select(p => MutoolTextExtractor.ExtractPage(saved, p) ?? string.Empty));
        everyPage.Should().NotContain("Page 2 Content",
            "an independent extractor must not find the removed page's content anywhere in the saved file");

        Close(window, dir);
    }

    [FixedAvaloniaFact]
    public async Task RemoveThenUndo_ThenSaveAs_ReinstatedPageIsPresentOnReopen()
    {
        var (dir, src) = NewDir("remove-undo-persist");
        TestPdfGenerator.CreateMultiPagePdf(src, pageCount: 4);
        var (vm, window) = await OpenAsync(src);
        Select(vm, 1);

        await vm.RemoveSelectedPagesCommand.Execute();
        vm.CanUndo.Should().BeTrue("a page removal must be undoable");
        await vm.UndoCommand.Execute();

        var saved = await SaveAsAsync(vm, dir, "undone.pdf");

        // UndoRedoWorkflowTests asserts undo only against in-memory state
        // (vm.TotalPages). This asserts the undone state is what gets WRITTEN —
        // an undo that restores the view-model but not the document that is
        // saved would silently discard the user's page on the next save.
        using var reopened = PdfDocument.Open(File.ReadAllBytes(saved));
        reopened.PageCount.Should().Be(4, "undo must restore the page in the document that is saved, not just on screen");
        AllTextOf(reopened).Should().Contain("Page 2 Content", "the re-inserted page must carry its original content");
        reopened.GetPage(2).Text.Should().Contain("Page 2 Content", "and be restored at its original index");

        Close(window, dir);
    }

    // ── rotate ───────────────────────────────────────────────────────────────

    [FixedAvaloniaFact]
    public async Task RotatePage180Command_ThenSaveAs_RotationSurvivesReopen()
    {
        var (dir, src) = NewDir("rotate-persist");
        TestPdfGenerator.CreateSimpleTextPdf(src, "Rotate me");
        var (vm, window) = await OpenAsync(src);
        var before = vm.PdfCoreDocument!.GetPage(1).Rotation;

        await vm.RotatePage180Command.Execute();
        var saved = await SaveAsAsync(vm, dir, "rotated.pdf");

        // The existing rotate tests read vm.PdfCoreDocument only, so /Rotate
        // persistence through a GUI save was never checked.
        using var reopened = PdfDocument.Open(File.ReadAllBytes(saved));
        reopened.GetPage(1).Rotation.Should().Be((before + 180) % 360,
            "the /Rotate the user applied must be written to the file");

        Close(window, dir);
    }

    // ── a REAL document, not a synthetic fixture ─────────────────────────────

    [FixedAvaloniaFact]
    public async Task RealWorldForm_RemoveThenSaveAs_DropsThePageAndKeepsTheAcroFormIntact()
    {
        // Synthetic three-page fixtures have no AcroForm, no outline, no
        // structure tree and no inherited page attributes — which is exactly
        // where page-tree edits break on real documents. irs-w9.pdf is a real
        // government form (6 pages, AcroForm) from the smoke corpus.
        var source = TryFindRepoFile("test-pdfs", "smoke", "irs-w9.pdf");
        Assert.SkipWhen(source == null, "smoke corpus not present (scripts/download-smoke-corpus.sh)");
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");

        var (dir, src) = NewDir("realworld-remove");
        File.Copy(source!, src, overwrite: true);
        var (vm, window) = await OpenAsync(src);

        var pagesBefore = vm.TotalPages;
        pagesBefore.Should().BeGreaterThan(1, "the fixture must be multi-page for a delete to mean anything");
        vm.PdfCoreDocument!.GetAcroForm().Should().NotBeNull("fixture precondition: this document has an AcroForm");

        Select(vm, 1);
        await vm.RemoveSelectedPagesCommand.Execute();
        var saved = await SaveAsAsync(vm, dir, "realworld-removed.pdf");

        using var reopened = PdfDocument.Open(File.ReadAllBytes(saved));
        reopened.PageCount.Should().Be(pagesBefore - 1, "exactly one page should be gone");

        // The point of using a real document: deleting a page must not take the
        // interactive form with it, and must not leave a page tree that only
        // excise can parse.
        reopened.GetAcroForm().Should().NotBeNull(
            "removing a page must not destroy the document's AcroForm — a synthetic fixture cannot catch this");

        var check = QpdfReferenceTool.Check(saved);
        check.Should().NotBeNull();
        check!.Value.Success.Should().BeTrue(
            $"an independent tool must still consider the document valid after a GUI page delete. qpdf said:\n{check.Value.Output}");

        Close(window, dir);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// Saves through the REAL save-as command, via the picker seam.
    private static async Task<string> SaveAsAsync(MainWindowViewModel vm, string dir, string fileName)
    {
        var outputPath = Path.Combine(dir, fileName);
        vm.PickSavePdfPathOverride = () => Task.FromResult<string?>(outputPath);
        await vm.SaveAsCommand.Execute();
        File.Exists(outputPath).Should().BeTrue("SaveAsCommand must write the chosen file");
        return outputPath;
    }

    private static (string dir, string src) NewDir(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ExcisePageOrgPersist", $"{tag}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return (dir, Path.Combine(dir, "source.pdf"));
    }

    private static async Task<(MainWindowViewModel vm, MainWindow window)> OpenAsync(string path)
    {
        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await vm.LoadDocumentAsync(path);
        return (vm, window);
    }

    private static void Select(MainWindowViewModel vm, int pageIndex) =>
        vm.PageThumbnails.Single(t => t.PageIndex == pageIndex).IsMarkedForPageOperation = true;

    private static string AllTextOf(PdfDocument doc) =>
        string.Join("|", Enumerable.Range(1, doc.PageCount).Select(n => doc.GetPage(n).Text));

    private static string? TryFindRepoFile(params string[] relativeParts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(relativeParts).ToArray());
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private static void Close(MainWindow window, string dir)
    {
        window.Close();
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { }
    }
}
