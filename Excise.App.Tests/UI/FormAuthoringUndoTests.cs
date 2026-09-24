using System;
using System.Diagnostics;
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
/// Adding a form field, by drawing one or by Auto-Detect, is undoable (#1811). The saved file is read
/// with qpdf (structure check and object dump), not excise's own parser.
/// </summary>
[Collection("AvaloniaTests")]
public class FormAuthoringUndoTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"excise-formundo-{Guid.NewGuid():N}");
    public FormAuthoringUndoTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static string QpdfJson(string pdf)
    {
        var psi = new ProcessStartInfo("qpdf", $"--json=2 \"{pdf}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi)!;
        // #1068/#1516: drain both pipes concurrently and bound the wait.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("qpdf did not exit within 30s (#1516).");
        }
        _ = stderr.GetAwaiter().GetResult();
        return stdout.GetAwaiter().GetResult();
    }

    private async Task<(MainWindowViewModel Vm, MainWindow Window)> OpenAsync(string content = "")
    {
        var path = FormAuthoringTests.WritePdf(FormAuthoringTests.BarePdf(content));
        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1000, Height = 700 };
        window.Show();
        await Task.Delay(100);
        await vm.LoadDocumentAsync(path);
        return (vm, window);
    }

    private static void RequireQpdf() => Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");

    [FixedAvaloniaFact(Timeout = 90000)]
    public async Task DrawnField_Undo_RemovesItFromTheSavedFile_AndTheFileStaysValid()
    {
        RequireQpdf();
        var (vm, window) = await OpenAsync();
        vm.FormAuthoringFieldType = PdfFieldType.Text;
        vm.OnFormFieldRectDrawn(new PdfRectangle(100, 600, 300, 620), 1);
        var name = vm.PdfCoreDocument!.GetAcroForm()!.Fields.Single().FullName;
        vm.UndoMenuHeader.Should().StartWith("_Undo Add form field");

        await vm.UndoCommand.Execute();

        vm.PdfCoreDocument!.GetAcroForm()?.Fields.Should().BeNullOrEmpty();
        var saved = Path.Combine(_dir, "undone.pdf");
        await vm.SaveFileAsAsync(saved);
        QpdfReferenceTool.Check(saved)!.Value.Success.Should().BeTrue("undoing the field must leave a structurally valid file");
        var json = QpdfJson(saved);
        json.Should().NotContain(name, "the undone field's name must not survive in the saved file");
        json.Should().NotContain("/Widget", "no orphan widget may stay on the page");
        window.Close();
    }

    [FixedAvaloniaFact(Timeout = 90000)]
    public async Task DrawnField_UndoThenRedo_PutsTheSameFieldBack()
    {
        RequireQpdf();
        var (vm, window) = await OpenAsync();
        vm.FormAuthoringFieldType = PdfFieldType.Text;
        vm.OnFormFieldRectDrawn(new PdfRectangle(100, 600, 300, 620), 1);
        var name = vm.PdfCoreDocument!.GetAcroForm()!.Fields.Single().FullName;

        await vm.UndoCommand.Execute();
        vm.CanRedo.Should().BeTrue();
        await vm.RedoCommand.Execute();

        vm.PdfCoreDocument!.GetAcroForm()!.Fields.Single().FullName.Should().Be(name);
        var saved = Path.Combine(_dir, "redone.pdf");
        await vm.SaveFileAsAsync(saved);
        QpdfReferenceTool.Check(saved)!.Value.Success.Should().BeTrue();
        QpdfJson(saved).Should().Contain(name);
        window.Close();
    }

    [FixedAvaloniaFact(Timeout = 90000)]
    public async Task AutoDetect_Undo_RemovesEveryFieldItAdded()
    {
        RequireQpdf();
        var (vm, window) = await OpenAsync("100 700 m 300 700 l S\n320 700 12 12 re S");
        var created = await vm.AutoDetectFieldsCommand.Execute().FirstAsync();
        created.Should().Be(2);
        vm.UndoMenuHeader.Should().Be("_Undo Auto-detect 2 form field(s)");

        await vm.UndoCommand.Execute();

        vm.PdfCoreDocument!.GetAcroForm()?.Fields.Should().BeNullOrEmpty();
        var saved = Path.Combine(_dir, "auto-undone.pdf");
        await vm.SaveFileAsAsync(saved);
        QpdfReferenceTool.Check(saved)!.Value.Success.Should().BeTrue();
        QpdfJson(saved).Should().NotContain("/Widget");
        window.Close();
    }

    [FixedAvaloniaFact(Timeout = 90000)]
    public async Task AutoDetect_UndoThenRedo_AddsThemAgain()
    {
        RequireQpdf();
        var (vm, window) = await OpenAsync("100 700 m 300 700 l S\n320 700 12 12 re S");
        await vm.AutoDetectFieldsCommand.Execute().FirstAsync();

        await vm.UndoCommand.Execute();
        await vm.RedoCommand.Execute();

        vm.PdfCoreDocument!.GetAcroForm()!.Fields.Should().HaveCount(2);
        var saved = Path.Combine(_dir, "auto-redone.pdf");
        await vm.SaveFileAsAsync(saved);
        QpdfReferenceTool.Check(saved)!.Value.Success.Should().BeTrue();
        window.Close();
    }
}
