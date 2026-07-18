using Avalonia;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Excise.Avalonia.Controls;
using Excise.Core.Document;
using Excise.App.Services;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Excise.App.ViewModels;

public partial class MainWindowViewModel
{
    private bool _revealHiddenText;
    private bool _isHiddenTextScanInProgress;
    private CancellationTokenSource? _hiddenTextRefreshCts;

    /// <summary>
    /// When true, scans the current page for hidden-behind-overlay text
    /// and surfaces it through <see cref="HiddenTextHighlights"/>.
    /// </summary>
    public bool RevealHiddenText
    {
        get => _revealHiddenText;
        set
        {
            this.RaiseAndSetIfChanged(ref _revealHiddenText, value);
            RefreshHiddenTextHighlights();
        }
    }

    private bool _revealRasterizedHidden;

    /// <summary>
    /// When true (and <see cref="RevealHiddenText"/> is also on), runs
    /// differential OCR on the current page in addition to the structural
    /// scan. Slower; requires the <c>tesseract</c> CLI. Catches text
    /// that's only present inside images and visually obstructed.
    /// </summary>
    public bool RevealRasterizedHidden
    {
        get => _revealRasterizedHidden;
        set
        {
            this.RaiseAndSetIfChanged(ref _revealRasterizedHidden, value);
            RefreshHiddenTextHighlights();
        }
    }

    /// <summary>
    /// Highlights to paint on top of the current page — each entry is a
    /// piece of text that the PDF still contains but has visually hidden
    /// behind an overlay. Coords are in rendered-image pixels at the
    /// current render DPI, top-left origin.
    /// </summary>
    public ObservableCollection<HiddenTextHighlight> HiddenTextHighlights { get; }
        = new();

    public bool IsHiddenTextScanInProgress
    {
        get => _isHiddenTextScanInProgress;
        private set => this.RaiseAndSetIfChanged(ref _isHiddenTextScanInProgress, value);
    }

    private void RefreshHiddenTextHighlights()
    {
        var previous = Interlocked.Exchange(ref _hiddenTextRefreshCts, null);
        previous?.Cancel();
        HiddenTextHighlights.Clear();
        IsHiddenTextScanInProgress = false;
        if (!_revealHiddenText) return;
        if (!_documentService.IsDocumentLoaded || string.IsNullOrEmpty(_currentFilePath)) return;

        var filePath = _currentFilePath;
        var pageIndex = CurrentPageIndex;
        var includeRasterizedHidden = _revealRasterizedHidden;
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _hiddenTextRefreshCts, cts)?.Cancel();
        IsHiddenTextScanInProgress = true;

        _ = RefreshHiddenTextHighlightsAsync(filePath, pageIndex, includeRasterizedHidden, cts);
    }

    private async Task RefreshHiddenTextHighlightsAsync(
        string filePath,
        int pageIndex,
        bool includeRasterizedHidden,
        CancellationTokenSource cts)
    {
        try
        {
            var token = cts.Token;
            var highlights = await Task.Run(
                () => ScanHiddenTextHighlights(filePath, pageIndex, includeRasterizedHidden, token),
                token);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!IsCurrentHiddenTextScan(cts, filePath, pageIndex, includeRasterizedHidden))
                    return;

                HiddenTextHighlights.Clear();
                foreach (var highlight in highlights)
                    HiddenTextHighlights.Add(highlight);

                _logger.LogInformation(
                    "Reveal-hidden-text: {Count} leak(s) on page {Page}",
                    HiddenTextHighlights.Count, pageIndex + 1);
            });
        }
        catch (OperationCanceledException)
        {
            // Expected when the user changes page or toggles reveal options.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hidden-text scan failed for page {Page}", pageIndex + 1);
        }
        finally
        {
            if (ReferenceEquals(Volatile.Read(ref _hiddenTextRefreshCts), cts))
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!ReferenceEquals(_hiddenTextRefreshCts, cts))
                        return;
                    _hiddenTextRefreshCts = null;
                    IsHiddenTextScanInProgress = false;
                });
            }
            cts.Dispose();
        }
    }

    private IReadOnlyList<HiddenTextHighlight> ScanHiddenTextHighlights(
        string filePath,
        int pageIndex,
        bool includeRasterizedHidden,
        CancellationToken cancellationToken)
    {
        // Scan fresh from disk so we see exactly what a downstream extractor
        // would see; the in-memory doc may have pending GUI edits we do not
        // want to audit against.
        cancellationToken.ThrowIfCancellationRequested();
        byte[]? pdfBytes = includeRasterizedHidden ? File.ReadAllBytes(filePath) : null;
        using var doc = pdfBytes is null
            ? PdfDocument.Open(filePath)
            : PdfDocument.Open(pdfBytes);
        cancellationToken.ThrowIfCancellationRequested();
        if (pageIndex < 0 || pageIndex >= doc.PageCount)
            return Array.Empty<HiddenTextHighlight>();

        var pageNumber = pageIndex + 1;
        var page = doc.GetPage(pageNumber);
        var highlights = new List<HiddenTextHighlight>();

        // Pass 1: structural — fast, exact characters, never wrong.
        foreach (var h in Excise.Core.Text.Segmentation.HiddenTextDetector.ScanPage(page, pageNumber))
        {
            cancellationToken.ThrowIfCancellationRequested();
            highlights.Add(CreateHighlight(h.Text, h.BoundingBox, h.HiddenBy, pageNumber,
                HiddenTextSource.Structural));
        }

        // Pass 2: differential OCR — slow, opt-in, recovers text hidden
        // inside rasters. Only runs when the user explicitly asks for it AND
        // the tesseract CLI is reachable.
        if (includeRasterizedHidden && pdfBytes != null)
            AddRasterizedHiddenTextHighlights(pdfBytes, pageNumber, highlights, cancellationToken);

        return highlights;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void AddRasterizedHiddenTextHighlights(
        byte[] pdfBytes,
        int pageNumber,
        List<HiddenTextHighlight> highlights,
        CancellationToken cancellationToken)
    {
        var ocr = new Excise.Ocr.PdfOcrService();
        if (ocr.IsAvailable())
        {
            var auditor = new Excise.Ocr.DifferentialOcrAuditor(ocr);
            foreach (var h in auditor.ScanPage(pdfBytes, pageNumber))
            {
                cancellationToken.ThrowIfCancellationRequested();
                highlights.Add(CreateHighlight(h.Text, h.BoundingBox,
                    $"raster (OCR conf {h.Confidence:F2})", pageNumber,
                    HiddenTextSource.DifferentialOcr));
            }
        }
        else
        {
            _logger.LogWarning(
                "RevealRasterizedHidden requested but `tesseract` CLI is not available; skipping differential-OCR pass.");
        }
    }

    private static HiddenTextHighlight CreateHighlight(
        string text,
        PdfRectangle bbox,
        string source,
        int pageNumber,
        HiddenTextSource severity)
    {
        return new HiddenTextHighlight(
            text,
            PdfPageRect.FromContentPoints(pageNumber, bbox),
            source,
            severity);
    }

    private bool IsCurrentHiddenTextScan(
        CancellationTokenSource cts,
        string filePath,
        int pageIndex,
        bool includeRasterizedHidden)
    {
        return !cts.IsCancellationRequested
            && ReferenceEquals(Volatile.Read(ref _hiddenTextRefreshCts), cts)
            && _revealHiddenText
            && _revealRasterizedHidden == includeRasterizedHidden
            && CurrentPageIndex == pageIndex
            && string.Equals(_currentFilePath, filePath, StringComparison.Ordinal);
    }
}
