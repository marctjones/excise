using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Excise.App.Services;
using Excise.App.Services.Printing;
using Excise.App.Tests.Utilities.Fakes;
using Excise.App.ViewModels;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #1546: the Windows printer with its two Win32 halves replaced — a scripted
/// print dialog and a spooler that does everything the GDI one does short of
/// talking to a printer (asks the sheet source for each sheet at a device
/// geometry and takes its BGRA pixels). What runs for real: reading the print
/// copy, the sheet order, auto-rotate, placement, and excise's rasteriser.
/// </summary>
/// <remarks>
/// ⚠️ Most of this file fakes both Win32 halves on every platform — but the
/// two <c>GdiSpooler_*</c> tests below do NOT. They skip off Windows and, on
/// Windows, drive the real <c>System.Drawing.Printing.PrintDocument</c>
/// path, the printer DC and a real driver via "Microsoft Print to PDF", which
/// <c>.github/workflows/windows.yml</c> provisions. Both were observed passing
/// there on 2026-09-20.
/// <para>
/// So <c>PrintDlgExW</c> — the modal dialog — is the only piece still
/// unexercised by any automated run; it cannot be driven headlessly and stays
/// a manual check (see the #1546 PR description). This remark said "nothing
/// here runs on Windows" until 2026-09-20, which was true when written and
/// has not been since the runner started provisioning a printer.
/// </para>
/// </remarks>
[Collection("AvaloniaTests")]
public class WindowsDocumentPrinterTests : IDisposable
{
    private const string Secret = "WINPRINTSECRET1546";
    private const string Survivor = "WINPRINTSURVIVOR1546";

