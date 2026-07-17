using System.Linq;
using System.Security.Cryptography;

namespace Pdfe.Core.Security;

/// <summary>
/// Writer-side counterpart of <see cref="PdfStandardSecurityHandler"/>.
/// Supports two Standard Security Handler variants:
///
/// <list type="bullet">
/// <item><description>V=5 R=6 (AES-256, PDF 2.0 native — issue #639).
/// Implements ISO 32000-2 §7.6.4.4 Algorithm 8 (compute <c>/U</c> and
/// <c>/UE</c>), Algorithm 9 (compute <c>/O</c> and <c>/OE</c>), and
/// Algorithm 10 (compute <c>/Perms</c>) — the exact inverse of
/// <see cref="PdfStandardSecurityHandler.BuildR6"/>. Built via
/// <see cref="CreateR6"/>, encrypted via <see cref="EncryptBytes"/>.</description></item>
/// <item><description>V=4 R=4 (AES-128, CFM=AESV2 — issue #640). Implements
/// ISO 32000-1 §7.6.3.3 Algorithm 3 (compute <c>/O</c>) and Algorithm 5
/// (compute <c>/U</c>) — the exact inverse of the R2-R4 KDF path in
/// <see cref="PdfStandardSecurityHandler.Build(Pdfe.Core.Primitives.PdfDictionary,byte[],byte[])"/>.
/// Built via <see cref="CreateR4"/>, encrypted via
/// <see cref="EncryptObjectBytes"/>.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// The two variants differ structurally, not just numerically: per spec
/// §7.6.3.1, V=5 uses the file encryption key directly as the AES-256
/// cipher key for every object (no Algorithm 1 per-object derivation), so
/// <see cref="EncryptBytes"/> takes no object number. V=4/R=4 (like every
/// R&lt;=4 revision) DOES apply Algorithm 1 per object — every
/// stream/string gets its own derived key — so <see cref="EncryptObjectBytes"/>
/// takes <c>(objNum, gen, plaintext)</c>. <see cref="R"/> tags which shape
/// an instance was built for; the two encrypt methods each guard against
/// being called on the wrong one.
/// </remarks>
internal sealed class PdfStandardSecurityEncryptor
{
    /// <summary>The random file encryption key (32 bytes for R=6, 16 bytes for R=4). Never written to the file directly.</summary>
    public byte[] FileKey { get; }

    /// <summary>/U value: 48 bytes for R=6 (hash||validation salt||key salt), 32 bytes for R=4 (Algorithm 5).</summary>
    public byte[] U { get; }

    /// <summary>/O value: 48 bytes for R=6 (chains through /U, Algorithm 9), 32 bytes for R=4 (Algorithm 3).</summary>
    public byte[] O { get; }

    /// <summary>32-byte /UE value: file key, AES-256-CBC encrypted under a key derived from the user password. R=6 only; null for R=4 (no /UE in the V=4 dict).</summary>
    public byte[]? UE { get; }

    /// <summary>32-byte /OE value: file key, AES-256-CBC encrypted under a key derived from the owner password. R=6 only; null for R=4 (no /OE in the V=4 dict).</summary>
    public byte[]? OE { get; }

    /// <summary>16-byte /Perms value: encrypted permissions (Algorithm 10). R=6 only; null for R=4 (no /Perms in the V=4 dict).</summary>
    public byte[]? Perms { get; }

    /// <summary>Standard security handler revision this instance was built for (4 or 6) — determines which encrypt method is valid.</summary>
    public int R { get; }

    private PdfStandardSecurityEncryptor(byte[] fileKey, byte[] u, byte[] o, byte[]? ue, byte[]? oe, byte[]? perms, int r)
    {
        FileKey = fileKey;
        U = u;
        O = o;
        UE = ue;
        OE = oe;
        Perms = perms;
        R = r;
    }

