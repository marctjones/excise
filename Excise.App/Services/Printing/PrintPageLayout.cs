using System;
using System.Collections.Generic;

namespace Excise.App.Services.Printing;

/// <summary>
/// The printable geometry of one sheet, in INCHES, measured from the top-left
/// corner of the physical paper (#1546). The Windows printer reads it from the
/// page's device context (<c>GetDeviceCaps</c>), so it is already in the
/// orientation the sheet is printed in.
/// </summary>
/// <param name="PaperWidth">Physical paper width.</param>
/// <param name="PaperHeight">Physical paper height.</param>
/// <param name="PrintableX">Left edge of the printable area (the hard margin).</param>
/// <param name="PrintableY">Top edge of the printable area (the hard margin).</param>
/// <param name="PrintableWidth">Width of the printable area.</param>
/// <param name="PrintableHeight">Height of the printable area.</param>
/// <param name="DpiX">Device resolution across.</param>
/// <param name="DpiY">Device resolution down.</param>
internal readonly record struct PrintDeviceGeometry(
    double PaperWidth,
    double PaperHeight,
    double PrintableX,
    double PrintableY,
    double PrintableWidth,
    double PrintableHeight,
    double DpiX,
    double DpiY);

/// <summary>
/// Where one PDF page lands on a sheet, in inches from the paper's top-left
/// corner, and the resolution to rasterise it at.
/// </summary>
/// <param name="X">Left edge of the page image on the paper.</param>
/// <param name="Y">Top edge of the page image on the paper.</param>
/// <param name="Width">Printed width of the page image.</param>
/// <param name="Height">Printed height of the page image.</param>
/// <param name="Scale">Printed size over the page's own size (1 = actual size).</param>
/// <param name="RenderDpi">The DPI to pass to the renderer for this page.</param>
internal readonly record struct PrintPlacement(
    double X,
    double Y,
    double Width,
    double Height,
    double Scale,
    int RenderDpi);

/// <summary>
/// Pure page-on-paper geometry for the raster print path (#1546). No
/// platform types, so all of it is tested on any OS.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scaling</b> follows PDFKit's modes, which the macOS path hands to the
/// system (#1545): <see cref="PrintScalingMode.ActualSize"/> prints at 100%,
/// <see cref="PrintScalingMode.FitToPage"/> scales up or down to the
/// printable area, and <see cref="PrintScalingMode.ShrinkOversized"/> scales
/// down only a page that does not fit the printable area.
/// </para>
/// <para>
/// <b>Position.</b> The image is centred on the PAPER, then moved inside the
/// printable area along any axis where it fits there. A page that fits is
/// therefore never cut by an asymmetric hard margin, and an actual-size page
/// larger than the paper loses the same amount on both sides, which is what a
/// centred physical copy would.
/// </para>
/// <para>
/// <b>Resolution.</b> The page is rasterised at the device resolution, capped
/// at <see cref="MaxDeviceDpi"/>, and the whole page bitmap is capped at
/// <see cref="MaxPagePixels"/>. A 600 DPI Letter page is 33.7 million pixels
/// (135 MB of BGRA); without the caps a 1200 DPI A3 page would be 280 million,
/// above the renderer's own 256 million limit.
/// </para>
/// </remarks>
internal static class PrintPageLayout
{
    /// <summary>Highest resolution a page is rasterised at for printing.</summary>
    internal const int MaxDeviceDpi = 600;

    /// <summary>Largest page bitmap the print path allocates.</summary>
    internal const long MaxPagePixels = 48_000_000;

    private const double PointsPerInch = 72.0;
    private const double Tolerance = 1e-6;

    /// <summary>
    /// The paper orientation that fits the page best, as the value for
    /// <c>PageSettings.Landscape</c>: <c>true</c> when the page and the paper
    /// (in the driver's portrait orientation) have opposite shapes. Null when
    /// either is square or degenerate, so the user's own choice stands. This is
    /// the equivalent of PDFKit's <c>autoRotate:YES</c>.
    /// </summary>
    internal static bool? ChooseLandscape(
        double pageWidthPt,
        double pageHeightPt,
        double portraitPaperWidth,
        double portraitPaperHeight)
    {
        if (!IsPositive(pageWidthPt) || !IsPositive(pageHeightPt) ||
            !IsPositive(portraitPaperWidth) || !IsPositive(portraitPaperHeight))
            return null;
        if (Math.Abs(pageWidthPt - pageHeightPt) < Tolerance ||
            Math.Abs(portraitPaperWidth - portraitPaperHeight) < Tolerance)
            return null;

        bool pageIsWide = pageWidthPt > pageHeightPt;
        bool paperIsWide = portraitPaperWidth > portraitPaperHeight;
        return pageIsWide != paperIsWide;
    }

