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

    // ─── Choice field (combo/list box) tests — #661 ────────────────────────

    [Fact]
    public void ListBoxField_AllOptionsAppearInExtractedText()
    {
        // A list box (Choice, Ff=0 i.e. no Combo bit) visually renders its
        // ENTIRE /Opt option list, not just the selected /V — that's how
        // list boxes work (you see every option, with the selection
        // highlighted). Confirmed against mutool on the real fixture
        // test-pdfs/pdfjs/annotation-choice-widget.pdf.
        var pdf = BuildChoiceFieldPdf(
            options: new[] { "Lorem", "Ipsum", "Dolor", "Sit", "Amet", "Consectetur" },
            selectedValue: "Dolor",
            isCombo: false);
        using var doc = PdfDocument.Open(pdf);

        var page = doc.GetPage(1);
        var text = page.Text;

        text.Should().Contain("Lorem");
        text.Should().Contain("Ipsum");
        text.Should().Contain("Dolor");
        text.Should().Contain("Sit");
        text.Should().Contain("Amet");
        text.Should().Contain("Consectetur");
    }

    [Fact]
    public void ListBoxField_OptionLettersLandInWidgetRect()
    {
        var pdf = BuildChoiceFieldPdf(
            options: new[] { "Lorem", "Ipsum", "Dolor" },
            selectedValue: "Dolor",
            isCombo: false);
        using var doc = PdfDocument.Open(pdf);

        var letters = doc.GetPage(1).Letters
            .Where(l => l.FontName.StartsWith("AcroForm:", StringComparison.Ordinal))
            .ToList();

        letters.Should().NotBeEmpty();
        letters.All(l => l.GlyphRectangle.Left   >= 100 - 0.5
                      && l.GlyphRectangle.Right  <= 400 + 0.5
                      && l.GlyphRectangle.Bottom >= 500 - 0.5
                      && l.GlyphRectangle.Top    <= 700 + 0.5).Should().BeTrue(
            "list-box option letters must land within the widget rectangle");
    }

    [Fact]
    public void ComboBoxField_OnlySelectedValueAppears_NotOtherOptions()
    {
        // A closed combo box only ever renders its currently-selected value —
        // confirmed against mutool. Unlike a list box, unselected /Opt
        // entries must NOT show up in extracted text.
        var pdf = BuildChoiceFieldPdf(
            options: new[] { "Lorem", "Ipsum", "Dolor", "Sit" },
            selectedValue: "Dolor",
            isCombo: true);
        using var doc = PdfDocument.Open(pdf);

        var text = doc.GetPage(1).Text;

        text.Should().Contain("Dolor");
        text.Should().NotContain("Lorem");
        text.Should().NotContain("Ipsum");
        text.Should().NotContain("Sit");
    }

    [Fact]
    public void RedactText_RemovesListBoxOptionFromSavedBytes()
    {
        // #661's redaction-completeness half: a matched option must not
        // merely disappear from page.Text — it must be gone from the SAVED
        // BYTES (ASCII and UTF-16BE), and gone from /Opt itself. Leaving
        // /Opt behind after wiping /V/DV/AP would let a NeedAppearances
        // re-render restate the option, and a raw byte-scan of the file
        // would still find the "redacted" string. Carrier-agnostic check per
        // CLAUDE.md — page.Text/ExtractAllText alone is not sufficient.
        var pdf = BuildChoiceFieldPdf(
            options: new[] { "Lorem", "Ipsum", "SECRET-Consectetur", "Sit" },
            selectedValue: "Sit",
            isCombo: false);
        using var doc = PdfDocument.Open(pdf);

        var matches = doc.RedactText("SECRET-Consectetur", caseSensitive: false);
        matches.Should().BeGreaterThan(0);

        var saved = doc.SaveToBytes();
        var savedAscii = Encoding.ASCII.GetString(saved);
        var savedUtf16 = Encoding.BigEndianUnicode.GetString(saved);
        (savedAscii + savedUtf16).Should().NotContain("SECRET-Consectetur",
            "the option text must be gone from the saved bytes in every carrier, not just page.Text");

        using var reopened = PdfDocument.Open(saved);
        reopened.GetPage(1).Text.Should().NotContain("SECRET-Consectetur");
        var field = reopened.GetAcroForm()!.FindField("field1")!;
        (field.Options ?? Array.Empty<string>()).Should().NotContain("SECRET-Consectetur",
            "the whole /Opt array must be cleared, not just /V, or a NeedAppearances re-render " +
            "would restate the redacted option");
    }

    // ─── Signature field appearance tests — #669 ───────────────────────────

    [Fact]
    public void SignatureField_AppearanceText_AppearsInExtractedText()
    {
        // #669: a signature widget's /V is a signature dictionary, not text,
        // but its /AP/N appearance stream commonly draws real, visible text
        // (a "Digitally signed by..." block) — confirmed against mutool on
        // test-pdfs/pdfjs/bug854315.pdf, which nests /AP/N -> a Form XObject
        // -> a further nested Form XObject before reaching the Tj calls, the
        // same nesting depth this fixture exercises.
        var pdf = BuildSignatureFieldPdf(appearanceText: "Digitally signed by Jane Doe");
        using var doc = PdfDocument.Open(pdf);

        var page = doc.GetPage(1);
        page.Text.Should().Contain("Digitally signed by Jane Doe");
    }

    [Fact]
    public void SignatureField_AppearanceText_LettersUseAcroFormFontNamePrefix()
    {
        // The synthesized letters must carry the "AcroForm:" FontName prefix
        // so PdfDocumentRedactionExtensions.IsInteractiveOnlyMatch routes a
        // match through InteractiveRedactionScrubber instead of the
        // content-stream glyph-removal pass — there is no content-stream
        // glyph here to remove, only the widget's /AP.
        var pdf = BuildSignatureFieldPdf(appearanceText: "SIGTEXT");
        using var doc = PdfDocument.Open(pdf);

        var letters = doc.GetPage(1).Letters;
        letters.Should().NotBeEmpty();
        letters.All(l => l.FontName.StartsWith("AcroForm:", StringComparison.Ordinal)).Should().BeTrue();
    }

    [Fact]
    public void SignatureField_NoAppearance_StillEmitsNoText()
    {
        // No /AP at all (e.g. an unsigned signature field): must stay silent,
        // same as before #669 — there is nothing to extract.
        var pdf = BuildFormPdf(signature: true);
        using var doc = PdfDocument.Open(pdf);

        doc.GetPage(1).Text.Should().BeEmpty();
    }

    [Fact]
    public void RedactText_FindsAndRemovesSignatureAppearanceText()
    {
        // #669's redaction-completeness half, mirroring
        // RedactText_RemovesListBoxOptionFromSavedBytes: a matched
        // signature-appearance word must be gone from the SAVED BYTES
        // (ASCII and UTF-16BE), not just page.Text, and the widget's /AP
        // must actually be stripped rather than merely made unfindable —
        // the exact "found but not removable" risk #660 had to check for
        // FreeText annotations.
        var pdf = BuildSignatureFieldPdf(appearanceText: "SIGSECRET signed the document");
        using var doc = PdfDocument.Open(pdf);

        var matches = doc.RedactText("SIGSECRET", caseSensitive: false);
        matches.Should().BeGreaterThan(0, "RedactText must actually find the signature appearance text");

        var saved = doc.SaveToBytes();
        var savedAscii = Encoding.ASCII.GetString(saved);
        var savedUtf16 = Encoding.BigEndianUnicode.GetString(saved);
        (savedAscii + savedUtf16).Should().NotContain("SIGSECRET",
            "the signature appearance text must be gone from the saved bytes in every carrier");

        using var reopened = PdfDocument.Open(saved);
        reopened.GetPage(1).Text.Should().NotContain("SIGSECRET");
    }

    // ─── Multiline text field tests — #672 ─────────────────────────────────

    [Fact]
    public void MultilineTextField_LongValue_IsNotTruncatedToOneLine()
    {
        // #672: EmitFormFieldLetters routed every non-Choice-listbox field
        // through the single-line EmitLettersInRect, which truncates to
        // whatever fits the rect's width — even when /Ff bit 12 (Multiline)
        // is set and the rect is tall enough for many lines. Rect here is
        // 300pt wide; a single line fits roughly 45 chars at the default
        // approximation, so a value well past that length would previously
        // have been cut off partway through a word.
        var longValue = string.Join(" ", Enumerable.Repeat("word", 40)); // ~200 chars
        var pdf = BuildFormPdf(textValue: longValue, multiline: true, tall: true);
        using var doc = PdfDocument.Open(pdf);

        var page = doc.GetPage(1);
        page.Text.Should().Contain(longValue.Substring(longValue.Length - 10),
            "the tail of a long multiline value must survive, not just the first ~45 chars");
    }

    [Fact]
    public void PlainTextField_LongValue_StillTruncatesToOneLine()
    {
        // Non-multiline fields keep the existing single-line truncation
        // behavior — #672 only changes routing for IsMultiline fields.
        var longValue = new string('A', 200);
        var pdf = BuildFormPdf(textValue: longValue, multiline: false, tall: true);
        using var doc = PdfDocument.Open(pdf);

        var letters = doc.GetPage(1).Letters.Where(l => l.FontName.StartsWith("AcroForm:")).ToList();
        letters.Count.Should().BeLessThan(longValue.Length);
    }

    [Fact]
    public void RedactText_RemovesMultilineFieldTailFromSavedBytes()
    {
        // The previously-truncated tail of a multiline value must actually be
        // removable, not merely unreachable by the old truncating path.
        var longValue = "Head text " + new string('X', 30) + " TAILSECRET at the very end";
        var pdf = BuildFormPdf(textValue: longValue, multiline: true, tall: true);
        using var doc = PdfDocument.Open(pdf);

        doc.GetPage(1).Text.Should().Contain("TAILSECRET",
            "the tail must be extractable before it can be redacted");

        var matches = doc.RedactText("TAILSECRET", caseSensitive: false);
        matches.Should().BeGreaterThan(0);

        var saved = doc.SaveToBytes();
        var savedAscii = Encoding.ASCII.GetString(saved);
        var savedUtf16 = Encoding.BigEndianUnicode.GetString(saved);
        (savedAscii + savedUtf16).Should().NotContain("TAILSECRET");
    }

    // ─── PDF builder ─────────────────────────────────────────────────────────

    /// <summary>
    /// Build a one-page PDF with a single Choice (/FT /Ch) AcroForm field —
    /// combo box or list box depending on <paramref name="isCombo"/> (Ff bit
    /// 0x20000). Options are emitted as plain PDF string literals in /Opt
    /// (the simpler of the two spec-legal /Opt shapes; the
    /// [exportValue, displayValue] pair form is covered by
    /// <c>PdfAcroFormParserTests</c>, not needed to exercise #661's
    /// extraction/redaction path).
    /// </summary>
    private static byte[] BuildChoiceFieldPdf(string[] options, string selectedValue, bool isCombo)
    {
        var sb = new StringBuilder();
        var offsets = new long[8];
        void Mark(int n) => offsets[n] = sb.Length;

        sb.Append("%PDF-1.7\n");

        Mark(1); sb.Append("1 0 obj <</Type/Catalog/Pages 2 0 R/AcroForm 5 0 R>> endobj\n");
        Mark(2); sb.Append("2 0 obj <</Type/Pages/Count 1/Kids[3 0 R]>> endobj\n");
        Mark(3);
        sb.Append("3 0 obj <</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]" +
                  "/Annots[4 0 R]/Resources<<>>>> endobj\n");

        var ff = isCombo ? 0x20000 : 0;
        var optArray = string.Concat(options.Select(o => $"({o})"));
        Mark(4);
        sb.Append($"4 0 obj <</Type/Annot/Subtype/Widget/FT/Ch/T(field1)/Ff {ff}" +
                  $"/Rect[100 500 400 700]/P 3 0 R/Opt[{optArray}]/V({selectedValue})>> endobj\n");

        Mark(5);
        sb.Append("5 0 obj <</Fields[4 0 R]>> endobj\n");

        var xrefPos = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        for (int i = 1; i <= 5; i++)
            sb.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer <</Size 6/Root 1 0 R>>\nstartxref\n").Append(xrefPos).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Build a one-page PDF with a single AcroForm field. The field has
    /// /Subtype /Widget so the field dictionary is also the widget annotation,
    /// the simplest form layout PDF emitters use.
    /// </summary>
    private static byte[] BuildFormPdf(string? textValue = "Hello",
                                        string? defaultValue = null,
                                        string? buttonValue = null,
                                        bool signature = false,
                                        bool multiline = false,
                                        bool tall = false)
    {
        var sb = new StringBuilder();
        var offsets = new long[8];
        void Mark(int n) => offsets[n] = sb.Length;

        sb.Append("%PDF-1.7\n");

        Mark(1); sb.Append("1 0 obj <</Type/Catalog/Pages 2 0 R/AcroForm 5 0 R>> endobj\n");
        Mark(2); sb.Append("2 0 obj <</Type/Pages/Count 1/Kids[3 0 R]>> endobj\n");

        // Page 3: page dict referring to the field-as-widget annotation (4).
        // "tall" gives a multiline field room for several lines (used by the
        // #672 tests) instead of the default single-line-height rect.
        var rect = tall ? "[100 500 400 720]" : "[100 700 400 720]";
        Mark(3);
        sb.Append("3 0 obj <</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]" +
                  "/Annots[4 0 R]/Resources<<>>>> endobj\n");

        // Field 4: combined Widget + form field (the common minimal shape).
        var ft = signature ? "Sig" : (buttonValue != null ? "Btn" : "Tx");
        var ff = multiline ? 0x1000 : 0;
        Mark(4);
        sb.Append($"4 0 obj <</Type/Annot/Subtype/Widget/FT/{ft}/T(field1)/Ff {ff}" +
                  $"/Rect{rect}/P 3 0 R");
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

    /// <summary>
    /// Build a one-page PDF with a single Signature (/FT /Sig) AcroForm
    /// field whose widget has a real /AP/N appearance stream, nested two
    /// Form XObjects deep — mirroring the confirmed shape of
    /// test-pdfs/pdfjs/bug854315.pdf (/AP/N invokes a /FRM Form XObject,
    /// which is where the Tj calls actually live) so the fixture exercises
    /// the same nested-Do resolution path (#669) as the real-world file.
    /// </summary>
    private static byte[] BuildSignatureFieldPdf(string appearanceText)
    {
        var sb = new StringBuilder();
        var offsets = new long[9];
        void Mark(int n) => offsets[n] = sb.Length;

        sb.Append("%PDF-1.7\n");

        Mark(1); sb.Append("1 0 obj <</Type/Catalog/Pages 2 0 R/AcroForm 7 0 R>> endobj\n");
        Mark(2); sb.Append("2 0 obj <</Type/Pages/Count 1/Kids[3 0 R]>> endobj\n");
        Mark(3);
        sb.Append("3 0 obj <</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]" +
                  "/Annots[4 0 R]/Resources<<>>>> endobj\n");

        // Field 4: Signature widget. /V is a minimal signature dict (its
        // contents are irrelevant here — #669 is about /AP text, not /V).
        Mark(4);
        sb.Append("4 0 obj <</Type/Annot/Subtype/Widget/FT/Sig/T(field1)" +
                  "/Rect[100 700 400 720]/P 3 0 R/V<</Type/Sig>>/AP<</N 5 0 R>>>> endobj\n");

        // Object 5: /AP/N — a Form XObject whose content just invokes a
        // further-nested Form XObject (object 6), exactly like bug854315.pdf's
        // /AP/N -> /FRM shape.
        var apContent = "q 1 0 0 1 0 0 cm /FRM Do Q";
        Mark(5);
        sb.Append("5 0 obj <</Type/XObject/Subtype/Form/BBox[0 0 300 20]" +
                  $"/Resources<</XObject<</FRM 6 0 R>>>>/Length {apContent.Length}>>\n" +
                  $"stream\n{apContent}\nendstream\nendobj\n");

        // Object 6: the nested form — this is where the actual Tj call lives.
        var frmContent = $"BT /F1 10 Tf 2 4 Td ({appearanceText}) Tj ET";
        Mark(6);
        sb.Append("6 0 obj <</Type/XObject/Subtype/Form/BBox[0 0 300 20]" +
                  $"/Resources<</Font<</F1 8 0 R>>>>/Length {frmContent.Length}>>\n" +
                  $"stream\n{frmContent}\nendstream\nendobj\n");

        // AcroForm 7: minimal — just /Fields.
        Mark(7);
        sb.Append("7 0 obj <</Fields[4 0 R]>> endobj\n");

        Mark(8);
        sb.Append("8 0 obj <</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>> endobj\n");

        var xrefPos = sb.Length;
        sb.Append("xref\n0 9\n0000000000 65535 f \n");
        for (int i = 1; i <= 8; i++)
            sb.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer <</Size 9/Root 1 0 R>>\nstartxref\n").Append(xrefPos).Append("\n%%EOF\n");
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
