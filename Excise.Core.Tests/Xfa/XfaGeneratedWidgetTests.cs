using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Core.Xfa;
using Excise.TestSupport;

namespace Excise.Core.Tests.Xfa;

/// <summary>
/// #2028 (XFA Phase 3 S1): the AcroForm fields the layout generates for a dynamic form (ISO 32000-2
/// Annex K.2). These pin the structure excise writes, from the specification pages cited; whether
/// other readers see the same fields, values and ink is checked with qpdf and mutool in
/// Excise.Rendering.Tests (XfaGeneratedWidgetOracleTests).
/// </summary>
public class XfaGeneratedWidgetTests
{
    private static string Text(string name, string y, string extra = "", string ui = "<textEdit/>") =>
        $"<field name=\"{name}\" x=\"1in\" y=\"{y}\" w=\"3in\" h=\"0.3in\"><ui>{ui}</ui>{extra}</field>";

    /// <summary>One field of each kind S1 maps, on one positioned page.</summary>
    private static readonly string KindsTemplate = XfaTestForms.Template(
        "<subform name=\"S\" x=\"0in\" y=\"0in\" w=\"8in\" h=\"10in\" layout=\"position\">"
        + Text("Name", "0.5in")
        + Text("Notes", "1in", "<value><text maxChars=\"40\"/></value>", "<textEdit multiLine=\"1\"/>")
        + Text("Code", "1.5in", "<value><text maxChars=\"4\"/></value>", "<textEdit><comb numberOfCells=\"4\"/></textEdit>")
        + Text("Secret", "2in", "", "<passwordEdit/>")
        + Text("Agree", "2.5in", "<items><text>Y</text><text>N</text></items>", "<checkButton/>")
        + Text("Country", "3in", "<items><text>Canada</text><text>France</text></items><items save=\"1\"><text>CA</text><text>FR</text></items>", "<choiceList/>")
        + "<exclGroup name=\"Sex\" x=\"1in\" y=\"3.5in\" w=\"3in\" h=\"0.3in\" layout=\"lr-tb\">"
        + "<field name=\"M\" w=\"1in\" h=\"0.3in\"><ui><checkButton shape=\"round\"/></ui><items><text>M</text></items></field>"
        + "<field name=\"F\" w=\"1in\" h=\"0.3in\"><ui><checkButton shape=\"round\"/></ui><items><text>F</text></items></field>"
        + "</exclGroup>"
        + Text("Go", "4in", "", "<button/>")
        + Text("Sign", "4.5in", "", "<signature/>")
        + Text("Ghost", "5in", "<bind match=\"dataRef\" ref=\"$record.S.Name\"/>").Replace("<field name=\"Ghost\"", "<field name=\"Ghost\" presence=\"hidden\"")
        + Text("Total", "5.5in", "<calculate><script contentType=\"application/x-formcalc\">1+1</script></calculate>")
        + "</subform>",
        layout: "position");

    private const string KindsData =
        "<S><Name>Ada Lovelace</Name><Notes>line one</Notes><Code>AB12</Code><Secret>hunter2</Secret>"
        + "<Agree>Y</Agree><Country>FR</Country><Sex>F</Sex></S>";

    private static PdfDocument LaidOut(out XfaLayoutResult result, bool emitWidgets = true)
    {
        var document = PdfDocument.Open(XfaTestForms.BuildPdf(KindsTemplate, XfaTestForms.Data(KindsData)));
        result = document.ApplyXfaLayout(new XfaLayoutOptions { EmitWidgets = emitWidgets }, TestContext.Current.CancellationToken);
        result.Status.Should().Be(XfaLayoutStatus.LaidOut, result.FailureReason);
        return document;
    }

    private static PdfDictionary Field(PdfDocument document, string fullName)
    {
        var field = document.GetAcroForm()!.FindField(fullName);
        field.Should().NotBeNull(fullName);
        return field!.RawDictionary;
    }

    private static int Flags(PdfDictionary field) => (int)field.GetOptional("Ff")!.GetNumber();

    // ------------------------------------------------------------ K.2: a field per XFA field, SOM names

