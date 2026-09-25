using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Operations;
using Excise.Core.Text.Segmentation;
using Excise.TestSupport;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// #1854: a marked-content property list whose <c>/ActualText</c>, <c>/Alt</c>
/// or <c>/E</c> holds the term over glyphs that do NOT spell it. The glyph pass
/// finds nothing to remove, so neither enclosure nor the removed-text match
/// reaches the carrier (#1182), and mutool and pdftotext read the term as the
/// page's text. Only a scrub by TERM, like the structure-tree carrier's, closes
/// it.
/// </summary>
/// <remarks>
/// Every assertion reads the SAVED bytes through <see cref="SavedPdfLeakScanner"/>,
/// which inflates streams itself; the independent extractors are in
/// <c>MarkedContentActualTextOracleTests</c> (Excise.Rendering.Tests).
/// </remarks>
public class MarkedContentTermCarrierTests
{
    private const string Term = "KESTREL";
    private const string Arabic = "سلام";
    private const string Rest = "chapter";
    private const string CarrierRow = "marked-content /ActualText, /Alt, /E";

    public static TheoryData<string, string, RedactionProfile, bool> Encodings()
    {
        var data = new TheoryData<string, string, RedactionProfile, bool>();
        foreach (var (term, encoding) in new[]
                 {
                     (Term, "literal"), (Term, "utf16-hex"), (Term, "utf16-octal"),
                     (Arabic, "utf16-hex"), (Arabic, "utf16-octal"),
                 })
            foreach (var profile in new[] { RedactionProfile.Standard, RedactionProfile.Maximum })
                foreach (var viaRedactText in new[] { true, false })
                    data.Add(term, encoding, profile, viaRedactText);
        return data;
    }

    public static TheoryData<string, RedactionProfile, bool> Placements()
    {
        var data = new TheoryData<string, RedactionProfile, bool>();
        foreach (var placement in MarkedContentCarrierFixtures.Placements.Keys)
            foreach (var profile in new[] { RedactionProfile.Standard, RedactionProfile.Maximum })
                foreach (var viaRedactText in new[] { true, false })
                    data.Add(placement, profile, viaRedactText);
        return data;
    }

    [Theory]
    [MemberData(nameof(Encodings))]
    public void TheTermLeavesTheCarrier_WhateverItsEncoding(
        string term, string encoding, RedactionProfile profile, bool viaRedactText)
    {
        var value = $"{term} {Rest}";
        using var document = PdfDocument.Open(MarkedContentCarrierFixtures.Inline(encoding switch
        {
            "literal" => MarkedContentCarrierFixtures.Literal(value),
            "utf16-hex" => MarkedContentCarrierFixtures.Utf16Hex(value),
            _ => MarkedContentCarrierFixtures.Utf16Octal(value),
        }));

        Redact(document, term, profile, viaRedactText);
        var saved = document.SaveToBytes();

        SavedPdfLeakScanner.FindTerm(saved, term).Should().BeEmpty(
            "the span's /ActualText is the page's text to every extractor, whatever the glyphs paint");
        if (profile == RedactionProfile.Standard)
            SavedPdfLeakScanner.FindTerm(saved, Rest).Should().NotBeEmpty(
                "Standard cuts the term out of the value and keeps the rest, like every other text carrier");
        else
            SavedPdfLeakScanner.FindTerm(saved, Rest).Should().BeEmpty(
                "Maximum drops the whole value the term was in");
    }

    [Theory]
    [MemberData(nameof(Placements))]
    public void TheTermLeavesTheCarrier_WhereverThePropertyListSits(
        string placement, RedactionProfile profile, bool viaRedactText)
    {
        using var document = PdfDocument.Open(
            MarkedContentCarrierFixtures.Placements[placement](MarkedContentCarrierFixtures.Literal($"{Term} {Rest}")));

        Redact(document, Term, profile, viaRedactText);
        var saved = document.SaveToBytes();

        SavedPdfLeakScanner.FindTerm(saved, Term).Should().BeEmpty(
            $"{placement}: a property list is a text carrier wherever marked content can occur");
        using var reopened = PdfDocument.Open(saved);
        reopened.GetPage(1).Letters.Select(l => l.Value).Should().ContainInOrder(
            MarkedContentCarrierFixtures.Painted.Where(c => c != ' ').Select(c => c.ToString()),
            "the painted glyphs never held the term and must survive");
    }

    [Fact]
    public void RedactText_ReportsTheCarrierScrubbed()
    {
        using var document = PdfDocument.Open(
            MarkedContentCarrierFixtures.Inline(MarkedContentCarrierFixtures.Literal($"{Term} {Rest}")));

        var report = document.RedactText(Term, RedactionOptions.Default);

        report.Carriers.Should().ContainSingle(c => c.Carrier == CarrierRow)
            .Which.Scrubbed.Should().BeTrue();
        report.IsCleanSuccess.Should().BeTrue();
        SavedPdfLeakScanner.FindTerm(document.SaveToBytes(), Term).Should().BeEmpty();
    }

    [Fact]
    public void ScrubTerms_ReportsTheCarrierModified()
    {
        using var document = PdfDocument.Open(
            MarkedContentCarrierFixtures.Inline(MarkedContentCarrierFixtures.Literal($"{Term} {Rest}")));

        var outcome = PdfDocumentSanitizer.ScrubTerms(
            document, new[] { Term }, caseSensitive: false, RedactionCarriers.All, CarrierScrubPolicy.Default);

        outcome.Changed.Should().BeTrue();
        outcome.For(RedactionCarriers.MarkedContent).Should().BeEquivalentTo(
            new CarrierScrubResult(RedactionCarriers.MarkedContent, CarrierScrubMode.Strip, TermFound: true, Modified: true));
        SavedPdfLeakScanner.FindTerm(document.SaveToBytes(), Term).Should().BeEmpty();
    }

