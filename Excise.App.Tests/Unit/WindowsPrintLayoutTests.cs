using System;
using System.Linq;
using System.Runtime.InteropServices;
using AwesomeAssertions;
using Excise.App.Services.Printing;
using Xunit;

namespace Excise.App.Tests.Unit;

/// <summary>
/// #1546: the platform-neutral arithmetic of Windows printing — where a page
/// lands on the paper, what resolution it is rasterised at, which sheets come
/// out in which order, and the Win32 structure layout the dialog call relies
/// on. None of it needs Windows, so all of it runs here.
/// </summary>
public class WindowsPrintLayoutTests
{
    // Letter paper at 600 DPI with quarter-inch hard margins.
    private static readonly PrintDeviceGeometry Letter600 = new(
        PaperWidth: 8.5, PaperHeight: 11,
        PrintableX: 0.25, PrintableY: 0.25, PrintableWidth: 8.0, PrintableHeight: 10.5,
        DpiX: 600, DpiY: 600);

    private const int LetterWidthPt = 612;
    private const int LetterHeightPt = 792;

    // ── scaling ────────────────────────────────────────────────────────

    [Fact]
    public void ActualSize_PrintsAtOneHundredPercent_CentredOnThePaper()
    {
        var p = PrintPageLayout.Place(LetterWidthPt, LetterHeightPt, Letter600, PrintScalingMode.ActualSize);

        p.Scale.Should().Be(1.0);
        p.Width.Should().BeApproximately(8.5, 1e-9);
        p.Height.Should().BeApproximately(11, 1e-9);
        p.X.Should().BeApproximately(0, 1e-9, "a page the size of the paper is centred on it and loses the hard margins");
        p.Y.Should().BeApproximately(0, 1e-9);
    }

    [Fact]
    public void ShrinkOversized_ShrinksAPageThatDoesNotFitThePrintableArea()
    {
        var p = PrintPageLayout.Place(LetterWidthPt, LetterHeightPt, Letter600, PrintScalingMode.ShrinkOversized);

        p.Scale.Should().BeApproximately(Math.Min(8.0 / 8.5, 10.5 / 11), 1e-9);
        p.Width.Should().BeLessThanOrEqualTo(Letter600.PrintableWidth + 1e-9);
        p.Height.Should().BeLessThanOrEqualTo(Letter600.PrintableHeight + 1e-9);
        AssertInsidePrintable(p, Letter600);
    }

    [Fact]
    public void ShrinkOversized_LeavesASmallPageAtActualSize()
    {
        // A 4 x 6 inch card.
        var p = PrintPageLayout.Place(288, 432, Letter600, PrintScalingMode.ShrinkOversized);

        p.Scale.Should().Be(1.0, "only pages larger than the printable area shrink");
        p.Width.Should().BeApproximately(4, 1e-9);
        p.X.Should().BeApproximately((8.5 - 4) / 2, 1e-9);
        p.Y.Should().BeApproximately((11 - 6) / 2.0, 1e-9);
    }

    [Fact]
    public void FitToPage_EnlargesASmallPageToThePrintableArea()
    {
        var p = PrintPageLayout.Place(288, 432, Letter600, PrintScalingMode.FitToPage);

        p.Scale.Should().BeApproximately(Math.Min(8.0 / 4, 10.5 / 6), 1e-9);
        p.Height.Should().BeApproximately(10.5, 1e-9, "the limiting dimension fills the printable area");
        AssertInsidePrintable(p, Letter600);
    }

    [Fact]
    public void ActualSize_LargerThanThePaper_IsCentred_AndCroppedEvenly()
    {
        // Tabloid (11 x 17) at 100% on Letter.
        var p = PrintPageLayout.Place(792, 1224, Letter600, PrintScalingMode.ActualSize);

        p.Scale.Should().Be(1.0);
        p.X.Should().BeApproximately((8.5 - 11) / 2, 1e-9);
        p.Y.Should().BeApproximately((11 - 17) / 2.0, 1e-9);
    }

