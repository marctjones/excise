using System.Text;
using System.Xml.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Security;
using Excise.TestSupport;
using Xunit;
using F = Excise.TestSupport.XfaStaticFillFixtures;

namespace Excise.Core.Tests.Xfa;

/// <summary>
/// #2013 (slice S4 of #1547 Phase 3): setting a field value on a STATIC XFA
/// form writes the same value into the datasets packet, at the data node the
/// field binds to, so the two copies ISO 32000-2 Annex K.2 requires to agree
/// do agree. Values are read back from the SAVED bytes after a reopen; the
/// qpdf/mutool oracles live in Excise.Rendering.Tests
/// (XfaStaticFillOracleTests).
/// </summary>
public class XfaStaticDataSyncTests
{
    private static PdfDocument Open(byte[] pdf) => PdfDocument.Open(new MemoryStream(pdf));

    private static byte[] Fill(byte[] pdf, params (string Name, string? Value)[] values)
    {
        using var doc = Open(pdf);
        doc.DetectXfaForm().Should().Be(PdfXfaFormKind.Static, "fixture sanity");
        var form = doc.GetAcroForm()!;
        foreach (var (name, value) in values)
        {
            var field = form.FindField(name);
            field.Should().NotBeNull($"fixture sanity: field {name}");
            field!.SetValue(value);
        }
        return doc.SaveToBytes();
    }

    private static XElement Data(byte[] saved)
    {
        using var doc = Open(saved);
        return F.DataRoot(doc);
    }

    private static string? DataValue(byte[] saved, params string[] path)
    {
        XElement? node = Data(saved);
        foreach (var name in path)
            node = node?.Element(name);
        return node?.Value;
    }

    [Fact]
    public void Text_SpecialCharacters_WrittenToBoundNode_EscapedAndRoundTripped()
    {
        const string value = "Ann & <Bo> \"q\" 'x' ü 日本 \U0001F600";
        var saved = Fill(F.Build(), (F.NamePath, value));

        DataValue(saved, "Name").Should().Be(value);
        using var doc = Open(saved);
        doc.GetAcroForm()!.FindField(F.NamePath)!.Value.Should().Be(value, "/V and the datasets agree");
        Encoding.UTF8.GetString(F.Packets(doc)["datasets"]).Should().Contain("Ann &amp; &lt;Bo&gt;",
            "the value is XML-escaped in the packet, not spliced in raw");
    }

    [Fact]
    public void OnlyTheDatasetsPacketChanges_EveryOtherPacketIsByteIdentical()
    {
        var source = F.Build(formPacket: $"<form xmlns=\"{F.FormNamespace}\"/>");
        Dictionary<string, byte[]> before;
        using (var original = Open(source))
            before = F.Packets(original);

        var saved = Fill(source, (F.NamePath, "Changed"));
        using var doc = Open(saved);
        var after = F.Packets(doc);
        after.Keys.Should().BeEquivalentTo(before.Keys);
        foreach (var name in before.Keys.Where(k => k != "datasets"))
            after[name].Should().Equal(before[name], $"the {name} packet must not change");
        after["datasets"].Should().NotEqual(before["datasets"]);
    }

    [Fact]
    public void BindingShapes_TransparentSubform_NamedGroup_UnnamedSubform_Global()
    {
        var saved = Fill(F.Build(),
            (F.CityPath, "Paris"),
            (F.CodePath, "C-7"),
            (F.TotalPath, "42"));

        DataValue(saved, "Address", "City").Should().Be("Paris", "Address is a named subform bound to its data group");
        DataValue(saved, "Code").Should().Be("C-7", "an unnamed subform (#subform[0]) is transparent to data");
        DataValue(saved, "Address", "Total").Should().Be("42", "a global field binds to its node wherever it sits");
        Data(saved).Elements("Total").Should().BeEmpty("no second Total node may be created");
    }

