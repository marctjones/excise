namespace Pdfe.Core.Security;

/// <summary>
/// Standard Security Handler algorithm variant to encrypt with.
/// </summary>
/// <remarks>
/// Only <see cref="Aes256"/> (V=5 R=6, the PDF 2.0 native algorithm) is
/// implemented today (issue #639). <see cref="Aes128"/> is reserved for
/// issue #640 (V=4 R=4, CFM=AESV2) so callers/serialized options written
/// against this version keep compiling once that lands — selecting it
/// before #640 ships throws <see cref="System.NotSupportedException"/>
/// from <see cref="Pdfe.Core.Writing.PdfDocumentWriter"/>.
/// </remarks>
public enum PdfEncryptionAlgorithm
{
    /// <summary>AES-256 in CBC mode, V=5 R=6 (PDF 2.0 native). Implemented.</summary>
    Aes256 = 0,

    /// <summary>AES-128 in CBC mode, V=4 R=4 (CFM=AESV2). Reserved for issue #640 — not yet implemented.</summary>
    Aes128 = 1,
}

/// <summary>
/// Encryption intent for <see cref="Pdfe.Core.Writing.PdfDocumentWriter"/>:
/// the user/owner passwords, permission bitmask, and algorithm to encrypt
/// a saved document with, per the PDF Standard Security Handler (ISO
/// 32000-2 §7.6).
/// </summary>
/// <remarks>
/// This type intentionally carries only what the writer needs to produce a
/// spec-correct <c>/Encrypt</c> dictionary — it does not enforce permissions
/// (issue #642's scope), manage password prompts (issue #641's scope), or
/// preserve encryption across redaction/edit round-trips (issue #643's
/// scope). Those issues build their surfaces on top of this one without
/// needing to change its shape.
/// </remarks>
public sealed class PdfEncryptionOptions
{
    /// <summary>
    /// The user (open) password. <c>null</c> or empty means an empty user
    /// password — the common "encrypted, but no prompt to open" case.
    /// Encoded as UTF-8 for R=6 (matches the decrypt handler's
    /// <c>EncodeUserPasswordCandidates</c> for revision &gt;= 5).
    /// </summary>
    public string? UserPassword { get; init; }

    /// <summary>
    /// The owner (permissions) password. <c>null</c> or empty means an
    /// empty owner password. Per spec an empty owner password paired with
    /// a non-empty user password is unusual but valid — this is not
    /// required to be non-empty.
    /// </summary>
    public string? OwnerPassword { get; init; }

    /// <summary>
    /// The <c>/P</c> permission bitmask (ISO 32000-2 Table 22), a signed
    /// 32-bit integer. Plumbed through structurally and stored correctly
    /// in <c>/P</c> and the encrypted <c>/Perms</c> field; pdfe does not
    /// yet enforce these permissions on read (issue #642). Default (-4,
    /// i.e. 0xFFFFFFFC) grants every permission bit while keeping the two
    /// reserved low-order bits zero, matching the conventional
    /// "no restrictions" default used by other PDF libraries.
    /// </summary>
    public long Permissions { get; init; } = -4;

    /// <summary>
    /// Whether document metadata (the XMP <c>/Metadata</c> stream) is
    /// covered by encryption. Stored in <c>/EncryptMetadata</c> and the
    /// encrypted <c>/Perms</c> field. Default <c>true</c> (encrypt
    /// everything, including metadata).
    /// </summary>
    public bool EncryptMetadata { get; init; } = true;

    /// <summary>
    /// The Standard Security Handler algorithm variant. Only
    /// <see cref="PdfEncryptionAlgorithm.Aes256"/> is implemented; see the
    /// enum's remarks.
    /// </summary>
    public PdfEncryptionAlgorithm Algorithm { get; init; } = PdfEncryptionAlgorithm.Aes256;
}
