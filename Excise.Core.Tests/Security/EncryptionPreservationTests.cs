using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Parsing;
using Excise.Core.Security;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Security;

/// <summary>
/// #643: a document opened encrypted must be able to SAVE encrypted with the
/// same parameters. <see cref="PdfDocument.GetReEncryptionOptions"/> is the
/// core API — it reconstructs <see cref="PdfEncryptionOptions"/> from what
/// the open-time security handler retained (algorithm revision, /P mask,
/// /EncryptMetadata), combined with the caller-supplied password.
///
/// These are excise-internal round-trip checks. The independent-oracle
/// verification (qpdf structure/permissions, qpdf-decrypt byte scan, mutool
/// extraction) lives in
/// Excise.Rendering.Tests/Differential/EncryptionPreservationInteropTests.cs —
/// per CLAUDE.md, excise reopening its own output proves only self-consistency.
/// </summary>
public sealed class EncryptionPreservationTests
{
    private const long RestrictivePermissions = -3392; // print + assemble denied etc.

    [Fact]
    public void GetReEncryptionOptions_UnencryptedDocument_ReturnsNull()
    {
        using var doc = PdfDocument.Open(CreateSimplePdf("plain"));

        doc.GetReEncryptionOptions("anything").Should().BeNull(
            "an unencrypted source must stay unencrypted: Save(path, GetReEncryptionOptions(pw)) " +
            "must be a no-op passthrough for plaintext documents");
    }

    [Fact]
    public void GetReEncryptionOptions_R6Source_MapsToAes256_PreservingPermissionsAndMetadataFlag()
    {
        var encrypted = SaveEncrypted("R6 source", new PdfEncryptionOptions
        {
            UserPassword = "pw",
            OwnerPassword = "pw",
            Permissions = RestrictivePermissions,
            EncryptMetadata = false,
            Algorithm = PdfEncryptionAlgorithm.Aes256,
        });

        using var doc = PdfDocument.Open(encrypted, "pw");
        var options = doc.GetReEncryptionOptions("pw");

        options.Should().NotBeNull();
        options!.Algorithm.Should().Be(PdfEncryptionAlgorithm.Aes256, "V=5 R=6 round-trips as AES-256");
        options.Permissions.Should().Be(RestrictivePermissions, "the source /P mask must survive byte-identically");
        options.EncryptMetadata.Should().BeFalse("the source's /EncryptMetadata false must survive");
        options.UserPassword.Should().Be("pw");
        options.OwnerPassword.Should().Be("pw",
            "the source owner password is unrecoverable from a user-password open (#324); " +
            "reusing the user password grants no authority the caller didn't already have");
    }

    [Fact]
    public void GetReEncryptionOptions_R4AesSource_MapsToAes128()
    {
        var encrypted = SaveEncrypted("R4 source", new PdfEncryptionOptions
        {
            UserPassword = "pw",
            OwnerPassword = "pw",
            Permissions = RestrictivePermissions,
            Algorithm = PdfEncryptionAlgorithm.Aes128,
        });

        using var doc = PdfDocument.Open(encrypted, "pw");
        var options = doc.GetReEncryptionOptions("pw");

        options.Should().NotBeNull();
        options!.Algorithm.Should().Be(PdfEncryptionAlgorithm.Aes128,
            "a V=4 R=4 CFM=AESV2 source must round-trip as AES-128, not silently change revision");
        options.Permissions.Should().Be(RestrictivePermissions);
        options.EncryptMetadata.Should().BeTrue("default /EncryptMetadata true must survive");
    }

    [Fact]
    public void GetReEncryptionOptions_Rc4Source_UpgradesToAes256()
    {
        // RC4 R=3 (V=2, 128-bit) real-world fixture, user password "test",
        // restrictive P = -3904. excise's writer does not emit RC4 — the
        // documented policy is to upgrade to AES-256, never to downgrade
        // or silently decrypt.
        var path = ExistingFixturePath("test-pdfs/pdfjs/issue15893_reduced.pdf");

        using var doc = PdfDocument.Open(path, "test");
        var options = doc.GetReEncryptionOptions("test");

        options.Should().NotBeNull();
        options!.Algorithm.Should().Be(PdfEncryptionAlgorithm.Aes256,
            "RC4 sources re-encrypt as AES-256 (upgrade-only policy, #643)");
        options.Permissions.Should().Be(-3904, "the fixture's restrictive /P mask (qpdf-verified) must survive");
        options.UserPassword.Should().Be("test");
    }