    [Fact]
    public void CheckButton_TwoItems_OnAndOff()
    {
        var on = Fill(F.Build(), (F.AgreePath, "Y"));
        DataValue(on, "Agree").Should().Be("Y");

        var off = Fill(on, (F.AgreePath, "Off"));
        DataValue(off, "Agree").Should().Be("N");

        var cleared = Fill(on, (F.AgreePath, null));
        DataValue(cleared, "Agree").Should().Be("N", "a cleared check box takes its off value");
    }

    [Fact]
    public void CheckButton_OneItem_OffIsTheNullString_PerXfa33Page759()
    {
        var on = Fill(F.Build(), (F.SoloPath, "on"));
        DataValue(on, "Solo").Should().Be("on");

        var off = Fill(on, (F.SoloPath, "Off"));
        DataValue(off, "Solo").Should().Be(string.Empty,
            "an items list with no second value has the null string as its off value (XFA 3.3 p759); see #2016");
    }

    [Fact]
    public void ExclusionGroup_ShortFormat_SelectedMemberAndCleared()
    {
        var picked = Fill(F.Build(), (F.SexPath, "F"));
        DataValue(picked, "Sex").Should().Be("F");

        var cleared = Fill(picked, (F.SexPath, null));
        DataValue(cleared, "Sex").Should().Be(string.Empty);
    }

    /// <summary>
    /// Long exclusion format (XFA 3.3 p196-197): the group's data node holds one data value per
    /// member, and each member binds its own. Selecting a member writes its on value there and the
    /// other member's off value (the null string, p759) to the other; the group node keeps no text.
    /// </summary>
    [Fact]
    public void ExclusionGroup_LongFormat_EachMemberNodeTakesItsOnOrOffValue()
    {
        var saved = Fill(F.Build(sexData: "<Sex><M/><F/></Sex>"), (F.SexPath, "F"));

        DataValue(saved, "Sex", "M").Should().Be(string.Empty);
        DataValue(saved, "Sex", "F").Should().Be("F");
        Data(saved).Element("Sex")!.Elements().Select(e => e.Name.LocalName).Should().Equal("M", "F");

        var switched = Fill(saved, (F.SexPath, "M"));
        DataValue(switched, "Sex", "M").Should().Be("M");
        DataValue(switched, "Sex", "F").Should().Be(string.Empty);
    }

    [Fact]
    public void ChoiceList_DisplayTextMapsToSavedValue()
    {
        var saved = Fill(F.Build(), (F.CountryPath, "France"));
        DataValue(saved, "Country").Should().Be("FR", "the save=\"1\" column holds what the field stores (XFA 3.3 p760)");
    }

    [Fact]
    public void MatchNone_IsLeftAlone_AndMissingNodeIsCreatedInScope()
    {
        var saved = Fill(F.Build(),
            (F.UnboundPath, "ignored"),
            (F.MissingPath, "created"));

        DataValue(saved, "Unbound").Should().Be("KEEPUNBOUND", "match=\"none\" binds no data");
        DataValue(saved, "Missing").Should().Be("created",
            "a once-bound field with no data node gets one under its scope (Page1 is transparent, so form1)");
    }

    [Fact]
    public void Cleared_EmptiesTheNode_AndTheOldValueLeavesTheFile()
    {
        var saved = Fill(F.Build(), (F.NamePath, null));
        DataValue(saved, "Name").Should().Be(string.Empty);
        SavedPdfLeakScanner.FindTerm(saved, F.OriginalName).Should().BeEmpty(
            "the cleared value must not survive in /V, the datasets or anywhere else");
    }

    // -------------------------------------------------------- xsi:nil (#2020)

    /// <summary>The <c>nil</c> attributes left on a data node, in any XMLSchema-instance namespace.</summary>
    private static List<string> NilMarkers(XElement node)
        => node.Attributes()
            .Where(a => a.Name.LocalName == "nil" && a.Name.NamespaceName.EndsWith("XMLSchema-instance", StringComparison.Ordinal))
            .Select(a => a.Value)
            .ToList();

