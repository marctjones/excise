using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Excise.App.Services.Printing;
using Excise.Core.Security;
using Microsoft.Extensions.Logging;
using ReactiveUI;

namespace Excise.App.ViewModels;

/// <summary>
/// File ▸ Print… / Cmd+P (#1545, superseding #621). The document as currently
/// edited is written to a temporary copy with pending redactions and type-over
/// edits applied, printed through the platform <see cref="IDocumentPrinter"/>,
/// and deleted afterwards (<see cref="DocumentPrintWorkflowService"/>). macOS
/// prints through PDFKit and the system print sheet; Windows through the
/// system print dialog and excise's own rendering (#1546); Linux through CUPS
/// and excise's own printer chooser (#1710); other platforms show an honest
/// explanation.
/// </summary>
public partial class MainWindowViewModel
{
    internal const string PrintDialogTitle = "Print";
    internal const string PrintNeedsDocumentMessage = "Open a PDF before printing.";
    internal const string PrintDeniedAction = "Printing";
    internal const string PrintDeniedPermission = "printing (/P bit 3)";
    internal const string PrintHighQualityDeniedPermission =
        "full-quality printing (/P bit 12). excise prints and saves through the system print sheet at full " +
        "quality and cannot produce the degraded output this document allows";

    private PrintScalingMode _printScaling = PrintScalingMode.ShrinkOversized;

    /// <summary>
    /// Cancels the print job being sent, if any. Windows rasterises and
    /// spools in the background after its dialog closes; closing the window
    /// aborts that job (best effort: the process may exit first, and the
    /// Windows spooler then discards the unfinished job itself).
    /// </summary>
    private CancellationTokenSource? _printCancellation;

    /// <summary>
    /// How printed pages are scaled onto the paper (#1545). Defaults to
    /// shrinking only oversized pages. A Preferences setting.
    /// </summary>
    public PrintScalingMode PrintScaling
    {
        get => _printScaling;
        set => this.RaiseAndSetIfChanged(ref _printScaling, value);
    }

    /// <summary>
    /// Apply the persisted print-scaling preference on startup. An
    /// unparseable value keeps the default.
    /// </summary>
    public void ApplyPrintScalingPreference(string? printScaling)
    {
        if (Enum.TryParse<PrintScalingMode>(printScaling, out var scaling) && Enum.IsDefined(scaling))
            PrintScaling = scaling;
    }

    /// <summary>
    /// Whether Print… is enabled: a document is open and its /P flags allow
    /// full-quality printing (bits 3 and 12), or the scripting override is
    /// set. On a platform without a printer the item stays enabled so the
    /// command can explain why nothing prints.
    /// </summary>
    public bool CanPrint => IsDocumentLoaded && (IsPrintPermitted(CurrentDocumentPermissions) || IgnoreDocumentPermissions);

    /// <summary>Why Print… is disabled, or null when it is not.</summary>
    public string? PrintDisabledReason =>
        !IsDocumentLoaded ? PrintNeedsDocumentMessage
        : CanPrint ? null
        : "This document's security settings do not allow printing.";

    /// <summary>Abort a print job still being sent. A no-op when none is.</summary>
    internal void CancelPrintInProgress() => _printCancellation?.Cancel();

    private static bool IsPrintPermitted(PdfPermissions permissions) =>
        permissions.CanPrint && permissions.CanPrintHighQuality;

    private void RaiseCanPrintWithDocumentState(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IsDocumentLoaded) or nameof(PdfCoreDocument))
        {
            this.RaisePropertyChanged(nameof(CanPrint));
            this.RaisePropertyChanged(nameof(PrintDisabledReason));
        }
    }

    private async Task PrintAsync()
    {
        _logger.LogInformation("Print command triggered");

        var document = _documentService.GetCurrentDocument();
        if (!_documentService.IsDocumentLoaded || document == null)
        {
            _logger.LogWarning("Cannot print: No document loaded");
            await _dialogService.ShowMessageAsync(PrintDialogTitle, PrintNeedsDocumentMessage);
            return;
        }

        if (!EnsureDocumentPermission(p => p.CanPrint, PrintDeniedAction, PrintDeniedPermission) ||
            !EnsureDocumentPermission(p => p.CanPrintHighQuality, PrintDeniedAction, PrintHighQualityDeniedPermission))
        {
            return;
        }

        if (!_printWorkflow.Printer.IsSupported)
        {
            _logger.LogInformation("Print is not supported on this platform");
            await _dialogService.ShowMessageAsync(PrintDialogTitle, _printWorkflow.Printer.UnsupportedReason);
            return;
        }

        // What Save writes: the form values the viewer holds are pushed into
        // the service document first. This changes no user-visible state.
        SyncAllFormFieldValuesToServiceDocument();

        DocumentPrintWorkflowResult result;
        using var cancellation = new CancellationTokenSource();
        _printCancellation = cancellation;
        try
        {
            result = await _printWorkflow.PrintAsync(DocumentPrintJob.Capture(
                document,
                RedactionWorkflow.PendingRedactions,
                TypewriterTextOperations,
                BuildRedactedCopySafetyOptions(),
                string.IsNullOrWhiteSpace(DocumentName) ? "excise document" : DocumentName,
                PrintScaling,
                _windowHost.MainWindow),
                cancellation.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Printing failed while preparing the print copy");
            await _dialogService.ShowMessageAsync(PrintDialogTitle, $"Printing failed: {ex.Message}");
            return;
        }
        finally
        {
            _printCancellation = null;
        }

        if (result.RedactionSafety is { HasWarnings: true })
        {
            _toastService.ShowWarning(
                "Print copy redaction check",
                "Pending redactions were applied to the printed copy, but the redaction check reported " +
                "warnings. Review them with Apply All before sharing the document.");
        }

        switch (result.Print.Outcome)
        {
            case DocumentPrintOutcome.Failed:
                await _dialogService.ShowMessageAsync(PrintDialogTitle, result.Print.Error ?? "Printing failed.");
                break;
            case DocumentPrintOutcome.Printed when RedactionWorkflow.PendingCount > 0:
                _toastService.ShowInfo(
                    "Printed with pending redactions applied",
                    "The printed pages have the marked content removed. The document itself is unchanged until you apply the redactions.");
                break;
        }
    }
}
