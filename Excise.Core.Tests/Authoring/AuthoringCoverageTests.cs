using System.Linq;
using AwesomeAssertions;
using Excise.Core.Authoring;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Excise.Core.Text;
using Xunit;

namespace Excise.Core.Tests.Authoring;

/// <summary>
/// Coverage-focused tests for the authoring value types and builder paths
/// (#351 — drive Excise.Core line coverage to the 94% gate). They also harden the
/// blocks/styles added for the Writer-MVP work.
/// </summary>
public class AuthoringCoverageTests
{
    private static string Extract(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        return string.Join("\n", doc.GetPages().Select(p => new TextExtractor(p).ExtractText()));
    }

    // ── TextStyle ─────────────────────────────────────────────────────────────

    [Fact]
    public void TextStyle_WithHelpers_AreImmutableCopies()
    {
        var s = TextStyle.Body;
        var s2 = s.WithSize(20).AsBold().AsItalic().WithColor(PdfColor.Red)
                  .WithAlignment(TextAlignment.Center).WithSpaceAfter(99);
        s.Size.Should().Be(11);            // original untouched
        s2.Size.Should().Be(20);
        s2.Bold.Should().BeTrue();
        s2.Italic.Should().BeTrue();
        s2.Color.Should().Be(PdfColor.Red);
        s2.Alignment.Should().Be(TextAlignment.Center);
        s2.SpaceAfter.Should().Be(99);
        s2.LineHeight.Should().BeApproximately(20 * 1.2, 0.001);
        s2.ResolveBrush().Color.Should().Be(PdfColor.Red);
    }

    [Theory]
    [InlineData(FontFamily.SansSerif, false, false, "Helvetica")]
    [InlineData(FontFamily.SansSerif, true, false, "Helvetica-Bold")]
    [InlineData(FontFamily.SansSerif, false, true, "Helvetica-Oblique")]
    [InlineData(FontFamily.SansSerif, true, true, "Helvetica-BoldOblique")]
    [InlineData(FontFamily.Serif, false, false, "Times-Roman")]
    [InlineData(FontFamily.Serif, true, false, "Times-Bold")]
    [InlineData(FontFamily.Serif, false, true, "Times-Italic")]
    [InlineData(FontFamily.Serif, true, true, "Times-BoldItalic")]
    [InlineData(FontFamily.Monospace, false, false, "Courier")]
    [InlineData(FontFamily.Monospace, true, false, "Courier-Bold")]
    [InlineData(FontFamily.Monospace, false, true, "Courier-Oblique")]
    [InlineData(FontFamily.Monospace, true, true, "Courier-BoldOblique")]
    public void TextStyle_ResolveFont_MapsFamilyWeightSlant(FontFamily fam, bool bold, bool italic, string expected)
    {
        var style = new TextStyle { Family = fam, Bold = bold, Italic = italic, Size = 10 };
        style.ResolveFont().BaseFont.Should().Be(expected);
    }

    // ── PageSize / PageMargins ────────────────────────────────────────────────

    [Fact]
    public void PageSize_LandscapePortraitPresets()
    {
        PageSize.A4.Landscape().Width.Should().BeGreaterThan(PageSize.A4.Landscape().Height);
        PageSize.A4.Landscape().Portrait().Height.Should().BeGreaterThan(PageSize.A4.Landscape().Portrait().Width);
        PageSize.Legal.Height.Should().BeApproximately(1008, 0.1);
        PageSize.A3.Width.Should().BeGreaterThan(PageSize.A5.Width);
    }

    [Fact]
    public void PageMargins_Presets()
    {
        PageMargins.All(10).Should().Be(new PageMargins(10, 10, 10, 10));
        PageMargins.Symmetric(5, 8).Should().Be(new PageMargins(5, 8, 5, 8));
        PageMargins.Default.Left.Should().Be(72);
    }

    // ── PdfDocumentBuilder block paths ────────────────────────────────────────

