using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Excise.Core.Document;
using Microsoft.Extensions.Logging;

namespace Excise.App.Services.Printing;

/// <summary>What the user chose in the Linux printer chooser (#1710).</summary>
internal enum LinuxPrintDialogOutcome
{
    Print,
    Cancelled,
    Failed,
}

/// <summary>
/// The Linux printer chooser's answer (#1710). <paramref name="Ranges"/> is
/// empty for "All pages". <paramref name="Copies"/> is handed to CUPS as
/// <c>-n</c>: unlike Windows, excise never produces copies itself, because the
/// queue's own filters do it.
/// </summary>
internal sealed record LinuxPrintTicket(
    LinuxPrintDialogOutcome Outcome,
    string QueueName,
    IReadOnlyList<PrintPageRange> Ranges,
    int Copies,
    bool Collate,
    string? Error = null)
{
    internal static LinuxPrintTicket Cancelled { get; } =
        new(LinuxPrintDialogOutcome.Cancelled, string.Empty, Array.Empty<PrintPageRange>(), 1, true);

    internal static LinuxPrintTicket Fail(string error) =>
        new(LinuxPrintDialogOutcome.Failed, string.Empty, Array.Empty<PrintPageRange>(), 1, true, error);

    internal static LinuxPrintTicket Print(
        string queueName,
        IReadOnlyList<PrintPageRange>? ranges = null,
        int copies = 1,
        bool collate = true) =>
        new(LinuxPrintDialogOutcome.Print, queueName, ranges ?? Array.Empty<PrintPageRange>(), copies, collate);
}

/// <summary>
/// Shows the printer chooser (#1710). Linux has no system print dialog excise
/// can call — Avalonia has no printing support at all, and GTK's and Qt's
/// dialogs belong to their own toolkits — so excise draws its own. Called on
/// the UI thread.
/// </summary>
internal interface ILinuxPrintDialog
{
    Task<LinuxPrintTicket> ShowAsync(Window? owner, IReadOnlyList<CupsPrintQueue> queues, int pageCount);
}

/// <summary>
/// Prints on Linux through CUPS (#1710): queues enumerated with
/// <c>lpstat</c>, the job handed to <c>lp</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the command line and not libcups.</b> PDF is CUPS's native spool
/// format and <see cref="DocumentPrintWorkflowService"/> already writes a PDF
/// for every job, so the whole Linux path is "give CUPS this file" — no
/// rasterising, no equivalent of the Windows GDI work (#1546). <c>lp</c> and
/// <c>lpstat</c> are part of CUPS itself and ship on every mainstream distro,
/// so this adds no dependency, nothing to version-match, and nothing
/// reflection-heavy that Native AOT would trip over. P/Invoking libcups would
/// buy richer status at the cost of a native dependency and ABI risk.
/// </para>
/// <para>
/// <b>Everything here fails loudly.</b> A print that quietly does nothing is
/// worse than one that says why, so each of the five ways this can go wrong —
/// the tools are missing, the scheduler is unreachable, there are no queues,
/// the chooser broke, <c>lp</c> refused the job — returns its own message
/// through <see cref="DocumentPrintResult.Fail"/>, which the view model puts
/// in front of the user. <see cref="IsSupported"/> stays true on Linux so the
/// menu item can be reached and explain itself, exactly as on the other two
/// platforms.
/// </para>
/// <para>
/// <b>Scaling.</b> CUPS offers <c>fit-to-page</c> and nothing else, so
/// <see cref="PrintScalingMode.FitToPage"/> sends it and both
/// <see cref="PrintScalingMode.ActualSize"/> and
/// <see cref="PrintScalingMode.ShrinkOversized"/> send no option at all and
/// leave the queue's own filter to place the page. Shrink-only is not
/// expressible as a CUPS option; claiming it here would be inventing a
/// behaviour excise cannot deliver.
/// </para>
/// </remarks>
internal sealed class LinuxCupsDocumentPrinter : IDocumentPrinter
{
    /// <summary>The CUPS client tools this printer shells out to.</summary>
    internal const string LpCommand = "lp";
    internal const string LpstatCommand = "lpstat";

    internal const string ToolsMissingMessage =
        "Printing on Linux needs the CUPS command-line tools, and 'lpstat' could not be run. " +
        "Install the CUPS client package (for example 'sudo apt install cups-client') and try again.";

