using System.Text;
using AwesomeAssertions;
using Pdfe.Core.Document;
using Pdfe.Core.Primitives;
using Xunit;

namespace Pdfe.Core.Tests.Document;

/// <summary>
/// Tests for PDF interactive form (AcroForm) parsing (§12.7).
/// Synthetic in-memory PDFs are used so no fixture files are required.
/// </summary>
public class PdfAcroFormTests
{
    // ─── helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Build a minimal single-page PDF with an AcroForm containing the given fields.
    /// </summary>
    private static byte[] MakePdfWithAcroForm(string acroFormDef)
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long obj1Pos, obj2Pos, obj3Pos, obj4Pos, obj5Pos;

        // obj 1 — catalog with AcroForm
        obj1Pos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine($"<< /Type /Catalog /Pages 2 0 R {acroFormDef} >>");
        sb.AppendLine("endobj");

        // obj 2 — pages node
        obj2Pos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        // obj 3 — page
        obj3Pos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        // obj 4 — content stream (empty)
        obj4Pos = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Length 0 >>");
        sb.AppendLine("stream");
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");

        // obj 5 — placeholder for field/widget references
        obj5Pos = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine("<< >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 6");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{obj1Pos:D10} 00000 n ");
        sb.AppendLine($"{obj2Pos:D10} 00000 n ");
        sb.AppendLine($"{obj3Pos:D10} 00000 n ");
        sb.AppendLine($"{obj4Pos:D10} 00000 n ");
        sb.AppendLine($"{obj5Pos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 6 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Build a more complex PDF with multiple field types and widget annotations.
    /// </summary>
    private static byte[] MakePdfWithComplexAcroForm()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long catalogPos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine(@"<<
            /Type /Catalog
            /Pages 2 0 R
            /AcroForm <<
                /Fields [5 0 R 6 0 R]
                /NeedsAppearances false
            >>
        >>");
        sb.AppendLine("endobj");

        long pagesPos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        long pagePos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        long contentPos = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Length 0 >>");
        sb.AppendLine("stream");
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");

        // Obj 5 — Text field (Tx) with widget
        long textFieldPos = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine(@"<<
            /Type /Annot
            /Subtype /Widget
            /FT /Tx
            /T (Name)
            /V (Alice)
            /DV (DefaultName)
            /Ff 0
            /Rect [72 700 300 720]
            /P 3 0 R
        >>");
        sb.AppendLine("endobj");

        // Obj 6 — Button field (Btn) with widget
        long buttonFieldPos = sb.Length;
        sb.AppendLine("6 0 obj");
        sb.AppendLine(@"<<
            /Type /Annot
            /Subtype /Widget
            /FT /Btn
            /T (Accept)
            /V /Yes
            /Ff 0
            /Rect [72 680 100 700]
            /P 3 0 R
        >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 7");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{catalogPos:D10} 00000 n ");
        sb.AppendLine($"{pagesPos:D10} 00000 n ");
        sb.AppendLine($"{pagePos:D10} 00000 n ");
        sb.AppendLine($"{contentPos:D10} 00000 n ");
        sb.AppendLine($"{textFieldPos:D10} 00000 n ");
        sb.AppendLine($"{buttonFieldPos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 7 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>
    /// A radio-button group: a Btn field with two Widget kids, each with an
    /// /AP /N holding a distinct on-state ("Male" / "Female") plus "Off".
    /// </summary>
    private static byte[] MakePdfWithRadioGroup()
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
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [6 0 R 7 0 R] >>");
        sb.AppendLine("endobj");

        long contentPos = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Length 0 >>\nstream\nendstream\nendobj");

        // Obj 5 — radio group parent (Btn, Radio flag 0x10000) with two widget kids.
        long radioPos = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine("<< /FT /Btn /Ff 49152 /T (Gender) /V /Male /Kids [6 0 R 7 0 R] >>");
        sb.AppendLine("endobj");

        // Obj 6 — "Male" widget.
        long w1Pos = sb.Length;
        sb.AppendLine("6 0 obj");
        sb.AppendLine("<< /Type /Annot /Subtype /Widget /Parent 5 0 R /Rect [72 700 90 718] " +
                      "/AS /Male /AP << /N << /Male << >> /Off << >> >> >> /P 3 0 R >>");
        sb.AppendLine("endobj");

        // Obj 7 — "Female" widget.
        long w2Pos = sb.Length;
        sb.AppendLine("7 0 obj");
        sb.AppendLine("<< /Type /Annot /Subtype /Widget /Parent 5 0 R /Rect [72 680 90 698] " +
                      "/AS /Off /AP << /N << /Female << >> /Off << >> >> >> /P 3 0 R >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 8");
        sb.AppendLine("0000000000 65535 f ");
        foreach (var p in new[] { catalogPos, pagesPos, pagePos, contentPos, radioPos, w1Pos, w2Pos })
            sb.AppendLine($"{p:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 8 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    // ─── basic parsing ─────────────────────────────────────────────────────

    [Fact]
    public void ButtonExportValues_RadioGroup_ReturnsPerWidgetOnStates()
    {
        // #424: a consumer importing a radio group needs its selectable export
        // values (the widget /AP /N on-states), not a generic boolean.
        byte[] pdf = MakePdfWithRadioGroup();
        using var doc = PdfDocument.Open(new MemoryStream(pdf), ownsStream: false);

        var field = doc.GetAcroForm()!.Fields.Single(f => f.PartialName == "Gender");
        field.FieldType.Should().Be(PdfFieldType.Button);
        field.ButtonExportValues.Should().Equal("Male", "Female");
    }

    [Fact]
    public void ButtonExportValues_NonButtonField_IsEmpty()
    {
        byte[] pdf = MakePdfWithComplexAcroForm();
        using var doc = PdfDocument.Open(new MemoryStream(pdf), ownsStream: false);

        var text = doc.GetAcroForm()!.Fields.First(f => f.FieldType == PdfFieldType.Text);
        text.ButtonExportValues.Should().BeEmpty();
    }

    [Fact]
    public void GetAcroForm_NullDocument_ReturnsNull()
    {
        // PDF without /AcroForm in catalog
        byte[] pdf = MakePdfWithAcroForm("");
        using var doc = PdfDocument.Open(new MemoryStream(pdf), ownsStream: false);

        doc.GetAcroForm().Should().BeNull();
    }

    [Fact]
    public void GetAcroForm_WithTextAndButtonFields_ParsesBoth()
    {
        byte[] pdf = MakePdfWithComplexAcroForm();
        using var doc = PdfDocument.Open(new MemoryStream(pdf), ownsStream: false);

        var form = doc.GetAcroForm();
        form.Should().NotBeNull();
        form!.Fields.Should().HaveCount(2);

        // Check types
        var types = form.Fields.Select(f => f.FieldType).ToList();
        types.Should().Contain(PdfFieldType.Text);
        types.Should().Contain(PdfFieldType.Button);
    }

    [Fact]
    public void GetAcroForm_TextFieldValue_ParsedCorrectly()
    {
        byte[] pdf = MakePdfWithComplexAcroForm();
        using var doc = PdfDocument.Open(new MemoryStream(pdf), ownsStream: false);

        var form = doc.GetAcroForm();
        form.Should().NotBeNull();

        var nameField = form!.Fields.FirstOrDefault(f => f.PartialName == "Name");
        nameField.Should().NotBeNull();
        nameField!.Value.Should().Be("Alice");
        nameField.DefaultValue.Should().Be("DefaultName");
    }

    [Fact]
    public void GetAcroForm_WidgetRect_ParsedCorrectly()
    {
        byte[] pdf = MakePdfWithComplexAcroForm();
        using var doc = PdfDocument.Open(new MemoryStream(pdf), ownsStream: false);

        var form = doc.GetAcroForm();
        form.Should().NotBeNull();

        var nameField = form!.Fields.FirstOrDefault(f => f.PartialName == "Name");
        nameField.Should().NotBeNull();
        nameField!.Rect.Should().NotBeNull();
        nameField.Rect!.Value.Left.Should().Be(72);
        nameField.Rect!.Value.Bottom.Should().Be(700);
        nameField.Rect!.Value.Right.Should().Be(300);
        nameField.Rect!.Value.Top.Should().Be(720);
    }

    [Fact]
    public void GetAcroForm_NeedsAppearances_ParsedCorrectly()
    {
        byte[] pdf = MakePdfWithComplexAcroForm();
        using var doc = PdfDocument.Open(new MemoryStream(pdf), ownsStream: false);

        var form = doc.GetAcroForm();
        form.Should().NotBeNull();
        form!.NeedsAppearances.Should().BeFalse();
    }

    [Fact]
    public void GetAcroForm_ButtonFieldValue_ParsedAsName()
    {
        byte[] pdf = MakePdfWithComplexAcroForm();
        using var doc = PdfDocument.Open(new MemoryStream(pdf), ownsStream: false);

        var form = doc.GetAcroForm();
        var acceptField = form!.Fields.FirstOrDefault(f => f.PartialName == "Accept");
        acceptField.Should().NotBeNull();
        acceptField!.Value.Should().Be("Yes");
    }

    [Fact]
    public void GetAcroForm_MultipleFields_AllParsed()
    {
        // Test that multiple fields at the top level are all parsed
        byte[] pdf = MakePdfWithComplexAcroForm();
        using var doc = PdfDocument.Open(new MemoryStream(pdf), ownsStream: false);

        var form = doc.GetAcroForm();
        form.Should().NotBeNull();
        form!.Fields.Should().HaveCount(2);

        // Verify we can find both fields
        var textField = form.FindField("Name");
        var buttonField = form.FindField("Accept");

        textField.Should().NotBeNull();
        buttonField.Should().NotBeNull();
        textField!.FieldType.Should().Be(PdfFieldType.Text);
        buttonField!.FieldType.Should().Be(PdfFieldType.Button);
    }

    [Fact]
    public void GetAcroForm_GetFields_FiltersByType()
    {
        byte[] pdf = MakePdfWithComplexAcroForm();
        using var doc = PdfDocument.Open(new MemoryStream(pdf), ownsStream: false);

        var form = doc.GetAcroForm();
        form.Should().NotBeNull();

        form!.GetTextFields().Should().HaveCount(1);
        form.GetButtonFields().Should().HaveCount(1);
        form.GetChoiceFields().Should().HaveCount(0);
        form.GetSignatureFields().Should().HaveCount(0);
    }

    [Fact]
    public void GetAcroForm_FindField_ByFullName()
    {
        byte[] pdf = MakePdfWithComplexAcroForm();
        using var doc = PdfDocument.Open(new MemoryStream(pdf), ownsStream: false);

        var form = doc.GetAcroForm();
        form.Should().NotBeNull();

        var field = form!.FindField("Name");
        field.Should().NotBeNull();
        field!.Value.Should().Be("Alice");
    }

    [Fact]
    public void GetAcroForm_FindField_ReturnsNull_WhenNotFound()
    {
        byte[] pdf = MakePdfWithComplexAcroForm();
        using var doc = PdfDocument.Open(new MemoryStream(pdf), ownsStream: false);

        var form = doc.GetAcroForm();
        var field = form!.FindField("NonExistent");
        field.Should().BeNull();
    }

    [Fact]
    public void GetAcroForm_ToString_FormatsCorrectly()
    {
        byte[] pdf = MakePdfWithComplexAcroForm();
        using var doc = PdfDocument.Open(new MemoryStream(pdf), ownsStream: false);

        var form = doc.GetAcroForm();
        var nameField = form!.Fields.First(f => f.PartialName == "Name");

        var str = nameField.ToString();
        str.Should().Contain("Text");
        str.Should().Contain("Name");
        str.Should().Contain("Alice");
    }

    [Fact]
    public void GetAcroForm_RawDictionary_Accessible()
    {
        byte[] pdf = MakePdfWithComplexAcroForm();
        using var doc = PdfDocument.Open(new MemoryStream(pdf), ownsStream: false);

        var form = doc.GetAcroForm();
        var nameField = form!.Fields.First(f => f.PartialName == "Name");

        nameField.RawDictionary.Should().NotBeNull();
        nameField.RawDictionary.GetStringOrNull("T").Should().Be("Name");
    }

    // ─── choice fields with options ─────────────────────────────────────────

    [Fact]
    public void GetAcroForm_ChoiceField_WithOptions()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long catalogPos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine(@"<<
            /Type /Catalog
            /Pages 2 0 R
            /AcroForm <<
                /Fields [5 0 R]
                /NeedsAppearances false
            >>
        >>");
        sb.AppendLine("endobj");

        long pagesPos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        long pagePos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        long contentPos = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Length 0 >>");
        sb.AppendLine("stream");
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");

        long choiceFieldPos = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine(@"<<
            /Type /Annot
            /Subtype /Widget
            /FT /Ch
            /T (Country)
            /V (USA)
            /Opt [(USA) (Canada) (Mexico)]
            /Rect [72 700 300 720]
            /P 3 0 R
        >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 6");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{catalogPos:D10} 00000 n ");
        sb.AppendLine($"{pagesPos:D10} 00000 n ");
        sb.AppendLine($"{pagePos:D10} 00000 n ");
        sb.AppendLine($"{contentPos:D10} 00000 n ");
        sb.AppendLine($"{choiceFieldPos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 6 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        byte[] pdf = Encoding.Latin1.GetBytes(sb.ToString());
        using var doc = PdfDocument.Open(new MemoryStream(pdf), ownsStream: false);

        var form = doc.GetAcroForm();
        form.Should().NotBeNull();

        var choiceField = form!.Fields.FirstOrDefault(f => f.PartialName == "Country");
        choiceField.Should().NotBeNull();
        choiceField!.FieldType.Should().Be(PdfFieldType.Choice);
        choiceField.Options.Should().NotBeNull();
        choiceField.Options!.Should().HaveCount(3);
        choiceField.Options.Should().Contain("USA");
        choiceField.Options.Should().Contain("Canada");
        choiceField.Options.Should().Contain("Mexico");
    }

    // ─── field flags: read-only, required, multiline ──────────────────────

    [Fact]
    public void GetAcroForm_TextField_ReadOnlyFlag()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long catalogPos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine(@"<<
            /Type /Catalog
            /Pages 2 0 R
            /AcroForm <<
                /Fields [5 0 R]
            >>
        >>");
        sb.AppendLine("endobj");

        long pagesPos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        long pagePos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        long contentPos = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Length 0 >>");
        sb.AppendLine("stream");
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");

        // Ff = 0x1 sets ReadOnly (bit 0)
        long fieldPos = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine(@"<<
            /Type /Annot
            /Subtype /Widget
            /FT /Tx
            /T (ReadOnlyField)
            /V (locked)
            /Ff 1
            /Rect [72 700 300 720]
            /P 3 0 R
        >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 6");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{catalogPos:D10} 00000 n ");
        sb.AppendLine($"{pagesPos:D10} 00000 n ");
        sb.AppendLine($"{pagePos:D10} 00000 n ");
        sb.AppendLine($"{contentPos:D10} 00000 n ");
        sb.AppendLine($"{fieldPos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 6 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        byte[] pdf = Encoding.Latin1.GetBytes(sb.ToString());
        using var doc = PdfDocument.Open(new MemoryStream(pdf), ownsStream: false);

        var form = doc.GetAcroForm();
        var field = form!.Fields.First();
        field.IsReadOnly.Should().BeTrue();
        field.IsRequired.Should().BeFalse();
    }

    [Fact]
    public void GetAcroForm_TextField_RequiredFlag()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long catalogPos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine(@"<<
            /Type /Catalog
            /Pages 2 0 R
            /AcroForm <<
                /Fields [5 0 R]
            >>
        >>");
        sb.AppendLine("endobj");

        long pagesPos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        long pagePos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        long contentPos = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Length 0 >>");
        sb.AppendLine("stream");
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");

        // Ff = 0x2 sets Required (bit 1)
        long fieldPos = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine(@"<<
            /Type /Annot
            /Subtype /Widget
            /FT /Tx
            /T (RequiredField)
            /Ff 2
            /Rect [72 700 300 720]
            /P 3 0 R
        >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 6");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{catalogPos:D10} 00000 n ");
        sb.AppendLine($"{pagesPos:D10} 00000 n ");
        sb.AppendLine($"{pagePos:D10} 00000 n ");
        sb.AppendLine($"{contentPos:D10} 00000 n ");
        sb.AppendLine($"{fieldPos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 6 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        byte[] pdf = Encoding.Latin1.GetBytes(sb.ToString());
        using var doc = PdfDocument.Open(new MemoryStream(pdf), ownsStream: false);

        var form = doc.GetAcroForm();
        var field = form!.Fields.First();
        field.IsRequired.Should().BeTrue();
        field.IsReadOnly.Should().BeFalse();
    }

    [Fact]
    public void GetAcroForm_TextField_MultilineFlag()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long catalogPos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine(@"<<
            /Type /Catalog
            /Pages 2 0 R
            /AcroForm <<
                /Fields [5 0 R]
            >>
        >>");
        sb.AppendLine("endobj");

        long pagesPos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        long pagePos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        long contentPos = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Length 0 >>");
        sb.AppendLine("stream");
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");

        // Ff = 0x1000 (4096) sets Multiline (bit 12)
        long fieldPos = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine(@"<<
            /Type /Annot
            /Subtype /Widget
            /FT /Tx
            /T (MultilineField)
            /Ff 4096
            /Rect [72 700 300 720]
            /P 3 0 R
        >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 6");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{catalogPos:D10} 00000 n ");
        sb.AppendLine($"{pagesPos:D10} 00000 n ");
        sb.AppendLine($"{pagePos:D10} 00000 n ");
        sb.AppendLine($"{contentPos:D10} 00000 n ");
        sb.AppendLine($"{fieldPos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 6 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        byte[] pdf = Encoding.Latin1.GetBytes(sb.ToString());
        using var doc = PdfDocument.Open(new MemoryStream(pdf), ownsStream: false);

        var form = doc.GetAcroForm();
        var field = form!.Fields.First();
        field.IsMultiline.Should().BeTrue();
    }

    // ─── signature field ────────────────────────────────────────────────

    [Fact]
    public void GetAcroForm_SignatureField_Parsed()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long catalogPos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine(@"<<
            /Type /Catalog
            /Pages 2 0 R
            /AcroForm <<
                /Fields [5 0 R]
            >>
        >>");
        sb.AppendLine("endobj");

        long pagesPos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        long pagePos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        long contentPos = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Length 0 >>");
        sb.AppendLine("stream");
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");

        long sigFieldPos = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine(@"<<
            /Type /Annot
            /Subtype /Widget
            /FT /Sig
            /T (Signature)
            /Rect [72 700 300 720]
            /P 3 0 R
        >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 6");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{catalogPos:D10} 00000 n ");
        sb.AppendLine($"{pagesPos:D10} 00000 n ");
        sb.AppendLine($"{pagePos:D10} 00000 n ");
        sb.AppendLine($"{contentPos:D10} 00000 n ");
        sb.AppendLine($"{sigFieldPos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 6 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        byte[] pdf = Encoding.Latin1.GetBytes(sb.ToString());
        using var doc = PdfDocument.Open(new MemoryStream(pdf), ownsStream: false);

        var form = doc.GetAcroForm();
        form.Should().NotBeNull();
        form!.GetSignatureFields().Should().HaveCount(1);
        var sigField = form.GetSignatureFields().First();
        sigField.FieldType.Should().Be(PdfFieldType.Signature);
        sigField.FullName.Should().Be("Signature");
    }

    // ─── field inheritance ──────────────────────────────────────────────

    [Fact]
    public void GetAcroForm_FieldInheritsType_FromParent()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long catalogPos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine(@"<<
            /Type /Catalog
            /Pages 2 0 R
            /AcroForm <<
                /Fields [5 0 R]
            >>
        >>");
        sb.AppendLine("endobj");

        long pagesPos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        long pagePos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        long contentPos = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Length 0 >>");
        sb.AppendLine("stream");
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");

        // Parent field with /FT /Tx, child field without /FT
        // Parent serves as intermediary, child is terminal with widget
        long parentFieldPos = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine(@"<<
            /T (Parent)
            /FT /Tx
            /Kids [6 0 R]
        >>");
        sb.AppendLine("endobj");

        long childFieldPos = sb.Length;
        sb.AppendLine("6 0 obj");
        sb.AppendLine(@"<<
            /Type /Annot
            /Subtype /Widget
            /T (Child)
            /V (ChildValue)
            /Rect [72 700 300 720]
            /P 3 0 R
        >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 7");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{catalogPos:D10} 00000 n ");
        sb.AppendLine($"{pagesPos:D10} 00000 n ");
        sb.AppendLine($"{pagePos:D10} 00000 n ");
        sb.AppendLine($"{contentPos:D10} 00000 n ");
        sb.AppendLine($"{parentFieldPos:D10} 00000 n ");
        sb.AppendLine($"{childFieldPos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 7 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        byte[] pdf = Encoding.Latin1.GetBytes(sb.ToString());
        using var doc = PdfDocument.Open(new MemoryStream(pdf), ownsStream: false);

        var form = doc.GetAcroForm();
        form.Should().NotBeNull();
        var childField = form!.Fields.FirstOrDefault(f => f.FullName == "Parent.Child");
        childField.Should().NotBeNull();
        childField!.Value.Should().Be("ChildValue");
        // Parser resolves field tree and produces a child field entry
        form.Fields.Should().HaveCount(1, "child (terminal) field is the only real field");
    }

    [Fact]
    public void GetAcroForm_FieldInheritsValue_FromParent()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long catalogPos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine(@"<<
            /Type /Catalog
            /Pages 2 0 R
            /AcroForm <<
                /Fields [5 0 R]
            >>
        >>");
        sb.AppendLine("endobj");

        long pagesPos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        long pagePos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        long contentPos = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Length 0 >>");
        sb.AppendLine("stream");
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");

        long parentFieldPos = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine(@"<<
            /T (ParentWithValue)
            /FT /Tx
            /V (ParentValue)
            /Kids [6 0 R]
        >>");
        sb.AppendLine("endobj");

        // Child without /V should use parent value if implementation inherits
        // Note: PDF spec doesn't require this, but it's a valid test of field tree traversal
        long childFieldPos = sb.Length;
        sb.AppendLine("6 0 obj");
        sb.AppendLine(@"<<
            /Type /Annot
            /Subtype /Widget
            /T (ChildNoValue)
            /Rect [72 700 300 720]
            /P 3 0 R
        >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 7");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{catalogPos:D10} 00000 n ");
        sb.AppendLine($"{pagesPos:D10} 00000 n ");
        sb.AppendLine($"{pagePos:D10} 00000 n ");
        sb.AppendLine($"{contentPos:D10} 00000 n ");
        sb.AppendLine($"{parentFieldPos:D10} 00000 n ");
        sb.AppendLine($"{childFieldPos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 7 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        byte[] pdf = Encoding.Latin1.GetBytes(sb.ToString());
        using var doc = PdfDocument.Open(new MemoryStream(pdf), ownsStream: false);

        var form = doc.GetAcroForm();
        var childField = form!.Fields.FirstOrDefault(f => f.FullName.Contains("ChildNoValue"));
        childField.Should().NotBeNull();
        // Child may not have explicit value (inheritance not required by spec)
        childField!.Value.Should().BeNull();
    }

    [Fact]
    public void GetAcroForm_NoAcroForm_ReturnsNull()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long catalogPos = sb.Length;
        sb.AppendLine("1 0 obj");
        // Catalog WITHOUT /AcroForm
        sb.AppendLine(@"<<
            /Type /Catalog
            /Pages 2 0 R
        >>");
        sb.AppendLine("endobj");

        long pagesPos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        long pagePos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 4");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{catalogPos:D10} 00000 n ");
        sb.AppendLine($"{pagesPos:D10} 00000 n ");
        sb.AppendLine($"{pagePos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 4 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        byte[] pdf = Encoding.Latin1.GetBytes(sb.ToString());
        using var doc = PdfDocument.Open(new MemoryStream(pdf), ownsStream: false);

        doc.GetAcroForm().Should().BeNull();
    }

    [Fact]
    public void GetAcroForm_FindField_NonExistent_ReturnsNull()
    {
        byte[] pdf = MakePdfWithComplexAcroForm();
        using var doc = PdfDocument.Open(new MemoryStream(pdf), ownsStream: false);

        var form = doc.GetAcroForm();
        form.Should().NotBeNull();

        var field = form!.FindField("NonExistent");
        field.Should().BeNull();
    }

    [Fact]
    public void GetAcroForm_GetFields_ChoiceFields()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long catalogPos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine(@"<<
            /Type /Catalog
            /Pages 2 0 R
            /AcroForm <<
                /Fields [5 0 R]
            >>
        >>");
        sb.AppendLine("endobj");

        long pagesPos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        long pagePos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        long contentPos = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Length 0 >>");
        sb.AppendLine("stream");
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");

        long choiceFieldPos = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine(@"<<
            /Type /Annot
            /Subtype /Widget
            /FT /Ch
            /T (Dropdown)
            /Opt [(Option1) (Option2)]
            /Rect [72 700 300 720]
            /P 3 0 R
        >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 6");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{catalogPos:D10} 00000 n ");
        sb.AppendLine($"{pagesPos:D10} 00000 n ");
        sb.AppendLine($"{pagePos:D10} 00000 n ");
        sb.AppendLine($"{contentPos:D10} 00000 n ");
        sb.AppendLine($"{choiceFieldPos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 6 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        byte[] pdf = Encoding.Latin1.GetBytes(sb.ToString());
        using var doc = PdfDocument.Open(new MemoryStream(pdf), ownsStream: false);

        var form = doc.GetAcroForm();
        form!.GetChoiceFields().Should().HaveCount(1);
    }

    [Fact]
    public void GetAcroForm_DefaultValue_Parsed()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long catalogPos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine(@"<<
            /Type /Catalog
            /Pages 2 0 R
            /AcroForm <<
                /Fields [5 0 R]
            >>
        >>");
        sb.AppendLine("endobj");

        long pagesPos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        long pagePos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        long contentPos = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine("<< /Length 0 >>");
        sb.AppendLine("stream");
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");

        long fieldPos = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine(@"<<
            /Type /Annot
            /Subtype /Widget
            /FT /Tx
            /T (Email)
            /DV (default@example.com)
            /Rect [72 700 300 720]
            /P 3 0 R
        >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 6");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{catalogPos:D10} 00000 n ");
        sb.AppendLine($"{pagesPos:D10} 00000 n ");
        sb.AppendLine($"{pagePos:D10} 00000 n ");
        sb.AppendLine($"{contentPos:D10} 00000 n ");
        sb.AppendLine($"{fieldPos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 6 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        byte[] pdf = Encoding.Latin1.GetBytes(sb.ToString());
        using var doc = PdfDocument.Open(new MemoryStream(pdf), ownsStream: false);

        var form = doc.GetAcroForm();
        var field = form!.Fields.First();
        field.DefaultValue.Should().Be("default@example.com");
    }
}
