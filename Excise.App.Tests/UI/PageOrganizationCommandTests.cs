using System;
using System.Collections.Generic;
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
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #816 batch 1 — executes the REAL page-organization toolbar/menu COMMANDS
/// (not the underlying async methods) and asserts the real page-level effect.
/// Prior coverage proved these methods only by calling them directly or by
/// asserting the command object is non-null; a button mis-wired to the wrong
/// method would have passed. Each test here does
/// <c>await vm.&lt;Name&gt;Command.Execute()</c> and verifies order/count/rotation
/// or the saved output.
///
/// The file/folder-picker commands (Add/Insert/Combine/Split/Extract) reach the
/// dialog through the picker seams on the view-model
/// (<c>PickPdfFilesOverride</c> / <c>PickSavePdfPathOverride</c> /
/// <c>PickFolderOverride</c>, #816) — headless Avalonia has no desktop lifetime
/// and its storage interfaces are sealed against user implementation, so these
/// path-returning delegates are the injection point.
/// </summary>
[Collection("AvaloniaTests")]
public class PageOrganizationCommandTests
{
    // ── Rotation — the audit canary. Assert the exact task-specified values. ──
    [FixedAvaloniaFact]
    public async Task RotatePageLeftCommand_RotatesCurrentPageMinus90()
    {
        var (dir, src) = NewDir("rot-left");
        TestPdfGenerator.CreateSimpleTextPdf(src, "Rotate me");
        var (vm, window) = await OpenAsync(src);

        var before = vm.PdfCoreDocument!.GetPage(1).Rotation;
        await vm.RotatePageLeftCommand.Execute();
        vm.PdfCoreDocument!.GetPage(1).Rotation.Should().Be((before + 270) % 360);

        Close(window, dir);
    }

    [FixedAvaloniaFact]
    public async Task RotatePage180Command_RotatesCurrentPage180()
    {
        var (dir, src) = NewDir("rot-180");
        TestPdfGenerator.CreateSimpleTextPdf(src, "Rotate me");
        var (vm, window) = await OpenAsync(src);

        var before = vm.PdfCoreDocument!.GetPage(1).Rotation;
        await vm.RotatePage180Command.Execute();
        vm.PdfCoreDocument!.GetPage(1).Rotation.Should().Be((before + 180) % 360);

        Close(window, dir);
    }

    // ── Move current page ─────────────────────────────────────────────────────
    [FixedAvaloniaFact]
    public async Task MoveCurrentPageEarlierCommand_MovesCurrentPageToPriorIndex()
    {
        var (dir, src) = NewDir("move-cur-earlier");
        TestPdfGenerator.CreateMultiPagePdf(src, pageCount: 3);
        var (vm, window) = await OpenAsync(src);
        vm.CurrentPageIndex = 2; // Page 3

        await vm.MoveCurrentPageEarlierCommand.Execute();

        // [P1, P2, P3] → move P3 earlier ⇒ [P1, P3, P2]
        PageText(vm, 2).Should().Contain("Page 3 Content");
        vm.CurrentPageIndex.Should().Be(1, "the moved page follows itself to its new index");

        Close(window, dir);
    }

    [FixedAvaloniaFact]
    public async Task MoveCurrentPageLaterCommand_MovesCurrentPageToNextIndex()
    {
        var (dir, src) = NewDir("move-cur-later");
        TestPdfGenerator.CreateMultiPagePdf(src, pageCount: 3);
        var (vm, window) = await OpenAsync(src);
        vm.CurrentPageIndex = 1; // Page 2

        await vm.MoveCurrentPageLaterCommand.Execute();

        // [P1, P2, P3] → move P2 later ⇒ [P1, P3, P2]
        PageText(vm, 3).Should().Contain("Page 2 Content");
        vm.CurrentPageIndex.Should().Be(2);

        Close(window, dir);
    }

    // ── Selected-page operations ─────────────────────────────────────────────
    [FixedAvaloniaFact]
    public async Task RemoveSelectedPagesCommand_RemovesTheMarkedPages()
    {
        var (dir, src) = NewDir("remove-selected");
        TestPdfGenerator.CreateMultiPagePdf(src, pageCount: 4);
        var (vm, window) = await OpenAsync(src);
        Select(vm, 1); // mark Page 2

        await vm.RemoveSelectedPagesCommand.Execute();

        vm.TotalPages.Should().Be(3);
        // [P1, P2, P3, P4] − P2 ⇒ [P1, P3, P4]
        PageText(vm, 2).Should().Contain("Page 3 Content");
        AllText(vm).Should().NotContain("Page 2 Content");

        Close(window, dir);
    }

