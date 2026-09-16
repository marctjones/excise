using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Excise.App.Services.Host;

/// <summary>
/// One file type offered by a picker. <paramref name="MimeTypes"/> is carried
/// because the redacted-copy save dialog sets it and dropping it would change
/// what the OS panel accepts.
/// </summary>
internal sealed record FilePickerFilter(
    string Name,
    IReadOnlyList<string> Patterns,
    IReadOnlyList<string>? MimeTypes = null);

/// <summary>
/// The file types the app offers, in one place, so two call sites cannot
/// drift into offering different patterns for the same thing.
/// </summary>
internal static class FilePickerFilters
{
    internal static FilePickerFilter Pdf { get; } =
        new("PDF Files", new[] { "*.pdf" });

    /// <summary>
    /// The redacted-copy save dialog's filter: a different display name and an
    /// explicit MIME type, both preserved verbatim from the pre-#1500 dialog.
    /// </summary>
    internal static FilePickerFilter PdfDocumentWithMimeType { get; } =
        new("PDF Document", new[] { "*.pdf" }, new[] { "application/pdf" });

    internal static FilePickerFilter Png { get; } =
        new("PNG Image", new[] { "*.png" });

    internal static FilePickerFilter Jpeg { get; } =
        new("JPEG Image", new[] { "*.jpg", "*.jpeg" });

    internal static FilePickerFilter Images { get; } =
        new("Images", new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp" });

    internal static FilePickerFilter Pkcs12Certificate { get; } =
        new("PKCS#12 certificate", new[] { "*.p12", "*.pfx" });
}

/// <summary>Request for the "open file(s)" picker.</summary>
internal sealed record OpenFilesRequest
{
    public required string Title { get; init; }

    public bool AllowMultiple { get; init; }

    /// <summary>Empty means an unfiltered picker.</summary>
    public IReadOnlyList<FilePickerFilter> Filters { get; init; } = Array.Empty<FilePickerFilter>();
}

/// <summary>Request for the "save file as" picker.</summary>
internal sealed record SaveFileRequest
{
    public required string Title { get; init; }

    /// <summary>
    /// Null means "do not force an extension". Load-bearing for attachments: an
    /// embedded file is arbitrary content, and renaming a ZUGFeRD invoice to
    /// <c>.pdf</c> on the way out would be a corruption we invented.
    /// </summary>
    public string? DefaultExtension { get; init; }

    public string? SuggestedFileName { get; init; }

    /// <summary>Empty means an unfiltered picker.</summary>
    public IReadOnlyList<FilePickerFilter> Filters { get; init; } = Array.Empty<FilePickerFilter>();

    /// <summary>
    /// Where the panel opens. Best effort: a directory that does not exist, or
    /// a platform that cannot honour it, is ignored rather than failing the
    /// pick.
    /// </summary>
    public string? SuggestedStartDirectory { get; init; }
}

/// <summary>
/// File and folder pickers as a dependency (#1500 step 1), so a view model
/// neither builds Avalonia <c>FilePicker*Options</c> nor resolves a storage
/// provider of its own.
/// </summary>
/// <remarks>
/// Every method returns local filesystem paths, never Avalonia storage items:
/// a cancelled or unavailable picker yields an empty list / null, which is
/// exactly what the pre-#1500 call sites produced.
/// </remarks>
internal interface IFilePicker
{
    /// <summary>The chosen local paths; empty if cancelled or unavailable.</summary>
    Task<IReadOnlyList<string>> OpenFilesAsync(OpenFilesRequest request);

    /// <summary>The chosen local path; null if cancelled or unavailable.</summary>
    Task<string?> SaveFileAsync(SaveFileRequest request);

    /// <summary>The chosen folder's local path; null if cancelled or unavailable.</summary>
    Task<string?> PickFolderAsync(string title);
}
