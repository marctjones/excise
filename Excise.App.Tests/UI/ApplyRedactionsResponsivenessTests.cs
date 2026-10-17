using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using AwesomeAssertions;
using Excise.App.Services;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Excise.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #2074: Apply All used to run the redaction engine on the UI thread, freezing the window for the whole save
/// (16 s on the 126-page IRS book). It now works on a private copy on a worker. The test holds the worker through
/// a seam (<c>SetRedactedCopyRunnerForTests</c>) so it can watch the UI thread meanwhile, deterministically and
/// without timing a real redaction: the dispatcher must keep ticking while the engine runs, a mark made meanwhile
/// is refused, and a second Apply is ignored.
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

    [FixedAvaloniaFact(Timeout = 120000)]
    public async Task ApplyAll_WhileTheEngineRuns_TheDispatcherKeepsTicking_EditsAreRefused_AndASecondApplyIsIgnored()
    {
        const string rel = "test-pdfs/smoke/irs-w9.pdf";
        var source = TestRepoLayout.FindFile(rel);
        Assert.SkipWhen(source == null, TestRepoLayout.AbsenceReason("smoke corpus fixture", rel));

        var vm = MainWindowViewModelTestFactory.Create(dialogService: new QuietDialogService());
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await vm.LoadDocumentAsync(source!);

        vm.RedactionWorkflow.MarkArea(PdfPageRect.FromContentPoints(1, new PdfRectangle(72, 600, 300, 640)), "mark");
        vm.FileState.PendingRedactionsCount = vm.RedactionWorkflow.PendingCount;

        var output = Path.Combine(_dir, "redacted.pdf");
        vm.SetRedactedSavePathProviderForTests(_ => Task.FromResult<string?>(output));

        // The engine call: signals it started, holds until released, then does the real work.
        using var started = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var runs = 0;
        var real = new RedactionWorkflowService(
            new RedactionService(NullLogger<RedactionService>.Instance, NullLoggerFactory.Instance),
            NullLogger<RedactionWorkflowService>.Instance);
        vm.SetRedactedCopyRunnerForTests(request =>
        {
            Interlocked.Increment(ref runs);
            started.Set();
            release.Wait(TimeSpan.FromSeconds(60));
            return real.CreateRedactedCopy(request);
        });

        var apply = vm.ApplyAllRedactionsCommand!.Execute().ToTask();

        // Wait (yielding to the dispatcher) until the engine call is running.
        var waited = 0;
        while (!started.IsSet && waited++ < 400) await Task.Delay(25);
        started.IsSet.Should().BeTrue("Apply reached the engine call");
        vm.OperationStatus.Should().Be("Applying redactions…");

        // 1. The dispatcher keeps ticking while the engine is held: a frozen UI thread ticks 0 times.
        var ticks = 0;
        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) => ticks++;
        timer.Start();
        await Task.Delay(600);
        timer.Stop();
        ticks.Should().BeGreaterThanOrEqualTo(3, "the engine runs on a worker; before #2074 the UI thread ran it and could not tick");

        // 2. A mark made now is refused with a message (it would otherwise be moved to "applied" unapplied).
        var pendingBefore = vm.RedactionWorkflow.PendingCount;
        vm.CurrentTextSelectionPageArea = PdfPageRect.FromContentPoints(1, new PdfRectangle(72, 700, 200, 720));
        vm.SelectedText = "late mark";
        await vm.RedactSelectionCommand.Execute();
        vm.RedactionWorkflow.PendingCount.Should().Be(pendingBefore, "no mark is added while Apply works on its snapshot");
        vm.OperationStatus.Should().StartWith("Redactions are being applied");

        // 3. A second Apply while one is running does nothing.
        await vm.ApplyAllRedactionsCommand!.Execute();
        runs.Should().Be(1, "the second Apply was ignored");

        release.Set();
        await apply;

        File.Exists(output).Should().BeTrue("the redacted copy was written once released");
        runs.Should().Be(1);
    }
}
