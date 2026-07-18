using Microsoft.Extensions.Logging;
using Excise.Core.Document;
using Excise.Core.Graphics;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Excise.App.Services;

/// <summary>
/// Service for applying Bates numbering to PDF documents
/// </summary>
public class BatesNumberingService
{
    private readonly ILogger<BatesNumberingService> _logger;

    public BatesNumberingService(ILogger<BatesNumberingService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Apply Bates numbers to a document
    /// </summary>
    public void ApplyBatesNumbers(Excise.Core.Document.PdfDocument document, BatesOptions options)
    {
        _logger.LogInformation(
            "Applying Bates numbers: Prefix={Prefix}, Start={Start}, Digits={Digits}, Position={Position}",
            options.Prefix, options.StartNumber, options.NumberOfDigits, options.Position);

        var currentNumber = options.StartNumber;

        for (int i = 0; i < document.Pages.Count; i++)
        {
            var page = document.Pages[i];
            var batesNumber = FormatBatesNumber(currentNumber, options);

            ApplyBatesNumberToPage(page, batesNumber, options);
            currentNumber++;
        }

        _logger.LogInformation("Applied Bates numbers {Start} to {End}",
            FormatBatesNumber(options.StartNumber, options),
            FormatBatesNumber(currentNumber - 1, options));
    }

    /// <summary>
    /// Apply Bates numbers across multiple documents, maintaining sequence
    /// </summary>
    public BatesResult ApplyBatesNumbersToSet(
        IEnumerable<string> filePaths,
        BatesOptions options)
    {
        var result = new BatesResult();
        var files = filePaths.ToList();
        var currentNumber = options.StartNumber;
        var processedFiles = new HashSet<string>(); // Track processed files to avoid duplicates

        _logger.LogInformation("Applying Bates numbers to {Count} documents starting at {Start}",
            files.Count, options.StartNumber);

        foreach (var filePath in files)
        {
            // Skip if already processed (defensive check)
            if (processedFiles.Contains(filePath))
            {
                _logger.LogWarning("Skipping duplicate file: {File}", filePath);
                continue;
            }
            processedFiles.Add(filePath);

            try
            {
                var docResult = new BatesDocumentResult
                {
                    FilePath = filePath,
                    FileName = Path.GetFileName(filePath),
                    FirstBatesNumber = FormatBatesNumber(currentNumber, options)
                };

                int pageCount;
                var outputPath = GenerateOutputPath(filePath, options.OutputDirectory, options.OutputSuffix);

                // Use explicit using block for proper disposal
                using (var document = Excise.Core.Document.PdfDocument.Open(filePath))
                {
                    pageCount = document.Pages.Count;

                    for (int i = 0; i < pageCount; i++)
                    {
                        var page = document.Pages[i];
                        var batesNumber = FormatBatesNumber(currentNumber, options);

                        ApplyBatesNumberToPage(page, batesNumber, options);
                        currentNumber++;
                    }

                    // Save the document before disposal
                    document.Save(outputPath);
                }

                docResult.LastBatesNumber = FormatBatesNumber(currentNumber - 1, options);
                docResult.PageCount = pageCount;
                docResult.Success = true;
                docResult.OutputPath = outputPath;

                result.Documents.Add(docResult);
                result.TotalPages += pageCount;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply Bates numbers to {File}", filePath);
                result.Documents.Add(new BatesDocumentResult
                {
                    FilePath = filePath,
                    FileName = Path.GetFileName(filePath),
                    Success = false,
                    ErrorMessage = ex.Message
                });
            }
        }

        result.FirstBatesNumber = FormatBatesNumber(options.StartNumber, options);
        result.LastBatesNumber = FormatBatesNumber(currentNumber - 1, options);
        result.NextBatesNumber = currentNumber;

        _logger.LogInformation(
            "Bates numbering complete. Range: {First} to {Last}, Total pages: {Pages}",
            result.FirstBatesNumber, result.LastBatesNumber, result.TotalPages);

        return result;
    }

    /// <summary>
    /// Get the next Bates number after numbering a set of documents
    /// </summary>
    public int CalculateNextNumber(IEnumerable<string> filePaths, int startNumber)
    {
        int totalPages = 0;
        foreach (var filePath in filePaths)
        {
            try
            {
                using var document = Excise.Core.Document.PdfDocument.Open(filePath);
                totalPages += document.Pages.Count;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not count pages in {File}", filePath);
            }
        }
        return startNumber + totalPages;
    }

    private void ApplyBatesNumberToPage(PdfPage page, string batesNumber, BatesOptions options)
    {
        using var gfx = page.GetGraphics();

        // Create font - map font name to standard PDF font
        var font = MapToStandardFont(options.FontName, options.FontSize);
        var brush = PdfBrush.Black;

        // Measure text
        var textSize = PdfGraphics.MeasureString(batesNumber, font);

        // Calculate position
        var (x, y) = CalculatePosition(page, textSize, options);

        // Draw the Bates number
        gfx.DrawString(batesNumber, font, brush, x, y);
    }

    /// <summary>
    /// Maps a font name to a standard PDF font.
    /// </summary>
    private static PdfFont MapToStandardFont(string fontName, double fontSize)
    {
        // Map common font names to standard PDF fonts
        return fontName.ToLowerInvariant() switch
        {
            "arial" or "helvetica" or "sans-serif" => PdfFont.Helvetica(fontSize),
            "arial bold" or "helvetica bold" or "helvetica-bold" => PdfFont.HelveticaBold(fontSize),
            "times" or "times new roman" or "times-roman" or "serif" => PdfFont.TimesRoman(fontSize),
            "times bold" or "times new roman bold" => PdfFont.TimesBold(fontSize),
            "courier" or "courier new" or "monospace" => PdfFont.Courier(fontSize),
            "courier bold" => PdfFont.CourierBold(fontSize),
            _ => PdfFont.Helvetica(fontSize) // Default to Helvetica
        };
    }

    private (double x, double y) CalculatePosition(PdfPage page, PdfSize textSize, BatesOptions options)
    {
        double x, y;
        var pageWidth = page.Width;
        var pageHeight = page.Height;

        switch (options.Position)
        {
            case BatesPosition.TopLeft:
                x = options.MarginX;
                y = options.MarginY + textSize.Height;
                break;

            case BatesPosition.TopCenter:
                x = (pageWidth - textSize.Width) / 2;
                y = options.MarginY + textSize.Height;
                break;

            case BatesPosition.TopRight:
                x = pageWidth - textSize.Width - options.MarginX;
                y = options.MarginY + textSize.Height;
                break;

            case BatesPosition.BottomLeft:
                x = options.MarginX;
                y = pageHeight - options.MarginY;
                break;

            case BatesPosition.BottomCenter:
                x = (pageWidth - textSize.Width) / 2;
                y = pageHeight - options.MarginY;
                break;

            case BatesPosition.BottomRight:
            default:
                x = pageWidth - textSize.Width - options.MarginX;
                y = pageHeight - options.MarginY;
                break;
        }

        return (x, y);
    }

    private string FormatBatesNumber(int number, BatesOptions options)
    {
        var numberPart = number.ToString().PadLeft(options.NumberOfDigits, '0');
        return $"{options.Prefix}{numberPart}{options.Suffix}";
    }

    private string GenerateOutputPath(string inputPath, string? outputDirectory, string outputSuffix)
    {
        var directory = outputDirectory ?? Path.GetDirectoryName(inputPath) ?? ".";
        var fileName = Path.GetFileNameWithoutExtension(inputPath);
        var extension = Path.GetExtension(inputPath);

        return Path.Combine(directory, $"{fileName}{outputSuffix}{extension}");
    }
}

/// <summary>
/// Options for Bates numbering
/// </summary>
public class BatesOptions
{
    /// <summary>
    /// Prefix before the number (e.g., "DOE" for DOE000001)
    /// </summary>
    public string Prefix { get; set; } = "";