    [Fact]
    public void Builder_AllContentBlocks_ProduceExtractablePdf()
    {
        var pdf = PdfDocumentBuilder.Create(PageSize.A4, PageMargins.All(50))
            .Title("T").Author("A").Subject("S").Keywords("k").Language("en-GB")
            .Heading("H1", 1).Heading("H2", 2).Heading("H3", 3).Heading("H4", 4)
            .Paragraph("Body paragraph.")
            .Spacer(10)
            .HorizontalRule()
            .HorizontalRule(thickness: 1.5, color: PdfColor.Blue)
            .KeyValue("Key", "Value", labelWidth: 0.4)
            .Table(new[] { new[] { "a", "b" }, new[] { "c", "d" } })            // no weights, no header
            .Table(new[] { new[] { "h1", "h2" }, new[] { "x", "y" } }, headerRow: true, gridLines: false)
            .Custom((g, ctx) => g.DrawString("custom", PdfFont.Helvetica(10), PdfBrush.Black, ctx.Left, ctx.Top - 12))
            .SaveToBytes();

        var text = Extract(pdf);
        foreach (var s in new[] { "H1", "H2", "H3", "H4", "Body paragraph", "Key", "Value", "custom" })
            text.Should().Contain(s);

        using var doc = PdfDocument.Open(pdf);
        doc.Title.Should().Be("T");
        doc.Language.Should().Be("en-GB");
    }

    [Fact]
    public void Builder_FormFields_WithDateAndTooltips_AreLive()
    {
        var pdf = PdfDocumentBuilder.Create()
            .TextField("Name", "name", tooltip: "Full legal name", maxLength: 40)
            .TextField("SSN", "ssn", maxLength: 9, comb: true)
            .DateField("Date of birth", "dob", format: "yyyy-mm-dd", required: true)
            .CheckBox("I agree", "agree", checkedByDefault: true, tooltip: "Consent")
            .Dropdown("Tier", new[] { "A", "B" }, "tier", tooltip: "Choose")
            .SaveToBytes();

        using var doc = PdfDocument.Open(pdf);
        var names = doc.GetAcroForm()!.Fields.Select(f => f.FullName).ToList();
        names.Should().Contain(new[] { "name", "ssn", "dob", "agree", "tier" });
        doc.GetAcroForm()!.FindField("dob")!.RawDictionary.GetOptional("AA").Should().NotBeNull();
    }

    [Fact]
    public void Builder_Build_ReturnsUnderlyingDocument()
    {
        var b = PdfDocumentBuilder.Create().Paragraph("x");
        var doc = b.Build();
        doc.Should().BeOfType<PdfDocument>();
        doc.PageCount.Should().Be(1);
    }

    [Fact]
    public void Builder_EmptyTable_IsNoOp()
    {
        var pdf = PdfDocumentBuilder.Create().Table(System.Array.Empty<string[]>()).Paragraph("after").SaveToBytes();
        Extract(pdf).Should().Contain("after");
    }

    [Fact]
    public void Builder_ContentLeftAndWidth_ReflectMargins()
    {
        var b = PdfDocumentBuilder.Create(PageSize.Letter, PageMargins.All(40));
        b.ContentLeft.Should().Be(40);
        b.ContentWidth.Should().BeApproximately(612 - 80, 0.01);
    }

    // ── TextWrapper edges ─────────────────────────────────────────────────────

    [Fact]
    public void WrapText_NonPositiveWidth_KeepsHardLinesOnly()
    {
        var font = PdfFont.Helvetica(11);
        var lines = PdfDocumentBuilder.WrapText("a b c\nd e", font, 0).ToList();
        lines.Should().Equal("a b c", "d e");
    }

    [Fact]
    public void WrapText_BlankLinesPreserved()
    {
        var font = PdfFont.Helvetica(11);
        PdfDocumentBuilder.WrapText("a\n\nb", font, 400).ToList()
            .Should().Equal("a", "", "b");
    }
}
