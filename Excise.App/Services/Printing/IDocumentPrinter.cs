using System.Threading.Tasks;
using Avalonia.Controls;

namespace Excise.App.Services.Printing;

/// <summary>
/// How the platform print path scales each page onto the paper (#1545). The
/// values match PDFKit's <c>PDFPrintScalingMode</c> (PDFDocument.h), so the
/// macOS printer passes them through unchanged.
/// </summary>
public enum PrintScalingMode
{
    /// <summary>Print every page at 100%. <c>kPDFPrintPageScaleNone</c>.</summary>
    ActualSize = 0,

    /// <summary>Scale every page up or down to fit the paper. <c>kPDFPrintPageScaleToFit</c>.</summary>
    FitToPage = 1,

    /// <summary>Shrink only pages larger than the paper. <c>kPDFPrintPageScaleDownToFit</c>. The default.</summary>
    ShrinkOversized = 2,
}

/// <summary>What happened to one print request.</summary>
internal enum DocumentPrintOutcome
{
    /// <summary>The user confirmed the print sheet and the job was handed to the OS.</summary>
    Printed,

    /// <summary>The user dismissed the print sheet.</summary>
    Cancelled,

    /// <summary>The platform could not print. <see cref="DocumentPrintResult.Error"/> says why.</summary>
    Failed,
}

/// <summary>The result of <see cref="IDocumentPrinter.PrintAsync"/>.</summary>
internal readonly record struct DocumentPrintResult(DocumentPrintOutcome Outcome, string? Error = null)
{
    internal static DocumentPrintResult Printed => new(DocumentPrintOutcome.Printed);
    internal static DocumentPrintResult Cancelled => new(DocumentPrintOutcome.Cancelled);
    internal static DocumentPrintResult Fail(string error) => new(DocumentPrintOutcome.Failed, error);
}

/// <summary>
/// One print job. <paramref name="PdfPath"/> is a file excise wrote for this
/// job (see <see cref="DocumentPrintWorkflowService"/>); the printer reads it
/// and must not delete it. <paramref name="Owner"/> is the window the print
/// sheet attaches to.
/// </summary>
internal sealed record DocumentPrintRequest(
    string PdfPath,
    string JobTitle,
    PrintScalingMode Scaling,
    Window? Owner);

/// <summary>
/// The platform half of printing (#1545). The view model and
/// <see cref="DocumentPrintWorkflowService"/> are platform-neutral; each OS
/// supplies one of these. macOS is <see cref="MacPdfKitDocumentPrinter"/>;
/// every other platform gets <see cref="UnsupportedDocumentPrinter"/> until
/// its own issue lands (Windows is #1546; Linux printing is out of scope).
/// </summary>
internal interface IDocumentPrinter
{
    /// <summary>Whether this platform can print at all.</summary>
    bool IsSupported { get; }

    /// <summary>The user-facing explanation shown when <see cref="IsSupported"/> is false.</summary>
    string UnsupportedReason { get; }

    /// <summary>
    /// Show the platform print UI for <paramref name="request"/> and complete
    /// when the whole operation has finished (sheet dismissed, job spooled or
    /// cancelled). Called on the UI thread. The file must stay readable until
    /// the returned task completes; the caller deletes it afterwards.
    /// </summary>
    Task<DocumentPrintResult> PrintAsync(DocumentPrintRequest request);
}

/// <summary>
/// The printer for platforms excise does not print on yet. It never prints;
/// the view model shows <see cref="UnsupportedReason"/> instead.
/// </summary>
internal sealed class UnsupportedDocumentPrinter : IDocumentPrinter
{
    internal const string DefaultReason =
        "Printing is available on macOS only in this build. Windows printing is tracked in #1546; " +
        "Linux printing is not planned. Until then, use File > Save As and print the PDF from your " +
        "system's PDF viewer.";

    public bool IsSupported => false;

    public string UnsupportedReason => DefaultReason;

    public Task<DocumentPrintResult> PrintAsync(DocumentPrintRequest request) =>
        Task.FromResult(DocumentPrintResult.Fail(DefaultReason));
}

/// <summary>Chooses the printer for the running OS.</summary>
internal static class DocumentPrinterFactory
{
    internal static IDocumentPrinter CreateForCurrentPlatform(Microsoft.Extensions.Logging.ILoggerFactory loggerFactory) =>
        System.OperatingSystem.IsMacOS()
            ? new MacPdfKitDocumentPrinter(
                Microsoft.Extensions.Logging.LoggerFactoryExtensions.CreateLogger<MacPdfKitDocumentPrinter>(loggerFactory))
            : new UnsupportedDocumentPrinter();
}
