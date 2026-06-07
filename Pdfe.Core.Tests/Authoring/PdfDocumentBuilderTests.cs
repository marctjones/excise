using System.Linq;
using System.Text;
using AwesomeAssertions;
using Pdfe.Core.Authoring;
using Pdfe.Core.Document;
using Pdfe.Core.Graphics;
using Pdfe.Core.Text;
using Xunit;

namespace Pdfe.Core.Tests.Authoring;

/// <summary>
/// Tests for the high-level <see cref="PdfDocumentBuilder"/> writer facade
/// (issue #383). Verifies the friendly API produces real, parseable PDFs:
/// text is extractable, content flows across pages, and form fields are live.
/// </summary>
public class PdfDocumentBuilderTests
{
    private static string ExtractAllText(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        var sb = new StringBuilder();
        foreach (var page in doc.GetPages())
            sb.AppendLine(new TextExtractor(page).ExtractText());
        return sb.ToString();
    }

    [Fact]
    public void Create_WithNoContent_ProducesNoPagesUntilContentAdded()
    {
        var builder = PdfDocumentBuilder.Create();
        builder.PageCount.Should().Be(0, "pages are created lazily on first content");

        builder.Paragraph("hello");
        builder.PageCount.Should().Be(1);
    }

    [Fact]
    public void HeadingAndParagraph_TextIsExtractable()
    {
        var pdf = PdfDocumentBuilder.Create()
            .Heading("Quarterly Report")
            .Paragraph("This document summarizes the results for the quarter.")
            .SaveToBytes();

        var text = ExtractAllText(pdf);
        text.Should().Contain("Quarterly Report");
        text.Should().Contain("summarizes the results");
    }

    [Fact]
    public void SaveToBytes_RoundTripsThroughOpen()
    {
        var pdf = PdfDocumentBuilder.Create()
            .Heading("Title")
            .Paragraph("Body text.")
            .SaveToBytes();

        var act = () => PdfDocument.Open(pdf);
        act.Should().NotThrow();

        using var doc = PdfDocument.Open(pdf);
        doc.PageCount.Should().Be(1);
    }

    [Fact]
    public void LongContent_FlowsOntoMultiplePages()
    {
        var builder = PdfDocumentBuilder.Create();
        // ~120 lines of text far exceeds one Letter page (~49 body lines).
        for (int i = 0; i < 120; i++)
            builder.Paragraph($"Line number {i} of the overflow test paragraph.");

        var pdf = builder.SaveToBytes();
        using var doc = PdfDocument.Open(pdf);
        doc.PageCount.Should().BeGreaterThan(1, "content longer than a page must paginate");
    }