    /// <summary>
    /// XFA 3.3 p141: a value assigned to a field bound to a null data node propagates to the node,
    /// which "is no longer marked as null"; p151 (nullType empty, the default when no data
    /// description says otherwise, p150): a non-null value unloads as <c>&lt;title&gt;A Book&lt;/title&gt;</c>.
    /// Both namespaces count: real forms use the 2001 URI, the spec text names 2000/10 (p139).
    /// </summary>
    [Theory]
    [InlineData("http://www.w3.org/2001/XMLSchema-instance")]
    [InlineData("http://www.w3.org/2000/10/XMLSchema-instance")]
    public void NilNode_ValueWritten_LosesItsNullMarker(string xsiNamespace)
    {
        var saved = Fill(F.Build(codeData: F.NilCode("true", xsiNamespace: xsiNamespace)), (F.CodePath, "C-9"));

        var code = Data(saved).Element("Code")!;
        code.Value.Should().Be("C-9");
        NilMarkers(code).Should().BeEmpty("xsi:nil=\"true\" would make an XFA processor load the node as null (p140)");
    }

    /// <summary>
    /// A nil node may carry content, which the XFA Data DOM ignores (p140, note a). Writing that
    /// same text must still drop the marker: the old early return compared text only.
    /// </summary>
    [Fact]
    public void NilNodeWithContent_SameTextWritten_LosesItsNullMarker()
    {
        var saved = Fill(F.Build(codeData: F.NilCode("true", content: "C-9")), (F.CodePath, "C-9"));

        var code = Data(saved).Element("Code")!;
        code.Value.Should().Be("C-9");
        NilMarkers(code).Should().BeEmpty();
    }

    /// <summary>
    /// Cleared means null. p151 (nullType empty): a null value unloads as an empty element with no
    /// marker; an explicit <c>xsi:nil="false"</c> kept on it would load as a NON-null empty string
    /// (p139).
    /// </summary>
    [Theory]
    [InlineData("false")]
    [InlineData("true")]
    public void NilMarkedNode_Cleared_IsAnEmptyElementWithNoMarker(string nil)
    {
        var saved = Fill(F.Build(codeData: F.NilCode(nil, content: "OLDCODEVALUE")), (F.CodePath, null));

        var code = Data(saved).Element("Code")!;
        code.Value.Should().BeEmpty();
        NilMarkers(code).Should().BeEmpty();
        SavedPdfLeakScanner.FindTerm(saved, "OLDCODEVALUE").Should().BeEmpty(
            "the content a nil marker hid is still data in the file (p140 note a) and must go when cleared");
    }

    [Fact]
    public void StaleValue_AtoB_ANoLongerInTheFile()
    {
        var first = Fill(F.Build(), (F.NamePath, "ALPHAFILLEDVALUE"));
        SavedPdfLeakScanner.FindTerm(first, "ALPHAFILLEDVALUE").Should().NotBeEmpty("fixture sanity");

        var second = Fill(first, (F.NamePath, "BETAFILLEDVALUE"));
        SavedPdfLeakScanner.FindTerm(second, "ALPHAFILLEDVALUE").Should().BeEmpty(
            "a stale datasets copy of the old value was the defect (#2013)");
        SavedPdfLeakScanner.FindTerm(second, F.OriginalName).Should().BeEmpty();
        DataValue(second, "Name").Should().Be("BETAFILLEDVALUE");
    }

    [Fact]
    public void Newlines_AreStoredAsLineFeeds()
    {
        var saved = Fill(F.Build(), (F.NamePath, "a\r\nb\rc\nd"));
        DataValue(saved, "Name").Should().Be("a\nb\nc\nd",
            "XML normalizes CR on read (XML 1.0 2.11); the data uses LF");
    }

    [Fact]
    public void ValueXmlCannotCarry_IsRefused_BeforeTheFieldChanges()
    {
        using var doc = Open(F.Build());
        var field = doc.GetAcroForm()!.FindField(F.NamePath)!;
        var act = () => field.SetValue("bad\u0001value");
        act.Should().Throw<ArgumentException>();
        field.Value.Should().Be(F.OriginalName, "a refusal leaves /V as it was");
    }