    private static readonly PrintDeviceGeometry Letter100 = new(8.5, 11, 0.25, 0.25, 8.0, 10.5, 100, 100);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "excise-1546-print", Guid.NewGuid().ToString("N"));

    public WindowsDocumentPrinterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // ── fakes ──────────────────────────────────────────────────────────

    private sealed class ScriptedDialog(WindowsPrintTicket ticket) : IWindowsPrintDialog
    {
        public List<(nint Owner, int PageCount)> Calls { get; } = new();

        public int? ThreadId { get; private set; }

        public WindowsPrintTicket Show(nint ownerHwnd, int pageCount)
        {
            ThreadId = Environment.CurrentManagedThreadId;
            Calls.Add((ownerHwnd, pageCount));
            return ticket;
        }
    }

    private sealed record SpooledSheet(
        int PageIndex, int Width, int Height, PrintPlacement Placement, bool? Landscape, byte[] FirstPixelBgra, double TopBandDark, double BottomBandDark);

    /// <summary>
    /// Everything <c>GdiPrintSpooler</c> does with the sheet source, against a
    /// fixed Letter device, recording what a printer would have received.
    /// </summary>
    private sealed class RasterisingSpooler : IWindowsPrintSpooler
    {
        public List<SpooledSheet> Sheets { get; } = new();

        public int? ThreadId { get; private set; }

        public string? JobTitle { get; private set; }

        /// <summary>Invoked after each sheet; lets a test cancel mid-job.</summary>
        public Action<int>? AfterSheet { get; set; }

        /// <summary>Runs while the print copy still exists.</summary>
        public Action? DuringSpool { get; set; }

        public DocumentPrintResult Spool(
            WindowsPrintTicket ticket, PrintSheetSource sheets, string jobTitle, CancellationToken cancellationToken)
        {
            ThreadId = Environment.CurrentManagedThreadId;
            JobTitle = jobTitle;
            DuringSpool?.Invoke();
            for (int sheet = 0; sheet < sheets.SheetCount; sheet++)
            {
                if (cancellationToken.IsCancellationRequested)
                    return DocumentPrintResult.Cancelled;

                bool? landscape = sheets.ChooseLandscape(sheet, 850, 1100);
                using var raster = sheets.Render(sheet, Letter100, cancellationToken);
                double top = DarkFraction(raster.Bitmap, 0.05, 0.15);
                double bottom = DarkFraction(raster.Bitmap, 0.82, 0.87);
                nint pixels = raster.GetBgraPixels(out int width, out int height, out int stride);
                stride.Should().Be(width * 4);
                var first = new byte[4];
                System.Runtime.InteropServices.Marshal.Copy(pixels, first, 0, 4);
                Sheets.Add(new SpooledSheet(raster.PageIndex, width, height, raster.Placement, landscape, first, top, bottom));
                AfterSheet?.Invoke(sheet);
            }
            return DocumentPrintResult.Printed;
        }

        private static double DarkFraction(SKBitmap bitmap, double fromY, double toY)
        {
            int y0 = (int)(bitmap.Height * fromY), y1 = (int)(bitmap.Height * toY);
            long dark = 0, total = 0;
            for (int y = y0; y < y1; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    var c = bitmap.GetPixel(x, y);
                    if (c.Red < 128 && c.Green < 128 && c.Blue < 128)
                        dark++;
                    total++;
                }
            }
            return total == 0 ? 0 : (double)dark / total;
        }
    }

    private static WindowsPrintTicket Ticket(
        IReadOnlyList<PrintPageRange>? ranges = null, int copies = 1, bool collate = true) =>
        new(WindowsPrintDialogOutcome.Print, "Fake Printer 1546", ranges ?? Array.Empty<PrintPageRange>(), copies, collate, new object());

    private static WindowsDocumentPrinter Printer(IWindowsPrintDialog dialog, IWindowsPrintSpooler spooler, nint owner = 0x1546) =>
        new(dialog, spooler, NullLogger.Instance, isSupported: true, ownerResolver: _ => owner);

    private string ThreePagePdf(string name)
    {
        var path = Path.Combine(_dir, name);
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();                // 0: Letter portrait
        doc.Pages.AddBlank(792, 612);        // 1: Letter landscape
        doc.Pages.AddBlank(288, 432);        // 2: 4 x 6 card
        doc.Save(path);
        return path;
    }

    private static DocumentPrintRequest Request(string path, PrintScalingMode scaling = PrintScalingMode.ShrinkOversized,
        CancellationToken cancellationToken = default) =>
        new(path, "job 1546", scaling, Owner: null, cancellationToken);

    /// <summary>
    /// A one-page PDF with two annotations that the view rule and the print rule
    /// disagree about (#1573): a red square with no <c>/F</c> at all, which
    /// §12.5.3 keeps off paper, and a blue <c>NoView|Print</c> square, which is
    /// a print-only watermark. Hand-written, because
    /// <c>PdfAnnotationAuthoring</c> stamps <c>/F Print</c> on everything it
    /// creates — the flag under test.
    /// </summary>
    private string AnnotationFlagPdf(string name)
    {
        var annots = new[]
        {
            "/Subtype /Square /Rect [20 500 200 680] /C [1 0 0] /IC [1 0 0] /BS << /W 3 >>",
            "/Subtype /Square /F 36 /Rect [220 500 400 680] /C [0 0 1] /IC [0 0 1] /BS << /W 3 >>",
        };
        var refs = string.Join(" ", annots.Select((_, i) => $"{4 + i} 0 R"));
        var objects = new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>\nendobj\n",
            $"3 0 obj\n<< /Type /Page /Parent 2 0 R /Annots [{refs}] >>\nendobj\n",
        }.Concat(annots.Select((a, i) => $"{4 + i} 0 obj\n<< /Type /Annot {a} >>\nendobj\n")).ToArray();

        var sb = new System.Text.StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        foreach (var o in objects)
        {
            offsets.Add(sb.Length);
            sb.Append(o);
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            sb.Append(offset.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");

        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, System.Text.Encoding.Latin1.GetBytes(sb.ToString()));
        return path;
    }

    /// <summary>
    /// #1573: the sheets this printer hands the spooler are rastered with PRINT
    /// intent, so the annotation <c>/F</c> flags follow §12.5.3's print rule and
    /// not the viewer's. Before this, a sticky note with no Print flag reached
    /// paper although Acrobat and PDFKit leave it off, and a print-only
    /// watermark was dropped.
    ///
    /// <para>The assertion is about the PIXELS the spooler receives, not about
    /// the options object: an options assertion would pass on a renderer that
    /// ignores the flag. The rule itself is pinned against Ghostscript in
    /// Excise.Rendering.Tests' AnnotationPrintIntentTests.</para>
    /// </summary>
    [Fact]
    public void PrintSheets_ApplyTheSection1253PrintRuleToAnnotationFlags()
    {
        using var document = PdfDocument.Open(File.ReadAllBytes(AnnotationFlagPdf("print-flags.pdf")));
        var sheets = new PrintSheetSource(
            document, PrintPageSequence.Build(document.PageCount, [], 1, true), PrintScalingMode.ShrinkOversized);

        using var raster = sheets.Render(0, Letter100, CancellationToken.None);

        long red = 0, blue = 0;
        for (int y = 0; y < raster.Bitmap.Height; y++)
        {
            for (int x = 0; x < raster.Bitmap.Width; x++)
            {
                var c = raster.Bitmap.GetPixel(x, y);
                if (c.Red > 150 && c.Green < 100 && c.Blue < 100) red++;
                else if (c.Blue > 150 && c.Red < 100 && c.Green < 100) blue++;
            }
        }

        red.Should().Be(0,
            "an annotation with no Print flag does not go on paper (§12.5.3)");
        blue.Should().BeGreaterThan(100,
            "a NoView annotation WITH the Print flag is exactly what the author meant to print");
    }

    // ── the printer ────────────────────────────────────────────────────

    [Fact]
    public async Task Print_AllPages_RasterisesEverySheet_InOrder_AtTheDeviceScale()
    {
        var dialog = new ScriptedDialog(Ticket());
        var spooler = new RasterisingSpooler();

        var result = await Printer(dialog, spooler).PrintAsync(Request(ThreePagePdf("all.pdf")));

        result.Should().Be(DocumentPrintResult.Printed);
        dialog.Calls.Should().Equal(((nint)0x1546, 3));
        spooler.JobTitle.Should().Be("job 1546");
        spooler.Sheets.Select(s => s.PageIndex).Should().Equal(0, 1, 2);

        // Letter on Letter shrinks to the 8" printable width at 100 DPI.
        var letter = spooler.Sheets[0];
        letter.Placement.Scale.Should().BeApproximately(8.0 / 8.5, 1e-9);
        letter.Placement.RenderDpi.Should().Be(94, "100 DPI x 0.941, floored");
        letter.Width.Should().Be((int)Math.Ceiling(612 / 72.0 * 94));
        letter.Height.Should().Be((int)Math.Ceiling(792 / 72.0 * 94));
        letter.FirstPixelBgra.Should().Equal(new byte[] { 255, 255, 255, 255 }, "a blank page prints as opaque white paper");
        letter.Landscape.Should().BeFalse();

        // The landscape page asks for landscape paper; the card fits unscaled.
        spooler.Sheets[1].Landscape.Should().BeTrue("auto-rotate turns the paper for a landscape page");
        spooler.Sheets[2].Placement.Scale.Should().Be(1.0);
        spooler.Sheets[2].Placement.RenderDpi.Should().Be(100);
    }

    [Fact]
    public async Task Print_RangesAndApplicationCopies_ProduceTheCollatedSequence()
    {
        var spooler = new RasterisingSpooler();

        var result = await Printer(new ScriptedDialog(Ticket([new PrintPageRange(2, 3)], copies: 2, collate: true)), spooler)
            .PrintAsync(Request(ThreePagePdf("ranges.pdf")));

        result.Should().Be(DocumentPrintResult.Printed);
        spooler.Sheets.Select(s => s.PageIndex).Should().Equal(1, 2, 1, 2);
    }

    [Fact]
    public async Task Print_UncollatedCopies_RepeatEachPage()
    {
        var spooler = new RasterisingSpooler();

        await Printer(new ScriptedDialog(Ticket([new PrintPageRange(1, 2)], copies: 2, collate: false)), spooler)
            .PrintAsync(Request(ThreePagePdf("uncollated.pdf")));

        spooler.Sheets.Select(s => s.PageIndex).Should().Equal(0, 0, 1, 1);
    }

    [Fact]
    public async Task Print_UsesTheScalingMode()
    {
        var spooler = new RasterisingSpooler();

        await Printer(new ScriptedDialog(Ticket([new PrintPageRange(3, 3)])), spooler)
            .PrintAsync(Request(ThreePagePdf("fit.pdf"), PrintScalingMode.FitToPage));

        spooler.Sheets.Single().Placement.Scale.Should().BeApproximately(1.75, 1e-9, "a 4x6 card fits 10.5\" tall at 1.75x");
    }

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task Print_ShowsTheDialogOnTheUiThread_AndSpoolsOffIt()
    {
        var uiThread = Environment.CurrentManagedThreadId;
        var dialog = new ScriptedDialog(Ticket());
        var spooler = new RasterisingSpooler();

        await Printer(dialog, spooler).PrintAsync(Request(ThreePagePdf("thread.pdf")));

        dialog.ThreadId.Should().Be(uiThread, "the modal dialog needs the owner window's thread");
        spooler.ThreadId.Should().NotBeNull();
        spooler.ThreadId.Should().NotBe(uiThread,
            "rasterising every page on the UI thread would freeze the window");
    }

    [Fact]
    public async Task Print_DialogCancelled_SpoolsNothing()
    {
        var spooler = new RasterisingSpooler();

        var result = await Printer(new ScriptedDialog(WindowsPrintTicket.Cancelled), spooler)
            .PrintAsync(Request(ThreePagePdf("cancel.pdf")));

        result.Should().Be(DocumentPrintResult.Cancelled);
        spooler.Sheets.Should().BeEmpty();
    }

    [Fact]
    public async Task Print_DialogFailed_ReportsItsReason()
    {
        var spooler = new RasterisingSpooler();

        var result = await Printer(new ScriptedDialog(WindowsPrintTicket.Fail("No printer is installed.")), spooler)
            .PrintAsync(Request(ThreePagePdf("nodevice.pdf")));

        result.Should().Be(DocumentPrintResult.Fail("No printer is installed."));
        spooler.Sheets.Should().BeEmpty();
    }

    [Fact]
    public async Task Print_RangeOutsideTheDocument_FailsWithoutSpooling()
    {
        var spooler = new RasterisingSpooler();

        var result = await Printer(new ScriptedDialog(Ticket([new PrintPageRange(7, 9)])), spooler)
            .PrintAsync(Request(ThreePagePdf("outside.pdf")));

        result.Should().Be(DocumentPrintResult.Fail(WindowsDocumentPrinter.NoPagesMessage));
        spooler.ThreadId.Should().BeNull();
    }

    [Fact]
    public async Task Print_WithoutAnOwnerWindow_FailsBeforeTheDialog()
    {
        var dialog = new ScriptedDialog(Ticket());

        var result = await Printer(dialog, new RasterisingSpooler(), owner: 0).PrintAsync(Request(ThreePagePdf("owner.pdf")));

        result.Should().Be(DocumentPrintResult.Fail(WindowsDocumentPrinter.NeedsWindowMessage));
        dialog.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Print_CancelledMidJob_StopsAtTheNextSheet()
    {
        using var cancellation = new CancellationTokenSource();
        var spooler = new RasterisingSpooler { AfterSheet = sheet => { if (sheet == 0) cancellation.Cancel(); } };

        var result = await Printer(new ScriptedDialog(Ticket()), spooler)
            .PrintAsync(Request(ThreePagePdf("midjob.pdf"), cancellationToken: cancellation.Token));

        result.Should().Be(DocumentPrintResult.Cancelled);
        spooler.Sheets.Should().ContainSingle();
    }

    [Fact]
    public async Task Print_CancelledBeforeStart_NeverOpensTheDialog()
    {
        var dialog = new ScriptedDialog(Ticket());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await Printer(dialog, new RasterisingSpooler())
            .PrintAsync(Request(ThreePagePdf("precancel.pdf"), cancellationToken: cancellation.Token));

        result.Should().Be(DocumentPrintResult.Cancelled);
        dialog.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Print_UnreadableCopy_FailsWithAMessage()
    {
        var path = Path.Combine(_dir, "not-a.pdf");
        File.WriteAllText(path, "this is not a PDF");
        var dialog = new ScriptedDialog(Ticket());

        var result = await Printer(dialog, new RasterisingSpooler()).PrintAsync(Request(path));

        result.Outcome.Should().Be(DocumentPrintOutcome.Failed);
        result.Error.Should().NotBeNullOrWhiteSpace();
        dialog.Calls.Should().BeEmpty("nothing is offered for printing before the copy has been read");
    }

    [Fact]
    public async Task Print_KeepsNoHandleOnTheCopy()
    {
        var path = ThreePagePdf("handle.pdf");
        var spooler = new RasterisingSpooler();
        spooler.DuringSpool = () =>
        {
            // Windows refuses to delete a file with an open handle; the
            // workflow deletes the copy the moment the printer returns.
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        };

        var result = await Printer(new ScriptedDialog(Ticket()), spooler).PrintAsync(Request(path));

        result.Should().Be(DocumentPrintResult.Printed);
    }

    [Fact]
    public async Task UnsupportedPrinter_RefusesWithoutADialog()
    {
        var dialog = new ScriptedDialog(Ticket());
        var printer = new WindowsDocumentPrinter(dialog, new RasterisingSpooler(), NullLogger.Instance, isSupported: false);

        var result = await printer.PrintAsync(Request(ThreePagePdf("unsupported.pdf")));

        printer.IsSupported.Should().BeFalse();
        result.Outcome.Should().Be(DocumentPrintOutcome.Failed);
        result.Error.Should().Contain("64-bit");
        dialog.Calls.Should().BeEmpty();
    }

    // ── platform selection ─────────────────────────────────────────────

    [Fact]
    public void Factory_PicksThePlatformPrinter_AndLoadsSystemDrawingOnlyOnWindows()
    {
        var printer = DocumentPrinterFactory.CreateForCurrentPlatform(NullLoggerFactory.Instance);

        if (OperatingSystem.IsWindows())
        {
            // Never executed on the macOS dev box; a Windows run must cover it.
            printer.Should().BeOfType<WindowsDocumentPrinter>();
            printer.IsSupported.Should().Be(Environment.Is64BitProcess);
            return;
        }

        printer.Should().NotBeOfType<WindowsDocumentPrinter>();
        printer.IsSupported.Should().Be(OperatingSystem.IsMacOS(), "Linux printing is out of scope");
        AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetName().Name)
            .Should().NotContain("System.Drawing.Common",
                "System.Drawing.Common is Windows-only at run time; nothing outside Windows may load it");
    }

    // ── the real spooler (Windows only) ────────────────────────────────

    private const string PrintToPdfPrinter = "Microsoft Print to PDF";

    /// <summary>
    /// The GDI spooler against the printer every Windows 10/11 install has,
    /// printing to a file so no dialog or paper is involved. Skipped here;
    /// this is what a Windows run must execute. The dialog itself is modal and
    /// stays a manual check.
    /// </summary>
    [Fact]
    public void GdiSpooler_PrintsEveryPage_ToMicrosoftPrintToPdf()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the GDI print path exists only on Windows");
        SpoolToPrintToPdf(cancelled: false);
    }

    [Fact]
    public void GdiSpooler_AbortsACancelledJob()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the GDI print path exists only on Windows");
        SpoolToPrintToPdf(cancelled: true);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void SpoolToPrintToPdf(bool cancelled)
    {
        Assert.SkipUnless(
            System.Drawing.Printing.PrinterSettings.InstalledPrinters.Cast<string>().Contains(PrintToPdfPrinter),
            $"the \"{PrintToPdfPrinter}\" printer is not installed");

        var output = Path.Combine(_dir, cancelled ? "aborted.pdf" : "spooled.pdf");
        var settings = new System.Drawing.Printing.PrinterSettings
        {
            PrinterName = PrintToPdfPrinter,
            PrintToFile = true,
            PrintFileName = output,
        };
        var ticket = new WindowsPrintTicket(
            WindowsPrintDialogOutcome.Print, PrintToPdfPrinter, Array.Empty<PrintPageRange>(), 1, true, settings);
        using var document = PdfDocument.Open(File.ReadAllBytes(ThreePagePdf("spool-source.pdf")));
        var sheets = new PrintSheetSource(
            document, PrintPageSequence.Build(document.PageCount, [], 1, true), PrintScalingMode.ShrinkOversized);
        using var cancellation = new CancellationTokenSource();
        if (cancelled)
            cancellation.Cancel();

        var result = new GdiPrintSpooler(NullLogger.Instance).Spool(ticket, sheets, "spool 1546", cancellation.Token);

        if (cancelled)
        {
            result.Should().Be(DocumentPrintResult.Cancelled);
            return;
        }

        result.Should().Be(DocumentPrintResult.Printed);
        File.Exists(output).Should().BeTrue();
        using var printed = PdfDocument.Open(File.ReadAllBytes(output));
        printed.PageCount.Should().Be(3);
        printed.GetPage(1).VisualWidth.Should().BeLessThan(printed.GetPage(1).VisualHeight, "a portrait page prints portrait");
        printed.GetPage(2).VisualWidth.Should().BeGreaterThan(printed.GetPage(2).VisualHeight,
            "auto-rotate turns the paper for the landscape page");
    }

    // ── end to end through the view model ──────────────────────────────

    /// <summary>
    /// Cmd/Ctrl+P with a pending redaction, through the real workflow and the
    /// Windows printer: the dialog is shown, every page is rasterised from a
    /// print copy that holds no redacted text (carrier-agnostic scan of the
    /// bytes the printer read, with the survivor as the control), the black
    /// box prints where the text was, and the copy is gone afterwards.
    /// </summary>
    [FixedAvaloniaFact(Timeout = 90000)]
    public async Task ViewModel_PrintsThroughTheWindowsPrinter_WithPendingRedactionsApplied()
    {
        var printDir = Path.Combine(_dir, "print-copies");
        var dialog = new ScriptedDialog(Ticket());
        var spooler = new RasterisingSpooler();
        byte[]? copyBytes = null;
        spooler.DuringSpool = () => copyBytes = File.ReadAllBytes(Directory.GetFiles(printDir).Single());
        var printer = Printer(dialog, spooler);

        var loggerFactory = NullLoggerFactory.Instance;
        var documentService = new PdfDocumentService(NullLogger<PdfDocumentService>.Instance);
        var redactionService = new RedactionService(NullLogger<RedactionService>.Instance, loggerFactory);
        var extraction = new PdfTextExtractionService(NullLogger<PdfTextExtractionService>.Instance);
        var redactionWorkflow = new RedactionWorkflowService(
            redactionService, extraction, NullLogger<RedactionWorkflowService>.Instance);
        var messages = new List<string>();
        var vm = MainWindowViewModelTestFactory.Create(
            documentService: documentService,
            redactionService: redactionService,
            textExtractionService: extraction,
            redactionWorkflowService: redactionWorkflow,
            dialogService: new MessageRecorder(messages),
            settingsStore: new InMemorySettingsStore(),
            printWorkflow: new DocumentPrintWorkflowService(
                redactionWorkflow, printer, NullLogger<DocumentPrintWorkflowService>.Instance, () => printDir));

        var source = Path.Combine(_dir, "vm.pdf");
        using (var doc = PdfDocument.CreateNew())
        {
            var page = doc.Pages.AddBlank();
            using (var g = page.GetGraphics())
            {
                var font = PdfFont.Helvetica(18);
                g.DrawString(Secret, font, PdfBrush.Black, 100, 700);
                g.DrawString(Survivor, font, PdfBrush.Black, 100, 120);
                g.Flush();
            }
            doc.Pages.AddBlank();
            doc.Save(source);
        }
        await vm.LoadDocumentAsync(source);
        vm.IsRedactionMode = true;
        vm.RedactionWorkflow.MarkArea(PdfPageRect.FromContentPoints(1, new PdfRectangle(40, 675, 500, 750)), Secret);

        await vm.PrintCommand.Execute();

        messages.Should().BeEmpty();
        dialog.Calls.Should().Equal(((nint)0x1546, 2));
        spooler.JobTitle.Should().Be("vm.pdf");
        spooler.Sheets.Select(s => s.PageIndex).Should().Equal(0, 1);

        copyBytes.Should().NotBeNull("the printer must read the copy while it exists");
        SavedPdfLeakScanner.FindTerm(copyBytes!, Secret).Should().BeEmpty(
            "the pending redaction is applied to what prints, not drawn over it");
        SavedPdfLeakScanner.FindTerm(copyBytes!, Survivor).Should().NotBeEmpty(
            "unmarked content still prints (and proves the scan reads this file)");

        var first = spooler.Sheets[0];
        first.TopBandDark.Should().BeGreaterThan(0.3, "the redaction box prints over the removed text");
        first.BottomBandDark.Should().BeGreaterThan(0.005, "the survivor line prints");
        first.BottomBandDark.Should().BeLessThan(0.3, "the survivor line is text, not a box");

        Directory.GetFiles(printDir).Should().BeEmpty("the print copy is deleted once the job is spooled");
        vm.RedactionWorkflow.PendingCount.Should().Be(1, "printing is not applying");
    }

    private sealed class MessageRecorder(List<string> messages) : IUserDialogService
    {
        public Task ShowMessageAsync(string title, string message)
        {
            messages.Add($"{title}: {message}");
            return Task.CompletedTask;
        }

        public Task<bool> ShowConfirmAsync(string title, string message) => Task.FromResult(false);
    }
}
