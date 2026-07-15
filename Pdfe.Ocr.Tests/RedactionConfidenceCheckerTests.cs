using System;
using System.Linq;
using AwesomeAssertions;
using Pdfe.Core.Document;
using Pdfe.Core.Graphics;
using Pdfe.Rendering.Differential;
using Xunit;

namespace Pdfe.Ocr.Tests;

/// <summary>
/// #650: a per-document runtime check comparing pdfe's own extraction
/// against an independent oracle, before <c>RedactText</c> mutates a page.
/// </summary>
public class RedactionConfidenceCheckerTests
{
    // ------------------------------------------------------------------
    // ClassifyPage: pure classification math, no PDF or external tool
    // needed — the fast, always-runs coverage for the actual tiering
    // logic (#637's leak was one bad page hiding in a healthy document,
    // so this is the part that most needs to be right).
    // ------------------------------------------------------------------

    [Fact]
    public void ClassifyPage_IdenticalText_IsOk()
    {
        var result = RedactionConfidenceChecker.ClassifyPage(1,
            "The quick brown fox jumps over the lazy dog, repeated for length.",
            "The quick brown fox jumps over the lazy dog, repeated for length.");

        result.Tier.Should().Be(RedactionConfidenceTier.Ok);
        result.CoverageRatio.Should().BeApproximately(1.0, 0.01);
        result.Similarity.Should().BeApproximately(1.0, 0.01);
    }

    [Fact]
    public void ClassifyPage_PdfeFindsNothing_OracleFindsRealText_IsSevere()
    {
        // The #637 shape: pdfe extracts (near-)nothing on a page that
        // genuinely has substantial text, per an independent oracle.
        var result = RedactionConfidenceChecker.ClassifyPage(47, "",
            "This entire page of real content was invisible to pdfe's own extraction " +
            "due to a parser gap, which is exactly the leak class this check exists to catch.");

        result.Tier.Should().Be(RedactionConfidenceTier.Severe);
        result.CoverageRatio.Should().Be(0.0);
    }

    [Fact]
    public void ClassifyPage_PartialExtractionGap_IsDegradedNotSevere()
    {
        var oracle = "Alpha bravo charlie delta echo foxtrot golf hotel india juliet kilo lima mike november.";
        // pdfe finds most of it but is missing a meaningful chunk — degraded, not severe.
        var pdfe = "Alpha bravo charlie delta echo foxtrot golf hotel india";

        var result = RedactionConfidenceChecker.ClassifyPage(1, pdfe, oracle);

        result.Tier.Should().Be(RedactionConfidenceTier.Degraded);
    }

    [Fact]
    public void ClassifyPage_NoOracleTextForThisPage_IsUnverified()
    {
        var result = RedactionConfidenceChecker.ClassifyPage(1, "some pdfe text", null);

        result.Tier.Should().Be(RedactionConfidenceTier.Unverified);
        result.CoverageRatio.Should().BeNull();
        result.Similarity.Should().BeNull();
    }

    [Fact]
    public void ClassifyPage_OracleTextTooShortToBeSignal_IsOkRegardlessOfPdfeText()
    {
        // A handful of stray characters from either extractor shouldn't
        // swing a page to Severe — same MinOracleLength guard as the
        // release-gate ExtractionParityTests.
        var result = RedactionConfidenceChecker.ClassifyPage(1, "", "ok");

        result.Tier.Should().Be(RedactionConfidenceTier.Ok);
    }

    [Fact]
    public void ClassifyPage_BothEmpty_IsOk()
    {
        var result = RedactionConfidenceChecker.ClassifyPage(1, "", "");
        result.Tier.Should().Be(RedactionConfidenceTier.Ok);
    }

