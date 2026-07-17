using System.Security.Cryptography;
using System.Text;
using Pdfe.Core.Parsing;
using Pdfe.Core.Primitives;

namespace Pdfe.Core.Security;

/// <summary>
/// PDF Standard Security Handler — derives the file encryption key
/// from a user password, verifies it against the /U and /O entries,
/// and produces per-object keys for stream/string decryption.
///
/// References: ISO 32000-2:2020 §7.6.3 (Standard Security Handler),
/// Algorithms 2 (key derivation), 4-5 (encryption), 6 (password
/// verification). Algorithm numbering matches the spec.
///
/// This implementation supports:
/// - V=1 R=2 (40-bit RC4) — legacy
/// - V=2 R=3 (128-bit RC4) — most common
/// - V=4 R=4 with CFM=V2 (RC4-128 via crypt filters) — common
/// - V=4 R=4 with CFM=AESV2 (AES-128 in CBC mode) — modern pre-PDF-2.0
/// - V=5 R=6 with CFM=AESV3 (AES-256 in CBC mode) — PDF 2.0 native
///
/// PDF 2.0 (R=6) is materially different from R=2/3/4: 48-byte /U /O,
/// new /UE /OE entries holding the encrypted file key, and the chained
/// SHA-256/384/512 hash from Algorithm 2.B replacing the old MD5-50x
/// loop. R=5 (an Adobe Extension Level 3 transitional spec) is buggy
/// and not implemented.
/// </summary>
public sealed class PdfStandardSecurityHandler
{
    /// <summary>32-byte padding string used by Algorithms 2 and 6.</summary>
    private static readonly byte[] PasswordPadding =
    {
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41,
        0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80,
        0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A
    };

    /// <summary>Encryption algorithm version (/V).</summary>
    public int V { get; }

    /// <summary>Standard security handler revision (/R).</summary>
    public int R { get; }

    /// <summary>Encryption key length in bytes (e.g. 5 for V=1, 16 for V=2/4).</summary>
    public int KeyLengthBytes { get; }

    /// <summary>The derived file encryption key for stream/string keys.</summary>
    private readonly byte[] _fileEncryptionKey;

    /// <summary>True if streams/strings use AES (V=4 with CFM=AESV2 or V=5).</summary>
    public bool UsesAes { get; }

    private PdfStandardSecurityHandler(int v, int r, int keyLengthBytes, byte[] fileKey, bool usesAes)
    {
        V = v;
        R = r;
        KeyLengthBytes = keyLengthBytes;
        _fileEncryptionKey = fileKey;
        UsesAes = usesAes;
    }

