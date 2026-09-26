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

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// #1860: a carrier that cuts the term out of a string can re-form it.
/// <c>KESKESTRELTREL</c> less <c>KESTREL</c> is <c>KESTREL</c>, and a
/// case-insensitive cut of <c>kestrel</c> from <c>KESkestrelTREL</c> leaves
/// <c>KESTREL</c> too. Every carrier gets the nested term under both profiles'
/// policies, and the saved bytes are read by <see cref="SavedPdfLeakScanner"/>;
/// the text-visible carriers are read by mutool in
/// <c>ReformedTermCarrierOracleTests</c> (Excise.Rendering.Tests).
/// </summary>
public class ReformedTermCarrierRedactionTests
{
    /// <summary>What one single-pass cut of either nesting leaves behind.</summary>
    private const string Reformed = "KESTREL";

    public enum Entry { RedactText, ScrubTerms }

    /// <summary>The term, and a value in which it is nested inside itself.</summary>
    private static readonly (string Term, string Nested)[] Nestings =
    {
        ("KESTREL", "KESKESTRELTREL"),
        ("kestrel", "KESkestrelTREL"),
    };

    /// <summary>The value every carrier holds: the rest of it must survive Strip.</summary>
    private static string Value(string nested) => $"Wren {nested} Heron";

    /// <summary>Each cut-the-term carrier: a PDF holding <c>v</c> there, and its RedactText report row.</summary>
    private static readonly Dictionary<RedactionCarriers, (Func<string, byte[]> Build, string? Row)> Carriers = new()
    {
        [RedactionCarriers.Info] = (v => CarrierTrapFixtures.WithInfo($"<< /Title ({v}) /Subject ({v}) >>"), "/Info"),
        [RedactionCarriers.Xmp] = (v => CarrierTrapFixtures.WithCatalog("/Metadata 6 0 R",
            extra: AttachmentRedactionTests.Stream("/Type /Metadata /Subtype /XML",
                $"<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><dc:title>{v}</dc:title></x:xmpmeta>")), "XMP /Metadata"),
        [RedactionCarriers.Xfa] = (v => CarrierTrapFixtures.WithCatalog("/AcroForm << /Fields [] /XFA 6 0 R >>",
            extra: AttachmentRedactionTests.Stream("",
                "<xdp:xdp xmlns:xdp=\"http://ns.adobe.com/xdp/\"><template><field name=\"who\">" +
                $"<value><text>{v}</text></value><assist><toolTip>{v}</toolTip></assist></field></template></xdp:xdp>")), null),
        [RedactionCarriers.Outlines] = (v => CarrierTrapFixtures.WithCatalog("/Outlines 6 0 R", extra: new[]
        {
            "<< /Type /Outlines /First 7 0 R /Last 7 0 R /Count 1 >>",
            $"<< /Title ({v}) /Parent 6 0 R /Dest [3 0 R /Fit] >>",
        }), "/Outlines titles"),
        [RedactionCarriers.Annotations] = (v => CarrierTrapFixtures.WithCatalog("", page: "/Annots [6 0 R]",
            $"<< /Type /Annot /Subtype /Text /Rect [72 600 92 620] /Contents ({v}) /Subj ({v}) >>"), "annotation /Contents"),
        [RedactionCarriers.FormFields] = (v => CarrierTrapFixtures.Field(null,
            $"/FT /Tx /T ({v}) /TU ({v}) /V ({v}) /DV ({v}) /RV (<p>{v}</p>)"), null),
        [RedactionCarriers.StructTree] = (v => CarrierTrapFixtures.WithCatalog("/StructTreeRoot 6 0 R", extra: new[]
        {
            "<< /Type /StructTreeRoot /K 7 0 R >>",
            $"<< /Type /StructElem /S /Figure /P 6 0 R /Alt ({v}) /ActualText ({v}) >>",
        }), null),
        [RedactionCarriers.JavaScript] = (v => CarrierTrapFixtures.WithCatalog(
            "/Names << /JavaScript << /Names [(init) 6 0 R] >> >>",
            extra: $"<< /S /JavaScript /JS (var who = '{v}';) >>"), null),
        [RedactionCarriers.ActionUris] = (v => CarrierTrapFixtures.WithCatalog("", page: "/Annots [6 0 R]",
            $"<< /Type /Annot /Subtype /Link /Rect [72 600 372 620] /A << /S /URI /URI (https://example.test/{v}) >> >>"),
            "link /A /URI"),
        [RedactionCarriers.MarkedContent] = (v => MarkedContentCarrierFixtures.Inline(MarkedContentCarrierFixtures.Literal(v)),
            "marked-content /ActualText, /Alt, /E"),
        [RedactionCarriers.PageLabels] = (v => CarrierTrapFixtures.WithCatalog($"/PageLabels << /Nums [0 << /S /D /P ({v}) >>] >>"),
            "/PageLabels /P"),
        [RedactionCarriers.NameTreeKeys] = (v => CarrierTrapFixtures.WithCatalog($"/Names << /Dests << /Names [({v}) [3 0 R /Fit]] >> >>"),
            "/Names and /Dests keys"),
    };