    [FixedAvaloniaFact]
    public async Task MoveSelectedPagesEarlierCommand_MovesMarkedPageEarlier()
    {
        var (dir, src) = NewDir("move-selected-earlier");
        TestPdfGenerator.CreateMultiPagePdf(src, pageCount: 4);
        var (vm, window) = await OpenAsync(src);
        Select(vm, 2); // mark Page 3 (index 2)

        await vm.MoveSelectedPagesEarlierCommand.Execute();

        // Page 3 moves from index 2 → index 1 ⇒ 1-based page 2 is now Page 3
        PageText(vm, 2).Should().Contain("Page 3 Content");

        Close(window, dir);
    }

    [FixedAvaloniaFact]
    public async Task MoveSelectedPagesLaterCommand_MovesMarkedPageLater()
    {
        var (dir, src) = NewDir("move-selected-later");
        TestPdfGenerator.CreateMultiPagePdf(src, pageCount: 4);
        var (vm, window) = await OpenAsync(src);
        Select(vm, 1); // mark Page 2 (index 1)

        await vm.MoveSelectedPagesLaterCommand.Execute();

        // Page 2 moves from index 1 → index 2 ⇒ 1-based page 3 is now Page 2
        PageText(vm, 3).Should().Contain("Page 2 Content");

        Close(window, dir);
    }

    [FixedAvaloniaFact]
    public async Task ClearSelectedPagesCommand_UnmarksAllSelections()
    {
        var (dir, src) = NewDir("clear-selected");
        TestPdfGenerator.CreateMultiPagePdf(src, pageCount: 3);
        var (vm, window) = await OpenAsync(src);
        Select(vm, 0);
        Select(vm, 2);
        vm.SelectedPageCount.Should().Be(2);

        await vm.ClearSelectedPagesCommand.Execute();

        vm.SelectedPageCount.Should().Be(0);
        vm.PageThumbnails.Should().OnlyContain(t => !t.IsMarkedForPageOperation);

        Close(window, dir);
    }

    // ── Add / Insert (picker-driven, seam-injected source path) ──────────────
    [FixedAvaloniaFact]
    public async Task AddPagesCommand_AppendsInsertedPageAtEnd()
    {
        var (dir, src) = NewDir("add-pages");
        TestPdfGenerator.CreateMultiPagePdf(src, pageCount: 3);
        var insertPath = Path.Combine(dir, "insert.pdf");
        TestPdfGenerator.CreateSimpleTextPdf(insertPath, "INSERTED_MARKER");
        var (vm, window) = await OpenAsync(src);
        vm.PickPdfFilesOverride = _ => Task.FromResult<IReadOnlyList<string>>(new[] { insertPath });

        await vm.AddPagesCommand.Execute();

        vm.TotalPages.Should().Be(4);
        PageText(vm, 4).Should().Contain("INSERTED_MARKER", "AddPages appends at the end");

        Close(window, dir);
    }

    [FixedAvaloniaFact]
    public async Task InsertPagesBeforeCurrentCommand_InsertsAtCurrentIndex()
    {
        var (dir, src) = NewDir("insert-before");
        TestPdfGenerator.CreateMultiPagePdf(src, pageCount: 3);
        var insertPath = Path.Combine(dir, "insert.pdf");
        TestPdfGenerator.CreateSimpleTextPdf(insertPath, "INSERTED_MARKER");
        var (vm, window) = await OpenAsync(src);
        vm.CurrentPageIndex = 1; // before Page 2 ⇒ new index 1
        vm.PickPdfFilesOverride = _ => Task.FromResult<IReadOnlyList<string>>(new[] { insertPath });

        await vm.InsertPagesBeforeCurrentCommand.Execute();

        vm.TotalPages.Should().Be(4);
        // [P1, INSERTED, P2, P3]
        PageText(vm, 2).Should().Contain("INSERTED_MARKER");
        PageText(vm, 3).Should().Contain("Page 2 Content");

        Close(window, dir);
    }

