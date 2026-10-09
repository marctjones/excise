using System.Xml.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Xfa;
using Excise.TestSupport;

namespace Excise.Core.Tests.Xfa;

/// <summary>
/// #2027 (XFA Phase 3 S0): the field map <see cref="XfaLayoutResult.Fields"/>. Each expectation is
/// written from the XFA 3.3 specification page cited, not from what the code produced. Where the
/// boxes land against an independent layout engine is checked in Excise.Rendering.Tests
/// (XfaFieldMapPdfJsOracleTests, pdf.js).
/// </summary>
public class XfaFieldMapTests
{
    private const string DatasetsNs = "http://www.xfa.org/schema/xfa-data/1.0/";

    private static XfaLayoutResult LayOut(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        var result = document.ApplyXfaLayout(cancellationToken: TestContext.Current.CancellationToken);
        result.Status.Should().Be(XfaLayoutStatus.LaidOut, result.FailureReason);
        return result;
    }

    private static XfaLayoutResult LayOut(string body, string? data = null, string layout = "tb")
        => LayOut(XfaTestForms.BuildPdf(XfaTestForms.Template(body, layout), data));

    /// <summary>An XDP whose datasets packet holds <paramref name="datasetsBody"/> verbatim (LOV lists, signatures).</summary>
    private static XfaLayoutResult LayOutXdp(string body, string datasetsBody, string extraPackets = "")
        => LayOut(XfaTestForms.BuildPdfFromXdp(
            "<xdp:xdp xmlns:xdp=\"http://ns.adobe.com/xdp/\">"
            + XfaTestForms.Template(body)
            + $"<xfa:datasets xmlns:xfa=\"{DatasetsNs}\">{datasetsBody}</xfa:datasets>"
            + extraPackets
            + "</xdp:xdp>"));

    private static XfaFieldInfo Single(XfaLayoutResult result, string somPath)
    {
        var matches = result.Fields.Where(f => f.SomPath == somPath).ToList();
        matches.Should().HaveCount(1, $"one entry for {somPath}; map has: {string.Join(", ", result.Fields.Select(f => f.SomPath))}");
        return matches[0];
    }

    private static string Text(string name, string extra = "") =>
        $"<field name=\"{name}\" w=\"2in\" h=\"0.3in\"><ui><textEdit/></ui>{extra}</field>";

    // ------------------------------------------------------------ SOM names (p72-74, p95-96, p849; #2035)

    [Fact]
    public void SomPaths_IndexSameNamedSiblings_AndLookThroughTransparentObjects()
    {
        var result = LayOut(
            "<subform name=\"Page1\" layout=\"tb\">" + Text("A") + Text("A") + "</subform>"
            + "<subform layout=\"tb\">" + Text("B") + "</subform>"
            + "<subform layout=\"tb\">" + Text("C") + "</subform>"
            + "<subform name=\"Wrapper\" scope=\"none\" layout=\"tb\">" + Text("D") + "</subform>"
            + "<subform name=\"Row\" layout=\"tb\"><occur min=\"2\" max=\"2\"/>" + Text("E") + "</subform>");

        result.Fields.Select(f => f.SomPath).Should().Equal(
            "form1[0].Page1[0].A[0]",
            "form1[0].Page1[0].A[1]",
            "form1[0].B[0]",          // p95: a nameless subform is transparent; its children are the parent's
            "form1[0].C[0]",
            "form1[0].D[0]",          // scope="none" takes no part in SOM names (p849)
            "form1[0].Row[0].E[0]",
            "form1[0].Row[1].E[0]");
    }

    [Fact]
    public void SomPaths_CountSameNamedObjectsAcrossTransparentWrappers()
    {
        // p94-95, Example 3.12: Description in the first and second nameless detail subforms is
        // Receipt.Description[0] and Receipt.Description[1]; the three Total_Price fields are siblings.
        var detail = "<subform layout=\"tb\">" + Text("Description") + Text("Total_Price") + "</subform>";
        var result = LayOut("<subform name=\"Receipt\" layout=\"tb\">" + detail + detail + Text("Total_Price") + "</subform>");

        result.Fields.Select(f => f.SomPath).Should().Equal(
            "form1[0].Receipt[0].Description[0]",
            "form1[0].Receipt[0].Total_Price[0]",
            "form1[0].Receipt[0].Description[1]",
            "form1[0].Receipt[0].Total_Price[1]",
            "form1[0].Receipt[0].Total_Price[2]");
    }