    [Fact]
    public void SaveWithReEncryptionOptions_RoundTripsProtectionAndPermissions()
    {
        var encrypted = SaveEncrypted("Round trip body", new PdfEncryptionOptions
        {
            UserPassword = "hunter2",
            OwnerPassword = "hunter2",
            Permissions = RestrictivePermissions,
            Algorithm = PdfEncryptionAlgorithm.Aes256,
        });

        byte[] resaved;
        using (var doc = PdfDocument.Open(encrypted, "hunter2"))
        {
            resaved = doc.SaveToBytes(doc.GetReEncryptionOptions("hunter2"));
        }

        // Wrong/missing password must fail closed on the re-saved file.
        var wrongPassword = () => PdfDocument.Open(resaved);
        wrongPassword.Should().Throw<PdfEncryptionNotSupportedException>(
            "the re-saved file must still require the original password");

        using var reopened = PdfDocument.Open(resaved, "hunter2");
        reopened.IsEncrypted.Should().BeTrue("protection must survive the save round-trip (#643)");
        reopened.Permissions.RawValue.Should().Be(unchecked((int)RestrictivePermissions),
            "the /P mask must survive the round-trip");
        reopened.GetReEncryptionOptions("hunter2")!.Algorithm.Should().Be(PdfEncryptionAlgorithm.Aes256);
    }

    [Fact]
    public void Save_WithoutOptions_StillWritesPlaintext_ByDesign()
    {
        // The no-options Save()/SaveToBytes() default is deliberately
        // unchanged: "save = decrypt" stays explicit so no flow re-encrypts
        // by surprise. Callers opt in via GetReEncryptionOptions (#643).
        var encrypted = SaveEncrypted("Default save body", new PdfEncryptionOptions
        {
            UserPassword = "pw",
            Algorithm = PdfEncryptionAlgorithm.Aes256,
        });

        byte[] resaved;
        using (var doc = PdfDocument.Open(encrypted, "pw"))
        {
            resaved = doc.SaveToBytes();
        }

        using var reopened = PdfDocument.Open(resaved);
        reopened.IsEncrypted.Should().BeFalse("the parameterless Save contract is plaintext output");
    }

    [Fact]
    public void RedactEncryptedV4AesXrefStreamSource_DoesNotThrow_AndStaysEncrypted()
    {
        // #1048: a V=4/R=4/AESV2 source whose cross-reference data is an xref
        // STREAM crashed on Save. GetAllObjects() reached the xref-stream object
        // and AES-decrypted it — but §7.5.8.2 makes cross-reference streams
        // exempt from encryption, so AES-CBC threw "input data is not a complete
        // block" on its unencrypted bytes. excise cannot EMIT this shape (it
        // writes classic xref when encrypting), so the fixture is qpdf-generated
        // and checked in — no gitignored corpus, no qpdf on the test machine.
        var encrypted = LoadEmbeddedFixture("EncryptedV4AesXrefStream.pdf");
        Encoding.Latin1.GetString(encrypted).Should().Contain("/XRef",
            "the reproduction needs a cross-reference STREAM (the exempt object), not a classic table");

        using var doc = PdfDocument.Open(encrypted, "");
        doc.RedactText("REDACTME");

        // #643: an encrypted source re-encrypts like the source. This Save — the
        // GetAllObjects walk inside it — is exactly where #1048 threw.
        var options = doc.GetReEncryptionOptions("");
        options.Should().NotBeNull("an encrypted source must produce re-encryption options");
        var outBytes = doc.SaveToBytes(options!);

        using var reopened = PdfDocument.Open(outBytes, "");
        reopened.GetReEncryptionOptions("")!.Algorithm.Should().Be(
            PdfEncryptionAlgorithm.Aes128,
            "the output must stay encrypted and keep the source's V=4 AES-128 (#643)");
        reopened.GetPage(1).Text.Should().NotContain("REDACTME", "the redacted term must be gone")
            .And.Contain("keep", "surviving text must remain");
    }

    [Fact]
    public void RedactEncryptedPdf_PerStreamIdentityCryptFilter_DoesNotDecryptPlaintextStream()
    {
        // #1167: /StmF selects AESV2 for the document, but this particular
        // content stream explicitly selects /Crypt /Name /Identity. Its bytes
        // are plaintext by definition (§7.4.10), so applying the document
        // handler's AES-CBC decryptor used to throw "input data is not a
        // complete block" before redaction could inspect the page.
        const string secret = "IDENTITY-CRYPT-SECRET";
        var encrypted = CreateAes128PdfWithPlaintextIdentityContent(secret);

        using var doc = PdfDocument.Open(encrypted, "pw");
        doc.IsEncrypted.Should().BeTrue();
        doc.GetPage(1).Text.Should().Contain(secret,
            "the per-stream Identity override must leave the content stream readable");

        doc.RedactText(secret, drawBlackRect: false).VerifiedRemovals.Should().Be(1);
        var saved = doc.SaveToBytes(doc.GetReEncryptionOptions("pw"));

        using var reopened = PdfDocument.Open(saved, "pw");
        reopened.IsEncrypted.Should().BeTrue("#643 must still preserve source protection");
        reopened.GetPage(1).Text.Should().NotContain(secret);
        SavedPdfLeakScanner.FindTerm(reopened.SaveToBytes(), secret).Should().BeEmpty(
            "a per-stream identity override must not weaken glyph-level removal");
    }

