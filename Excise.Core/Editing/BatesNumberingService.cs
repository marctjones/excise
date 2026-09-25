using Excise.Core.Document;
using Excise.Core.Graphics;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Excise.Core.Editing;

/// <summary>
/// Service for applying Bates numbering to PDF documents
/// </summary>
public class BatesNumberingService
{
    private readonly Action<string>? _diagnostics;

    /// <param name="diagnostics">Optional sink for progress and warning messages.</param>
    public BatesNumberingService(Action<string>? diagnostics = null)
    {
        _diagnostics = diagnostics;
    }

    /// <summary>
    /// Apply Bates numbers to a document
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The prefix or suffix has a character the stamp font cannot represent
    /// (#1671). Thrown before any page is touched — the alternative is a '?' in
    /// every Bates number of an evidence set.
    /// </exception>
    public void ApplyBatesNumbers(Excise.Core.Document.PdfDocument document, BatesOptions options)
    {
        EnsurePrefixAndSuffixDrawable(options);

        _diagnostics?.Invoke(
            $"Applying Bates numbers: Prefix={options.Prefix}, Start={options.StartNumber}, Digits={options.NumberOfDigits}, Position={options.Position}");

        var currentNumber = options.StartNumber;

        for (int i = 0; i < document.Pages.Count; i++)
        {
            var page = document.Pages[i];
            var batesNumber = FormatBatesNumber(currentNumber, options);

            ApplyBatesNumberToPage(page, batesNumber, options);
            currentNumber++;
        }

        _diagnostics?.Invoke(
            $"Applied Bates numbers {FormatBatesNumber(options.StartNumber, options)} to {FormatBatesNumber(currentNumber - 1, options)}");
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

        _diagnostics?.Invoke($"Applying Bates numbers to {files.Count} documents starting at {options.StartNumber}");

        foreach (var filePath in files)
        {
            // Skip if already processed (defensive check)
            if (processedFiles.Contains(filePath))
            {
                _diagnostics?.Invoke($"Skipping duplicate file: {filePath}");
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
                _diagnostics?.Invoke($"Failed to apply Bates numbers to {filePath}: {ex}");
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

        _diagnostics?.Invoke(
            $"Bates numbering complete. Range: {result.FirstBatesNumber} to {result.LastBatesNumber}, Total pages: {result.TotalPages}");

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
                _diagnostics?.Invoke($"Could not count pages in {filePath}: {ex}");
            }
        }
        return startNumber + totalPages;
    }

    private static void EnsurePrefixAndSuffixDrawable(BatesOptions options)
        => MapToStandardFont(options.FontName, options.FontSize)
            .EnsureCanEncode(options.Prefix + options.Suffix, "the Bates prefix and suffix");

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

    /// <summary>
    /// Baseline origin of the stamp, in the y-UP page space <c>DrawString</c> takes.
    /// </summary>
    /// <remarks>
    /// The vertical maths used to treat y as measured DOWN from the top of the page, so
    /// "Bottom right" stamped the top edge and "Top left" the bottom (measured with
    /// Poppler's <c>pdftotext -bbox</c>; the text-only tests could not see it). A Bates
    /// number is cited by where it sits, and BottomRight is the convention for a
    /// production, so a stamp on the wrong edge is a wrong evidence set. Here the
    /// bottom margin is the baseline itself and the top margin is taken off the cap
    /// height below the page's top edge.
    /// </remarks>
    private (double x, double y) CalculatePosition(PdfPage page, PdfSize textSize, BatesOptions options)
    {
        var pageWidth = page.Width;
        var pageHeight = page.Height;

        var x = options.Position switch
        {
            BatesPosition.TopLeft or BatesPosition.BottomLeft => options.MarginX,
            BatesPosition.TopCenter or BatesPosition.BottomCenter => (pageWidth - textSize.Width) / 2,
            _ => pageWidth - textSize.Width - options.MarginX,
        };

        var y = options.Position switch
        {
            BatesPosition.TopLeft or BatesPosition.TopCenter or BatesPosition.TopRight
                => pageHeight - options.MarginY - textSize.Height,
            _ => options.MarginY,
        };

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