    [Fact]
    public void Paragraph_WrapsLongTextWithinContentColumn()
    {
        // A single long paragraph with no hard breaks must wrap to many lines.
        var longText = string.Join(" ", Enumerable.Repeat("word", 400));
        var pdf = PdfDocumentBuilder.Create()
            .Paragraph(longText)
            .SaveToBytes();

        var text = ExtractAllText(pdf);
        text.Should().Contain("word");
        // It wrapped/flowed without throwing and stayed parseable.
        using var doc = PdfDocument.Open(pdf);
        doc.PageCount.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void PageBreak_ForcesNewPage()
    {
        var pdf = PdfDocumentBuilder.Create()
            .Paragraph("Page one.")
            .PageBreak()
            .Paragraph("Page two.")
            .SaveToBytes();

        using var doc = PdfDocument.Open(pdf);
        doc.PageCount.Should().Be(2);

        new TextExtractor(doc.GetPage(1)).ExtractText().Should().Contain("Page one");
        new TextExtractor(doc.GetPage(2)).ExtractText().Should().Contain("Page two");
    }

    [Fact]
    public void TextField_CreatesLiveAcroFormField()
    {
        var pdf = PdfDocumentBuilder.Create()
            .TextField("Full name", fieldName: "fullName", required: true)
            .SaveToBytes();

        using var doc = PdfDocument.Open(pdf);
        var form = doc.GetAcroForm();
        form.Should().NotBeNull();
        form!.Fields.Should().ContainSingle(f => f.FullName == "fullName");
        form.Fields.Single(f => f.FullName == "fullName").FieldType.Should().Be(PdfFieldType.Text);

        // The label (with required marker) is drawn as page content.
        ExtractAllText(pdf).Should().Contain("Full name");
    }

    [Fact]
    public void CheckBoxAndDropdown_CreateLiveFields()
    {
        var pdf = PdfDocumentBuilder.Create()
            .CheckBox("I agree", fieldName: "agree")
            .Dropdown("Country", new[] { "USA", "Canada", "Mexico" }, fieldName: "country", defaultValue: "Canada")
            .SaveToBytes();

        using var doc = PdfDocument.Open(pdf);
        var form = doc.GetAcroForm();
        form.Should().NotBeNull();
        form!.Fields.Select(f => f.FullName).Should().Contain(new[] { "agree", "country" });
    }

    [Fact]
    public void AutoFieldNames_AreUniqueWhenNotSupplied()
    {
        var pdf = PdfDocumentBuilder.Create()
            .TextField("A")
            .TextField("B")
            .SaveToBytes();

        using var doc = PdfDocument.Open(pdf);
        var names = doc.GetAcroForm()!.Fields.Select(f => f.FullName).ToList();
        names.Should().OnlyHaveUniqueItems();
        names.Should().HaveCount(2);
    }

    [Fact]
    public void KeyValue_RendersLabelAndValue()
    {
        var pdf = PdfDocumentBuilder.Create()
            .KeyValue("Name", "Ada Lovelace")
            .KeyValue("Role", "Mathematician")
            .SaveToBytes();

        var text = ExtractAllText(pdf);
        text.Should().Contain("Name");
        text.Should().Contain("Ada Lovelace");
        text.Should().Contain("Mathematician");
    }

    [Fact]
    public void Table_RendersAllCells()
    {
        var rows = new[]
        {
            new[] { "Item", "Qty", "Price" },
            new[] { "Widget", "3", "$9.00" },
            new[] { "Gadget", "1", "$4.50" },
        };

        var pdf = PdfDocumentBuilder.Create()
            .Table(rows, columnWeights: new[] { 2.0, 1.0, 1.0 }, headerRow: true)
            .SaveToBytes();

        var text = ExtractAllText(pdf);
        foreach (var cell in new[] { "Item", "Qty", "Price", "Widget", "Gadget", "$9.00", "$4.50" })
            text.Should().Contain(cell);
    }

    [Fact]
    public void Custom_GivesDirectGraphicsAccess()
    {
        var pdf = PdfDocumentBuilder.Create()
            .Custom((g, ctx) =>
                g.DrawString("custom drawn", PdfFont.Helvetica(12), PdfBrush.Black, ctx.Left, ctx.Top - 20))
            .SaveToBytes();

        ExtractAllText(pdf).Should().Contain("custom drawn");
    }

    [Fact]
    public void A4Landscape_UsesRequestedPageSize()
    {
        var pdf = PdfDocumentBuilder.Create(PageSize.A4.Landscape())
            .Paragraph("landscape")
            .SaveToBytes();

        using var doc = PdfDocument.Open(pdf);
        var page = doc.GetPage(1);
        page.Width.Should().BeGreaterThan(page.Height, "A4 landscape is wider than tall");
        page.Width.Should().BeApproximately(841.89, 0.5);
    }

    [Theory]
    [InlineData("", 1)]                                  // empty → one empty line
    [InlineData("short", 1)]
    public void WrapText_EmptyAndShortStaySingleLine(string input, int expectedLines)
    {
        var font = PdfFont.Helvetica(11);
        var lines = PdfDocumentBuilder.WrapText(input, font, 400).ToList();
        lines.Should().HaveCount(expectedLines);
    }

    [Fact]
    public void WrapText_HardBreaksArePreserved()
    {
        var font = PdfFont.Helvetica(11);
        var lines = PdfDocumentBuilder.WrapText("one\ntwo\nthree", font, 400).ToList();
        lines.Should().Equal("one", "two", "three");
    }

    [Fact]
    public void WrapText_WrapsWhenExceedingWidth()
    {
        var font = PdfFont.Helvetica(11);
        var text = string.Join(" ", Enumerable.Repeat("word", 100));
        var lines = PdfDocumentBuilder.WrapText(text, font, 100).ToList();
        lines.Count.Should().BeGreaterThan(1);
        lines.All(l => font.MeasureWidth(l) <= 100 || !l.Contains(' '))
             .Should().BeTrue("each wrapped line fits the column (or is a single oversized word)");
    }
}
