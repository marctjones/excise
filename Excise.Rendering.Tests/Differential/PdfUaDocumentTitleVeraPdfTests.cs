using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Core.Validation;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1532's acceptance, checked against veraPDF: PDF/UA-1's document-title rule
/// and excise's <c>UA-Title</c> rule, compared on the same bytes.
/// </summary>
/// <remarks>
/// <para><b>Method.</b> One conformant tagged document (built by excise's own
/// builder, the fixture <c>RedactionProfileAccessibilityTests</c> already
/// establishes as ua1-conformant) is re-emitted with only its XMP <c>dc:title</c>
/// changed — everything else is the same, so a veraPDF failure on the variant is
/// attributable to the title. The verdict is veraPDF's <c>-f ua1</c> profile,
/// never ours, and the failing rule is read out of veraPDF's XML report rather
/// than inferred from a bare FAIL.</para>
///
/// <para><b>The Info <c>/Title</c> is removed from every variant unless a row
/// says otherwise.</b> <c>UA-Title</c> is "Info /Title <i>or</i> XMP dc:title",
/// so a file that keeps an Info title passes excise however empty its XMP title
/// is, while veraPDF reads the XMP alone. That is one of the divergences below;
/// keeping the Info title out of the other rows makes each one test the XMP
/// parse and nothing else.</para>
///
/// <para><b>Measured 2026-09-21, veraPDF 1.30.0 (<c>-f ua1</c>): the two tools
/// AGREE on the shapes in the first two tests and DISAGREE on the rest, and the
/// disagreements are pinned exactly rather than hidden.</b> Every veraPDF
/// failure is clause 7.1 test 9. veraPDF does not check the VALUE of a
/// well-formed <c>rdf:Alt</c>/<c>rdf:li</c>, so an empty one passes it while
/// excise (following the ISO wording) fails it; and veraPDF rejects the bare
/// and attribute serialisations and an Info-only title, which excise accepts.
/// The lenient direction is filed as #1774.</para>
/// </remarks>
public class PdfUaDocumentTitleVeraPdfTests
{
    private const string Title = "Accessible Sample";

    private const string RealTitle =
        "   <dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">" + Title + "</rdf:li></rdf:Alt></dc:title>\n";

    private static string? FontPath() =>
        TestRepoLayout.FindFile("Excise.Core.Tests", "Fixtures", "Fonts", "DejaVuSans.ttf");

    private static string Xmp(string titleBody, string descAttr = "") =>
        "<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n" +
        "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">\n" +
        " <rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">\n" +
        "  <rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" " +
        "xmlns:pdfuaid=\"http://www.aiim.org/pdfua/ns/id/\"" + descAttr + ">\n" +
        titleBody +
        "   <dc:language><rdf:Bag><rdf:li>en-US</rdf:li></rdf:Bag></dc:language>\n" +
        "   <pdfuaid:part>1</pdfuaid:part>\n" +
        "  </rdf:Description>\n </rdf:RDF>\n</x:xmpmeta>\n<?xpacket end=\"w\"?>";

    /// <summary>
    /// The conformant fixture with its XMP replaced by <paramref name="xmp"/>
    /// and, unless <paramref name="keepInfoTitle"/>, its Info <c>/Title</c>
    /// removed.
    /// </summary>
    private static byte[] Fixture(string fontPath, string xmp, bool keepInfoTitle = false)
    {
        var font = Excise.Core.Graphics.PdfFont.FromTrueType(File.ReadAllBytes(fontPath), 11);
        var built = Excise.Core.Authoring.PdfDocumentBuilder.Create()
            .Tagged().DefaultFont(font).Language("en-US").Title(Title)
            .Heading("Overview", 1)
            .Paragraph("A paragraph, so the document has real content to tag.")
            .SaveToBytes();

        using var doc = PdfDocument.Open(built);
        var bytes = Encoding.UTF8.GetBytes(xmp);
        var dict = new PdfDictionary();
        dict.SetName("Type", "Metadata");
        dict.SetName("Subtype", "XML");
        dict.SetInt("Length", bytes.Length);
        doc.Catalog["Metadata"] = doc.AddIndirectObject(new PdfStream(dict, bytes));
        if (!keepInfoTitle)
            doc.Info!.Remove("Title");
        return doc.SaveToBytes();
    }

    private sealed record VeraVerdict(bool Compliant, string[] FailedRules);

    /// <summary>veraPDF's <c>-f ua1</c> XML report: the verdict and each failed clause#test.</summary>
    private static VeraVerdict VeraUa1(byte[] pdf)
    {
        var path = Path.Combine(Path.GetTempPath(), $"uatitle_{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdf);
        try
        {
            var psi = new ProcessStartInfo("verapdf")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in new[] { "--format", "xml", "-f", "ua1", path }) psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi)!;
            // #925: drain both pipes concurrently.
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(120_000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* gone */ }
                throw new TimeoutException("veraPDF did not finish within 120 s");
            }
            stderr.GetAwaiter().GetResult();
            var raw = stdout.GetAwaiter().GetResult();