    /// <summary>
    /// Build a fresh V=5 R=6 encryptor: generates a random file key and
    /// derives /U, /O, /UE, /OE, /Perms from the given passwords and
    /// permission bitmask.
    /// </summary>
    /// <param name="userPasswordBytes">User password bytes (UTF-8 encoded by the caller). Empty array for an empty password.</param>
    /// <param name="ownerPasswordBytes">Owner password bytes (UTF-8 encoded by the caller). Empty array for an empty password.</param>
    /// <param name="permissions">The /P permission bitmask (signed 32-bit).</param>
    /// <param name="encryptMetadata">Whether /EncryptMetadata is true.</param>
    public static PdfStandardSecurityEncryptor CreateR6(
        byte[] userPasswordBytes, byte[] ownerPasswordBytes, long permissions, bool encryptMetadata)
    {
        var fileKey = RandomNumberGenerator.GetBytes(32);

        // Algorithm 8: /U and /UE from the user password. userKey parameter
        // to ComputeR6Hash is empty for the user-password path.
        var userValidationSalt = RandomNumberGenerator.GetBytes(8);
        var userKeySalt = RandomNumberGenerator.GetBytes(8);
        var userValidationHash = PdfStandardSecurityHandler.ComputeR6Hash(
            userPasswordBytes, userValidationSalt, Array.Empty<byte>());
        var u = Concat(userValidationHash, userValidationSalt, userKeySalt);

        var userIntermediateKey = PdfStandardSecurityHandler.ComputeR6Hash(
            userPasswordBytes, userKeySalt, Array.Empty<byte>());
        var ue = AesCbcEncryptNoPadZeroIv(userIntermediateKey, fileKey);

        // Algorithm 9: /O and /OE from the owner password. userKey parameter
        // is the 48-byte /U just computed — this is what chains the owner
        // password to the user password per spec.
        var ownerValidationSalt = RandomNumberGenerator.GetBytes(8);
        var ownerKeySalt = RandomNumberGenerator.GetBytes(8);
        var ownerValidationHash = PdfStandardSecurityHandler.ComputeR6Hash(
            ownerPasswordBytes, ownerValidationSalt, u);
        var o = Concat(ownerValidationHash, ownerValidationSalt, ownerKeySalt);

        var ownerIntermediateKey = PdfStandardSecurityHandler.ComputeR6Hash(
            ownerPasswordBytes, ownerKeySalt, u);
        var oe = AesCbcEncryptNoPadZeroIv(ownerIntermediateKey, fileKey);

        // Algorithm 10: /Perms.
        var perms = ComputePerms(fileKey, permissions, encryptMetadata);

        return new PdfStandardSecurityEncryptor(fileKey, u, o, ue, oe, perms, r: 6);
    }

