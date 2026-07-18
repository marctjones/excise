using System;
using System.IO;
using System.Text;
using AwesomeAssertions;
using Pdfe.Core.Document;
using Pdfe.Core.Graphics;
using Pdfe.Core.Security;
using Pdfe.Core.Text.Segmentation;
using Pdfe.Core.Writing;
using Pdfe.Rendering.Differential;
using Xunit;

namespace Pdfe.Rendering.Tests.Differential;

/// <summary>
/// Independent-oracle verification for #643: redacting/editing an encrypted
/// document and saving with <see cref="PdfDocument.GetReEncryptionOptions"/>
/// must yield output that (a) is still encrypted with the SAME revision and
/// permissions, and (b) no longer contains the redacted secret in ANY
/// carrier of the decrypted file.
///
/// The stakes here are the highest intersection in the codebase: a redaction
/// leak hidden under fresh encryption would be reported as "redacted and
/// still protected" while the secret sits in the ciphertext, recoverable by
/// anyone holding the (known!) password. Per CLAUDE.md's no-self-oracle
/// rule, every assertion about the output goes through qpdf and mutool, not
/// pdfe. And because the saved bytes are ciphertext, a naive byte-scan of
/// them proves nothing — the leak scan runs over qpdf's independently
/// DECRYPTED (and uncompressed) serialization.
/// </summary>
public class EncryptionPreservationInteropTests : IDisposable
{
    private const string Secret = "TOPSECRETWORD";
    private const string Survivor = "SURVIVORTEXT";
    private const long RestrictivePermissions = -3392;

    private readonly string _tempDir;

    public EncryptionPreservationInteropTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"pdfe-enc-preserve-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Redact_R6Source_OutputSameR6AndPermissions_SecretUnrecoverableByQpdfAndMutool()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not on PATH");
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not on PATH");

        var sourcePath = SaveEncryptedSource(new PdfEncryptionOptions
        {
            UserPassword = "hunter2",
            OwnerPassword = "hunter2",
            Permissions = RestrictivePermissions,
            Algorithm = PdfEncryptionAlgorithm.Aes256,
        });

        var outputPath = RedactAndSavePreserved(sourcePath, "hunter2");

        // (a) Same protection: qpdf's independent parser agrees the output
        // is encrypted, same revision, byte-identical /P.
        QpdfReferenceTool.IsEncrypted(outputPath).Should().BeTrue(
            "redacting a password-protected PDF must yield a password-protected PDF (#643)");
        var show = QpdfReferenceTool.ShowEncryption(outputPath, "hunter2");
        show.Should().NotBeNull();
        show.Should().Contain("R = 6", "an R=6 source must round-trip as R=6");
        show.Should().Contain($"P = {RestrictivePermissions}", "the restrictive /P mask must survive byte-identically");

        // (b) The secret is unrecoverable — by qpdf's decrypted bytes and by
        // mutool's extraction, neither of which is pdfe.
        AssertSecretGoneAndSurvivorIntact(outputPath, "hunter2");
    }

    [Fact]
    public void Redact_R4AesSource_OutputSameR4_SecretUnrecoverableByQpdfAndMutool()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not on PATH");
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not on PATH");

        var sourcePath = SaveEncryptedSource(new PdfEncryptionOptions
        {
            UserPassword = "pw-r4",
            OwnerPassword = "pw-r4",
            Permissions = RestrictivePermissions,
            Algorithm = PdfEncryptionAlgorithm.Aes128,
        });

        var outputPath = RedactAndSavePreserved(sourcePath, "pw-r4");

        QpdfReferenceTool.IsEncrypted(outputPath).Should().BeTrue();
        var show = QpdfReferenceTool.ShowEncryption(outputPath, "pw-r4");
        show.Should().NotBeNull();
        show.Should().Contain("R = 4", "an R=4 source must round-trip as R=4, not silently change revision");
        show.Should().Contain("AESv2", "the AES-128 crypt filter must survive");
        show.Should().Contain($"P = {RestrictivePermissions}");

        AssertSecretGoneAndSurvivorIntact(outputPath, "pw-r4");
    }

    [Fact]
    public void Redact_Rc4Fixture_UpgradesToR6_PermissionsSurvive_SecretUnrecoverable()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not on PATH");
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not on PATH");

        // Real-world RC4 R=3 fixture, user password "test", P = -3904
        // (qpdf-verified), page text contains "Issue 15893". pdfe's writer
        // does not emit RC4; the documented policy is upgrade to AES-256.
        var fixturePath = ExistingFixturePathOrSkip("test-pdfs/pdfjs/issue15893_reduced.pdf");
        const string fixtureSecret = "Issue 15893";
        const string password = "test";

        var outputPath = Path.Combine(_tempDir, "rc4-upgraded.pdf");
        using (var doc = PdfDocument.Open(File.ReadAllBytes(fixturePath), password))
        {
            doc.RedactText(fixtureSecret, drawBlackRect: false).Should().BeGreaterThan(0,
                "the fixture's known secret must be found before this test can prove anything");
            doc.Save(outputPath, doc.GetReEncryptionOptions(password));
        }

        QpdfReferenceTool.IsEncrypted(outputPath).Should().BeTrue();
        var show = QpdfReferenceTool.ShowEncryption(outputPath, password);
        show.Should().NotBeNull();
        show.Should().Contain("R = 6", "RC4 sources are re-encrypted as AES-256 R=6 — upgrade, never downgrade (#643)");
        show.Should().Contain("P = -3904", "the fixture's restrictive /P mask must survive the upgrade");

        var decryptedBytes = QpdfDecryptedBytes(outputPath, password);
        ScannableText(decryptedBytes).Should().NotContain(fixtureSecret,
            "the redacted secret must not survive in any carrier of the decrypted output");

        var extracted = MutoolTextExtractor.ExtractPage(outputPath, 1, password);
        extracted.Should().NotBeNull("mutool must still be able to open the re-encrypted output");
        extracted.Should().NotContain(fixtureSecret);
    }

    [Fact]
    public void Preserve_EncryptMetadataFalse_SurvivesRoundTrip()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not on PATH");

        var sourcePath = SaveEncryptedSource(new PdfEncryptionOptions
        {
            UserPassword = "meta-pw",
            OwnerPassword = "meta-pw",
            EncryptMetadata = false,
            Algorithm = PdfEncryptionAlgorithm.Aes256,
        });

        var outputPath = RedactAndSavePreserved(sourcePath, "meta-pw");

        // /EncryptMetadata is cleartext structure in the /Encrypt dict, so a
        // raw byte scan of the OUTPUT (not a pdfe reparse) can verify it.
        var raw = Encoding.Latin1.GetString(File.ReadAllBytes(outputPath));
        raw.Should().Contain("/EncryptMetadata false",
            "the source's metadata-coverage choice must survive the round-trip");

        var check = QpdfReferenceTool.Check(outputPath, "meta-pw");
        check.Should().NotBeNull();
        check!.Value.Success.Should().BeTrue($"qpdf --check reported problems:\n{check.Value.Output}");
    }

    #region Helpers

    /// <summary>
    /// A pdfe-authored single-page document whose text contains both the
    /// secret (to be redacted) and a survivor marker (to prove the redaction
    /// didn't destroy unrelated content), saved encrypted with
    /// <paramref name="options"/>.
    /// </summary>
    private string SaveEncryptedSource(PdfEncryptionOptions options)
    {
        using var plain = PdfDocument.CreateNew();
        var page = plain.Pages.AddBlank(400, 200);
        var font = PdfFont.Helvetica(12);
        using (var g = page.GetGraphics())
        {
            g.DrawText($"{Survivor} then {Secret}", font, PdfBrush.Black, new PdfRectangle(20, 20, 380, 180));
        }

        using var doc = PdfDocument.Open(plain.SaveToBytes());
        var path = Path.Combine(_tempDir, $"src-{Guid.NewGuid():N}.pdf");
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            new PdfDocumentWriter(doc, options).Write(fs);
        }
        return path;
    }

    /// <summary>
    /// The #643 flow under test: open with the password, glyph-redact the
    /// secret, save with <see cref="PdfDocument.GetReEncryptionOptions"/>.
    /// </summary>
    private string RedactAndSavePreserved(string sourcePath, string password)
    {
        var outputPath = Path.Combine(_tempDir, $"out-{Guid.NewGuid():N}.pdf");
        using var doc = PdfDocument.Open(File.ReadAllBytes(sourcePath), password);
        doc.RedactText(Secret, drawBlackRect: false).Should().BeGreaterThan(0,
            "the secret must be found and removed before this test can prove anything");
        doc.Save(outputPath, doc.GetReEncryptionOptions(password));
        return outputPath;
    }

    private byte[] QpdfDecryptedBytes(string encryptedPath, string password)
    {
        var decryptedPath = Path.Combine(_tempDir, $"dec-{Guid.NewGuid():N}.pdf");
        QpdfReferenceTool.Decrypt(encryptedPath, decryptedPath, password, uncompressStreams: true)
            .Should().BeTrue("qpdf must be able to independently decrypt the preserved output");
        return File.ReadAllBytes(decryptedPath);
    }

    /// <summary>ASCII/Latin-1 and UTF-16BE views of the bytes, concatenated — the carrier-agnostic scan surface.</summary>
    private static string ScannableText(byte[] bytes)
        => Encoding.Latin1.GetString(bytes) + Encoding.BigEndianUnicode.GetString(bytes);

    private void AssertSecretGoneAndSurvivorIntact(string outputPath, string password)
    {
        // Carrier-agnostic scan of the independently decrypted, uncompressed
        // serialization: if the secret is anywhere in the file, this fails.
        var scannable = ScannableText(QpdfDecryptedBytes(outputPath, password));
        scannable.Should().NotContain(Secret,
            "the redacted secret must not survive in ANY carrier of the decrypted output — " +
            "encryption must never become a place for a redaction leak to hide");
        scannable.Should().Contain(Survivor, "unrelated content must survive the redaction round-trip");

        // Independent extractor agrees.
        var extracted = MutoolTextExtractor.ExtractPage(outputPath, 1, password);
        extracted.Should().NotBeNull("mutool must still be able to open and read the re-encrypted output");
        extracted.Should().NotContain(Secret);
        extracted.Should().Contain(Survivor);
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

    private static string ExistingFixturePathOrSkip(string relativePath)
    {
        var path = Path.Combine(FindRepoRoot(), relativePath);
        Assert.SkipWhen(!File.Exists(path), $"Encrypted PDF fixture not available: {relativePath}");
        return path;
    }

    #endregion
}
