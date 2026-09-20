using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Excise.Core.Authoring;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Excise.Rendering.Differential;
using Xunit;
using Excise.TestSupport;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1524, judged by the only authority for a conformance claim: <b>veraPDF</b>,
/// the PDF Association's reference validator. excise's own
/// <c>PdfAStructuralValidator</c> reads the same XMP with the same parser as the
/// writer, so it could not see this defect and would not have — a tool must not
/// be its own oracle for the property it exists to guarantee.
///
/// <para><b>The defect.</b> The writer suppressed the object streams PDF/A-1
/// forbids only when the XMP spelled the identification as an ELEMENT
/// (<c>&lt;pdfaid:part&gt;1&lt;/pdfaid:part&gt;</c>). XMP permits the same
/// simple property as an ATTRIBUTE of the <c>rdf:Description</c>
/// (<c>pdfaid:part="1"</c>), and such a file — valid PDF/A-1, accepted by
/// veraPDF — came out of excise carrying an object stream and a cross-reference
/// stream, which PDFA-1B rejects (ISO 19005-1 6.1.4#3,
/// <c>containsXRefStream == false</c>).</para>
///
/// <para><b>Conservation, not validation</b> (#1057's rule): the input must
/// already pass, and the assertion is that the verdict does not get worse —
/// same detected flavour, still passing. Every premise is a documented skip
/// rather than a silent pass, because a green run over an input veraPDF never
/// accepted would prove nothing.</para>
///
/// <para>The deterministic byte-level counterpart, which needs no external
/// tool and covers every serialisation and header version, is
/// <c>Excise.Core.Tests/Writing/PdfA1ObjectStreamSuppressionTests</c>. Both are
/// asserted here on purpose: if a future veraPDF stopped flagging the xref
/// stream, the byte assertion still holds excise to ISO 19005-1.</para>
/// </summary>
public class PdfA1SerialisationConformanceTests
{
    [Fact]
    public void AttributeFormPdfA1_KeepsItsConformanceThroughASave() =>
        RunConservation(rewriteToAttributeForm: true);

    /// <summary>
    /// The control: the element form, which is what excise itself emits and the
    /// only spelling that ever worked. It shares every step with the row above,
    /// so a failure in both points at the fixture or the validator rather than
    /// at #1524.
    /// </summary>
    [Fact]
    public void ElementFormPdfA1_KeepsItsConformanceThroughASave() =>
        RunConservation(rewriteToAttributeForm: false);

    private static void RunConservation(bool rewriteToAttributeForm)
    {
        Assert.SkipUnless(VeraPdfReferenceValidator.IsAvailable, "verapdf not installed");

        var fontPath = Path.Combine(
            RepoRoot(), "Excise.Core.Tests", "Fixtures", "Fonts", "DejaVuSans.ttf");
        Assert.SkipWhen(!File.Exists(fontPath), "DejaVuSans.ttf fixture not present");

        var authored = AuthorPdfA1(fontPath);

        // The defect needs a header of at least 1.5 as well as the attribute
        // form, because the writer's compression gate gets there first. excise's
        // own authored documents are 1.7 (PdfDocument.CreateNew), which is what
        // makes this fixture representative — and PDFA-1B pins no version in the
        // header rule, so 1.7 is a header the validator accepts. Asserted rather
        // than assumed: if this stopped being true the test would still pass,
        // while measuring nothing.
        using (var check = PdfDocument.Open(authored))
        {
            check.Version.Should().BeOneOf(new[] { "1.5", "1.6", "1.7", "2.0" },
                "a PDF/A-1 file with a pre-1.5 header is saved by the writer's version "
                + "gate, not by its pdfaid detection, so this fixture would no longer "
                + "exercise #1524");
        }

        var input = rewriteToAttributeForm ? RewriteIdentificationAsAttribute(authored) : authored;

        var inputPath = TempPath("in");
        var outputPath = TempPath("out");
        try
        {
            File.WriteAllBytes(inputPath, input);

            var before = VeraPdfReferenceValidator.Validate(inputPath);
            Assert.SkipWhen(before is null or { Ran: false },
                $"verapdf could not judge the input: {before?.Failure}");
            Assert.SkipWhen(!before!.Passed,
                "the input does not conform (flavour " + before.Flavour + "), so there is "
                + "nothing to conserve — for the attribute-form row this also means "
                + "veraPDF did not read the attribute serialisation as a PDF/A "
                + "identification on this build, which is #1524's premise and worth "
                + "reporting rather than asserting past");
            before.Flavour.Should().Be("1b",
                "the fixture is authored as PDF/A-1B, and the flavour veraPDF detects "
                + "comes from the file's own pdfaid — a different one means the rewrite "
                + "changed the claim rather than only its serialisation");

            byte[] saved;
            using (var doc = PdfDocument.Open(input))
            {
                doc.TargetsPdfA.Should().BeTrue(
                    "excise must read the claim in whichever serialisation the file uses");
                saved = doc.SaveToBytes();
            }
            File.WriteAllBytes(outputPath, saved);

            Encoding.Latin1.GetString(saved).Should().NotContain("/Type /ObjStm",
                "PDF/A-1 forbids object streams (ISO 19005-1 6.1.4); this is the byte-level "
                + "property, held independently of what any validator currently reports");

            var after = VeraPdfReferenceValidator.Validate(outputPath);
            after.Should().NotBeNull();
            after!.Ran.Should().BeTrue($"verapdf must be able to judge what excise wrote: {after.Failure}");
            after.Flavour.Should().Be(before.Flavour,
                "the detected flavour comes from the file's own pdfaid — a change means "
                + "excise altered or dropped the conformance claim (#1056)");
            after.Passed.Should().BeTrue(
                "a file that arrived as valid PDF/A-1B must not be downgraded by being "
                + "opened and saved — the claim is an archival guarantee somebody relied on");
        }
        finally
        {
            TryDelete(inputPath);
            TryDelete(outputPath);
        }
    }

