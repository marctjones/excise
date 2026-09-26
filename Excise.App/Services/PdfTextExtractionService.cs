using Microsoft.Extensions.Logging;
using Excise.Core.Document;
using System;
using System.IO;
using System.Text;

namespace Excise.App.Services;

/// <summary>
/// Service for extracting text from PDF pages
/// Uses Excise.Core for text extraction
/// </summary>
internal class PdfTextExtractionService
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
            using var document = PdfDocument.Open(pdfStream);

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
}
