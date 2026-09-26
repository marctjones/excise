using System;
using System.Collections.Generic;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Operations;
using Excise.Core.Text.Segmentation;
using Excise.TestSupport;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// #1871: the page matcher reads a curly quote as <c>'</c>, a typographic dash
/// as <c>-</c> and a whitespace run as one space, and every carrier now matches
/// through that same fold. Each carrier holds the term spelled the other way,
/// under both profiles' policies. <see cref="SavedPdfLeakScanner"/> is literal,
/// so the saved bytes are searched for the spelling the carrier HELD as well as
/// the typed term; mutool reads the text-visible carriers in
/// <c>TypographicCarrierOracleTests</c> (Excise.Rendering.Tests).
/// </summary>
public class TypographicCarrierRedactionTests
{
    public enum Entry { RedactText, ScrubTerms }

    /// <summary>The term as typed, and as a carrier spells it.</summary>
    public static readonly (string Term, string Held)[] Spellings =
    {
        ("O'Brien", "O’Brien"),
        ("O’Brien", "O'Brien"),
        ("12-345", "12–345"),
        ("12-345", "12—345"),
        ("Smith Jones", "Smith  Jones"),
        ("Smith Jones", "Smith  Jones"),
    };

    /// <summary>The value every carrier holds: Strip must keep the rest of it.</summary>
    private static string Value(string held) => $"Wren {held} Heron";

    /// <summary>
    /// <paramref name="text"/> as a PDF text string: PDFDocEncoding, the way
    /// producers write titles (the issue's <c>\220</c>), or UTF-16BE for a
    /// no-break space, which PDFDocEncoding has no code for.
    /// </summary>
    public static string Pdf(string text) => text.Contains(' ')
        ? MarkedContentCarrierFixtures.Utf16Hex(text)
        : "(" + text.Replace("’", @"\220").Replace("–", @"\205").Replace("—", @"\204") + ")";

