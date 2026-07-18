using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text;
using Xunit;

namespace Excise.Core.Tests.Parsing;

public class RecoverablePdfRegressionTests
{
    private const string Bug1606566 = "../../../../test-pdfs/pdfjs/bug1606566.pdf";
    private const string PagesTreeRefs = "../../../../test-pdfs/pdfjs/Pages-tree-refs.pdf";
    private const string Issue19484_1 = "../../../../test-pdfs/pdfjs/issue19484_1.pdf";
    private const string Issue19484_2 = "../../../../test-pdfs/pdfjs/issue19484_2.pdf";

    [Fact]
    public void Open_InvalidHeaderWithRecoverableXref_UsesUnknownVersionAndLoadsPage()
    {
        Assert.SkipWhen(!File.Exists(Bug1606566), "pdf.js regression fixture not available");

        using var doc = PdfDocument.Open(Bug1606566);

        doc.Version.Should().Be("0.0");
        doc.PageCount.Should().Be(1);
        new TextExtractor(doc.GetPage(1)).ExtractText().Should().Contain("Bug 1606566");
    }

    [Fact]
    public void GetPage_CircularPagesSubtree_ExposesDeclaredPageAndThrowsTypedCycleError()
    {
        Assert.SkipWhen(!File.Exists(PagesTreeRefs), "pdf.js regression fixture not available");

        using var doc = PdfDocument.Open(PagesTreeRefs);

        doc.PageCount.Should().Be(2);
        new TextExtractor(doc.GetPage(1)).ExtractText().Should().Contain("Testcase");
        Action act = () => doc.GetPage(2);
        act.Should().Throw<Excise.Core.Parsing.PdfParseException>()
            .WithMessage("*circular reference*");
    }

    [Theory]
    [InlineData(Issue19484_1)]
    [InlineData(Issue19484_2)]
    public void Open_AcrobatCompatibleV4R4ShortKeyPadding_DecodesFirstPage(string path)
    {
        Assert.SkipWhen(!File.Exists(path), "pdf.js regression fixture not available");

        using var doc = PdfDocument.Open(path);

        doc.PageCount.Should().BeGreaterThan(0);
        Action act = () => _ = doc.GetPage(1).GetContentStreamBytes();
        act.Should().NotThrow("V=4/R=4 short encryption keys are padded before object-key derivation");
    }
}
