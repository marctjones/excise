using System.IO;
using System.Text;
using AwesomeAssertions;
using Pdfe.Core.Document;
using Pdfe.Core.Parsing;
using Pdfe.Core.Text.Segmentation;
using Xunit;

namespace Pdfe.Core.Tests.Text.Segmentation;

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
        using var doc = PdfDocument.Open(Path.Combine(FindRepoRoot(), relativePath), password);
        doc.IsEncrypted.Should().BeTrue();
        string.Concat(doc.GetPage(1).Letters.Select(l => l.Value)).Should().Contain(secret);

        doc.RedactText(secret, drawBlackRect: false).Should().Be(1);

        var saved = doc.SaveToBytes();
        Encoding.Latin1.GetString(saved).Should().NotContain(secret);

        using var reopened = PdfDocument.Open(saved);
        reopened.IsEncrypted.Should().BeFalse("redacted output is currently written as an unencrypted clean copy");
        string.Concat(reopened.GetPage(1).Letters.Select(l => l.Value)).Should().NotContain(secret);
    }

    [Theory]
    [MemberData(nameof(DocumentedPasswordOnlyFixtures))]
    public void OpenEncryptedPdf_WithoutDocumentedPassword_FailsClosed(
        string relativePath,
        string password)
    {
        var path = Path.Combine(FindRepoRoot(), relativePath);

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
            if (File.Exists(Path.Combine(dir.FullName, "pdfe.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root from test base directory.");
    }
}
