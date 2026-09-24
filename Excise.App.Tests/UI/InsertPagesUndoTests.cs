using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Excise.Rendering.Differential;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// Add Pages and Insert Pages Before/After are undoable (#1809). Judged on the SAVED file with qpdf
/// (page count) and mutool (which page holds which text), not with excise's own reader.
/// </summary>
[Collection("AvaloniaTests")]
public class InsertPagesUndoTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"excise-insundo-{Guid.NewGuid():N}");
    public InsertPagesUndoTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static string Squash(string? t) => new string((t ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());

    private async Task<(MainWindowViewModel Vm, MainWindow Window, string Source)> OpenAsync()
    {
        TestPdfGenerator.CreateMultiPagePdf(Path.Combine(_dir, "base.pdf"), pageCount: 3);
        var source = Path.Combine(_dir, "source.pdf");
        TestPdfGenerator.CreateSimpleTextPdf(source, "INSERTEDMARKER");
        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1000, Height = 700 };
        window.Show();
        await vm.LoadDocumentAsync(Path.Combine(_dir, "base.pdf"));
        return (vm, window, source);
    }

    private static void RequireOracles()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
    }

    [FixedAvaloniaFact(Timeout = 90000)]
    public async Task InsertPages_Undo_RemovesExactlyTheInsertedPages_FromTheSavedFile()
    {
        RequireOracles();
        var (vm, window, source) = await OpenAsync();

        await vm.InsertPagesFromFileAsync(source, insertAtIndex: 1);
        vm.TotalPages.Should().Be(4);
        vm.UndoMenuHeader.Should().Be("_Undo Insert pages");

        await vm.UndoCommand.Execute();
        vm.TotalPages.Should().Be(3);

        var saved = Path.Combine(_dir, "undone.pdf");
        await vm.SaveFileAsAsync(saved);
        QpdfReferenceTool.PageCount(saved).Should().Be(3, "the inserted page must be gone from the saved file");
        for (var p = 1; p <= 3; p++)
        {
            var text = Squash(MutoolTextExtractor.ExtractPage(saved, p));
            text.Should().Contain($"Page{p}Content", $"the original page {p} must be back in place");
            text.Should().NotContain("INSERTEDMARKER");
        }
        window.Close();
    }

    [FixedAvaloniaFact(Timeout = 90000)]
    public async Task InsertPages_UndoThenRedo_PutsTheInsertedPageBackWhereItWas()
    {
        RequireOracles();
        var (vm, window, source) = await OpenAsync();

        await vm.InsertPagesFromFileAsync(source, insertAtIndex: 1);
        await vm.UndoCommand.Execute();
        vm.CanRedo.Should().BeTrue();
        await vm.RedoCommand.Execute();

        var saved = Path.Combine(_dir, "redone.pdf");
        await vm.SaveFileAsAsync(saved);
        QpdfReferenceTool.PageCount(saved).Should().Be(4);
        Squash(MutoolTextExtractor.ExtractPage(saved, 2)).Should().Contain("INSERTEDMARKER",
            "redo re-inserts the page at the same position");
        Squash(MutoolTextExtractor.ExtractPage(saved, 3)).Should().Contain("Page2Content");
        window.Close();
    }

    [FixedAvaloniaFact(Timeout = 90000)]
    public async Task AddPages_AppendsAtTheEnd_AndIsUndoable()
    {
        RequireOracles();
        var (vm, window, source) = await OpenAsync();

        await vm.AddPagesFromFileAsync(source);
        vm.TotalPages.Should().Be(4);
        vm.UndoMenuHeader.Should().Be("_Undo Add pages");

        await vm.UndoCommand.Execute();

        var saved = Path.Combine(_dir, "add-undone.pdf");
        await vm.SaveFileAsAsync(saved);
        QpdfReferenceTool.PageCount(saved).Should().Be(3);
        Squash(MutoolTextExtractor.ExtractPage(saved, 3)).Should().Contain("Page3Content")
            .And.NotContain("INSERTEDMARKER");
        window.Close();
    }
}
