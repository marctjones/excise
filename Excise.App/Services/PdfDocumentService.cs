using Excise.Core.Signatures;
using Microsoft.Extensions.Logging;
using Excise.Core.Document;
using Excise.Core.Operations;
using Excise.Core.Security;
using Excise.Core.Xfa;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Excise.App.Services;

/// <summary>
/// Owns the currently-loaded <see cref="PdfDocument"/> and exposes
/// load / save / page-manipulation / rotation operations to the GUI.
/// Pure Excise.Core — no other PDF library.
/// </summary>
public class PdfDocumentService
{
    private readonly ILogger<PdfDocumentService> _logger;
    private PdfDocument? _currentDocument;
    private string? _currentFilePath;
    private string? _currentUserPassword;

    /// <summary>
    /// How long laying out a dynamic XFA form may take on open (#1547) before
    /// excise gives up and shows the document's own pages.
    /// </summary>
    internal static readonly TimeSpan XfaLayoutTimeLimit = TimeSpan.FromSeconds(15);

    public int PageCount => _currentDocument?.PageCount ?? 0;

    /// <summary>
    /// #1547: what happened when the current document's dynamic XFA form was
    /// laid out on open. Null when the document is not a dynamic XFA form.
    /// </summary>
    public XfaLayoutResult? XfaLayout { get; private set; }
    public bool IsDocumentLoaded => _currentDocument != null;

    /// <summary>
    /// Whether the currently-loaded document's source was encrypted. When
    /// true, <see cref="SaveDocument"/> re-encrypts the output with the same
    /// algorithm/permissions and the password the document was opened with
    /// (#643) — dropping protection is only done via the explicit Security
    /// dialog "Remove Protection" action (#641).
    /// </summary>
    public bool IsEncrypted => _currentDocument?.IsEncrypted ?? false;

    /// <summary>
    /// Encryption options that preserve the current document's source
    /// protection on save (#643): same algorithm (RC4 sources upgraded to
    /// AES-256), same permissions, same metadata-coverage choice, and the
    /// password the document was opened with. Null when no document is
    /// loaded or the source was not encrypted — safe to pass straight to
    /// <see cref="PdfDocument.Save(string, Excise.Core.Security.PdfEncryptionOptions?)"/>.
    /// </summary>
    public Excise.Core.Security.PdfEncryptionOptions? GetReEncryptionOptions()
        => _currentDocument?.GetReEncryptionOptions(_currentUserPassword);

    /// <summary>
    /// Whether the currently-loaded document already has a signed /Sig field.
    /// A cheap in-memory check, not cryptographic verification -- editing and
    /// saving invalidates any existing signature (excise saves are full
    /// rewrites), so this is what the GUI's edit/save flow uses to decide
    /// whether to warn the user first (#1415).
    /// </summary>
    public bool HasSignatures => _currentDocument != null && SignedFieldDetector.HasSignedField(_currentDocument);

    /// <summary>
    /// The password the current document was successfully opened with
    /// (null for none/empty). Needed since #643 because a preserving save
    /// writes ENCRYPTED output, so the app's own post-save reload paths
    /// must reopen that output with the same password instead of failing
    /// or re-prompting the user for a password they already entered.
    /// </summary>
    public string? CurrentUserPassword => _currentUserPassword;

    /// <summary>
    /// Current document's declared PDF version (e.g. "1.7"). Empty when
    /// no document is loaded.
    /// </summary>
    public string PdfVersion => _currentDocument?.Version ?? string.Empty;

    /// <summary>Width of page <paramref name="pageIndex"/> in points. Falls back to Letter.</summary>
    public double GetPageWidth(int pageIndex)
    {
        if (_currentDocument == null || pageIndex < 0 || pageIndex >= PageCount)
            return 612;
        return _currentDocument.GetPage(pageIndex + 1).Width;
    }

    /// <summary>Height of page <paramref name="pageIndex"/> in points. Falls back to Letter.</summary>
    public double GetPageHeight(int pageIndex)
    {
        if (_currentDocument == null || pageIndex < 0 || pageIndex >= PageCount)
            return 792;
        return _currentDocument.GetPage(pageIndex + 1).Height;
    }