    [Fact]
    public void EveryMapEntry_HasAField_NamedByItsSomPath()
    {
        using var document = LaidOut(out var result);

        var expected = result.Fields
            .Where(f => !(f.GroupSomPath != null && result.Fields.Any(g => g.UiKind == "exclGroup" && g.SomPath == f.GroupSomPath)))
            .Select(f => f.SomPath)
            .ToList();
        var names = document.GetAcroForm()!.Fields.Select(f => f.FullName).Distinct().ToList();

        names.Should().BeEquivalentTo(expected, "K.2: one AcroForm field per XFA field, named by XFA-SOM");
        result.GeneratedFieldCount.Should().Be(expected.Count);
        expected.Should().Contain("form1[0].S[0].Sex[0]").And.NotContain(n => n.EndsWith(".M[0]", StringComparison.Ordinal),
            "exclusion-group members are the radio field's widgets, not fields");
    }

    [Fact]
    public void PartialNames_AreSomSegments_InAHierarchy()
    {
        using var document = LaidOut(out _);
        var name = Field(document, "form1[0].S[0].Name[0]");

        name.GetOptional("T").Should().BeOfType<PdfString>().Which.Value.Should().Be("Name[0]");
        var parent = (PdfDictionary)document.Resolve(name.GetOptional("Parent")!);
        parent.GetOptional("T").Should().BeOfType<PdfString>().Which.Value.Should().Be("S[0]");
        parent.ContainsKey("FT").Should().BeFalse("a subform is a non-terminal field");
    }

    // ------------------------------------------------------------ display only (#2028 correction)

    [Fact]
    public void EveryGeneratedField_IsReadOnly_EvenWhereTheMapSaysEditable()
    {
        using var document = LaidOut(out var result);
        result.Fields.Should().Contain(f => f.Editable, "fixture sanity: the map has editable fields");

        var fields = document.GetAcroForm()!.Fields;
        fields.Should().NotBeEmpty();
        fields.Should().OnlyContain(f => f.IsReadOnly,
            "S1 is display only: without the S2 write-back an edit to /V would leave the datasets behind (K.2)");
        var name = fields.First(f => f.FullName == "form1[0].S[0].Name[0]");
        var edit = () => name.SetValue("changed");
        edit.Should().Throw<InvalidOperationException>();
    }

    // ------------------------------------------------------------ kinds (ISO 32000-2 §12.7.5)

    [Fact]
    public void Text_HasTheFormValue_AndTheTemplateLimits()
    {
        using var document = LaidOut(out _);

        var name = Field(document, "form1[0].S[0].Name[0]");
        name.GetNameOrNull("FT").Should().Be("Tx");
        ((PdfString)name.GetOptional("V")!).Value.Should().Be("Ada Lovelace");
        ((PdfString)name.GetOptional("DA")!).Value.Should().Be("/Helv 0 Tf 0 g");

        var notes = Field(document, "form1[0].S[0].Notes[0]");
        (Flags(notes) & (1 << 12)).Should().NotBe(0, "Multiline");
        notes.GetOptional("MaxLen")!.GetNumber().Should().Be(40);

        var code = Field(document, "form1[0].S[0].Code[0]");
        (Flags(code) & (1 << 24)).Should().NotBe(0, "Comb");
        code.GetOptional("MaxLen")!.GetNumber().Should().Be(4);
    }

    [Fact]
    public void Password_HasThePasswordFlag_AndNoStoredValue()
    {
        using var document = LaidOut(out _);
        var secret = Field(document, "form1[0].S[0].Secret[0]");

        (Flags(secret) & (1 << 13)).Should().NotBe(0);
        secret.ContainsKey("V").Should().BeFalse("ISO 32000-2 Table 231: a password field's value is never stored");
    }

    [Fact]
    public void CheckButton_StatesAreNamedByTheOnValue()
    {
        using var document = LaidOut(out _);
        var agree = Field(document, "form1[0].S[0].Agree[0]");

        agree.GetNameOrNull("FT").Should().Be("Btn");
        agree.GetNameOrNull("V").Should().Be("Y");
        agree.GetNameOrNull("AS").Should().Be("Y");
        var normal = (PdfDictionary)document.Resolve(((PdfDictionary)document.Resolve(agree.GetOptional("AP")!)).GetOptional("N")!);
        normal.Keys.Select(k => k.Value).Should().BeEquivalentTo(new[] { "Y", "Off" });
    }

