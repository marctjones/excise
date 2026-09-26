using AwesomeAssertions;
using Excise.Core.Document;
using Excise.TestSupport;
using Xunit;

namespace Excise.Core.Tests.Document;

/// <summary>
/// Tests using real PDF files from the veraPDF corpus.
/// </summary>
public class RealPdfTests
{
    private const string CorpusPath = "test-pdfs/verapdf-corpus/veraPDF-corpus-master/PDF_A-1b";

    private static string CorpusDirectory()
    {
        var dir = TestRepoLayout.FindDirectory(CorpusPath);
        Assert.SkipWhen(dir == null, TestRepoLayout.AbsenceReason("veraPDF corpus", CorpusPath));
        return dir!;
    }

    [Fact]
    public void Open_VeraPdfCorpusFile_ParsesSuccessfully()
    {
        var corpusDir = CorpusDirectory();
        var pdfFiles = Directory.GetFiles(corpusDir, "*.pdf", SearchOption.AllDirectories)
            .Take(10); // Test first 10 files

        foreach (var pdfFile in pdfFiles)
        {
            try
            {
                using var doc = PdfDocument.Open(pdfFile);

                doc.Should().NotBeNull();
                doc.PageCount.Should().BeGreaterThan(0);
                doc.Version.Should().NotBeNullOrEmpty();

                var page = doc.GetPage(1);
                page.Should().NotBeNull();
                page.Width.Should().BeGreaterThan(0);
                page.Height.Should().BeGreaterThan(0);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to parse {Path.GetFileName(pdfFile)}: {ex.Message}", ex);
            }
        }
    }

    [Fact]
    public void GetContentStreamBytes_VeraPdfCorpusFile_ReturnsContent()
    {
        var corpusDir = CorpusDirectory();
        var pdfFiles = Directory.GetFiles(corpusDir, "*pass*.pdf", SearchOption.AllDirectories)
            .Take(5); // Test first 5 pass files

        foreach (var pdfFile in pdfFiles)
        {
            try
            {
                using var doc = PdfDocument.Open(pdfFile);
                var page = doc.GetPage(1);

                // Content stream should be accessible (may be empty for some test files)
                var content = page.GetContentStreamBytes();
                content.Should().NotBeNull();
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to get content from {Path.GetFileName(pdfFile)}: {ex.Message}", ex);
            }
        }
    }

    [Fact]
    public void Open_NonExistentFile_ThrowsIOException()
    {
        var act = () => PdfDocument.Open("/nonexistent/path/to/file.pdf");

        // Could be FileNotFoundException or DirectoryNotFoundException depending on OS
        act.Should().Throw<IOException>();
    }
}
