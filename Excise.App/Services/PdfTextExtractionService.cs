using Avalonia;
using Microsoft.Extensions.Logging;
using Excise.Core.Document;
using Excise.Core.Text;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Excise.App.Services;

/// <summary>
/// Service for extracting text from PDF pages
/// Uses Excise.Core for text extraction
/// </summary>
public class PdfTextExtractionService
{
    private readonly ILogger<PdfTextExtractionService> _logger;

    public PdfTextExtractionService(ILogger<PdfTextExtractionService> logger)
    {
        _logger = logger;
        _logger.LogDebug("PdfTextExtractionService instance created");
    }

    /// <summary>
    /// Extract all text from a page (stream-based - primary method)
    /// </summary>
    /// <param name="pdfStream">PDF document stream (can be file stream or memory stream)</param>
    /// <param name="pageIndex">Zero-based page index</param>
    /// <param name="sourceName">Optional name for logging (e.g., filename)</param>
    public string ExtractTextFromPage(Stream pdfStream, int pageIndex, string sourceName = "PDF")
    {
        _logger.LogInformation("Extracting text from page {PageIndex} of {FileName}",
            pageIndex + 1, sourceName);

        try
        {
            using var document = PdfDocument.Open(pdfStream, ownsStream: false);

            if (pageIndex < 0 || pageIndex >= document.PageCount)
            {
                _logger.LogWarning("Invalid page index: {PageIndex}, total pages: {TotalPages}",
                    pageIndex, document.PageCount);
                return string.Empty;
            }

            var page = document.GetPage(pageIndex + 1); // 1-based indexing
            var text = page.Text;

            _logger.LogInformation("Extracted {Length} characters from page {PageIndex}",
                text.Length, pageIndex + 1);

            return text;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting text from page {PageIndex}", pageIndex);
            return string.Empty;
        }
    }

    /// <summary>
    /// Extract all text from a page (file-based wrapper for backward compatibility)
    /// </summary>
    public string ExtractTextFromPage(string pdfPath, int pageIndex)
    {
        using var stream = File.OpenRead(pdfPath);
        return ExtractTextFromPage(stream, pageIndex, Path.GetFileName(pdfPath));
    }

    /// <summary>
    /// Extract all text from all pages in the PDF document
    /// </summary>
    /// <param name="pdfPath">Path to PDF file</param>
    /// <returns>Concatenated text from all pages</returns>
    public string ExtractAllText(string pdfPath)
    {
        _logger.LogInformation("Extracting all text from {FileName}", Path.GetFileName(pdfPath));

        try
        {
            using var document = PdfDocument.Open(pdfPath);
            var allText = new StringBuilder();

            for (int i = 0; i < document.PageCount; i++)
            {
                var page = document.GetPage(i + 1); // 1-based indexing
                var pageText = page.Text;

                if (!string.IsNullOrWhiteSpace(pageText))
                {
                    allText.Append(pageText);
                    allText.Append('\n'); // Separate pages with newline
                }
            }

            var result = allText.ToString();
            _logger.LogInformation("Extracted {Length} total characters from {PageCount} pages",
                result.Length, document.PageCount);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting all text from {FileName}", pdfPath);
            return string.Empty;
        }
    }