    [Fact]
    public void ExclGroup_IsARadioField_WithOneWidgetPerMember()
    {
        using var document = LaidOut(out _);
        var sex = Field(document, "form1[0].S[0].Sex[0]");

        (Flags(sex) & (1 << 15)).Should().NotBe(0, "Radio");
        sex.GetNameOrNull("V").Should().Be("F");
        var kids = ((PdfArray)sex.GetOptional("Kids")!).Select(k => (PdfDictionary)document.Resolve(k)).ToList();
        kids.Should().HaveCount(2);
        kids.Select(k => k.GetNameOrNull("AS")).Should().Equal("Off", "F");
        kids.Should().OnlyContain(k => !k.ContainsKey("T"), "the kids are widgets, not fields");
    }

    [Fact]
    public void ChoiceList_HasSaveDisplayPairs_AndTheSavedValue()
    {
        using var document = LaidOut(out _);
        var country = Field(document, "form1[0].S[0].Country[0]");

        country.GetNameOrNull("FT").Should().Be("Ch");
        (Flags(country) & (1 << 17)).Should().NotBe(0, "a drop-down is a combo box");
        ((PdfString)country.GetOptional("V")!).Value.Should().Be("FR");
        var opt = ((PdfArray)country.GetOptional("Opt")!).Cast<PdfArray>()
            .Select(p => (((PdfString)p[0]).Value, ((PdfString)p[1]).Value)).ToList();
        opt.Should().Equal(("CA", "Canada"), ("FR", "France"));
    }

    [Fact]
    public void ButtonAndSignature_AreAPushButtonAndAnUnsignedSignatureField()
    {
        using var document = LaidOut(out _);

        var go = Field(document, "form1[0].S[0].Go[0]");
        go.GetNameOrNull("FT").Should().Be("Btn");
        (Flags(go) & (1 << 16)).Should().NotBe(0, "Pushbutton");

        var sign = Field(document, "form1[0].S[0].Sign[0]");
        sign.GetNameOrNull("FT").Should().Be("Sig");
        sign.ContainsKey("V").Should().BeFalse("unsigned");
    }

    [Fact]
    public void NoWidget_HasAnActionOrAdditionalActions_AndNeedAppearancesIsNeverSet()
    {
        using var document = LaidOut(out _);

        foreach (var page in document.Pages)
        {
            foreach (var widget in XfaWidgetWriter.GeneratedWidgets(document, page))
            {
                widget.ContainsKey("A").Should().BeFalse("K.2 rule 3");
                widget.ContainsKey("AA").Should().BeFalse("K.2 rule 3");
            }
        }
        var acroForm = (PdfDictionary)document.Resolve(document.Catalog.GetOptional("AcroForm")!);
        acroForm.ContainsKey("NeedAppearances").Should().BeFalse();
        acroForm.ContainsKey("XFA").Should().BeTrue("decision 3");
        document.Catalog.ContainsKey("NeedsRendering").Should().BeTrue("decision 3");
        var dr = (PdfDictionary)document.Resolve(acroForm.GetOptional("DR")!);
        ((PdfDictionary)document.Resolve(dr.GetOptional("Font")!)).ContainsKey("Helv").Should().BeTrue("the /DA names /Helv");
    }

    // ------------------------------------------------------------ hidden fields and the record

    [Fact]
    public void AHiddenField_HasOneHiddenZeroSizeWidgetOnTheFirstPage()
    {
        using var document = LaidOut(out _);
        var ghost = Field(document, "form1[0].S[0].Ghost[0]");

        ((PdfString)ghost.GetOptional("V")!).Value.Should().Be("Ada Lovelace", "K.2: /V is the XFA value, hidden or not");
        ghost.GetOptional("F")!.GetNumber().Should().Be(2, "Hidden");
        ((PdfArray)ghost.GetOptional("Rect")!).Select(n => n.GetNumber()).Should().Equal(0, 0, 0, 0);
        ghost.ContainsKey("AP").Should().BeFalse();
        XfaWidgetWriter.GeneratedWidgets(document, document.Pages[0]).Should().Contain(ghost);
    }