    /// <summary>
    /// Build a fresh V=4 R=4 encryptor (AES-128, CFM=AESV2): generates a
    /// random 16-byte file key and derives /O (Algorithm 3) and /U
    /// (Algorithm 5) from the given passwords, permission bitmask, and the
    /// document's /ID[0] — unlike R=6, R=4's KDF is anchored to /ID, so the
    /// caller must have already settled on the trailer's /ID before calling
    /// this (see <see cref="Pdfe.Core.Writing.PdfDocumentWriter"/>'s
    /// ID-before-PrepareEncryption ordering).
    /// </summary>
    /// <param name="userPasswordBytes">User password bytes (PDFDocEncoding-preferred; caller picks the encoding). Empty array for an empty password.</param>
    /// <param name="ownerPasswordBytes">Owner password bytes. Empty array for an empty password (falls back to the user password per Algorithm 3 step (a)).</param>
    /// <param name="permissions">The /P permission bitmask (signed 32-bit).</param>
    /// <param name="encryptMetadata">Whether /EncryptMetadata is true.</param>
    /// <param name="firstId">The trailer /ID[0] bytes — must be the exact bytes that will be written to the trailer, since Algorithms 2/3/5 all hash them in.</param>
    /// <param name="keyBytes">File key length in bytes. 16 for AES-128 (the only R=4 configuration this writer emits).</param>
    public static PdfStandardSecurityEncryptor CreateR4(
        byte[] userPasswordBytes, byte[] ownerPasswordBytes, long permissions,
        bool encryptMetadata, byte[] firstId, int keyBytes = 16)
    {
        // Algorithm 3: compute /O. Step (a): pad the owner password (or the
        // user password, if no owner password was supplied) to 32 bytes.
        var ownerPasswordForO = ownerPasswordBytes.Length > 0 ? ownerPasswordBytes : userPasswordBytes;
        var paddedOwner = PdfStandardSecurityHandler.PadPassword(ownerPasswordForO);

        // Step (b): MD5 hash it; step (c) (R>=3): 50 more rounds, each time
        // re-hashing only the first keyBytes of the previous digest —
        // exactly mirrors DeriveFileKey's step 8 re-hash loop.
        var oHash = MD5.HashData(paddedOwner);
        for (int i = 0; i < 50; i++)
        {
            var input = new byte[keyBytes];
            Array.Copy(oHash, input, keyBytes);
            oHash = MD5.HashData(input);
        }

        // Step (d): first keyBytes of the final hash is the RC4 key.
        var oRc4Key = new byte[keyBytes];
        Array.Copy(oHash, oRc4Key, keyBytes);

        // Step (e)-(f): pad the USER password to 32 bytes and RC4-encrypt
        // it with the owner-derived key.
        var paddedUser = PdfStandardSecurityHandler.PadPassword(userPasswordBytes);
        var oEncrypted = Rc4.Transform(oRc4Key, paddedUser);

        // Step (g) (R>=3): 19 more RC4 rounds, each with every key byte
        // XORed by the round index — same loop shape as Algorithm 5/6's
        // 20-round chain, just applied to the owner key over the padded
        // user password instead of the file key over the ID hash.
        for (int i = 1; i <= 19; i++)
        {
            var keyMod = new byte[keyBytes];
            for (int j = 0; j < keyBytes; j++)
                keyMod[j] = (byte)(oRc4Key[j] ^ i);
            oEncrypted = Rc4.Transform(keyMod, oEncrypted);
        }
        var o = oEncrypted; // 32 bytes

        // Algorithm 2: file encryption key from the USER password + the /O
        // just computed + /P + /ID[0]. Reuses the handler's exact read-side
        // implementation (internal, shared for this reason).
        var fileKey = PdfStandardSecurityHandler.DeriveFileKey(
            userPasswordBytes, o, permissions, firstId, r: 4, keyBytes, encryptMetadata);

        // Algorithm 5 (R>=3): same MD5(padding||ID[0]) + 20-round RC4 chain
        // Algorithm 6 uses to *verify* /U — here we capture the 16-byte
        // result instead of comparing it, then pad to 32 bytes. Per spec
        // the last 16 bytes of /U are unspecified for R>=3 (the read-side
        // VerifyUserPassword only ever compares the first 16 — see its
        // comment), so any bytes are valid; use random ones so two saves of
        // the same document don't produce byte-identical /U values.
        var uHash = PdfStandardSecurityHandler.ComputeUserPasswordHashR3Plus(fileKey, firstId);
        var u = new byte[32];
        Array.Copy(uHash, u, 16);
        RandomNumberGenerator.Fill(u.AsSpan(16, 16));

        return new PdfStandardSecurityEncryptor(fileKey, u, o, ue: null, oe: null, perms: null, r: 4);
    }

    /// <summary>
    /// Encrypt a stream's or string's plaintext bytes for storage under
    /// V=5 R=6: AES-256 CBC with a fresh random 16-byte IV, PKCS7 padding,
    /// IV prepended to the ciphertext — mirrors <c>AesCbcDecrypt</c>'s
    /// "first 16 bytes are the IV" convention on the read side. Uses the
    /// file key directly (no per-object derivation — see the V=5 note on
    /// this type). Throws if called on an R=4 instance; use
    /// <see cref="EncryptObjectBytes"/> there instead.
    /// </summary>
    public byte[] EncryptBytes(byte[] plaintext)
    {
        if (R != 6)
            throw new InvalidOperationException(
                $"EncryptBytes (file-key only, no per-object derivation) is only valid for R=6; " +
                $"this instance is R={R}. Use EncryptObjectBytes(objNum, gen, plaintext) instead.");

        var iv = RandomNumberGenerator.GetBytes(16);
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = FileKey;
        aes.IV = iv;

        using var encryptor = aes.CreateEncryptor();
        var cipher = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);

