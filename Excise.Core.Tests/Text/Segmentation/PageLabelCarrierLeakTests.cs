using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Operations;
using Excise.Core.Text.Segmentation;
using Excise.TestSupport;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// #1853: a term in a page-label prefix (<c>/PageLabels /Nums [0 &lt;&lt; /P (…) &gt;&gt;]</c>,
/// §12.4.2) survived <c>RedactText</c> and <c>ScrubTerms</c>, and the report had no
/// row for it. A viewer shows the prefix in its page-number box. The saved bytes
/// are read by <see cref="SavedPdfLeakScanner"/>, which decodes every string object.
/// </summary>
public class PageLabelCarrierLeakTests
{
    private const string Latin = "KESTREL";
    private const string Arabic = "سلام";

    public enum Entry { RedactText, ScrubTerms }

    private static string Utf16Hex(string text) =>
        "<FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(text)) + ">";

    private static string Utf16Octal(string text) =>
        @"(\376\377" + string.Concat(Encoding.BigEndianUnicode.GetBytes(text)
            .Select(b => "\\" + Convert.ToString(b, 8).PadLeft(3, '0'))) + ")";

    /// <summary>The prefix fixture, its term, and the prefix Strip must leave.</summary>
    private static (byte[] Pdf, string Term, string Remainder) Fixture(string shape) => shape switch
    {
        "literal" => (Labels($"<< /S /D /P (Annex {Latin} chapter) >>"), Latin, "Annex  chapter"),
        "utf16-hex" => (Labels($"<< /S /D /P {Utf16Hex($"Annex {Latin} chapter")} >>"), Latin, "Annex  chapter"),
        "utf16-octal" => (Labels($"<< /S /D /P {Utf16Octal($"Annex {Latin} chapter")} >>"), Latin, "Annex  chapter"),
        "arabic" => (Labels($"<< /S /D /P {Utf16Hex($"{Arabic} chapter")} >>"), Arabic, " chapter"),
        "partial-word" => (Labels($"<< /S /D /P (X{Latin}Y-) >>"), Latin, "XY-"),
        "indirect" => (CarrierTrapFixtures.WithCatalog("/PageLabels << /Nums [0 << /S /D /P 6 0 R >>] >>",
            extra: $"(Annex {Latin} chapter)"), Latin, "Annex  chapter"),
        // A number tree with /Kids: the label sits in a leaf, not the root.
        "kids" => (CarrierTrapFixtures.WithCatalog("/PageLabels 6 0 R", extra: new[]
        {
            "<< /Kids [7 0 R] >>",
            $"<< /Limits [0 0] /Nums [0 << /S /r /P (Annex {Latin} chapter) >>] >>",
        }), Latin, "Annex  chapter"),
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };

    private static byte[] Labels(string label) =>
        CarrierTrapFixtures.WithCatalog($"/PageLabels << /Nums [0 {label}] >>");

