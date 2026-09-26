using System;
using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Operations;
using Excise.Core.Text.Segmentation;
using Excise.TestSupport;
using Xunit;
using EntryPoint = Excise.Core.Tests.Text.Segmentation.RedactionProfileTests.EntryPoint;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// #1861: a signature names its signer in its dictionary (<c>/Name</c>,
/// <c>/Reason</c>, <c>/Location</c>, <c>/ContactInfo</c>) and in its certificates
/// (<c>/Contents</c>, the catalog's <c>/DSS</c>). Maximum removed the signature
/// field, but <c>/Perms /DocMDP</c> kept the dictionary and <c>/DSS</c> the
/// certificates, and the report said clean; Standard kept a redacted term in both,
/// also clean. The saved bytes are read by <see cref="SavedPdfLeakScanner"/>; qpdf
/// and mutool corroborate in <c>SignatureCarrierOracleTests</c> (Excise.Rendering.Tests).
/// </summary>
public class SignatureCarrierLeakTests
{
    private const string Signer = "SIGNERNAMETRAP";
    private const string Subject = "SIGNERCERTTRAP";
    private const string Row = "signature dictionaries and certificates";

    public enum Entry { RedactText, ScrubTerms }

    private static byte[] Signed() => CarrierTrapFixtures.Signed(null, Signer, Subject);

    [Theory]
    [InlineData(EntryPoint.RedactText)]
    [InlineData(EntryPoint.RedactArea)]
    [InlineData(EntryPoint.SafetyPass)]
    public void Maximum_RemovesTheSignatureDictionaryAndTheDss_AndReportsBoth(EntryPoint entry)
    {
        var standard = RedactionProfileTests.RunProfile(Signed(), entry, RedactionOptions.Default);
        SavedPdfLeakScanner.FindTerm(standard.Saved, Signer).Should().NotBeEmpty("planted failure: Standard keeps the signature");
        SavedPdfLeakScanner.FindTerm(standard.Saved, Subject).Should().NotBeEmpty();

        var max = RedactionProfileTests.RunProfile(Signed(), entry, RedactionOptions.Maximum);

        SavedPdfLeakScanner.FindTerm(max.Saved, Signer).Should().BeEmpty(
            "#1861: /Perms /DocMDP kept the signature dictionary, and its /Name, after the widget went");
        SavedPdfLeakScanner.FindTerm(max.Saved, Subject).Should().BeEmpty("/Contents and /DSS hold the certificate chain");
        max.Removals.Should().Contain(r => r.Feature == "signature field(s) removed" && r.Count == 1);
        max.Removals.Should().Contain(r => r.Feature == "signature dictionary(ies) removed" && r.Count == 1,
            "every removal is reported (CLAUDE.md rule 6)");
        max.Removals.Should().Contain(r => r.Feature == "/DSS document security store" && r.Count == 1);
        max.Refusals.Should().NotContain(r => r.Contains("signature dictionary") || r.Contains("/Perms"));
        using var reopened = PdfDocument.Open(max.Saved);
        reopened.Catalog.ContainsKey("Perms").Should().BeFalse("its one entry named the removed signature");
        reopened.Catalog.ContainsKey("DSS").Should().BeFalse();
    }

    /// <summary>
    /// The removal walks the object graph, not a list of places: a widget the
    /// flatten took off the page is still reached from the structure tree
    /// (<c>/OBJR</c>), and its <c>/V</c> would keep the signature (its other
    /// field carriers survive: #1881). A <c>/Perms</c>
    /// entry that is not a signature is kept and refused (CLAUDE.md rule 6).
    /// </summary>
    [Fact]
    public void Maximum_RemovesASignatureOnlyTheStructureTreeReaches_AndRefusesAPermsEntryThatIsNotOne()
    {
        var input = CarrierTrapFixtures.WithCatalog(
            "/AcroForm << /Fields [6 0 R] /SigFlags 3 >> /StructTreeRoot 8 0 R /Perms << /Custom << /Note (kept) >> >>",
            "/Annots [6 0 R] /StructParents 0",
            "<< /Type /Annot /Subtype /Widget /FT /Sig /T (Signature1) /Rect [72 100 272 140] /P 3 0 R /V 7 0 R /StructParent 1 >>",
            $"<< /Type /Sig /Filter /Adobe.PPKLite /Name ({Signer}) /ByteRange [0 0 0 0] /Contents <00> >>",
            "<< /Type /StructTreeRoot /K 9 0 R >>",
            "<< /Type /StructElem /S /Form /P 8 0 R /K << /Type /OBJR /Obj 6 0 R >> >>");
        using var document = PdfDocument.Open(input);

        var report = document.RedactText("NOMATCHXYZ", RedactionOptions.Maximum);

        SavedPdfLeakScanner.FindTerm(document.SaveToBytes(), Signer).Should().BeEmpty(
            "the structure tree still reaches the widget, and so the signature through its /V");
        report.Removals.Should().Contain(r => r.Feature == "signature dictionary(ies) removed" && r.Count == 1);
        report.Carriers.Should().ContainSingle(c => c.Carrier == "/Perms")
            .Which.RefusedReason.Should().Contain("/Custom");
        report.Carriers.Should().NotContain(c => c.Carrier.StartsWith("signature dictionary ", StringComparison.Ordinal),
            "nothing reaches the signature once its references are gone");
    }

