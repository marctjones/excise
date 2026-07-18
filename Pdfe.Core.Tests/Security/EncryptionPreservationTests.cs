using System.IO;
using System.Text;
using AwesomeAssertions;
using Pdfe.Core.Document;
using Pdfe.Core.Parsing;
using Pdfe.Core.Security;
using Xunit;

namespace Pdfe.Core.Tests.Security;

/// <summary>
/// #643: a document opened encrypted must be able to SAVE encrypted with the
/// same parameters. <see cref="PdfDocument.GetReEncryptionOptions"/> is the
/// core API — it reconstructs <see cref="PdfEncryptionOptions"/> from what
/// the open-time security handler retained (algorithm revision, /P mask,
/// /EncryptMetadata), combined with the caller-supplied password.
///
/// These are pdfe-internal round-trip checks. The independent-oracle
/// verification (qpdf structure/permissions, qpdf-decrypt byte scan, mutool
/// extraction) lives in
/// Pdfe.Rendering.Tests/Differential/EncryptionPreservationInteropTests.cs —
/// per CLAUDE.md, pdfe reopening its own output proves only self-consistency.
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
        // restrictive P = -3904. pdfe's writer does not emit RC4 — the
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

    private static byte[] SaveEncrypted(string text, PdfEncryptionOptions options)
    {
        using var doc = PdfDocument.Open(CreateSimplePdf(text));
        return doc.SaveToBytes(options);
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
            if (File.Exists(Path.Combine(dir.FullName, "pdfe.sln")))
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
