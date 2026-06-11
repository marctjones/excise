using Avalonia;
using Microsoft.Extensions.Logging;
using Pdfe.Avalonia.Controls;
using Pdfe.Core.Document;
using PdfEditor.Services;
using ReactiveUI;
using System;
using System.Collections.ObjectModel;
using System.IO;

namespace PdfEditor.ViewModels;

public partial class MainWindowViewModel
{
    private bool _revealHiddenText;

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

    private void RefreshHiddenTextHighlights()
    {
        HiddenTextHighlights.Clear();
        if (!_revealHiddenText) return;
        if (!_documentService.IsDocumentLoaded || string.IsNullOrEmpty(_currentFilePath)) return;

        try
        {
            // Scan fresh from disk so we see exactly what a downstream
            // extractor would see; the in-memory doc may have pending
            // GUI edits we don't want to audit against.
            byte[] bytes = File.ReadAllBytes(_currentFilePath);
            using var doc = PdfDocument.Open(bytes);
            if (CurrentPageIndex < 0 || CurrentPageIndex >= doc.PageCount) return;

            var page = doc.GetPage(CurrentPageIndex + 1);

            // Pass 1: structural — fast, exact characters, never wrong.
            foreach (var h in Pdfe.Core.Text.Segmentation.HiddenTextDetector.ScanPage(
                page, CurrentPageIndex + 1))
            {
                AddHighlight(h.Text, h.BoundingBox, h.HiddenBy, CurrentPageIndex + 1,
                    HiddenTextSource.Structural);
            }

            // Pass 2: differential OCR — slow, opt-in, recovers text
            // hidden inside rasters. Only runs when the user explicitly
            // asks for it AND the tesseract CLI is reachable.
            if (_revealRasterizedHidden)
            {
                var ocr = new Pdfe.Ocr.PdfOcrService();
                if (ocr.IsAvailable())
                {
                    var auditor = new Pdfe.Ocr.DifferentialOcrAuditor(ocr);
                    foreach (var h in auditor.ScanPage(bytes, CurrentPageIndex + 1))
                    {
                        AddHighlight(h.Text, h.BoundingBox,
                            $"raster (OCR conf {h.Confidence:F2})", CurrentPageIndex + 1,
                            HiddenTextSource.DifferentialOcr);
                    }
                }
                else
                {
                    _logger.LogWarning(
                        "RevealRasterizedHidden requested but `tesseract` CLI is not available; skipping differential-OCR pass.");
                }
            }

            _logger.LogInformation(
                "Reveal-hidden-text: {Count} leak(s) on page {Page}",
                HiddenTextHighlights.Count, CurrentPageIndex + 1);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hidden-text scan failed for page {Page}", CurrentPageIndex + 1);
        }
    }

    private void AddHighlight(
        string text,
        PdfRectangle bbox,
        string source,
        int pageNumber,
        HiddenTextSource severity)
    {
        HiddenTextHighlights.Add(new HiddenTextHighlight(
            text,
            PdfPageRect.FromContentPoints(pageNumber, bbox),
            source,
            severity));
    }
}