    [Fact]
    public void SomPaths_AnAreaIsTransparentEvenWhenNamed()
    {
        // p95 "Area Objects Are Always Transparent" (IMM 1295e has a named area).
        var result = LayOut("<subform name=\"S\" layout=\"position\"><area name=\"Box\">" + Text("Inside") + "</area>"
            + Text("Outside") + "</subform>");

        result.Fields.Select(f => f.SomPath).Should().Equal("form1[0].S[0].Inside[0]", "form1[0].S[0].Outside[0]");
    }

    [Fact]
    public void SomPaths_ANamelessField_IsWrittenByClass_IndexedAmongAllFieldSiblings()
    {
        // p96 note and p119-120: the #class index counts every true sibling of the class, named or not.
        // A nameless field inside a nameless subform needs that subform written by class too.
        var result = LayOut(
            "<subform name=\"S\" layout=\"tb\">" + Text("Named")
            + "<field w=\"1in\" h=\"0.3in\"><ui><textEdit/></ui></field>"
            + "<subform name=\"Inner\" layout=\"tb\">" + Text("X") + "</subform>"
            + "<subform layout=\"tb\"><field w=\"1in\" h=\"0.3in\"><ui><textEdit/></ui></field></subform>"
            + "</subform>");

        result.Fields.Select(f => f.SomPath).Should().Equal(
            "form1[0].S[0].Named[0]",
            "form1[0].S[0].#field[1]",
            "form1[0].S[0].Inner[0].X[0]",
            "form1[0].S[0].#subform[1].#field[0]");
    }

    [Fact]
    public void SomPaths_ANameWithADot_IsWrittenByClass_SoNoPartialNameHoldsADot()
    {
        // p75: an XFA name is an XML name, which may contain '.'. An AcroForm partial name may not
        // (ISO 32000-2 §12.7.4.2), so such an object is reached by class (p96).
        var result = LayOut("<subform name=\"a.b\" layout=\"tb\">" + Text("c.d") + Text("e") + "</subform>");

        result.Fields.Select(f => f.SomPath).Should().Equal(
            "form1[0].#subform[0].#field[0]",
            "form1[0].#subform[0].e[0]");
    }

    [Fact]
    public void Resolver_AcceptsTheClassSyntax_AndRefusesANameTheTwoClassCountsReadDifferently()
    {
        // Address (named) then a nameless subform holding Code: p96 makes the nameless one #subform[1];
        // excise's pre-#2035 count (nameless siblings only) made it #subform[0].
        var body = "<subform name=\"P\" layout=\"tb\">"
            + "<subform name=\"Address\" layout=\"tb\">" + Text("City") + "</subform>"
            + "<subform layout=\"tb\">" + Text("Code") + "</subform>"
            + "<subform layout=\"tb\">" + Text("Zip") + "</subform>"
            + "</subform>";
        var root = Merge(body);

        XfaFormSom.ResolveChain(root, "form1[0].P[0].Code[0]")![^1].Element.Attr("name").Should().Be("Code", "normal syntax, p95");
        XfaFormSom.ResolveChain(root, "form.form1[0].P[0].Code[0]")![^1].Element.Attr("name").Should().Be("Code", "leading form., p72");
        XfaFormSom.ResolveChain(root, "form1[0].P[0].#subform[1].Code[0]")![^1].Element.Attr("name").Should().Be("Code", "class syntax, p96");
        XfaFormSom.ResolveChain(root, "form1[0].P[0].#subform[0].Code[0]").Should().BeNull("#subform[0] is Address (p96)");

        // A name only the old count resolves is still read (a static form's /T from another producer).
        XfaFormSom.ResolveChainLenient(root, "form1[0].P[0].#subform[0].Code[0]", out var ambiguous)![^1]
            .Element.Attr("name").Should().Be("Code");
        ambiguous.Should().BeFalse();

        // #subform[1] is the Code subform to the spec and the Zip subform to the old count: refused.
        XfaFormSom.ResolveChainLenient(root, "form1[0].P[0].#subform[1]", out ambiguous).Should().BeNull();
        ambiguous.Should().BeTrue("the two class counts name different subforms; writing either could fill the wrong field");
    }

