using System.IO;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Parsing;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

public sealed class EncryptedRedactionRegressionTests
{
    public static TheoryData<string, string, string> DocumentedPasswordFixtures => new()
    {
        { "test-pdfs/pdfjs/issue15893_reduced.pdf", "test", "Issue 15893" },
        { "test-pdfs/poppler/unittestcases/PasswordEncryptedReconstructed.pdf", "test", "Issue 15893" },
    };

    public static TheoryData<string, string> DocumentedPasswordOnlyFixtures => new()
    {
        { "test-pdfs/pdfjs/issue15893_reduced.pdf", "test" },
        { "test-pdfs/poppler/unittestcases/PasswordEncryptedReconstructed.pdf", "test" },
    };

    [Theory]
    [MemberData(nameof(DocumentedPasswordFixtures))]
    public void RedactText_WithDocumentedPassword_RemovesRecoverableText(
        string relativePath,
        string password,
        string secret)
    {
        var path = ExistingFixturePath(relativePath);

        using var doc = PdfDocument.Open(path, password);
        doc.IsEncrypted.Should().BeTrue();
        string.Concat(doc.GetPage(1).Letters.Select(l => l.Value)).Should().Contain(secret);

        doc.RedactText(secret, drawBlackRect: false).Should().Be(1);

        // #643: the redacted copy of an encrypted source saves ENCRYPTED —
        // same permissions, same password (these RC4 fixtures upgrade to
        // AES-256, the documented upgrade-only policy).
        var saved = doc.SaveToBytes(doc.GetReEncryptionOptions(password));

        var withoutPassword = () => PdfDocument.Open(saved);
        withoutPassword.Should().Throw<PdfEncryptionNotSupportedException>(
            "the redacted output must still require the source's password (#643)");

        using var reopened = PdfDocument.Open(saved, password);
        reopened.IsEncrypted.Should().BeTrue(
            "redacting a password-protected PDF must yield a password-protected PDF (#643)");
        reopened.Permissions.RawValue.Should().Be(doc.Permissions.RawValue,
            "the source /P permission mask must survive the redaction round-trip");
        string.Concat(reopened.GetPage(1).Letters.Select(l => l.Value)).Should().NotContain(secret);

        // The saved bytes are ciphertext, so a raw byte-scan of them proves
        // nothing about the secret. Scan the DECRYPTED serialization instead
        // (carrier-agnostic; the independent qpdf/mutool equivalent lives in
        // Excise.Rendering.Tests/Differential/EncryptionPreservationInteropTests.cs).
        var decrypted = reopened.SaveToBytes();
        (Encoding.Latin1.GetString(decrypted) + Encoding.BigEndianUnicode.GetString(decrypted))
            .Should().NotContain(secret,
                "the secret must not survive in ANY carrier of the decrypted output");
    }

    [Theory]
    [MemberData(nameof(DocumentedPasswordOnlyFixtures))]
    public void OpenEncryptedPdf_WithoutDocumentedPassword_FailsClosed(
        string relativePath,
        string password)
    {
        var path = ExistingFixturePath(relativePath);

        var missingPassword = () => PdfDocument.Open(path);
        missingPassword.Should().Throw<PdfEncryptionNotSupportedException>();

        var wrongPassword = () => PdfDocument.Open(path, password + "-wrong");
        wrongPassword.Should().Throw<PdfEncryptionNotSupportedException>();
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
