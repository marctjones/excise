using System;
using System.Collections.Generic;
using System.Globalization;
using Excise.Core.Text;

namespace Excise.App.Models;

/// <summary>
/// One embedded file, as the Attachments pane shows it (#1414, #1563).
/// </summary>
/// <param name="Name">The name-tree key, used to address the file for save.</param>
/// <param name="FileName">Name as the file declares it, preferring the Unicode <c>/UF</c> entry. Attacker-controlled: display it through <see cref="DisplayName"/> and write it through <see cref="Services.AttachmentFileNames"/>.</param>
/// <param name="Description">The <c>/Desc</c> entry, if the producer set one.</param>
/// <param name="MimeType">The <c>/EF</c> stream's <c>/Subtype</c> (e.g. ZUGFeRD's application/xml).</param>
/// <param name="SizeInBytes">
/// Decoded length, or <c>null</c> when the stream could not be decoded.
/// <c>PdfEmbeddedFile</c> exposes no size property, so this is derived from the
/// decoded bytes rather than read from <c>/Params /Size</c> — a producer's
/// declared size is not evidence of what is actually there.
/// </param>
/// <param name="ModifiedDate">The embedded file stream's <c>/Params /ModDate</c>, if present.</param>
/// <param name="PageNumber">
/// The 1-based page whose <c>/FileAttachment</c> annotation carries the file, or
/// <c>null</c> for a document-level attachment.
/// </param>
public sealed record AttachmentEntry(
    string Name,
    string FileName,
    string? Description,
    string? MimeType,
    int? SizeInBytes,
    DateTimeOffset? ModifiedDate = null,
    int? PageNumber = null)
{
    /// <summary>
    /// The file name with invisible format controls made visible. An embedded
    /// file name is chosen by whoever made the PDF, and a right-to-left override (U+202E) can make
    /// "invoice[RLO]fdp.exe" read as a PDF when it is an executable (#1205).
    /// </summary>
    public string DisplayName => UnicodeTextSafety.EscapeForDisplay(FileName);

    /// <summary>Human-readable size for the list.</summary>
    public string SizeDisplay => SizeInBytes switch
    {
        null => "unreadable",
        < 1024 => $"{SizeInBytes} B",
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{SizeInBytes / 1024.0:0.#} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{SizeInBytes / (1024.0 * 1024.0):0.#} MB"),
    };

    /// <summary>"Page 3" for an annotation attachment, empty for a document-level one.</summary>
    public string LocationDisplay => PageNumber is int page ? $"Page {page}" : string.Empty;

    /// <summary>
    /// Second line of the pane row: description, modified date and page, in
    /// that order, skipping whatever the file does not declare.
    /// </summary>
    public string DetailText
    {
        get
        {
            var parts = new List<string>(3);
            if (!string.IsNullOrWhiteSpace(Description))
                parts.Add(UnicodeTextSafety.EscapeForDisplay(Description));
            if (ModifiedDate is DateTimeOffset modified)
                parts.Add("Modified " + modified.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            if (PageNumber != null)
                parts.Add(LocationDisplay);
            return string.Join(" · ", parts);
        }
    }

    /// <summary>True when <see cref="DetailText"/> has anything to show.</summary>
    public bool HasDetail => DetailText.Length > 0;

    /// <summary>
    /// What a screen reader announces for the row: name and size first (#1563's
    /// accessibility criterion), then the page when there is one.
    /// </summary>
    public string AutomationName =>
        PageNumber is int page
            ? $"{DisplayName}, {SizeDisplay}, on page {page}"
            : $"{DisplayName}, {SizeDisplay}";

    /// <summary>Tooltip for the row: everything the file declares about itself.</summary>
    public string ToolTipText
    {
        get
        {
            var lines = new List<string>(4) { DisplayName, SizeDisplay };
            if (!string.IsNullOrWhiteSpace(MimeType))
                lines.Add(UnicodeTextSafety.EscapeForDisplay(MimeType));
            if (HasDetail)
                lines.Add(DetailText);
            return string.Join(Environment.NewLine, lines);
        }
    }
}