    [FixedAvaloniaFact]
    public async Task InsertPagesAfterCurrentCommand_InsertsAfterCurrentIndex()
    {
        var (dir, src) = NewDir("insert-after");
        TestPdfGenerator.CreateMultiPagePdf(src, pageCount: 3);
        var insertPath = Path.Combine(dir, "insert.pdf");
        TestPdfGenerator.CreateSimpleTextPdf(insertPath, "INSERTED_MARKER");
        var (vm, window) = await OpenAsync(src);
        vm.CurrentPageIndex = 1; // after Page 2 ⇒ new index 2
        vm.PickPdfFilesOverride = _ => Task.FromResult<IReadOnlyList<string>>(new[] { insertPath });

        await vm.InsertPagesAfterCurrentCommand.Execute();

        vm.TotalPages.Should().Be(4);
        // [P1, P2, INSERTED, P3]
        PageText(vm, 3).Should().Contain("INSERTED_MARKER");
        PageText(vm, 4).Should().Contain("Page 3 Content");

        Close(window, dir);
    }

    // ── Extract ──────────────────────────────────────────────────────────────
    [FixedAvaloniaFact]
    public async Task ExtractCurrentPageCommand_WritesOnlyTheCurrentPage()
    {
        var (dir, src) = NewDir("extract-current");
        TestPdfGenerator.CreateMultiPagePdf(src, pageCount: 3);
        var outPath = Path.Combine(dir, "extracted.pdf");
        var (vm, window) = await OpenAsync(src);
        vm.CurrentPageIndex = 1; // Page 2
        vm.PickSavePdfPathOverride = () => Task.FromResult<string?>(outPath);

        await vm.ExtractCurrentPageCommand.Execute();

        File.Exists(outPath).Should().BeTrue();
        using var extracted = PdfDocument.Open(outPath);
        extracted.PageCount.Should().Be(1);
        extracted.GetPage(1).Text.Should().Contain("Page 2 Content");
        extracted.GetPage(1).Text.Should().NotContain("Page 1 Content");

        Close(window, dir);
    }

    [FixedAvaloniaFact]
    public async Task ExtractSelectedPagesCommand_WritesExactlyTheMarkedPages()
    {
        var (dir, src) = NewDir("extract-selected");
        TestPdfGenerator.CreateMultiPagePdf(src, pageCount: 4);
        var outPath = Path.Combine(dir, "extracted.pdf");
        var (vm, window) = await OpenAsync(src);
        Select(vm, 0); // Page 1
        Select(vm, 2); // Page 3
        vm.PickSavePdfPathOverride = () => Task.FromResult<string?>(outPath);

        await vm.ExtractSelectedPagesCommand.Execute();

        File.Exists(outPath).Should().BeTrue();
        using var extracted = PdfDocument.Open(outPath);
        extracted.PageCount.Should().Be(2);
        var text = extracted.GetPage(1).Text + "|" + extracted.GetPage(2).Text;
        text.Should().Contain("Page 1 Content");
        text.Should().Contain("Page 3 Content");
        text.Should().NotContain("Page 2 Content");

        Close(window, dir);
    }

    // ── Combine ──────────────────────────────────────────────────────────────
    [FixedAvaloniaFact]
    public async Task CombineDocumentsCommand_MergesBothSourcesIntoOneFile()
    {
        var (dir, src) = NewDir("combine");
        var a = Path.Combine(dir, "a.pdf");
        var b = Path.Combine(dir, "b.pdf");
        TestPdfGenerator.CreateTextOnlyPdf(a, "ALPHA_DOC");
        TestPdfGenerator.CreateTextOnlyPdf(b, "BRAVO_DOC");
        var outPath = Path.Combine(dir, "combined.pdf");

        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        vm.PickPdfFilesOverride = _ => Task.FromResult<IReadOnlyList<string>>(new[] { a, b });
        vm.PickSavePdfPathOverride = () => Task.FromResult<string?>(outPath);

        await vm.CombineDocumentsCommand.Execute();

        File.Exists(outPath).Should().BeTrue();
        using var combined = PdfDocument.Open(outPath);
        combined.PageCount.Should().Be(2, "combine sums the source page counts");
        var text = combined.GetPage(1).Text + "|" + combined.GetPage(2).Text;
        text.Should().Contain("ALPHA_DOC");
        text.Should().Contain("BRAVO_DOC");

        Close(window, dir);
    }