    /// <summary>
    /// Suffix after the number (e.g., "-CONF" for DOE000001-CONF)
    /// </summary>
    public string Suffix { get; set; } = "";

    /// <summary>
    /// Starting number
    /// </summary>
    public int StartNumber { get; set; } = 1;

    /// <summary>
    /// Minimum number of digits (will pad with zeros)
    /// </summary>
    public int NumberOfDigits { get; set; } = 6;

    /// <summary>
    /// Position on the page
    /// </summary>
    public BatesPosition Position { get; set; } = BatesPosition.BottomRight;

    /// <summary>
    /// Font name
    /// </summary>
    public string FontName { get; set; } = "Arial";

    /// <summary>
    /// Font size in points
    /// </summary>
    public double FontSize { get; set; } = 10;

    /// <summary>
    /// Horizontal margin from page edge
    /// </summary>
    public double MarginX { get; set; } = 36; // 0.5 inch

    /// <summary>
    /// Vertical margin from page edge
    /// </summary>
    public double MarginY { get; set; } = 36; // 0.5 inch

    /// <summary>
    /// Output directory for batch processing (null = same directory)
    /// </summary>
    public string? OutputDirectory { get; set; }

    /// <summary>
    /// Suffix to add to output filename (e.g., "_bates")
    /// </summary>
    public string OutputSuffix { get; set; } = "_bates";
}

/// <summary>
/// Position options for Bates number placement
/// </summary>
public enum BatesPosition
{
    TopLeft,
    TopCenter,
    TopRight,
    BottomLeft,
    BottomCenter,
    BottomRight
}

/// <summary>
/// Result of Bates numbering operation
/// </summary>
public class BatesResult
{
    public List<BatesDocumentResult> Documents { get; set; } = new();
    public string FirstBatesNumber { get; set; } = "";
    public string LastBatesNumber { get; set; } = "";
    public int NextBatesNumber { get; set; }
    public int TotalPages { get; set; }
}

/// <summary>
/// Result for a single document in Bates numbering
/// </summary>
public class BatesDocumentResult
{
    public string FilePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public string OutputPath { get; set; } = "";
    public string FirstBatesNumber { get; set; } = "";
    public string LastBatesNumber { get; set; } = "";
    public int PageCount { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}