    public PdfDocumentService(ILogger<PdfDocumentService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Raised after this service has let go of a loaded document (#1481): the
    /// instance was disposed and is no longer <see cref="GetCurrentDocument"/>.
    /// Raised on the thread that released it, which for an open is a pool thread.
    /// </summary>
    internal event Action<DocumentReleaseReason>? DocumentReleased;

    /// <summary>Load a PDF from disk. Replaces any previously-loaded document.</summary>
    public void LoadDocument(string filePath, string? userPassword = null) =>
        LoadDocument(filePath, userPassword, DocumentReleaseReason.Replaced);

    /// <summary>
    /// <see cref="LoadDocument(string, string?)"/>, naming why a previously
    /// loaded document is being released: the app's post-save reload passes
    /// <see cref="DocumentReleaseReason.SaveReload"/>.
    /// </summary>
    internal void LoadDocument(string filePath, string? userPassword, DocumentReleaseReason releaseReason)
    {
        _logger.LogInformation("Loading PDF document from: {FilePath}", filePath);
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"PDF file not found: {filePath}");

        bool replacing = DisposeIfLoaded(_currentDocument);
        XfaLayout = null;
        _currentDocument = OpenCurrent(filePath, userPassword);
        _currentFilePath = filePath;
        _currentUserPassword = userPassword;
        XfaLayout = LayOutDynamicXfa(_currentDocument);

        _logger.LogInformation(
            "PDF loaded. Pages: {PageCount}, Version: {Version}, File: {FileName}",
            PageCount, PdfVersion, Path.GetFileName(filePath));

        // A failed open throws above and leaves the disposed previous instance
        // current; the caller's CloseDocument then reports it as Closed.
        if (replacing)
            DocumentReleased?.Invoke(releaseReason);
    }

    /// <summary>
    /// #1547: replace a dynamic XFA form's placeholder pages with the form's
    /// layout, before anything else reads the document. Every other document
    /// costs one <see cref="PdfXfaDetection.DetectXfaForm"/> call and nothing
    /// more. A form that cannot be laid out keeps its own pages.
    /// </summary>
    private XfaLayoutResult? LayOutDynamicXfa(PdfDocument document)
    {
        try
        {
            if (document.DetectXfaForm() != PdfXfaFormKind.Dynamic)
                return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "XFA detection failed");
            return null;
        }

        try
        {
            using var timeout = new System.Threading.CancellationTokenSource(XfaLayoutTimeLimit);
            var result = document.ApplyXfaLayout(
                new XfaLayoutOptions { TimeLimit = XfaLayoutTimeLimit }, timeout.Token);
            _logger.LogInformation(
                "Dynamic XFA form: {Status}, {Pages} page(s), omissions [{Omissions}], scripts run [{Ran}], not run [{Scripts}], failed [{Failures}], reason {Reason}",
                result.Status, result.PageCount, string.Join("; ", result.Omissions),
                string.Join(", ", result.ScriptsRun.Select(kv => $"{kv.Key}={kv.Value}")),
                string.Join(", ", result.ScriptsNotRun.Select(kv => $"{kv.Key}={kv.Value}")),
                string.Join("; ", result.ScriptFailures),
                result.FailureReason);
            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("XFA layout timed out after {Seconds} s", XfaLayoutTimeLimit.TotalSeconds);
            return FailedXfaLayout(document, "Laying out the form took too long.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A layout defect must never stop the document opening.
            _logger.LogWarning(ex, "XFA layout failed");
            return FailedXfaLayout(document, ex.Message);
        }
    }

    private static XfaLayoutResult FailedXfaLayout(PdfDocument document, string reason)
        => new()
        {
            Status = XfaLayoutStatus.Failed,
            PageCount = document.PageCount,
            FailureReason = reason,
        };

    /// <summary>
    /// The current document reads from the file on demand (#1567). Until this
    /// it was opened from <c>File.ReadAllBytes</c> so the file stayed freely
    /// writable, which cost one live array the size of the file for the life
    /// of the document — 122 MB on the Altona suite, the largest single item on
    /// the GUI's heap. The share mode keeps the file writable and replaceable:
    /// a save back onto it goes through a sibling temp and a rename
    /// (<c>AtomicFileReplace</c>), after which this stream keeps reading the old
    /// inode until the reload. What is NOT protected is another program
    /// rewriting the file IN PLACE while it is open — a torn read until the
    /// next open, the same exposure Preview has through its file mapping.
    /// </summary>
    private static PdfDocument OpenCurrent(string path, string? userPassword)
    {
        var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1 << 16, FileOptions.None);
        try
        {
            return PdfDocument.Open(stream, userPassword, ownsStream: true);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Dispose without leaving the instance in a caller's local: the release
    /// event hands off to a collection that may run while the caller's frame
    /// is still on the stack, and an unoptimised local keeps the document alive.
    /// </summary>
    private static bool DisposeIfLoaded(PdfDocument? document)
    {
        if (document == null)
            return false;
        document.Dispose();
        return true;
    }

    /// <summary>
    /// Save the current document. If <paramref name="filePath"/> is null,
    /// saves back to the file the document was loaded from. An
    /// encrypted-source document saves encrypted with the same parameters
    /// and password it was opened with (#643).
    /// </summary>
    public void SaveDocument(string? filePath = null)
    {
        if (_currentDocument == null)
            throw new InvalidOperationException("No document loaded");

        var savePath = filePath ?? _currentFilePath
            ?? throw new ArgumentException("File path is required");

        _currentDocument.Save(savePath, GetReEncryptionOptions());
        _logger.LogInformation("PDF saved to: {FilePath}", savePath);

        // Reload to reset in-memory state from the persisted bytes.
        _currentDocument.Dispose();
        _currentDocument = OpenCurrent(savePath, _currentUserPassword);
        _currentFilePath = savePath;
        // #1547: the saved copy is marked (so this is a no-op re-check), or a
        // redaction removed its XFA form (so the result goes back to null).
        XfaLayout = LayOutDynamicXfa(_currentDocument);
        DocumentReleased?.Invoke(DocumentReleaseReason.SaveReload);
    }

    /// <summary>Remove a single page by 0-based index.</summary>
    public void RemovePage(int pageIndex)
    {
        if (_currentDocument == null)
            throw new InvalidOperationException("No document loaded");
        if (pageIndex < 0 || pageIndex >= PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));

        _currentDocument.Pages.RemoveAt(pageIndex);
        _logger.LogInformation("Page {PageIndex} removed; remaining: {Count}", pageIndex, PageCount);
    }

    /// <summary>Remove multiple pages by 0-based indices (in any order).</summary>
    public void RemovePages(IEnumerable<int> pageIndices)
    {
        foreach (var index in pageIndices.OrderByDescending(i => i))
            RemovePage(index);
    }

    /// <summary>Move a page from one 0-based position to another.</summary>
    public void MovePage(int fromIndex, int toIndex)
    {
        if (_currentDocument == null)
            throw new InvalidOperationException("No document loaded");
        if (fromIndex < 0 || fromIndex >= PageCount)
            throw new ArgumentOutOfRangeException(nameof(fromIndex));
        if (toIndex < 0 || toIndex >= PageCount)
            throw new ArgumentOutOfRangeException(nameof(toIndex));

        _currentDocument.Pages.Move(fromIndex, toIndex);
        _logger.LogInformation("Moved page from {FromIndex} to {ToIndex}", fromIndex, toIndex);
    }

    /// <summary>Move selected pages one position earlier or later while preserving their relative order.</summary>
    public IReadOnlyList<int> MovePages(IEnumerable<int> pageIndices, int delta)
    {
        if (_currentDocument == null)
            throw new InvalidOperationException("No document loaded");
        if (delta is not (-1 or 1))
            throw new ArgumentOutOfRangeException(nameof(delta), "Delta must be -1 or 1.");

        var selected = pageIndices
            .Where(i => i >= 0 && i < PageCount)
            .Distinct()
            .OrderBy(i => i)
            .ToList();

        if (selected.Count == 0)
            return Array.Empty<int>();

        var selectedPositions = selected.ToHashSet();
        var traversal = delta < 0
            ? selected
            : selected.OrderByDescending(i => i).ToList();

        foreach (var index in traversal)
        {
            if (!selectedPositions.Contains(index))
                continue;

            var target = index + delta;
            if (target < 0 || target >= PageCount || selectedPositions.Contains(target))
                continue;

            _currentDocument.Pages.Move(index, target);
            selectedPositions.Remove(index);
            selectedPositions.Add(target);
        }

        var newPositions = selectedPositions.OrderBy(i => i).ToList();
        _logger.LogInformation("Moved {Count} selected page(s) by delta {Delta}", newPositions.Count, delta);
        return newPositions;
    }

    /// <summary>
    /// Append pages from another PDF file to the end of the current
    /// document. If <paramref name="pageIndices"/> is null, all pages
    /// from the source are appended.
    /// </summary>
    public void AddPagesFromPdf(string sourcePdfPath, IEnumerable<int>? pageIndices = null)
    {
        if (_currentDocument == null)
            throw new InvalidOperationException("No document loaded");

        // Page cloning copies every stream's bytes at Add time, so the source
        // need not outlive this method (#918).
        using var sourceDocument = PdfDocument.Open(sourcePdfPath);
        var indices = pageIndices?.ToList() ?? Enumerable.Range(0, sourceDocument.PageCount).ToList();

        foreach (var index in indices)
        {
            if (index < 0 || index >= sourceDocument.PageCount) continue;
            _currentDocument.Pages.Add(sourceDocument.GetPage(index + 1));
        }

        _logger.LogInformation("Added {Count} page(s); total now: {Total}", indices.Count, PageCount);
    }

    /// <summary>Insert pages from another PDF at a specific 0-based position.</summary>
    public void InsertPagesFromPdf(string sourcePdfPath, int insertAtIndex, IEnumerable<int>? pageIndices = null)
    {
        if (_currentDocument == null)
            throw new InvalidOperationException("No document loaded");
        if (insertAtIndex < 0 || insertAtIndex > PageCount)
            throw new ArgumentOutOfRangeException(nameof(insertAtIndex));

        using var sourceDocument = PdfDocument.Open(sourcePdfPath);
        var indices = pageIndices?.ToList() ?? Enumerable.Range(0, sourceDocument.PageCount).ToList();

        var cursor = insertAtIndex;
        foreach (var index in indices)
        {
            if (index < 0 || index >= sourceDocument.PageCount) continue;
            _currentDocument.Pages.Insert(cursor, sourceDocument.GetPage(index + 1));
            cursor++;
        }

        _logger.LogInformation(
            "Inserted {Count} source page(s) at {Index}; total now: {Total}",
            indices.Count, insertAtIndex, PageCount);
    }

    /// <summary>
    /// Save selected pages to a new PDF. Page indices are 0-based and emitted
    /// in the caller-provided order. An encrypted source's copy stays encrypted
    /// (#1829), and /P bit 11 gates it as it gates merge and split.
    /// </summary>
    public void ExtractPagesToPdf(string outputPath, IEnumerable<int> pageIndices, bool ignorePermissions = false)
    {
        if (_currentDocument == null)
            throw new InvalidOperationException("No document loaded");
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("Output path is required", nameof(outputPath));

        var indices = pageIndices
            .Distinct()
            .Where(i => i >= 0 && i < PageCount)
            .ToList();

        if (indices.Count == 0)
            throw new ArgumentException("At least one valid page index is required", nameof(pageIndices));

        AssembleGate(ignorePermissions)(_currentDocument, "extracting pages");
        using var extracted = PdfDocument.CreateNew(_currentDocument.Version);
        foreach (var index in indices)
            extracted.Pages.Add(_currentDocument.GetPage(index + 1));

        extracted.Save(outputPath, GetReEncryptionOptions());
        _logger.LogInformation(
            "Extracted {Count} page(s) to {OutputPath}", indices.Count, outputPath);
    }

    /// <summary>
    /// Merge every page of each source PDF, in order, into a new file (see
    /// <see cref="PdfDocumentAssembly.Merge"/>). Does not touch the currently-loaded document.
    /// </summary>
    public MergeDocumentsResult MergeDocumentsToPdf(
        IReadOnlyList<string> sourcePaths, string outputPath, bool ignorePermissions = false)
    {
        var result = PdfDocumentAssembly.Merge(sourcePaths, outputPath, AssembleGate(ignorePermissions));
        _logger.LogInformation(
            "Merged {Count} source document(s) into {OutputPath}; catalog entries not conserved: [{Dropped}]",
            sourcePaths.Count, result.OutputPath, string.Join(", ", result.DroppedCatalogEntries));
        return result;
    }

    /// <summary>
    /// Split the currently-loaded document, as it is in memory, into files under
    /// <paramref name="outputFolder"/> (see <see cref="PdfDocumentAssembly.Split"/>).
    /// </summary>
    public SplitDocumentResult SplitDocument(
        string outputFolder, SplitDocumentSpecification specification, bool ignorePermissions = false)
    {
        if (_currentDocument == null)
            throw new InvalidOperationException("No document loaded");

        var baseName = _currentFilePath != null ? Path.GetFileNameWithoutExtension(_currentFilePath) : "document";
        var result = PdfDocumentAssembly.Split(
            _currentDocument, _currentUserPassword, specification, outputFolder, baseName,
            AssembleGate(ignorePermissions));
        _logger.LogInformation(
            "Split document into {Count} file(s) in {OutputFolder}; catalog entries not conserved: [{Dropped}]",
            result.WrittenPaths.Count, outputFolder, string.Join(", ", result.DroppedCatalogEntries));
        return result;
    }

    /// <summary>
    /// The /P page-assembly gate (bit 11) for extract, merge and split. <paramref name="ignorePermissions"/>
    /// is <c>MainWindowViewModel.IgnoreDocumentPermissions</c>; an override is logged.
    /// </summary>
    private Action<PdfDocument, string> AssembleGate(bool ignorePermissions) => (document, operation) =>
    {
        var permissions = document.EffectivePermissions;
        if (permissions.Allows(DocumentAction.AssembleDocument))
            return;
        if (!ignorePermissions)
        {
            throw new InvalidOperationException(
                $"Blocked by document permissions: {operation} requires " +
                $"{DocumentAction.AssembleDocument.Requirement()}, which this document denies ({permissions}).");
        }
        _logger.LogWarning(
            "Overriding document permissions ({Permissions}): {Action} proceeds because " +
            "IgnoreDocumentPermissions is set", permissions, operation);
    };

    /// <summary>
    /// Whether <paramref name="candidate"/> matches the password that
    /// successfully opened the currently-loaded document (empty/null for an
    /// empty user password — the common case). Used by the Security dialog
    /// (#641) to gate changing/removing password protection on a re-entered
    /// "current password" before writing anything.
    ///
    /// This is NOT a fresh cryptographic re-derivation against the
    /// document's own <c>/Encrypt</c> dictionary — it compares against
    /// <see cref="_currentUserPassword"/>, which <see cref="LoadDocument"/>
    /// already proved correct via <see cref="PdfDocument.Open(byte[],string?)"/>'s
    /// own <c>PdfStandardSecurityHandler</c> verification. Re-deriving here
    /// would mean re-resolving the trailer's <c>/Encrypt</c> dictionary
    /// through the document's normal (decrypting) object resolver, which
    /// risks running the dictionary's own ciphertext /O /U strings back
    /// through string-decryption a second time — comparing against the
    /// already-verified password is exactly as strong and avoids that.
    /// Returns <c>true</c> unconditionally when the document isn't
    /// encrypted (nothing to verify).
    /// </summary>
    public bool VerifyPassword(string? candidate)
    {
        if (_currentDocument == null || !_currentDocument.IsEncrypted) return true;
        return string.Equals(candidate ?? string.Empty, _currentUserPassword ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>Get the current document for advanced operations. Null when unloaded.</summary>
    public PdfDocument? GetCurrentDocument() => _currentDocument;

    /// <summary>
    /// Serialize the current document to a fresh <see cref="MemoryStream"/>
    /// for in-memory rendering.
    /// </summary>
    public MemoryStream? GetCurrentDocumentAsStream()
    {
        if (_currentDocument == null) return null;
        var ms = new MemoryStream(_currentDocument.SaveToBytes()) { Position = 0 };
        return ms;
    }

    /// <summary>
    /// Rotate a page by the given number of degrees (added to the
    /// existing rotation). Must be a multiple of 90.
    /// </summary>
    public void RotatePage(int pageIndex, int degrees)
    {
        if (_currentDocument == null)
            throw new InvalidOperationException("No document loaded");
        if (pageIndex < 0 || pageIndex >= PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));

        degrees = ((degrees % 360) + 360) % 360;
        if (degrees != 0 && degrees != 90 && degrees != 180 && degrees != 270)
            throw new ArgumentException("Rotation must be 0, 90, 180, or 270 degrees", nameof(degrees));

        var page = _currentDocument.GetPage(pageIndex + 1);
        page.Rotation = (page.Rotation + degrees) % 360;
    }

    public void RotatePageRight(int pageIndex) => RotatePage(pageIndex, 90);
    public void RotatePageLeft(int pageIndex) => RotatePage(pageIndex, 270);
    public void RotatePage180(int pageIndex) => RotatePage(pageIndex, 180);

    /// <summary>Dispose the current document and clear state.</summary>
    public void CloseDocument()
    {
        bool closing = DisposeIfLoaded(_currentDocument);
        _currentDocument = null;
        _currentFilePath = null;
        XfaLayout = null;
        if (closing)
            DocumentReleased?.Invoke(DocumentReleaseReason.Closed);
    }
}

/// <summary>Why <see cref="PdfDocumentService"/> released a document (#1481).</summary>
internal enum DocumentReleaseReason
{
    /// <summary>A different load replaced it.</summary>
    Replaced,

    /// <summary>The document was closed.</summary>
    Closed,

    /// <summary>A save reopened the document from the bytes it just wrote.</summary>
    SaveReload,
}
