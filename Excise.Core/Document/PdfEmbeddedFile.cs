using Excise.Core.Primitives;

namespace Excise.Core.Document;

/// <summary>
/// Represents a document-level embedded file (PDF 2.0 §7.7).
/// This is the file specification for files referenced in /Catalog/Names/EmbeddedFiles
/// (portfolio) or /Catalog/Names/AF (associated files).
/// </summary>
public sealed record PdfEmbeddedFile
{
    /// <summary>
    /// The entry name in the embedded files name tree.
    /// This is the key under which the file is registered.
    /// </summary>
    public string Name { get; init; }

    /// <summary>
    /// The file name, preferring /UF (Unicode) over /F (legacy 7-bit/PDFDocEncoded).
    /// May be null if the file specification lacks both /UF and /F entries.
    /// </summary>
    public string? FileName { get; init; }

    /// <summary>
    /// Optional description of the embedded file (/Desc entry).
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// The decoded file bytes from /EF stream entry.
    /// May be null if the stream could not be decoded or the /EF entry is missing.
    /// </summary>
    public byte[]? Bytes { get; init; }

    /// <summary>
    /// Optional MIME type from /EF stream /Subtype entry.
    /// For PDF 2.0, typical values include "application/xml" for ZUGFeRD/Factur-X,
    /// "text/plain", "application/pdf", etc.
    /// </summary>
    public string? MimeType { get; init; }

    /// <summary>
    /// Optional creation date from /EF stream /Params /CreationDate entry.
    /// </summary>
    public DateTimeOffset? CreationDate { get; init; }

    /// <summary>
    /// Optional modification date from /EF stream /Params /ModDate entry.
    /// </summary>
    public DateTimeOffset? ModDate { get; init; }

    /// <summary>
    /// The raw file specification dictionary for this embedded file.
    /// Provides access to any extended attributes not captured in the record properties.
    /// </summary>
    public PdfDictionary RawDictionary { get; init; }

    /// <summary>
    /// Create a new embedded file record.
    /// </summary>
    public PdfEmbeddedFile(
        string name,
        string? fileName,
        string? description,
        byte[]? bytes,
        string? mimeType,
        DateTimeOffset? creationDate,
        DateTimeOffset? modDate,
        PdfDictionary rawDictionary)
    {
        Name = name;
        FileName = fileName;
        Description = description;
        Bytes = bytes;
        MimeType = mimeType;
        CreationDate = creationDate;
        ModDate = modDate;
        RawDictionary = rawDictionary;
    }
}