    // ── Split (default split spec "1" ⇒ one page per file, via NullDialog) ────
    [FixedAvaloniaFact]
    public async Task SplitDocumentCommand_WritesOnePageFilePerPage()
    {
        var (dir, src) = NewDir("split");
        TestPdfGenerator.CreateMultiPagePdf(src, pageCount: 4);
        var outFolder = Path.Combine(dir, "out");
        Directory.CreateDirectory(outFolder);
        var (vm, window) = await OpenAsync(src);
        vm.PickFolderOverride = () => Task.FromResult<string?>(outFolder);

        await vm.SplitDocumentCommand.Execute();

        var files = Directory.GetFiles(outFolder, "*.pdf").OrderBy(f => f).ToList();
        files.Should().HaveCount(4, "the default \"1\" spec splits one page per file");
        var allText = string.Empty;
        foreach (var f in files)
        {
            using var part = PdfDocument.Open(f);
            part.PageCount.Should().Be(1, "each split file carries exactly one page");
            allText += part.GetPage(1).Text + "|";
        }
        allText.Should().Contain("Page 1 Content");
        allText.Should().Contain("Page 4 Content");

        Close(window, dir);
    }

    // ── Independent-oracle siblings (#856) ───────────────────────────────────
    // The four command tests above reopen their written file with
    // PdfDocument.Open — excise reading its own output, which per CLAUDE.md
    // proves only that its bugs are self-consistent. An extract that writes a
    // page tree only excise's parser tolerates passes all of them.
    //
    // These re-read the SAME writes with tools that are not excise: qpdf for
    // page count and structural validity, mutool for per-page text. They are
    // siblings, not replacements — the always-on assertions above keep running
    // when the tools are absent (CI), so coverage never depends on them.

