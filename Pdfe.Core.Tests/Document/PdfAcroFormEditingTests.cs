using System.Text;
using AwesomeAssertions;
using Pdfe.Core.Document;
using Pdfe.Core.Primitives;
using Xunit;

namespace Pdfe.Core.Tests.Document;

/// <summary>
/// Tests for AcroForm editing — PdfField.SetValue, PdfDocument.FlattenAcroForm,
/// and round-trip persistence of mutated values.
/// </summary>
public class PdfAcroFormEditingTests
{
    /// <summary>
    /// PDF with: a text field ("Name", value="Alice"), a button field
    /// ("Accept", value=/Yes), a choice field ("Country", value="US",
    /// options=US/UK/FR), a read-only text field ("Locked", value="immutable"),
    /// and an /Annots array on the page that includes all four widgets.
    /// </summary>
    private static byte[] MakePdfWithEditableForm()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long catalogPos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine(@"<<
            /Type /Catalog
            /Pages 2 0 R
            /AcroForm << /Fields [5 0 R 6 0 R 7 0 R 8 0 R] /NeedsAppearances false >>
        >>");
        sb.AppendLine("endobj");

        long pagesPos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        long pagePos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine(@"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]
            /Contents 4 0 R /Annots [5 0 R 6 0 R 7 0 R 8 0 R] >>");
        sb.AppendLine("endobj");

        long contentPos = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Length 0 >>");
        sb.AppendLine("stream");
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");

        // 5 — text field
        long o5 = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine(@"<< /Type /Annot /Subtype /Widget /FT /Tx /T (Name)
            /V (Alice) /DV (Anon) /Ff 0 /Rect [72 720 300 760] /P 3 0 R >>");
        sb.AppendLine("endobj");

        // 6 — button field
        long o6 = sb.Length;
        sb.AppendLine("6 0 obj");
        sb.AppendLine(@"<< /Type /Annot /Subtype /Widget /FT /Btn /T (Accept)
            /V /Yes /AS /Yes /Ff 0 /Rect [72 680 100 700] /P 3 0 R >>");
        sb.AppendLine("endobj");

        // 7 — choice field
        long o7 = sb.Length;
        sb.AppendLine("7 0 obj");
        sb.AppendLine(@"<< /Type /Annot /Subtype /Widget /FT /Ch /T (Country)
            /V (US) /Opt [(US) (UK) (FR)] /Ff 0 /Rect [72 660 200 680] /P 3 0 R >>");
        sb.AppendLine("endobj");

        // 8 — read-only text field (Ff bit 0 = 1)
        long o8 = sb.Length;
        sb.AppendLine("8 0 obj");
        sb.AppendLine(@"<< /Type /Annot /Subtype /Widget /FT /Tx /T (Locked)
            /V (immutable) /Ff 1 /Rect [72 640 200 660] /P 3 0 R >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 9");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{catalogPos:D10} 00000 n ");
        sb.AppendLine($"{pagesPos:D10} 00000 n ");
        sb.AppendLine($"{pagePos:D10} 00000 n ");
        sb.AppendLine($"{contentPos:D10} 00000 n ");
        sb.AppendLine($"{o5:D10} 00000 n ");
        sb.AppendLine($"{o6:D10} 00000 n ");
        sb.AppendLine($"{o7:D10} 00000 n ");
        sb.AppendLine($"{o8:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 9 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static byte[] MakePdfWithRadioForm()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long catalogPos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine("<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] >> >>");
        sb.AppendLine("endobj");

        long pagesPos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        long pagePos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Annots [6 0 R 7 0 R] >>");
        sb.AppendLine("endobj");

        long contentPos = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Length 0 >>");
        sb.AppendLine("stream");
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");

        long fieldPos = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine("<< /FT /Btn /Ff 32768 /T (Choice) /V /Choice2 /Kids [6 0 R 7 0 R] >>");
        sb.AppendLine("endobj");

        long widget1Pos = sb.Length;
        sb.AppendLine("6 0 obj");
        sb.AppendLine("<< /Type /Annot /Subtype /Widget /Parent 5 0 R /Rect [72 700 92 720] /P 3 0 R /AS /Off /AP << /N << /Choice1 <<>> /Off <<>> >> >> >>");
        sb.AppendLine("endobj");

        long widget2Pos = sb.Length;
        sb.AppendLine("7 0 obj");
        sb.AppendLine("<< /Type /Annot /Subtype /Widget /Parent 5 0 R /Rect [72 660 92 680] /P 3 0 R /AS /Choice2 /AP << /N << /Choice2 <<>> /Off <<>> >> >> >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 8");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{catalogPos:D10} 00000 n ");
        sb.AppendLine($"{pagesPos:D10} 00000 n ");
        sb.AppendLine($"{pagePos:D10} 00000 n ");
        sb.AppendLine($"{contentPos:D10} 00000 n ");
        sb.AppendLine($"{fieldPos:D10} 00000 n ");
        sb.AppendLine($"{widget1Pos:D10} 00000 n ");
        sb.AppendLine($"{widget2Pos:D10} 00000 n ");
        sb.AppendLine("trailer << /Size 8 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    // ─── PdfField.SetValue ──────────────────────────────────────────────────

    [Fact]
    public void SetValue_TextField_UpdatesValueAndPersistsAfterRoundTrip()
    {
        using var doc = PdfDocument.Open(MakePdfWithEditableForm());
        var name = doc.GetAcroForm()!.FindField("Name")!;
        name.Value.Should().Be("Alice");

        name.SetValue("Bob");
        name.Value.Should().Be("Bob");

        var saved = doc.SaveToBytes();
        using var reopened = PdfDocument.Open(saved);
        reopened.GetAcroForm()!.FindField("Name")!.Value.Should().Be("Bob");
    }

    [Fact]
    public void SetValue_TextField_SetsNeedAppearancesOnAcroForm()
    {
        using var doc = PdfDocument.Open(MakePdfWithEditableForm());
        var acroFormDict = (PdfDictionary)doc.Resolve(doc.Catalog.GetOptional("AcroForm")!);
        acroFormDict.GetBool("NeedAppearances").Should().BeFalse();

        doc.GetAcroForm()!.FindField("Name")!.SetValue("Bob");

        acroFormDict.GetBool("NeedAppearances").Should().BeTrue(
            "PDF readers need this flag to regenerate the visual appearance after /V changes");
    }

    [Fact]
    public void SetValue_TextField_RoundTripReportsNeedAppearances()
    {
        using var doc = PdfDocument.Open(MakePdfWithEditableForm());
        doc.GetAcroForm()!.FindField("Name")!.SetValue("Bob");

        using var reopened = PdfDocument.Open(doc.SaveToBytes());

        reopened.GetAcroForm()!.NeedsAppearances.Should().BeTrue();
    }

    [Fact]
    public void SetValue_NullClearsValue()
    {
        using var doc = PdfDocument.Open(MakePdfWithEditableForm());
        var name = doc.GetAcroForm()!.FindField("Name")!;
        name.SetValue(null);
        name.Value.Should().BeNull();
        name.RawDictionary.ContainsKey("V").Should().BeFalse();
    }

    [Fact]
    public void SetValue_ReadOnlyField_Throws()
    {
        using var doc = PdfDocument.Open(MakePdfWithEditableForm());
        var locked = doc.GetAcroForm()!.FindField("Locked")!;
        locked.IsReadOnly.Should().BeTrue();

        var act = () => locked.SetValue("changed");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*read-only*");

        // Original value preserved.
        locked.Value.Should().Be("immutable");
    }

    [Fact]
    public void SetValue_ButtonField_StoresAsName_AndUpdatesAS()
    {
        using var doc = PdfDocument.Open(MakePdfWithEditableForm());
        var accept = doc.GetAcroForm()!.FindField("Accept")!;

        accept.SetValue("Off");

        accept.Value.Should().Be("Off");
        accept.RawDictionary["V"].Should().BeOfType<PdfName>().Which.Value.Should().Be("Off");
        accept.RawDictionary.GetNameOrNull("AS").Should().Be("Off",
            "widget /AS must mirror /V so readers without /NeedAppearances render correctly");
    }

    [Fact]
    public void SetValue_ChoiceField_AcceptsValueInOptions()
    {
        using var doc = PdfDocument.Open(MakePdfWithEditableForm());
        var country = doc.GetAcroForm()!.FindField("Country")!;
        country.SetValue("UK");
        country.Value.Should().Be("UK");
    }

    [Fact]
    public void SetValue_ChoiceField_RejectsValueNotInOptions()
    {
        using var doc = PdfDocument.Open(MakePdfWithEditableForm());
        var country = doc.GetAcroForm()!.FindField("Country")!;
        var act = () => country.SetValue("CN");
        act.Should().Throw<ArgumentException>()
            .WithMessage("*not one of the choice field options*");
    }

    [Fact]
    public void Parse_RadioButton_CapturesFlagsAndWidgetExportValues()
    {
        using var doc = PdfDocument.Open(MakePdfWithRadioForm());

        var choice = doc.GetAcroForm()!.FindField("Choice")!;

        choice.IsRadioButton.Should().BeTrue();
        choice.ButtonExportValues.Should().ContainInOrder("Choice1", "Choice2");
        choice.Widgets.Should().HaveCount(2);
        choice.Widgets.Select(w => w.ExportValue).Should().ContainInOrder("Choice1", "Choice2");
    }

    // ─── PdfDocument.FlattenAcroForm ────────────────────────────────────────

    [Fact]
    public void FlattenAcroForm_RemovesAcroFormDictionary()
    {
        using var doc = PdfDocument.Open(MakePdfWithEditableForm());
        doc.Catalog.ContainsKey("AcroForm").Should().BeTrue();

        doc.FlattenAcroForm();

        doc.Catalog.ContainsKey("AcroForm").Should().BeFalse();
        doc.GetAcroForm().Should().BeNull();
    }

    [Fact]
    public void FlattenAcroForm_RemovesWidgetAnnotationsFromPageAnnots()
    {
        using var doc = PdfDocument.Open(MakePdfWithEditableForm());
        var page = doc.GetPage(1);

        page.GetAnnotations().Count.Should().Be(4, "fixture has four widget annotations");

        doc.FlattenAcroForm();

        var remaining = page.GetAnnotations();
        remaining.Should().BeEmpty("all four widgets must be stripped after flattening");
    }

    [Fact]
    public void FlattenAcroForm_BakesTextValueIntoContentStream()
    {
        using var doc = PdfDocument.Open(MakePdfWithEditableForm());
        var name = doc.GetAcroForm()!.FindField("Name")!;
        name.SetValue("Bob");

        doc.FlattenAcroForm();

        var content = Encoding.Latin1.GetString(doc.GetPage(1).GetContentStreamBytes());
        content.Should().Contain("(Bob) Tj",
            "the new field value must be drawn into the page content stream");
    }

    [Fact]
    public void FlattenAcroForm_RoundTripsThroughSave()
    {
        using var doc = PdfDocument.Open(MakePdfWithEditableForm());
        doc.GetAcroForm()!.FindField("Name")!.SetValue("Carol");
        doc.FlattenAcroForm();

        var saved = doc.SaveToBytes();
        using var reopened = PdfDocument.Open(saved);

        reopened.GetAcroForm().Should().BeNull();
        var content = Encoding.Latin1.GetString(reopened.GetPage(1).GetContentStreamBytes());
        content.Should().Contain("(Carol) Tj");
    }

    [Fact]
    public void FlattenAcroForm_EscapesParenthesesInValue()
    {
        using var doc = PdfDocument.Open(MakePdfWithEditableForm());
        doc.GetAcroForm()!.FindField("Name")!.SetValue("A (B) C");

        doc.FlattenAcroForm();

        var content = Encoding.Latin1.GetString(doc.GetPage(1).GetContentStreamBytes());
        content.Should().Contain(@"(A \(B\) C) Tj",
            "literal parentheses must be backslash-escaped or the PDF parser sees an unbalanced group");
    }

    [Fact]
    public void FlattenAcroForm_LongText_ClipsAndWrapsInsideFieldBounds()
    {
        using var doc = PdfDocument.Open(MakePdfWithEditableForm());
        doc.GetAcroForm()!.FindField("Name")!.SetValue(
            "This is a deliberately long field value that should wrap before it leaves the field rectangle.");

        doc.FlattenAcroForm();

        var content = Encoding.Latin1.GetString(doc.GetPage(1).GetContentStreamBytes());
        content.Should().Contain(" re W n", "flattened field text should be clipped to the widget rectangle");
        content.Should().Contain("0 -12 Td", "wrapped flattened field text should move to a second line");
    }

    [Fact]
    public void FlattenAcroForm_RadioButton_DrawsOnlySelectedWidget()
    {
        using var doc = PdfDocument.Open(MakePdfWithRadioForm());

        doc.FlattenAcroForm();

        var content = Encoding.Latin1.GetString(doc.GetPage(1).GetContentStreamBytes());
        content.Split("(X) Tj").Length.Should().Be(2,
            "a radio group should draw a mark only for the selected widget");
        doc.GetPage(1).GetAnnotations().Should().BeEmpty();
        doc.GetAcroForm().Should().BeNull();
    }

    [Fact]
    public void FlattenAcroForm_NoForm_IsNoOp()
    {
        // Build a minimal PDF without an AcroForm.
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");
        long o1 = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine("<< /Type /Catalog /Pages 2 0 R >>");
        sb.AppendLine("endobj");
        long o2 = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");
        long o3 = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");
        long xref = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 4");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{o1:D10} 00000 n ");
        sb.AppendLine($"{o2:D10} 00000 n ");
        sb.AppendLine($"{o3:D10} 00000 n ");
        sb.AppendLine("trailer << /Size 4 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xref.ToString());
        sb.AppendLine("%%EOF");

        using var doc = PdfDocument.Open(Encoding.Latin1.GetBytes(sb.ToString()));
        var act = doc.FlattenAcroForm;
        act.Should().NotThrow();
    }

    [Fact]
    public void SetAcroFormNeedAppearances_FlipsFlag()
    {
        using var doc = PdfDocument.Open(MakePdfWithEditableForm());
        var formDict = (PdfDictionary)doc.Resolve(doc.Catalog.GetOptional("AcroForm")!);
        formDict.GetBool("NeedAppearances").Should().BeFalse();

        doc.SetAcroFormNeedAppearances();

        formDict.GetBool("NeedAppearances").Should().BeTrue();
    }
}
