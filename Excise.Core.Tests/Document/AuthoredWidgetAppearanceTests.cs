using AwesomeAssertions;
using Excise.Core.Authoring;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Excise.Core.Primitives;
using Excise.Core.Tests.Fixtures;
using Excise.TestSupport;
using Xunit;

namespace Excise.Core.Tests.Document;

/// <summary>
/// #1444: fields excise authors carry their own appearance. Before, every
/// widget had no <c>/AP</c> and no <c>/F</c>, and the AcroForm set
/// <c>NeedAppearances true</c>: legal in plain PDF, forbidden by PDF/A, and a
/// reader that ignores the flag (pdf.js, Chrome) drew nothing. The veraPDF gate
/// is <c>PdfATests.PdfA_WithFormFields_IsConformant_PerVeraPdf</c>; these pin
/// the structure it depends on, and use mutool to check the appearance really
/// draws the value.
/// </summary>
public class AuthoredWidgetAppearanceTests
{
    private static PdfDocument NewDocumentWithEveryFieldKind()
    {
        var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank(612, 792);
        doc.AddTextField(1, new PdfRectangle(72, 700, 300, 720), "name", defaultValue: "Ada Lovelace");
        doc.AddTextField(1, new PdfRectangle(72, 600, 300, 680), "notes", multiline: true);
        doc.AddCheckBox(1, new PdfRectangle(72, 560, 86, 574), "subscribe", defaultChecked: true);
        doc.AddChoiceField(1, new PdfRectangle(72, 520, 300, 540), "colour", new[] { "Red", "Green" }, defaultValue: "Green");
        doc.AddSignatureField(1, new PdfRectangle(72, 440, 300, 500), "signature");
        return doc;
    }

    [Fact]
    public void EveryAuthoredWidget_HasThePrintFlag_AndOnlyANormalAppearance_AndNeedAppearancesIsUnset()
    {
        byte[] saved;
        using (var authored = NewDocumentWithEveryFieldKind())
            saved = authored.SaveToBytes();

        using var doc = PdfDocument.Open(saved);
        var form = doc.GetAcroForm()!;
        form.Fields.Should().HaveCount(5);

        foreach (var field in form.Fields)
        {
            var widget = field.RawDictionary;
            var flags = widget.GetInt("F", 0);
            (flags & 4).Should().Be(4, $"{field.FullName}: the Print flag must be set");
            (flags & (1 | 2 | 32)).Should().Be(0, $"{field.FullName}: Invisible, Hidden and NoView must be clear");

            var ap = doc.Resolve(widget.GetOptional("AP")!).Should().BeAssignableTo<PdfDictionary>().Subject;
            ap.Select(entry => entry.Key.Value).Should().Equal(new[] { "N" },
                $"{field.FullName}: PDF/A allows only the normal appearance");

            var normal = doc.Resolve(ap.GetOptional("N")!);
            if (field.FieldType == PdfFieldType.Button)
            {
                normal.Should().NotBeOfType<PdfStream>($"{field.FullName}: a checkbox's /N is a state dictionary");
                var states = (PdfDictionary)normal!;
                doc.Resolve(states.GetOptional("Yes")!).Should().BeOfType<PdfStream>();
                doc.Resolve(states.GetOptional("Off")!).Should().BeOfType<PdfStream>();
                states.ContainsKey(widget.GetNameOrNull("AS")!).Should().BeTrue($"{field.FullName}: /AS names a state /N has");
            }
            else
            {
                var stream = normal.Should().BeOfType<PdfStream>().Subject;
                stream.GetNameOrNull("Subtype").Should().Be("Form");
                stream.GetOptional("BBox").Should().NotBeNull();
            }
        }

        var acroForm = (PdfDictionary)doc.Resolve(doc.Catalog.GetOptional("AcroForm")!);
        acroForm.GetBool("NeedAppearances").Should().BeFalse("the widgets carry their own appearances");
    }

    /// <summary>The independent check: a renderer that is not excise draws the values.</summary>
    [Fact]
    public void AuthoredValues_AreDrawnByTheirAppearances_PerMutool()
    {
        Assert.SkipUnless(MutoolTextOracle.IsAvailable, "mutool not installed");

        byte[] helvetica;
        using (var doc = NewDocumentWithEveryFieldKind())
            helvetica = doc.SaveToBytes();
        var embedded = PdfDocumentBuilder.Create()
            .DefaultFont(PdfFont.FromTrueType(TestFontFixtures.LoadDejaVuSansBytes(), 11))
            .TextField("Name", "name", defaultValue: "Grace Hopper")
            .SaveToBytes();

        var helveticaText = MutoolTextOracle.ExtractAllPages(helvetica);
        helveticaText.Should().Contain("Ada Lovelace", "the /Helv text field appearance draws its value");
        helveticaText.Should().Contain("Green", "the choice field appearance draws its value");
        MutoolTextOracle.ExtractAllPages(embedded).Should().Contain("Grace Hopper",
            "the embedded /DA font appearance draws its value");
    }

    [Fact]
    public void SetValue_OnAFieldAuthoredInThisSession_RedrawsTheAppearance_WithoutNeedAppearances()
    {
        Assert.SkipUnless(MutoolTextOracle.IsAvailable, "mutool not installed");

        using var doc = NewDocumentWithEveryFieldKind();
        var form = doc.GetAcroForm()!;
        form.FindField("name")!.SetValue("Grace Hopper");
        form.FindField("subscribe")!.SetValue("Off");

        var acroForm = (PdfDictionary)doc.Resolve(doc.Catalog.GetOptional("AcroForm")!);
        acroForm.GetBool("NeedAppearances").Should().BeFalse("the authored appearances were redrawn");

        var text = MutoolTextOracle.ExtractAllPages(doc.SaveToBytes());
        text.Should().Contain("Grace Hopper", "the redrawn appearance shows the new value");
        text.Should().NotContain("Ada Lovelace", "the old value's appearance is gone");
    }

    /// <summary>
    /// The documented limit: a reopened document has no font object excise can
    /// encode a new value with, so SetValue keeps the NeedAppearances fallback.
    /// </summary>
    [Fact]
    public void SetValue_AfterReopening_FallsBackToNeedAppearances()
    {
        byte[] saved;
        using (var authored = NewDocumentWithEveryFieldKind())
            saved = authored.SaveToBytes();

        using var doc = PdfDocument.Open(saved);
        doc.GetAcroForm()!.FindField("name")!.SetValue("Grace Hopper");

        var acroForm = (PdfDictionary)doc.Resolve(doc.Catalog.GetOptional("AcroForm")!);
        acroForm.GetBool("NeedAppearances").Should().BeTrue(
            "excise cannot redraw an appearance it did not author in this session");
    }
}