    private static byte[] AuthorPdfA1(string fontPath) =>
        PdfDocumentBuilder.Create()
            .Language("en-US")
            .Title("Archival Serialisation Fixture")
            .DefaultFont(PdfFont.FromTrueType(File.ReadAllBytes(fontPath), 11))
            .PdfA(PdfAConformance.PdfA1B)
            .Heading("Archival Serialisation Fixture")
            .Paragraph("Body text with unicode: café.")
            .SaveToBytes();

    /// <summary>
    /// Move <c>pdfaid:part</c> and <c>pdfaid:conformance</c> from elements onto
    /// the <c>rdf:Description</c> start tag, <b>preserving the packet's byte
    /// length</b> by re-padding it with the whitespace XMP packets carry anyway
    /// (ISO 16684-1). Keeping the length identical is what lets this be a raw
    /// byte splice: the stream's <c>/Length</c> and every cross-reference offset
    /// in the file stay correct, so the only difference between the two inputs
    /// is the serialisation under test.
    /// </summary>
    /// <remarks>
    /// Everything else in the packet — <c>dc:title</c>, <c>pdf:Producer</c> —
    /// is left exactly as it was: PDFA-1B's Info/XMP consistency rules compare
    /// the two, and dropping one side would fail the input for an unrelated
    /// reason.
    /// </remarks>
    private static byte[] RewriteIdentificationAsAttribute(byte[] pdf)
    {
        var latin1 = Encoding.Latin1.GetString(pdf);
        var start = latin1.IndexOf("<?xpacket begin=", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, "the authored PDF/A file must carry an XMP packet");

        const string endMarker = "<?xpacket end=\"w\"?>";
        var end = latin1.IndexOf(endMarker, start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start, "the XMP packet must be terminated");
        var length = end + endMarker.Length - start;

        var packet = Encoding.UTF8.GetString(pdf, start, length);

        var part = ReadElementValue(packet, "part");
        var conformance = ReadElementValue(packet, "conformance");
        part.Should().Be("1", "the fixture is authored as PDF/A-1");
        conformance.Should().Be("B");

        var rewritten = Regex.Replace(
            packet, @"[ \t]*<pdfaid:(part|conformance)>[^<]*</pdfaid:\1>\r?\n?", "");
        rewritten.Should().NotContain("<pdfaid:", "both identification elements must be gone");

        const string nsDeclaration = "xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\"";
        rewritten.Should().Contain(nsDeclaration);
        rewritten = rewritten.Replace(
            nsDeclaration,
            nsDeclaration + $" pdfaid:part=\"{part}\" pdfaid:conformance=\"{conformance}\"",
            StringComparison.Ordinal);

        // Re-pad to the original byte length. The attribute form is shorter than
        // the two elements it replaces, so this is padding, never truncation —
        // and if that ever stops holding, fail loudly rather than corrupt the
        // file's offsets.
        var body = rewritten[..rewritten.LastIndexOf(endMarker, StringComparison.Ordinal)];
        var bodyBytes = Encoding.UTF8.GetByteCount(body);
        var markerBytes = Encoding.UTF8.GetByteCount(endMarker);
        var padding = length - bodyBytes - markerBytes;
        if (padding < 0)
        {
            Assert.Fail(
                $"the attribute-form packet is {-padding} bytes LONGER than the element "
                + "form, so it cannot be spliced in place; the rewrite would have to "
                + "rebuild the document's xref offsets");
        }

        var replacement = Encoding.UTF8.GetBytes(body + new string(' ', padding) + endMarker);
        replacement.Length.Should().Be(length, "the splice must be length-preserving");

        var result = (byte[])pdf.Clone();
        Array.Copy(replacement, 0, result, start, replacement.Length);

        // Guard: the mutated bytes really do carry the attribute form and no
        // element form, so a green run cannot come from having rewritten nothing.
        var mutated = Encoding.UTF8.GetString(result, start, length);
        mutated.Should().Contain("pdfaid:part=\"1\"");
        mutated.Should().NotContain("<pdfaid:part>");
        return result;
    }

    private static string? ReadElementValue(string packet, string property)
    {
        var m = Regex.Match(packet, $"<pdfaid:{property}>([^<]*)</pdfaid:{property}>");
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private static string TempPath(string tag) =>
        Path.Combine(Path.GetTempPath(), $"excise-pdfa1-{tag}-{Guid.NewGuid():N}.pdf");

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }

    // #1706 — TestRepoLayout, not a hand-rolled walk to .git/excise.sln. LOCAL
    // checkout, deliberately: this reads THIS worktree's own source / writes its
    // own artifacts, and the main checkout may be on a different branch.
    private static string RepoRoot() =>
        TestRepoLayout.LocalCheckoutRoot ?? throw new InvalidOperationException("repository root unavailable");
}
