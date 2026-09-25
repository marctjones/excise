using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1839: a document <see cref="PdfDocument.CreateNew"/> hands out, saved,
/// read by qpdf's own parser rather than excise's.
/// </summary>
public class CreateNewQpdfOracleTests
{
    [Theory]
    [InlineData("1.4", 2)]
    [InlineData("1.7", 2)]
    [InlineData("2.0", 1)]
    public void SavedNewDocument_PassesQpdfCheckAndHasTheRequestedPageCount(string version, int pages)
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");

        WithSavedNewDocument(version, pages, path =>
        {
            var check = QpdfReferenceTool.Check(path);
            check.Should().NotBeNull("qpdf is available, so it must have produced an answer");
            check!.Value.Success.Should().BeTrue(check.Value.Output);
            QpdfReferenceTool.PageCount(path).Should().Be(pages);
        });
    }

    // qpdf 12 answers --check with "ERROR: vector" for ANY zero-page file,
    // including a hand-written one, so the empty document is held to what qpdf
    // can still say about it: it reads the file and counts no pages.
    [Theory]
    [InlineData("1.4")]
    [InlineData("1.7")]
    public void SavedEmptyNewDocument_IsReadByQpdfAsZeroPages(string version)
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");

        WithSavedNewDocument(version, pages: 0, path =>
            QpdfReferenceTool.PageCount(path).Should().Be(0));
    }

    private static void WithSavedNewDocument(string version, int pages, Action<string> assertOn)
    {
        using var doc = PdfDocument.CreateNew(version);
        for (var i = 0; i < pages; i++)
            doc.Pages.AddBlank();

        var path = Path.Combine(Path.GetTempPath(), $"createnew-{Guid.NewGuid():N}.pdf");
        try
        {
            doc.Save(path);
            assertOn(path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
