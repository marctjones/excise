using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using Avalonia.Threading;
using AwesomeAssertions;
using Excise.App.Services;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Excise.Core.Text.Segmentation;
using Excise.TestSupport;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #2074: Apply All used to run the redaction engine on the UI thread, freezing the window for the whole save
/// (16 s on the 126-page IRS book). It now works on a private copy on a worker. This applies many marks to that
/// book through the real command and checks the dispatcher kept turning while it ran, that marks added meanwhile
/// are refused, and that a second Apply is ignored. The bound is generous (the window must not freeze for seconds);
/// it is not a latency gate.
/// </summary>
[Collection("AvaloniaTests")]
public class ApplyRedactionsResponsivenessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"excise-applyresp-{Guid.NewGuid():N}");

    public ApplyRedactionsResponsivenessTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private sealed class QuietDialogService : IUserDialogService
    {
        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
        public Task<bool> ShowConfirmAsync(string title, string message) => Task.FromResult(true);
    }

    [FixedAvaloniaFact(Timeout = 180000)]
    public async Task ApplyAll_OnALongDocument_KeepsTheDispatcherTurning_AndRefusesEditsMeanwhile()
    {
        const string rel = "test-pdfs/smoke/irs-1040-instructions.pdf";
        var source = TestRepoLayout.FindFile(rel);
        Assert.SkipWhen(source == null, TestRepoLayout.AbsenceReason("smoke corpus fixture", rel));

        var vm = MainWindowViewModelTestFactory.Create(dialogService: new QuietDialogService());
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await vm.LoadDocumentAsync(source!);

        // Sixty marks over the first twenty pages, so the engine has real work.
        for (var page = 1; page <= 20; page++)
            for (var i = 0; i < 3; i++)
                vm.RedactionWorkflow.MarkArea(
                    PdfPageRect.FromContentPoints(page, new PdfRectangle(72, 600 - i * 80, 300, 640 - i * 80)), $"mark {page}.{i}");
        vm.FileState.PendingRedactionsCount = vm.RedactionWorkflow.PendingCount;
        var pendingBefore = vm.RedactionWorkflow.PendingCount;

        var output = Path.Combine(_dir, "redacted.pdf");
        vm.SetRedactedSavePathProviderForTests(_ => Task.FromResult<string?>(output));

        // A heartbeat on the dispatcher: the longest gap between two ticks while Apply runs.
        var clock = Stopwatch.StartNew();
        var lastTick = clock.Elapsed;
        var maxGap = TimeSpan.Zero;
        var sawBusyStatus = false;
        var attempted = false;
        var sawRefusal = false;
        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(25) };
        timer.Tick += (_, _) =>
        {
            var now = clock.Elapsed;
            if (now - lastTick > maxGap) maxGap = now - lastTick;
            lastTick = now;
            if (vm.OperationStatus == "Applying redactions…")
            {
                sawBusyStatus = true;
                if (!attempted)
                {
                    // A mark made now must be refused: it would be marked applied without ever being applied.
                    attempted = true;
                    vm.CurrentTextSelectionPageArea = PdfPageRect.FromContentPoints(1, new PdfRectangle(72, 700, 200, 720));
                    vm.SelectedText = "late mark";
                    vm.RedactSelectionCommand.Execute().Subscribe();
                }
            }
            else if (attempted && vm.OperationStatus.StartsWith("Redactions are being applied", StringComparison.Ordinal))
            {
                sawRefusal = true;
            }
        };
        timer.Start();

        var apply = vm.ApplyAllRedactionsCommand!.Execute().ToTask();
        // A second Apply while the first is running does nothing.
        await Task.Delay(100);
        await vm.ApplyAllRedactionsCommand!.Execute();
        await apply;
        timer.Stop();

        File.Exists(output).Should().BeTrue("the redacted copy was written");
        sawBusyStatus.Should().BeTrue("the status said it was working while the engine ran");
        attempted.Should().BeTrue();
        sawRefusal.Should().BeTrue("a mark made while Apply was running was refused with a message");
        maxGap.Should().BeLessThan(TimeSpan.FromSeconds(3),
            "the engine runs on a worker; before #2074 this window was frozen for the whole redaction");
        pendingBefore.Should().Be(60);
    }
}
