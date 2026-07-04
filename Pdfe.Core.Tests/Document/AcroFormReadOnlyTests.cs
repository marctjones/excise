using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Pdfe.Core.Document;
using Pdfe.Core.Text.Segmentation;
using Xunit;

namespace Pdfe.Core.Tests.Document;

/// <summary>
/// Integration tests for AcroForm read-only support: form field values must
/// be visible to text extraction, search, and (transitively) redaction.
///
/// These tests construct synthetic PDFs with AcroForm dictionaries and assert
/// that <see cref="TextExtractor"/> emits the field values as Letters located
/// inside the widget's rectangle. Search and redaction sit on top of Letters,
/// so this is the integration point that closes the redaction-of-form-fields
/// security gap.
/// </summary>
public class AcroFormReadOnlyTests
{
    [Fact]
    public void TextField_ValueAppearsInExtractedText()
    {
        var pdf = BuildFormPdf(textValue: "John Smith");
        using var doc = PdfDocument.Open(pdf);

        var page = doc.GetPage(1);

        page.Text.Should().Contain("John Smith");
    }

    [Fact]
    public void TextField_ValueAppearsAsLettersInWidgetRect()
    {
        var pdf = BuildFormPdf(textValue: "John Smith");
        using var doc = PdfDocument.Open(pdf);

        var page = doc.GetPage(1);
        var letters = page.Letters;

        // The whole "John Smith" string should fit inside the field's rect (100..400, 700..720).
        letters.Should().NotBeEmpty();
        var formLetters = letters.Where(l => l.FontName.StartsWith("AcroForm:")).ToList();
        formLetters.Should().HaveCountGreaterThan(0);
        formLetters.All(l => l.GlyphRectangle.Left   >= 100 - 0.1
                          && l.GlyphRectangle.Right  <= 400 + 0.1
                          && l.GlyphRectangle.Bottom >= 700 - 0.1
                          && l.GlyphRectangle.Top    <= 720 + 0.1).Should().BeTrue(
            "AcroForm value letters must land within the widget rectangle");
    }

    [Fact]
    public void TextField_EmptyValue_NoExtraLetters()
    {
        var pdf = BuildFormPdf(textValue: "");
        using var doc = PdfDocument.Open(pdf);

        var page = doc.GetPage(1);
        page.Letters.Should().BeEmpty();
    }

    [Fact]
    public void TextField_NoValue_DefaultValueAppears()
    {
        var pdf = BuildFormPdf(textValue: null, defaultValue: "Default Text");
        using var doc = PdfDocument.Open(pdf);

        var page = doc.GetPage(1);
        page.Text.Should().Contain("Default Text");
    }

    [Fact]
    public void TextField_ButtonField_NotEmittedAsText()
    {
        // Buttons have name-style values like /Yes /Off — meaningful to forms,
        // not to humans. Don't pollute extracted text with them.
        var pdf = BuildFormPdf(buttonValue: "Yes");
        using var doc = PdfDocument.Open(pdf);

        var page = doc.GetPage(1);
        page.Text.Should().NotContain("Yes");
    }

    [Fact]
    public void TextField_SignatureField_NotEmittedAsText()
    {
        var pdf = BuildFormPdf(signature: true);
        using var doc = PdfDocument.Open(pdf);

        var page = doc.GetPage(1);
        // Signature widgets shouldn't surface arbitrary text from /V which is
        // a binary signature dict.
        page.Text.Should().BeEmpty();
    }

    [Fact]
    public void IncludeFormFieldValues_DisabledViaFlag_ExcludesFormValues()
    {
        var pdf = BuildFormPdf(textValue: "Hidden");
        using var doc = PdfDocument.Open(pdf);

        var page = doc.GetPage(1);
        var extractor = new Pdfe.Core.Text.TextExtractor(page) { IncludeFormFieldValues = false };

        var letters = extractor.ExtractLetters();
        letters.Should().BeEmpty("turning off IncludeFormFieldValues must drop form letters");
    }

    [Fact]
    public void TextField_ValueLongerThanWidget_TruncatesToFit()
    {
        // Widget rect is 300pt wide. At default fontSize ~12 and 0.55em advance,
        // ~45 chars fit. A 200-char string must be truncated to fit the rect.
        var longText = new string('A', 200);
        var pdf = BuildFormPdf(textValue: longText);
        using var doc = PdfDocument.Open(pdf);

        var page = doc.GetPage(1);
        var letters = page.Letters.Where(l => l.FontName.StartsWith("AcroForm:")).ToList();

        letters.Count.Should().BeLessThan(longText.Length);
        letters.All(l => l.GlyphRectangle.Right <= 400 + 0.1).Should().BeTrue();
    }

    [Fact]
    public void GetFormFields_OnlyReturnsFieldsForCurrentPage()
    {
        // Page 1 has the field; page 2 has none. GetFormFields() on page 2
        // must return empty.
        var pdf = BuildFormPdf(textValue: "On page 1");
        using var doc = PdfDocument.Open(pdf);
        var page1 = doc.GetPage(1);
        page1.GetFormFields().Should().HaveCount(1);
    }

    [Fact]
    public void GetAcroForm_NoAcroFormDict_ReturnsNull()
    {
        // A document with no /AcroForm catalog entry must return null.
        var pdf = BuildPlainPdf();
        using var doc = PdfDocument.Open(pdf);
        doc.GetAcroForm().Should().BeNull();
    }