    /// <summary>
    /// Extract text from a specific area of the page (stream-based - primary method)
    /// </summary>
    /// <param name="pdfStream">PDF document stream (can be file stream or memory stream)</param>
    /// <param name="pageIndex">Zero-based page index</param>
    /// <param name="area">Selection area with an explicit coordinate space.</param>
    /// <param name="sourceName">Optional name for logging (e.g., filename)</param>
    public string ExtractTextFromArea(Stream pdfStream, int pageIndex, PdfPageRect area, string sourceName = "PDF")
    {
        _logger.LogInformation(
            "Extracting text from {Space} area ({X:F2},{Y:F2},{W:F2}x{H:F2}) on page {PageIndex} of {FileName}",
            area.Space, area.X, area.Y, area.Width, area.Height, pageIndex + 1, sourceName);

        try
        {
            using var document = PdfDocument.Open(pdfStream, ownsStream: false);

            if (pageIndex < 0 || pageIndex >= document.PageCount)
            {
                _logger.LogWarning("Invalid page index: {PageIndex}, total pages: {TotalPages}",
                    pageIndex, document.PageCount);
                return string.Empty;
            }

            var page = document.GetPage(pageIndex + 1); // 1-based indexing

            // Get words from page (we'll iterate their letters for character-level selection)
            var words = page.GetWords();
            var extractedText = new StringBuilder();

            var pdfRect = PdfCoordinateMapper.ToContentPoints(page, area).ToPdfRectangle().Normalize();

            _logger.LogInformation(
                "Coordinate conversion via PdfCoordinateMapper: " +
                "({X:F2},{Y:F2},{W:F2}x{H:F2}) {Space} → PDF content ({Left:F2},{Bottom:F2}) to ({Right:F2},{Top:F2})",
                area.X, area.Y, area.Width, area.Height,
                area.Space,
                pdfRect.Left, pdfRect.Bottom, pdfRect.Right, pdfRect.Top);

            // CHARACTER-LEVEL SELECTION: Find individual letters whose center point is inside the selection
            // This allows precise text selection (e.g., selecting "can therefore" from a sentence
            // without accidentally including adjacent words)
            var selectedLetters = new List<Letter>();

            foreach (var word in words)
            {
                foreach (var letter in word.Letters)
                {
                    if (IsLetterCenterInSelection(letter.GlyphRectangle, pdfRect))
                    {
                        selectedLetters.Add(letter);
                    }
                }
            }

            _logger.LogInformation("Found {LetterCount} letters in selection area (character-level)", selectedLetters.Count);

            if (selectedLetters.Count == 0)
            {
                _logger.LogWarning("No letters found in selection area");
                return string.Empty;
            }

            // Group letters into lines based on Y coordinate
            // Issue #105: Use StartBaseLine.Y for more reliable vertical positioning
            // which handles PDFs with complex text matrices better than GlyphRectangle
            const double lineHeightThreshold = 5.0;
            var lines = new List<List<Letter>>();

            foreach (var letter in selectedLetters)
            {
                // Use StartBaseLine.Y as the primary position indicator
                var letterY = letter.StartBaseLine.Y;
                var line = lines.FirstOrDefault(l =>
                {
                    var lineY = l[0].StartBaseLine.Y;
                    return Math.Abs(lineY - letterY) < lineHeightThreshold;
                });

                if (line == null)
                {
                    line = new List<Letter>();
                    lines.Add(line);
                }
                line.Add(letter);
            }

            // Sort lines top to bottom (higher Y = top in PDF coordinates)
            var sortedLines = lines.OrderByDescending(line => line[0].StartBaseLine.Y).ToList();

            // Within each line, sort letters left to right
            // Issue #105: Use StartBaseLine.X for sorting as it's more reliable than GlyphRectangle.Left
            // in PDFs with complex text positioning (Type 1 fonts, Tz scaling issues)
            foreach (var line in sortedLines)
            {
                line.Sort((a, b) =>
                {
                    // Prefer StartBaseLine.X as it represents the actual text rendering position
                    // Fall back to GlyphRectangle.Left for compatibility
                    var aX = a.StartBaseLine.X;
                    var bX = b.StartBaseLine.X;
                    return aX.CompareTo(bX);
                });
            }

            // Build text with proper spacing
            for (int i = 0; i < sortedLines.Count; i++)
            {
                var line = sortedLines[i];
                double lastRight = 0;

                for (int j = 0; j < line.Count; j++)
                {
                    var letter = line[j];

                    if (j > 0)
                    {
                        // Check gap from previous letter
                        var gap = letter.GlyphRectangle.Left - lastRight;
                        // If gap is larger than typical letter spacing, add a space
                        // Threshold: 2.5 points (typical word spacing is 3-4 points)
                        if (gap > 2.5)
                        {
                            extractedText.Append(' ');
                        }
                    }

                    extractedText.Append(letter.Value);
                    lastRight = letter.GlyphRectangle.Right;
                }

                // Add newline after line (except last)
                if (i < sortedLines.Count - 1)
                    extractedText.AppendLine();
            }

            var result = extractedText.ToString().Trim();
            _logger.LogInformation(
                "Text extraction complete: {LetterCount} letters extracted, {CharCount} characters in result",
                selectedLetters.Count, result.Length);

            // Log the actual extracted text (first 200 chars)
            if (result.Length > 0)
            {
                var preview = result.Length > 200 ? result.Substring(0, 200) + "..." : result;
                _logger.LogInformation("Extracted text preview: \"{Preview}\"", preview);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting text from area on page {PageIndex}", pageIndex);
            return string.Empty;
        }
    }

    public string ExtractTextFromArea(Stream pdfStream, int pageIndex, Rect area, int renderDpi = 150, string sourceName = "PDF")
    {
        return ExtractTextFromArea(
            pdfStream,
            pageIndex,
            PdfPageRect.ViewerDips(pageIndex + 1, area.X, area.Y, area.Width, area.Height, renderDpi),
            sourceName);
    }

    /// <summary>
    /// Check if two rectangles intersect (kept for potential future use)
    /// </summary>
    private static bool Intersects(PdfRectangle a, PdfRectangle b)
    {
        return !(a.Right < b.Left || a.Left > b.Right ||
                 a.Top < b.Bottom || a.Bottom > b.Top);
    }

    /// <summary>
    /// Extract text from a specific area of the page (file-based wrapper for backward compatibility)
    /// </summary>
    /// <param name="pdfPath">Path to PDF file</param>
    /// <param name="pageIndex">Zero-based page index</param>
    /// <param name="area">Selection area in rendered-page pixels/DIPs</param>
    /// <param name="renderDpi">The DPI at which the page was rendered (default 150)</param>
    public string ExtractTextFromArea(string pdfPath, int pageIndex, Rect area, int renderDpi = 150)
    {
        using var stream = File.OpenRead(pdfPath);
        return ExtractTextFromArea(stream, pageIndex, area, renderDpi, Path.GetFileName(pdfPath));
    }

    /// <summary>
    /// Extract text from a specific page-scoped area.
    /// </summary>
    public string ExtractTextFromArea(string pdfPath, int pageIndex, PdfPageRect area)
    {
        using var stream = File.OpenRead(pdfPath);
        return ExtractTextFromArea(stream, pageIndex, area, Path.GetFileName(pdfPath));
    }

    /// <summary>
    /// Check if a letter's center point is inside the selection rectangle.
    /// This provides precise character-level selection - a letter is only selected
    /// if its center point falls within the selection bounds.
    /// Issue #285: Use 50% overlap threshold to handle position differences
    /// between word bounding boxes and letter positions.
    /// </summary>
    /// <param name="letterBox">The bounding box of the letter (glyph rectangle)</param>
    /// <param name="selection">The selection rectangle in PDF coordinates</param>
    /// <returns>True if at least 50% of the letter overlaps with selection</returns>
    private static bool IsLetterCenterInSelection(PdfRectangle letterBox, PdfRectangle selection)
    {
        // Issue #285 fix: Check if at least 50% of the letter overlaps with the selection.
        // This is more forgiving than center-point but more precise than any-intersection.

        // Calculate the intersection rectangle
        var intersectLeft = Math.Max(letterBox.Left, selection.Left);
        var intersectRight = Math.Min(letterBox.Right, selection.Right);
        var intersectBottom = Math.Max(letterBox.Bottom, selection.Bottom);
        var intersectTop = Math.Min(letterBox.Top, selection.Top);

        // No intersection if any dimension is negative
        if (intersectRight <= intersectLeft || intersectTop <= intersectBottom)
            return false;

        // Calculate areas
        var letterArea = (letterBox.Right - letterBox.Left) * (letterBox.Top - letterBox.Bottom);
        var intersectArea = (intersectRight - intersectLeft) * (intersectTop - intersectBottom);

        // Select if at least 50% of the letter is inside the selection
        return intersectArea >= (letterArea * 0.5);
    }
}
