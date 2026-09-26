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
}
