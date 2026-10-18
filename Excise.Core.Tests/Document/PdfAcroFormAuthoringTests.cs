using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Core.Tests.Document;

/// <summary>
/// Tests for the AcroForm authoring API — adding new form fields to a PDF
/// that may or may not already have an AcroForm.
/// </summary>
public class PdfAcroFormAuthoringTests
{
    /// <summary>
    /// Single-page PDF with no AcroForm. Smallest possible base for
    /// authoring tests.
    /// </summary>
    private static byte[] BarePdf()
    {
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
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    [Fact]
    public void AddTextField_OnPdfWithoutAcroForm_CreatesAcroFormAndField()
    {
        using var doc = PdfDocument.Open(BarePdf());
        doc.GetAcroForm().Should().BeNull("baseline: no AcroForm yet");

        var field = doc.AddTextField(
            pageNumber: 1,
            rect: new PdfRectangle(72, 700, 300, 720),
            fieldName: "Name",
            defaultValue: "Alice");

        field.FullName.Should().Be("Name");
        field.FieldType.Should().Be(PdfFieldType.Text);
        field.Value.Should().Be("Alice");
        field.PageNumber.Should().Be(1);
        field.Rect.Should().NotBeNull();
        field.Rect!.Value.Left.Should().Be(72);

        doc.GetAcroForm().Should().NotBeNull("AddTextField must auto-create the form");
        doc.GetAcroForm()!.Fields.Should().HaveCount(1);
    }

    [Fact]
    public void AddTextField_AppendsToExistingAcroForm_NotReplaceIt()
    {
        using var doc = PdfDocument.Open(BarePdf());
        doc.AddTextField(1, new PdfRectangle(72, 700, 300, 720), "First");
        doc.AddTextField(1, new PdfRectangle(72, 670, 300, 690), "Second");

        var fields = doc.GetAcroForm()!.Fields;
        fields.Should().HaveCount(2);
        fields.Select(f => f.FullName).Should().BeEquivalentTo(new[] { "First", "Second" });
    }

    [Fact]
    public void AddTextField_AppendsWidgetToPageAnnots()
    {
        using var doc = PdfDocument.Open(BarePdf());
        doc.AddTextField(1, new PdfRectangle(72, 700, 300, 720), "F");

        var annots = doc.GetPage(1).GetAnnotations();
        annots.Should().HaveCount(1);
        annots[0].Subtype.Should().Be(PdfAnnotationSubtype.Widget);
    }

    [Fact]
    public void AddTextField_SurvivesSaveAndReload()
    {
        using var doc = PdfDocument.Open(BarePdf());
        var field = doc.AddTextField(1, new PdfRectangle(72, 700, 300, 720), "Email");
        field.SetValue("alice@example.com");

        var saved = doc.SaveToBytes();
        using var reopened = PdfDocument.Open(saved);
        var loaded = reopened.GetAcroForm()!.FindField("Email");
        loaded.Should().NotBeNull();
        loaded!.Value.Should().Be("alice@example.com");
    }

    [Fact]
    public void AddTextField_RegistersHelvInAcroFormDR()
    {
        using var doc = PdfDocument.Open(BarePdf());
        doc.AddTextField(1, new PdfRectangle(72, 700, 300, 720), "F");

        var formDict = (PdfDictionary)doc.Resolve(doc.Catalog.GetOptional("AcroForm")!);
        var dr = (PdfDictionary)doc.Resolve(formDict.GetOptional("DR")!);
        var fonts = (PdfDictionary)doc.Resolve(dr.GetOptional("Font")!);
        fonts.ContainsKey("Helv").Should().BeTrue(
            "default appearance string /DA references /Helv — it must exist in /DR for renderers to resolve it");
    }

    [Fact]
    public void AddTextField_WritesItsOwnAppearance_AndLeavesNeedAppearancesUnset()
    {
        using var doc = PdfDocument.Open(BarePdf());
        doc.AddTextField(1, new PdfRectangle(72, 700, 300, 720), "F");

        // #1444: this used to assert NeedAppearances == true, which pinned the
        // defect — a widget with no /AP that PDF/A forbids and pdf.js/Chrome
        // draw as nothing.
        var formDict = (PdfDictionary)doc.Resolve(doc.Catalog.GetOptional("AcroForm")!);
        formDict.GetBool("NeedAppearances").Should().BeFalse(
            "the widget carries its own /AP; PDF/A forbids NeedAppearances");
        var widget = doc.GetAcroForm()!.FindField("F")!.RawDictionary;
        doc.Resolve(widget.GetOptional("AP")!).Should().BeAssignableTo<PdfDictionary>();
        (widget.GetInt("F", 0) & 4).Should().Be(4, "the widget prints");
    }

    [Fact]
    public void AddTextField_RespectsFlags()
    {
        using var doc = PdfDocument.Open(BarePdf());
        var f = doc.AddTextField(1, new PdfRectangle(72, 700, 300, 720),
            "Notes", multiline: true, readOnly: true, required: true);

        f.IsMultiline.Should().BeTrue();
        f.IsReadOnly.Should().BeTrue();
        f.IsRequired.Should().BeTrue();
    }

    [Fact]
    public void AddCheckBox_DefaultUnchecked_StoresOff()
    {
        using var doc = PdfDocument.Open(BarePdf());
        var f = doc.AddCheckBox(1, new PdfRectangle(72, 700, 90, 720), "Agree");
        f.FieldType.Should().Be(PdfFieldType.Button);
        f.Value.Should().Be("Off");
    }

    [Fact]
    public void AddCheckBox_DefaultChecked_StoresYes()
    {
        using var doc = PdfDocument.Open(BarePdf());
        var f = doc.AddCheckBox(1, new PdfRectangle(72, 700, 90, 720),
            "Subscribe", defaultChecked: true);
        f.Value.Should().Be("Yes");
    }

    [Fact]
    public void AddChoiceField_StoresOptions()
    {
        using var doc = PdfDocument.Open(BarePdf());
        var f = doc.AddChoiceField(1, new PdfRectangle(72, 700, 300, 720),
            "Country", new[] { "US", "UK", "FR" }, defaultValue: "US");

        f.FieldType.Should().Be(PdfFieldType.Choice);
        f.Options.Should().BeEquivalentTo(new[] { "US", "UK", "FR" });
        f.Value.Should().Be("US");
    }

    [Fact]
    public void AddChoiceField_DefaultNotInOptions_Throws()
    {
        using var doc = PdfDocument.Open(BarePdf());
        var act = () => doc.AddChoiceField(1, new PdfRectangle(72, 700, 300, 720),
            "Country", new[] { "US" }, defaultValue: "CN");

        act.Should().Throw<ArgumentException>().WithMessage("*not one of the supplied options*");
    }

    [Fact]
    public void AddChoiceField_NoOptions_Throws()
    {
        using var doc = PdfDocument.Open(BarePdf());
        var act = () => doc.AddChoiceField(1, new PdfRectangle(72, 700, 300, 720),
            "X", Array.Empty<string>());

        act.Should().Throw<ArgumentException>().WithMessage("*at least one option*");
    }

    [Fact]
    public void AddSignatureField_MarksSignatureType()
    {
        using var doc = PdfDocument.Open(BarePdf());
        var f = doc.AddSignatureField(1, new PdfRectangle(72, 700, 300, 720), "ApproverSig");
        f.FieldType.Should().Be(PdfFieldType.Signature);
    }

    [Fact]
    public void AddTextField_EmptyName_Throws()
    {
        using var doc = PdfDocument.Open(BarePdf());
        var act = () => doc.AddTextField(1, new PdfRectangle(72, 700, 300, 720), "");
        act.Should().Throw<ArgumentException>().WithMessage("*must not be empty*");
    }

    [Fact]
    public void AddTextField_AfterFlatten_RecreatesAcroForm()
    {
        using var doc = PdfDocument.Open(BarePdf());
        doc.AddTextField(1, new PdfRectangle(72, 700, 300, 720), "Existing")
            .SetValue("baked");
        doc.FlattenAcroForm();
        doc.GetAcroForm().Should().BeNull();

        // After flatten, adding a new field must work and create a fresh form.
        doc.AddTextField(1, new PdfRectangle(72, 670, 300, 690), "New");
        doc.GetAcroForm()!.Fields.Single().FullName.Should().Be("New");
    }

    // ─── #2052: a field must be a field of its own, on its page ───

    public static TheoryData<string> AddKinds() => new() { "text", "date", "checkbox", "choice", "signature" };

    private static PdfField Add(PdfDocument doc, string kind, PdfRectangle rect, string name) => kind switch
    {
        "text" => doc.AddTextField(1, rect, name),
        "date" => doc.AddDateField(1, rect, name),
        "checkbox" => doc.AddCheckBox(1, rect, name),
        "choice" => doc.AddChoiceField(1, rect, name, new[] { "A", "B" }),
        _ => doc.AddSignatureField(1, rect, name),
    };

    [Theory]
    [MemberData(nameof(AddKinds))]
    public void AddField_RefusesAFullNameTheFormAlreadyHas_AndAddsNothing(string kind)
    {
        // Two fields with one full name: fill-form reached only the first, and other
        // readers link the two values (#2052).
        using var doc = PdfDocument.Open(BarePdf());
        doc.AddTextField(1, new PdfRectangle(72, 700, 300, 720), "Applicant");

        var act = () => Add(doc, kind, new PdfRectangle(72, 500, 300, 520), "Applicant");

        act.Should().Throw<ArgumentException>().WithMessage("*'Applicant' already exists*");
        doc.GetAcroForm()!.Fields.Should().ContainSingle();
        doc.GetPage(1).GetAnnotations().Should().ContainSingle();
    }

    [Fact]
    public void AddField_RefusesTheFullNameOfANestedField()
    {
        using var doc = PdfDocument.Open(BarePdf());
        var parent = new PdfDictionary();
        parent.SetString("T", "Address");
        var kid = new PdfDictionary();
        kid.SetString("T", "City");
        kid.SetName("FT", "Tx");
        var kidRef = doc.AddIndirectObject(kid);
        kid["Parent"] = doc.AddIndirectObject(parent);
        parent["Kids"] = new PdfArray { kidRef };
        doc.AddTextField(1, new PdfRectangle(72, 700, 300, 720), "Other");
        var fields = (PdfArray)doc.Resolve(((PdfDictionary)doc.Resolve(doc.Catalog["AcroForm"]))["Fields"]);
        fields.Add(kid["Parent"]);

        var act = () => doc.AddTextField(1, new PdfRectangle(72, 500, 300, 520), "Address.City");
        act.Should().Throw<ArgumentException>().WithMessage("*'Address.City' already exists*");
        doc.AddTextField(1, new PdfRectangle(72, 450, 300, 470), "City").FullName.Should().Be("City",
            "only the full name is taken, not a partial one");
    }

    [Theory]
    [InlineData(300, 720, 72, 700, "inverted")]
    [InlineData(72, 720, 300, 700, "inverted")]
    [InlineData(72, 700, 72, 720, "inverted or empty")]
    [InlineData(700, 900, 800, 920, "outside page 1")]
    [InlineData(-200, 100, -10, 120, "outside page 1")]
    [InlineData(100, 792, 200, 800, "outside page 1")]
    public void AddField_RefusesAnInvertedEmptyOrOffPageRectangle(double l, double b, double r, double t, string why)
    {
        using var doc = PdfDocument.Open(BarePdf());
        foreach (var kind in new[] { "text", "date", "checkbox", "choice", "signature" })
        {
            var act = () => Add(doc, kind, new PdfRectangle(l, b, r, t), "F_" + kind);
            act.Should().Throw<ArgumentException>($"a {kind} field").WithMessage($"*{why}*");
        }
        doc.GetAcroForm().Should().BeNull("nothing was added");
    }

    [Fact]
    public void AddField_AcceptsARectanglePartlyOnThePage()
    {
        using var doc = PdfDocument.Open(BarePdf());
        doc.AddTextField(1, new PdfRectangle(-20, 780, 40, 800), "Edge").FullName.Should().Be("Edge");
    }
}
