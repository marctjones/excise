using System.IO;
using System.Text;
using AwesomeAssertions;
using Excise.Cli;
using Excise.Core.Document;
using Excise.Core.Security;
using Xunit;

namespace Excise.Cli.Tests;

/// <summary>
/// Tests for the <c>excise encrypt</c> / <c>excise decrypt</c> subcommands
/// (#641), exercising the internal <see cref="Program.RunEncrypt"/> /
/// <see cref="Program.RunDecrypt"/> cores, mirroring
/// <see cref="RedactCommandTests"/>' pattern. The Standard Security
/// Handler writer itself is already independently verified against
/// qpdf/mutool/Ghostscript by
/// <c>Excise.Rendering.Tests/Differential/EncryptionWriterInteropTests.cs</c>
/// — these tests cover the CLI layer's guards and round-trip wiring, plus
/// saved-bytes checks that the plaintext genuinely leaves the file.
/// </summary>
public class EncryptDecryptCommandTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            if (File.Exists(f)) try { File.Delete(f); } catch { }
    }

    private string TempPath(string suffix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-cli-test-{Guid.NewGuid():N}{suffix}");
        _tempFiles.Add(path);
        return path;
    }

    [Fact]
    public void RunEncrypt_Aes256_ProducesEncryptedFile_PlaintextGoneFromSavedBytes()
    {
        var inputPath = TempPath(".pdf");
        var outputPath = TempPath(".pdf");
        File.WriteAllBytes(inputPath, TestPdfBuilder.SinglePage("CLI ENCRYPT MARKER"));

        Program.RunEncrypt(inputPath, outputPath, "user-pw", "owner-pw",
            permissions: -4, PdfEncryptionAlgorithm.Aes256, encryptMetadata: true);

        var saved = File.ReadAllBytes(outputPath);
        Encoding.Latin1.GetString(saved).Should().NotContain("MARKER",
            "content-stream plaintext must not survive encryption in the saved bytes");
        Encoding.Latin1.GetString(saved).Should().Contain("/Encrypt");

        using var reopened = PdfDocument.Open(saved, "user-pw");
        reopened.IsEncrypted.Should().BeTrue();
        reopened.GetPage(1).Text.Should().Contain("CLI ENCRYPT MARKER");
    }

    [Fact]
    public void RunEncrypt_Aes128_ReportsR4InTheEncryptDictionary()
    {
        var inputPath = TempPath(".pdf");
        var outputPath = TempPath(".pdf");
        File.WriteAllBytes(inputPath, TestPdfBuilder.SinglePage("R4 CONTENT"));

        Program.RunEncrypt(inputPath, outputPath, "pw", null,
            permissions: -4, PdfEncryptionAlgorithm.Aes128, encryptMetadata: true);

        var savedText = Encoding.Latin1.GetString(File.ReadAllBytes(outputPath));
        savedText.Should().Contain("/R 4");
        savedText.Should().Contain("/AESV2");
        savedText.Should().NotContain("CONTENT");
    }

    [Fact]
    public void RunEncrypt_AlreadyEncryptedSource_ThrowsWithDecryptFirstGuidance()
    {
        var inputPath = TempPath(".pdf");
        var midPath = TempPath(".pdf");
        var outputPath = TempPath(".pdf");
        File.WriteAllBytes(inputPath, TestPdfBuilder.SinglePage("X"));
        Program.RunEncrypt(inputPath, midPath, "pw", null, -4, PdfEncryptionAlgorithm.Aes256, true);

        var act = () => Program.RunEncrypt(midPath, outputPath, "new-pw", null, -4, PdfEncryptionAlgorithm.Aes256, true);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*already encrypted*decrypt*",
                "a password-protected source must get the decrypt-first guidance, not a confusing " +
                "password-verification error that reads as the NEW password being wrong");
    }

    [Fact]
    public void RunDecrypt_RoundTrip_RemovesEncryptionAndPreservesContent()
    {
        var inputPath = TempPath(".pdf");
        var encPath = TempPath(".pdf");
        var decPath = TempPath(".pdf");
        File.WriteAllBytes(inputPath, TestPdfBuilder.SinglePage("ROUNDTRIP CONTENT"));
        Program.RunEncrypt(inputPath, encPath, "open-pw", null, -4, PdfEncryptionAlgorithm.Aes256, true);

        Program.RunDecrypt(encPath, decPath, "open-pw");

        var saved = File.ReadAllBytes(decPath);
        Encoding.Latin1.GetString(saved).Should().NotContain("/Encrypt",
            "decrypt's whole purpose is stripping the encryption dictionary");

        using var reopened = PdfDocument.Open(saved);
        reopened.IsEncrypted.Should().BeFalse();
        reopened.GetPage(1).Text.Should().Contain("ROUNDTRIP CONTENT");
    }

    [Fact]
    public void RunDecrypt_UnencryptedSource_Throws()
    {
        var inputPath = TempPath(".pdf");
        var outputPath = TempPath(".pdf");
        File.WriteAllBytes(inputPath, TestPdfBuilder.SinglePage("PLAIN"));

        var act = () => Program.RunDecrypt(inputPath, outputPath, null);

        act.Should().Throw<InvalidOperationException>().WithMessage("*not encrypted*");
    }

    [Fact]
    public void RunDecrypt_WrongPassword_Throws()
    {
        var inputPath = TempPath(".pdf");
        var encPath = TempPath(".pdf");
        var outputPath = TempPath(".pdf");
        File.WriteAllBytes(inputPath, TestPdfBuilder.SinglePage("X"));
        Program.RunEncrypt(inputPath, encPath, "right-pw", null, -4, PdfEncryptionAlgorithm.Aes256, true);

        var act = () => Program.RunDecrypt(encPath, outputPath, "wrong-pw");

        act.Should().Throw<Exception>("a wrong password must never silently produce a decrypted file");
        File.Exists(outputPath).Should().BeFalse();
    }

    [Fact]
    public void ChangePasswordFlow_DecryptThenEncrypt_OldPasswordStopsWorking()
    {
        // The documented "change password" flow: decrypt with the old
        // password, encrypt the result with the new one. The old password
        // must no longer open the final file; the new one must.
        var inputPath = TempPath(".pdf");
        var v1 = TempPath(".pdf");
        var plain = TempPath(".pdf");
        var v2 = TempPath(".pdf");
        File.WriteAllBytes(inputPath, TestPdfBuilder.SinglePage("SEKRIT"));
        Program.RunEncrypt(inputPath, v1, "old-pw", null, -4, PdfEncryptionAlgorithm.Aes256, true);
        Program.RunDecrypt(v1, plain, "old-pw");
        Program.RunEncrypt(plain, v2, "new-pw", null, -4, PdfEncryptionAlgorithm.Aes256, true);

        var v2Bytes = File.ReadAllBytes(v2);
        var openWithOld = () => PdfDocument.Open(v2Bytes, "old-pw");
        openWithOld.Should().Throw<Exception>("the old password must no longer open the re-encrypted file");

        using var withNew = PdfDocument.Open(v2Bytes, "new-pw");
        withNew.GetPage(1).Text.Should().Contain("SEKRIT");
    }
}
