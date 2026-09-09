using System.Diagnostics;
using System.Reactive.Linq;
using AwesomeAssertions;
using Excise.App.Services;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #1306 — Bates numbering, production-wired.
/// </summary>
/// <remarks>
/// <para>
/// README's Desktop app feature list advertised "Bates numbering" while
/// <c>BatesNumberingService</c> had zero production callers — no command, no
/// menu item, no CLI verb. Worse, <c>ApplyBatesNumbers</c> (the single-document
/// entry point a GUI needs) was classified <c>nowhere</c> in the unwired-API
/// baseline: not reached even by the 45 existing tests, which mostly assert
/// <c>BatesOptions</c> default values rather than exercising the pipeline.
/// </para>
/// <para>
/// <b>The stamp is read back by pdftotext, not by excise.</b> "The number is on
/// the page" is a claim about output, and excise's own extractor sharing a bug
/// with excise's own writer would confirm it happily. Poppler is a separate
/// implementation.
/// </para>
/// </remarks>
[Collection("AvaloniaTests")]
public class BatesNumberingWorkflowTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), $"excise-bates-{Guid.NewGuid():N}");

    public BatesNumberingWorkflowTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    private string NewPdf(string name, int pages = 3)
    {
        var path = Path.Combine(_tempDir, name);
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: pages);
        return path;
    }

    private static bool PdftotextAvailable()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("pdftotext", "-v")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            })!;
            p.WaitForExit(10000);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Independent oracle: Poppler's view of the page text.</summary>
    private static string PdftotextPage(string pdfPath, int page)
    {
        var psi = new ProcessStartInfo(
            "pdftotext", $"-f {page} -l {page} \"{pdfPath}\" -")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit(30000);
        return stdout;
    }

    // ------------------------------------------------------------- dialog VM

    [Fact]
    public void DialogPreview_ShowsWhatTheFirstPageWillRead()
    {
        var vm = new BatesNumberingDialogViewModel
        {
            Prefix = "DOE",
            StartNumber = 42,
            NumberOfDigits = 6,
            Suffix = "-CONF",
        };

        vm.Preview.Should().Be("DOE000042-CONF",
            "the user must see the padding and affixes before stamping anything");
    }

    [Fact]
    public void DialogOptions_CarryEveryFieldThrough_AndClampDigits()
    {
        var vm = new BatesNumberingDialogViewModel
        {
            Prefix = "ACME",
            Suffix = "-X",
            StartNumber = 7,
            NumberOfDigits = 99,          // out of range
            Position = BatesPosition.TopLeft,
            FontSize = 0,                 // nonsensical
        };

        var options = vm.ToOptions();

        options.Prefix.Should().Be("ACME");
        options.Suffix.Should().Be("-X");
        options.StartNumber.Should().Be(7);
        options.NumberOfDigits.Should().Be(12, "digits are clamped rather than producing a broken format string");
        options.Position.Should().Be(BatesPosition.TopLeft);
        options.FontSize.Should().Be(10, "a zero font size must fall back, not stamp invisible text");
    }

    [Fact]
    public void Dialog_NotConfirmed_ByDefault()
    {
        new BatesNumberingDialogViewModel().Confirmed.Should().BeFalse(
            "only pressing Apply may count as confirmation");
    }

    // ------------------------------------------------------------- wiring

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task BatesCommand_Cancelled_ChangesNothing()
    {
        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1000, Height = 700 };
        window.Show();
        await vm.LoadDocumentAsync(NewPdf("cancelled.pdf"));

        vm.BatesOptionsOverride = () => null;   // user pressed Cancel
        await vm.BatesNumberingCommand.Execute();

        vm.HasUnsavedDocumentChanges.Should().BeFalse(
            "cancelling the dialog must not mark the document modified");

        window.Close();
    }

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task BatesCommand_MarksTheDocumentDirty_ButDoesNotTouchTheSourceFile()
    {
        var pdf = NewPdf("dirty.pdf");
        var originalBytes = await File.ReadAllBytesAsync(pdf);

        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1000, Height = 700 };
        window.Show();
        await vm.LoadDocumentAsync(pdf);

        vm.BatesOptionsOverride = () => new BatesOptions { Prefix = "DOE", StartNumber = 1 };
        await vm.BatesNumberingCommand.Execute();

        vm.HasUnsavedDocumentChanges.Should().BeTrue(
            "the stamp is a pending edit, so the save routing and close guard apply (#1233)");
        (await File.ReadAllBytesAsync(pdf)).Should().Equal(originalBytes,
            "Bates numbering is applied to evidence sets — it must never rewrite the source in place");

        window.Close();
    }

    /// <summary>
    /// The whole point of the issue: a user can now reach this, and it works.
    /// Verified by Poppler rather than by excise reading its own output.
    /// </summary>
    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task BatesCommand_StampsEveryPage_VerifiedByPdftotext()
    {
        Assert.SkipWhen(!PdftotextAvailable(), "pdftotext is not installed [requires: tool:pdftotext]");

        var pdf = NewPdf("stamped.pdf", pages: 3);
        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1000, Height = 700 };
        window.Show();
        await vm.LoadDocumentAsync(pdf);

        vm.BatesOptionsOverride = () => new BatesOptions
        {
            Prefix = "DOE",
            StartNumber = 1,
            NumberOfDigits = 6,
            Position = BatesPosition.BottomRight,
        };
        await vm.BatesNumberingCommand.Execute();

        var output = Path.Combine(_tempDir, "stamped-out.pdf");
        await vm.SaveFileAsAsync(output);

        // Sequential and per-page: a stamp that numbered every page "000001"
        // would pass a "contains DOE" assertion and be useless.
        PdftotextPage(output, 1).Should().Contain("DOE000001");
        PdftotextPage(output, 2).Should().Contain("DOE000002");
        PdftotextPage(output, 3).Should().Contain("DOE000003");

        PdftotextPage(output, 1).Should().NotContain("DOE000002",
            "each page must carry its own number, not the whole sequence");

        window.Close();
    }

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task BatesCommand_HonoursPrefixSuffixAndStartNumber_VerifiedByPdftotext()
    {
        Assert.SkipWhen(!PdftotextAvailable(), "pdftotext is not installed [requires: tool:pdftotext]");

        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1000, Height = 700 };
        window.Show();
        await vm.LoadDocumentAsync(NewPdf("affixes.pdf", pages: 2));

        vm.BatesOptionsOverride = () => new BatesOptions
        {
            Prefix = "ACME-",
            Suffix = "-CONF",
            StartNumber = 500,
            NumberOfDigits = 4,
            Position = BatesPosition.TopLeft,
        };
        await vm.BatesNumberingCommand.Execute();

        var output = Path.Combine(_tempDir, "affixes-out.pdf");
        await vm.SaveFileAsAsync(output);

        PdftotextPage(output, 1).Should().Contain("ACME-0500-CONF",
            "prefix, zero padding, start number and suffix must all reach the page");
        PdftotextPage(output, 2).Should().Contain("ACME-0501-CONF");

        window.Close();
    }
}