    [Theory]
    [InlineData("<button/>", false)]
    [InlineData("<barcode type=\"code128\"/>", false)]
    [InlineData("<signature/>", false)]
    [InlineData("<imageEdit/>", false)]
    [InlineData("<textEdit/>", true)]
    public void Editable_IsGatedByTheWidgetKind(string widget, bool editable)
    {
        // #2035 item 5: buttons, barcodes, signatures and image fields take no value from the user here.
        var result = LayOut($"<field name=\"W\" w=\"2in\" h=\"0.3in\"><ui>{widget}</ui></field>",
            XfaTestForms.Data("<W>v</W>"));

        Single(result, "form1[0].W[0]").Editable.Should().Be(editable);
    }

    private static XfaFormNode Merge(string body, string? data = null)
    {
        var budget = new XfaBudget(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var report = new XfaReport();
        return new XfaMerge(budget, report, data == null ? null : XElement.Parse(data))
            .Merge(new XfaTemplate(XElement.Parse(XfaTestForms.Template(body)), budget, report).Root);
    }

    [Fact]
    public void SomPaths_ResolveBackToTheSameObject()
    {
        var body =
            "<subform name=\"P\" layout=\"tb\">" + Text("A") + Text("A")
            + "<subform layout=\"tb\">" + Text("B") + "</subform>"
            + "<exclGroup name=\"G\"><field name=\"X\"><ui><checkButton/></ui><items><text>1</text></items></field></exclGroup>"
            + "</subform>";
        var result = LayOut(body, XfaTestForms.Data("<P><A>first</A><A>second</A><B>bee</B></P>"));

        // An independent merge of the same template and data, resolved by the static write-back's
        // SOM resolver: every name must land on an object of the same kind holding the same value.
        var budget = new XfaBudget(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var report = new XfaReport();
        var root = new XfaMerge(budget, report, XElement.Parse(XfaTestForms.Data("<P><A>first</A><A>second</A><B>bee</B></P>")))
            .Merge(new XfaTemplate(XElement.Parse(XfaTestForms.Template(body)), budget, report).Root);

        result.Fields.Should().NotBeEmpty();
        foreach (var field in result.Fields)
        {
            var chain = XfaFormSom.ResolveChain(root, field.SomPath);
            chain.Should().NotBeNull(field.SomPath);
            chain![^1].Kind.Should().Be(field.Node.Kind, field.SomPath);
            chain[^1].Element.Attr("name").Should().Be(field.Name, field.SomPath);
            chain[^1].Value.Should().Be(field.Node.Value, field.SomPath);
        }
        Single(result, "form1[0].P[0].A[1]").Value.Should().Be("second");
        Single(result, "form1[0].P[0].B[0]").Value.Should().Be("bee");
    }

    // ------------------------------------------------------------ geometry

    [Fact]
    public void Rect_IsTheLayoutBoxInContentPoints()
    {
        // FullName: x=1in y=1in from a content area at (0.25in, 0.25in), 4in x 0.4in, on US Letter.
        var result = LayOut(XfaTestForms.BuildPdf(XfaTestForms.PositionedTemplate()));

        var field = Single(result, "form1[0].FullName[0]");
        field.PageIndex.Should().Be(0);
        field.Rect.Should().NotBeNull();
        var rect = field.Rect!.Value;
        rect.Space.Should().Be(PdfCoordinateSpace.ContentPoints);
        rect.PageNumber.Should().Be(1);
        rect.X.Should().BeApproximately(90, 0.01);
        rect.Width.Should().BeApproximately(288, 0.01);
        rect.Height.Should().BeApproximately(28.8, 0.01);
        // pdfY of the bottom edge: 792 - (18 + 72) - 28.8.
        rect.Y.Should().BeApproximately(673.2, 0.01);
    }

    [Fact]
    public void HiddenField_IsInTheMap_WithNoPageOrRect()
    {
        var result = LayOut(Text("Shown") + "<field name=\"Gone\" presence=\"hidden\" w=\"2in\" h=\"0.3in\"><ui><textEdit/></ui></field>");

        var gone = Single(result, "form1[0].Gone[0]");
        gone.PageIndex.Should().Be(-1);
        gone.Rect.Should().BeNull();
        gone.Presence.Should().Be("hidden");
        Single(result, "form1[0].Shown[0]").PageIndex.Should().Be(0);
    }

    [Fact]
    public void PageAreaField_HasOneEntryPerPage_AndIsNotEditable()
    {
        var pageSet = "<pageSet><pageArea name=\"Page1\">"
            + "<contentArea x=\"0.25in\" y=\"1in\" w=\"8in\" h=\"9.75in\"/>"
            + "<medium stock=\"letter\" short=\"8.5in\" long=\"11in\"/>"
            + "<field name=\"Header\" x=\"0.25in\" y=\"0.25in\" w=\"3in\" h=\"0.3in\"><ui><textEdit/></ui></field>"
            + "</pageArea></pageSet>";
        var tall = "<field name=\"Tall\" w=\"2in\" h=\"6in\"><ui><textEdit/></ui></field>";
        var result = LayOut(XfaTestForms.BuildPdf(XfaTestForms.Template(tall + tall, pageSet: pageSet)));

        var headers = result.Fields.Where(f => f.Name == "Header").ToList();
        headers.Select(h => h.SomPath).Should().Equal("form1[0].#pageSet[0].Page1[0].Header[0]", "form1[0].#pageSet[0].Page1[1].Header[0]");
        headers.Select(h => h.PageIndex).Should().Equal(0, 1);
        headers.Should().OnlyContain(h => h.InPageArea && !h.Editable);
    }

    // ------------------------------------------------------------ access (p727, p842)

    [Theory]
    [InlineData(null, null, "Open")]
    [InlineData("readOnly", null, "ReadOnly")]
    [InlineData("readOnly", "open", "ReadOnly")]               // content may only restrict further
    [InlineData("protected", "readOnly", "Protected")]
    [InlineData("readOnly", "nonInteractive", "NonInteractive")]
    [InlineData(null, "protected", "Protected")]
    public void Access_IsTheMostRestrictiveUpTheSubforms(string? subformAccess, string? fieldAccess, string expectedName)
    {
        var expected = Enum.Parse<XfaAccess>(expectedName);
        var sub = subformAccess == null ? string.Empty : $" access=\"{subformAccess}\"";
        var own = fieldAccess == null ? string.Empty : $" access=\"{fieldAccess}\"";
        var result = LayOut(
            $"<subform name=\"Outer\"{sub} layout=\"tb\"><subform name=\"Inner\" layout=\"tb\">"
            + $"<field name=\"F\"{own} w=\"2in\" h=\"0.3in\"><ui><textEdit/></ui></field></subform></subform>");

        var field = Single(result, "form1[0].Outer[0].Inner[0].F[0]");
        field.Access.Should().Be(expected);
        field.Editable.Should().Be(expected == XfaAccess.Open);
    }

    [Fact]
    public void Access_OfAnExclGroup_ReachesItsMembers()
    {
        var result = LayOut(
            "<exclGroup name=\"G\" access=\"readOnly\">"
            + "<field name=\"Y\"><ui><checkButton/></ui><items><text>Y</text></items></field>"
            + "<field name=\"N\"><ui><checkButton/></ui><items><text>N</text></items></field></exclGroup>");

        Single(result, "form1[0].G[0]").Access.Should().Be(XfaAccess.ReadOnly);
        Single(result, "form1[0].G[0].Y[0]").Access.Should().Be(XfaAccess.ReadOnly);
        Single(result, "form1[0].G[0].Y[0]").GroupSomPath.Should().Be("form1[0].G[0]");
    }

    // ------------------------------------------------------------ calculate (p641-642)

    [Theory]
    [InlineData("", false, "Disabled", true)]
    [InlineData("<calculate><script contentType=\"application/x-formcalc\">1</script></calculate>", true, "Error", false)]
    [InlineData("<calculate/>", true, "Error", false)]   // the element decides, not the script
    [InlineData("<calculate override=\"ignore\"/>", true, "Ignore", false)]
    [InlineData("<calculate override=\"warning\"/>", true, "Warning", true)]
    [InlineData("<calculate override=\"disabled\"/>", true, "Disabled", true)]
    public void Calculate_DefaultsToError_WhenTheElementIsPresent(string calculate, bool has, string expectedName, bool editable)
    {
        var expected = Enum.Parse<XfaCalculateOverride>(expectedName);
        var result = LayOut(Text("F", calculate), XfaTestForms.Data("<F/>"));

        var field = Single(result, "form1[0].F[0]");
        field.HasCalculate.Should().Be(has);
        field.CalculateOverride.Should().Be(expected);
        field.Editable.Should().Be(editable);
    }

    // ------------------------------------------------------------ bindItems (p210-212, p624) and items (p758-760)

    private const string CountryField =
        "<field name=\"Country\" w=\"2in\" h=\"0.3in\"><ui><choiceList/></ui>"
        + "<items><text>template only</text></items>"
        + "<bindItems ref=\"xfa.datasets.LOVFile.LOV.CountryList.Country[*]\" labelRef=\"$\" valueRef=\"lic\"/>"
        + "</field>";

    private const string CountryLov =
        "<LOVFile><LOV><CountryList>"
        + "<Country lic=\"\"/><Country lic=\"FR\">France</Country><Country lic=\"DE\">Germany</Country>"
        + "</CountryList></LOV></LOVFile>";

    [Fact]
    public void BindItems_FromDatasetsOutsideTheData_ReplaceTheTemplateItems()
    {
        var result = LayOutXdp(CountryField, CountryLov + "<xfa:data><form1><Country>DE</Country></form1></xfa:data>");

        var field = Single(result, "form1[0].Country[0]");
        field.ItemsFromData.Should().BeTrue();
        field.Items.Should().Equal(new XfaItem("", ""), new XfaItem("France", "FR"), new XfaItem("Germany", "DE"));
        field.Value.Should().Be("DE", "the field stores the save column (p758, p760)");
    }

    [Fact]
    public void BindItems_RefRelativeToTheBoundDataNode()
    {
        const string field =
            "<field name=\"Pick\" w=\"2in\" h=\"0.3in\"><ui><choiceList/></ui>"
            + "<bindItems ref=\"$.opt[*]\" labelRef=\"label\" valueRef=\"code\"/></field>";
        var result = LayOut(field,
            XfaTestForms.Data("<Pick><opt><label>One</label><code>1</code></opt><opt><label>Two</label><code>2</code></opt></Pick>"));

        Single(result, "form1[0].Pick[0]").Items.Should().Equal(new XfaItem("One", "1"), new XfaItem("Two", "2"));
    }

    [Fact]
    public void BindItems_WithAConnection_IsNotResolved_AndIsReported()
    {
        const string field =
            "<field name=\"Pick\" w=\"2in\" h=\"0.3in\"><ui><choiceList/></ui><items><text>kept</text></items>"
            + "<bindItems connection=\"svc\" ref=\"!LOVFile.LOV.CountryList.Country[*]\" valueRef=\"lic\"/></field>";
        var result = LayOutXdp(field, CountryLov);

        var pick = Single(result, "form1[0].Pick[0]");
        pick.ItemsFromData.Should().BeFalse("a web-service connection is never contacted");
        pick.Items.Should().Equal(new XfaItem("kept", "kept"));
        result.Omissions.Should().Contain(o => o.Contains("connection", StringComparison.Ordinal));
    }

    [Fact]
    public void TemplateItems_TwoColumns_DisplayAndSave()
    {
        var result = LayOut(
            "<field name=\"C\" w=\"2in\" h=\"0.3in\"><ui><choiceList/></ui>"
            + "<items><text>France</text><text>Germany</text></items>"
            + "<items save=\"1\"><text>FR</text><text>DE</text></items></field>");

        Single(result, "form1[0].C[0]").Items.Should().Equal(new XfaItem("France", "FR"), new XfaItem("Germany", "DE"));
    }

    [Fact]
    public void MultiSelect_BindsADataGroup_AndJoinsItsValuesWithNewlines()
    {
        var result = LayOut(
            "<field name=\"grains\" w=\"2in\" h=\"1in\"><ui><choiceList open=\"multiSelect\"/></ui>"
            + "<items save=\"1\"><text>wheat</text><text>rye</text><text>millet</text></items></field>",
            XfaTestForms.Data("<grains><value>rye</value><value>barley</value></grains>"));

        var field = Single(result, "form1[0].grains[0]");
        field.MultiSelect.Should().BeTrue();
        field.Value.Should().Be("rye\nbarley", "p198: the values of all the group's children, newline-separated");
    }

    // ------------------------------------------------------------ check buttons (p759) and exclusion groups (p196-197)

    [Fact]
    public void CheckButton_OnOffNeutral()
    {
        var result = LayOut(
            "<field name=\"Three\"><ui><checkButton/></ui><items><text>Y</text><text>N</text><text>?</text></items></field>"
            + "<field name=\"One\"><ui><checkButton/></ui><items><text>on</text></items></field>");

        var three = Single(result, "form1[0].Three[0]");
        (three.OnValue, three.OffValue, three.NeutralValue).Should().Be(("Y", "N", "?"));
        var one = Single(result, "form1[0].One[0]");
        (one.OnValue, one.OffValue, one.NeutralValue).Should().Be(("on", "", ""), "p759: missing values are the null string");
    }

    private const string SexGroup =
        "<exclGroup name=\"Sex\">"
        + "<field name=\"Male\"><ui><checkButton/></ui><items><text>M</text></items></field>"
        + "<field name=\"Female\"><ui><checkButton/></ui><items><text>F</text></items></field>"
        + "</exclGroup>";

    [Fact]
    public void ExclGroup_ShortFormat_TheGroupHoldsTheValue()
    {
        var result = LayOut(SexGroup, XfaTestForms.Data("<Sex>F</Sex>"));

        var group = Single(result, "form1[0].Sex[0]");
        group.GroupFormat.Should().Be(XfaExclGroupFormat.Short);
        group.BoundData!.Name.LocalName.Should().Be("Sex");
        group.Value.Should().Be("F");
        Single(result, "form1[0].Sex[0].Male[0]").BoundData.Should().BeNull("p196: in short format the members are left unbound");
    }

    [Fact]
    public void ExclGroup_LongFormat_EachMemberHoldsItsValue()
    {
        var result = LayOut(SexGroup, XfaTestForms.Data("<Sex><Male>M</Male><Female></Female></Sex>"));

        var group = Single(result, "form1[0].Sex[0]");
        group.GroupFormat.Should().Be(XfaExclGroupFormat.Long);
        group.Value.Should().Be("M");
        var male = Single(result, "form1[0].Sex[0].Male[0]");
        male.BoundData!.Value.Should().Be("M");
        male.Value.Should().Be("M");
        Single(result, "form1[0].Sex[0].Female[0]").BoundData!.Value.Should().Be(string.Empty);
    }

    // ------------------------------------------------------------ text-edit properties

    [Fact]
    public void TextEdit_MaxCharsMultilineAndComb()
    {
        var result = LayOut(
            "<field name=\"Code\" w=\"2in\" h=\"0.3in\"><ui><textEdit><comb/></textEdit></ui><value><text maxChars=\"6\"/></value></field>"
            + "<field name=\"Notes\" w=\"2in\" h=\"1in\"><ui><textEdit multiLine=\"1\"/></ui></field>");

        var code = Single(result, "form1[0].Code[0]");
        code.MaxChars.Should().Be(6);
        code.CombCells.Should().Be(6, "a comb without numberOfCells has maxChars cells");
        code.Multiline.Should().BeFalse();
        Single(result, "form1[0].Notes[0]").Multiline.Should().BeTrue();
    }

    // ------------------------------------------------------------ binding and the Editable rule

    [Fact]
    public void Binding_Kinds_AndWhereAMissingNodeWouldBeCreated()
    {
        var result = LayOut(
            "<subform name=\"S\" layout=\"tb\">"
            + Text("Normal")
            + Text("Unbound", "<bind match=\"none\"/>")
            + Text("Global", "<bind match=\"global\"/>")
            + Text("Ref", "<bind match=\"dataRef\" ref=\"$record.Other.Value\"/>")
            + "<field w=\"2in\" h=\"0.3in\"><ui><textEdit/></ui></field>"
            + "</subform>",
            XfaTestForms.Data("<S/>"));

        var normal = Single(result, "form1[0].S[0].Normal[0]");
        (normal.Binding, normal.BoundData, normal.CreatablePath).Should().Be((XfaBindingKind.Normal, null, "$record.S[0].Normal[0]"));
        normal.Editable.Should().BeTrue();

        var unbound = Single(result, "form1[0].S[0].Unbound[0]");
        unbound.Binding.Should().Be(XfaBindingKind.None);
        unbound.Editable.Should().BeFalse("p81, p176: an unbound field's value lives only in the Form DOM, which excise cannot save");

        Single(result, "form1[0].S[0].Global[0]").Binding.Should().Be(XfaBindingKind.Global);
        var byRef = Single(result, "form1[0].S[0].Ref[0]");
        (byRef.Binding, byRef.CreatablePath).Should().Be((XfaBindingKind.DataRef, "$record.Other.Value"));

        var nameless = Single(result, "form1[0].S[0].#field[4]");   // p96: among all five field siblings
        nameless.Binding.Should().Be(XfaBindingKind.None, "a nameless object does not correspond to data (p95)");
        nameless.Editable.Should().BeFalse();
    }

    [Fact]
    public void BoundField_ReportsItsDataNode()
    {
        var result = LayOut("<subform name=\"S\" layout=\"tb\">" + Text("Name") + "</subform>",
            XfaTestForms.Data("<S><Name>Ada</Name></S>"));

        var field = Single(result, "form1[0].S[0].Name[0]");
        field.BoundData!.Value.Should().Be("Ada");
        field.CreatablePath.Should().BeNull();
        field.Value.Should().Be("Ada");
    }

    [Fact]
    public void BindPicture_MakesTheFieldReadOnly()
    {
        var result = LayOut(Text("When", "<bind match=\"once\"><picture>date{YYYY-MM-DD}</picture></bind>") + Text("Plain"),
            XfaTestForms.Data("<When>2026-10-08</When><Plain/>"));

        var when = Single(result, "form1[0].When[0]");
        when.HasBindPicture.Should().BeTrue();
        when.Editable.Should().BeFalse("no bind picture is applied yet (#2019)");
        Single(result, "form1[0].Plain[0]").Editable.Should().BeTrue();
    }

    [Fact]
    public void XmlSignatureInTheDatasets_MakesEveryFieldReadOnly()
    {
        var result = LayOutXdp(Text("Name"),
            "<xfa:data><form1><Name>Ada</Name></form1></xfa:data>"
            + "<Signature xmlns=\"http://www.w3.org/2000/09/xmldsig#\"><SignedInfo/></Signature>");

        var field = Single(result, "form1[0].Name[0]");
        field.XmlSignature.Should().BeTrue();
        field.Editable.Should().BeFalse("p559: respecting the signed state means not changing signed data");
    }

    [Fact]
    public void SignaturePacket_MakesEveryFieldReadOnly()
    {
        var result = LayOutXdp(Text("Name"), "<xfa:data><form1><Name>Ada</Name></form1></xfa:data>",
            "<signature xmlns=\"http://www.w3.org/2000/09/xmldsig#\"/>");

        Single(result, "form1[0].Name[0]").Editable.Should().BeFalse("p1040: a detached XML signature packet");
    }

    [Fact]
    public void UnsignedForm_IsNotFlagged()
    {
        var result = LayOutXdp(Text("Name"), "<xfa:data><form1><Name>Ada</Name></form1></xfa:data>");

        var field = Single(result, "form1[0].Name[0]");
        field.XmlSignature.Should().BeFalse();
        field.Editable.Should().BeTrue();
    }
}