    [Fact]
    public void APageThatFits_IsNeverCutByAnAsymmetricHardMargin()
    {
        // A printer with a 0.1" top and a 0.6" bottom margin; 10" printable.
        var device = Letter600 with { PrintableY = 0.1, PrintableHeight = 10.3 };

        var p = PrintPageLayout.Place(612, 720, device, PrintScalingMode.ActualSize); // 8.5 x 10

        p.Height.Should().BeApproximately(10, 1e-9);
        p.Y.Should().BeGreaterThanOrEqualTo(device.PrintableY - 1e-9);
        (p.Y + p.Height).Should().BeLessThanOrEqualTo(device.PrintableY + device.PrintableHeight + 1e-9,
            "a page centred on the paper would cross the 0.6\" bottom margin; it is moved inside instead");
    }

    [Fact]
    public void Placement_RejectsADegeneratePageOrDevice()
    {
        var zeroPage = () => PrintPageLayout.Place(0, 792, Letter600, PrintScalingMode.FitToPage);
        var noPrintable = () => PrintPageLayout.Place(612, 792, Letter600 with { PrintableWidth = 0 }, PrintScalingMode.FitToPage);
        var nanPage = () => PrintPageLayout.Place(double.NaN, 792, Letter600, PrintScalingMode.FitToPage);

        zeroPage.Should().Throw<ArgumentOutOfRangeException>();
        noPrintable.Should().Throw<ArgumentOutOfRangeException>();
        nanPage.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ── resolution ─────────────────────────────────────────────────────

    [Fact]
    public void RenderDpi_MatchesTheDevice_TimesTheScale()
    {
        var device = Letter600 with { DpiX = 300, DpiY = 300 };

        PrintPageLayout.Place(288, 432, device, PrintScalingMode.ActualSize).RenderDpi.Should().Be(300);
        PrintPageLayout.Place(288, 432, device, PrintScalingMode.FitToPage).RenderDpi
            .Should().Be((int)Math.Floor(300 * Math.Min(8.0 / 4, 10.5 / 6)),
                "a page printed at 1.75x needs 1.75x the source pixels to meet the device one to one");
    }

    [Fact]
    public void RenderDpi_UsesTheCoarserAxis_AndIsCappedAtSixHundred()
    {
        var nonSquare = Letter600 with { DpiX = 1200, DpiY = 600 };
        var fine = Letter600 with { DpiX = 2400, DpiY = 2400 };

        PrintPageLayout.Place(288, 432, nonSquare, PrintScalingMode.ActualSize).RenderDpi.Should().Be(600);
        PrintPageLayout.Place(288, 432, fine, PrintScalingMode.ActualSize).RenderDpi.Should().Be(PrintPageLayout.MaxDeviceDpi);
    }

    [Fact]
    public void RenderDpi_KeepsTheBitmapWithinThePixelBudget()
    {
        // A0 (33.1 x 46.8 in) at actual size: 600 DPI would be 557 million pixels.
        var p = PrintPageLayout.Place(2384, 3370, Letter600, PrintScalingMode.ActualSize);

        double widthPx = Math.Ceiling(2384 / 72.0 * p.RenderDpi);
        double heightPx = Math.Ceiling(3370 / 72.0 * p.RenderDpi);
        (widthPx * heightPx).Should().BeLessThanOrEqualTo(PrintPageLayout.MaxPagePixels + widthPx + heightPx + 1,
            "the renderer rounds each side up; the budget holds up to that rounding");
        p.RenderDpi.Should().BeLessThan(600).And.BeGreaterThan(100);
    }

    [Fact]
    public void RenderDpi_IsNeverBelowOne()
    {
        var p = PrintPageLayout.Place(2384, 3370, Letter600 with { DpiX = 1, DpiY = 1 }, PrintScalingMode.FitToPage);

        p.RenderDpi.Should().Be(1);
    }

    // ── auto-rotate ────────────────────────────────────────────────────

    [Theory]
    [InlineData(792, 612, 850, 1100, true)]    // landscape page, portrait Letter -> rotate
    [InlineData(612, 792, 850, 1100, false)]   // portrait page, portrait paper -> keep
    [InlineData(612, 792, 1700, 1100, true)]   // portrait page on landscape-shaped paper -> rotate
    [InlineData(792, 612, 1700, 1100, false)]  // landscape page on landscape-shaped paper -> keep
    public void ChooseLandscape_MatchesThePageShapeToThePaper(
        double pageWidth, double pageHeight, double paperWidth, double paperHeight, bool expected)
    {
        PrintPageLayout.ChooseLandscape(pageWidth, pageHeight, paperWidth, paperHeight).Should().Be(expected);
    }

    [Theory]
    [InlineData(600, 600, 850, 1100)]  // square page
    [InlineData(612, 792, 1000, 1000)] // square paper
    [InlineData(0, 792, 850, 1100)]    // degenerate
    public void ChooseLandscape_LeavesTheUsersChoiceWhenThereIsNoBetterOne(
        double pageWidth, double pageHeight, double paperWidth, double paperHeight)
    {
        PrintPageLayout.ChooseLandscape(pageWidth, pageHeight, paperWidth, paperHeight).Should().BeNull();
    }

    // ── sheet order ────────────────────────────────────────────────────

    [Fact]
    public void Sequence_AllPages_OneCopy()
    {
        PrintPageSequence.Build(3, [], copies: 1, collate: true).Should().Equal(0, 1, 2);
    }

    [Fact]
    public void Sequence_Collated_RepeatsTheSet()
    {
        PrintPageSequence.Build(3, [], copies: 2, collate: true).Should().Equal(0, 1, 2, 0, 1, 2);
    }

    [Fact]
    public void Sequence_Uncollated_RepeatsEachPage()
    {
        PrintPageSequence.Build(3, [], copies: 2, collate: false).Should().Equal(0, 0, 1, 1, 2, 2);
    }

    [Fact]
    public void Sequence_Ranges_InTheOrderTyped_Clamped_ReversedReadLowToHigh_AndDeduplicated()
    {
        var sheets = PrintPageSequence.Build(
            10,
            [new PrintPageRange(8, 12), new PrintPageRange(3, 2), new PrintPageRange(9, 9), new PrintPageRange(0, 1)],
            copies: 1,
            collate: true);

        sheets.Should().Equal(7, 8, 9, 1, 2, 0);
    }

    [Fact]
    public void Sequence_RangeOutsideTheDocument_PrintsNothing()
    {
        PrintPageSequence.Build(3, [new PrintPageRange(5, 9)], copies: 3, collate: true).Should().BeEmpty();
        PrintPageSequence.Build(0, [], copies: 1, collate: true).Should().BeEmpty();
    }

    [Fact]
    public void Sequence_ClampsCopies()
    {
        PrintPageSequence.Build(1, [], copies: 0, collate: true).Should().Equal(0);
        PrintPageSequence.Build(1, [], copies: int.MaxValue, collate: true).Should().HaveCount(PrintPageSequence.MaxCopies);
    }

    // ── device caps → inches ───────────────────────────────────────────

    [Fact]
    public void DeviceCaps_ConvertToInchesFromThePaperCorner()
    {
        // A 600x1200 DPI Letter page with 0.25" / 0.2" hard margins.
        var caps = new PrintDeviceCaps(
            DpiX: 600, DpiY: 1200,
            PhysicalWidth: 5100, PhysicalHeight: 13200,
            OffsetX: 150, OffsetY: 240,
            PrintableWidth: 4800, PrintableHeight: 12720);

        var g = caps.ToGeometry();

        g.PaperWidth.Should().BeApproximately(8.5, 1e-9);
        g.PaperHeight.Should().BeApproximately(11, 1e-9);
        g.PrintableX.Should().BeApproximately(0.25, 1e-9);
        g.PrintableY.Should().BeApproximately(0.2, 1e-9);
        g.PrintableWidth.Should().BeApproximately(8, 1e-9);
        g.PrintableHeight.Should().BeApproximately(10.6, 1e-9);
        g.DpiX.Should().Be(600);
        g.DpiY.Should().Be(1200);
    }

    [Fact]
    public void DeviceCaps_WithoutAPhysicalSize_AreTreatedAsCentredMargins()
    {
        var caps = new PrintDeviceCaps(300, 300, 0, 0, 30, 30, 2490, 3240);

        var g = caps.ToGeometry();

        g.PaperWidth.Should().BeApproximately((30 * 2 + 2490) / 300.0, 1e-9);
        g.PaperHeight.Should().BeApproximately((30 * 2 + 3240) / 300.0, 1e-9);
    }

    // ── Win32 layout ───────────────────────────────────────────────────

    /// <summary>
    /// PRINTDLGEXW is 136 bytes on 64-bit Windows, and PrintDlgExW rejects a
    /// wrong lStructSize with E_INVALIDARG. The offsets are the commdlg.h
    /// layout at 8-byte alignment; a reordered field would still size to 136
    /// on its own, so the load-bearing ones are pinned too. The layout depends
    /// only on the pointer size, so a 64-bit macOS process checks it.
    /// </summary>
    [Fact]
    public void PrintDlgEx_LayoutMatchesTheWindowsHeader_On64Bit()
    {
        Assert.SkipUnless(Environment.Is64BitProcess, "the structure is declared for 64-bit Windows only");

        Marshal.SizeOf<PrintDlgExInterop.PRINTDLGEXW>().Should().Be(136);
        Offset("hwndOwner").Should().Be(8);
        Offset("hDevMode").Should().Be(16);
        Offset("hDevNames").Should().Be(24);
        Offset("Flags").Should().Be(40);
        Offset("ExclusionFlags").Should().Be(48);
        Offset("nPageRanges").Should().Be(52);
        Offset("nMaxPageRanges").Should().Be(56);
        Offset("lpPageRanges").Should().Be(64);
        Offset("nMinPage").Should().Be(72);
        Offset("nMaxPage").Should().Be(76);
        Offset("nCopies").Should().Be(80);
        Offset("hInstance").Should().Be(88);
        Offset("lpCallback").Should().Be(104);
        Offset("nPropertyPages").Should().Be(112);
        Offset("lphPropertyPages").Should().Be(120);
        Offset("nStartPage").Should().Be(128);
        Offset("dwResultAction").Should().Be(132);
        Marshal.SizeOf<PrintDlgExInterop.PRINTPAGERANGE>().Should().Be(8);

        static long Offset(string field) => Marshal.OffsetOf<PrintDlgExInterop.PRINTDLGEXW>(field).ToInt64();
    }

    [Fact]
    public void PrintDlgEx_Flags_HideWhatExciseCannotHonour()
    {
        var flags = PrintDlgExInterop.DialogFlags;

        (flags & PrintDlgExInterop.PD_NOSELECTION).Should().NotBe(0u);
        (flags & PrintDlgExInterop.PD_NOCURRENTPAGE).Should().NotBe(0u);
        (flags & PrintDlgExInterop.PD_HIDEPRINTTOFILE).Should().NotBe(0u);
        (flags & PrintDlgExInterop.PD_USEDEVMODECOPIESANDCOLLATE).Should().NotBe(0u,
            "copies go to the driver when it can make them; PrintPageSequence makes the rest");
        (flags & PrintDlgExInterop.PD_PAGENUMS).Should().Be(0u, "the dialog opens on All pages");
        PrintDlgExInterop.PD_EXCL_COPIESANDCOLLATE.Should().Be(0x8100u);
    }

    [Fact]
    public void PrintDlgEx_FailuresReadAsSentences()
    {
        PrintDlgExInterop.DescribeFailure(unchecked((int)0x80004005), 0x1008).Should().StartWith("No printer is installed");
        PrintDlgExInterop.DescribeFailure(unchecked((int)0x80070057), 0).Should().Contain("0x80070057");
        PrintDlgExInterop.DescribeFailure(unchecked((int)0x8007000E), 0).Should().Contain("memory");
    }

    // ── pixels ─────────────────────────────────────────────────────────

    [Fact]
    public void SwapRedBlue_TurnsRgbaIntoBgra()
    {
        byte[] pixels = [1, 2, 3, 4, 10, 20, 30, 40];

        PrintSheetRaster.SwapRedBlue(pixels);

        pixels.Should().Equal(3, 2, 1, 4, 30, 20, 10, 40);
    }

    private static void AssertInsidePrintable(PrintPlacement p, PrintDeviceGeometry d)
    {
        const double eps = 1e-9;
        p.X.Should().BeGreaterThanOrEqualTo(d.PrintableX - eps);
        p.Y.Should().BeGreaterThanOrEqualTo(d.PrintableY - eps);
        (p.X + p.Width).Should().BeLessThanOrEqualTo(d.PrintableX + d.PrintableWidth + eps);
        (p.Y + p.Height).Should().BeLessThanOrEqualTo(d.PrintableY + d.PrintableHeight + eps);
    }
}