    /// <summary>
    /// Build a handler from the /Encrypt dict and document /ID, verifying
    /// the supplied user password. Throws <see cref="PdfEncryptionNotSupportedException"/>
    /// for unsupported algorithm versions and a clear message when the
    /// password is wrong.
    /// </summary>
    /// <param name="encryptDict">The /Encrypt dictionary from the trailer.</param>
    /// <param name="firstId">First element of the trailer /ID array.</param>
    /// <param name="userPassword">User password bytes (empty array for empty password — the most common case).</param>
    public static PdfStandardSecurityHandler Build(
        PdfDictionary encryptDict, byte[] firstId, byte[] userPassword)
    {
        var filter = encryptDict.GetNameOrNull("Filter");
        if (filter != "Standard")
            throw new PdfEncryptionNotSupportedException(
                $"Only the Standard security handler is supported (file uses /Filter /{filter}).");

        int v = encryptDict.GetInt("V");
        int r = encryptDict.GetInt("R");
        int lengthBits = encryptDict.ContainsKey("Length") ? encryptDict.GetInt("Length") : 40;
        // /Length is in bits per spec; some files (incorrectly) store bytes.
        // Bytes vs bits is unambiguous: encryption keys are 40-128 bits = 5-16 bytes.
        int keyBytes = lengthBits >= 40 ? lengthBits / 8 : lengthBits;
        if (keyBytes < 5 || keyBytes > 32)
            throw new PdfEncryptionNotSupportedException(
                $"Unsupported /Length value {lengthBits} in /Encrypt dict.");

        // Determine cipher: RC4 (V=1, V=2) or look at /CF for V=4
        bool usesAes = false;
        if (v == 4 || v == 5)
        {
            var cf = encryptDict.GetDictionaryOrNull("CF");
            var stmF = encryptDict.GetNameOrNull("StmF") ?? "Identity";
            if (cf != null && stmF != "Identity")
            {
                var cfm = cf.GetDictionaryOrNull(stmF)?.GetNameOrNull("CFM");
                if (cfm == "AESV2" || cfm == "AESV3")
                    usesAes = true;
            }
        }

        if (v == 5)
        {
            // PDF 2.0 path: 48-byte /U + /UE encrypted file key + SHA-256
            // chained hash (Algorithm 2.B). Completely separate from the
            // V=1/2/4 KDF flow, so handle it in a dedicated builder.
            return BuildR6(encryptDict, userPassword);
        }
        if (v != 1 && v != 2 && v != 4)
            throw new PdfEncryptionNotSupportedException(
                $"Encryption algorithm V={v} is not supported. Only V=1, V=2, V=4, V=5 are implemented.");

        // /O, /U: usually 32-byte hex strings. Read as raw bytes.
        var oBytes = GetByteString(encryptDict, "O", 32);
        var uBytes = GetByteString(encryptDict, "U", 32);
        // /P is a 32-bit signed integer of permission flags.
        long permissions = encryptDict.GetInt("P");
        bool encryptMetadata = !encryptDict.ContainsKey("EncryptMetadata") ||
                               encryptDict.GetBool("EncryptMetadata");

        // Algorithm 2: derive file encryption key.
        var fileKey = DeriveFileKey(
            userPassword, oBytes, permissions, firstId, r, keyBytes, encryptMetadata);

        // Algorithm 6: verify the user password.
        if (!VerifyUserPassword(fileKey, uBytes, firstId, r))
        {
            throw new PdfEncryptionNotSupportedException(
                "Password verification failed. The supplied user password was rejected, " +
                "or the file requires a non-empty user password.");
        }

        if (v == 4 && r == 4 && fileKey.Length < 16)
        {
            var paddedFileKey = new byte[16];
            Array.Copy(fileKey, paddedFileKey, fileKey.Length);
            fileKey = paddedFileKey;
            keyBytes = 16;
        }

        return new PdfStandardSecurityHandler(v, r, keyBytes, fileKey, usesAes);
    }

    /// <summary>
    /// Build a handler from a .NET password string. PDF encryption algorithms
    /// operate on byte strings; legacy files commonly use PDFDocEncoding bytes
    /// while PDF 2.0 AES-256 files use UTF-8 bytes. This overload tries the
    /// spec-appropriate encoding first and keeps the byte-exact overload
    /// available for callers that already have raw password bytes.
    /// </summary>
    /// <param name="encryptDict">The /Encrypt dictionary from the trailer.</param>
    /// <param name="firstId">First element of the trailer /ID array.</param>
    /// <param name="userPassword">User password text. <c>null</c> is treated as the empty password.</param>
    public static PdfStandardSecurityHandler Build(
        PdfDictionary encryptDict, byte[] firstId, string? userPassword)
    {
        var password = userPassword ?? string.Empty;
        var revision = encryptDict.GetInt("R");
        PdfEncryptionNotSupportedException? lastPasswordFailure = null;

        foreach (var candidate in EncodeUserPasswordCandidates(password, revision))
        {
            try
            {
                return Build(encryptDict, firstId, candidate);
            }
            catch (PdfEncryptionNotSupportedException ex) when (IsPasswordVerificationFailure(ex))
            {
                lastPasswordFailure = ex;
            }
        }

        throw new PdfEncryptionNotSupportedException(
            lastPasswordFailure?.Message
            ?? "Password verification failed. The supplied user password was rejected.");
    }

    /// <summary>
    /// Decrypt a stream's encoded bytes for the given indirect object. The
    /// returned bytes are still subject to the stream's /Filter pipeline
    /// (FlateDecode etc.) — Crypt is conceptually the first filter.
    /// </summary>
    public byte[] DecryptStream(int objNum, int gen, byte[] cipherBytes)
    {
        var key = DeriveObjectKey(objNum, gen);
        return UsesAes ? AesCbcDecrypt(key, cipherBytes) : Rc4.Transform(key, cipherBytes);
    }

