using System;
using System.IO;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1586's accessibility acceptance: a tagged PDF that passed veraPDF PDF/UA
/// before a <see cref="RedactionProfile.Standard"/> redaction must still pass
/// after it, and <see cref="RedactionProfile.Maximum"/> must REPORT the loss.
/// </summary>
/// <remarks>
/// <para><b>The verdict is veraPDF's, never ours.</b> excise has its own
/// <c>PdfUaValidator</c>, and using it here would be excise certifying that
/// excise kept a property excise defines — the self-oracle this project
/// forbids. veraPDF is the PDF Association's reference validator.</para>
///
/// <para><b>Why this test exists at all.</b> Standard strips the catalog
/// <c>/Metadata</c> packet, and PDF/UA-1 clause 7.1 requires that packet AND a
/// non-empty <c>dc:title</c>. Measured on
/// <c>test-pdfs/pdfua/7.1-t01-pass-a.pdf</c> (2026-09-17, veraPDF 1.28):
/// removing <c>/Metadata</c> fails 7.1 test 8; re-emitting <c>pdfuaid:part</c>
/// alone fails 7.1 test 9; <c>pdfuaid:part</c> plus a SYNTHESISED
/// <c>dc:title</c> passes. So the design had to change to satisfy the
/// acceptance criterion, and this is the gate that says it did — not the
/// reasoning that said it should.</para>
///
/// <para>⚠️ The fixture is asserted to pass BEFORE the redaction. Without
/// that, a conformant-input assumption that quietly stopped holding would turn
/// this into a test that cannot fail (#1527).</para>
/// </remarks>
public class RedactionProfileAccessibilityTests
{
    private const string Secret = "QUILLFEATHER";

    /// <summary>
    /// A conformant PDF/UA-1 document built by excise's own builder — the same
    /// fixture <c>PdfUaVeraPdfCrossCheckTests</c> validates as conformant — with
    /// the secret in a paragraph so there is something to redact.
    /// </summary>
    private static byte[] TaggedFixture(string fontPath)
    {
        var font = Excise.Core.Graphics.PdfFont.FromTrueType(File.ReadAllBytes(fontPath), 11);
        return Excise.Core.Authoring.PdfDocumentBuilder.Create()
            .Tagged().DefaultFont(font).Language("en-US").Title("Accessible Sample")
            .Heading("Overview", 1)
            .Paragraph($"The witness {Secret} gave a statement.")
            .Paragraph("An unrelated second paragraph, so the document is not one line.")
            .SaveToBytes();
    }

    /// <summary>
    /// The DejaVu fixture, resolved through the ONE locator (#1527). A
    /// hand-rolled upward walk is what `check-fixture-locators.sh` fails on,
    /// and for a reason: a bounded one silently found nothing and turned four
    /// corpus gates into skips that satisfied every skip check.
    /// </summary>
    private static string? FontPath() =>
        TestRepoLayout.FindFile("Excise.Core.Tests", "Fixtures", "Fonts", "DejaVuSans.ttf");

    private static VeraPdfResult ValidateUa1(byte[] pdf)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ua1_{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdf);
        try
        {
            return VeraPdfReferenceValidator.Validate(path, "ua1")
                ?? throw new InvalidOperationException("veraPDF unavailable after the availability check");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Standard_KeepsATaggedDocumentPdfUaConformant()
    {
        Assert.SkipUnless(VeraPdfReferenceValidator.IsAvailable,
            "veraPDF is the independent PDF/UA oracle and is not installed (brew install verapdf)");
        var fontPath = FontPath();
        Assert.SkipWhen(fontPath is null, TestRepoLayout.AbsenceReason(
            "the DejaVuSans.ttf fixture", "Excise.Core.Tests/Fixtures/Fonts/DejaVuSans.ttf"));

        var input = TaggedFixture(fontPath!);

        // The premise, measured rather than assumed.
        var before = ValidateUa1(input);
        before.Ran.Should().BeTrue($"veraPDF must produce a verdict: {before.Failure}");
        before.Passed.Should().BeTrue(
            "the acceptance criterion is 'still passes WHERE THE INPUT PASSED', so this test " +
            "means nothing unless the input passes");

        using var doc = PdfDocument.Open(input);
        var report = doc.RedactText(Secret, RedactionOptions.Default);
        var output = doc.SaveToBytes();

        // The redaction did its job.
        SavedPdfLeakScanner.FindTerm(output, Secret).Should().BeEmpty(
            "carrier-agnostic: the secret is nowhere in the saved bytes, compressed streams included");
        report.Profile.Should().Be(RedactionProfile.Standard);
        report.AccessibilityAndInteractivityRemoved.Should().BeFalse();

        // ...and the document is still accessible, per veraPDF.
        var after = ValidateUa1(output);
        after.Ran.Should().BeTrue($"veraPDF must produce a verdict: {after.Failure}");
        after.Passed.Should().BeTrue(
            "#1586: Standard strips the XMP packet, and PDF/UA-1 clause 7.1 needs both the " +
            "packet and a non-empty dc:title — so the strip re-emits pdfuaid:part with a " +
            "SYNTHESISED placeholder title. If this fails, either the placeholder stopped " +
            "being written or a Standard removal broke the structure tree");
    }

    [Fact]
    public void Standard_KeepsTheStructureTree_AndItsAlternateText()
    {
        // The half of accessibility veraPDF's verdict does not spell out: the
        // tags and the /Alt text a screen reader actually reads are still
        // there. A document can be UA-conformant and useless.
        var fontPath = FontPath();
        Assert.SkipWhen(fontPath is null, TestRepoLayout.AbsenceReason(
            "the DejaVuSans.ttf fixture", "Excise.Core.Tests/Fixtures/Fonts/DejaVuSans.ttf"));

        using var doc = PdfDocument.Open(TaggedFixture(fontPath!));
        doc.RedactText(Secret, RedactionOptions.Default);
        var text = SavedPdfLeakScanner.AllCarriersText(doc.SaveToBytes());

        text.Should().Contain("/StructTreeRoot", "the structure tree survives Standard");
        text.Should().Contain("/Lang", "and so does the language, which UA requires");
    }

    [Fact]
    public void Maximum_ReportsThatAccessibilityIsGone()
    {
        Assert.SkipUnless(VeraPdfReferenceValidator.IsAvailable,
            "veraPDF is the independent PDF/UA oracle and is not installed (brew install verapdf)");
        var fontPath = FontPath();
        Assert.SkipWhen(fontPath is null, TestRepoLayout.AbsenceReason(
            "the DejaVuSans.ttf fixture", "Excise.Core.Tests/Fixtures/Fonts/DejaVuSans.ttf"));

        var input = TaggedFixture(fontPath!);
        ValidateUa1(input).Passed.Should().BeTrue("the premise again");

        using var doc = PdfDocument.Open(input);
        var report = doc.RedactText(Secret, RedactionOptions.Maximum);

        report.AccessibilityAndInteractivityRemoved.Should().BeTrue(
            "#1586 requires Maximum to SAY the output is no longer accessible or " +
            "interactive. Whether veraPDF then fails it is not the contract — the report " +
            "is. A profile that destroyed accessibility silently would be the same class " +
            "of problem as a carrier that silently keeps a term");
        report.ToString().Should().Contain("NO LONGER accessible or interactive");

        SavedPdfLeakScanner.FindTerm(doc.SaveToBytes(), Secret).Should().BeEmpty();
    }
}
