using AwesomeAssertions;
using Excise.Core.Document;
using Xunit;

namespace Excise.Core.Tests.Document;

/// <summary>
/// Tests using real PDF files from the veraPDF corpus.
/// </summary>
public class RealPdfTests
{
    // Path resolves from the test DLL's bin/Debug/net10.0/ up four levels
    // to the repo root, then into test-pdfs. Matches the convention every
    // other test file in this repo uses (PdfAnnotationTests, CjkRenderingTests,
    // CorpusConformanceTests, etc.). Pre-fix: only three "../" — landed in
    // Excise.Core.Tests/test-pdfs/ which never exists, so the corpus-presence
    // gate always reported "not available" and these tests always skipped.
    private const string CorpusPath = "../../../../test-pdfs/verapdf-corpus/veraPDF-corpus-master/PDF_A-1b";

    private static bool CorpusAvailable => Directory.Exists(Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, CorpusPath)));

    [Fact]
    public void Open_VeraPdfCorpusFile_ParsesSuccessfully()
    {
        Assert.SkipUnless(CorpusAvailable, "veraPDF corpus not available");

        var corpusDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, CorpusPath));
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
        Assert.SkipUnless(CorpusAvailable, "veraPDF corpus not available");

        var corpusDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, CorpusPath));
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
