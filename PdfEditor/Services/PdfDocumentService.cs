using Microsoft.Extensions.Logging;
using Pdfe.Core.Document;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PdfEditor.Services;

/// <summary>
/// Owns the currently-loaded <see cref="PdfDocument"/> and exposes
/// load / save / page-manipulation / rotation operations to the GUI.
/// Pure Pdfe.Core — no other PDF library.
/// </summary>
public class PdfDocumentService
{
    private readonly ILogger<PdfDocumentService> _logger;
    private PdfDocument? _currentDocument;
    private string? _currentFilePath;
    private string? _currentUserPassword;

    public int PageCount => _currentDocument?.PageCount ?? 0;
    public bool IsDocumentLoaded => _currentDocument != null;

    /// <summary>
    /// Whether the currently-loaded document's source was encrypted. pdfe's
    /// writer cannot emit <c>/Encrypt</c> (#624), so any save of this
    /// document produces an unprotected copy — callers must warn before
    /// saving when this is true (#638).
    /// </summary>
    public bool IsEncrypted => _currentDocument?.IsEncrypted ?? false;

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

    /// <summary>Load a PDF from disk. Replaces any previously-loaded document.</summary>
    public void LoadDocument(string filePath, string? userPassword = null)
    {
        _logger.LogInformation("Loading PDF document from: {FilePath}", filePath);
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"PDF file not found: {filePath}");

        _currentDocument?.Dispose();
        // Open from bytes so the file is not held open — matches the
        // previous file-based behavior that kept the file freely writable.
        _currentDocument = userPassword is null
            ? PdfDocument.Open(File.ReadAllBytes(filePath))
            : PdfDocument.Open(File.ReadAllBytes(filePath), userPassword);
        _currentFilePath = filePath;
        _currentUserPassword = userPassword;

        _logger.LogInformation(
            "PDF loaded. Pages: {PageCount}, Version: {Version}, File: {FileName}",
            PageCount, PdfVersion, Path.GetFileName(filePath));
    }

    /// <summary>
    /// Save the current document. If <paramref name="filePath"/> is null,
    /// saves back to the file the document was loaded from.
    /// </summary>
    public void SaveDocument(string? filePath = null)
    {
        if (_currentDocument == null)
            throw new InvalidOperationException("No document loaded");

        var savePath = filePath ?? _currentFilePath
            ?? throw new ArgumentException("File path is required");

        _currentDocument.Save(savePath);
        _logger.LogInformation("PDF saved to: {FilePath}", savePath);

        // Reload to reset in-memory state from the persisted bytes.
        _currentDocument.Dispose();
        _currentDocument = _currentUserPassword is null
            ? PdfDocument.Open(File.ReadAllBytes(savePath))
            : PdfDocument.Open(File.ReadAllBytes(savePath), _currentUserPassword);
        _currentFilePath = savePath;
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

        using var sourceDocument = PdfDocument.Open(File.ReadAllBytes(sourcePdfPath));
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

        using var sourceDocument = PdfDocument.Open(File.ReadAllBytes(sourcePdfPath));
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
    /// in the caller-provided order.
    /// </summary>
    public void ExtractPagesToPdf(string outputPath, IEnumerable<int> pageIndices)
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

        using var extracted = PdfDocument.CreateNew(_currentDocument.Version);
        foreach (var index in indices)
            extracted.Pages.Add(_currentDocument.GetPage(index + 1));

        extracted.Save(outputPath);
        _logger.LogInformation(
            "Extracted {Count} page(s) to {OutputPath}", indices.Count, outputPath);
    }

    /// <summary>
    /// Report document structures that page operations may not preserve perfectly
    /// with the current page-copy implementation.
    /// </summary>
    public PageOperationDiagnostics AnalyzePageOperationPreservation(IEnumerable<int>? pageIndices = null)
    {
        if (_currentDocument == null)
            throw new InvalidOperationException("No document loaded");

        var indices = pageIndices?.Distinct().ToList()
            ?? Enumerable.Range(0, PageCount).ToList();
        var warnings = new List<string>();

        if (_currentDocument.Catalog.GetOptional("Outlines") != null)
            warnings.Add("Document outlines/bookmarks may still point to their original page destinations after page organization changes.");

        if (_currentDocument.Catalog.GetOptional("AcroForm") != null)
            warnings.Add("AcroForm field order and document-level form metadata may not be fully preserved by page insert/extract operations.");

        if (_currentDocument.Catalog.GetOptional("Names") != null)
            warnings.Add("Named destinations or embedded-file name trees may not be fully remapped during page organization changes.");

        foreach (var index in indices.Where(i => i >= 0 && i < PageCount))
        {
            var page = _currentDocument.GetPage(index + 1);
            if (page.GetAnnotations().Any(a => a.Subtype is PdfAnnotationSubtype.Link or PdfAnnotationSubtype.Widget))
            {
                warnings.Add("Links and form widgets on affected pages are copied at page level, but related document-level destinations or field metadata may need review.");
                break;
            }
        }

        return new PageOperationDiagnostics(warnings.Distinct().ToList());
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
        _currentDocument?.Dispose();
        _currentDocument = null;
        _currentFilePath = null;
    }
}

public sealed record PageOperationDiagnostics(IReadOnlyList<string> Warnings)
{
    public bool HasWarnings => Warnings.Count > 0;
}
