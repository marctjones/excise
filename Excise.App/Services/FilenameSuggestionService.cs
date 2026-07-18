using System;
using System.IO;

namespace Excise.App.Services;

/// <summary>
/// Suggests filenames for saved PDF operations (redactions, page extractions, etc.)
/// </summary>
public class FilenameSuggestionService
{
    /// <summary>
    /// Suggest a filename for a redacted version of a PDF
    /// </summary>
    /// <param name="originalPath">Path to the original PDF file</param>
    /// <returns>Suggested path with "_REDACTED" appended before extension</returns>
    /// <example>
    /// "C:\docs\contract.pdf" → "C:\docs\contract_REDACTED.pdf"
    /// </example>
    public string SuggestRedactedFilename(string originalPath)
    {
        if (string.IsNullOrEmpty(originalPath))
            throw new ArgumentException("Original path cannot be empty", nameof(originalPath));

        var name = Path.GetFileNameWithoutExtension(originalPath);
        var ext = Path.GetExtension(originalPath);
        var dir = Path.GetDirectoryName(originalPath) ?? string.Empty;

        var suggestedFilename = $"{name}_REDACTED{ext}";
        return string.IsNullOrEmpty(dir) ? suggestedFilename : Path.Combine(dir, suggestedFilename);
    }

    /// <summary>
    /// Suggest a filename for a subset of pages from a PDF
    /// </summary>
    /// <param name="originalPath">Path to the original PDF file</param>
    /// <param name="pageRange">Page range description (e.g., "1-5", "3,7,9")</param>
    /// <returns>Suggested path with page range in filename</returns>
    /// <example>
    /// "C:\docs\contract.pdf" + "1-5" → "C:\docs\contract_pages_1-5.pdf"
    /// </example>
    public string SuggestPageSubsetFilename(string originalPath, string pageRange)
    {
        if (string.IsNullOrEmpty(originalPath))
            throw new ArgumentException("Original path cannot be empty", nameof(originalPath));

        if (string.IsNullOrEmpty(pageRange))
            throw new ArgumentException("Page range cannot be empty", nameof(pageRange));

        var name = Path.GetFileNameWithoutExtension(originalPath);
        var ext = Path.GetExtension(originalPath);
        var dir = Path.GetDirectoryName(originalPath) ?? string.Empty;

        var suggestedFilename = $"{name}_pages_{pageRange}{ext}";
        return string.IsNullOrEmpty(dir) ? suggestedFilename : Path.Combine(dir, suggestedFilename);
    }

    /// <summary>
    /// Add auto-increment number to filename if file already exists
    /// </summary>
    /// <param name="path">Proposed file path</param>
    /// <returns>
    /// Original path if file doesn't exist, otherwise path with "_2", "_3", etc.
    /// </returns>
    /// <example>
    /// If "contract_REDACTED.pdf" exists:
    /// "contract_REDACTED.pdf" → "contract_REDACTED_2.pdf"
    /// </example>
    public string SuggestWithAutoIncrement(string path)
    {
        if (string.IsNullOrEmpty(path))
            throw new ArgumentException("Path cannot be empty", nameof(path));

        if (!File.Exists(path))
            return path;

        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        var dir = Path.GetDirectoryName(path) ?? string.Empty;

        int counter = 2;
        string newPath;
        do
        {
            var newFilename = $"{name}_{counter}{ext}";
            newPath = string.IsNullOrEmpty(dir) ? newFilename : Path.Combine(dir, newFilename);
            counter++;
        }
        while (File.Exists(newPath) && counter < 1000); // Prevent infinite loop

        return newPath;
    }

    /// <summary>
    /// Suggest a safe filename that doesn't overwrite existing files
    /// Combines SuggestRedactedFilename with auto-increment
    /// </summary>
    /// <param name="originalPath">Path to the original PDF file</param>
    /// <returns>Safe filename that won't overwrite existing files</returns>
    public string SuggestSafeRedactedFilename(string originalPath)
    {
        var suggested = SuggestRedactedFilename(originalPath);
        return SuggestWithAutoIncrement(suggested);
    }
}
