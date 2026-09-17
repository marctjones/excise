using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Excise.App.Services.Printing;

/// <summary>
/// The Windows common print dialog, <c>PrintDlgExW</c> (#1546), owned by the
/// excise window. Chosen over WinForms' <c>PrintDialog</c> because that is a
/// wrapper around this same call and would pull the whole WinForms framework
/// (and its message loop) into an Avalonia app; and over the WinRT
/// <c>PrintManager</c> because that needs a <c>net10.0-windows10.*</c> target
/// and CsWinRT projections for a desktop HWND, which would split the build.
/// </summary>
/// <remarks>
/// The chosen printer is returned as a <see cref="PrinterSettings"/> built
/// from the dialog's DEVMODE and DEVNAMES, which is exactly what WinForms'
/// <c>PrintDialog</c> does. The last choice is remembered for the next
/// dialog in this session.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class Win32PrintDialog : IWindowsPrintDialog
{
    /// <summary>Page ranges the dialog may return ("1-3, 5, 8-9").</summary>
    internal const int MaxPageRanges = 64;

    private readonly ILogger _logger;
    private PrinterSettings? _lastSettings;

    public Win32PrintDialog(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public unsafe WindowsPrintTicket Show(nint ownerHwnd, int pageCount)
    {
        if (ownerHwnd == 0)
            return WindowsPrintTicket.Fail(WindowsDocumentPrinter.NeedsWindowMessage);
        if (pageCount < 1)
            return WindowsPrintTicket.Fail("The document has no pages to print.");

        var ranges = stackalloc PrintDlgExInterop.PRINTPAGERANGE[MaxPageRanges];
        ranges[0] = new PrintDlgExInterop.PRINTPAGERANGE { nFromPage = 1, nToPage = (uint)pageCount };

        var dialog = new PrintDlgExInterop.PRINTDLGEXW
        {
            lStructSize = (uint)sizeof(PrintDlgExInterop.PRINTDLGEXW),
            hwndOwner = ownerHwnd,
            // The previous choice, as new HGLOBALs the dialog may replace.
            hDevMode = _lastSettings?.GetHdevmode() ?? 0,
            hDevNames = _lastSettings?.GetHdevnames() ?? 0,
            Flags = PrintDlgExInterop.DialogFlags,
            ExclusionFlags = PrintDlgExInterop.PD_EXCL_COPIESANDCOLLATE,
            nPageRanges = 1,
            nMaxPageRanges = MaxPageRanges,
            lpPageRanges = (nint)ranges,
            nMinPage = 1,
            nMaxPage = (uint)pageCount,
            nCopies = 1,
            nStartPage = PrintDlgExInterop.START_PAGE_GENERAL,
        };

        try
        {
            int hr = PrintDlgExInterop.PrintDlgExW(&dialog);
            if (hr != 0)
            {
                uint detail = PrintDlgExInterop.CommDlgExtendedError();
                _logger.LogWarning("PrintDlgExW failed: HRESULT 0x{Hr:X8}, CommDlgExtendedError 0x{Detail:X}", hr, detail);
                return WindowsPrintTicket.Fail(PrintDlgExInterop.DescribeFailure(hr, detail));
            }

            if (dialog.dwResultAction != PrintDlgExInterop.PD_RESULT_PRINT)
                return WindowsPrintTicket.Cancelled;
            if (dialog.hDevMode == 0 || dialog.hDevNames == 0)
                return WindowsPrintTicket.Fail("The print dialog returned no printer.");

            var settings = new PrinterSettings();
            settings.SetHdevmode(dialog.hDevMode);
            settings.SetHdevnames(dialog.hDevNames);
            if (!settings.IsValid)
                return WindowsPrintTicket.Fail($"The printer \"{settings.PrinterName}\" is not available.");
            _lastSettings = settings;

            var chosen = new List<PrintPageRange>();
            if ((dialog.Flags & PrintDlgExInterop.PD_PAGENUMS) != 0)
            {
                uint count = Math.Min(dialog.nPageRanges, (uint)MaxPageRanges);
                for (int i = 0; i < count; i++)
                    chosen.Add(new PrintPageRange((int)ranges[i].nFromPage, (int)ranges[i].nToPage));
            }

            return new WindowsPrintTicket(
                WindowsPrintDialogOutcome.Print,
                settings.PrinterName,
                chosen,
                // With PD_USEDEVMODECOPIESANDCOLLATE this is 1 unless the
                // driver cannot make copies, in which case excise must.
                (int)Math.Clamp(dialog.nCopies, 1u, (uint)PrintPageSequence.MaxCopies),
                (dialog.Flags & PrintDlgExInterop.PD_COLLATE) != 0,
                settings);
        }
        finally
        {
            // Whatever the dialog handed back is ours to free; it has already
            // freed any input handle it replaced.
            if (dialog.hDevMode != 0)
                PrintDlgExInterop.GlobalFree(dialog.hDevMode);
            if (dialog.hDevNames != 0)
                PrintDlgExInterop.GlobalFree(dialog.hDevNames);
        }
    }
}

/// <summary>
/// Sends rasterised sheets to a printer through .NET's
/// <see cref="PrintDocument"/> (#1546): it creates the printer DC from the
/// dialog's DEVMODE, runs StartDoc/StartPage/EndPage/EndDoc, and AbortDoc on
/// cancellation. Each sheet's orientation is set in
/// <see cref="PrintDocument.QueryPageSettings"/>, and its image is drawn in
/// device pixels at the offset the page's own DC reports.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class GdiPrintSpooler : IWindowsPrintSpooler
{
    private readonly ILogger _logger;

    public GdiPrintSpooler(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public DocumentPrintResult Spool(
        WindowsPrintTicket ticket,
        PrintSheetSource sheets,
        string jobTitle,
        CancellationToken cancellationToken)
    {
        if (ticket.NativeSettings is not PrinterSettings settings)
            return DocumentPrintResult.Fail("The print dialog returned no printer settings.");

        using var document = new PrintDocument
        {
            DocumentName = string.IsNullOrWhiteSpace(jobTitle) ? "excise document" : jobTitle,
            PrinterSettings = settings,
            PrintController = new StandardPrintController(),
            OriginAtMargins = false,
        };

        // The document's page settings come from the same DEVMODE: paper,
        // source, and the orientation the user chose (kept for square pages).
        nint devMode = settings.GetHdevmode();
        try
        {
            document.DefaultPageSettings.SetHdevmode(devMode);
        }
        finally
        {
            PrintDlgExInterop.GlobalFree(devMode);
        }
        bool userLandscape = document.DefaultPageSettings.Landscape;

        int next = 0;
        bool cancelled = false;
        Exception? failure = null;

        document.QueryPageSettings += (_, e) =>
        {
            try
            {
                if (next >= sheets.SheetCount)
                    return;
                // PaperSize is in the driver's portrait orientation.
                var paper = e.PageSettings.PaperSize;
                e.PageSettings.Landscape =
                    sheets.ChooseLandscape(next, paper.Width, paper.Height) ?? userLandscape;
            }
            catch (Exception ex)
            {
                failure = ex;
                e.Cancel = true;
            }
        };

        document.PrintPage += (_, e) =>
        {
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                    e.Cancel = true;
                    return;
                }

                DrawSheet(e.Graphics ?? throw new InvalidOperationException("The printer gave no drawing surface."),
                    sheets, next, cancellationToken);
                next++;
                e.HasMorePages = next < sheets.SheetCount;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                e.Cancel = true;
            }
            catch (Exception ex)
            {
                // Cancelling the event makes PrintDocument abort the job
                // instead of sending a partial document.
                failure = ex;
                e.Cancel = true;
            }
        };

        try
        {
            document.Print();
        }
        catch (Exception ex) when (ex is InvalidPrinterException or Win32Exception or ExternalException)
        {
            _logger.LogError(ex, "The printer rejected the job");
            return DocumentPrintResult.Fail($"The printer rejected the job: {ex.Message}");
        }

        if (failure != null)
        {
            _logger.LogError(failure, "Printing failed on sheet {Sheet}", next + 1);
            return DocumentPrintResult.Fail($"Printing failed on sheet {next + 1}: {failure.Message}");
        }
        if (cancelled)
        {
            _logger.LogInformation("Print job aborted after {Sheets} sheet(s)", next);
            return DocumentPrintResult.Cancelled;
        }

        _logger.LogInformation("Spooled {Sheets} sheet(s) to {Printer}", next, settings.PrinterName);
        return DocumentPrintResult.Printed;
    }

    private static void DrawSheet(Graphics graphics, PrintSheetSource sheets, int sheet, CancellationToken cancellationToken)
    {
        var caps = ReadDeviceCaps(graphics);
        var device = caps.ToGeometry();
        using var raster = sheets.Render(sheet, device, cancellationToken);
        var placement = raster.Placement;

        nint pixels = raster.GetBgraPixels(out int width, out int height, out int stride);
        // Wraps the page buffer; no copy. PArgb matches the renderer's
        // premultiplied, opaque output.
        using var image = new Bitmap(width, height, stride, PixelFormat.Format32bppPArgb, pixels);
        image.SetResolution(placement.RenderDpi, placement.RenderDpi);

        // Device pixels, with the origin at the printable area's corner:
        // subtract the hard margin so paper coordinates land where they say.
        // Interpolation is left at GDI+'s default: the image is already at
        // (or, above the DPI cap, an integer fraction of) device resolution,
        // and a forced high-quality resample would make GDI+ band the page
        // itself instead of handing it to the driver.
        graphics.PageUnit = GraphicsUnit.Pixel;
        var destination = new RectangleF(
            (float)(placement.X * caps.DpiX - caps.OffsetX),
            (float)(placement.Y * caps.DpiY - caps.OffsetY),
            (float)(placement.Width * caps.DpiX),
            (float)(placement.Height * caps.DpiY));
        graphics.DrawImage(image, destination, new RectangleF(0, 0, width, height), GraphicsUnit.Pixel);
    }

    /// <summary>
    /// The page DC's geometry. Read from the DC rather than
    /// <c>PageSettings.PrintableArea</c>, because the DC is already in the
    /// orientation this sheet prints in.
    /// </summary>
    private static PrintDeviceCaps ReadDeviceCaps(Graphics graphics)
    {
        nint hdc = graphics.GetHdc();
        try
        {
            return PrintDeviceCaps.FromDeviceContext(hdc);
        }
        finally
        {
            graphics.ReleaseHdc(hdc);
        }
    }
}

