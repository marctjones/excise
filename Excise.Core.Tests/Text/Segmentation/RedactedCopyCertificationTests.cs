using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Redaction;
using Excise.Core.Text.Segmentation;
using Excise.TestSupport;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// #2042: a redacted copy is a full rewrite, so the catalog's <c>/Perms</c> DocMDP and UR3 signatures
/// (and the <c>/Legal</c> attestation) can no longer verify. Every profile removes them through the
/// option flag <see cref="RedactionOptions.RemoveVoidedCertification"/> (not the profile label) and
/// reports it. Absence is read with the inflating byte scanner, which sees every signer marker.
/// </summary>
public class RedactedCopyCertificationTests
{
    private static readonly string[] Markers =
    {
        CertifiedFormFixtures.DocMdpSigner, CertifiedFormFixtures.Ur3Signer, CertifiedFormFixtures.LegalMarker,
        CertifiedFormFixtures.SignatureAppearanceMarker,
    };

    private static readonly PdfRectangle WholePage = new(0, 0, 612, 792);

    private static (byte[] Input, string Token) Certified(bool certify = true)
    {
        var trap = CarrierTrapFixtures.Get("pieceinfo");
        var plain = trap.Build(true);
        return (certify ? CertifiedFormFixtures.Certify(plain) : plain, trap.Token);
    }

    private static void AssertNoCertification(byte[] saved)
    {
        foreach (var marker in Markers)
            SavedPdfLeakScanner.FindTerm(saved, marker).Should().BeEmpty(marker);
        var all = SavedPdfLeakScanner.AllCarriersText(saved);
        all.Should().NotContain("/DocMDP").And.NotContain("/UR3");
        using var reopened = PdfDocument.Open(saved);
        reopened.Catalog.ContainsKey("Perms").Should().BeFalse();
        reopened.Catalog.ContainsKey("Legal").Should().BeFalse();
    }

    private static bool Reports(RedactionReport report) =>
        report.Removals.Any(r => r.Feature.Contains("certification", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void PositiveControl_TheInputCarriesEveryMarker()
    {
        var (input, _) = Certified();
        foreach (var marker in Markers)
            SavedPdfLeakScanner.FindTerm(input, marker).Should().NotBeEmpty(marker);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RedactText_RemovesTheCertification_AndReportsIt(bool maximum)
    {
        var (input, token) = Certified();
        using var doc = PdfDocument.Open(input);
        var options = maximum ? RedactionOptions.Maximum : RedactionOptions.Default;
        var report = doc.RedactText(token, options);
        AssertNoCertification(doc.SaveToBytes());
        Reports(report).Should().BeTrue("a removal the caller did not ask for is named");
    }

    [Fact]
    public void RedactArea_RemovesTheCertification_AndReportsIt()
    {
        var (input, _) = Certified();
        using var doc = PdfDocument.Open(input);
        var report = doc.GetPage(1).RedactAreaWithReport(WholePage, RedactionOptions.Default with { DrawBox = false });
        AssertNoCertification(doc.SaveToBytes());
        Reports(report).Should().BeTrue();
    }

    [Fact]
    public void SequentialRedactions_ReportTheCertificationOnce()
    {
        var (input, token) = Certified();
        using var doc = PdfDocument.Open(input);
        var first = doc.RedactText(token, RedactionOptions.Default);
        var second = doc.RedactText(token, RedactionOptions.Default);
        var third = doc.GetPage(1).RedactAreaWithReport(WholePage, RedactionOptions.Default);
        Reports(first).Should().BeTrue();
        Reports(second).Should().BeFalse("the certification is already gone");
        Reports(third).Should().BeFalse();
    }

    [Fact]
    public void UncertifiedDocument_ProducesNoCertificationRow()
    {
        var (input, token) = Certified(certify: false);
        using var doc = PdfDocument.Open(input);
        Reports(doc.RedactText(token, RedactionOptions.Default)).Should().BeFalse();
    }

    [Fact]
    public void FlagOff_KeepsTheCertification_SoTheFlagIsWhatDecides()
    {
        var (input, token) = Certified();
        using var doc = PdfDocument.Open(input);
        var report = doc.RedactText(token, RedactionOptions.Default with { RemoveVoidedCertification = false });
        SavedPdfLeakScanner.FindTerm(doc.SaveToBytes(), CertifiedFormFixtures.DocMdpSigner).Should().NotBeEmpty(
            "planted failure: with the flag off the certification survives, so the scanner and fixture work");
        Reports(report).Should().BeFalse();
    }

    [Fact]
    public void Maximum_RemovesPerms_AndLegal_AndTheSignerData()
    {
        var (input, token) = Certified();
        using var doc = PdfDocument.Open(input);
        doc.RedactText(token, RedactionOptions.Maximum);
        AssertNoCertification(doc.SaveToBytes());
    }

    [Fact]
    public void FullRewrite_DoesNotEmbedTheSignedOriginal()
    {
        var (input, token) = Certified();
        using var doc = PdfDocument.Open(input);
        doc.RedactText(token, RedactionOptions.Default);
        var saved = doc.SaveToBytes();
        SavedPdfLeakScanner.FindTerm(saved, token).Should().BeEmpty("no incremental update keeps the signed original bytes");
        System.Text.Encoding.Latin1.GetString(saved).Split("%%EOF").Length.Should().BeLessThanOrEqualTo(2);
    }
}