    /// <summary>A stream body carrying <paramref name="text"/> as UTF-8 bytes (the fixtures write Latin-1).</summary>
    private static string Utf8(string text) => Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(text));

    /// <summary>Each carrier: a PDF holding <c>v</c> there, and its RedactText report row.</summary>
    private static readonly Dictionary<RedactionCarriers, (Func<string, byte[]> Build, string? Row)> Carriers = new()
    {
        [RedactionCarriers.Info] = (v => CarrierTrapFixtures.WithInfo($"<< /Title {Pdf(v)} /Subject {Pdf(v)} >>"), "/Info"),
        [RedactionCarriers.Xmp] = (v => CarrierTrapFixtures.WithCatalog("/Metadata 6 0 R",
            extra: AttachmentRedactionTests.Stream("/Type /Metadata /Subtype /XML",
                Utf8($"<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><dc:title>{v}</dc:title></x:xmpmeta>"))), "XMP /Metadata"),
        [RedactionCarriers.Xfa] = (v => CarrierTrapFixtures.WithCatalog("/AcroForm << /Fields [] /XFA 6 0 R >>",
            extra: AttachmentRedactionTests.Stream("", Utf8(
                "<xdp:xdp xmlns:xdp=\"http://ns.adobe.com/xdp/\"><template><field name=\"who\">" +
                $"<value><text>{v}</text></value><assist><toolTip>{v}</toolTip></assist></field></template></xdp:xdp>"))), null),
        [RedactionCarriers.Outlines] = (v => CarrierTrapFixtures.WithCatalog("/Outlines 6 0 R", extra: new[]
        {
            "<< /Type /Outlines /First 7 0 R /Last 7 0 R /Count 1 >>",
            $"<< /Title {Pdf(v)} /Parent 6 0 R /Dest [3 0 R /Fit] >>",
        }), "/Outlines titles"),
        [RedactionCarriers.Annotations] = (v => CarrierTrapFixtures.WithCatalog("", page: "/Annots [6 0 R]",
            $"<< /Type /Annot /Subtype /Text /Rect [72 600 92 620] /Contents {Pdf(v)} /Subj {Pdf(v)} >>"), "annotation /Contents"),
        [RedactionCarriers.FormFields] = (v => CarrierTrapFixtures.Field(null,
            $"/FT /Tx /T (name) /TU {Pdf(v)} /V {Pdf(v)} /DV {Pdf(v)} /RV {Pdf($"<p>{v}</p>")}"), null),
        [RedactionCarriers.StructTree] = (v => CarrierTrapFixtures.WithCatalog("/StructTreeRoot 6 0 R", extra: new[]
        {
            "<< /Type /StructTreeRoot /K 7 0 R >>",
            $"<< /Type /StructElem /S /Figure /P 6 0 R /Alt {Pdf(v)} /ActualText {Pdf(v)} >>",
        }), null),
        [RedactionCarriers.JavaScript] = (v => CarrierTrapFixtures.WithCatalog(
            "/Names << /JavaScript << /Names [(init) 6 0 R] >> >>",
            extra: $"<< /S /JavaScript /JS {Pdf($"var who = '{v}';")} >>"), null),
        [RedactionCarriers.MarkedContent] = (v => MarkedContentCarrierFixtures.Inline(Pdf(v)),
            "marked-content /ActualText, /Alt, /E"),
        [RedactionCarriers.PageLabels] = (v => CarrierTrapFixtures.WithCatalog($"/PageLabels << /Nums [0 << /S /D /P {Pdf(v)} >>] >>"),
            "/PageLabels /P"),
        [RedactionCarriers.NameTreeKeys] = (v => CarrierTrapFixtures.WithCatalog($"/Names << /Dests << /Names [{Pdf(v)} [3 0 R /Fit]] >> >>"),
            "/Names and /Dests keys"),
        [RedactionCarriers.EmbeddedFiles] = (v => TextAttachment(v), null),
    };

    private static byte[] TextAttachment(string text) =>
        CarrierTrapFixtures.WithCatalog("/Names << /EmbeddedFiles << /Names [(note.txt) 6 0 R] >> >>", extra: new[]
        {
            "<< /Type /Filespec /F (note.txt) /UF (note.txt) /EF << /F 7 0 R >> >>",
            AttachmentRedactionTests.Stream("/Type /EmbeddedFile", Utf8(text)),
        });

    public static TheoryData<RedactionCarriers, RedactionProfile, int, Entry> Matrix()
    {
        var data = new TheoryData<RedactionCarriers, RedactionProfile, int, Entry>();
        foreach (var carrier in Carriers.Keys)
            foreach (var profile in new[] { RedactionProfile.Standard, RedactionProfile.Maximum })
                for (var spelling = 0; spelling < Spellings.Length; spelling++)
                    foreach (var entry in new[] { Entry.RedactText, Entry.ScrubTerms })
                        data.Add(carrier, profile, spelling, entry);
        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void ACarrierSpellingTheTermTypographically_KeepsNoneOfIt(
        RedactionCarriers carrier, RedactionProfile profile, int spelling, Entry entry)
    {
        var (term, held) = Spellings[spelling];
        var (build, row) = Carriers[carrier];
        var pdf = build(Value(held));
        SavedPdfLeakScanner.FindTerm(pdf, held).Should().NotBeEmpty("input-side control");
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
        SavedPdfLeakScanner.FindTerm(saved, held).Should().BeEmpty($"{carrier}: the carrier spelled the term '{held}'");
        SavedPdfLeakScanner.FindTerm(saved, term).Should().BeEmpty();
        if (entry == Entry.ScrubTerms && carrier != RedactionCarriers.EmbeddedFiles
            && options.CarrierPolicy.ModeFor(carrier) == CarrierScrubMode.Strip)
            SavedPdfLeakScanner.FindTerm(saved, "Wren").Should().NotBeEmpty(
                "Strip cuts the span whose fold is the term and keeps the rest of the value");
    }

    public static TheoryData<RedactionCarriers, RedactionProfile> LookAlikes()
    {
        var data = new TheoryData<RedactionCarriers, RedactionProfile>();
        foreach (var carrier in Carriers.Keys)
            foreach (var profile in new[] { RedactionProfile.Standard, RedactionProfile.Maximum })
                data.Add(carrier, profile);
        return data;
    }

    /// <summary>A curly quote, a dash and a doubled space that do not spell the term are left alone.</summary>
    [Theory]
    [MemberData(nameof(LookAlikes))]
    public void ALookAlikeThatDoesNotSpellTheTerm_IsKept(RedactionCarriers carrier, RedactionProfile profile)
    {
        const string lookAlike = "O’Brian 12–346 Smith  Jonas";
        var pdf = Carriers[carrier].Build(Value(lookAlike));
        using var document = PdfDocument.Open(pdf);

        var outcome = PdfDocumentSanitizer.ScrubTerms(document, new[] { "O'Brien", "12-345", "Smith Jones" },
            caseSensitive: false, RedactionCarriers.All, RedactionOptions.ForProfile(profile).CarrierPolicy);

        outcome.For(carrier).Should().BeEquivalentTo(new { TermFound = false, Modified = false });
        SavedPdfLeakScanner.FindTerm(document.SaveToBytes(), lookAlike).Should().NotBeEmpty($"{carrier}: nothing in it is the term");
    }

    /// <summary>
    /// Whole-word is judged on the raw neighbours of the span whose fold is the
    /// term, as the page matcher judges it: <c>O’Brien</c> is cut, <c>O’Briens</c> is not.
    /// </summary>
    [Fact]
    public void WholeWord_CutsABoundedTypographicSpellingAndKeepsALongerWord()
    {
        using var document = PdfDocument.Open(CarrierTrapFixtures.WithInfo(
            @"<< /Title (Wren O\220Brien Heron) /Subject (Wren O\220Briens Heron) >>"));

        PdfDocumentSanitizer.ScrubTerms(document, new[] { "O'Brien" }, caseSensitive: false,
                RedactionCarriers.All, CarrierScrubPolicy.Default, wholeWord: true)
            .For(RedactionCarriers.Info).Should().BeEquivalentTo(new { TermFound = true, Modified = true });

        var saved = document.SaveToBytes();
        SavedPdfLeakScanner.FindTerm(saved, "O’Brien Heron").Should().BeEmpty();
        document.Info!.GetStringOrNull("Title").Should().Be("Wren  Heron");
        document.Info!.GetStringOrNull("Subject").Should().Be("Wren O’Briens Heron");
    }

    /// <summary>
    /// ReportOnly keeps the value, so the row is the only thing that says the
    /// term is there: before #1871 a typographic spelling reported clean.
    /// </summary>
    [Theory]
    [InlineData(RedactionCarriers.Info)]
    [InlineData(RedactionCarriers.Outlines)]
    [InlineData(RedactionCarriers.Xfa)]
    public void ReportOnly_SaysTheCarrierHoldsATypographicSpelling(RedactionCarriers carrier)
    {
        var pdf = Carriers[carrier].Build(Value("O’Brien"));
        using var document = PdfDocument.Open(pdf);

        var outcome = PdfDocumentSanitizer.ScrubTerms(document, new[] { "O'Brien" }, caseSensitive: false,
            RedactionCarriers.All, CarrierScrubPolicy.Uniform(CarrierScrubMode.ReportOnly));

        outcome.For(carrier).Should().BeEquivalentTo(new { TermFound = true, Modified = false });
        SavedPdfLeakScanner.FindTerm(document.SaveToBytes(), "O’Brien").Should().NotBeEmpty("ReportOnly keeps it");
    }

    /// <summary>
    /// A kept text attachment has the typographic spelling cut out of it
    /// (#1572) and keeps the rest.
    /// </summary>
    [Theory]
    [InlineData(RedactionProfile.Standard, 0)]
    [InlineData(RedactionProfile.Standard, 2)]
    [InlineData(RedactionProfile.Standard, 4)]
    [InlineData(RedactionProfile.Maximum, 1)]
    [InlineData(RedactionProfile.Maximum, 3)]
    [InlineData(RedactionProfile.Maximum, 5)]
    public void AKeptTextAttachment_KeepsNoneOfIt(RedactionProfile profile, int spelling)
    {
        var (term, held) = Spellings[spelling];
        using var document = PdfDocument.Open(TextAttachment(Value(held)));

        var report = document.RedactText(term, RedactionOptions.ForProfile(profile) with { KeepAttachments = true });

        report.Attachments.Should().ContainSingle().Which.Disposition.Should().Be(AttachmentDisposition.KeptTermRemoved);
        var saved = document.SaveToBytes();
        SavedPdfLeakScanner.FindTerm(saved, held).Should().BeEmpty();
        SavedPdfLeakScanner.FindTerm(saved, term).Should().BeEmpty();
        SavedPdfLeakScanner.FindTerm(saved, "Wren").Should().NotBeEmpty("the rest of the text is kept");
    }
}
