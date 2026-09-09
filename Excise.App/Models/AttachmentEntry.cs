using System;

namespace Excise.App.Models;

/// <summary>
/// One embedded file, as the attachments panel shows it (#1414).
/// </summary>
/// <param name="Name">The name-tree key, used to address the file for save.</param>
/// <param name="FileName">Display name, preferring the Unicode <c>/UF</c> entry.</param>
/// <param name="Description">The <c>/Desc</c> entry, if the producer set one.</param>
/// <param name="MimeType">The <c>/EF</c> stream's <c>/Subtype</c> (e.g. ZUGFeRD's application/xml).</param>
/// <param name="SizeInBytes">
/// Decoded length, or <c>null</c> when the stream could not be decoded.
/// <c>PdfEmbeddedFile</c> exposes no size property, so this is derived from the
/// decoded bytes rather than read from <c>/Params /Size</c> — a producer's
/// declared size is not evidence of what is actually there.
/// </param>
public sealed record AttachmentEntry(
    string Name,
    string FileName,
    string? Description,
    string? MimeType,
    int? SizeInBytes)
{
    /// <summary>Human-readable size for the list.</summary>
    public string SizeDisplay => SizeInBytes switch
    {
        null => "unreadable",
        < 1024 => $"{SizeInBytes} B",
        < 1024 * 1024 => $"{SizeInBytes / 1024.0:0.#} KB",
        _ => $"{SizeInBytes / (1024.0 * 1024.0):0.#} MB",
    };

    /// <summary>Single line for the list row.</summary>
    public string DisplayText =>
        string.IsNullOrWhiteSpace(Description)
            ? $"{FileName}  ({SizeDisplay})"
            : $"{FileName}  ({SizeDisplay}) — {Description}";
}