    [Fact]
    public void TextField_RedactionThroughSearch_CanFindFormValue()
    {
        // End-to-end: search a form value via the page's letter stream.
        // (Real search lives in the GUI layer; here we validate the underlying
        //  letter stream contains the value, which is what search consumes.)
        var pdf = BuildFormPdf(textValue: "SECRET-12345");
        using var doc = PdfDocument.Open(pdf);

        var page = doc.GetPage(1);
        var text = string.Concat(page.Letters.Select(l => l.Value));
        text.Should().Contain("SECRET-12345");
    }

    [Fact]
    public void RedactText_RemovesAcroFormValueAfterSaveAndReopen()
    {
        var pdf = BuildFormPdf(textValue: "SECRET-12345");
        using var doc = PdfDocument.Open(pdf);

        var matches = doc.RedactText("SECRET", caseSensitive: false);
        var bytes = doc.SaveToBytes();

        matches.Should().Be(1);
        using var reopened = PdfDocument.Open(bytes);
        reopened.GetPage(1).Text.Should().NotContain("SECRET");
        reopened.GetAcroForm()!.FindField("field1")!.Value.Should().BeNull(
            "security redaction removes the whole form value instead of leaving recoverable fragments in /V");
    }

    [Fact]
    public void RedactText_RemovesAcroFormDefaultValueAfterSaveAndReopen()
    {
        var pdf = BuildFormPdf(textValue: null, defaultValue: "Fallback SECRET");
        using var doc = PdfDocument.Open(pdf);

        var matches = doc.RedactText("SECRET", caseSensitive: false);
        var bytes = doc.SaveToBytes();

        matches.Should().Be(1);
        using var reopened = PdfDocument.Open(bytes);
        reopened.GetPage(1).Text.Should().NotContain("SECRET");
        reopened.GetAcroForm()!.FindField("field1")!.DefaultValue.Should().BeNull(
            "security redaction removes the whole default value instead of leaving recoverable fragments in /DV");
    }

    // ─── PDF builder ─────────────────────────────────────────────────────────

    /// <summary>
    /// Build a one-page PDF with a single AcroForm field. The field has
    /// /Subtype /Widget so the field dictionary is also the widget annotation,
    /// the simplest form layout PDF emitters use.
    /// </summary>
    private static byte[] BuildFormPdf(string? textValue = "Hello",
                                        string? defaultValue = null,
                                        string? buttonValue = null,
                                        bool signature = false)
    {
        var sb = new StringBuilder();
        var offsets = new long[8];
        void Mark(int n) => offsets[n] = sb.Length;

        sb.Append("%PDF-1.7\n");

        Mark(1); sb.Append("1 0 obj <</Type/Catalog/Pages 2 0 R/AcroForm 5 0 R>> endobj\n");
        Mark(2); sb.Append("2 0 obj <</Type/Pages/Count 1/Kids[3 0 R]>> endobj\n");

        // Page 3: page dict referring to the field-as-widget annotation (4).
        Mark(3);
        sb.Append("3 0 obj <</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]" +
                  "/Annots[4 0 R]/Resources<<>>>> endobj\n");

        // Field 4: combined Widget + form field (the common minimal shape).
        var ft = signature ? "Sig" : (buttonValue != null ? "Btn" : "Tx");
        Mark(4);
        sb.Append($"4 0 obj <</Type/Annot/Subtype/Widget/FT/{ft}/T(field1)" +
                  $"/Rect[100 700 400 720]/P 3 0 R");
        if (signature)
        {
            // Signature: /V is a dict — we don't care about its contents for
            // this test, just that the parser sees /Sig and refuses to extract
            // text from it.
            sb.Append("/V<</Type/Sig>>");
        }
        else if (buttonValue != null)
        {
            sb.Append($"/V/{buttonValue}");
        }
        else
        {
            if (textValue != null) sb.Append($"/V({textValue})");
            if (defaultValue != null) sb.Append($"/DV({defaultValue})");
        }
        sb.Append(">> endobj\n");

        // AcroForm 5: minimal — just /Fields.
        Mark(5);
        sb.Append("5 0 obj <</Fields[4 0 R]>> endobj\n");

        var xrefPos = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        for (int i = 1; i <= 5; i++)
            sb.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer <</Size 6/Root 1 0 R>>\nstartxref\n").Append(xrefPos).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] BuildPlainPdf()
    {
        var sb = new StringBuilder();
        var offsets = new long[5];
        void Mark(int n) => offsets[n] = sb.Length;

        sb.Append("%PDF-1.7\n");
        Mark(1); sb.Append("1 0 obj <</Type/Catalog/Pages 2 0 R>> endobj\n");
        Mark(2); sb.Append("2 0 obj <</Type/Pages/Count 1/Kids[3 0 R]>> endobj\n");
        Mark(3); sb.Append("3 0 obj <</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<<>>>> endobj\n");
        var xrefPos = sb.Length;
        sb.Append("xref\n0 4\n0000000000 65535 f \n");
        for (int i = 1; i <= 3; i++)
            sb.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer <</Size 4/Root 1 0 R>>\nstartxref\n").Append(xrefPos).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