        var result = new byte[16 + cipher.Length];
        Array.Copy(iv, 0, result, 0, 16);
        Array.Copy(cipher, 0, result, 16, cipher.Length);
        return result;
    }

    /// <summary>
    /// Encrypt a stream's or string's plaintext bytes for storage under
    /// V=4 R=4 (CFM=AESV2): derives this object's own key via Algorithm 1
    /// (<see cref="PdfStandardSecurityHandler.ComputeObjectKey"/> — file key
    /// + obj#/gen# + "sAlT"), then AES-128 CBC encrypts with a fresh random
    /// 16-byte IV, PKCS7 padding, IV prepended — same on-disk shape as
    /// <see cref="EncryptBytes"/>, but the AES key differs per object.
    /// Throws if called on an R=6 instance; use <see cref="EncryptBytes"/>
    /// there instead.
    /// </summary>
    public byte[] EncryptObjectBytes(int objNum, int gen, byte[] plaintext)
    {
        if (R != 4)
            throw new InvalidOperationException(
                $"EncryptObjectBytes (per-object key derivation) is only valid for R=4; " +
                $"this instance is R={R}. Use EncryptBytes(plaintext) instead.");

        var objectKey = PdfStandardSecurityHandler.ComputeObjectKey(FileKey, objNum, gen, usesAes: true);

        var iv = RandomNumberGenerator.GetBytes(16);
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = objectKey;
        aes.IV = iv;

        using var encryptor = aes.CreateEncryptor();
        var cipher = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);

        var result = new byte[16 + cipher.Length];
        Array.Copy(iv, 0, result, 0, 16);
        Array.Copy(cipher, 0, result, 16, cipher.Length);
        return result;
    }

    /// <summary>
    /// Algorithm 10 (PDF 2.0 §7.6.4.4.6): 16-byte encrypted permissions.
    /// Bytes 0-3 = /P as little-endian signed 32-bit; bytes 4-7 = 0xFF
    /// fixed marker; byte 8 = 'T'/'F' for /EncryptMetadata; bytes 9-11 =
    /// ASCII "adb"; bytes 12-15 = random. The whole 16-byte block is then
    /// encrypted with AES-256 in ECB mode (not CBC — this field is the one
    /// spec-mandated exception), no padding, using the file key directly.
    /// </summary>
    private static byte[] ComputePerms(byte[] fileKey, long permissions, bool encryptMetadata)
    {
        var block = new byte[16];
        int p = unchecked((int)permissions);
        block[0] = (byte)(p & 0xFF);
        block[1] = (byte)((p >> 8) & 0xFF);
        block[2] = (byte)((p >> 16) & 0xFF);
        block[3] = (byte)((p >> 24) & 0xFF);
        block[4] = 0xFF;
        block[5] = 0xFF;
        block[6] = 0xFF;
        block[7] = 0xFF;
        block[8] = (byte)(encryptMetadata ? 'T' : 'F');
        block[9] = (byte)'a';
        block[10] = (byte)'d';
        block[11] = (byte)'b';
        RandomNumberGenerator.Fill(block.AsSpan(12, 4));

        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = fileKey;
        using var encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(block, 0, 16);
    }

    /// <summary>
    /// AES-256 CBC encrypt with zero IV and no padding — the encrypt-side
    /// inverse of <c>PdfStandardSecurityHandler.AesCbcDecryptNoPadZeroIv</c>,
    /// used to wrap the file key into /UE and /OE. <paramref name="plaintext"/>
    /// is always exactly 32 bytes here (the file key), a whole number of AES
    /// blocks, so "no padding" is safe.
    /// </summary>
    private static byte[] AesCbcEncryptNoPadZeroIv(byte[] key, byte[] plaintext)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.KeySize = key.Length * 8;
        aes.Key = key;
        aes.IV = new byte[16];
        using var encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);
    }

    private static byte[] Concat(params byte[][] parts)
    {
        int total = parts.Sum(p => p.Length);
        var output = new byte[total];
        int offset = 0;
        foreach (var part in parts)
        {
            Array.Copy(part, 0, output, offset, part.Length);
            offset += part.Length;
        }
        return output;
    }
}
