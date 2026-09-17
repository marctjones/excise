using System;
using System.IO;
using AwesomeAssertions;
using Excise.App.Services.Printing;
using Xunit;

namespace Excise.App.Tests.Unit;

/// <summary>
/// #1545: the Objective-C half of printing, exercised without any UI. The
/// print SHEET needs a live window and is checked by hand (see the PR
/// description); what is provable here is that PDFKit loads, opens a PDF excise
/// wrote, and builds an <c>NSPrintOperation</c> with the requested panel
/// options, all without running it.
/// </summary>
[Collection("AvaloniaTests")]
public class MacPdfKitPrintInteropTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "excise-1545-interop", Guid.NewGuid().ToString("N"));

    public MacPdfKitPrintInteropTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void PdfKit_OpensAnExciseWrittenPdf_AndReadsItsPageCount()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "PDFKit exists only on macOS");

        MacPdfKitInterop.TryResolveApi().Should().BeTrue("PDFKit, AppKit and the ObjC runtime must bind");
        var path = TestPdfGenerator.CreateMultiPagePdf(Path.Combine(_dir, "three.pdf"), pageCount: 3);

        MacPdfKitInterop.ProbePageCount(path).Should().Be(3);
    }

    [Fact]
    public void PdfKit_RefusesAFileThatIsNotAPdf()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "PDFKit exists only on macOS");

        var path = Path.Combine(_dir, "not-a.pdf");
        File.WriteAllText(path, "this is not a PDF");

        MacPdfKitInterop.ProbePageCount(path).Should().Be(-1,
            "a nil PDFDocument must be reported, not dereferenced (negative control for the probe above)");
    }

    [Fact]
    public void PdfKit_BuildsAPrintOperation_WithTheRequestedPanelOptions_WithoutRunningIt()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "PDFKit exists only on macOS");

        var path = TestPdfGenerator.CreateMultiPagePdf(Path.Combine(_dir, "two.pdf"), pageCount: 2);

        foreach (var scaling in Enum.GetValues<PrintScalingMode>())
        {
            var (created, panelOptionsApplied) = MacPdfKitInterop.ProbePrintOperation(path, scaling);
            created.Should().BeTrue($"PDFKit must create an NSPrintOperation for scaling {scaling}");
            panelOptionsApplied.Should().BeTrue(
                "the sheet must offer copies, page range, paper size, orientation, scale and preview");
        }
    }

    [Fact]
    public void ScalingModes_MatchPdfKitsEnum()
    {
        // PDFDocument.h: kPDFPrintPageScaleNone = 0, ToFit = 1, DownToFit = 2.
        ((int)PrintScalingMode.ActualSize).Should().Be(0);
        ((int)PrintScalingMode.FitToPage).Should().Be(1);
        ((int)PrintScalingMode.ShrinkOversized).Should().Be(2);
    }

    [Fact]
    public void ResolveWindow_RejectsAHandleThatIsNotAWindowOrView()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "PDFKit exists only on macOS");

        MacPdfKitInterop.TryResolveApi().Should().BeTrue();
        MacPdfKitInterop.ResolveWindow(0, "NSWindow").Should().Be(0);
        // A class object is an ObjC object, but neither a window nor a view.
        MacPdfKitInterop.ResolveWindow(MacPdfKitInterop.Class("NSObject"), "NSWindow").Should().Be(0);
    }

    [Fact]
    public async System.Threading.Tasks.Task UnsupportedPrinter_NeverPrints_AndExplainsWhy()
    {
        var printer = new UnsupportedDocumentPrinter();
        printer.IsSupported.Should().BeFalse();
        printer.UnsupportedReason.Should().Contain("#1546");
        var result = await printer.PrintAsync(new DocumentPrintRequest("x.pdf", "x", PrintScalingMode.ShrinkOversized, null));
        result.Outcome.Should().Be(DocumentPrintOutcome.Failed);
    }
}