    /// <summary>
    /// Decrypt a PDF string belonging to the given indirect object.
    /// AES-mode strings are also AES-CBC encrypted with a 16-byte IV
    /// prefix.
    /// </summary>
    public byte[] DecryptString(int objNum, int gen, byte[] cipherBytes)
    {
        var key = DeriveObjectKey(objNum, gen);
        return UsesAes ? AesCbcDecrypt(key, cipherBytes) : Rc4.Transform(key, cipherBytes);
    }

    /// <summary>
    /// AES-128/CBC with PKCS#7 padding. Per PDF spec the first 16 bytes
    /// of <paramref name="cipherBytes"/> are the IV; the rest is the
    /// ciphertext. Empty/under-block-size inputs are returned as-is —
    /// some PDFs (notably qpdf-emitted ones) emit empty AES strings
    /// when the underlying string is empty.
    /// </summary>
    private static byte[] AesCbcDecrypt(byte[] key, byte[] cipherBytes)
    {
        if (cipherBytes.Length < 16)
            return Array.Empty<byte>();

        var iv = new byte[16];
        Array.Copy(cipherBytes, 0, iv, 0, 16);
        int cipherLen = cipherBytes.Length - 16;
        if (cipherLen == 0) return Array.Empty<byte>();

        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        try
        {
            using var decryptor = aes.CreateDecryptor();
            return decryptor.TransformFinalBlock(cipherBytes, 16, cipherLen);
        }
        catch (CryptographicException)
        {
            // Fall back to no-padding: some non-PKCS7 PDFs emit garbage
            // tails. Return whatever survives stripping the trailing block.
            aes.Padding = PaddingMode.None;
            using var decryptor = aes.CreateDecryptor();
            return decryptor.TransformFinalBlock(cipherBytes, 16, cipherLen);
        }
    }

    /// <summary>
    /// Algorithm 1: per-object key from file key + obj# + gen# (+ "sAlT"
    /// for AES). Used by both string and stream decryption.
    ///
    /// V=5 R=6 special case: per spec §7.6.3.1, the file encryption key
    /// is used directly as the AES-256 cipher key for every object —
    /// Algorithm 1 is not applied. Skip the derivation entirely.
    /// </summary>
    private byte[] DeriveObjectKey(int objNum, int gen)
    {
        if (V == 5) return _fileEncryptionKey;

        // Append low-order 3 bytes of obj#, low-order 2 bytes of gen# (LE).
        // For AES (CFM=AESV2), append "sAlT" (4 bytes 0x73 0x41 0x6C 0x54)
        // before MD5 — this differentiates AES per-object keys from RC4.
        int extra = UsesAes ? 9 : 5;
        var input = new byte[_fileEncryptionKey.Length + extra];
        Array.Copy(_fileEncryptionKey, input, _fileEncryptionKey.Length);
        int p = _fileEncryptionKey.Length;
        input[p + 0] = (byte)(objNum & 0xFF);
        input[p + 1] = (byte)((objNum >> 8) & 0xFF);
        input[p + 2] = (byte)((objNum >> 16) & 0xFF);
        input[p + 3] = (byte)(gen & 0xFF);
        input[p + 4] = (byte)((gen >> 8) & 0xFF);
        if (UsesAes)
        {
            input[p + 5] = 0x73; // 's'
            input[p + 6] = 0x41; // 'A'
            input[p + 7] = 0x6C; // 'l'
            input[p + 8] = 0x54; // 'T'
        }

        var hash = MD5.HashData(input);
        // Object key length: min(filekey + 5, 16). For AES the cipher
        // requires exactly the file-key length bytes (16 for AES-128).
        int n = Math.Min(_fileEncryptionKey.Length + 5, 16);
        var key = new byte[n];
        Array.Copy(hash, key, n);
        return key;
    }

