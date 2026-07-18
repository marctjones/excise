using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Excise.Cli.Tests;

/// <summary>
/// Builds small, self-contained single-page PDFs whose content stream
/// draws a given string via Helvetica at a fixed position. Used by the
/// CLI tests to produce known-content inputs whose bytes we can inspect
/// before and after <c>excise redact</c> runs.
/// </summary>
/// <remarks>
/// This is a near-clone of the <c>CreatePdfWithText</c> helper in
/// <c>Excise.Core.Tests</c>. Duplicated here rather than shared so the
/// test projects stay independent.
/// </remarks>
internal static class TestPdfBuilder
{
    public static byte[] SinglePage(string text, double fontSize = 12, double x = 100, double y = 700)
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\n" };

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[6];

        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[2] = ms.Position;
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[3] = ms.Position;
        writer.WriteLine("3 0 obj");
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                         "/Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>");
        writer.WriteLine("endobj");
        writer.Flush();

        var contentBody = $"BT /F1 {fontSize} Tf {x} {y} Td ({text}) Tj ET";
        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine($"<< /Length {contentBody.Length} >>");
        writer.WriteLine("stream");
        writer.Write(contentBody);
        writer.WriteLine();
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[5] = ms.Position;
        writer.WriteLine("5 0 obj");
        writer.WriteLine("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        writer.WriteLine("endobj");
        writer.Flush();

        long xrefPos = ms.Position;
        writer.WriteLine("xref");
        writer.WriteLine("0 6");
        writer.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 5; i++)
            writer.WriteLine($"{offsets[i]:D10} 00000 n ");
        writer.Flush();
        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Size 6 >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    /// <summary>32-byte password padding string, PDF32000-1 §7.6.3.3 Algorithm 2 step (a).</summary>
    private static readonly byte[] PasswordPadding =
    {
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41,
        0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80,
        0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A
    };

    private static byte[] Rc4(byte[] key, byte[] data)
    {
        var s = new byte[256];
        for (int i = 0; i < 256; i++) s[i] = (byte)i;
        int j = 0;
        for (int i = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }

        var output = new byte[data.Length];
        int a = 0, b = 0;
        for (int k = 0; k < data.Length; k++)
        {
            a = (a + 1) & 0xFF;
            b = (b + s[a]) & 0xFF;
            (s[a], s[b]) = (s[b], s[a]);
            output[k] = (byte)(data[k] ^ s[(s[a] + s[b]) & 0xFF]);
        }

        return output;
    }

    /// <summary>
    /// A single-page PDF encrypted with the Standard security handler,
    /// V=1 R=2 (40-bit RC4), empty owner AND user password — the common
    /// "restricted but not password-protected" case: <c>PdfDocument.Open</c>
    /// opens it with no password (the empty password satisfies Algorithm 6),
    /// and <c>IsEncrypted</c> is true. Computes /O and /U by hand
    /// (PDF32000-1 §7.6.3.3 Algorithms 2-4) since excise's writer cannot
    /// produce encrypted output itself (#624) — used by #638's tests to
    /// exercise the "source is encrypted" guard without needing a real
    /// password-protected fixture. The content stream is left as plaintext:
    /// callers that only need <c>IsEncrypted</c> to be true before their
    /// check fires (never reading page content) don't need it decrypted.
    /// </summary>
    public static byte[] EncryptedSinglePageEmptyPassword(string text = "SECRET DATA")
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\n" };

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[7];

        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[2] = ms.Position;
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[3] = ms.Position;
        writer.WriteLine("3 0 obj");
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                         "/Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>");
        writer.WriteLine("endobj");
        writer.Flush();

        var contentBody = $"BT /F1 12 Tf 100 700 Td ({text}) Tj ET";
        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine($"<< /Length {contentBody.Length} >>");
        writer.WriteLine("stream");
        writer.Write(contentBody);
        writer.WriteLine();
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[5] = ms.Position;
        writer.WriteLine("5 0 obj");
        writer.WriteLine("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Algorithm 3: /O, empty owner AND user password (both pad to the
        // same 32-byte constant). Key length for R=2 is always 5 bytes.
        var ownerKey = MD5.HashData(PasswordPadding).AsSpan(0, 5).ToArray();
        var o = Rc4(ownerKey, PasswordPadding);

        const long permissions = -3904; // arbitrary but must be consistent with the /P entry below.
        var idBytes = new byte[16];
        for (int i = 0; i < 16; i++) idBytes[i] = (byte)i;

        // Algorithm 2: file encryption key. R=2 has no 50x MD5 re-hash.
        using var md5 = MD5.Create();
        md5.TransformBlock(PasswordPadding, 0, 32, null, 0);
        md5.TransformBlock(o, 0, 32, null, 0);
        var pBytes = new byte[]
        {
            (byte)(permissions & 0xFF),
            (byte)((permissions >> 8) & 0xFF),
            (byte)((permissions >> 16) & 0xFF),
            (byte)((permissions >> 24) & 0xFF)
        };
        md5.TransformBlock(pBytes, 0, 4, null, 0);
        md5.TransformFinalBlock(idBytes, 0, idBytes.Length);
        var fileKey = md5.Hash!.AsSpan(0, 5).ToArray();

        // Algorithm 4: /U for R=2 is RC4(fileKey, padding) — no MD5 step.
        var u = Rc4(fileKey, PasswordPadding);

        var idHex = Convert.ToHexString(idBytes);
        offsets[6] = ms.Position;
        writer.WriteLine("6 0 obj");
        writer.WriteLine("<< /Filter /Standard /V 1 /R 2 " +
                         $"/O <{Convert.ToHexString(o)}> /U <{Convert.ToHexString(u)}> /P {permissions} >>");
        writer.WriteLine("endobj");
        writer.Flush();

        long xrefPos = ms.Position;
        writer.WriteLine("xref");
        writer.WriteLine("0 7");
        writer.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 6; i++)
            writer.WriteLine($"{offsets[i]:D10} 00000 n ");
        writer.Flush();
        writer.WriteLine("trailer");
        writer.WriteLine($"<< /Root 1 0 R /Size 7 /Encrypt 6 0 R /ID [<{idHex}> <{idHex}>] >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }
}
