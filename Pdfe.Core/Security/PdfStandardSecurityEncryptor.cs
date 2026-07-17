using System.Linq;
using System.Security.Cryptography;

namespace Pdfe.Core.Security;

/// <summary>
/// Writer-side counterpart of <see cref="PdfStandardSecurityHandler"/> for
/// V=5 R=6 (AES-256, PDF 2.0 native). Implements ISO 32000-2 §7.6.4.4
/// Algorithm 8 (compute <c>/U</c> and <c>/UE</c>), Algorithm 9 (compute
/// <c>/O</c> and <c>/OE</c>), and Algorithm 10 (compute <c>/Perms</c>) —
/// the exact inverse of <see cref="PdfStandardSecurityHandler.BuildR6"/>,
/// which recovers the file key from those same fields.
/// </summary>
/// <remarks>
/// Per spec §7.6.3.1, V=5 uses the file encryption key directly as the
/// AES-256 cipher key for every object — there is no Algorithm 1
/// per-object key derivation for R=6, so <see cref="EncryptBytes"/> does
/// not take an object number/generation. (Contrast with V&lt;=4, where
/// each object gets its own derived key — see issue #640.)
/// </remarks>
internal sealed class PdfStandardSecurityEncryptor
{
    /// <summary>The random 32-byte file encryption key. Never written to the file directly — only via /UE, /OE.</summary>
    public byte[] FileKey { get; }

    /// <summary>48-byte /U value: 32-byte hash || 8-byte validation salt || 8-byte key salt.</summary>
    public byte[] U { get; }

    /// <summary>48-byte /O value: same shape as /U, but chains through /U (Algorithm 9).</summary>
    public byte[] O { get; }

    /// <summary>32-byte /UE value: file key, AES-256-CBC encrypted under a key derived from the user password.</summary>
    public byte[] UE { get; }

    /// <summary>32-byte /OE value: file key, AES-256-CBC encrypted under a key derived from the owner password.</summary>
    public byte[] OE { get; }

    /// <summary>16-byte /Perms value: encrypted permissions (Algorithm 10).</summary>
    public byte[] Perms { get; }

    private PdfStandardSecurityEncryptor(byte[] fileKey, byte[] u, byte[] o, byte[] ue, byte[] oe, byte[] perms)
    {
        FileKey = fileKey;
        U = u;
        O = o;
        UE = ue;
        OE = oe;
        Perms = perms;
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

        return new PdfStandardSecurityEncryptor(fileKey, u, o, ue, oe, perms);
    }

    /// <summary>
    /// Encrypt a stream's or string's plaintext bytes for storage: AES-256
    /// CBC with a fresh random 16-byte IV, PKCS7 padding, IV prepended to
    /// the ciphertext — mirrors <c>AesCbcDecrypt</c>'s "first 16 bytes are
    /// the IV" convention on the read side. Uses the file key directly (no
    /// per-object derivation — see the V=5 note on this type).
    /// </summary>
    public byte[] EncryptBytes(byte[] plaintext)
    {
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