/// <summary>
/// A printer page DC's geometry in device pixels, and its conversion to the
/// inch-based <see cref="PrintDeviceGeometry"/>. Pure apart from
/// <see cref="FromDeviceContext"/>; internal for tests.
/// </summary>
internal readonly record struct PrintDeviceCaps(
    int DpiX,
    int DpiY,
    int PhysicalWidth,
    int PhysicalHeight,
    int OffsetX,
    int OffsetY,
    int PrintableWidth,
    int PrintableHeight)
{
    [SupportedOSPlatform("windows")]
    internal static PrintDeviceCaps FromDeviceContext(nint hdc)
    {
        var caps = new PrintDeviceCaps(
            PrintDlgExInterop.GetDeviceCaps(hdc, PrintDlgExInterop.LOGPIXELSX),
            PrintDlgExInterop.GetDeviceCaps(hdc, PrintDlgExInterop.LOGPIXELSY),
            PrintDlgExInterop.GetDeviceCaps(hdc, PrintDlgExInterop.PHYSICALWIDTH),
            PrintDlgExInterop.GetDeviceCaps(hdc, PrintDlgExInterop.PHYSICALHEIGHT),
            PrintDlgExInterop.GetDeviceCaps(hdc, PrintDlgExInterop.PHYSICALOFFSETX),
            PrintDlgExInterop.GetDeviceCaps(hdc, PrintDlgExInterop.PHYSICALOFFSETY),
            PrintDlgExInterop.GetDeviceCaps(hdc, PrintDlgExInterop.HORZRES),
            PrintDlgExInterop.GetDeviceCaps(hdc, PrintDlgExInterop.VERTRES));
        if (caps.DpiX <= 0 || caps.DpiY <= 0 || caps.PrintableWidth <= 0 || caps.PrintableHeight <= 0)
            throw new InvalidOperationException("The printer reported no printable area.");
        return caps;
    }

    /// <summary>
    /// Inches from the paper's corner. A driver that reports no physical page
    /// size (some virtual printers) is treated as borderless.
    /// </summary>
    internal PrintDeviceGeometry ToGeometry()
    {
        double paperWidth = PhysicalWidth > 0 ? PhysicalWidth : OffsetX * 2 + PrintableWidth;
        double paperHeight = PhysicalHeight > 0 ? PhysicalHeight : OffsetY * 2 + PrintableHeight;
        return new PrintDeviceGeometry(
            paperWidth / DpiX,
            paperHeight / DpiY,
            (double)OffsetX / DpiX,
            (double)OffsetY / DpiY,
            (double)PrintableWidth / DpiX,
            (double)PrintableHeight / DpiY,
            DpiX,
            DpiY);
    }
}