    internal const string SchedulerUnreachableMessage =
        "The CUPS printing service did not answer. Start it (for example 'sudo systemctl start cups') and try again.";

    internal const string NoQueuesMessage =
        "No printers are set up on this computer. Add one in your desktop's printer settings, " +
        "or with 'lpadmin', and try again.";

    internal const string NoPagesMessage = "The chosen page range contains no pages of this document.";

    internal const string DialogFailedMessage = "The printer chooser could not be shown.";

    /// <summary>
    /// How long a CUPS tool gets. <c>lpstat</c> blocks on an unreachable
    /// scheduler and <c>lp</c> blocks while it copies the file to the
    /// spooler, so neither may run unbounded (CLAUDE.md Pitfall 3).
    /// </summary>
    internal static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan SubmitTimeout = TimeSpan.FromSeconds(120);

    private readonly ICupsProcessRunner _runner;
    private readonly ILinuxPrintDialog _dialog;
    private readonly ILogger _logger;

    internal LinuxCupsDocumentPrinter(
        ICupsProcessRunner runner,
        ILinuxPrintDialog dialog,
        ILogger logger,
        bool isSupported = true)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _dialog = dialog ?? throw new ArgumentNullException(nameof(dialog));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        IsSupported = isSupported;
    }

    /// <summary>The production printer: real subprocesses, the real chooser window.</summary>
    internal static LinuxCupsDocumentPrinter CreateNative(ILogger logger) =>
        new(new CupsProcessRunner(), new LinuxPrintChooserDialog(), logger);

    public bool IsSupported { get; }

    public string UnsupportedReason => IsSupported
        ? ToolsMissingMessage
        : UnsupportedDocumentPrinter.DefaultReason;

    /// <summary>
    /// The queues CUPS knows about, or a failure message. Public to the
    /// printer's own tests; the print path calls it first and refuses to show
    /// a chooser with nothing in it.
    /// </summary>
    internal (IReadOnlyList<CupsPrintQueue> Queues, string? Error) ListQueues()
    {
        var destinations = _runner.Run(LpstatCommand, ["-e"], QueryTimeout);
        if (!destinations.Executed)
        {
            _logger.LogWarning("lpstat -e did not run: {Failure}", destinations.Failure);
            return (Array.Empty<CupsPrintQueue>(), ToolsMissingMessage);
        }

        var printers = _runner.Run(LpstatCommand, ["-p"], QueryTimeout);
        var defaults = _runner.Run(LpstatCommand, ["-d"], QueryTimeout);

        // `lpstat -e` exits non-zero when the scheduler is not running, and so
        // does `-p`; a zero exit from either means CUPS answered.
        if (!destinations.Succeeded && !printers.Succeeded)
        {
            var detail = destinations.Diagnostics();
            _logger.LogWarning("lpstat could not reach the CUPS scheduler: {Detail}", detail);
            return (Array.Empty<CupsPrintQueue>(),
                detail.Length > 0 ? $"{SchedulerUnreachableMessage} ({detail})" : SchedulerUnreachableMessage);
        }

        var queues = CupsPrintQueues.Parse(
            destinations.Succeeded ? destinations.StandardOutput : null,
            printers.Succeeded ? printers.StandardOutput : null,
            defaults.Succeeded ? defaults.StandardOutput : null);

        return queues.Count == 0
            ? (queues, NoQueuesMessage)
            : (queues, null);
    }

    public async Task<DocumentPrintResult> PrintAsync(DocumentPrintRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsSupported)
            return DocumentPrintResult.Fail(UnsupportedDocumentPrinter.DefaultReason);

        var cancellationToken = request.CancellationToken;
        try
        {
            var (queues, listError) = await Task.Run(ListQueues, cancellationToken);
            if (listError != null)
                return DocumentPrintResult.Fail(listError);

            // Read the whole copy into memory: no handle stays open on the
            // file, so the workflow can delete it the moment this returns.
            int pageCount = await Task.Run(
                () =>
                {
                    using var document = PdfDocument.Open(File.ReadAllBytes(request.PdfPath));
                    return document.PageCount;
                },
                cancellationToken);
            if (pageCount < 1)
                return DocumentPrintResult.Fail(NoPagesMessage);

            // Back on the caller's (UI) thread for the modal chooser.
            var ticket = await _dialog.ShowAsync(request.Owner, queues, pageCount);
            switch (ticket.Outcome)
            {
                case LinuxPrintDialogOutcome.Cancelled:
                    return DocumentPrintResult.Cancelled;
                case LinuxPrintDialogOutcome.Failed:
                    return DocumentPrintResult.Fail(ticket.Error ?? DialogFailedMessage);
            }

            if (string.IsNullOrWhiteSpace(ticket.QueueName))
                return DocumentPrintResult.Fail(NoQueuesMessage);
            if (PrintPageSequence.Build(pageCount, ticket.Ranges, 1, collate: true).Count == 0)
                return DocumentPrintResult.Fail(NoPagesMessage);

            var arguments = BuildLpArguments(request, ticket);
            _logger.LogInformation(
                "Sending {Pages} page(s) to CUPS queue {Queue}: {Copies} copy/copies, collate={Collate}, scaling {Scaling}",
                pageCount, ticket.QueueName, ticket.Copies, ticket.Collate, request.Scaling);

            var submitted = await Task.Run(
                () => _runner.Run(LpCommand, arguments, SubmitTimeout),
                cancellationToken);

            if (!submitted.Executed)
            {
                _logger.LogError("lp did not run: {Failure}", submitted.Failure);
                return DocumentPrintResult.Fail(
                    submitted.Failure != null && submitted.Failure.Contains("did not finish", StringComparison.Ordinal)
                        ? $"The print job was not accepted: {submitted.Failure}"
                        : ToolsMissingMessage);
            }
            if (submitted.ExitCode != 0)
            {
                var detail = submitted.Diagnostics();
                _logger.LogError("lp exited {Exit}: {Detail}", submitted.ExitCode, detail);
                return DocumentPrintResult.Fail(
                    detail.Length > 0
                        ? $"The printer did not accept the job: {detail}"
                        : $"The printer did not accept the job (lp exited with code {submitted.ExitCode}).");
            }

            var jobId = CupsPrintQueues.ParseRequestId(submitted.StandardOutput);
            _logger.LogInformation("CUPS accepted the job as {JobId}", jobId ?? "(no request id reported)");
            return DocumentPrintResult.Printed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Linux print cancelled");
            return DocumentPrintResult.Cancelled;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Linux print failed");
            return DocumentPrintResult.Fail($"Printing failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The <c>lp</c> command line for one job. Pure, so every option it can
    /// emit is pinned by a unit test without a printer.
    /// </summary>
    internal static IReadOnlyList<string> BuildLpArguments(DocumentPrintRequest request, LinuxPrintTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(ticket);

        var arguments = new List<string> { "-d", ticket.QueueName };

        var title = JobTitle(request.JobTitle);
        if (title.Length > 0)
        {
            arguments.Add("-t");
            arguments.Add(title);
        }

        int copies = Math.Clamp(ticket.Copies, 1, PrintPageSequence.MaxCopies);
        if (copies > 1)
        {
            arguments.Add("-n");
            arguments.Add(copies.ToString(CultureInfo.InvariantCulture));
            arguments.Add("-o");
            arguments.Add(ticket.Collate ? "Collate=True" : "Collate=False");
        }

        var ranges = PrintPageRangeText.ToCupsValue(ticket.Ranges);
        if (ranges != null)
        {
            arguments.Add("-o");
            arguments.Add($"page-ranges={ranges}");
        }

        // See the class remarks: fit-to-page is the only scaling CUPS has.
        if (request.Scaling == PrintScalingMode.FitToPage)
        {
            arguments.Add("-o");
            arguments.Add("fit-to-page");
        }

        // `--` so a spool path that begins with '-' is still read as a file.
        arguments.Add("--");
        arguments.Add(request.PdfPath);
        return arguments;
    }

    /// <summary>
    /// A job title CUPS will take: IPP <c>job-name</c> is text, so control
    /// characters go and the length is bounded (CUPS truncates at 255 itself).
    /// </summary>
    private static string JobTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return string.Empty;
        var cleaned = new string(title.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return cleaned.Length <= 255 ? cleaned : cleaned[..255];
    }
}
