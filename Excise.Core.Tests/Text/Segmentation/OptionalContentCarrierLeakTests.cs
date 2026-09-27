using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Operations;
using Excise.Core.Primitives;
using Excise.Core.Text.Segmentation;
using Excise.TestSupport;
using Xunit;
using static Excise.Core.Tests.Text.Segmentation.FormXObjectRedactionTests;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// #1862: a term in an optional-content group's <c>/Name</c> (the layers panel) or
/// in another string of the optional-content tree survived <c>RedactText</c> and
/// <c>ScrubTerms</c> under both profiles, with no report. The saved bytes are read
/// by <see cref="SavedPdfLeakScanner"/>, which decodes every string object.
/// </summary>
public class OptionalContentCarrierLeakTests
{
    private const string Latin = "KESTREL";

    public enum Entry { RedactText, ScrubTerms }

    private static string Utf16Hex(string text) =>
        "<FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(text)) + ">";

    /// <summary>
    /// A page drawing its line inside layer /oc1 (object 6). <paramref name="catalog"/>
    /// goes into the catalog, <paramref name="ocg"/> is the group, and
    /// <paramref name="extra"/> follows it from object 7.
    /// </summary>
    private static byte[] Layered(string catalog, string ocg, params string[] extra) => Build(new[]
    {
        Obj($"<< /Type /Catalog /Pages 2 0 R {catalog} >>"),
        Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
            "/Resources << /Font << /F1 5 0 R >> /Properties << /oc1 6 0 R >> >> >>"),
        Stream("", "/OC /oc1 BDC BT /F1 12 Tf 72 700 Td (Public line) Tj ET EMC"),
        Obj(HelveticaFont),
        Obj(ocg),
    }.Concat(extra.Select(Obj)).ToArray());

    private const string Listed = "/OCProperties << /OCGs [6 0 R] /D << /Order [6 0 R] >> >>";

    private static byte[] Fixture(string shape) => shape switch
    {
        "layer /Name" => Layered(Listed, $"<< /Type /OCG /Name (Layer {Latin} notes) >>"),
        "utf16-hex" => Layered(Listed, $"<< /Type /OCG /Name {Utf16Hex($"Layer {Latin} notes")} >>"),
        "indirect" => Layered(Listed, "<< /Type /OCG /Name 7 0 R >>", $"(Layer {Latin} notes)"),
        // A group only the page's /Properties names: no layers panel lists it, the file still holds it.
        "unlisted" => Layered("", $"<< /Type /OCG /Name (Layer {Latin} notes) >>"),
        "configuration labels" => Layered(
            $"/OCProperties << /OCGs [6 0 R] /D << /Name ({Latin} view) /Creator ({Latin} CAD) " +
            $"/Order [(Layer {Latin} notes) 6 0 R] >> /Configs [<< /Name (Print {Latin}) >>] >>",
            "<< /Type /OCG /Name (Layer) >>"),
        "usage strings" => Layered(Listed,
            $"<< /Type /OCG /Name (Layer) /Usage << /User << /Type /Ind /Name [(Reviewer) ({Latin})] >> " +
            $"/CreatorInfo << /Creator ({Latin} CAD) /Subtype /Artwork >> >> >>"),
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };

    public static TheoryData<string, RedactionProfile, Entry> Matrix()
    {
        var data = new TheoryData<string, RedactionProfile, Entry>();
        foreach (var shape in new[] { "layer /Name", "utf16-hex", "indirect", "unlisted", "configuration labels", "usage strings" })
            foreach (var profile in new[] { RedactionProfile.Standard, RedactionProfile.Maximum })
                foreach (var entry in new[] { Entry.RedactText, Entry.ScrubTerms })
                    data.Add(shape, profile, entry);
        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void OptionalContentString_IsScrubbed(string shape, RedactionProfile profile, Entry entry)
    {
        var pdf = Fixture(shape);
        SavedPdfLeakScanner.FindTerm(pdf, Latin).Should().NotBeEmpty("input-side control: the tree holds the term");
        var options = RedactionOptions.ForProfile(profile);

        using var document = PdfDocument.Open(pdf);
        if (entry == Entry.RedactText)
            document.RedactText(Latin, options);
        else
            PdfDocumentSanitizer.ScrubTerms(document, new[] { Latin }, caseSensitive: false,
                    RedactionCarriers.All, options.CarrierPolicy)
                .For(RedactionCarriers.OptionalContent)
                .Should().BeEquivalentTo(new { TermFound = true, Modified = true, RefusedReason = (string?)null });

        var saved = document.SaveToBytes();
        SavedPdfLeakScanner.FindTerm(saved, Latin).Should().BeEmpty($"{shape}: no layer string may keep the term");

        using var reopened = PdfDocument.Open(saved);
        var properties = (PdfDictionary)reopened.Resolve(reopened.GetPage(1).Resources!.GetOptional("Properties")!);
        var ocg = (PdfDictionary)reopened.Resolve(properties.GetOptional("oc1")!);
        var name = reopened.Resolve(ocg.GetOptional("Name")!).Should().BeOfType<PdfString>(
            "an OCG's /Name is required (Table 96), so even Maximum keeps the key").Which.Value;
        if (shape is "layer /Name" or "utf16-hex" or "indirect" or "unlisted")
            name.Should().Be(profile == RedactionProfile.Maximum ? "" : "Layer  notes");
    }

    [Fact]
    public void ReportOnly_LeavesTheNameAndSaysItHoldsTheTerm()
    {
        using var document = PdfDocument.Open(Fixture("layer /Name"));

        var outcome = PdfDocumentSanitizer.ScrubTerms(document, new[] { Latin }, caseSensitive: false,
            RedactionCarriers.All,
            CarrierScrubPolicy.Default.With(RedactionCarriers.OptionalContent, CarrierScrubMode.ReportOnly));

        outcome.NeedingAttention.Should().ContainSingle(r => r.Carrier == RedactionCarriers.OptionalContent && r.TermFound);
        SavedPdfLeakScanner.FindTerm(document.SaveToBytes(), Latin).Should().NotBeEmpty();
    }

    /// <summary>
    /// The planted failure: with the carrier switched off the term stays, and the
    /// two audits that do not share the scrub's code path must say so. With it on,
    /// both are clean.
    /// </summary>
    [Fact]
    public void Audits_SeeALayerNameTheScrubLeft_AndNotOneItScrubbed()
    {
        var pdf = Fixture("unlisted");

        using (var leaking = PdfDocument.Open(pdf))
        {
            leaking.RedactText(Latin,
                RedactionOptions.Default with { Carriers = RedactionCarriers.All & ~RedactionCarriers.OptionalContent });
            SavedPdfLeakScanner.FindTerm(leaking.SaveToBytes(), Latin).Should().NotBeEmpty("the planted failure");

            RedactionCarrierAudit.Inspect(leaking, new[] { Latin }).OptionalContentStringCount.Should().Be(1);
            CarrierTextRecovery.Scan(leaking).Should().Contain(f =>
                f.Carrier == "layer /Name" && f.Text.Contains(Latin, StringComparison.Ordinal));
        }

        using var scrubbed = PdfDocument.Open(pdf);
        scrubbed.RedactText(Latin, RedactionOptions.Default);
        SavedPdfLeakScanner.FindTerm(scrubbed.SaveToBytes(), Latin).Should().BeEmpty();
        RedactionCarrierAudit.Inspect(scrubbed, new[] { Latin }).OptionalContentStringCount.Should().Be(0);
        CarrierTextRecovery.Scan(scrubbed).Should().NotContain(f => f.Text.Contains(Latin, StringComparison.Ordinal));
    }

    /// <summary>The safe-copy pass the GUI runs warns about a layer name its own scrub was told to skip.</summary>
    [Fact]
    public void SafeCopy_WarnsAboutALayerNameItsScrubSkipped_AndNotOneItScrubbed()
    {
        var pdf = Fixture("layer /Name");
        static IReadOnlyList<string> Warnings(byte[] pdf, RedactionCarriers carriers)
        {
            using var document = PdfDocument.Open(pdf);
            return RedactedCopySafetyPolicy.Evaluate(document, RedactedCopySafetyRequest.ForTerms(
                new[] { Latin }, RedactionOptions.Default with { Carriers = carriers })).Warnings;
        }

        Warnings(pdf, RedactionCarriers.All & ~RedactionCarriers.OptionalContent)
            .Should().Contain(w => w.Contains("layer name", StringComparison.Ordinal));
        Warnings(pdf, RedactionCarriers.All)
            .Should().NotContain(w => w.Contains("layer name", StringComparison.Ordinal));
    }

    [Fact]
    public void AreaAudit_CountsEveryLayerString_AsUnexamined()
    {
        using var document = PdfDocument.Open(Fixture("configuration labels"));

        var audit = RedactionCarrierAudit.Inspect(document);

        audit.OptionalContentStringCount.Should().Be(4,
            "an area redaction has no term to test a layer label against; the tool-written /Creator is not a label");
        audit.Describe().Should().Contain(line => line.Contains("layer name", StringComparison.Ordinal));
    }

    /// <summary>
    /// #1862: a viewer shows a layer name in its layers panel and a page-label
    /// prefix in its page-number box, so recovery classes neither as hidden.
    /// </summary>
    [Fact]
    public void Recovery_ClassesLayerNamesAndPageLabelPrefixesAsVisible()
    {
        using var layered = PdfDocument.Open(Fixture("layer /Name"));
        CarrierTextRecovery.Scan(layered).Should().ContainSingle(f => f.Carrier == "layer /Name")
            .Which.VisibleElsewhere.Should().BeTrue();

        using var labelled = PdfDocument.Open(CarrierTrapFixtures.WithCatalog($"/PageLabels << /Nums [0 << /S /D /P ({Latin}-) >>] >>"));
        CarrierTextRecovery.Scan(labelled).Should().ContainSingle(f => f.Carrier == "page label /P")
            .Which.VisibleElsewhere.Should().BeTrue();
    }
}