    /// <summary>
    /// Algorithm 2: file encryption key from password + /O + /P + /ID[0].
    /// </summary>
    private static byte[] DeriveFileKey(
        byte[] password, byte[] o, long p, byte[] firstId,
        int r, int keyBytes, bool encryptMetadata)
    {
        // Step 1-2: pad/truncate password to 32 bytes
        var paddedPwd = PadPassword(password);

        // Step 3-7: MD5 of padded password + O + P + ID[0]
        using var md5 = MD5.Create();
        md5.TransformBlock(paddedPwd, 0, 32, null, 0);
        md5.TransformBlock(o, 0, 32, null, 0);
        var pBytes = new byte[]
        {
            (byte)(p & 0xFF),
            (byte)((p >> 8) & 0xFF),
            (byte)((p >> 16) & 0xFF),
            (byte)((p >> 24) & 0xFF)
        };
        md5.TransformBlock(pBytes, 0, 4, null, 0);
        md5.TransformBlock(firstId, 0, firstId.Length, null, 0);

        // Step 6 (R≥4): if /EncryptMetadata is false, hash 4 bytes of 0xFF.
        if (r >= 4 && !encryptMetadata)
        {
            var ff = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
            md5.TransformBlock(ff, 0, 4, null, 0);
        }

        md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        var hash = md5.Hash!;

        // Step 8 (R≥3): re-hash 50 times, taking only first keyBytes bytes
        if (r >= 3)
        {
            for (int i = 0; i < 50; i++)
            {
                var input = new byte[keyBytes];
                Array.Copy(hash, input, keyBytes);
                hash = MD5.HashData(input);
            }
        }

        // Step 9: take first keyBytes
        var key = new byte[keyBytes];
        Array.Copy(hash, key, keyBytes);
        return key;
    }

    /// <summary>
    /// Algorithm 6: verify user password by comparing against /U.
    /// R=2: encrypt the padding string with the file key; compare to /U.
    /// R≥3: MD5(padding || ID[0]) → 20 RC4 rounds with key XOR i; compare first 16 bytes.
    /// </summary>
    private static bool VerifyUserPassword(byte[] fileKey, byte[] u, byte[] firstId, int r)
    {
        if (r == 2)
        {
            var encrypted = Rc4.Transform(fileKey, PasswordPadding);
            return ByteArrayEquals(encrypted, u);
        }

        // R ≥ 3
        using var md5 = MD5.Create();
        md5.TransformBlock(PasswordPadding, 0, 32, null, 0);
        md5.TransformBlock(firstId, 0, firstId.Length, null, 0);
        md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        var hash = md5.Hash!;

        var encrypted2 = Rc4.Transform(fileKey, hash);
        for (int i = 1; i <= 19; i++)
        {
            var keyMod = new byte[fileKey.Length];
            for (int j = 0; j < fileKey.Length; j++)
                keyMod[j] = (byte)(fileKey[j] ^ i);
            encrypted2 = Rc4.Transform(keyMod, encrypted2);
        }

        // Compare first 16 bytes only — /U for R≥3 is 32 bytes, but the
        // last 16 are arbitrary salt.
        for (int i = 0; i < 16; i++)
            if (encrypted2[i] != u[i]) return false;
        return true;
    }

