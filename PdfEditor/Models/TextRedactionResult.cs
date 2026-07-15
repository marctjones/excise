using System.Collections.Generic;

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
    /// <summary>
    /// Non-blocking caveats about this redaction — currently only the
    /// extraction-confidence check (#650): a degraded or unverified result
    /// against an independent oracle. Empty, never null, on every result.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = System.Array.Empty<string>();

    public static TextRedactionResult Succeeded(int count, IReadOnlyList<string>? warnings = null) =>
        new(true, count) { Warnings = warnings ?? System.Array.Empty<string>() };

    public static TextRedactionResult Failed(string error) => new(false, 0, error);
}