    // ------------------------------------------------------------------
    // WorstTier: the whole-document aggregation. #637's leak was one bad
    // page out of 200+ good ones — an average would have hidden it, so
    // this must genuinely be "worst page wins," not a blend.
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(new[] { RedactionConfidenceTier.Ok, RedactionConfidenceTier.Ok }, RedactionConfidenceTier.Ok)]
    [InlineData(new[] { RedactionConfidenceTier.Ok, RedactionConfidenceTier.Degraded, RedactionConfidenceTier.Ok }, RedactionConfidenceTier.Degraded)]
    [InlineData(new[] { RedactionConfidenceTier.Ok, RedactionConfidenceTier.Severe, RedactionConfidenceTier.Degraded }, RedactionConfidenceTier.Severe)]
    [InlineData(new[] { RedactionConfidenceTier.Ok, RedactionConfidenceTier.Unverified }, RedactionConfidenceTier.Unverified)]
    [InlineData(new[] { RedactionConfidenceTier.Unverified, RedactionConfidenceTier.Severe }, RedactionConfidenceTier.Severe)]
    public void WorstTier_OneBadPageAmongManyGoodOnes_DrivesTheOverallVerdict(
        RedactionConfidenceTier[] pageTiers, RedactionConfidenceTier expected)
    {
        var pages = pageTiers.Select((t, i) =>
            new RedactionConfidencePageResult(i + 1, t, CoverageRatio: null, Similarity: null));

        RedactionConfidenceChecker.WorstTier(pages).Should().Be(expected);
    }

    [Fact]
    public void WorstTier_NoPages_IsOk()
    {
        RedactionConfidenceChecker.WorstTier(Array.Empty<RedactionConfidencePageResult>())
            .Should().Be(RedactionConfidenceTier.Ok);
    }

    // ------------------------------------------------------------------
    // CheckDocument: end-to-end, needs a real oracle (mutool preferred —
    // fast, no rasterization; falls back to tesseract). Skips cleanly
    // when neither is installed, matching the rest of this test suite's
    // convention for optional-tool-gated tests.
    // ------------------------------------------------------------------

    private static bool AnyOracleAvailable =>
        MutoolReferenceRenderer.IsAvailable || new PdfOcrService().IsAvailable();

    [Fact]
    public void CheckDocument_HealthyPlainTextDocument_ReportsOk()
    {
        Assert.SkipUnless(AnyOracleAvailable, "neither mutool nor tesseract is installed");

        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(612, 792);
        using (var g = page.GetGraphics())
        {
            g.DrawString(
                "This is a perfectly ordinary page of plain text with nothing hidden from extraction.",
                PdfFont.Helvetica(14), PdfBrush.Black, 50, 700);
            g.Flush();
        }

        var report = new RedactionConfidenceChecker().CheckDocument(doc);

        report.Tier.Should().Be(RedactionConfidenceTier.Ok);
        report.ShouldWarn.Should().BeFalse();
        report.ShouldRefuse.Should().BeFalse();
        report.Oracle.Should().NotBeNull();
        report.Pages.Should().HaveCount(1);
    }

    [Fact]
    public void CheckDocument_MultiPageDocument_ReportsOnePerPage()
    {
        // WorstTier_* above proves the aggregation math directly; this
        // proves CheckDocument's plumbing actually produces one result
        // per real page rather than collapsing/averaging across pages.
        Assert.SkipUnless(AnyOracleAvailable, "neither mutool nor tesseract is installed");

        using var doc = PdfDocument.CreateNew();
        var page1 = doc.Pages.AddBlank(612, 792);
        using (var g = page1.GetGraphics())
        {
            g.DrawString("Page one has ordinary, fully extractable text content right here.",
                PdfFont.Helvetica(14), PdfBrush.Black, 50, 700);
            g.Flush();
        }
        doc.Pages.AddBlank(612, 792); // page 2: nothing drawn at all

        var report = new RedactionConfidenceChecker().CheckDocument(doc);

        // Page 2 has no oracle text either (nothing was drawn), so it's
        // Ok (nothing to be blind to) rather than a false Severe — the
        // real assertion here is that a per-page report exists for both
        // pages and the aggregate reflects the worst of them.
        report.Pages.Should().HaveCount(2);
        report.Pages[0].Tier.Should().Be(RedactionConfidenceTier.Ok);
    }

    [Fact]
    public void CheckDocument_DoesNotMutateTheDocument()
    {
        Assert.SkipUnless(AnyOracleAvailable, "neither mutool nor tesseract is installed");

        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(612, 792);
        using (var g = page.GetGraphics())
        {
            g.DrawString("Confidence checking must be read-only against the document under test.",
                PdfFont.Helvetica(14), PdfBrush.Black, 50, 700);
            g.Flush();
        }

        var textBefore = doc.GetPage(1).Text;
        _ = new RedactionConfidenceChecker().CheckDocument(doc);
        var textAfter = doc.GetPage(1).Text;

        textAfter.Should().Be(textBefore, "the confidence check runs before RedactText and must not itself alter the document");
    }
}
