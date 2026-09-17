using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Excise.Core.Document;
using Excise.Rendering;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Excise.App.Services.Printing;

/// <summary>What the user chose in the Windows print dialog.</summary>
internal enum WindowsPrintDialogOutcome
{
    Print,
    Cancelled,
    Failed,
}

/// <summary>
/// The Windows print dialog's answer (#1546). <paramref name="Ranges"/> is
/// empty for "All pages". <paramref name="ApplicationCopies"/> is the number
/// of copies excise must produce itself: with
/// <c>PD_USEDEVMODECOPIESANDCOLLATE</c> a driver that can make copies gets
/// them in its DEVMODE and this is 1. <paramref name="NativeSettings"/> is the
/// chosen printer and its DEVMODE, opaque outside the Windows-only code (a
/// <c>System.Drawing.Printing.PrinterSettings</c> there).
/// </summary>
internal sealed record WindowsPrintTicket(
    WindowsPrintDialogOutcome Outcome,
    string PrinterName,
    IReadOnlyList<PrintPageRange> Ranges,
    int ApplicationCopies,
    bool Collate,
    object? NativeSettings,
    string? Error = null)
{
    internal static WindowsPrintTicket Cancelled { get; } =
        new(WindowsPrintDialogOutcome.Cancelled, string.Empty, Array.Empty<PrintPageRange>(), 1, false, null);

    internal static WindowsPrintTicket Fail(string error) =>
        new(WindowsPrintDialogOutcome.Failed, string.Empty, Array.Empty<PrintPageRange>(), 1, false, null, error);
}

/// <summary>
/// Shows the system print dialog. Called on the UI thread; modal to
/// <paramref name="ownerHwnd"/>.
/// </summary>
internal interface IWindowsPrintDialog
{
    WindowsPrintTicket Show(nint ownerHwnd, int pageCount);
}

/// <summary>
/// Sends the sheets of <paramref name="sheets"/> to the printer in
/// <paramref name="ticket"/>. Runs on a worker thread and blocks until the
/// job is spooled, aborted or failed. Must observe
/// <paramref name="cancellationToken"/> between sheets and abort the job
/// (not end it) when it fires.
/// </summary>
internal interface IWindowsPrintSpooler
{
    DocumentPrintResult Spool(
        WindowsPrintTicket ticket,
        PrintSheetSource sheets,
        string jobTitle,
        CancellationToken cancellationToken);
}

/// <summary>
/// Prints on Windows (#1546): the system print dialog, then every chosen page
/// rasterised by excise's own renderer and sent through .NET's
/// <c>System.Drawing.Printing.PrintDocument</c>.
/// </summary>
/// <remarks>
/// <para>
/// This class holds the platform-neutral half, so it is tested on any OS with
/// a fake dialog and spooler. The Win32 half — <c>PrintDlgExW</c> and the GDI
/// job — is <see cref="Win32PrintDialog"/> and <see cref="GdiPrintSpooler"/>,
/// the only code that touches <c>System.Drawing.Common</c>; they are created
/// only by <see cref="CreateNative"/>, which runs only on Windows, so the
/// assembly is never loaded elsewhere.
/// </para>
/// <para>
/// Unlike the macOS path, the pages printed are excise's rendering: what the
/// viewer shows (annotations and filled form fields included, never the
/// form-field highlight tint) is what prints. The PDF's per-annotation
/// <c>/Print</c> flag is not consulted yet: the renderer has no print intent.
/// </para>
/// <para>
/// Threading: the copy is parsed on the pool, the dialog runs on the UI
/// thread (it needs the owner window and an STA; <c>Program.Main</c> is
/// <c>[STAThread]</c>), and rasterising and spooling run on the pool, so the
/// window stays responsive while a long job is sent. The cancellation token
/// on the request is checked between sheets and inside each render.
/// </para>
/// </remarks>
internal sealed class WindowsDocumentPrinter : IDocumentPrinter
{
    internal const string NeedsWindowMessage = "The print dialog needs the excise window, and none was found.";
    internal const string NoPagesMessage = "The chosen page range contains no pages of this document.";

    private readonly IWindowsPrintDialog _dialog;
    private readonly IWindowsPrintSpooler _spooler;
    private readonly ILogger _logger;
    private readonly Func<Window?, nint> _ownerResolver;

