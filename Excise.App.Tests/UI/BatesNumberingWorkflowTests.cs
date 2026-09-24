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
        // #925/#1516: drain both redirected pipes concurrently and bound the
        // WAIT, not just the process. A synchronous ReadToEnd() here blocks
        // forever if the pipe never reaches EOF (a surviving grandchild
        // inherits the write handle — the #1068 mechanism), and it runs BEFORE
        // WaitForExit, so the timeout below is unreachable. This body runs on
        // the single Avalonia headless dispatcher thread, so one blocked read
        // wedges every remaining Avalonia test in the process.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* gone */ }
            throw new TimeoutException(
                "pdftotext did not exit within 30s; killed it rather than hanging the suite (#1516).");
        }
        _ = stderrTask.GetAwaiter().GetResult();
        return stdoutTask.GetAwaiter().GetResult();
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
    // ------------------------------------------------------------- undo (#1805)

    /// <summary>
    /// The dialog calls the stamp "real page content", so it must be as undoable as a
    /// rotation. Read back with Poppler, not excise: an undo that only reset a counter
    /// would still leave the number on the page.
    /// </summary>
    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task BatesNumbering_CanBeUndone_AndTheStampLeavesThePage()
    {
        Assert.SkipWhen(!PdftotextAvailable(), "pdftotext is not installed [requires: tool:pdftotext]");

        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1000, Height = 700 };
        window.Show();
        await vm.LoadDocumentAsync(NewPdf("undo.pdf", pages: 3));

        vm.BatesOptionsOverride = () => new BatesOptions { Prefix = "DOE", StartNumber = 1 };
        await vm.BatesNumberingCommand.Execute();

        vm.CanUndo.Should().BeTrue("Bates numbering must join the undo stack");
        vm.UndoMenuHeader.Should().Be("_Undo Bates numbering",
            "Edit > Undo names what it will undo, the macOS convention");

        await vm.UndoCommand.Execute();

        var output = Path.Combine(_tempDir, "undo-out.pdf");
        await vm.SaveFileAsAsync(output);
        for (var page = 1; page <= 3; page++)
            PdftotextPage(output, page).Should().NotContain("DOE",
                $"page {page} still carries the stamp after Undo");

        window.Close();
    }

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task BatesNumbering_UndoThenRedo_RestoresTheStamp()
    {
        Assert.SkipWhen(!PdftotextAvailable(), "pdftotext is not installed [requires: tool:pdftotext]");

        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1000, Height = 700 };
        window.Show();
        await vm.LoadDocumentAsync(NewPdf("redo.pdf", pages: 3));

        vm.BatesOptionsOverride = () => new BatesOptions { Prefix = "DOE", StartNumber = 1 };
        await vm.BatesNumberingCommand.Execute();
        await vm.UndoCommand.Execute();
        vm.CanRedo.Should().BeTrue();
        await vm.RedoCommand.Execute();

        var output = Path.Combine(_tempDir, "redo-out.pdf");
        await vm.SaveFileAsAsync(output);
        for (var page = 1; page <= 3; page++)
            PdftotextPage(output, page).Should().Contain($"DOE{page:D6}");

        window.Close();
    }
    // ------------------------------------------------------------- position

    /// <summary>Poppler's word box for <paramref name="word"/>: yMin/yMax measured DOWN from the page top.</summary>
    private static (double XMin, double YMin, double XMax, double YMax, double PageWidth, double PageHeight) PdftotextWordBox(
        string pdfPath, string word)
    {
        var psi = new ProcessStartInfo("pdftotext", $"-bbox -f 1 -l 1 \"{pdfPath}\" -")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi)!;
        // #1068/#1516: drain BOTH pipes concurrently and bound the WAIT, not just the process; a
        // synchronous ReadToEnd() blocks forever if the pipe never reaches EOF, and xUnit's
        // Timeout cannot abort it on the single Avalonia headless thread.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* gone */ }
            throw new TimeoutException("pdftotext -bbox did not exit within 30s; killed it (#1516).");
        }
        _ = stderrTask.GetAwaiter().GetResult();
        var output = stdoutTask.GetAwaiter().GetResult();

        var page = System.Text.RegularExpressions.Regex.Match(output, "<page width=\"([\\d.]+)\" height=\"([\\d.]+)\"");
        var box = System.Text.RegularExpressions.Regex.Match(output,
            "<word xMin=\"([\\d.]+)\" yMin=\"([\\d.]+)\" xMax=\"([\\d.]+)\" yMax=\"([\\d.]+)\">" + word);
        box.Success.Should().BeTrue($"Poppler must find '{word}' on the page");
        double D(System.Text.RegularExpressions.Group g) => double.Parse(g.Value, System.Globalization.CultureInfo.InvariantCulture);
        return (D(box.Groups[1]), D(box.Groups[2]), D(box.Groups[3]), D(box.Groups[4]), D(page.Groups[1]), D(page.Groups[2]));
    }

    /// <summary>
    /// A Bates number is cited by where it sits; BottomRight is the convention for a
    /// production. Judged by Poppler's own coordinates, not excise's (the stamp used to land
    /// on the opposite edge and every text-only assertion above passed).
    /// </summary>
    [Theory]
    [InlineData(BatesPosition.BottomRight, true, false)]
    [InlineData(BatesPosition.BottomLeft, true, true)]
    [InlineData(BatesPosition.TopRight, false, false)]
    [InlineData(BatesPosition.TopLeft, false, true)]
    public void BatesStamp_LandsInTheRequestedCorner_MeasuredByPoppler(
        BatesPosition position, bool bottom, bool left)
    {
        Assert.SkipWhen(!PdftotextAvailable(), "pdftotext is not installed [requires: tool:pdftotext]");

        var src = NewPdf($"pos-{position}.pdf", pages: 1);
        using (var doc = Excise.Core.Document.PdfDocument.Open(src))
        {
            new BatesNumberingService(Microsoft.Extensions.Logging.Abstractions.NullLogger<BatesNumberingService>.Instance)
                .ApplyBatesNumbers(doc, new BatesOptions { Prefix = "BATESPOS", Position = position });
            doc.Save(Path.Combine(_tempDir, $"pos-{position}-out.pdf"));
        }

        var box = PdftotextWordBox(Path.Combine(_tempDir, $"pos-{position}-out.pdf"), "BATESPOS000001");

        if (bottom)
            box.YMin.Should().BeGreaterThan(box.PageHeight / 2, $"{position} is on the BOTTOM half of the page");
        else
            box.YMax.Should().BeLessThan(box.PageHeight / 2, $"{position} is on the TOP half of the page");

        if (left)
            box.XMax.Should().BeLessThan(box.PageWidth / 2, $"{position} is on the LEFT half of the page");
        else
            box.XMin.Should().BeGreaterThan(box.PageWidth / 2, $"{position} is on the RIGHT half of the page");
    }
}
