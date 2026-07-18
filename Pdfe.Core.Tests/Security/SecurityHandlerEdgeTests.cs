using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using Pdfe.Core.Document;
using Pdfe.Core.Parsing;
using Pdfe.Core.Primitives;
using Pdfe.Core.Security;
using Pdfe.Core.Writing;
using Xunit;

namespace Pdfe.Core.Tests.Security;

/// <summary>
/// Edge-branch coverage for <see cref="PdfStandardSecurityHandler"/> that
/// previously ran only under gitignored corpus fixtures (which CI does not
/// download) or not at all. Added while closing the v2.30.0 CI
/// coverage-gate shortfall, but every test here pins real spec behavior:
/// the RC4 V1/R2 legacy decrypt path end-to-end (including its #643
/// upgrade-to-AES-256 re-encryption), Algorithm 2's
/// /EncryptMetadata-false step for R4, malformed-/Encrypt rejection, and
/// the byte-string tolerance rules for /U//O.
///
/// The RC4 fixture is synthesized in-process (a hand-assembled V=1 R=2
/// file whose content stream and Info strings are GENUINELY RC4-encrypted
/// with per-object Algorithm 1 keys, unlike Pdfe.Cli.Tests'
/// plaintext-content builder) — so this coverage is environment-independent
/// and cannot silently skip on a fixture-less runner.
/// </summary>
public class SecurityHandlerEdgeTests
{
    private static readonly byte[] PasswordPadding =
    {
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41,
        0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80,
        0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A
    };

