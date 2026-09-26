namespace Excise.Core.Document;

/// <summary>Options for <see cref="PdfDocument.Open(string, PdfOpenOptions?)"/> and its stream and byte-array siblings.</summary>
public sealed record PdfOpenOptions
{
    /// <summary>User password. <c>null</c> is treated as the empty password.</summary>
    public string? UserPassword { get; init; }

    /// <summary>
    /// When false (default), an encrypted PDF that no security handler can decrypt
    /// (unsupported handler, wrong password, missing or unreadable /Encrypt) throws
    /// <see cref="Excise.Core.Parsing.PdfEncryptionNotSupportedException"/>: its streams
    /// would read as ciphertext, and extraction and redaction would find nothing and
    /// report success. When true it opens for inspection with ciphertext streams and
    /// <see cref="PdfDocument.IsDecrypting"/> false.
    /// </summary>
    public bool AllowEncrypted { get; init; }

    /// <summary>
    /// Whether the document disposes a <see cref="Stream"/> source on close. Ignored for
    /// path and byte-array sources: the document always owns the stream it creates.
    /// </summary>
    public bool OwnsStream { get; init; }
}