    private static byte[] LoadEmbeddedFixture(string fileName)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith(fileName, StringComparison.Ordinal));
        using var s = asm.GetManifestResourceStream(name)!;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static byte[] SaveEncrypted(string text, PdfEncryptionOptions options)
    {
        using var doc = PdfDocument.Open(CreateSimplePdf(text));
        return doc.SaveToBytes(options);
    }

    /// <summary>
    /// Makes a compact V=4/R=4 AESV2 fixture entirely in memory. The normal
    /// writer encrypts every content stream, so start with its valid encryption
    /// dictionary and file ID, then replace only object 4 with a legal
    /// plaintext /Crypt /Identity stream and rebuild its classic xref table.
    /// This is deliberately self-contained: qpdf can emit the same shape, but
    /// the regression must not become skipped on machines without qpdf.
    /// </summary>
    private static byte[] CreateAes128PdfWithPlaintextIdentityContent(string text)
    {
        var encrypted = SaveEncrypted("writer-seed", new PdfEncryptionOptions
        {
            UserPassword = "pw",
            OwnerPassword = "pw",
            Algorithm = PdfEncryptionAlgorithm.Aes128,
        });

        var source = Encoding.Latin1.GetString(encrypted);
        // Search for the table's line boundary: the final startxref token
        // itself also ends in "xref\\n".
        var xrefOffset = source.LastIndexOf("\nxref\n0 ", StringComparison.Ordinal) + 1;
        var trailerOffset = source.IndexOf("trailer\n", xrefOffset, StringComparison.Ordinal);
        var startXrefOffset = source.IndexOf("startxref\n", trailerOffset, StringComparison.Ordinal);
        xrefOffset.Should().BeGreaterThanOrEqualTo(0);
        trailerOffset.Should().BeGreaterThan(xrefOffset);
        startXrefOffset.Should().BeGreaterThan(trailerOffset);

        var xrefLines = source[xrefOffset..trailerOffset]
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        xrefLines[0].Should().Be("xref");
        var xrefHeader = xrefLines[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        xrefHeader[0].Should().Be("0");
        var size = int.Parse(xrefHeader[1], System.Globalization.CultureInfo.InvariantCulture);
        var offsets = new long[size];
        for (var objectNumber = 1; objectNumber < size; objectNumber++)
        {
            // xrefLines[2] is object 0's free entry; object N follows it.
            var parts = xrefLines[objectNumber + 2]
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            offsets[objectNumber] = long.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
        }

        var content = $"BT /F1 12 Tf 100 700 Td ({text}) Tj ET";
        var replacement = Encoding.ASCII.GetBytes(
            $"4 0 obj\n<< /Length {content.Length} /Filter /Crypt /DecodeParms << /Name /Identity >> >>\nstream\n{content}\nendstream\nendobj\n");

        using var output = new MemoryStream();
        output.Write(encrypted, 0, checked((int)offsets[1]));
        var rebuiltOffsets = new long[size];
        for (var objectNumber = 1; objectNumber < size; objectNumber++)
        {
            rebuiltOffsets[objectNumber] = output.Position;
            if (objectNumber == 4)
            {
                output.Write(replacement);
                continue;
            }

            var nextOffset = objectNumber + 1 < size ? offsets[objectNumber + 1] : xrefOffset;
            output.Write(encrypted, checked((int)offsets[objectNumber]), checked((int)(nextOffset - offsets[objectNumber])));
        }

        var rebuiltXrefOffset = output.Position;
        using var writer = new StreamWriter(output, Encoding.ASCII, leaveOpen: true) { NewLine = "\n" };
        writer.WriteLine("xref");
        writer.WriteLine($"0 {size}");
        writer.WriteLine("0000000000 65535 f ");
        for (var objectNumber = 1; objectNumber < size; objectNumber++)
            writer.WriteLine($"{rebuiltOffsets[objectNumber]:D10} 00000 n ");
        writer.Write(source[trailerOffset..startXrefOffset]);
        writer.WriteLine("startxref");
        writer.WriteLine(rebuiltXrefOffset.ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.WriteLine("%%EOF");
        writer.Flush();
        return output.ToArray();
    }

    private static byte[] CreateSimplePdf(string text)
    {
        var content = $"BT /F1 12 Tf 100 700 Td ({text}) Tj ET";

        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, Encoding.ASCII, leaveOpen: true);
        writer.NewLine = "\n";

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
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine($"<< /Length {content.Length} >>");
        writer.WriteLine("stream");
        writer.Write(content);
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

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "excise.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root from test base directory.");
    }

    private static string ExistingFixturePath(string relativePath)
    {
        var path = Path.Combine(FindRepoRoot(), relativePath);
        Assert.SkipWhen(!File.Exists(path), $"Encrypted PDF fixture not available: {relativePath}");
        return path;
    }
}
