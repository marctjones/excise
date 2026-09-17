using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Excise.App.Services;
using Excise.App.Services.Printing;
using Excise.App.Tests.Utilities.Fakes;
using Excise.App.ViewModels;
using Excise.Core.Document;
using Excise.Core.Editing;
using Excise.Core.Graphics;
using Excise.Core.Security;
using Excise.Rendering.Differential;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #1545: File ▸ Print… / Cmd+P through the platform-neutral print workflow,
/// with a fake printer in place of PDFKit. What is pinned:
/// <list type="bullet">
/// <item>the printer receives a copy of the document AS CURRENTLY EDITED, and
/// the copy is gone afterwards on every outcome (printed, cancelled, failed,
/// thrown);</item>
/// <item>pending redactions are REMOVED from the printed copy, proven by the
/// carrier-agnostic leak scanner and by mutool, with a planted-leak control
/// showing both oracles can fail;</item>
/// <item>/P bit 3 (and bit 12) gate the command and the menu item;</item>
/// <item>a platform without a printer says so.</item>
/// </list>
/// </summary>
[Collection("AvaloniaTests")]
public class DocumentPrintingTests : IDisposable
{
    private const string Secret = "PRINTSECRET1545";
    private const string Survivor = "PRINTSURVIVOR1545";

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "excise-1545-print", Guid.NewGuid().ToString("N"));
    private readonly string _printDir;

    public DocumentPrintingTests()
    {
        Directory.CreateDirectory(_tempDir);
        _printDir = Path.Combine(_tempDir, "print-copies");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    private sealed class RecordingDialog : IUserDialogService
    {
        public List<(string Title, string Message)> Messages { get; } = new();

        public Task ShowMessageAsync(string title, string message)
        {
            Messages.Add((title, message));
            return Task.CompletedTask;
        }

        public Task<bool> ShowConfirmAsync(string title, string message) => Task.FromResult(false);
    }

    private sealed record Harness(
        MainWindowViewModel Vm,
        RecordingDocumentPrinter Printer,
        RecordingDialog Dialog,
        List<ToastService.ToastEventArgs> Toasts,
        PdfDocumentService DocumentService);

    private Harness Create(RecordingDocumentPrinter? printer = null)
    {
        printer ??= new RecordingDocumentPrinter();
        var loggerFactory = NullLoggerFactory.Instance;
        var documentService = new PdfDocumentService(NullLogger<PdfDocumentService>.Instance);
        var redactionService = new RedactionService(NullLogger<RedactionService>.Instance, loggerFactory);
        var extraction = new PdfTextExtractionService(NullLogger<PdfTextExtractionService>.Instance);
        var redactionWorkflow = new RedactionWorkflowService(
            redactionService, extraction, NullLogger<RedactionWorkflowService>.Instance);
        var toastService = new ToastService();
        var toasts = new List<ToastService.ToastEventArgs>();
        toastService.ToastRequested += (_, args) => toasts.Add(args);
        var dialog = new RecordingDialog();
        var workflow = new DocumentPrintWorkflowService(
            redactionWorkflow, printer, NullLogger<DocumentPrintWorkflowService>.Instance, () => _printDir);

        var vm = MainWindowViewModelTestFactory.Create(
            documentService: documentService,
            redactionService: redactionService,
            textExtractionService: extraction,
            redactionWorkflowService: redactionWorkflow,
            toastService: toastService,
            dialogService: dialog,
            settingsStore: new InMemorySettingsStore(),
            printWorkflow: workflow);
        return new Harness(vm, printer, dialog, toasts, documentService);
    }

    private string CreateTwoTokenPdf(string name)
    {
        var path = Path.Combine(_tempDir, name);
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank();
        using (var g = page.GetGraphics())
        {
            var font = PdfFont.Helvetica(18);
            g.DrawString(Secret, font, PdfBrush.Black, 100, 700);
            g.DrawString(Survivor, font, PdfBrush.Black, 100, 120);
            g.Flush();
        }
        doc.Pages.AddBlank();
        doc.Save(path);
        return path;
    }

    private string SaveEncrypted(string plainPath, string name, string userPassword, long permissions)
    {
        var path = Path.Combine(_tempDir, name);
        using var doc = PdfDocument.Open(File.ReadAllBytes(plainPath));
        doc.Save(path, new PdfEncryptionOptions
        {
            UserPassword = userPassword,
            OwnerPassword = "owner-1545",
            Permissions = permissions,
        });
        return path;
    }

    private string WriteSnapshot(byte[] bytes, string name)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private void AssertNoPrintCopiesLeft()
    {
        if (!Directory.Exists(_printDir))
            return;
        Directory.GetFiles(_printDir).Should().BeEmpty("the print copy must be deleted once the print operation is over");
    }

    private static void MarkSecret(MainWindowViewModel vm)
    {
        vm.IsRedactionMode = true;
        vm.RedactionWorkflow.MarkArea(PdfPageRect.FromContentPoints(1, new PdfRectangle(40, 675, 500, 750)), Secret);
        vm.RedactionWorkflow.PendingCount.Should().Be(1);
    }

    // ── the copy: current state in, deleted afterwards ──────────────────

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task Print_HandsThePrinterTheCurrentState_AndDeletesTheCopy()
    {
        var h = Create();
        await h.Vm.LoadDocumentAsync(CreateTwoTokenPdf("state.pdf"));

        // Unsaved edits: drop page 2 and add a pending type-over box.
        h.DocumentService.RemovePage(1);
        h.Vm.TypewriterTextOperations.Add(PdfTypewriterTextOperation.Create(
            1, new PdfRectangle(100, 400, 400, 430), "TYPEDONPRINT1545"));

        await h.Vm.PrintCommand.Execute();

        h.Printer.Requests.Should().ContainSingle();
        var request = h.Printer.Requests[0];
        h.Printer.CopyExistedDuringPrint[0].Should().BeTrue("the printer must be able to read the copy while it prints");
        request.JobTitle.Should().Be("state.pdf");
        request.Scaling.Should().Be(PrintScalingMode.ShrinkOversized, "the default scaling shrinks only oversized pages");
        Path.GetDirectoryName(request.PdfPath).Should().Be(_printDir);
        File.Exists(request.PdfPath).Should().BeFalse();
        AssertNoPrintCopiesLeft();

        using var copy = PdfDocument.Open(h.Printer.CopyBytes[0]);
        copy.PageCount.Should().Be(1, "the unsaved page removal is part of what prints");
        copy.GetPage(1).Text.Should().Contain("TYPEDONPRINT1545", "pending type-over text is flattened into the print copy");

        // Printing is not saving or applying: the UI's pending state is untouched.
        h.Vm.TypewriterTextOperations.Should().ContainSingle(o => o.IsPending);
        h.Vm.PdfCoreDocument!.GetPage(1).Text.Should().NotContain("TYPEDONPRINT1545",
            "the live document must not have the type-over text flattened into it by a print");
        h.Dialog.Messages.Should().BeEmpty("a cancelled print sheet needs no dialog");
    }

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task Print_UsesTheScalingPreference()
    {
        var h = Create();
        await h.Vm.LoadDocumentAsync(CreateTwoTokenPdf("scaling.pdf"));
        h.Vm.ApplyPrintScalingPreference("ActualSize");

        await h.Vm.PrintCommand.Execute();

        h.Printer.Requests.Single().Scaling.Should().Be(PrintScalingMode.ActualSize);

        h.Vm.ApplyPrintScalingPreference("not-a-mode");
        h.Vm.PrintScaling.Should().Be(PrintScalingMode.ActualSize, "an unparseable preference keeps the current value");
    }

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task Print_Printed_DeletesTheCopy()
    {
        var h = Create(new RecordingDocumentPrinter { NextResult = DocumentPrintResult.Printed });
        await h.Vm.LoadDocumentAsync(CreateTwoTokenPdf("printed.pdf"));

        await h.Vm.PrintCommand.Execute();

        h.Printer.Requests.Should().ContainSingle();
        AssertNoPrintCopiesLeft();
        h.Dialog.Messages.Should().BeEmpty();
    }

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task Print_PrinterFails_ShowsTheError_AndDeletesTheCopy()
    {
        var h = Create(new RecordingDocumentPrinter { NextResult = DocumentPrintResult.Fail("no printer 1545") });
        await h.Vm.LoadDocumentAsync(CreateTwoTokenPdf("failed.pdf"));

        await h.Vm.PrintCommand.Execute();

        h.Printer.CopyExistedDuringPrint.Should().Equal(true);
        AssertNoPrintCopiesLeft();
        h.Dialog.Messages.Should().ContainSingle(m => m.Title == "Print" && m.Message == "no printer 1545");
    }

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task Print_PrinterThrows_ShowsTheError_AndDeletesTheCopy()
    {
        var h = Create(new RecordingDocumentPrinter { ThrowOnPrint = new InvalidOperationException("bridge exploded 1545") });
        await h.Vm.LoadDocumentAsync(CreateTwoTokenPdf("thrown.pdf"));

        await h.Vm.PrintCommand.Execute();

        h.Printer.CopyExistedDuringPrint.Should().Equal(true);
        AssertNoPrintCopiesLeft();
        h.Dialog.Messages.Should().ContainSingle(m => m.Message.Contains("bridge exploded 1545"));
        h.Vm.IsDocumentLoaded.Should().BeTrue("a failed print leaves the document open");
    }

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task Print_CopyIsOwnerOnly_WhileItExists()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes do not apply on Windows");

        UnixFileMode? mode = null;
        var printer = new RecordingDocumentPrinter { OnPrint = path => mode = File.GetUnixFileMode(path) };
        var h = Create(printer);
        await h.Vm.LoadDocumentAsync(CreateTwoTokenPdf("mode.pdf"));

        await h.Vm.PrintCommand.Execute();

        mode.Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite,
            "a plaintext print copy must not be readable by other users");
        AssertNoPrintCopiesLeft();
    }

    [Fact]
    public void StaleSweep_RemovesOldCopies_AndKeepsRecentOnes()
    {
        Directory.CreateDirectory(_printDir);
        var old = Path.Combine(_printDir, "excise-print-old.pdf");
        var recent = Path.Combine(_printDir, "excise-print-recent.pdf");
        var unrelated = Path.Combine(_printDir, "someone-else.pdf");
        foreach (var file in new[] { old, recent, unrelated })
            File.WriteAllText(file, "x");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow - DocumentPrintWorkflowService.StaleCopyAge - TimeSpan.FromMinutes(5));

        var redactionService = new RedactionService(NullLogger<RedactionService>.Instance, NullLoggerFactory.Instance);
        var workflow = new DocumentPrintWorkflowService(
            new RedactionWorkflowService(
                redactionService,
                new PdfTextExtractionService(NullLogger<PdfTextExtractionService>.Instance),
                NullLogger<RedactionWorkflowService>.Instance),
            new RecordingDocumentPrinter(),
            NullLogger<DocumentPrintWorkflowService>.Instance,
            () => _printDir);

        workflow.SweepStaleCopies().Should().Be(1);
        File.Exists(old).Should().BeFalse("a copy older than the stale age is a crash leftover");
        File.Exists(recent).Should().BeTrue("a recent copy may belong to a print still in progress");
        File.Exists(unrelated).Should().BeTrue("only excise's own print copies are swept");
    }

    [Fact]
    public void DefaultPrintDirectory_IsUnderTheAppCache_NotDocuments()
    {
        var redactionService = new RedactionService(NullLogger<RedactionService>.Instance, NullLoggerFactory.Instance);
        var workflow = new DocumentPrintWorkflowService(
            new RedactionWorkflowService(
                redactionService,
                new PdfTextExtractionService(NullLogger<PdfTextExtractionService>.Instance),
                NullLogger<RedactionWorkflowService>.Instance),
            new RecordingDocumentPrinter(),
            NullLogger<DocumentPrintWorkflowService>.Instance);

        workflow.PrintDirectory.Should().Be(Path.Combine(AppPaths.CacheDir, DocumentPrintWorkflowService.PrintDirectoryName));
        workflow.PrintDirectory.Should().NotContain(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) is { Length: > 0 } docs ? docs : "\0");
    }

    // ── redaction safety ────────────────────────────────────────────────

    /// <summary>
    /// A marked-but-not-applied redaction must be REMOVED from what prints.
    /// Both oracles are independent of excise's extractor: the carrier-agnostic
    /// scanner over the saved bytes, and mutool.
    /// </summary>
    [FixedAvaloniaFact(Timeout = 90000)]
    public async Task Print_WithPendingRedaction_TheCopyHoldsNoRedactedText_SavedBytesOracle()
    {
        var bytes = await PrintCopyAsync(markSecret: true, "pending");

        SavedPdfLeakScanner.FindTerm(bytes, Secret).Should().BeEmpty(
            "the pending redaction must be applied to the print copy, not merely drawn over it");
        SavedPdfLeakScanner.FindTerm(bytes, Survivor).Should().NotBeEmpty(
            "content outside the marked area must still print (and proves the scanner reads this file)");
    }

    [FixedAvaloniaFact(Timeout = 90000)]
    public async Task Print_WithPendingRedaction_TheCopyHoldsNoRedactedText_IndependentExtractor()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var bytes = await PrintCopyAsync(markSecret: true, "pending-mutool");
        var extracted = MutoolTextExtractor.ExtractPage(WriteSnapshot(bytes, "pending-mutool-copy.pdf"), 1);

        extracted.Should().NotBeNull("mutool must be able to read the print copy");
        extracted!.Should().NotContain(Secret, "an independent extractor must not read the redacted term from the print copy");
        extracted.Should().Contain(Survivor);
    }

    /// <summary>
    /// Planted leak: the same driver with no mark. Both oracles MUST find the
    /// term, or the two tests above could pass on a copy they cannot read.
    /// </summary>
    [FixedAvaloniaFact(Timeout = 90000)]
    public async Task PlantedLeak_WithoutAMark_BothOraclesFindTheTerm()
    {
        var bytes = await PrintCopyAsync(markSecret: false, "planted");

        SavedPdfLeakScanner.FindTerm(bytes, Secret).Should().NotBeEmpty(
            "negative control: an unredacted print copy must read as leaking to the scanner");

        if (MutoolReferenceRenderer.IsAvailable)
        {
            MutoolTextExtractor.ExtractPage(WriteSnapshot(bytes, "planted-copy.pdf"), 1)
                .Should().Contain(Secret, "negative control: mutool must read the unredacted term");
        }
    }

    [FixedAvaloniaFact(Timeout = 90000)]
    public async Task Print_WithPendingRedaction_LeavesTheDocumentAndThePendingListAlone()
    {
        var h = Create(new RecordingDocumentPrinter { NextResult = DocumentPrintResult.Printed });
        await h.Vm.LoadDocumentAsync(CreateTwoTokenPdf("untouched.pdf"));
        MarkSecret(h.Vm);

        await h.Vm.PrintCommand.Execute();

        h.Vm.RedactionWorkflow.PendingCount.Should().Be(1, "printing is not applying");
        h.Vm.RedactionWorkflow.AppliedCount.Should().Be(0);
        h.DocumentService.GetCurrentDocument()!.GetPage(1).Text.Should().Contain(Secret,
            "the live document is only redacted by Apply All");
        h.Toasts.Should().Contain(t => t.Message.Contains("pending redactions applied"),
            "the user is told the printout differs from the unapplied document");
        AssertNoPrintCopiesLeft();
    }

    private async Task<byte[]> PrintCopyAsync(bool markSecret, string tag)
    {
        var h = Create();
        await h.Vm.LoadDocumentAsync(CreateTwoTokenPdf($"{tag}.pdf"));
        if (markSecret)
            MarkSecret(h.Vm);

        await h.Vm.PrintCommand.Execute();

        h.Printer.CopyBytes.Should().ContainSingle();
        AssertNoPrintCopiesLeft();
        return h.Printer.CopyBytes[0];
    }

    // ── encryption ──────────────────────────────────────────────────────

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task Print_PasswordProtectedDocument_PrintsAPlaintextCopy_ThatIsDeleted()
    {
        var h = Create();
        var encrypted = SaveEncrypted(CreateTwoTokenPdf("enc-src.pdf"), "enc.pdf", "user-1545", permissions: -4);
        h.DocumentService.LoadDocument(encrypted, "user-1545");
        h.DocumentService.IsEncrypted.Should().BeTrue();

        await h.Vm.PrintCommand.Execute();

        h.Printer.Requests.Should().ContainSingle();
        using (var copy = PdfDocument.Open(h.Printer.CopyBytes[0]))
        {
            copy.IsEncrypted.Should().BeFalse(
                "the print copy is deliberately plaintext so PDFKit needs no password (see DocumentPrintWorkflowService)");
        }
        AssertNoPrintCopiesLeft();
        File.Exists(encrypted).Should().BeTrue();
        using var source = PdfDocument.Open(File.ReadAllBytes(encrypted), "user-1545");
        source.IsEncrypted.Should().BeTrue("printing must not touch the encrypted source");
    }

    // ── permissions ─────────────────────────────────────────────────────

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task Print_PrintDeniedDocument_IsBlockedWithAToast_AndTheMenuItemIsDisabled()
    {
        var h = Create();
        // Bit 3 (value 4) cleared; everything else allowed.
        var restricted = SaveEncrypted(CreateTwoTokenPdf("noprint-src.pdf"), "noprint.pdf", "", permissions: -4 & ~4L);
        await h.Vm.LoadDocumentAsync(restricted);
        h.Vm.IsDocumentLoaded.Should().BeTrue("an empty user password opens without a prompt");
        h.DocumentService.GetCurrentDocument()!.EffectivePermissions.CanPrint.Should().BeFalse("fixture precondition");

        h.Vm.CanPrint.Should().BeFalse("Print… must be disabled when /P denies printing");
        h.Vm.PrintDisabledReason.Should().Contain("do not allow printing");

        await h.Vm.PrintCommand.Execute();

        h.Printer.Requests.Should().BeEmpty("a print-denied document must never reach the printer");
        h.Toasts.Should().ContainSingle(t =>
            t.Message.Contains("Blocked by document permissions") &&
            t.Details != null && t.Details.Contains("/P bit 3"));
        AssertNoPrintCopiesLeft();

        var raised = new List<string?>();
        h.Vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        h.Vm.IgnoreDocumentPermissions = true;
        raised.Should().Contain(nameof(MainWindowViewModel.CanPrint));
        h.Vm.CanPrint.Should().BeTrue("the scripting override re-enables Print…");

        await h.Vm.PrintCommand.Execute();
        h.Printer.Requests.Should().ContainSingle("the override lets the print through");
    }

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task Print_DegradedOnlyDocument_IsBlocked_BecauseExciseCannotDegrade()
    {
        var h = Create();
        // Bit 12 (value 2048) cleared: printing allowed only in degraded form.
        var degraded = SaveEncrypted(CreateTwoTokenPdf("lowq-src.pdf"), "lowq.pdf", "", permissions: -4 & ~2048L);
        await h.Vm.LoadDocumentAsync(degraded);
        var permissions = h.DocumentService.GetCurrentDocument()!.EffectivePermissions;
        permissions.CanPrint.Should().BeTrue("fixture precondition");
        permissions.CanPrintHighQuality.Should().BeFalse("fixture precondition");

        h.Vm.CanPrint.Should().BeFalse();
        await h.Vm.PrintCommand.Execute();

        h.Printer.Requests.Should().BeEmpty();
        h.Toasts.Should().ContainSingle(t => t.Details != null && t.Details.Contains("/P bit 12"));
    }

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task CanPrint_FollowsTheDocumentLifecycle()
    {
        var h = Create();
        h.Vm.CanPrint.Should().BeFalse("nothing to print before a document is open");
        h.Vm.PrintDisabledReason.Should().Be(MainWindowViewModel.PrintNeedsDocumentMessage);

        var raised = new List<string?>();
        h.Vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        await h.Vm.LoadDocumentAsync(CreateTwoTokenPdf("lifecycle.pdf"));

        raised.Should().Contain(nameof(MainWindowViewModel.CanPrint), "the menu item must be told to re-enable");
        h.Vm.CanPrint.Should().BeTrue();
        h.Vm.PrintDisabledReason.Should().BeNull();

        raised.Clear();
        await h.Vm.CloseDocumentCommand.Execute();
        raised.Should().Contain(nameof(MainWindowViewModel.CanPrint));
        h.Vm.CanPrint.Should().BeFalse();
    }

    // ── platforms without a printer ─────────────────────────────────────

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task Print_OnAPlatformWithoutAPrinter_ExplainsWhy_AndWritesNothing()
    {
        var h = Create(new RecordingDocumentPrinter { IsSupported = false });
        await h.Vm.LoadDocumentAsync(CreateTwoTokenPdf("unsupported.pdf"));

        h.Vm.CanPrint.Should().BeTrue("the item stays enabled so the command can explain itself");
        await h.Vm.PrintCommand.Execute();

        h.Printer.Requests.Should().BeEmpty();
        h.Dialog.Messages.Should().ContainSingle(m =>
            m.Title == "Print" && m.Message == UnsupportedDocumentPrinter.DefaultReason);
        Directory.Exists(_printDir).Should().BeFalse("no copy is written when nothing can print it");
    }

    [Fact]
    public void Factory_PicksPdfKitOnMacOs_GdiOnWindows_AndTheHonestRefusalElsewhere()
    {
        var printer = DocumentPrinterFactory.CreateForCurrentPlatform(NullLoggerFactory.Instance);
        if (OperatingSystem.IsMacOS())
        {
            printer.Should().BeOfType<MacPdfKitDocumentPrinter>();
            printer.IsSupported.Should().BeTrue();
        }
        else if (OperatingSystem.IsWindows())
        {
            // #1546. Not executed on the macOS dev box.
            printer.Should().BeOfType<WindowsDocumentPrinter>();
            printer.IsSupported.Should().Be(Environment.Is64BitProcess);
        }
        else
        {
            printer.Should().BeOfType<UnsupportedDocumentPrinter>();
            printer.IsSupported.Should().BeFalse();
        }
    }
}
