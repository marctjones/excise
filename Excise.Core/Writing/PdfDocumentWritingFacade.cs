using Excise.Core.Security;

namespace Excise.Core.Document;

/// <summary>
/// Writer compatibility members exposed on the document model. The partial
/// type preserves the established public API while fresh serialization remains
/// owned by <c>core-writing</c>.
/// </summary>
public partial class PdfDocument
{
    /// <summary>
    /// Save the document to a stream. Writes an unencrypted file — even when
    /// the source was opened encrypted (see <see cref="IsEncrypted"/>). To
    /// keep an encrypted source encrypted, pass
    /// <see cref="GetReEncryptionOptions"/>'s result to
    /// <see cref="Save(Stream, PdfEncryptionOptions?)"/> (#643).
    /// The plaintext default is deliberate: dozens of flows (rendering,
    /// splitting, extraction) rely on "save = decrypt" being explicit, so
    /// nothing re-encrypts by surprise.
    /// </summary>
    public void Save(Stream outputStream) => Save(outputStream, encryptionOptions: null);

    /// <summary>
    /// Save the document to a stream, optionally encrypting the output with
    /// the PDF Standard Security Handler. <paramref name="encryptionOptions"/>
    /// of <c>null</c> writes plaintext (identical to <see cref="Save(Stream)"/>).
    /// Combine with <see cref="GetReEncryptionOptions"/> to preserve an
    /// encrypted source's protection across a redact/edit round-trip (#643).
    /// </summary>
    public void Save(Stream outputStream, PdfEncryptionOptions? encryptionOptions)
    {
        foreach (var action in _preSaveActions)
            action();
        var writer = new Excise.Core.Writing.PdfDocumentWriter(this, encryptionOptions);
        writer.Write(outputStream);
    }

    /// <summary>
    /// Save the document to a byte array. Plaintext output — see
    /// <see cref="Save(Stream)"/>'s remarks.
    /// </summary>
    public byte[] SaveToBytes() => SaveToBytes(encryptionOptions: null);

    /// <summary>
    /// Save the document to a byte array, optionally encrypted — see
    /// <see cref="Save(Stream, PdfEncryptionOptions?)"/>.
    /// </summary>
    public byte[] SaveToBytes(PdfEncryptionOptions? encryptionOptions)
    {
        using var ms = new MemoryStream();
        Save(ms, encryptionOptions);
        return ms.ToArray();
    }

    /// <summary>
    /// Save the document to a file. Plaintext output — see
    /// <see cref="Save(Stream)"/>'s remarks.
    /// </summary>
    public void Save(string path) => Save(path, encryptionOptions: null);

    /// <summary>
    /// Save the document to a file, optionally encrypted — see
    /// <see cref="Save(Stream, PdfEncryptionOptions?)"/>.
    /// </summary>
    public void Save(string path, PdfEncryptionOptions? encryptionOptions)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        Save(fs, encryptionOptions);
    }
}