    private static void RequireOracles()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
    }

    /// mutool's text output carries layout whitespace; fixture markers are single tokens.
    private static string StripWs(string? text) =>
        new string((text ?? string.Empty).Where(ch => !char.IsWhiteSpace(ch)).ToArray());

    private static void AssertStructurallyValid(string path)
    {
        var check = QpdfReferenceTool.Check(path);
        check.Should().NotBeNull("qpdf reported IsAvailable but produced no result");
        check!.Value.Success.Should().BeTrue(
            $"{Path.GetFileName(path)} must satisfy an independent structural check. qpdf said:\n{check.Value.Output}");
    }

    [FixedAvaloniaFact]
    public async Task ExtractCurrentPageCommand_WrittenFile_IsVerifiedByIndependentTools()
    {
        RequireOracles();
        var (dir, src) = NewDir("extract-current-oracle");
        TestPdfGenerator.CreateMultiPagePdf(src, pageCount: 3);
        var outPath = Path.Combine(dir, "extracted.pdf");
        var (vm, window) = await OpenAsync(src);
        vm.CurrentPageIndex = 1; // Page 2
        vm.PickSavePdfPathOverride = () => Task.FromResult<string?>(outPath);

        await vm.ExtractCurrentPageCommand.Execute();

        QpdfReferenceTool.PageCount(outPath).Should().Be(1,
            "an independent parser must agree the extract is a one-page document");
        StripWs(MutoolTextExtractor.ExtractPage(outPath, 1)).Should().Contain("Page2Content");
        StripWs(MutoolTextExtractor.ExtractPage(outPath, 1)).Should().NotContain("Page1Content",
            "extracting page 2 must not smuggle page 1's content along with it");
        AssertStructurallyValid(outPath);

        Close(window, dir);
    }

    [FixedAvaloniaFact]
    public async Task ExtractSelectedPagesCommand_WrittenFile_IsVerifiedByIndependentTools()
    {
        RequireOracles();
        var (dir, src) = NewDir("extract-selected-oracle");
        TestPdfGenerator.CreateMultiPagePdf(src, pageCount: 4);
        var outPath = Path.Combine(dir, "extracted.pdf");
        var (vm, window) = await OpenAsync(src);
        Select(vm, 0); // Page 1
        Select(vm, 2); // Page 3
        vm.PickSavePdfPathOverride = () => Task.FromResult<string?>(outPath);

        await vm.ExtractSelectedPagesCommand.Execute();

        QpdfReferenceTool.PageCount(outPath).Should().Be(2);
        // Order matters: marked pages must land in document order, not selection order.
        StripWs(MutoolTextExtractor.ExtractPage(outPath, 1)).Should().Contain("Page1Content");
        StripWs(MutoolTextExtractor.ExtractPage(outPath, 2)).Should().Contain("Page3Content");
        var both = StripWs(MutoolTextExtractor.ExtractPage(outPath, 1))
                 + StripWs(MutoolTextExtractor.ExtractPage(outPath, 2));
        both.Should().NotContain("Page2Content", "an unmarked page must not be written");
        AssertStructurallyValid(outPath);

        Close(window, dir);
    }

    [FixedAvaloniaFact]
    public async Task CombineDocumentsCommand_WrittenFile_IsVerifiedByIndependentTools()
    {
        RequireOracles();
        var (dir, _) = NewDir("combine-oracle");
        var a = Path.Combine(dir, "a.pdf");
        var b = Path.Combine(dir, "b.pdf");
        TestPdfGenerator.CreateTextOnlyPdf(a, "ALPHA_DOC");
        TestPdfGenerator.CreateTextOnlyPdf(b, "BRAVO_DOC");
        var outPath = Path.Combine(dir, "combined.pdf");

        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        vm.PickPdfFilesOverride = _ => Task.FromResult<IReadOnlyList<string>>(new[] { a, b });
        vm.PickSavePdfPathOverride = () => Task.FromResult<string?>(outPath);

        await vm.CombineDocumentsCommand.Execute();

        QpdfReferenceTool.PageCount(outPath).Should().Be(2);
        StripWs(MutoolTextExtractor.ExtractPage(outPath, 1)).Should().Contain("ALPHA_DOC");
        StripWs(MutoolTextExtractor.ExtractPage(outPath, 2)).Should().Contain("BRAVO_DOC");
        AssertStructurallyValid(outPath);

        Close(window, dir);
    }

    [FixedAvaloniaFact]
    public async Task SplitDocumentCommand_WrittenFiles_AreVerifiedByIndependentTools()
    {
        RequireOracles();
        var (dir, src) = NewDir("split-oracle");
        TestPdfGenerator.CreateMultiPagePdf(src, pageCount: 4);
        var outFolder = Path.Combine(dir, "out");
        Directory.CreateDirectory(outFolder);
        var (vm, window) = await OpenAsync(src);
        vm.PickFolderOverride = () => Task.FromResult<string?>(outFolder);

        await vm.SplitDocumentCommand.Execute();

        var files = Directory.GetFiles(outFolder, "*.pdf").OrderBy(f => f).ToList();
        files.Should().HaveCount(4);
        for (int i = 0; i < files.Count; i++)
        {
            QpdfReferenceTool.PageCount(files[i]).Should().Be(1,
                $"fragment {i + 1} must hold exactly one page according to an independent parser");
            StripWs(MutoolTextExtractor.ExtractPage(files[i], 1)).Should().Contain($"Page{i + 1}Content",
                $"fragment {i + 1} must carry source page {i + 1} — a burst producing the right " +
                "number of one-page files with the wrong content in them is what a count-only check misses");
            AssertStructurallyValid(files[i]);
        }

        Close(window, dir);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────
    private static (string dir, string src) NewDir(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ExcisePageOrgCmd", $"{tag}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return (dir, Path.Combine(dir, "source.pdf"));
    }

    private static async Task<(MainWindowViewModel vm, MainWindow window)> OpenAsync(string path)
    {
        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await vm.LoadDocumentAsync(path);
        return (vm, window);
    }

    private static void Select(MainWindowViewModel vm, int pageIndex)
    {
        vm.PageThumbnails.Single(t => t.PageIndex == pageIndex).IsMarkedForPageOperation = true;
    }

    private static string PageText(MainWindowViewModel vm, int oneBasedPage) =>
        vm.PdfCoreDocument!.GetPage(oneBasedPage).Text;

    private static string AllText(MainWindowViewModel vm) =>
        string.Join("|", Enumerable.Range(1, vm.TotalPages)
            .Select(n => vm.PdfCoreDocument!.GetPage(n).Text));

    private static void Close(MainWindow window, string dir)
    {
        window.Close();
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { }
    }
}
