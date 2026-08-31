using Excise.Core.Primitives;
using Excise.Core.Security;
using Excise.Core.Writing;

namespace Excise.Core.Document;

/// <summary>
/// Writer compatibility members exposed on the document model. The partial
/// type preserves the established public API while fresh serialization remains
/// owned by <c>core-writing</c>.
/// </summary>
public partial class PdfDocument
{
    private PdfDocumentSaveLifecycle? _saveLifecycle;

    private PdfDocumentSaveLifecycle SaveLifecycle
        => _saveLifecycle ??= new PdfDocumentSaveLifecycle(
            _objectStore, Trailer, Catalog, Version);

    /// <summary>
    /// Register idempotent document finalization to run once at the start of
    /// each serialization. Font subsets, tagged structure, and PDF/A policy
    /// all enter the same lifecycle through this seam.
    /// </summary>
    internal void RegisterPreSaveAction(Action action)
        => SaveLifecycle.RegisterPreSaveAction(action);

    /// <summary>
    /// Start one writer-facing view after all registered finalizers have run.
    /// </summary>
    internal PdfDocumentSaveSession BeginSaveSession()
        => SaveLifecycle.BeginSave();

    /// <summary>
    /// Find the indirect reference of a cached object instance by identity.
    /// Tagged-PDF authoring uses this for widget annotation /OBJR entries.
    /// </summary>
    internal PdfReference? GetReferenceTo(PdfObject obj)
        => SaveLifecycle.GetReferenceTo(obj);

    /// <summary>
    /// Encryption options that re-encrypt a save of this document with the
    /// same protection its source was opened with (#643): same algorithm
    /// where the writer supports it, same <c>/P</c> permission mask, same
    /// <c>/EncryptMetadata</c> choice. Returns <c>null</c> when the source
    /// was not encrypted — so <c>doc.Save(path, doc.GetReEncryptionOptions(pw))</c>
    /// is always safe: unencrypted sources stay unencrypted.
    /// </summary>
    /// <remarks>
    /// Algorithm mapping: V=5 R=6 sources round-trip as
    /// <see cref="PdfEncryptionAlgorithm.Aes256"/>; V=4 R=4 AESV2 sources as
    /// <see cref="PdfEncryptionAlgorithm.Aes128"/>. RC4 variants and an
    /// unparseable /Encrypt dictionary upgrade to AES-256 rather than
    /// downgrading protection.
    ///
    /// The source owner password cannot be recovered from a user-password
    /// open (#324), so the returned options reuse <paramref name="userPassword"/>
    /// as the owner password. The document never retains password text.
    /// </remarks>
    public PdfEncryptionOptions? GetReEncryptionOptions(string? userPassword)
    {
        if (!IsEncrypted)
            return null;

        var securityHandler = SaveLifecycle.SecurityHandler;
        var algorithm = securityHandler switch
        {
            { V: 5, R: 6 } => PdfEncryptionAlgorithm.Aes256,
            { V: 4, R: 4, UsesAes: true } => PdfEncryptionAlgorithm.Aes128,
            _ => PdfEncryptionAlgorithm.Aes256,
        };

        return new PdfEncryptionOptions
        {
            UserPassword = userPassword,
            OwnerPassword = userPassword,
            Permissions = Permissions.RawValue,
            EncryptMetadata = securityHandler?.EncryptMetadata ?? true,
            Algorithm = algorithm,
        };
    }

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
        var writer = new PdfDocumentWriter(this, encryptionOptions);
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
