using System;
using System.IO;
using AwesomeAssertions;
using Excise.Rendering.Differential;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

public class QpdfReferenceToolTests
{
    private static string EncryptedFixture =>
        Path.Combine(LocateRepoRoot()!, "test-pdfs", "poppler", "unittestcases", "encrypted-256.pdf");

    // encrypted-256.pdf (above) is owner-password-only — its /U string does
    // NOT validate against an empty password (confirmed against both excise's
    // own decrypt handler and qpdf: both reject it, tracked as excise#324 for
    // the "owner password only" case). "Gday garçon - owner.pdf" genuinely
    // opens with an empty user password (confirmed via `qpdf --check`),
    // so it's the correct fixture for a decrypt-succeeds test.
    private static string EmptyPasswordEncryptedFixture =>
        Path.Combine(LocateRepoRoot()!, "test-pdfs", "poppler", "unittestcases", "Gday garçon - owner.pdf");

    [Fact]
    public void IsEncrypted_OnAnEncryptedFixture_ReturnsTrue()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not on PATH");
        Assert.SkipUnless(File.Exists(EncryptedFixture),
            "test-pdfs/poppler corpus (gitignored) not downloaded — run scripts/download-test-pdfs.sh");

        QpdfReferenceTool.IsEncrypted(EncryptedFixture).Should().BeTrue(
            "qpdf's own independent parser must agree this file is encrypted");
    }

    [Fact]
    public void ShowEncryption_OnAnEncryptedFixture_ReportsAesV3AndRSix()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not on PATH");
        Assert.SkipUnless(File.Exists(EncryptedFixture),
            "test-pdfs/poppler corpus (gitignored) not downloaded — run scripts/download-test-pdfs.sh");

        var output = QpdfReferenceTool.ShowEncryption(EncryptedFixture);

        output.Should().NotBeNull();
        output.Should().Contain("R = 6", "the fixture is AES-256 (R6) encrypted");
        output.Should().Contain("AESv3", "qpdf's independent parser must identify the AES-256 stream cipher");
    }

    [Fact]
    public void Decrypt_WithEmptyUserPassword_ProducesAReadablePlaintextFile()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not on PATH");
        Assert.SkipUnless(File.Exists(EmptyPasswordEncryptedFixture),
            "test-pdfs/poppler corpus (gitignored) not downloaded — run scripts/download-test-pdfs.sh");

        var outputPath = Path.Combine(Path.GetTempPath(), $"excise-qpdf-decrypt-test-{Guid.NewGuid():N}.pdf");
        try
        {
            // This is qpdf independently deriving the file key from an
            // empty password and stripping /Encrypt — not excise reading its
            // own output.
            var succeeded = QpdfReferenceTool.Decrypt(EmptyPasswordEncryptedFixture, outputPath);

            succeeded.Should().BeTrue();
            File.Exists(outputPath).Should().BeTrue();
            QpdfReferenceTool.IsEncrypted(outputPath).Should().BeFalse(
                "the decrypted output must no longer carry an /Encrypt dictionary");
        }
        finally
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }

    [Fact]
    public void IsAvailable_IsStableAcrossRepeatedReads()
    {
        // Not gated on Assert.SkipUnless — this test's whole point is to
        // exercise the Lazy<bool> caching regardless of whether qpdf
        // happens to be installed in a given environment.
        var first = QpdfReferenceTool.IsAvailable;
        QpdfReferenceTool.IsAvailable.Should().Be(first);
    }

    private static string? LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "excise.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