/// <summary>
/// comdlg32/gdi32/kernel32 declarations for the Windows print path. The
/// structures are laid out for 64-bit Windows (commdlg.h packs them to 1 byte
/// only on 32-bit x86); <see cref="WindowsDocumentPrinter.CreateNative"/>
/// refuses a 32-bit process. Blittable, so the source-generated stubs need no
/// marshalling and stay AOT-clean. Internal for the layout tests.
/// </summary>
internal static partial class PrintDlgExInterop
{
    // PRINTDLGEX Flags (commdlg.h).
    internal const uint PD_ALLPAGES = 0x00000000;
    internal const uint PD_PAGENUMS = 0x00000002;
    internal const uint PD_NOSELECTION = 0x00000004;
    internal const uint PD_COLLATE = 0x00000010;
    internal const uint PD_USEDEVMODECOPIESANDCOLLATE = 0x00040000;
    internal const uint PD_HIDEPRINTTOFILE = 0x00100000;
    internal const uint PD_NOCURRENTPAGE = 0x00800000;

    /// <summary>
    /// All pages by default; page ranges allowed; no "Selection" or "Current
    /// page" (excise does not pass either); copies and collate in the DEVMODE
    /// when the driver supports them; no "Print to file" (use Microsoft Print
    /// to PDF, or Save As).
    /// </summary>
    internal const uint DialogFlags =
        PD_ALLPAGES | PD_NOSELECTION | PD_NOCURRENTPAGE | PD_USEDEVMODECOPIESANDCOLLATE | PD_HIDEPRINTTOFILE;