    [Fact]
    public void UnknownSomName_IsReported_NotSilentlyDropped()
    {
        using var doc = Open(F.Build());
        doc.GetAcroForm()!.FindField(F.StrayPath)!.SetValue("x");
        doc.XfaStaticDataSync.Should().NotBeNull();
        doc.XfaStaticDataSync!.Notes.Should().ContainSingle(n => n.Contains(F.StrayPath));
    }

    [Fact]
    public void SingleStreamXdp_IsUpdated_AndANonEmptyFormPacketIsReset()
    {
        var formPacket = $"<form xmlns=\"{F.FormNamespace}\" checksum=\"abc\"><subform name=\"form1\">" +
                         $"<field name=\"Name\"><value><text>{F.OriginalName}</text></value></field></subform></form>";
        var saved = Fill(F.Build(F.Shape.SingleStream, formPacket), (F.NamePath, "Single"));

        DataValue(saved, "Name").Should().Be("Single");
        SavedPdfLeakScanner.FindTerm(saved, F.OriginalName).Should().BeEmpty(
            "the saved form state restated the old value; decision 8 resets it");
        using var doc = Open(saved);
        var xdp = XDocument.Parse(Encoding.UTF8.GetString(F.Packets(doc)["xdp"]));
        var form = xdp.Root!.Elements().Single(e => e.Name.LocalName == "form");
        form.HasElements.Should().BeFalse();
        form.Attribute("checksum").Should().BeNull();
    }

    [Fact]
    public void PacketArray_NonEmptyFormPacketIsReset_EmptyOneIsKept()
    {
        var saved = Fill(F.Build(formPacket: $"<form xmlns=\"{F.FormNamespace}\" checksum=\"abc\"/>"), (F.NamePath, "X"));
        using (var doc = Open(saved))
            Encoding.UTF8.GetString(F.Packets(doc)["form"]).Should().NotContain("checksum");

        var emptyForm = $"<form xmlns=\"{F.FormNamespace}\"\n/>";
        var kept = Fill(F.Build(formPacket: emptyForm), (F.NamePath, "X"));
        using var keptDoc = Open(kept);
        Encoding.UTF8.GetString(F.Packets(keptDoc)["form"]).Should().Be(emptyForm);
    }

    [Fact]
    public void NoDatasetsPacket_OneIsCreatedAfterTheTemplate()
    {
        var saved = Fill(F.Build(F.Shape.NoDatasets), (F.NamePath, "Fresh"), (F.CityPath, "Lyon"));
        using var doc = Open(saved);
        var acroForm = (Excise.Core.Primitives.PdfDictionary)doc.Resolve(doc.Catalog["AcroForm"]);
        var array = (Excise.Core.Primitives.PdfArray)doc.Resolve(acroForm["XFA"]);
        var names = Enumerable.Range(0, array.Count / 2)
            .Select(i => ((Excise.Core.Primitives.PdfString)doc.Resolve(array[2 * i])).Value).ToList();
        names.Should().Equal("preamble", "template", "datasets", "postamble");
        var data = F.DataRoot(F.Packets(doc)["datasets"]);
        data.Name.LocalName.Should().Be("form1");
        data.Element("Name")!.Value.Should().Be("Fresh");
        data.Element("Address")!.Element("City")!.Value.Should().Be("Lyon");
    }

    [Fact]
    public void SplitDatasetsPacket_IsRewrittenAsOneXdpStream()
    {
        var saved = Fill(F.Build(F.Shape.SplitDatasets), (F.NamePath, "Joined"));
        DataValue(saved, "Name").Should().Be("Joined");
        SavedPdfLeakScanner.FindTerm(saved, F.OriginalName).Should().BeEmpty();
    }

    [Fact]
    public void DynamicForm_IsNotTouched()
    {
        var source = F.Build(needsRendering: true);
        byte[] before;
        using (var original = Open(source))
        {
            original.DetectXfaForm().Should().Be(PdfXfaFormKind.Dynamic, "fixture sanity");
            before = F.Packets(original)["datasets"];
        }

        byte[] saved;
        using (var doc = Open(source))
        {
            doc.GetAcroForm()!.FindField(F.NamePath)!.SetValue("Dyn");
            doc.XfaStaticDataSync.Should().BeNull("dynamic XFA fill is out of scope for this slice");
            saved = doc.SaveToBytes();
        }
        using var reopened = Open(saved);
        F.Packets(reopened)["datasets"].Should().Equal(before);
    }

