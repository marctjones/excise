using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Excise.App.Models;
using Excise.Core.Document;
using Excise.Core.Editing;
using Excise.Core.Text.Segmentation;
using Microsoft.Extensions.Logging;

namespace Excise.App.Services.Printing;

/// <summary>
/// The platform-neutral half of printing (#1545): turn the document AS
/// CURRENTLY EDITED into a temporary PDF, hand that file to the platform
/// <see cref="IDocumentPrinter"/>, and delete it when the print operation is
/// over.
/// </summary>
/// <remarks>
/// <para>
/// <b>What "as currently edited" means.</b> The live document is serialised
/// through the normal writer (<see cref="PdfDocument.SaveToBytes()"/>, the
/// same writer Save As uses) and reopened as a private copy, exactly like
/// the flattened-form-copy flow. On that copy, and never on the live
/// document, the pending type-over edits are flattened and the PENDING
/// redactions are applied through <see cref="RedactionWorkflowService.ApplyToDocument"/>
/// — the same glyph-removal engine Apply All uses. A user who has marked
/// boxes and presses Cmd+P therefore prints the redacted content (removed,
/// not covered). Applied redactions are already in the live document,
/// because Apply All reloads from its redacted output. The UI's pending
/// state is left alone: printing is not applying.
/// </para>
/// <para>
/// <b>Encryption decision: the print copy is written PLAINTEXT.</b>
/// <list type="bullet">
/// <item>The /P print permission is enforced by excise before this service
/// runs, so encrypting the copy would add no access control.</item>
/// <item>PDFKit enforces /P itself on an encrypted file, so a re-encrypted
/// copy would make the documented <c>IgnoreDocumentPermissions</c> override
/// fail silently inside PDFKit, and a document with a user password would
/// need the password handed across the Objective-C bridge.</item>
/// <item>On Windows excise's own renderer rasterises the copy (#1546); a
/// plaintext copy needs no password handed to the print job.</item>
/// <item>The print system spools the job unencrypted anyway, so an encrypted
/// temp file would not keep plaintext off the disk.</item>
/// </list>
/// The exposure is bounded instead: the file lives in a per-print directory
/// under <see cref="AppPaths.CacheDir"/> (never <c>~/Documents</c>, #1543),
/// is created owner-read/write only (on Windows, the per-user ACL of
/// <c>%LOCALAPPDATA%</c>), is deleted on every exit path once the
/// print operation has finished (printed, cancelled, failed, or thrown),
/// and a leftover from a crashed earlier print is swept on the next print.
/// </para>
/// </remarks>
internal sealed class DocumentPrintWorkflowService
{
    internal const string PrintDirectoryName = "Print";

    /// <summary>A print copy older than this is a leftover from a crash and is swept.</summary>
    internal static readonly TimeSpan StaleCopyAge = TimeSpan.FromHours(1);

    private readonly RedactionWorkflowService _redactionWorkflow;
    private readonly IDocumentPrinter _printer;
    private readonly ILogger<DocumentPrintWorkflowService> _logger;
    private readonly Func<string> _printDirectoryResolver;

