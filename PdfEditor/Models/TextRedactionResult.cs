namespace PdfEditor.Models;

/// <summary>
/// Outcome of a scripted text-redaction run (file-to-file).
/// </summary>
/// <remarks>
/// Returned by <see cref="Services.RedactionService"/>'s text-search
/// redaction API. Area-click redaction mutates the in-memory document and
/// records removed terms on the service; this file-to-file result is just
/// pass/fail plus a match count for the scripting surface.
/// </remarks>
public sealed record TextRedactionResult(
    bool Success,
    int RedactionCount,
    string? ErrorMessage = null)
{
    public static TextRedactionResult Succeeded(int count) => new(true, count);
    public static TextRedactionResult Failed(string error) => new(false, 0, error);
}
