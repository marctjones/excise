using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using AwesomeAssertions;
using Pdfe.Ocr;
using PdfEditor.ViewModels;
using Xunit;

namespace PdfEditor.Tests.Unit;

/// <summary>
/// Wiring tests for the "Make Searchable" dialog (#658): command
/// enablement, progress reporting, cancellation, and the result summary
/// text — all against an injected fake OCR delegate, so these run without
/// tesseract installed or a real PdfDocument. The engine itself
/// (<see cref="PdfSearchableConverter"/>) is already covered by
/// <c>Pdfe.Ocr.Tests/PdfSearchableConverterTests.cs</c>.
/// </summary>
public class MakeSearchableDialogViewModelTests
{
    private static SearchableDocumentResult MakeResult(
        int processed = 2, int skipped = 1, int written = 5, int skippedEncoding = 0) =>
        new(processed, skipped, written, skippedEncoding, Array.Empty<SearchablePageResult>());

    [Fact]
    public void StartCommand_Disabled_WhenTesseractUnavailable()
    {
        var vm = new MakeSearchableDialogViewModel(
            tesseractAvailable: false,
            runOcr: (_, _, _, _) => Task.FromResult(MakeResult()));

        ((ICommand)vm.StartCommand).CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void StartCommand_Enabled_WhenTesseractAvailable()
    {
        var vm = new MakeSearchableDialogViewModel(
            tesseractAvailable: true,
            runOcr: (_, _, _, _) => Task.FromResult(MakeResult()));

        ((ICommand)vm.StartCommand).CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task Start_ReportsProgress_ThenCompletesWithSummaryAndEvent()
    {
        var vm = new MakeSearchableDialogViewModel(
            tesseractAvailable: true,
            runOcr: (language, force, progress, token) =>
            {
                language.Should().Be("eng");
                force.Should().BeFalse();
                progress.Report((1, 2));
                progress.Report((2, 2));
                return Task.FromResult(MakeResult(processed: 2, skipped: 1, written: 5));
            });

        SearchableDocumentResult? completedResult = null;
        vm.Completed += (_, result) => completedResult = result;

        await vm.StartCommand.Execute();

        vm.IsRunning.Should().BeFalse();
        vm.IsDone.Should().BeTrue();
        vm.ProgressDone.Should().Be(2);
        vm.ProgressTotal.Should().Be(2);
        vm.ResultSummary.Should().Contain("2 page(s) processed")
            .And.Contain("1 skipped (already searchable)")
            .And.Contain("5 word(s) written");
        vm.ErrorMessage.Should().BeNull();
        completedResult.Should().NotBeNull();
        completedResult!.TotalWordsWritten.Should().Be(5);

        // Once done, Start is disabled and Cancel reads "Close".
        ((ICommand)vm.StartCommand).CanExecute(null).Should().BeFalse();
        vm.CancelButtonText.Should().Be("Close");
    }

    [Fact]
    public void ResultSummary_MentionsEncodingSkips_WhenAnyWordsWereSkipped()
    {
        var summary = MakeSearchableDialogViewModel.FormatSummary(MakeResult(skippedEncoding: 3));
        summary.Should().Contain("3 word(s) skipped");
    }

    [Fact]
    public async Task Cancel_WhileRunning_CancelsTheInFlightRun()
    {
        var started = new TaskCompletionSource();
        var vm = new MakeSearchableDialogViewModel(
            tesseractAvailable: true,
            runOcr: async (_, _, _, token) =>
            {
                started.SetResult();
                var tcs = new TaskCompletionSource();
                using var reg = token.Register(() => tcs.TrySetCanceled(token));
                await tcs.Task;
                return MakeResult(); // unreachable: tcs.Task above throws once cancelled
            });

        var runTask = vm.StartCommand.Execute().ToTask();
        await started.Task;
        vm.IsRunning.Should().BeTrue();
        vm.CancelButtonText.Should().Be("Cancel");

        ((ICommand)vm.CancelCommand).Execute(null);

        await runTask;

        vm.IsRunning.Should().BeFalse();
        vm.IsDone.Should().BeFalse("a cancelled run did not complete");
        vm.ErrorMessage.Should().Contain("cancelled");
    }

    [Fact]
    public void Cancel_WhenNotRunning_RaisesCloseRequested()
    {
        var vm = new MakeSearchableDialogViewModel(
            tesseractAvailable: true,
            runOcr: (_, _, _, _) => Task.FromResult(MakeResult()));

        var closeRequested = false;
        vm.CloseRequested += (_, _) => closeRequested = true;

        ((ICommand)vm.CancelCommand).Execute(null);

        closeRequested.Should().BeTrue();
    }

    [Fact]
    public void LanguagePresets_IncludeCommonSingleAndCombinedCodes()
    {
        MakeSearchableDialogViewModel.LanguagePresets.Should().Contain(new[] { "eng", "deu", "eng+spa" });
    }

    [Fact]
    public async Task CanEditOptions_FalseWhileRunning_StaysFalseAfterCompletion()
    {
        // Uses a "started" checkpoint (like Cancel_WhileRunning above) so the
        // mid-run assertion is synchronized rather than racing the fake
        // delegate's completion against the test's own assertion.
        var started = new TaskCompletionSource();
        var gate = new TaskCompletionSource();
        var vm = new MakeSearchableDialogViewModel(
            tesseractAvailable: true,
            runOcr: async (_, _, _, _) =>
            {
                started.SetResult();
                await gate.Task;
                return MakeResult();
            });

        vm.CanEditOptions.Should().BeTrue();

        var runTask = vm.StartCommand.Execute().ToTask();
        await started.Task;
        vm.CanEditOptions.Should().BeFalse("options must not change mid-run");

        gate.SetResult();
        await runTask;

        vm.CanEditOptions.Should().BeFalse("after completion, options stay locked until the dialog is reopened");
    }
}