    internal WindowsDocumentPrinter(
        IWindowsPrintDialog dialog,
        IWindowsPrintSpooler spooler,
        ILogger logger,
        bool isSupported = true,
        Func<Window?, nint>? ownerResolver = null)
    {
        _dialog = dialog ?? throw new ArgumentNullException(nameof(dialog));
        _spooler = spooler ?? throw new ArgumentNullException(nameof(spooler));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        IsSupported = isSupported;
        _ownerResolver = ownerResolver ?? ResolveHwnd;
    }

    /// <summary>
    /// The production printer. The Win32 structures are declared for 64-bit
    /// Windows only (excise ships win-x64 and win-arm64), so a 32-bit process
    /// reports printing as unsupported rather than pass a mis-laid struct.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static WindowsDocumentPrinter CreateNative(ILogger logger) =>
        new(new Win32PrintDialog(logger), new GdiPrintSpooler(logger), logger,
            isSupported: Environment.Is64BitProcess);

    public bool IsSupported { get; }

    public string UnsupportedReason => IsSupported
        ? UnsupportedDocumentPrinter.DefaultReason
        : "Printing needs the 64-bit build of excise on Windows.";

    public async Task<DocumentPrintResult> PrintAsync(DocumentPrintRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsSupported)
            return DocumentPrintResult.Fail(UnsupportedReason);

        var cancellationToken = request.CancellationToken;
        nint owner = _ownerResolver(request.Owner);
        if (owner == 0)
            return DocumentPrintResult.Fail(NeedsWindowMessage);

        PdfDocument? document = null;
        try
        {
            // Read the whole copy into memory: no handle stays open on the
            // file, so the workflow can delete it the moment this returns.
            document = await Task.Run(() => PdfDocument.Open(File.ReadAllBytes(request.PdfPath)), cancellationToken);

            if (document.PageCount < 1)
                return DocumentPrintResult.Fail(NoPagesMessage);

            // Back on the caller's (UI) thread for the modal dialog.
            var ticket = _dialog.Show(owner, document.PageCount);
            switch (ticket.Outcome)
            {
                case WindowsPrintDialogOutcome.Cancelled:
                    return DocumentPrintResult.Cancelled;
                case WindowsPrintDialogOutcome.Failed:
                    return DocumentPrintResult.Fail(ticket.Error ?? "The Windows print dialog could not be shown.");
            }

            var sheets = PrintPageSequence.Build(
                document.PageCount, ticket.Ranges, ticket.ApplicationCopies, ticket.Collate);
            if (sheets.Count == 0)
                return DocumentPrintResult.Fail(NoPagesMessage);

            _logger.LogInformation(
                "Sending {Sheets} sheet(s) to {Printer}: {Ranges} range(s), {Copies} application copies, collate={Collate}, scaling {Scaling}",
                sheets.Count, ticket.PrinterName, ticket.Ranges.Count, ticket.ApplicationCopies, ticket.Collate, request.Scaling);

            var source = new PrintSheetSource(document, sheets, request.Scaling);
            // The document is disposed in the finally below, after the spool
            // has finished using it.
            return await Task.Run(
                () => _spooler.Spool(ticket, source, request.JobTitle, cancellationToken),
                CancellationToken.None);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Windows print cancelled");
            return DocumentPrintResult.Cancelled;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Windows print failed");
            return DocumentPrintResult.Fail($"Printing failed: {ex.Message}");
        }
        finally
        {
            document?.Dispose();
        }
    }

    /// <summary>The HWND behind an Avalonia window, or 0.</summary>
    private static nint ResolveHwnd(Window? owner)
    {
        var handle = owner?.TryGetPlatformHandle();
        if (handle == null || handle.Handle == 0)
            return 0;
        return string.Equals(handle.HandleDescriptor, "HWND", StringComparison.OrdinalIgnoreCase)
            ? handle.Handle
            : 0;
    }
}

/// <summary>
/// One rasterised sheet: the page bitmap and where it goes on the paper. The
/// spooler owns and disposes it.
/// </summary>
internal sealed class PrintSheetRaster : IDisposable
{
    private bool _isBgra;