    public DocumentPrintWorkflowService(
        RedactionWorkflowService redactionWorkflow,
        IDocumentPrinter printer,
        ILogger<DocumentPrintWorkflowService> logger,
        Func<string>? printDirectoryResolver = null)
    {
        _redactionWorkflow = redactionWorkflow ?? throw new ArgumentNullException(nameof(redactionWorkflow));
        _printer = printer ?? throw new ArgumentNullException(nameof(printer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _printDirectoryResolver = printDirectoryResolver ?? (() => Path.Combine(AppPaths.CacheDir, PrintDirectoryName));
    }

    public IDocumentPrinter Printer => _printer;

    /// <summary>The directory print copies are written to.</summary>
    internal string PrintDirectory => _printDirectoryResolver();

    /// <summary>
    /// Print the current state of <see cref="DocumentPrintJob.Document"/>.
    /// Must be called on the UI thread (the live document is serialised
    /// there, and the platform print UI needs it).
    /// <paramref name="cancellationToken"/> is handed to the printer, which
    /// aborts a job still being sent; the copy is deleted either way.
    /// </summary>
    public async Task<DocumentPrintWorkflowResult> PrintAsync(
        DocumentPrintJob job,
        System.Threading.CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(job.Document);

        if (!_printer.IsSupported)
            return new DocumentPrintWorkflowResult(DocumentPrintResult.Fail(_printer.UnsupportedReason), null);

        SweepStaleCopies();

        // Serialise the live document here, on the caller's (UI) thread, as
        // the flattened-copy flow does; everything after this touches only
        // the private copy and may run on the pool.
        var liveBytes = job.Document.SaveToBytes();
        var tempPath = NewCopyPath();
        RedactedCopySafetyReport? safetyReport = null;
        try
        {
            safetyReport = await Task.Run(() => WriteCopy(liveBytes, tempPath, job));
            var result = await _printer.PrintAsync(
                new DocumentPrintRequest(tempPath, job.JobTitle, job.Scaling, job.Owner, cancellationToken));
            _logger.LogInformation("Print finished: {Outcome} {Error}", result.Outcome, result.Error);
            return new DocumentPrintWorkflowResult(result, safetyReport);
        }
        finally
        {
            DeleteCopy(tempPath);
        }
    }

    private RedactedCopySafetyReport? WriteCopy(byte[] liveBytes, string tempPath, DocumentPrintJob job)
    {
        using var copy = PdfDocument.Open(liveBytes);
        RedactedCopySafetyReport? report = null;

        if (job.PendingRedactions.Count > 0)
        {
            // Applies the pending type-over edits too; do not flatten them twice.
            var application = _redactionWorkflow.ApplyToDocument(
                RedactionApplicationRequest.Capture(
                    copy, job.PendingRedactions, job.PendingTypewriterOperations, job.SafetyOptions));
            report = application.SafetyReport;
            _logger.LogInformation(
                "Print copy: applied {Applied} pending redaction(s), skipped {Skipped}, flattened {Typewriter} type-over edit(s)",
                application.AppliedRedactionCount,
                application.SkippedRedactionCount,
                application.AppliedTypewriterOperationCount);
        }
        else if (job.PendingTypewriterOperations.Count > 0)
        {
            PdfTypewriterTextApplier.Apply(copy, job.PendingTypewriterOperations);
        }

        var directory = Path.GetDirectoryName(tempPath)!;
        CreatePrivateDirectory(directory);
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        using (var stream = new FileStream(tempPath, options))
        {
            // Plaintext by design; see the class remarks.
            copy.Save(stream, encryptionOptions: null);
        }

        _logger.LogInformation("Print copy written: {Bytes} bytes", new FileInfo(tempPath).Length);
        return report;
    }

    private string NewCopyPath() =>
        Path.Combine(PrintDirectory, $"excise-print-{Guid.NewGuid():N}.pdf");

    private static void CreatePrivateDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(directory);
        else
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private void DeleteCopy(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The stale sweep retries it on the next print.
            _logger.LogWarning(ex, "Could not delete print copy {Path}", path);
        }
    }

    /// <summary>
    /// Delete print copies a crashed earlier print left behind. A copy newer
    /// than <see cref="StaleCopyAge"/> may belong to a print still in
    /// progress, so it is kept. Internal for tests.
    /// </summary>
    internal int SweepStaleCopies(DateTime? nowUtc = null)
    {
        var directory = PrintDirectory;
        if (!Directory.Exists(directory))
            return 0;

        var cutoff = (nowUtc ?? DateTime.UtcNow) - StaleCopyAge;
        var removed = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "excise-print-*.pdf"))
            {
                if (File.GetLastWriteTimeUtc(file) > cutoff)
                    continue;
                DeleteCopy(file);
                if (!File.Exists(file))
                    removed++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not sweep stale print copies in {Directory}", directory);
        }

        if (removed > 0)
            _logger.LogInformation("Swept {Count} stale print copies", removed);
        return removed;
    }
}

/// <summary>Everything a print needs from the view model's current state.</summary>
internal sealed record DocumentPrintJob(
    PdfDocument Document,
    IReadOnlyList<PendingRedaction> PendingRedactions,
    IReadOnlyList<PdfTypewriterTextOperation> PendingTypewriterOperations,
    RedactedCopySafetyOptions? SafetyOptions,
    string JobTitle,
    PrintScalingMode Scaling,
    Window? Owner)
{
    public static DocumentPrintJob Capture(
        PdfDocument document,
        IEnumerable<PendingRedaction> pendingRedactions,
        IEnumerable<PdfTypewriterTextOperation> typewriterOperations,
        RedactedCopySafetyOptions? safetyOptions,
        string jobTitle,
        PrintScalingMode scaling,
        Window? owner) =>
        new(
            document,
            pendingRedactions.ToArray(),
            typewriterOperations.Where(operation => operation.IsPending && operation.HasText).ToArray(),
            safetyOptions,
            jobTitle,
            scaling,
            owner);
}

/// <summary>
/// The printer's result plus the redaction safety report of the print copy
/// (null when no pending redaction was applied to it).
/// </summary>
internal sealed record DocumentPrintWorkflowResult(
    DocumentPrintResult Print,
    RedactedCopySafetyReport? RedactionSafety);
