using System;
using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Operations;
using Excise.Core.Tests.Content;
using Excise.Core.Text.Segmentation;
using Excise.TestSupport;
using Xunit;

namespace Excise.Core.Tests.Redaction;

/// <summary>
/// #1836 rule 4: pins what each bool-flag <c>RedactText</c> / <c>RedactArea(s)</c>
/// overload did, against the <see cref="RedactionOptions"/> that replaces it, so
/// deleting the overloads changes nothing a caller can observe. The two families
/// are NOT alike: <c>RedactText</c> built the Standard profile itself, so its
/// mapping is exact; the area overloads skipped the profile pass and drew no box,
/// so their mapping needs <c>DrawBox = false</c> and is exact only on a document
/// that carries nothing the profile removes (this fixture).
/// </summary>
public sealed class RedactionOverloadMappingTests
{
    private const string Term = "SECRET";

    private static byte[] Pdf() => ContentStreamFixture.Build(
        "BT /F1 12 Tf 72 700 Td (Louise SECRET Anne Secret and SECRETIVE more) Tj ET\n");

    private static byte[] Masked(PdfDocument doc) => SavedPdfLeakScanner.MaskFileIdentifier(doc.SaveToBytes());

    public static TheoryData<string> TextCases() => new()
    {
        "defaults", "closeGapNoBox", "overshootColoured", "wholeWordContained", "carrierScope",
    };

    [Theory]
    [MemberData(nameof(TextCases))]
    public void RedactText_BoolOverload_EqualsTheMappedOptions(string name)
    {
        var (old, options) = name switch
        {
            "defaults" => ((Func<PdfDocument, RedactionReport>)(d => d.RedactText(Term)),
                RedactionOptions.Default),
            "closeGapNoBox" => (d => d.RedactText(Term, drawBlackRect: false, closeWidth: true),
                RedactionOptions.Default with { DrawBox = false, Width = WidthPolicy.CloseGap }),
            "overshootColoured" => (d => d.RedactText(Term, caseSensitive: true, overshootBox: true,
                    boxColor: (1.0, 0.0, 0.0)),
                RedactionOptions.Default with
                {
                    CaseSensitive = true,
                    Width = WidthPolicy.OvershootPreserveLayout,
                    BoxColor = (1.0, 0.0, 0.0),
                }),
            "wholeWordContained" => (d => d.RedactText(Term, strategy: GlyphRemovalStrategy.FullyContained,
                    includeHiddenLayers: false, scrubDocumentCarriers: false, wholeWord: true),
                RedactionOptions.Default with
                {
                    Strategy = GlyphRemovalStrategy.FullyContained,
                    IncludeHiddenLayers = false,
                    ScrubDocumentCarriers = false,
                    WholeWord = true,
                }),
            _ => (d => d.RedactText(Term,
                    carriers: RedactionCarriers.All & ~RedactionCarriers.Outlines,
                    carrierPolicy: CarrierScrubPolicy.Default.With(
                        RedactionCarriers.Annotations, CarrierScrubMode.ReportOnly)),
                RedactionOptions.Default with
                {
                    Carriers = RedactionCarriers.All & ~RedactionCarriers.Outlines,
                    CarrierPolicy = CarrierScrubPolicy.Default.With(
                        RedactionCarriers.Annotations, CarrierScrubMode.ReportOnly),
                }),
        };

        using var a = PdfDocument.Open(Pdf());
        var oldReport = old(a);
        var oldBytes = Masked(a);

        using var b = PdfDocument.Open(Pdf());
        var newReport = b.RedactText(Term, options);
        var newBytes = Masked(b);

        newBytes.Should().Equal(oldBytes, "the options overload must write the same file");
        newReport.VerifiedRemovals.Should().Be(oldReport.VerifiedRemovals);
        newReport.MatchesLocated.Should().Be(oldReport.MatchesLocated);
        newReport.Carriers.Count.Should().Be(oldReport.Carriers.Count);
        newReport.Removals.Count.Should().Be(oldReport.Removals.Count);
        newReport.WholeWord.Should().Be(oldReport.WholeWord);
        oldReport.VerifiedRemovals.Should().BeGreaterThan(0, "the fixture holds the term");
        SavedPdfLeakScanner.FindTerm(newBytes, "Louise SECRET").Should().BeEmpty();
    }