    internal PrintSheetRaster(int pageIndex, SKBitmap bitmap, PrintPlacement placement)
    {
        PageIndex = pageIndex;
        Bitmap = bitmap ?? throw new ArgumentNullException(nameof(bitmap));
        Placement = placement;
        if (bitmap.ColorType is not (SKColorType.Rgba8888 or SKColorType.Bgra8888))
            throw new ArgumentException($"Unexpected page bitmap format {bitmap.ColorType}.", nameof(bitmap));
        _isBgra = bitmap.ColorType == SKColorType.Bgra8888;
    }

    public int PageIndex { get; }

    /// <summary>
    /// The rendered page. After <see cref="GetBgraPixels"/> its bytes are
    /// BGRA whatever its <see cref="SKBitmap.ColorType"/> says; only the
    /// spooler reads it after that.
    /// </summary>
    public SKBitmap Bitmap { get; }

    public PrintPlacement Placement { get; }

    /// <summary>
    /// The page's pixels as 32-bit BGRA, premultiplied and opaque (the
    /// renderer fills the paper last), top row first, which is GDI's
    /// <c>PixelFormat.Format32bppPArgb</c> byte order. The renderer produces
    /// RGBA, so the swap is done in place: one page-sized buffer, not two.
    /// </summary>
    public unsafe nint GetBgraPixels(out int width, out int height, out int stride)
    {
        width = Bitmap.Width;
        height = Bitmap.Height;
        stride = Bitmap.RowBytes;
        nint pixels = Bitmap.GetPixels();
        if (pixels == 0)
            throw new InvalidOperationException("The page bitmap has no pixels.");
        if (!_isBgra)
        {
            SwapRedBlue(new Span<byte>((void*)pixels, checked(stride * height)));
            _isBgra = true;
        }
        return pixels;
    }

    /// <summary>Swap bytes 0 and 2 of every 4-byte pixel. Internal for tests.</summary>
    internal static void SwapRedBlue(Span<byte> pixels)
    {
        for (int i = 0; i + 3 < pixels.Length; i += 4)
            (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
    }

    public void Dispose() => Bitmap.Dispose();
}

/// <summary>
/// The platform-neutral producer of printed sheets (#1546): for sheet N, the
/// page it carries, the orientation it wants, and its raster placed for the
/// device geometry the spooler reports. Used from one worker thread.
/// </summary>
internal sealed class PrintSheetSource
{
    private readonly PdfDocument _document;
    private readonly IReadOnlyList<int> _sheets;
    private readonly PrintScalingMode _scaling;
    private readonly SkiaRenderer _renderer = new();

    internal PrintSheetSource(PdfDocument document, IReadOnlyList<int> sheets, PrintScalingMode scaling)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _sheets = sheets ?? throw new ArgumentNullException(nameof(sheets));
        _scaling = scaling;
    }

    public int SheetCount => _sheets.Count;

    public int PageIndexOf(int sheet) => _sheets[sheet];

    /// <summary>
    /// The <c>PageSettings.Landscape</c> value for <paramref name="sheet"/>
    /// given the paper's portrait size, or null to keep the dialog's choice.
    /// </summary>
    public bool? ChooseLandscape(int sheet, double portraitPaperWidth, double portraitPaperHeight)
    {
        var page = _document.GetPage(_sheets[sheet] + 1);
        return PrintPageLayout.ChooseLandscape(page.VisualWidth, page.VisualHeight, portraitPaperWidth, portraitPaperHeight);
    }

    /// <summary>Rasterise <paramref name="sheet"/> for <paramref name="device"/>.</summary>
    public PrintSheetRaster Render(int sheet, PrintDeviceGeometry device, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int pageIndex = _sheets[sheet];
        var page = _document.GetPage(pageIndex + 1);
        var placement = PrintPageLayout.Place(page.VisualWidth, page.VisualHeight, device, _scaling);

        var rendered = _renderer.RenderPage(
            page,
            new RenderOptions
            {
                Dpi = placement.RenderDpi,
                // What a reader sees: annotations and field values print; the
                // audit and highlight modes never do (RenderOptions remarks).
                RenderAnnotations = true,
                RevealHiddenAnnotations = false,
                HighlightFormFields = false,
                // Each sheet's page is rendered once, then dropped.
                ReleaseDecodedImageSamples = true,
            },
            cancellationToken);
        try
        {
            return new PrintSheetRaster(pageIndex, rendered, placement);
        }
        catch
        {
            rendered.Dispose();
            throw;
        }
    }
}
