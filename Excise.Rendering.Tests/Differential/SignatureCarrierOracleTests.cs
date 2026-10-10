using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1861 read back by qpdf and mutool, not by excise: after Maximum, the catalog
/// qpdf shows has neither <c>/Perms</c> nor <c>/DSS</c>, no signature dictionary
/// is left in qpdf's object dump, the file passes <c>qpdf --check</c> and mutool
/// opens it. Run on the synthetic certification-signed fixture and on a real
/// one, veraPDF's DocMDP test file, whose signer's e-mail address is only in the
/// certificate inside <c>/Contents</c>.
/// </summary>
public sealed class SignatureCarrierOracleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"signature-carriers-{Guid.NewGuid():N}");

    public SignatureCarrierOracleTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private const string VeraPdfSigned =
        "test-pdfs/verapdf-corpus/veraPDF-corpus-master/PDF_A-2b/6.1 File structure/6.1.12 Permissions/"
        + "veraPDF test suite 6-1-12-t02-pass-a.pdf";

    [Theory]
    [InlineData("synthetic", RedactionProfile.Standard)]
    [InlineData("synthetic", RedactionProfile.Maximum)]
    [InlineData("verapdf", RedactionProfile.Standard)]
    [InlineData("verapdf", RedactionProfile.Maximum)]
    public void Maximum_LeavesQpdfNoPermsNoDssAndNoSignature(string source, RedactionProfile profile)
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable && MutoolReferenceRenderer.IsAvailable,
            "qpdf and mutool are the independent readers (brew install qpdf mupdf-tools)");
        byte[] input;
        string marker;
        if (source == "verapdf")
        {
            var path = TestRepoLayout.FindFile(VeraPdfSigned);
            Assert.SkipWhen(path == null, TestRepoLayout.AbsenceReason("veraPDF corpus", VeraPdfSigned));
            input = File.ReadAllBytes(path!);
            marker = "admin@verapdf.org";
        }
        else
        {
            input = CarrierTrapFixtures.Signed(null, "SIGNERNAMETRAP", "SIGNERCERTTRAP");
            marker = "SIGNERCERTTRAP";
        }
        SavedPdfLeakScanner.FindTerm(input, marker).Should().NotBeEmpty("input-side control: the certificate names the signer");

        var output = Path.Combine(_dir, $"{source}-{profile}.pdf");
        RedactionReport report;
        using (var document = PdfDocument.Open(input))
        {
            var options = RedactionOptions.ForProfile(profile);
            // #2042: the rewrite voids the certification, so both profiles now strip /Perms by default.
            // The Standard control keeps it off so the oracle still proves it can see a kept /Perms.
            if (profile == RedactionProfile.Standard)
                options = options with { RemoveVoidedCertification = false };
            report = document.RedactText("NOMATCHXYZ", options);
            document.Save(output);
        }

        QpdfReferenceTool.Check(output)!.Value.Success.Should().BeTrue("the redacted file must stay valid to qpdf");
        MutoolTextExtractor.ExtractPage(output, 1).Should().NotBeNull("mutool must open the redacted file");
        var root = Show(output, "trailer");
        var catalog = Show(output, Regex.Match(root, @"/Root (\d+) 0 R").Groups[1].Value);
        var saved = File.ReadAllBytes(output);
        if (profile == RedactionProfile.Standard)
        {
            catalog.Should().Contain("/Perms", "planted failure: Standard keeps the signature");
            SavedPdfLeakScanner.FindTerm(saved, marker).Should().NotBeEmpty();
            return;
        }

        catalog.Should().NotContain("/Perms").And.NotContain("/DSS");
        CarrierTrapIndependentCorroborationTests.QpdfDump(output).Should().NotContain("/ByteRange",
            "no signature dictionary is left for qpdf to dump");
        SavedPdfLeakScanner.FindTerm(saved, marker).Should().BeEmpty("#1861: the certificate named the signer");
        // The certification strip runs first and leaves the field unsigned, so the flatten
        // removes the signature FIELD (and the report names the voided certification).
        report.Removals.Should().Contain(r => r.Feature == "signature field(s) removed" && r.Count == 1);
        report.Removals.Should().Contain(r => r.Feature == "certification entry(ies) voided by the rewrite");
    }

    private static string Show(string path, string what) =>
        Encoding.Latin1.GetString(CarrierTrapIndependentCorroborationTests.RunTool("qpdf", $"--show-object={what}", path)
                                  ?? throw new InvalidOperationException($"qpdf --show-object={what} failed on {path}"));
}
