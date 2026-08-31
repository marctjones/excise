namespace Excise.Core.Security;

/// <summary>
/// RC4 stream cipher (key-scheduling + pseudo-random generator).
/// .NET removed RC4 from System.Security.Cryptography because RC4 is
/// broken for new use, but PDFs encrypted with R=2/R=3 (and some
/// R=4 with CFM=V2) still use it. Ciphers are symmetric, so the
/// same routine encrypts and decrypts.
/// </summary>
internal static class Rc4
{
    /// <summary>
    /// Encrypt or decrypt <paramref name="data"/> in-place equivalent —
    /// returns a new byte array with the result. Caller supplies the
    /// derived key bytes (for PDF, this is the per-object key, not the
    /// file encryption key).
    /// </summary>
    public static byte[] Transform(byte[] key, byte[] data)
    {
        var s = new byte[256];
        for (int i = 0; i < 256; i++) s[i] = (byte)i;

        // Key-scheduling algorithm
        int j = 0;
        for (int i = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }

        // Pseudo-random generation + XOR
        var output = new byte[data.Length];
        int ii = 0, jj = 0;
        for (int n = 0; n < data.Length; n++)
        {
            ii = (ii + 1) & 0xFF;
            jj = (jj + s[ii]) & 0xFF;
            (s[ii], s[jj]) = (s[jj], s[ii]);
            byte k = s[(s[ii] + s[jj]) & 0xFF];
            output[n] = (byte)(data[n] ^ k);
        }
        return output;
    }
}
