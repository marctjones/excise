using System.Linq;
using AwesomeAssertions;
using Excise.Core.Authoring;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Excise.Core.Tests.Fixtures;
using Excise.Core.Validation;
using Xunit;

namespace Excise.Core.Tests.Validation;

/// <summary>
/// Verifies <see cref="PdfAStructuralValidator"/> against excise's own PDF/A
/// emitter: what <see cref="PdfDocumentBuilder.PdfA"/> writes must round-trip
/// (save → reparse) as structurally conformant, and a document missing a marker
/// must be flagged.
/// </summary>
public class PdfAStructuralValidatorTests
{
    private static byte[] EmitPdfA(PdfAConformance conformance)
    {
        var font = PdfFont.FromTrueType(TestFontFixtures.LoadDejaVuSansBytes(), 11);
        return PdfDocumentBuilder.Create()
            .Language("en-US").Title("Archival Test").DefaultFont(font)
            .PdfA(conformance)
            .Heading("Archival Test")
            .Paragraph("Body text with unicode: café.")
            .SaveToBytes();
    }

    [Theory]
    [InlineData(PdfAConformance.PdfA1B)]
    [InlineData(PdfAConformance.PdfA2B)]
    public void EmittedPdfA_RoundTrips_AsStructurallyConformant(PdfAConformance conformance)
    {
        var doc = PdfDocument.Open(EmitPdfA(conformance));
        var report = PdfAStructuralValidator.Validate(doc, conformance);

        report.CheckedSubsetConformant.Should().BeTrue(
            "excise's own PDF/A output must carry the required document-level markers. Report:\n" + report);
        Status(report, "A-XmpPdfaId").Should().Be(RuleStatus.Pass);
        Status(report, "A-OutputIntent").Should().Be(RuleStatus.Pass);
        Status(report, "A-TrailerId").Should().Be(RuleStatus.Pass);
        report.UncoveredCheckpoints.Should().NotBeEmpty();
    }

    [Fact]
    public void WrongPartNumber_IsFlagged()
    {
        // A file emitted as PDF/A-2 checked against the -1 expectation must fail
        // the pdfaid part rule.
        var doc = PdfDocument.Open(EmitPdfA(PdfAConformance.PdfA2B));
        Status(PdfAStructuralValidator.Validate(doc, PdfAConformance.PdfA1B), "A-XmpPdfaId")
            .Should().Be(RuleStatus.Fail);
    }

    [Fact]
    public void MissingOutputIntent_IsFlagged()
    {
        var doc = PdfDocument.Open(EmitPdfA(PdfAConformance.PdfA2B));
        doc.Catalog.Remove("OutputIntents");
        var report = PdfAStructuralValidator.Validate(doc, PdfAConformance.PdfA2B);
        Status(report, "A-OutputIntent").Should().Be(RuleStatus.Fail);
        report.CheckedSubsetConformant.Should().BeFalse();
    }

    private static RuleStatus Status(ValidationReport report, string ruleId) =>
        report.Results.Single(r => r.RuleId == ruleId).Status;
}