            // <rule specification="..." clause="7.1" testNumber="9" status="failed" ...>
            var failed = Regex.Matches(raw, "<rule [^>]*?clause=\"([^\"]+)\"[^>]*?testNumber=\"(\\d+)\"[^>]*?status=\"failed\"")
                .Select(m => $"{m.Groups[1].Value}#{m.Groups[2].Value}")
                .ToArray();
            return new VeraVerdict(raw.Contains("isCompliant=\"true\"", StringComparison.Ordinal), failed);
        }
        finally { File.Delete(path); }
    }

    private static RuleStatus ExciseTitleRule(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        return PdfUaValidator.Validate(doc).Results.Single(r => r.RuleId == "UA-Title").Status;
    }

    private static string RequireFont()
    {
        Assert.SkipUnless(VeraPdfReferenceValidator.IsAvailable,
            "veraPDF is the independent PDF/UA oracle and is not installed (brew install verapdf)");
        var fontPath = FontPath();
        Assert.SkipWhen(fontPath is null, TestRepoLayout.AbsenceReason(
            "the DejaVuSans.ttf fixture", "Excise.Core.Tests/Fixtures/Fonts/DejaVuSans.ttf"));
        return fontPath!;
    }

    private static void VeraFailsOnTheTitleRule(VeraVerdict vera) =>
        vera.FailedRules.Should().Equal(new[] { "7.1#9" },
            "veraPDF fails it on the §7.1 title test and nothing else about the fixture is broken");

    [Fact]
    public void ARealTitle_PassesBoth()
    {
        var fontPath = RequireFont();
        var pdf = Fixture(fontPath, Xmp(RealTitle));

        var vera = VeraUa1(pdf);
        vera.Compliant.Should().BeTrue(
            $"the premise: the fixture is ua1-conformant, so any failure elsewhere is the title. Failed: {string.Join(", ", vera.FailedRules)}");
        ExciseTitleRule(pdf).Should().Be(RuleStatus.Pass);
    }

    [Theory]
    // Empty, in the serialisations veraPDF also rejects.
    [InlineData("   <dc:title/>\n", "")]
    [InlineData("   <dc:title></dc:title>\n", "")]
    [InlineData("   <dc:title>   </dc:title>\n", "")]
    [InlineData("", " dc:title=\"\"")]
    // The property NAME occurring outside a title: a comment, another property's value.
    [InlineData("   <!-- dc:title is intentionally absent -->\n", "")]
    [InlineData("   <dc:description><rdf:Alt><rdf:li xml:lang=\"x-default\">see dc:title elsewhere</rdf:li></rdf:Alt></dc:description>\n", "")]
    public void NoUsableTitle_FailsBoth(string titleBody, string descAttr)
    {
        var fontPath = RequireFont();

        // The control, so a veraPDF FAIL below cannot be a fixture that was never valid.
        VeraUa1(Fixture(fontPath, Xmp(RealTitle))).Compliant.Should().BeTrue("the control fixture conforms");

        var pdf = Fixture(fontPath, Xmp(titleBody, descAttr));
        var vera = VeraUa1(pdf);
        vera.Compliant.Should().BeFalse("there is no usable dc:title (ISO 14289-1 §7.1)");
        VeraFailsOnTheTitleRule(vera);
        ExciseTitleRule(pdf).Should().Be(RuleStatus.Fail, "excise must reach the same verdict as veraPDF");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyRdfAltEntry_ExciseFails_VeraPdfPasses_RegisteredDivergence(string value)
    {
        var fontPath = RequireFont();
        var pdf = Fixture(fontPath, Xmp(
            $"   <dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">{value}</rdf:li></rdf:Alt></dc:title>\n"));

        VeraUa1(pdf).Compliant.Should().BeTrue(
            "veraPDF 1.30.0 checks that dc:title EXISTS as a Lang Alt, not that its value is non-empty. " +
            "If this now fails, veraPDF learned the empty-value rule and this row joins NoUsableTitle_FailsBoth");
        ExciseTitleRule(pdf).Should().Be(RuleStatus.Fail,
            "#1532: an empty title is not a title, so excise is deliberately stricter than veraPDF here");
    }

    [Theory]
    // Not valid XMP for a Lang Alt property; ReadDcTitle accepts them anyway. See #1774.
    [InlineData("   <dc:title>" + Title + "</dc:title>\n", "")]
    [InlineData("", " dc:title=\"" + Title + "\"")]
    public void ANonLangAltTitle_ExcisePasses_VeraPdfFails_RegisteredDivergence(string titleBody, string descAttr)
    {
        var fontPath = RequireFont();
        var pdf = Fixture(fontPath, Xmp(titleBody, descAttr));

        var vera = VeraUa1(pdf);
        vera.Compliant.Should().BeFalse("veraPDF does not read dc:title from these serialisations");
        VeraFailsOnTheTitleRule(vera);
        ExciseTitleRule(pdf).Should().Be(RuleStatus.Pass,
            "REGISTERED DIVERGENCE (#1774): excise accepts a title veraPDF does not see. If this now " +
            "Fails, the divergence is retired and this row should join NoUsableTitle_FailsBoth");
    }

    [Fact]
    public void AnInfoOnlyTitle_ExcisePasses_VeraPdfFails_RegisteredDivergence()
    {
        var fontPath = RequireFont();
        // Info /Title kept, XMP dc:title absent.
        var pdf = Fixture(fontPath, Xmp(""), keepInfoTitle: true);

        var vera = VeraUa1(pdf);
        vera.Compliant.Should().BeFalse("PDF/UA-1 reads the title from XMP; an Info /Title does not satisfy it");
        VeraFailsOnTheTitleRule(vera);
        ExciseTitleRule(pdf).Should().Be(RuleStatus.Pass,
            "REGISTERED DIVERGENCE (#1774): UA-Title accepts 'Info /Title or XMP dc:title'. If this now " +
            "Fails, the divergence is retired and this row should join NoUsableTitle_FailsBoth");
    }
}