    [Fact]
    public void Encrypted_RoundTrip_DatasetsUpdatedAndStillEncrypted()
    {
        const string password = "xfa-fill";
        byte[] encrypted;
        using (var plain = Open(F.Build()))
        {
            encrypted = plain.SaveToBytes(new PdfEncryptionOptions
            {
                UserPassword = password,
                OwnerPassword = password,
                Algorithm = PdfEncryptionAlgorithm.Aes256,
            });
        }

        byte[] saved;
        using (var doc = PdfDocument.Open(encrypted, new PdfOpenOptions { UserPassword = password }))
        {
            doc.GetAcroForm()!.FindField(F.NamePath)!.SetValue("CIPHERFILLVALUE");
            saved = doc.SaveToBytes(doc.GetReEncryptionOptions(password));
        }

        SavedPdfLeakScanner.FindTerm(saved, "CIPHERFILLVALUE").Should().BeEmpty(
            "the datasets stream is encrypted like every other stream");
        using var reopened = PdfDocument.Open(saved, new PdfOpenOptions { UserPassword = password });
        reopened.IsEncrypted.Should().BeTrue();
        F.DataRoot(reopened).Element("Name")!.Value.Should().Be("CIPHERFILLVALUE");
    }

    // ------------------------------------------------------------ real IRS forms

    private static byte[]? Corpus(string name)
    {
        var path = TestRepoLayout.FindFile("test-pdfs", "smoke", name);
        Assert.SkipWhen(path == null, TestRepoLayout.AbsenceReason("smoke corpus", $"test-pdfs/smoke/{name}"));
        return File.ReadAllBytes(path!);
    }

    private const string W9 = "topmostSubform[0].Page1[0].";

    [Fact]
    public void IrsW9_Text_NestedGlobalGroup_AndSharedCheckBoxes()
    {
        var source = Corpus("irs-w9.pdf")!;
        Dictionary<string, byte[]> before;
        using (var original = Open(source))
            before = F.Packets(original);

        var saved = Fill(source,
            (W9 + "f1_01[0]", "Jane Q. Public & Sons <LLC>"),
            (W9 + "Boxes3a-b_ReadOrder[0].f1_03[0]", "LLC-code"),
            (W9 + "Boxes3a-b_ReadOrder[0].c1_1[2]", "3"),
            (W9 + "Boxes3a-b_ReadOrder[0].c1_1[0]", "Off"));

        var data = Data(saved);
        data.Element("f1_01")!.Value.Should().Be("Jane Q. Public & Sons <LLC>");
        data.Element("Boxes3a-b_ReadOrder")!.Element("f1_03")!.Value.Should().Be("LLC-code");
        data.Element("c1_1")!.Value.Should().Be("3",
            "the seven c1_1 boxes share one global node; turning box 1 off must not clear box 3's value");

        using (var doc = Open(saved))
        {
            var after = F.Packets(doc);
            foreach (var name in before.Keys.Where(k => k != "datasets"))
                after[name].Should().Equal(before[name], $"the W-9 {name} packet must not change");
        }

        var unchecked3 = Fill(saved, (W9 + "Boxes3a-b_ReadOrder[0].c1_1[2]", "Off"));
        Data(unchecked3).Element("c1_1")!.Value.Should().Be("0");
    }

    [Fact]
    public void IrsW4_SharedCheckBoxGroup_AndTextFields()
    {
        var source = Corpus("irs-w4.pdf")!;
        var saved = Fill(source,
            (W9 + "Step1a[0].f1_01[0]", "Given"),
            (W9 + "c1_1[1]", "2"));

        var data = Data(saved);
        data.Element("Step1a")!.Element("f1_01")!.Value.Should().Be("Given");
        data.Element("c1_1")!.Value.Should().Be("2");
    }
}