    /// <summary>DM_COPIES | DM_COLLATE: the dialog's own controls replace the driver page's.</summary>
    internal const uint PD_EXCL_COPIESANDCOLLATE = 0x00000100 | 0x00008000;

    internal const uint START_PAGE_GENERAL = 0xFFFFFFFF;
    internal const uint PD_RESULT_CANCEL = 0;
    internal const uint PD_RESULT_PRINT = 1;
    internal const uint PD_RESULT_APPLY = 2;

    // GetDeviceCaps indices (wingdi.h).
    internal const int HORZRES = 8;
    internal const int VERTRES = 10;
    internal const int LOGPIXELSX = 88;
    internal const int LOGPIXELSY = 90;
    internal const int PHYSICALWIDTH = 110;
    internal const int PHYSICALHEIGHT = 111;
    internal const int PHYSICALOFFSETX = 112;
    internal const int PHYSICALOFFSETY = 113;

    private const int E_OUTOFMEMORY = unchecked((int)0x8007000E);
    private const int E_INVALIDARG = unchecked((int)0x80070057);
    private const int E_HANDLE = unchecked((int)0x80070006);
    private const uint PDERR_NODEFAULTPRN = 0x1008;
    private const uint PDERR_NODEVICES = 0x1007;
    private const uint PDERR_PRINTERNOTFOUND = 0x100B;

    [StructLayout(LayoutKind.Sequential)]
    internal struct PRINTPAGERANGE
    {
        public uint nFromPage;
        public uint nToPage;
    }

    /// <summary>PRINTDLGEXW (commdlg.h). 136 bytes on 64-bit Windows.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PRINTDLGEXW
    {
        public uint lStructSize;
        public nint hwndOwner;
        public nint hDevMode;
        public nint hDevNames;
        public nint hDC;
        public uint Flags;
        public uint Flags2;
        public uint ExclusionFlags;
        public uint nPageRanges;
        public uint nMaxPageRanges;
        public nint lpPageRanges;
        public uint nMinPage;
        public uint nMaxPage;
        public uint nCopies;
        public nint hInstance;
        public nint lpPrintTemplateName;
        public nint lpCallback;
        public uint nPropertyPages;
        public nint lphPropertyPages;
        public uint nStartPage;
        public uint dwResultAction;
    }

    [SupportedOSPlatform("windows")]
    [LibraryImport("comdlg32.dll")]
    internal static unsafe partial int PrintDlgExW(PRINTDLGEXW* lppd);

    [SupportedOSPlatform("windows")]
    [LibraryImport("comdlg32.dll")]
    internal static partial uint CommDlgExtendedError();

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll")]
    internal static partial nint GlobalFree(nint hMem);

    [SupportedOSPlatform("windows")]
    [LibraryImport("gdi32.dll")]
    internal static partial int GetDeviceCaps(nint hdc, int index);

    /// <summary>A user-facing message for a failed <c>PrintDlgExW</c>. Pure; internal for tests.</summary>
    internal static string DescribeFailure(int hresult, uint extendedError) => extendedError switch
    {
        PDERR_NODEFAULTPRN or PDERR_NODEVICES =>
            "No printer is installed. Add one in Windows Settings > Bluetooth & devices > Printers & scanners.",
        PDERR_PRINTERNOTFOUND => "The selected printer could not be found.",
        _ => hresult switch
        {
            E_OUTOFMEMORY => "The print dialog ran out of memory.",
            E_INVALIDARG or E_HANDLE => $"The print dialog could not be shown (0x{hresult:X8}).",
            _ => $"The print dialog failed (0x{hresult:X8}, 0x{extendedError:X}).",
        },
    };
}
