using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Excise.App.Services.Printing;
using Excise.App.Tests.Utilities.Fakes;
using Excise.App.ViewModels;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #1710: the Linux CUPS printer with its two seams replaced — a scripted
/// process runner in place of <c>lp</c>/<c>lpstat</c>, and a scripted chooser
/// in place of the window. What runs for real: queue parsing, the
/// <c>lp</c> command line, page-range validation, and every failure path.
/// </summary>
/// <remarks>
/// None of this file touches CUPS, so it runs on every platform. The test
/// that talks to a real <c>cupsd</c> is
/// <see cref="LinuxCupsPrintIntegrationTests"/>, which only runs where CUPS
/// is installed — a container, in practice
/// (<c>scripts/run-linux-print-test.sh</c>).
/// </remarks>
public class LinuxCupsDocumentPrinterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "excise-1710-print", Guid.NewGuid().ToString("N"));

    public LinuxCupsDocumentPrinterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string CreatePdf(string name, int pages = 3)
    {
        var path = Path.Combine(_dir, name);
        using var doc = PdfDocument.CreateNew();
        for (int i = 1; i <= pages; i++)
        {
            var page = doc.Pages.AddBlank();
            using var g = page.GetGraphics();
            g.DrawString($"LINUXPRINT1710 page {i}", PdfFont.Helvetica(18), PdfBrush.Black, 72, 700);
            g.Flush();
        }
        doc.Save(path);
        return path;
    }

    private DocumentPrintRequest Request(string path, PrintScalingMode scaling = PrintScalingMode.ShrinkOversized) =>
        new(path, "job 1710", scaling, Owner: null, CancellationToken.None);

    private static LinuxCupsDocumentPrinter Printer(ICupsProcessRunner runner, ILinuxPrintDialog dialog) =>
        new(runner, dialog, NullLogger.Instance);

    // ── lpstat parsing ─────────────────────────────────────────────────

    [Fact]
    public void Parse_MergesDestinationsStatesAndDefault_DefaultFirst()
    {
        var queues = CupsPrintQueues.Parse(
            "Office\nCups-PDF\nBasement\n",
            "printer Office is idle.  enabled since Sun 21 Sep 2026 10:00:00 AM UTC\n" +
            "printer Cups-PDF is idle.  enabled since Sun 21 Sep 2026 10:00:00 AM UTC\n" +
            "printer Basement disabled since Sun 21 Sep 2026 09:00:00 AM UTC -\n" +
            "\treason unknown\n",
            "system default destination: Cups-PDF\n");

        queues.Select(q => q.Name).Should().Equal("Cups-PDF", "Basement", "Office");
        queues[0].IsDefault.Should().BeTrue("the default is preselected, so it is listed first");
        queues[0].State.Should().Be("idle");
        queues.Single(q => q.Name == "Basement").State.Should().Be("disabled");
        queues[0].DisplayName.Should().Be("Cups-PDF — idle  (default)");
    }

    [Fact]
    public void Parse_QueueSeenOnlyByLpstatP_IsStillOffered()
    {
        // `lpstat -e` is empty on some configurations; the union must still
        // find the queue, or the user is told there are no printers while
        // there is one.
        var queues = CupsPrintQueues.Parse(
            string.Empty,
            "printer HomeLaser is printing job 7.  enabled since Mon 22 Sep 2026\n",
            "no system default destination\n");

        queues.Should().ContainSingle();
        queues[0].Name.Should().Be("HomeLaser");
        queues[0].State.Should().Be("printing job 7");
        queues[0].IsDefault.Should().BeFalse();
    }

    [Fact]
    public void Parse_IgnoresMessagesAndReportsNoDefault()
    {
        CupsPrintQueues.ParseDestinationNames("lpstat: Transport endpoint is not connected\n").Should().BeEmpty(
            "a destination name never contains a space, so a message is not a queue");
        CupsPrintQueues.ParseDefaultDestination("no system default destination\n").Should().BeNull();
        CupsPrintQueues.Parse(null, null, null).Should().BeEmpty();
    }

    [Theory]
    [InlineData("request id is Cups-PDF-1 (1 file(s))", "Cups-PDF-1")]
    [InlineData("request id is Office-42", "Office-42")]
    [InlineData("", null)]
    [InlineData("something else entirely", null)]
    public void ParseRequestId_ReadsTheJobIdOrNothing(string output, string? expected) =>
        CupsPrintQueues.ParseRequestId(output).Should().Be(expected);

    // ── page ranges ────────────────────────────────────────────────────

    [Theory]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("2", "2")]
    [InlineData("1-3", "1-3")]
    [InlineData(" 1 - 3 , 7 ", "1-3,7")]
    [InlineData("3-1", "1-3")]
    public void PageRange_ParsesAndRoundTripsToTheCupsValue(string text, string? expected)
    {
        PrintPageRangeText.TryParse(text, 10, out var ranges, out var error).Should().BeTrue(error);
        PrintPageRangeText.ToCupsValue(ranges).Should().Be(expected);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("11")]
    [InlineData("1-11")]
    [InlineData("one")]
    [InlineData("-")]
    [InlineData(",,")]
    public void PageRange_RefusesAnythingOutsideTheDocument(string text)
    {
        PrintPageRangeText.TryParse(text, 10, out _, out var error).Should().BeFalse(
            "an out-of-range or unparseable range must be refused, never silently turned into a different job");
        error.Should().NotBeNullOrWhiteSpace();
    }

    // ── the lp command line ────────────────────────────────────────────

    [Fact]
    public void LpArguments_SimplestJob_IsQueueTitleAndFile()
    {
        var path = Path.Combine(_dir, "simple.pdf");
        var arguments = LinuxCupsDocumentPrinter.BuildLpArguments(
            Request(path), LinuxPrintTicket.Print("Cups-PDF"));

        arguments.Should().Equal("-d", "Cups-PDF", "-t", "job 1710", "--", path);
        arguments.Should().NotContain("-n", "one copy is CUPS's default, so excise does not say it");
        arguments.Should().NotContain(a => a.StartsWith("page-ranges", StringComparison.Ordinal));
    }

    [Fact]
    public void LpArguments_CopiesRangesAndFitToPage()
    {
        var path = Path.Combine(_dir, "full.pdf");
        var arguments = LinuxCupsDocumentPrinter.BuildLpArguments(
            Request(path, PrintScalingMode.FitToPage),
            LinuxPrintTicket.Print("Office", [new PrintPageRange(2, 4), new PrintPageRange(7, 7)], copies: 3, collate: false));

        arguments.Should().Equal(
            "-d", "Office", "-t", "job 1710",
            "-n", "3", "-o", "Collate=False",
            "-o", "page-ranges=2-4,7",
            "-o", "fit-to-page",
            "--", path);
    }

    [Theory]
    [InlineData(PrintScalingMode.ActualSize)]
    [InlineData(PrintScalingMode.ShrinkOversized)]
    public void LpArguments_OnlyFitToPageEmitsAScalingOption(PrintScalingMode scaling)
    {
        // CUPS has no shrink-only mode. Emitting fit-to-page for it would
        // scale small pages UP, which is not what the preference says.
        LinuxCupsDocumentPrinter.BuildLpArguments(
                Request(Path.Combine(_dir, "s.pdf"), scaling), LinuxPrintTicket.Print("Q"))
            .Should().NotContain("fit-to-page");
    }

    [Fact]
    public void LpArguments_ClampCopies_AndStripControlCharactersFromTheTitle()
    {
        var request = new DocumentPrintRequest(
            Path.Combine(_dir, "t.pdf"), "ti\ntle", PrintScalingMode.ActualSize, null);
        var arguments = LinuxCupsDocumentPrinter.BuildLpArguments(
            request, LinuxPrintTicket.Print("Q", copies: 99999));

        arguments.Should().Contain("title", "a control character in a job name is not sent to CUPS");
        arguments[arguments.ToList().IndexOf("-n") + 1].Should()
            .Be(PrintPageSequence.MaxCopies.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    // ── the happy path ─────────────────────────────────────────────────

    [Fact]
    public async Task PrintAsync_SubmitsTheJob_AndOffersTheChooserEveryQueue()
    {
        var runner = FakeCupsProcessRunner.WithQueues("Cups-PDF", "Office");
        var dialog = new ScriptedLinuxPrintDialog(LinuxPrintTicket.Print("Office", copies: 2));
        var path = CreatePdf("happy.pdf", pages: 5);

        var result = await Printer(runner, dialog).PrintAsync(Request(path));

        result.Outcome.Should().Be(DocumentPrintOutcome.Printed);
        result.Error.Should().BeNull();
        dialog.PageCounts.Should().Equal(new[] { 5 }, "the chooser needs the page count to validate a range");
        dialog.OfferedQueues.Single().Select(q => q.Name).Should().BeEquivalentTo(["Cups-PDF", "Office"]);
        runner.CommandLines.Should().ContainSingle(c => c.StartsWith("lp -d Office", StringComparison.Ordinal));
        runner.Invocations.Last().Arguments.Should().Contain("-n").And.Contain("2");
        runner.Invocations.Last().Arguments.Last().Should().Be(path);
    }

    [Fact]
    public async Task PrintAsync_UserCancels_NothingIsSubmitted()
    {
        var runner = FakeCupsProcessRunner.WithQueues("Cups-PDF");
        var result = await Printer(runner, new ScriptedLinuxPrintDialog(LinuxPrintTicket.Cancelled))
            .PrintAsync(Request(CreatePdf("cancel.pdf")));

        result.Outcome.Should().Be(DocumentPrintOutcome.Cancelled);
        runner.CommandLines.Should().NotContain(c => c.StartsWith("lp ", StringComparison.Ordinal));
    }

    // ── failing loudly ─────────────────────────────────────────────────

    [Fact]
    public async Task PrintAsync_NoCupsTools_SaysSo()
    {
        var runner = new FakeCupsProcessRunner()
            .When("lpstat", CupsProcessResult.NotRun("lpstat could not be started: No such file or directory"));

        var result = await Printer(runner, new ScriptedLinuxPrintDialog(LinuxPrintTicket.Print("Q")))
            .PrintAsync(Request(CreatePdf("notools.pdf")));

        result.Outcome.Should().Be(DocumentPrintOutcome.Failed);
        result.Error.Should().Be(LinuxCupsDocumentPrinter.ToolsMissingMessage);
        result.Error.Should().Contain("cups-client", "the message must say what to install");
    }

    [Fact]
    public async Task PrintAsync_SchedulerNotRunning_SaysSo_AndQuotesCups()
    {
        var runner = new FakeCupsProcessRunner()
            .When("lpstat", CupsProcessResult.Ran(1, string.Empty, "lpstat: Transport endpoint is not connected\n"));

        var result = await Printer(runner, new ScriptedLinuxPrintDialog(LinuxPrintTicket.Print("Q")))
            .PrintAsync(Request(CreatePdf("nocupsd.pdf")));

        result.Outcome.Should().Be(DocumentPrintOutcome.Failed);
        result.Error.Should().StartWith("The CUPS printing service did not answer.");
        result.Error.Should().Contain("Transport endpoint is not connected",
            "CUPS's own words are more useful than ours alone");
    }

    [Fact]
    public async Task PrintAsync_NoQueues_SaysSo_AndNeverShowsTheChooser()
    {
        var runner = FakeCupsProcessRunner.WithQueues();
        var dialog = new ScriptedLinuxPrintDialog(LinuxPrintTicket.Print("Q"));

        var result = await Printer(runner, dialog).PrintAsync(Request(CreatePdf("noqueues.pdf")));

        result.Outcome.Should().Be(DocumentPrintOutcome.Failed);
        result.Error.Should().Be(LinuxCupsDocumentPrinter.NoQueuesMessage);
        dialog.OfferedQueues.Should().BeEmpty("an empty chooser would be a dead end");
    }

    [Fact]
    public async Task PrintAsync_LpRefusesTheJob_ReportsTheExitAndTheStderr()
    {
        var runner = FakeCupsProcessRunner.WithQueues("Cups-PDF")
            .When("lp -d", CupsProcessResult.Ran(1, string.Empty, "lp: Error - scheduler not responding.\n"));

        var result = await Printer(runner, new ScriptedLinuxPrintDialog(LinuxPrintTicket.Print("Cups-PDF")))
            .PrintAsync(Request(CreatePdf("refused.pdf")));

        result.Outcome.Should().Be(DocumentPrintOutcome.Failed);
        result.Error.Should().Be("The printer did not accept the job: lp: Error - scheduler not responding.");
    }

    [Fact]
    public async Task PrintAsync_LpSucceedsSilently_IsStillASuccess_ButASilentFailureIsNot()
    {
        // A zero exit with no "request id" line is still a success — some
        // queues say nothing. A NON-zero exit with no output must not be.
        var quiet = FakeCupsProcessRunner.WithQueues("Q").When("lp -d", CupsProcessResult.Ran(0, string.Empty, string.Empty));
        (await Printer(quiet, new ScriptedLinuxPrintDialog(LinuxPrintTicket.Print("Q")))
            .PrintAsync(Request(CreatePdf("quiet.pdf")))).Outcome.Should().Be(DocumentPrintOutcome.Printed);

        var mute = FakeCupsProcessRunner.WithQueues("Q").When("lp -d", CupsProcessResult.Ran(3, string.Empty, string.Empty));
        var result = await Printer(mute, new ScriptedLinuxPrintDialog(LinuxPrintTicket.Print("Q")))
            .PrintAsync(Request(CreatePdf("mute.pdf")));
        result.Outcome.Should().Be(DocumentPrintOutcome.Failed);
        result.Error.Should().Contain("code 3", "a failure with no message still has to name itself");
    }

    [Fact]
    public async Task PrintAsync_ChooserFails_ReportsTheChoosersReason()
    {
        var runner = FakeCupsProcessRunner.WithQueues("Q");
        var result = await Printer(runner, new ScriptedLinuxPrintDialog(LinuxPrintTicket.Fail("no window 1710")))
            .PrintAsync(Request(CreatePdf("nochooser.pdf")));

        result.Outcome.Should().Be(DocumentPrintOutcome.Failed);
        result.Error.Should().Be("no window 1710");
        runner.CommandLines.Should().NotContain(c => c.StartsWith("lp ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PrintAsync_UnreadableCopy_FailsWithAMessage()
    {
        var runner = FakeCupsProcessRunner.WithQueues("Q");
        var broken = Path.Combine(_dir, "broken.pdf");
        File.WriteAllText(broken, "this is not a PDF");

        var result = await Printer(runner, new ScriptedLinuxPrintDialog(LinuxPrintTicket.Print("Q")))
            .PrintAsync(Request(broken));

        result.Outcome.Should().Be(DocumentPrintOutcome.Failed);
        result.Error.Should().StartWith("Printing failed:");
        runner.CommandLines.Should().NotContain(c => c.StartsWith("lp ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PrintAsync_Cancelled_DoesNotSubmit()
    {
        var runner = FakeCupsProcessRunner.WithQueues("Q");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var request = new DocumentPrintRequest(
            CreatePdf("cancelled.pdf"), "t", PrintScalingMode.ActualSize, null, cancellation.Token);

        var result = await Printer(runner, new ScriptedLinuxPrintDialog(LinuxPrintTicket.Print("Q"))).PrintAsync(request);

        result.Outcome.Should().Be(DocumentPrintOutcome.Cancelled);
        runner.CommandLines.Should().NotContain(c => c.StartsWith("lp ", StringComparison.Ordinal));
    }

    // ── the chooser's own validation ───────────────────────────────────

    [Fact]
    public void ChooserViewModel_PreselectsTheDefaultQueue_AndBuildsTheTicket()
    {
        var vm = new LinuxPrintDialogViewModel(
            [new CupsPrintQueue("Office", "idle"), new CupsPrintQueue("Cups-PDF", "idle", IsDefault: true)],
            pageCount: 9);

        vm.SelectedQueue!.Name.Should().Be("Cups-PDF", "the system default is preselected");
        vm.PageRangePlaceholder.Should().Be("All pages (1-9)");
        vm.CopiesText = "4";
        vm.PageRangeText = "2-5";
        vm.Collate = false;

        vm.TryConfirm().Should().BeTrue();
        // Field by field: the record's Ranges is an array, so == on the whole
        // ticket would compare references and pass for the wrong reason.
        var ticket = vm.Ticket!;
        ticket.Outcome.Should().Be(LinuxPrintDialogOutcome.Print);
        ticket.QueueName.Should().Be("Cups-PDF");
        ticket.Copies.Should().Be(4);
        ticket.Collate.Should().BeFalse();
        ticket.Ranges.Should().Equal(new[] { new PrintPageRange(2, 5) });
    }

    [Theory]
    [InlineData("0", "", "Copies")]
    [InlineData("many", "", "Copies")]
    [InlineData("1", "12-14", "page range")]
    public void ChooserViewModel_RefusesBadInput_AndProducesNoTicket(string copies, string range, string expectedInError)
    {
        var vm = new LinuxPrintDialogViewModel([new CupsPrintQueue("Q", "idle", true)], pageCount: 9)
        {
            CopiesText = copies,
            PageRangeText = range,
        };

        vm.TryConfirm().Should().BeFalse();
        vm.Ticket.Should().BeNull("a refused chooser must never produce a job");
        vm.ValidationError.Should().Contain(expectedInError);
    }

    [Fact]
    public void ChooserViewModel_NoQueueSelected_IsRefused()
    {
        var vm = new LinuxPrintDialogViewModel([new CupsPrintQueue("Q", "idle", true)], pageCount: 3)
        {
            SelectedQueue = null,
        };

        vm.TryConfirm().Should().BeFalse();
        vm.ValidationError.Should().Be("Choose a printer.");
    }

    // ── the factory ────────────────────────────────────────────────────

    [Fact]
    public void Factory_PicksTheCupsPrinterOnLinuxOnly()
    {
        var printer = DocumentPrinterFactory.CreateForCurrentPlatform(NullLoggerFactory.Instance);

        if (OperatingSystem.IsLinux())
        {
            printer.Should().BeOfType<LinuxCupsDocumentPrinter>();
            printer.IsSupported.Should().BeTrue("Linux prints through CUPS since #1710");
        }
        else
        {
            printer.Should().NotBeOfType<LinuxCupsDocumentPrinter>(
                "a CUPS printer on macOS or Windows would shell out to tools that platform does not use");
        }
    }
}

/// <summary>
/// #1710: /P printing permission gates the CUPS path exactly as it gates the
/// macOS and Windows ones. The gate itself is the view model's and is pinned
/// by <see cref="DocumentPrintingTests"/>; what is pinned HERE is that the
/// real Linux printer sits behind it — a denied document must not cause a
/// single <c>lpstat</c> or <c>lp</c> subprocess, on any platform.
/// </summary>
[Collection("AvaloniaTests")]
public class LinuxPrintPermissionGatingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "excise-1710-perm", Guid.NewGuid().ToString("N"));

    public LinuxPrintPermissionGatingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private sealed record Harness(MainWindowViewModel Vm, FakeCupsProcessRunner Runner, ScriptedLinuxPrintDialog Dialog);

    private Harness Create()
    {
        var runner = FakeCupsProcessRunner.WithQueues("Cups-PDF");
        var dialog = new ScriptedLinuxPrintDialog(LinuxPrintTicket.Print("Cups-PDF"));
        var printer = new LinuxCupsDocumentPrinter(runner, dialog, NullLogger.Instance);

        var redactionService = new Excise.App.Services.RedactionService(
            NullLogger<Excise.App.Services.RedactionService>.Instance, NullLoggerFactory.Instance);
        var extraction = new Excise.App.Services.PdfTextExtractionService(
            NullLogger<Excise.App.Services.PdfTextExtractionService>.Instance);
        var workflow = new DocumentPrintWorkflowService(
            new Excise.App.Services.RedactionWorkflowService(
                redactionService, extraction, NullLogger<Excise.App.Services.RedactionWorkflowService>.Instance),
            printer,
            NullLogger<DocumentPrintWorkflowService>.Instance,
            () => Path.Combine(_dir, "print-copies"));

        var vm = MainWindowViewModelTestFactory.Create(printWorkflow: workflow);
        return new Harness(vm, runner, dialog);
    }

    private string CreatePdf(string name)
    {
        var path = Path.Combine(_dir, name);
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank();
        using (var g = page.GetGraphics())
        {
            g.DrawString("PERM1710", PdfFont.Helvetica(18), PdfBrush.Black, 72, 700);
            g.Flush();
        }
        doc.Save(path);
        return path;
    }

    private string SaveEncrypted(string plainPath, string name, long permissions)
    {
        var path = Path.Combine(_dir, name);
        using var doc = PdfDocument.Open(File.ReadAllBytes(plainPath));
        doc.Save(path, new Excise.Core.Security.PdfEncryptionOptions
        {
            UserPassword = string.Empty,
            OwnerPassword = "owner-1710",
            Permissions = permissions,
        });
        return path;
    }

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task PrintDenied_Bit3_NeverReachesCups()
    {
        var h = Create();
        await h.Vm.LoadDocumentAsync(SaveEncrypted(CreatePdf("p-src.pdf"), "noprint.pdf", -4 & ~4L));

        h.Vm.CanPrint.Should().BeFalse("Print… must be disabled when /P denies printing");
        await h.Vm.PrintCommand.Execute();

        h.Runner.Invocations.Should().BeEmpty("a print-denied document must not even enumerate the queues");
        h.Dialog.OfferedQueues.Should().BeEmpty();
    }

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task DegradedPrintingOnly_Bit12_NeverReachesCups()
    {
        var h = Create();
        // Bit 12 (value 2048) cleared: the document allows only degraded
        // printing, which excise cannot produce on any platform.
        await h.Vm.LoadDocumentAsync(SaveEncrypted(CreatePdf("d-src.pdf"), "degraded.pdf", -4 & ~2048L));

        h.Vm.CanPrint.Should().BeFalse();
        await h.Vm.PrintCommand.Execute();

        h.Runner.Invocations.Should().BeEmpty();
    }

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task PrintingAllowed_ReachesCups()
    {
        var h = Create();
        await h.Vm.LoadDocumentAsync(CreatePdf("allowed.pdf"));

        h.Vm.CanPrint.Should().BeTrue();
        await h.Vm.PrintCommand.Execute();

        h.Runner.CommandLines.Should().Contain(c => c.StartsWith("lp -d Cups-PDF", StringComparison.Ordinal),
            "the negative controls above only mean something if the same harness DOES print when allowed");
    }
}