    /// <summary>
    /// PDF 2.0 (V=5 R=6) handler: Algorithm 11 / 12 password validation
    /// using the SHA-256-based chained hash (Algorithm 2.B), then AES-256
    /// decryption of /UE to recover the file encryption key. Per spec
    /// §7.6.3.1, V=5 uses the file key directly as the AES-256 cipher
    /// key — no Algorithm 1 per-object derivation.
    /// </summary>
    private static PdfStandardSecurityHandler BuildR6(PdfDictionary encryptDict, byte[] userPassword)
    {
        int r = encryptDict.GetInt("R");
        if (r != 6)
            throw new PdfEncryptionNotSupportedException(
                $"V=5 with R={r} is not supported. Only R=6 (PDF 2.0) is implemented; " +
                "R=5 was a transitional Adobe extension and is not standards-compliant.");

        var u = GetByteString(encryptDict, "U", 48);
        var o = GetByteString(encryptDict, "O", 48);
        var ue = GetByteString(encryptDict, "UE", 32);
        var oe = GetByteString(encryptDict, "OE", 32);

        // Algorithm 11 step (a): try the user password.
        // U[0..32] = SHA-256-chained hash of (password || U[32..40])
        // U[32..40] = validation salt
        // U[40..48] = key salt
        var validationSalt = new byte[8];
        Array.Copy(u, 32, validationSalt, 0, 8);
        var userValidation = ComputeR6Hash(userPassword, validationSalt, Array.Empty<byte>());
        if (!ConstantTimeEquals(userValidation, 0, u, 0, 32))
        {
            // Algorithm 12: try the owner password (rare for empty-pwd files).
            // For now we support user passwords only; throw a clear error.
            throw new PdfEncryptionNotSupportedException(
                "AES-256 (V=5 R=6) password verification failed for the supplied user password. " +
                "Owner-password-only files are not yet supported (#324).");
        }

        // Algorithm 11 step (b): derive intermediate user key, then AES-256
        // decrypt /UE to get the file encryption key.
        var keySalt = new byte[8];
        Array.Copy(u, 40, keySalt, 0, 8);
        var intermediateKey = ComputeR6Hash(userPassword, keySalt, Array.Empty<byte>());

        var fileKey = AesCbcDecryptNoPadZeroIv(intermediateKey, ue);
        if (fileKey.Length != 32)
            throw new PdfParseException(
                $"Decrypted /UE produced {fileKey.Length} bytes; expected 32 for AES-256.");

        return new PdfStandardSecurityHandler(5, 6, 32, fileKey, usesAes: true);
    }

    /// <summary>
    /// Algorithm 2.B (PDF 2.0 §7.6.4.3.3): chained SHA-256/384/512 +
    /// AES-128 hash used for password verification and intermediate key
    /// derivation in R=6. The choice of hash for each round depends on
    /// (sum of first 16 bytes of the AES output) mod 3.
    ///
    /// Internal (not private) so <see cref="PdfStandardSecurityEncryptor"/>
    /// (the writer-side counterpart — Algorithms 8/9/10) can reuse this
    /// exact, already-correct implementation instead of duplicating it.
    /// </summary>
    internal static byte[] ComputeR6Hash(byte[] password, byte[] salt, byte[] userKey)
    {
        // Initial K = SHA-256(password || salt || userKey)
        // userKey is empty for the user-password path (Algorithm 11), or
        // the 48-byte /U for the owner-password path (Algorithm 12).
        var initial = ConcatBytes(password, salt, userKey);
        byte[] k = SHA256.HashData(initial);

        int round = 0;
        while (true)
        {
            // K1 = (password || K || userKey) repeated 64 times.
            var block = ConcatBytes(password, k, userKey);
            var k1 = new byte[block.Length * 64];
            for (int i = 0; i < 64; i++)
                Array.Copy(block, 0, k1, i * block.Length, block.Length);

            // E = AES-128-CBC encrypt K1 with key = K[0..16], IV = K[16..32], no padding.
            byte[] e;
            using (var aes = Aes.Create())
            {
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.None;
                aes.KeySize = 128;
                var aesKey = new byte[16];
                Array.Copy(k, 0, aesKey, 0, 16);
                var aesIv = new byte[16];
                Array.Copy(k, 16, aesIv, 0, 16);
                aes.Key = aesKey;
                aes.IV = aesIv;
                using var encryptor = aes.CreateEncryptor();
                e = encryptor.TransformFinalBlock(k1, 0, k1.Length);
            }

            // Sum the first 16 bytes of E modulo 3 → next hash variant.
            int sum = 0;
            for (int i = 0; i < 16; i++) sum += e[i];
            int variant = sum % 3;
            k = variant switch
            {
                0 => SHA256.HashData(e),
                1 => SHA384.HashData(e),
                2 => SHA512.HashData(e),
                _ => throw new InvalidOperationException()
            };

            round++;

            // Stopping condition (per spec): after at least 64 rounds, stop
            // when E[E.Length-1] ≤ round - 32.
            if (round >= 64 && e[e.Length - 1] <= round - 32)
                break;
        }

        // Result: first 32 bytes of K (it's 32, 48, or 64 bytes depending
        // on the last variant; the spec only uses the first 32).
        var result = new byte[32];
        Array.Copy(k, 0, result, 0, 32);
        return result;
    }

