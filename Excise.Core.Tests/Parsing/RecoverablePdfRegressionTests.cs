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

    // #1399: the two fixtures print their own decryption key in body text, so
    // that string is an oracle for whether the page actually decoded rather
    // than merely opening without throwing. A short-key regression here (the
    // wrong padded key, or none) does not throw either -- it silently
    // decrypts to garbage or an empty page -- so "opens, doesn't throw" alone
    // passed on a completely blank page (measured by corrupting the content
    // stream's key-dependent objects: opens fine, renders 1275x1650, 0.0000
    // ink, both regression AND rendering tests stayed green).
    private const string Issue19484_1_Key = "0x0446615747";
    private const string Issue19484_2_Key = "0x02988E82AFF8";

    [Theory]
    [InlineData(Issue19484_1, Issue19484_1_Key)]
    [InlineData(Issue19484_2, Issue19484_2_Key)]
    public void Open_AcrobatCompatibleV4R4ShortKeyPadding_DecodesFirstPage(string path, string expectedKeyText)
    {
        Assert.SkipWhen(!File.Exists(path), "pdf.js regression fixture not available");

        using var doc = PdfDocument.Open(path);

        doc.PageCount.Should().BeGreaterThan(0);
        Action act = () => _ = doc.GetPage(1).GetContentStreamBytes();
        act.Should().NotThrow("V=4/R=4 short encryption keys are padded before object-key derivation");

        var text = new TextExtractor(doc.GetPage(1)).ExtractText();
        text.Should().Contain(expectedKeyText,
            "the fixture prints its own decryption key in body text -- if the short-key padding " +
            "silently derived the wrong key, the page decodes to garbage or emptiness rather than throwing");
    }
}
