using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Core.Tests.Document;

/// <summary>
/// Tests for document-metadata setters + catalog /Lang (#381) and the extended
/// AcroForm field options — /TU tooltip, /MaxLen, comb, date field, tab order
/// (#380). Everything is verified by saving and reopening so we exercise the
/// real serialize → parse round-trip.
/// </summary>
public class MetadataAndFieldOptionsTests
{
    private static PdfDocument RoundTrip(PdfDocument doc)
        => PdfDocument.Open(doc.SaveToBytes());

    // ── #381 metadata ────────────────────────────────────────────────────────

    [Fact]
    public void Metadata_SettersPersistThroughSaveAndReopen()
    {
        var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();
        doc.SetTitle("Quarterly Report");
        doc.SetAuthor("Ada Lovelace");
        doc.SetSubject("Numbers");
        doc.SetKeywords("pdf;report");
        doc.Language = "en-US";

        using var reopened = RoundTrip(doc);
        reopened.Title.Should().Be("Quarterly Report");
        reopened.Author.Should().Be("Ada Lovelace");
        reopened.Subject.Should().Be("Numbers");
        reopened.Keywords.Should().Be("pdf;report");
        reopened.Language.Should().Be("en-US");
    }

    [Fact]
    public void Language_SetNull_RemovesCatalogLang()
    {
        var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();
        doc.Language = "fr-FR";
        doc.Language.Should().Be("fr-FR");
        doc.Language = null;
        doc.Language.Should().BeNull();
        RoundTrip(doc).Language.Should().BeNull();
    }

    [Fact]
    public void Metadata_OnExistingDocWithInfo_UpdatesInPlace()
    {
        var first = PdfDocument.CreateNew();
        first.Pages.AddBlank();
        first.SetTitle("v1");
        using var doc = RoundTrip(first);   // now has a real /Info object
        doc.SetTitle("v2");
        RoundTrip(doc).Title.Should().Be("v2");
    }

    // ── #380 field options ────────────────────────────────────────────────────

    private static PdfDocument BlankOnePage()
    {
        var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();
        return doc;
    }

    [Fact]
    public void TextField_Tooltip_WritesTU()
    {
        var doc = BlankOnePage();
        doc.AddTextField(1, new PdfRectangle(72, 700, 300, 720), "name", tooltip: "Your full name");

        using var re = RoundTrip(doc);
        var field = re.GetAcroForm()!.FindField("name")!;
        field.RawDictionary.GetStringOrNull("TU").Should().Be("Your full name");
    }

    [Fact]
    public void TextField_MaxLenAndComb_WriteMaxLenAndFlag()
    {
        var doc = BlankOnePage();
        doc.AddTextField(1, new PdfRectangle(72, 700, 300, 720), "ssn", maxLength: 9, comb: true);

        using var re = RoundTrip(doc);
        var d = re.GetAcroForm()!.FindField("ssn")!.RawDictionary;
        d.GetOptional("MaxLen").Should().NotBeNull();
        ((PdfInteger)d.GetOptional("MaxLen")!).Value.Should().Be(9);
        var ff = ((PdfInteger)d.GetOptional("Ff")!).Value;
        (ff & 0x1000000).Should().Be(0x1000000, "comb flag (bit 25) must be set");
    }

    [Fact]
    public void TextField_CombWithoutMaxLen_IsIgnored()
    {
        var doc = BlankOnePage();
        doc.AddTextField(1, new PdfRectangle(72, 700, 300, 720), "x", comb: true);
        using var re = RoundTrip(doc);
        var d = re.GetAcroForm()!.FindField("x")!.RawDictionary;
        var ffObj = d.GetOptional("Ff");
        var ff = ffObj is PdfInteger pi ? pi.Value : 0;
        (ff & 0x1000000).Should().Be(0, "comb must be ignored without /MaxLen");
    }

    [Fact]
    public void DateField_WritesJavaScriptFormatActions()
    {
        var doc = BlankOnePage();
        doc.AddDateField(1, new PdfRectangle(72, 700, 300, 720), "dob", format: "yyyy-mm-dd");

        using var re = RoundTrip(doc);
        var d = re.GetAcroForm()!.FindField("dob")!.RawDictionary;
        var aa = re.Resolve(d.GetOptional("AA")!) as PdfDictionary;
        aa.Should().NotBeNull();
        var fmt = re.Resolve(aa!.GetOptional("F")!) as PdfDictionary;
        fmt!.GetStringOrNull("JS").Should().Contain("AFDate_FormatEx");
        fmt.GetStringOrNull("JS").Should().Contain("yyyy-mm-dd");
    }

    [Fact]
    public void CheckBoxAndChoice_AcceptTooltip()
    {
        var doc = BlankOnePage();
        doc.AddCheckBox(1, new PdfRectangle(72, 700, 90, 718), "agree", tooltip: "Accept terms");
        doc.AddChoiceField(1, new PdfRectangle(72, 660, 300, 678), "tier",
            new[] { "Basic", "Pro" }, tooltip: "Pick a tier");

        using var re = RoundTrip(doc);
        var form = re.GetAcroForm()!;
        form.FindField("agree")!.RawDictionary.GetStringOrNull("TU").Should().Be("Accept terms");
        form.FindField("tier")!.RawDictionary.GetStringOrNull("TU").Should().Be("Pick a tier");
    }

    [Fact]
    public void SetTabOrder_WritesPageTabs()
    {
        var doc = BlankOnePage();
        doc.SetTabOrder(1, AcroFormAuthoring.TabOrder.Structure);

        using var re = RoundTrip(doc);
        re.GetPage(1).Dictionary.GetNameOrNull("Tabs").Should().Be("S");
    }
}
