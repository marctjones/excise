using System.Reactive.Linq;
using AwesomeAssertions;
using Excise.App.Services;
using Excise.App.Tests.Utilities;
using Excise.App.Tests.Utilities.Fakes;
using Excise.App.ViewModels;
using Excise.Core.Writing;
using Excise.TestSupport;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #1550 — Document ▸ Reduce File Size… Always a new file, never the open one;
/// refused while there are unsaved edits; before/after size reported.
/// </summary>
[Collection("AvaloniaTests")]
public class ReduceFileSizeWorkflowTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), $"excise-reduce-{Guid.NewGuid():N}");

    public ReduceFileSizeWorkflowTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { }
    }

    private string NewPdf(string name)
    {
        var path = Path.Combine(_tempDir, name);
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 3);
        return path;
    }

    // ------------------------------------------------------------- dialog VM

    [Fact]
    public void DialogDefaultsToLossless_AndIsNotConfirmedUntilContinue()
    {
        var vm = new ReduceFileSizeDialogViewModel();

        vm.Selected.Preset.Should().Be(PdfOptimizationPreset.Lossless,
            "the default must be the one choice that cannot change how a page looks");
        vm.Confirmed.Should().BeFalse();
        vm.Confirm();
        vm.Confirmed.Should().BeTrue();
    }

    [Fact]
    public void DialogOffersEveryPreset_WithADescription()
    {
        ReduceFileSizeDialogViewModel.Choices.Select(c => c.Preset)
            .Should().BeEquivalentTo(Enum.GetValues<PdfOptimizationPreset>());
        ReduceFileSizeDialogViewModel.Choices.Should().OnlyContain(c => c.Description.Length > 20);
    }

    [Fact]
    public void ResultMessage_ShowsBeforeAndAfterSizes()
    {
        var message = MainWindowViewModel.DescribeReduceFileSizeResult(
            "/tmp/out.pdf",
            4 * 1024 * 1024,
            new PdfOptimizationResult { OutputSizeBytes = 1024 * 1024, ImagesDownsampled = 3 });

        message.Should().Contain($"{4.0:0.0} MB").And.Contain($"{1.0:0.0} MB").And.Contain("75% smaller")
            .And.Contain("/tmp/out.pdf").And.Contain("3 image(s) downsampled");
    }

    // ------------------------------------------------------------- wiring

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task Command_WritesASmallerCopyWhereTheUserChose_AndLeavesTheOpenFileAlone()
    {
        var pdf = NewPdf("source.pdf");
        var original = await File.ReadAllBytesAsync(pdf, TestContext.Current.CancellationToken);
        var output = Path.Combine(_tempDir, "chosen.pdf");
        var picker = new RecordingFilePicker().WillSave(output);
        var dialogs = new RecordingDialogService();
        var vm = MainWindowViewModelTestFactory.Create(filePicker: picker, dialogService: dialogs);
        await vm.LoadDocumentAsync(pdf);

        vm.ReduceFileSizePresetOverride = () => PdfOptimizationPreset.Lossless;
        await vm.ReduceFileSizeCommand.Execute();

        File.Exists(output).Should().BeTrue();
        picker.LastSaveRequest!.SuggestedFileName.Should().EndWith("source_reduced.pdf");
        (await File.ReadAllBytesAsync(pdf, TestContext.Current.CancellationToken)).Should().Equal(original,
            "Reduce File Size never rewrites the source");
        vm.FilePath.Should().Be(pdf, "the smaller copy is not opened in place of the document");
        dialogs.Messages.Should().ContainSingle().Which.Should().Contain("Saved to " + output);
        SavedPdfLeakScanner.FindTerm(await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken), "Page 2")
            .Should().NotBeEmpty("the copy still carries the document's text");
    }

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task Command_Cancelled_WritesNothing()
    {
        var picker = new RecordingFilePicker();
        var vm = MainWindowViewModelTestFactory.Create(filePicker: picker);
        await vm.LoadDocumentAsync(NewPdf("cancel.pdf"));

        vm.ReduceFileSizePresetOverride = () => null;
        await vm.ReduceFileSizeCommand.Execute();

        picker.SaveRequests.Should().BeEmpty("a cancelled dialog must not go on to ask for a location");
        Directory.GetFiles(_tempDir).Should().ContainSingle();
    }

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task Command_RefusesToOverwriteTheOpenFile()
    {
        var pdf = NewPdf("same.pdf");
        var original = await File.ReadAllBytesAsync(pdf, TestContext.Current.CancellationToken);
        var dialogs = new RecordingDialogService();
        var vm = MainWindowViewModelTestFactory.Create(
            filePicker: new RecordingFilePicker().WillSave(pdf), dialogService: dialogs);
        await vm.LoadDocumentAsync(pdf);

        vm.ReduceFileSizePresetOverride = () => PdfOptimizationPreset.Screen;
        await vm.ReduceFileSizeCommand.Execute();

        (await File.ReadAllBytesAsync(pdf, TestContext.Current.CancellationToken)).Should().Equal(original);
        dialogs.Messages.Should().ContainSingle().Which.Should().Contain("never replaces the original");
    }

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task Command_WithUnsavedEdits_AsksTheUserToSaveFirst()
    {
        var picker = new RecordingFilePicker().WillSave(Path.Combine(_tempDir, "never.pdf"));
        var dialogs = new RecordingDialogService();
        var vm = MainWindowViewModelTestFactory.Create(filePicker: picker, dialogService: dialogs);
        await vm.LoadDocumentAsync(NewPdf("dirty.pdf"));
        vm.FileState.PageEditsCount++;

        vm.ReduceFileSizePresetOverride = () => PdfOptimizationPreset.Lossless;
        await vm.ReduceFileSizeCommand.Execute();

        picker.SaveRequests.Should().BeEmpty();
        File.Exists(Path.Combine(_tempDir, "never.pdf")).Should().BeFalse();
        dialogs.Messages.Should().ContainSingle().Which.Should().Contain("Save your changes first");
    }

    private sealed class RecordingDialogService : IUserDialogService
    {
        public List<string> Messages { get; } = new();

        public Task ShowMessageAsync(string title, string message)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }
}