    [Fact]
    public void ReportOnly_KeepsTheValueAndSaysItHoldsTheTerm()
    {
        using var document = PdfDocument.Open(
            MarkedContentCarrierFixtures.Inline(MarkedContentCarrierFixtures.Literal($"{Term} {Rest}")));

        var report = document.RedactText(Term, RedactionOptions.Default with
        {
            CarrierPolicy = CarrierScrubPolicy.Default.With(RedactionCarriers.MarkedContent, CarrierScrubMode.ReportOnly),
        });

        report.Carriers.Should().ContainSingle(c => c.Carrier == CarrierRow)
            .Which.RefusedReason.Should().Contain("HOLDS THE TERM");
        report.IsCleanSuccess.Should().BeFalse();
        SavedPdfLeakScanner.FindTerm(document.SaveToBytes(), Term).Should().NotBeEmpty();
    }

    [Fact]
    public void AContentStreamThatCannotBeDecoded_IsReportedNotSkipped()
    {
        // Listed in the resources, not drawn: the page still extracts, and the
        // form is in the file for any tool that can decode it.
        using var document = PdfDocument.Open(Excise.Core.Tests.Content.ContentStreamFixture.Build(
            $"BT /F1 12 Tf 72 700 Td ({MarkedContentCarrierFixtures.Painted}) Tj ET",
            extraObjects: "6 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Filter /NoSuchDecode /Length 3 >>\n"
                + "stream\nabc\nendstream\nendobj\n",
            extraResources: "/XObject << /Fm0 6 0 R >>"));

        var report = document.RedactText(Term, RedactionOptions.Default);

        report.Carriers.Should().ContainSingle(c => c.Carrier == CarrierRow)
            .Which.RefusedReason.Should().Contain("could not be read");
        report.IsCleanSuccess.Should().BeFalse(
            "a content stream excise could not read may hold the term in a property list");
    }

    [Fact]
    public void AValueWithoutTheTerm_IsLeftByteForByte()
    {
        var bytes = MarkedContentCarrierFixtures.Inline(MarkedContentCarrierFixtures.Literal("Harrier chapter"));
        using var document = PdfDocument.Open(bytes);
        using var original = PdfDocument.Open(bytes);

        var outcome = PdfDocumentSanitizer.ScrubTerms(
            document, new[] { Term }, caseSensitive: false, RedactionCarriers.All, CarrierScrubPolicy.Default);

        outcome.For(RedactionCarriers.MarkedContent)!.Modified.Should().BeFalse();
        document.GetPage(1).GetContentStreamBytes().Should().Equal(
            original.GetPage(1).GetContentStreamBytes(),
            "a stream whose carriers do not hold the term is not rewritten");
    }

    /// <summary>
    /// CLAUDE.md rule 4, planted: with the carrier switched off the term
    /// survives, and the two readers that do not share the scrub's walk (the
    /// safety pass's verification and the unredact audit) must both say so.
    /// With it on, both must be clean.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheVerificationSide_SeesTheCarrier_ThroughItsOwnWalk(bool scrubbed)
    {
        var options = RedactionOptions.Default with
        {
            Carriers = scrubbed ? RedactionCarriers.All : RedactionCarriers.All & ~RedactionCarriers.MarkedContent,
        };
        foreach (var placement in MarkedContentCarrierFixtures.Placements.Keys)
        {
            using var document = PdfDocument.Open(
                MarkedContentCarrierFixtures.Placements[placement](MarkedContentCarrierFixtures.Literal($"{Term} {Rest}")));
            document.RedactText(Term, options);

            var safety = RedactedCopySafetyPolicy.Evaluate(
                document, RedactedCopySafetyRequest.ForTerms(new[] { Term }, options));
            var saved = document.SaveToBytes();
            // The audit reads the file a recipient gets, as `excise unredact` does.
            using var shipped = PdfDocument.Open(saved);
            var audit = CarrierTextRecovery.Scan(shipped)
                .Where(f => f.Text.Contains(Term, StringComparison.Ordinal)).ToList();

            if (scrubbed)
            {
                safety.RemainingTermCount.Should().Be(0, placement);
                audit.Should().BeEmpty(placement);
                SavedPdfLeakScanner.FindTerm(saved, Term).Should().BeEmpty(placement);
            }
            else
            {
                SavedPdfLeakScanner.FindTerm(saved, Term).Should().NotBeEmpty(
                    $"{placement}: the planted failure — the carrier is switched off");
                safety.RemainingTermCount.Should().Be(1, $"{placement}: the safety pass must see the surviving carrier");
                audit.Should().Contain(f => f.Carrier.StartsWith("marked-content /", StringComparison.Ordinal),
                    $"{placement}: the unredact audit must read the surviving carrier");
            }
        }
    }

    private static void Redact(PdfDocument document, string term, RedactionProfile profile, bool viaRedactText)
    {
        var options = RedactionOptions.ForProfile(profile);
        if (viaRedactText)
            document.RedactText(term, options);
        else
            PdfDocumentSanitizer.ScrubTerms(
                document, new[] { term }, caseSensitive: false, RedactionCarriers.All, options.CarrierPolicy);
    }
}