    /// <summary>
    /// AES-256 CBC decrypt with zero IV and no padding. Used to recover
    /// the file encryption key from /UE (Algorithm 11).
    /// </summary>
    private static byte[] AesCbcDecryptNoPadZeroIv(byte[] key, byte[] cipherBytes)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.KeySize = key.Length * 8;
        aes.Key = key;
        aes.IV = new byte[16];
        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
    }

    private static IEnumerable<byte[]> EncodeUserPasswordCandidates(string password, int revision)
    {
        if (password.Length == 0)
        {
            yield return Array.Empty<byte>();
            yield break;
        }

        var emitted = new List<byte[]>();

        if (revision >= 5)
        {
            foreach (var bytes in UniquePasswordEncodings(password, preferUtf8: true, emitted))
                yield return bytes;
        }
        else
        {
            foreach (var bytes in UniquePasswordEncodings(password, preferUtf8: false, emitted))
                yield return bytes;
        }
    }

    private static IEnumerable<byte[]> UniquePasswordEncodings(
        string password,
        bool preferUtf8,
        List<byte[]> emitted)
    {
        if (preferUtf8)
        {
            foreach (var bytes in AddUnique(Encoding.UTF8.GetBytes(password), emitted))
                yield return bytes;
            if (PdfString.TryEncodePdfDocEncoding(password, out var pdfDocBytes))
            {
                foreach (var bytes in AddUnique(pdfDocBytes, emitted))
                    yield return bytes;
            }
        }
        else
        {
            if (PdfString.TryEncodePdfDocEncoding(password, out var pdfDocBytes))
            {
                foreach (var bytes in AddUnique(pdfDocBytes, emitted))
                    yield return bytes;
            }
            foreach (var bytes in AddUnique(Encoding.UTF8.GetBytes(password), emitted))
                yield return bytes;
        }
    }

    private static IEnumerable<byte[]> AddUnique(byte[] candidate, List<byte[]> emitted)
    {
        if (emitted.Any(existing => existing.SequenceEqual(candidate)))
            yield break;

        emitted.Add(candidate);
        yield return candidate;
    }

    private static bool IsPasswordVerificationFailure(PdfEncryptionNotSupportedException ex)
        => ex.Message.Contains("password verification failed", StringComparison.OrdinalIgnoreCase);

    private static byte[] ConcatBytes(params byte[][] parts)
    {
        int total = parts.Sum(p => p.Length);
        var output = new byte[total];
        int offset = 0;
        foreach (var p in parts)
        {
            Array.Copy(p, 0, output, offset, p.Length);
            offset += p.Length;
        }
        return output;
    }

    private static bool ConstantTimeEquals(byte[] a, int aOffset, byte[] b, int bOffset, int length)
    {
        int diff = 0;
        for (int i = 0; i < length; i++) diff |= a[aOffset + i] ^ b[bOffset + i];
        return diff == 0;
    }

    private static byte[] PadPassword(byte[] password)
    {
        var padded = new byte[32];
        int copyLen = Math.Min(password.Length, 32);
        if (copyLen > 0) Array.Copy(password, padded, copyLen);
        if (copyLen < 32) Array.Copy(PasswordPadding, 0, padded, copyLen, 32 - copyLen);
        return padded;
    }

    private static byte[] GetByteString(PdfDictionary dict, string name, int expectedLength)
    {
        var s = dict.GetOptional(name) as PdfString
            ?? throw new PdfParseException($"/Encrypt dict is missing /{name}");
        var bytes = s.Bytes;
        if (bytes.Length > expectedLength
            && expectedLength == 48
            && HasOnlyZeroPaddingAfter(bytes, expectedLength))
        {
            return bytes[..expectedLength];
        }

        if (bytes.Length != expectedLength)
            throw new PdfParseException(
                $"/{name} must be exactly {expectedLength} bytes; got {bytes.Length}");
        return bytes;
    }

    private static bool HasOnlyZeroPaddingAfter(byte[] bytes, int expectedLength)
    {
        for (int i = expectedLength; i < bytes.Length; i++)
        {
            if (bytes[i] != 0)
                return false;
        }

        return true;
    }

    private static bool ByteArrayEquals(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
}