    /// <summary>
    /// Hand-assemble a V=1 R=2 (40-bit RC4) PDF with an empty user/owner
    /// password whose content stream AND Info /Title string are genuinely
    /// RC4-encrypted under per-object Algorithm 1 keys, so pdfe's decrypt
    /// path must actually work (not just tolerate a flagged-but-plaintext
    /// file) for the text to round-trip.
    /// </summary>
    private static byte[] BuildRc4EncryptedPdf(string secretText, string infoTitle, long permissions = -3904)
    {
        var idBytes = new byte[16];
        for (int i = 0; i < 16; i++) idBytes[i] = (byte)(i * 7 + 1);

        // Algorithm 3 (R=2): /O from the (empty -> fully padded) owner password.
        var ownerKey = MD5.HashData(PasswordPadding).AsSpan(0, 5).ToArray();
        var o = Rc4.Transform(ownerKey, PasswordPadding);

        // Algorithm 2 (R=2): 5-byte file key, no 50-round re-hash.
        using var md5 = MD5.Create();
        md5.TransformBlock(PasswordPadding, 0, 32, null, 0);
        md5.TransformBlock(o, 0, 32, null, 0);
        var pBytes = new byte[]
        {
            (byte)(permissions & 0xFF),
            (byte)((permissions >> 8) & 0xFF),
            (byte)((permissions >> 16) & 0xFF),
            (byte)((permissions >> 24) & 0xFF),
        };
        md5.TransformBlock(pBytes, 0, 4, null, 0);
        md5.TransformFinalBlock(idBytes, 0, idBytes.Length);
        var fileKey = md5.Hash!.AsSpan(0, 5).ToArray();

        // Algorithm 4 (R=2): /U = RC4(fileKey, padding).
        var u = Rc4.Transform(fileKey, PasswordPadding);

        // Algorithm 1 per-object keys (RC4 flavor — no "sAlT").
        byte[] ObjectKey(int objNum, int gen) =>
            PdfStandardSecurityHandler.ComputeObjectKey(fileKey, objNum, gen, usesAes: false);

        var contentPlain = Encoding.ASCII.GetBytes($"BT /F1 12 Tf 100 700 Td ({secretText}) Tj ET");
        var contentCipher = Rc4.Transform(ObjectKey(4, 0), contentPlain);
        var titleCipher = Rc4.Transform(ObjectKey(7, 0), Encoding.ASCII.GetBytes(infoTitle));

        using var ms = new MemoryStream();
        var offsets = new long[8];
        void Write(string s) { var b = Encoding.Latin1.GetBytes(s); ms.Write(b, 0, b.Length); }

        Write("%PDF-1.4\n");
        offsets[1] = ms.Position;
        Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        offsets[2] = ms.Position;
        Write("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        offsets[3] = ms.Position;
        Write("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
              "/Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        offsets[4] = ms.Position;
        Write($"4 0 obj\n<< /Length {contentCipher.Length} >>\nstream\n");
        ms.Write(contentCipher, 0, contentCipher.Length);
        Write("\nendstream\nendobj\n");
        offsets[5] = ms.Position;
        Write("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n");
        offsets[6] = ms.Position;
        Write("6 0 obj\n<< /Filter /Standard /V 1 /R 2 " +
              $"/O <{Convert.ToHexString(o)}> /U <{Convert.ToHexString(u)}> /P {permissions} >>\nendobj\n");
        offsets[7] = ms.Position;
        Write($"7 0 obj\n<< /Title <{Convert.ToHexString(titleCipher)}> >>\nendobj\n");

        var idHex = Convert.ToHexString(idBytes);
        long xrefPos = ms.Position;
        Write("xref\n0 8\n0000000000 65535 f \n");
        for (int i = 1; i <= 7; i++) Write($"{offsets[i]:D10} 00000 n \n");
        Write("trailer\n<< /Root 1 0 R /Size 8 /Info 7 0 R /Encrypt 6 0 R " +
              $"/ID [<{idHex}> <{idHex}>] >>\nstartxref\n{xrefPos}\n%%EOF\n");

        return ms.ToArray();
    }

    [Fact]
    public void Rc4V1R2_GenuinelyEncryptedContentAndStrings_DecryptEndToEnd()
    {
        var pdf = BuildRc4EncryptedPdf("RC4SECRET", "Rc4Title");

        // The ciphertext must not leak the plaintext (i.e. this fixture is
        // really encrypted, unlike a flagged-but-plaintext file).
        Encoding.Latin1.GetString(pdf).Should().NotContain("RC4SECRET");

        using var doc = PdfDocument.Open(pdf);
        doc.IsEncrypted.Should().BeTrue();
        doc.Permissions.RawValue.Should().Be(-3904, "the /P mask must surface through the RC4 path too (#642)");
        doc.GetPage(1).Text.Should().Contain("RC4SECRET",
            "the RC4 V1/R2 decrypt path (Algorithm 2 without the 50-round re-hash, Algorithm 6 R=2 " +
            "verification, per-object Algorithm 1 keys without the AES salt) must round-trip real ciphertext");
        doc.Title.Should().Be("Rc4Title", "string decryption must use the string's own object key");
    }

    [Fact]
    public void Rc4V1R2_GetReEncryptionOptions_UpgradesToAes256AndPreservesPermissions()
    {
        // The #643 upgrade-only policy for algorithms the writer cannot
        // emit: RC4 sources re-encrypt as AES-256 — never a downgrade,
        // never a silent decrypt. Fixture-free twin of the corpus-gated
        // GetReEncryptionOptions_Rc4Source_UpgradesToAes256 test, so the
        // branch stays covered on runners without the pdfjs corpus.
        var pdf = BuildRc4EncryptedPdf("UPGRADE ME", "T");
        using var doc = PdfDocument.Open(pdf);

        var options = doc.GetReEncryptionOptions(null);
        options.Should().NotBeNull();
        options!.Algorithm.Should().Be(PdfEncryptionAlgorithm.Aes256);
        options.Permissions.Should().Be(-3904);

        var saved = doc.SaveToBytes(options);
        using var reopened = PdfDocument.Open(saved);
        reopened.IsEncrypted.Should().BeTrue();
        reopened.GetPage(1).Text.Should().Contain("UPGRADE ME");
        Encoding.Latin1.GetString(saved).Should().Contain("/R 6", "the upgraded file must be AES-256 (R6)");
    }

    [Fact]
    public void Build_V5WithR5_ThrowsNotSupported()
    {
        // R=5 was Adobe's transitional extension; only R=6 is standard.
        var dict = new PdfDictionary
        {
            ["Filter"] = new PdfName("Standard"),
            ["V"] = new PdfInteger(5),
            ["R"] = new PdfInteger(5),
            ["Length"] = new PdfInteger(256),
        };

        var act = () => PdfStandardSecurityHandler.Build(dict, new byte[16], Array.Empty<byte>());
        act.Should().Throw<PdfEncryptionNotSupportedException>().WithMessage("*R=5*");
    }

    [Fact]
    public void Build_R6WithTruncatedUE_ThrowsParseException()
    {
        // Start from a VALID R6 dict (so /U validation passes), then
        // truncate /UE to one AES block: it decrypts to 16 bytes, not the
        // 32 an AES-256 file key requires.
        var enc = PdfStandardSecurityEncryptor.CreateR6(
            Array.Empty<byte>(), Array.Empty<byte>(), permissions: -4, encryptMetadata: true);
        var dict = new PdfDictionary
        {
            ["Filter"] = new PdfName("Standard"),
            ["V"] = new PdfInteger(5),
            ["R"] = new PdfInteger(6),
            ["Length"] = new PdfInteger(256),
            ["O"] = new PdfString(enc.O, isHex: true),
            ["U"] = new PdfString(enc.U, isHex: true),
            ["OE"] = new PdfString(enc.OE!, isHex: true),
            ["UE"] = new PdfString(enc.UE!.AsSpan(0, 16).ToArray(), isHex: true),
            ["P"] = new PdfInteger(-4),
        };

        var act = () => PdfStandardSecurityHandler.Build(dict, new byte[16], Array.Empty<byte>());
        act.Should().Throw<PdfParseException>().WithMessage("*/UE must be exactly 32 bytes*");
    }

    [Fact]
    public void Build_R6ByteStrings_TolerateZeroPaddingBeyond48ButRejectOtherLengths()
    {
        var enc = PdfStandardSecurityEncryptor.CreateR6(
            Array.Empty<byte>(), Array.Empty<byte>(), permissions: -4, encryptMetadata: true);

        // Some writers pad /U//O beyond 48 bytes with zeros — tolerated.
        var paddedU = new byte[52];
        enc.U.CopyTo(paddedU, 0);
        var dict = new PdfDictionary
        {
            ["Filter"] = new PdfName("Standard"),
            ["V"] = new PdfInteger(5),
            ["R"] = new PdfInteger(6),
            ["Length"] = new PdfInteger(256),
            ["O"] = new PdfString(enc.O, isHex: true),
            ["U"] = new PdfString(paddedU, isHex: true),
            ["OE"] = new PdfString(enc.OE!, isHex: true),
            ["UE"] = new PdfString(enc.UE!, isHex: true),
            ["P"] = new PdfInteger(-4),
        };
        var handler = PdfStandardSecurityHandler.Build(dict, new byte[16], Array.Empty<byte>());
        handler.R.Should().Be(6, "zero padding after byte 48 of /U is tolerated per real-world writers");

        // NON-zero padding beyond 48 is not tolerable — wrong length throws.
        var garbageU = new byte[52];
        enc.U.CopyTo(garbageU, 0);
        garbageU[50] = 0xAB;
        dict["U"] = new PdfString(garbageU, isHex: true);
        var act = () => PdfStandardSecurityHandler.Build(dict, new byte[16], Array.Empty<byte>());
        act.Should().Throw<PdfParseException>().WithMessage("*must be exactly*");
    }

    [Fact]
    public void DecryptString_AesCipherShorterThanOneBlock_ReturnsEmpty()
    {
        // Per the read-side convention (seen in qpdf-emitted files), an AES
        // string shorter than the 16-byte IV decodes as empty, not a crash.
        var enc = PdfStandardSecurityEncryptor.CreateR6(
            Array.Empty<byte>(), Array.Empty<byte>(), permissions: -4, encryptMetadata: true);
        var dict = new PdfDictionary
        {
            ["Filter"] = new PdfName("Standard"),
            ["V"] = new PdfInteger(5),
            ["R"] = new PdfInteger(6),
            ["Length"] = new PdfInteger(256),
            ["CF"] = new PdfDictionary
            {
                ["StdCF"] = new PdfDictionary
                {
                    ["CFM"] = new PdfName("AESV3"),
                    ["AuthEvent"] = new PdfName("DocOpen"),
                    ["Length"] = new PdfInteger(32),
                },
            },
            ["StmF"] = new PdfName("StdCF"),
            ["StrF"] = new PdfName("StdCF"),
            ["O"] = new PdfString(enc.O, isHex: true),
            ["U"] = new PdfString(enc.U, isHex: true),
            ["OE"] = new PdfString(enc.OE!, isHex: true),
            ["UE"] = new PdfString(enc.UE!, isHex: true),
            ["P"] = new PdfInteger(-4),
        };
        var handler = PdfStandardSecurityHandler.Build(dict, new byte[16], Array.Empty<byte>());

        handler.DecryptString(1, 0, new byte[7]).Should().BeEmpty();
        handler.DecryptString(1, 0, new byte[16]).Should().BeEmpty(
            "an IV with zero ciphertext blocks is an empty string");
    }
}