    /// <summary>
    /// Place a page of <paramref name="pageWidthPt"/> x
    /// <paramref name="pageHeightPt"/> points (the page as displayed, after
    /// <c>/Rotate</c>) on <paramref name="device"/>.
    /// </summary>
    internal static PrintPlacement Place(
        double pageWidthPt,
        double pageHeightPt,
        PrintDeviceGeometry device,
        PrintScalingMode scaling,
        int maxDeviceDpi = MaxDeviceDpi,
        long maxPagePixels = MaxPagePixels)
    {
        if (!IsPositive(pageWidthPt) || !IsPositive(pageHeightPt))
            throw new ArgumentOutOfRangeException(nameof(pageWidthPt), "The page has no printable size.");
        if (!IsPositive(device.PaperWidth) || !IsPositive(device.PaperHeight) ||
            !IsPositive(device.PrintableWidth) || !IsPositive(device.PrintableHeight) ||
            !IsPositive(device.DpiX) || !IsPositive(device.DpiY))
            throw new ArgumentOutOfRangeException(nameof(device), "The printer reported no printable area.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDeviceDpi);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPagePixels);

        double pageWidth = pageWidthPt / PointsPerInch;
        double pageHeight = pageHeightPt / PointsPerInch;
        double fit = Math.Min(device.PrintableWidth / pageWidth, device.PrintableHeight / pageHeight);
        double scale = scaling switch
        {
            PrintScalingMode.ActualSize => 1.0,
            PrintScalingMode.FitToPage => fit,
            _ => Math.Min(1.0, fit),
        };

        double width = pageWidth * scale;
        double height = pageHeight * scale;
        double x = Position(device.PaperWidth, device.PrintableX, device.PrintableWidth, width);
        double y = Position(device.PaperHeight, device.PrintableY, device.PrintableHeight, height);

        return new PrintPlacement(x, y, width, height, scale,
            RenderDpi(pageWidth, pageHeight, scale, device, maxDeviceDpi, maxPagePixels));
    }

    private static double Position(double paper, double printableStart, double printableSize, double size)
    {
        double centred = (paper - size) / 2.0;
        if (size > printableSize + Tolerance)
            return centred;
        double printableEnd = printableStart + printableSize;
        return Math.Clamp(centred, printableStart, Math.Max(printableStart, printableEnd - size));
    }

    private static int RenderDpi(
        double pageWidth,
        double pageHeight,
        double scale,
        PrintDeviceGeometry device,
        int maxDeviceDpi,
        long maxPagePixels)
    {
        // Rasterising at renderDpi and printing at `scale` puts renderDpi/scale
        // source pixels on each printed inch, so renderDpi = deviceDpi * scale
        // matches the device one to one.
        double deviceDpi = Math.Min(Math.Min(device.DpiX, device.DpiY), maxDeviceDpi);
        double dpi = deviceDpi * scale;

        double pixels = pageWidth * dpi * pageHeight * dpi;
        if (pixels > maxPagePixels)
            dpi *= Math.Sqrt(maxPagePixels / pixels);

        // Floor, so the rounded-up bitmap the renderer allocates stays within
        // the budget; never below one dot per inch.
        return Math.Max(1, (int)Math.Floor(dpi));
    }

    private static bool IsPositive(double value) => value > 0 && double.IsFinite(value);
}

/// <summary>An inclusive, 1-based page range the user typed in the print dialog.</summary>
internal readonly record struct PrintPageRange(int From, int To);

/// <summary>
/// The order sheets come out of the printer (#1546): the chosen ranges, then
/// the copies the application has to produce itself.
/// </summary>
internal static class PrintPageSequence
{
    /// <summary>Upper bound on copies, matching the Windows print dialog's own limit.</summary>
    internal const int MaxCopies = 9999;

    /// <summary>
    /// Zero-based page indices, one per sheet. An empty
    /// <paramref name="ranges"/> means every page. Ranges are clamped to the
    /// document, a reversed range is read low to high, and a page named twice
    /// prints once, at its first position. <paramref name="copies"/> is the
    /// number of copies the application must produce (1 when the driver
    /// makes them); <paramref name="collate"/> chooses 1,2,3,1,2,3 over
    /// 1,1,2,2,3,3.
    /// </summary>
    internal static IReadOnlyList<int> Build(
        int pageCount,
        IReadOnlyList<PrintPageRange> ranges,
        int copies,
        bool collate)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageCount);
        ArgumentNullException.ThrowIfNull(ranges);
        if (pageCount == 0)
            return Array.Empty<int>();

        var pages = new List<int>();
        var seen = new HashSet<int>();
        if (ranges.Count == 0)
        {
            for (int i = 0; i < pageCount; i++)
                pages.Add(i);
        }
        else
        {
            foreach (var range in ranges)
            {
                int from = Math.Max(1, Math.Min(range.From, range.To));
                int to = Math.Min(pageCount, Math.Max(range.From, range.To));
                for (int page = from; page <= to; page++)
                {
                    if (seen.Add(page))
                        pages.Add(page - 1);
                }
            }
        }

        copies = Math.Clamp(copies, 1, MaxCopies);
        if (copies == 1 || pages.Count == 0)
            return pages;

        var sheets = new List<int>(pages.Count * copies);
        if (collate)
        {
            for (int copy = 0; copy < copies; copy++)
                sheets.AddRange(pages);
        }
        else
        {
            foreach (var page in pages)
            {
                for (int copy = 0; copy < copies; copy++)
                    sheets.Add(page);
            }
        }
        return sheets;
    }
}