    [Theory]
    [InlineData(Entry.RedactText)]
    [InlineData(Entry.ScrubTerms)]
    public void Standard_CutsATermFromTheSignerStrings_AndKeepsTheSignature(Entry entry)
    {
        using var document = PdfDocument.Open(Signed());
        if (entry == Entry.RedactText)
            document.RedactText(Signer, RedactionOptions.Default).Carriers
                .Should().ContainSingle(c => c.Carrier == Row).Which.Scrubbed.Should().BeTrue();
        else
            PdfDocumentSanitizer.ScrubTerms(document, new[] { Signer }, caseSensitive: false,
                    RedactionCarriers.All, CarrierScrubPolicy.Default)
                .For(RedactionCarriers.Signatures).Should().BeEquivalentTo(
                    new { TermFound = true, Modified = true, RefusedReason = (string?)null });

        var saved = document.SaveToBytes();
        SavedPdfLeakScanner.FindTerm(saved, Signer).Should().BeEmpty(
            "/Name, /Reason, /Location and /ContactInfo are kept carriers, cut like any other");
        SavedPdfLeakScanner.FindTerm(saved, "Approved by").Should().NotBeEmpty("Strip keeps the rest of the value");
        SavedPdfLeakScanner.FindTerm(saved, Subject).Should().NotBeEmpty("Standard keeps the signature and its certificates");
        using var reopened = PdfDocument.Open(saved);
        reopened.Catalog.ContainsKey("Perms").Should().BeTrue();
    }

    /// <summary>
    /// A name cannot be cut out of DER, and Standard keeps the signature: the
    /// term stays, and every entry point says so instead of reporting clean.
    /// </summary>
    [Fact]
    public void Standard_RefusesATermInACertificate_AndEveryEntryPointSaysSo()
    {
        using (var document = PdfDocument.Open(Signed()))
        {
            var report = document.RedactText(Subject, RedactionOptions.Default);
            report.Carriers.Should().ContainSingle(c => c.Carrier == Row)
                .Which.RefusedReason.Should().Contain("certificate");
            report.IsCleanSuccess.Should().BeFalse("CLAUDE.md rule 6: a carrier the engine could not scrub is reported");
            SavedPdfLeakScanner.FindTerm(document.SaveToBytes(), Subject).Should().NotBeEmpty(
                "the refusal is only honest if the term really is still there");
        }

        using (var document = PdfDocument.Open(Signed()))
            PdfDocumentSanitizer.ScrubTerms(document, new[] { Subject }, caseSensitive: false,
                    RedactionCarriers.All, CarrierScrubPolicy.Default)
                .For(RedactionCarriers.Signatures)!.RefusedReason.Should().Contain("certificate");

        using (var document = PdfDocument.Open(Signed()))
            RedactedCopySafetyPolicy.Evaluate(document,
                    RedactedCopySafetyRequest.ForTerms(new[] { Subject }, RedactionOptions.Default))
                .Warnings.Should().Contain(w => w.Contains("certificate", StringComparison.Ordinal));
    }

    /// <summary>
    /// The planted failure: with the carrier switched off the signer's name stays,
    /// and the two audits that do not share the scrub's code must say so.
    /// </summary>
    [Fact]
    public void Audits_SeeASignerTheScrubLeft_AndNotOneItCut()
    {
        using (var leaking = PdfDocument.Open(Signed()))
        {
            leaking.RedactText(Signer, RedactionOptions.Default with
                { Carriers = RedactionCarriers.All & ~RedactionCarriers.Signatures });
            SavedPdfLeakScanner.FindTerm(leaking.SaveToBytes(), Signer).Should().NotBeEmpty("the planted failure");

            RedactionCarrierAudit.Inspect(leaking, new[] { Signer }).SignatureCount.Should().Be(4,
                "/Name, /Reason, /Location and /ContactInfo each still hold it");
            CarrierTextRecovery.Scan(leaking).Should().Contain(f =>
                f.Carrier == "signature /Name" && f.Text.Contains(Signer, StringComparison.Ordinal));
        }

        using var scrubbed = PdfDocument.Open(Signed());
        scrubbed.RedactText(Signer, RedactionOptions.Default);
        RedactionCarrierAudit.Inspect(scrubbed, new[] { Signer }).SignatureCount.Should().Be(0);
        CarrierTextRecovery.Scan(scrubbed).Should().NotContain(f => f.Text.Contains(Signer, StringComparison.Ordinal));
    }

    [Fact]
    public void AreaAudit_CountsEverySignatureAndTheDss_AndNoneAfterMaximum()
    {
        using var document = PdfDocument.Open(Signed());

        var audit = RedactionCarrierAudit.Inspect(document);

        audit.SignatureCount.Should().Be(2, "an area redaction has no term, and a signature block is what it covers");
        audit.Describe().Should().Contain(line => line.Contains("signature", StringComparison.Ordinal));
        CarrierTextRecovery.Scan(document).Should().Contain(f =>
            f.Carrier == "signature certificates" && f.Kind == CarrierTextRecovery.CarrierFindingKind.Presence);

        document.GetPage(1).RedactAreaWithReport(new PdfRectangle(400, 100, 500, 120), RedactionOptions.Maximum);
        RedactionCarrierAudit.Inspect(document).SignatureCount.Should().Be(0);
        CarrierTextRecovery.Scan(document).Should().NotContain(f => f.Carrier.StartsWith("signature", StringComparison.Ordinal));
    }
}