    public static TheoryData<string, RedactionProfile, Entry> Matrix()
    {
        var data = new TheoryData<string, RedactionProfile, Entry>();
        foreach (var shape in new[] { "literal", "utf16-hex", "utf16-octal", "arabic", "partial-word", "indirect", "kids" })
            foreach (var profile in new[] { RedactionProfile.Standard, RedactionProfile.Maximum })
                foreach (var entry in new[] { Entry.RedactText, Entry.ScrubTerms })
                    data.Add(shape, profile, entry);
        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void PrefixTerm_IsScrubbedAndReported(string shape, RedactionProfile profile, Entry entry)
    {
        var (pdf, term, remainder) = Fixture(shape);
        SavedPdfLeakScanner.FindTerm(pdf, term).Should().NotBeEmpty("input-side control: the prefix holds the term");
        var options = RedactionOptions.ForProfile(profile);

        using var document = PdfDocument.Open(pdf);
        if (entry == Entry.RedactText)
        {
            var report = document.RedactText(term, options);
            report.Carriers.Should().ContainSingle(c => c.Carrier == "/PageLabels /P")
                .Which.Scrubbed.Should().BeTrue("the carrier is scrubbed and says so (CLAUDE.md rule 6)");
        }
        else
        {
            var outcome = PdfDocumentSanitizer.ScrubTerms(
                document, new[] { term }, caseSensitive: false, RedactionCarriers.All, options.CarrierPolicy);
            outcome.For(RedactionCarriers.PageLabels).Should().BeEquivalentTo(new
            {
                TermFound = true,
                Modified = true,
                RefusedReason = (string?)null,
            });
        }

        var saved = document.SaveToBytes();
        SavedPdfLeakScanner.FindTerm(saved, term).Should().BeEmpty($"{shape}: the prefix must not keep the term");
        using var reopened = PdfDocument.Open(saved);
        var label = PdfNumberTree.Enumerate(reopened, reopened.Catalog.GetOptional("PageLabels"))
            .Select(p => reopened.Resolve(p.Value)).OfType<Excise.Core.Primitives.PdfDictionary>().Single();
        if (profile == RedactionProfile.Maximum)
            label.ContainsKey("P").Should().BeFalse("Maximum removes the whole prefix, leaving no residue");
        else
            reopened.Resolve(label.GetOptional("P")!).Should().BeOfType<Excise.Core.Primitives.PdfString>()
                .Which.Value.Should().Be(remainder, "Strip cuts the term and keeps the rest, separators included");
        label.ContainsKey("S").Should().BeTrue("the numbering style is not text and stays");
    }

    [Fact]
    public void PrefixThatIsOnlyTheTerm_IsRemovedUnderStrip()
    {
        using var document = PdfDocument.Open(Labels($"<< /S /D /P ({Latin} ) >>"));

        PdfDocumentSanitizer.ScrubTerms(document, new[] { Latin });

        var label = PdfNumberTree.Enumerate(document, document.Catalog.GetOptional("PageLabels"))
            .Select(p => document.Resolve(p.Value)).OfType<Excise.Core.Primitives.PdfDictionary>().Single();
        label.ContainsKey("P").Should().BeFalse("a prefix left as white space carries nothing");
        document.GetPageLabel(1).Should().Be("1");
    }

    [Fact]
    public void ReportOnly_LeavesThePrefixAndSaysItHoldsTheTerm()
    {
        using var document = PdfDocument.Open(Fixture("literal").Pdf);

        var outcome = PdfDocumentSanitizer.ScrubTerms(document, new[] { Latin }, caseSensitive: false,
            RedactionCarriers.All,
            CarrierScrubPolicy.Default.With(RedactionCarriers.PageLabels, CarrierScrubMode.ReportOnly));

        outcome.NeedingAttention.Should().ContainSingle(r => r.Carrier == RedactionCarriers.PageLabels && r.TermFound);
        SavedPdfLeakScanner.FindTerm(document.SaveToBytes(), Latin).Should().NotBeEmpty();
    }

    /// <summary>
    /// The planted failure: with the carrier switched off the term stays, and the
    /// two audits that do not share the scrub's code must say so. With it on,
    /// both are clean.
    /// </summary>
    [Fact]
    public void Audits_SeeAPrefixTheScrubLeft_AndNotOneItRemoved()
    {
        var (pdf, term, _) = Fixture("utf16-hex");

        using (var leaking = PdfDocument.Open(pdf))
        {
            var report = leaking.RedactText(term,
                RedactionOptions.Default with { Carriers = RedactionCarriers.All & ~RedactionCarriers.PageLabels });
            report.Carriers.Single(c => c.Carrier == "/PageLabels /P").Scrubbed.Should().BeFalse();
            SavedPdfLeakScanner.FindTerm(leaking.SaveToBytes(), term).Should().NotBeEmpty("the planted failure");

            RedactionCarrierAudit.Inspect(leaking, new[] { term }).PageLabelPrefixCount.Should().Be(1);
            CarrierTextRecovery.Scan(leaking).Should().Contain(f =>
                f.Carrier == "page label /P" && f.Text.Contains(term, StringComparison.Ordinal));
        }

        using var scrubbed = PdfDocument.Open(pdf);
        scrubbed.RedactText(term, RedactionOptions.Default);
        RedactionCarrierAudit.Inspect(scrubbed, new[] { term }).PageLabelPrefixCount.Should().Be(0);
        CarrierTextRecovery.Scan(scrubbed).Should().NotContain(f => f.Text.Contains(term, StringComparison.Ordinal));
    }

    /// <summary>The safe-copy pass the GUI runs warns about a prefix its own scrub was told to skip.</summary>
    [Fact]
    public void SafeCopy_WarnsAboutAPrefixItsScrubSkipped_AndNotOneItScrubbed()
    {
        var (pdf, term, _) = Fixture("literal");
        static IReadOnlyList<string> Warnings(byte[] pdf, string term, RedactionCarriers carriers)
        {
            using var document = PdfDocument.Open(pdf);
            return RedactedCopySafetyPolicy.Evaluate(document, RedactedCopySafetyRequest.ForTerms(
                new[] { term }, RedactionOptions.Default with { Carriers = carriers })).Warnings;
        }

        Warnings(pdf, term, RedactionCarriers.All & ~RedactionCarriers.PageLabels)
            .Should().Contain(w => w.Contains("page-label prefix", StringComparison.Ordinal));
        Warnings(pdf, term, RedactionCarriers.All)
            .Should().NotContain(w => w.Contains("page-label prefix", StringComparison.Ordinal));
    }

    [Fact]
    public void AreaAudit_CountsEveryPrefix_AsUnexamined()
    {
        using var document = PdfDocument.Open(Labels("<< /S /D /P (Annex ) >>"));

        var audit = RedactionCarrierAudit.Inspect(document);

        audit.PageLabelPrefixCount.Should().Be(1, "an area redaction has no term to test a prefix against");
        audit.Describe().Should().Contain(line => line.Contains("page-label prefix", StringComparison.Ordinal));
    }
}