    public static TheoryData<RedactionCarriers, RedactionProfile, int, Entry> Matrix()
    {
        var data = new TheoryData<RedactionCarriers, RedactionProfile, int, Entry>();
        foreach (var carrier in Carriers.Keys)
            foreach (var profile in new[] { RedactionProfile.Standard, RedactionProfile.Maximum })
                for (var nesting = 0; nesting < Nestings.Length; nesting++)
                    foreach (var entry in new[] { Entry.RedactText, Entry.ScrubTerms })
                        data.Add(carrier, profile, nesting, entry);
        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void ACutThatWouldReformTheTerm_LeavesNoneOfIt(
        RedactionCarriers carrier, RedactionProfile profile, int nesting, Entry entry)
    {
        var (term, nested) = Nestings[nesting];
        var (build, row) = Carriers[carrier];
        var pdf = build(Value(nested));
        SavedPdfLeakScanner.FindTerm(pdf, nested).Should().NotBeEmpty("input-side control");
        var options = RedactionOptions.ForProfile(profile);

        using var document = PdfDocument.Open(pdf);
        if (entry == Entry.RedactText)
        {
            var report = document.RedactText(term, options);
            if (row != null)
                report.Carriers.Should().ContainSingle(c => c.Carrier == row).Which.Scrubbed.Should().BeTrue();
        }
        else
        {
            PdfDocumentSanitizer.ScrubTerms(document, new[] { term }, caseSensitive: false,
                    RedactionCarriers.All, options.CarrierPolicy)
                .For(carrier).Should().BeEquivalentTo(new { TermFound = true, Modified = true, RefusedReason = (string?)null });
        }

        var saved = document.SaveToBytes();
        SavedPdfLeakScanner.FindTerm(saved, Reformed).Should().BeEmpty($"{carrier}: the cut must not re-form the term");
        SavedPdfLeakScanner.FindTerm(saved, term).Should().BeEmpty();
        if (entry == Entry.ScrubTerms && options.CarrierPolicy.ModeFor(carrier) == CarrierScrubMode.Strip)
            SavedPdfLeakScanner.FindTerm(saved, "Wren").Should().NotBeEmpty(
                "Strip cuts until nothing is left to cut and keeps the rest of the value");
    }

    /// <summary>Cutting one term can join another: <c>KESZZZTREL</c> less <c>ZZZ</c> is <c>KESTREL</c>.</summary>
    [Fact]
    public void CuttingOneTermThatJoinsAnother_LeavesNeither()
    {
        using var document = PdfDocument.Open(CarrierTrapFixtures.WithInfo("<< /Title (Wren KESZZZTREL Heron) >>"));

        PdfDocumentSanitizer.ScrubTerms(document, new[] { Reformed, "ZZZ" }, caseSensitive: false,
                RedactionCarriers.All, CarrierScrubPolicy.Default)
            .For(RedactionCarriers.Info).Should().BeEquivalentTo(new { TermFound = true, Modified = true });

        var saved = document.SaveToBytes();
        SavedPdfLeakScanner.FindTerm(saved, Reformed).Should().BeEmpty();
        SavedPdfLeakScanner.FindTerm(saved, "ZZZ").Should().BeEmpty();
        document.Info!.GetStringOrNull("Title").Should().Be("Wren  Heron");
    }

    /// <summary>
    /// A soft hyphen inside the term hides it from a raw search but not from the
    /// page matcher's fold, nor from a reader, which does not draw it. The cut
    /// takes the raw span whose fold is the term (#1871), soft hyphen and all.
    /// </summary>
    [Fact]
    public void ATermOnlyTheFoldSees_IsCutWhereTheFoldMapsBackToTheValue()
    {
        const string hidden = "KES­TREL";
        var title = "<FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes($"Wren {hidden} Heron")) + ">";
        var pdf = CarrierTrapFixtures.WithCatalog("/Outlines 6 0 R", extra: new[]
        {
            "<< /Type /Outlines /First 7 0 R /Last 7 0 R /Count 1 >>",
            $"<< /Title {title} /Parent 6 0 R /Dest [3 0 R /Fit] >>",
        });
        SavedPdfLeakScanner.FindTerm(pdf, hidden).Should().NotBeEmpty("input-side control");
        using var document = PdfDocument.Open(pdf);

        PdfDocumentSanitizer.ScrubTerms(document, new[] { Reformed }, caseSensitive: false,
                RedactionCarriers.All, CarrierScrubPolicy.Default)
            .For(RedactionCarriers.Outlines).Should().BeEquivalentTo(new { TermFound = true, Modified = true });

        var saved = document.SaveToBytes();
        SavedPdfLeakScanner.FindTerm(saved, hidden).Should().BeEmpty();
        SavedPdfLeakScanner.FindTerm(saved, Reformed).Should().BeEmpty();
        PdfOutlineParser.Parse(document).Single().Title.Should().Be("Wren  Heron");
    }

    /// <summary>
    /// The area pass cuts the term out of a field whose widget the match
    /// overlaps (#1038), before and independently of the document-level stage.
    /// </summary>
    [Theory]
    [InlineData("/FT /Tx /T (name) /V ({0}) /DV ({0}) /RV (<p>{0}</p>)", 0)]
    [InlineData("/FT /Tx /T (name) /V ({0}) /DV ({0}) /RV (<p>{0}</p>)", 1)]
    [InlineData("/FT /Ch /T (choice) /Opt [({0}) [(export {0}) (shown {0})]]", 0)]
    [InlineData("/FT /Ch /T (choice) /Opt [({0}) [(export {0}) (shown {0})]]", 1)]
    public void TheAreaPassOverAField_LeavesNoneOfIt(string fieldEntries, int nesting)
    {
        var (term, nested) = Nestings[nesting];
        using var document = PdfDocument.Open(CarrierTrapFixtures.Field(null, string.Format(fieldEntries, Value(nested))));

        InteractiveRedactionScrubber.ScrubTerm(document.GetPage(1), new PdfRectangle(72, 600, 272, 620), term, caseSensitive: false)
            .Should().BeTrue();

        var saved = document.SaveToBytes();
        SavedPdfLeakScanner.FindTerm(saved, Reformed).Should().BeEmpty();
        SavedPdfLeakScanner.FindTerm(saved, term).Should().BeEmpty();
        SavedPdfLeakScanner.FindTerm(saved, "Wren").Should().NotBeEmpty("the area pass cuts, it does not drop the value");
    }

    /// <summary>
    /// A kept attachment has the term cut out of its text (#1572), and a file
    /// listed under a key that holds it is removed with the key cut out of its
    /// leaf's <c>/Limits</c> (#1582).
    /// </summary>
    [Theory]
    [InlineData("text", RedactionProfile.Standard, 0)]
    [InlineData("text", RedactionProfile.Standard, 1)]
    [InlineData("text", RedactionProfile.Maximum, 0)]
    [InlineData("text", RedactionProfile.Maximum, 1)]
    [InlineData("limits", RedactionProfile.Standard, 0)]
    [InlineData("limits", RedactionProfile.Standard, 1)]
    [InlineData("limits", RedactionProfile.Maximum, 0)]
    [InlineData("limits", RedactionProfile.Maximum, 1)]
    public void AKeptAttachment_LeavesNoneOfIt(string where, RedactionProfile profile, int nesting)
    {
        var (term, nested) = Nestings[nesting];
        var pdf = where == "text"
            ? CarrierTrapFixtures.WithCatalog("/Names << /EmbeddedFiles << /Names [(note.txt) 6 0 R] >> >>", extra: new[]
            {
                "<< /Type /Filespec /F (note.txt) /UF (note.txt) /EF << /F 7 0 R >> >>",
                AttachmentRedactionTests.Stream("/Type /EmbeddedFile", Value(nested)),
            })
            : CarrierTrapFixtures.WithCatalog("/Names << /EmbeddedFiles 6 0 R >>", extra: new[]
            {
                "<< /Kids [7 0 R] >>",
                $"<< /Limits [({Value(nested)}) ({Value(nested)})] /Names [({Value(nested)}) 8 0 R] >>",
                "<< /Type /Filespec /F (note.bin) /EF << /F 9 0 R >> >>",
                AttachmentRedactionTests.Stream("/Type /EmbeddedFile", "hello"),
            });
        using var document = PdfDocument.Open(pdf);

        var report = document.RedactText(term, RedactionOptions.ForProfile(profile) with { KeepAttachments = true });

        report.Attachments.Should().ContainSingle().Which.Disposition.Should().Be(where == "text"
            ? AttachmentDisposition.KeptTermRemoved
            : AttachmentDisposition.Removed);
        var saved = document.SaveToBytes();
        SavedPdfLeakScanner.FindTerm(saved, Reformed).Should().BeEmpty();
        SavedPdfLeakScanner.FindTerm(saved, term).Should().BeEmpty();
        if (where == "text")
            SavedPdfLeakScanner.FindTerm(saved, "Wren").Should().NotBeEmpty("the rest of the text is kept");
    }
}