    public static TheoryData<string> AreaCases() => new() { "defaults", "closeWidthNoScrub", "containedOverTwoAreas" };

    [Theory]
    [MemberData(nameof(AreaCases))]
    public void RedactArea_BoolOverload_EqualsTheMappedOptionsWithNoBox(string name)
    {
        var area = new PdfRectangle(70, 690, 300, 716);
        var second = new PdfRectangle(300, 690, 480, 716);
        var (old, options) = name switch
        {
            "defaults" => ((Action<PdfPage>)(p => p.RedactArea(area)),
                RedactionOptions.Default with { DrawBox = false }),
            "closeWidthNoScrub" => (p => p.RedactArea(area, scrubDocumentCarriers: false, closeWidth: true),
                RedactionOptions.Default with
                {
                    DrawBox = false,
                    Width = WidthPolicy.CloseGap,
                    ScrubDocumentCarriers = false,
                    KeepAttachments = true,   // the bool overload removed attachments only when it scrubbed
                }),
            _ => (p => p.RedactAreas(new[] { area, second }, GlyphRemovalStrategy.FullyContained),
                RedactionOptions.Default with { DrawBox = false, Strategy = GlyphRemovalStrategy.FullyContained }),
        };

        using var a = PdfDocument.Open(Pdf());
        old(a.GetPage(1));
        var oldBytes = Masked(a);

        using var b = PdfDocument.Open(Pdf());
        if (name == "containedOverTwoAreas")
            b.GetPage(1).RedactAreas(new[] { area, second }, options);
        else
            b.GetPage(1).RedactArea(area, options);
        var newBytes = Masked(b);

        newBytes.Should().Equal(oldBytes, "the options overload with no box must write the same file");
        SavedPdfLeakScanner.FindTerm(newBytes, "Louise SECRET").Should().BeEmpty();
    }

    /// <summary>
    /// The bool overload removed attachments only when it also scrubbed carriers
    /// (<c>removeAttachments: scrubDocumentCarriers</c>); the options overload removes
    /// them unless <c>KeepAttachments</c>. The mapping is therefore
    /// <c>KeepAttachments = !scrub</c>, pinned against the six-route attachment fixture.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RedactArea_BoolOverload_AttachmentRemovalFollowsTheScrubFlag(bool scrub)
    {
        var area = new PdfRectangle(70, 690, 300, 716);
        var pdf = Text.Segmentation.AttachmentRedactionTests.BuildAllRoutesPdf();

        using var a = PdfDocument.Open(pdf);
        a.GetPage(1).RedactArea(area, scrubDocumentCarriers: scrub);
        var oldSaved = a.SaveToBytes();

        using var b = PdfDocument.Open(pdf);
        b.GetPage(1).RedactArea(area, RedactionOptions.Default with
        {
            DrawBox = false,
            ScrubDocumentCarriers = scrub,
            KeepAttachments = !scrub,
        });
        var newSaved = b.SaveToBytes();

        using var oldReopened = PdfDocument.Open(oldSaved);
        using var newReopened = PdfDocument.Open(newSaved);
        newReopened.GetEmbeddedFiles().Count.Should().Be(oldReopened.GetEmbeddedFiles().Count);
        (oldReopened.GetEmbeddedFiles().Count == 0).Should().Be(scrub,
            "attachments go exactly when the bool overload scrubbed carriers");
        SavedPdfLeakScanner.FindTerm(newSaved, Text.Segmentation.AttachmentRedactionTests.DocSecret)
            .Should().HaveCount(SavedPdfLeakScanner.FindTerm(oldSaved, Text.Segmentation.AttachmentRedactionTests.DocSecret).Count);
        if (scrub)
            SavedPdfLeakScanner.FindTerm(newSaved, Text.Segmentation.AttachmentRedactionTests.DocSecret).Should().BeEmpty();
    }
}