    [Fact]
    public void EachPage_RecordsItsWidgets_Ordinal_Engine_AndDatasetsHash()
    {
        using var document = LaidOut(out _);
        var record = XfaWidgetWriter.Record(document, document.Pages[0])!;

        record.GetOptional("XfaLayout").Should().Be(PdfBoolean.True, "decision 4 marker");
        ((PdfArray)record.GetOptional(XfaWidgetWriter.WidgetsKey)!).Count.Should().BeGreaterThan(5);
        record.GetOptional(XfaWidgetWriter.PageOrdinalKey)!.GetNumber().Should().Be(0);
        record.GetOptional(XfaWidgetWriter.EngineKey)!.GetNumber().Should().Be(XfaWidgetWriter.LayoutEngineVersion);
        ((PdfString)record.GetOptional(XfaWidgetWriter.DatasetsHashKey)!).Value.Should().MatchRegex("^sha256:[0-9a-f]{64}$");
    }

    // ------------------------------------------------------------ values move out of the page

    [Fact]
    public void TheValue_IsDrawnByTheWidget_NotByThePage()
    {
        using var withWidgets = LaidOut(out _);
        using var phase2 = LaidOut(out _, emitWidgets: false);

        Encoding.Latin1.GetString(phase2.Pages[0].GetContentStreamBytes()).Should().Contain("(Ada Lovelace)",
            "fixture sanity: Phase 2 draws the value on the page");
        Encoding.Latin1.GetString(withWidgets.Pages[0].GetContentStreamBytes()).Should().NotContain("Ada Lovelace",
            "decision 10: the page keeps borders, captions and chrome; the value is the widget's");
        phase2.GetAcroForm()!.Fields.Should().BeEmpty("EmitWidgets = false is the Phase 2 rendition");

        var name = Field(withWidgets, "form1[0].S[0].Name[0]");
        var ap = (PdfStream)withWidgets.Resolve(((PdfDictionary)withWidgets.Resolve(name.GetOptional("AP")!)).GetOptional("N")!);
        Encoding.Latin1.GetString(ap.DecodedData).Should().Contain("(Ada Lovelace)");
        ((PdfArray)ap.GetOptional("BBox")!).Select(n => n.GetNumber())
            .Should().Equal(((PdfArray)name.GetOptional("Rect")!).Select(n => n.GetNumber()),
                "BBox = Rect makes the §12.5.5 mapping the identity: the widget draws where the page did");
    }

    // ------------------------------------------------------------ decision 17: flatten before redaction

    [Fact]
    public void Flatten_StampsShownWidgets_AndRemovesEveryGeneratedWidgetAndField()
    {
        using var document = LaidOut(out _);

        var row = PdfXfaLayout.FlattenGeneratedXfaFields(document);

        row.Should().StartWith("generated XFA fields flattened");
        document.RedactionLedger.XfaRemovals.Should().Contain(row);
        document.GetAcroForm()!.Fields.Should().BeEmpty();
        foreach (var page in document.Pages)
        {
            XfaWidgetWriter.GeneratedWidgets(document, page).Should().BeEmpty();
            (document.Resolve(page.Dictionary.GetOptional("Annots") ?? PdfNull.Instance) as PdfArray ?? new PdfArray())
                .Should().BeEmpty();
        }
        Encoding.Latin1.GetString(document.Pages[0].GetContentStreamBytes()).Should().Contain(" Do",
            "the shown appearances are stamped into the page");
        ((PdfDictionary)document.Resolve(document.Catalog.GetOptional("AcroForm")!)).ContainsKey("XFA")
            .Should().BeTrue("decision 5 is the redaction's own step, not the flatten's");
        PdfXfaLayout.FlattenGeneratedXfaFields(document).Should().BeNull("a second call finds nothing");
    }
}
