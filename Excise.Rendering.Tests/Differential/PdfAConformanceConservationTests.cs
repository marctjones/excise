using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// A document that arrives claiming PDF/A conformance must not lose it by
/// passing through excise.
///
/// <para><b>The first gate here judged by something other than excise or a
/// renderer.</b> Every other differential in this repo asks "does excise draw
/// what other engines draw" or "did excise remove what it said it removed".
/// This asks a question with an external, published answer: veraPDF is the PDF
/// Association's reference validator, and it either accepts the output or it
/// does not.</para>
///
/// <para><b>On PDF 2.0.</b> There is no validator for ISO 32000-2, because it
/// is a format specification rather than a conformance profile — "PDF 2.0
/// conformant" is not a checkable claim. PDF/A-4 IS built on PDF 2.0, so a
/// file that veraPDF accepts as PDF/A-4 is the nearest externally-checkable
/// statement available. Narrower than "excise is PDF 2.0 conformant", and worth
/// more, because somebody other than excise is saying it.</para>
///
/// <para><b>Conservation, not validation.</b> The assertion is that the verdict
/// does not get WORSE — same flavour, still passing. excise is not asked to
/// make a non-conforming file conform, and a file that arrives failing may
/// leave failing. That is the "judge the delta, not the state" rule (#944/#945)
/// with an external oracle on both sides.</para>
///
/// <para>This found #1056 on its first use: <c>excise merge</c> strips XMP
/// <c>pdfaid</c>, so a valid PDF/A-4 document came out conforming to nothing.
/// The file still opened, rendered identically, and passed
/// <c>qpdf --check</c> — no existing gate saw it.</para>
/// </summary>
public class PdfAConformanceConservationTests
{
    private const string CorpusRoot =
        "test-pdfs/verapdf-corpus/veraPDF-corpus-master";

    /// <summary>
    /// A bounded set of PDF/A files the validator already accepts. Bounded on
    /// purpose: veraPDF is a JVM tool at roughly a second per call and this runs
    /// it twice per fixture. The point is a regression tripwire on the writer,
    /// not a survey.
    /// </summary>
    public static TheoryData<string> Fixtures()
    {
        var d = new TheoryData<string>();
        foreach (var rel in new[]
                 {
                     "PDF_A-4/6.9 Embedded files/veraPDF test suite 6-9-t03-pass-a.pdf",
                     "PDF_A-4/6.10 Optional content/veraPDF test suite 6-10-t01-pass-a.pdf",
                     "PDF_A-4/6.10 Optional content/veraPDF test suite 6-10-t02-pass-a.pdf",
                     "PDF_A-2b/6.9 Optional content/veraPDF test suite 6-9-t03-pass-a.pdf",
                 })
            d.Add(rel);
        return d;
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void SavingAConformingDocument_DoesNotLoseItsConformance(string relative)
    {
        Assert.SkipUnless(VeraPdfReferenceValidator.IsAvailable, "verapdf not installed");

        var path = Resolve(Path.Combine(CorpusRoot, relative));
        Assert.SkipWhen(path == null, "veraPDF corpus not present");

        var before = VeraPdfReferenceValidator.Validate(path!);
        Assert.SkipWhen(before is null or { Ran: false }, "verapdf could not judge the input");

        // Only conservation is asserted, so a fixture the validator already
        // rejects proves nothing and is skipped rather than silently counted.
        Assert.SkipWhen(!before!.Passed, $"input does not conform ({before.Flavour}); nothing to conserve");

        var output = Path.Combine(Path.GetTempPath(), $"excise-pdfa-{Guid.NewGuid():N}.pdf");
        try
        {
            using (var doc = PdfDocument.Open(File.ReadAllBytes(path!)))
                doc.Save(output);

            var after = VeraPdfReferenceValidator.Validate(output);
            after.Should().NotBeNull();
            after!.Ran.Should().BeTrue($"verapdf must be able to judge what excise wrote: {after.Failure}");

            after.Flavour.Should().Be(before.Flavour,
                "the DETECTED flavour comes from the file's own XMP pdfaid — a change here means " +
                "excise altered or dropped the document's conformance claim. A fall back to '1b' " +
                "is the signature of the identification being lost entirely (#1056).");

            after.Passed.Should().BeTrue(
                $"a document that arrived as valid PDF/A-{before.Flavour} must not be downgraded by " +
                "being opened and saved — the claim is an archival guarantee somebody relied on");
        }
        finally
        {
            try { File.Delete(output); } catch { /* best effort */ }
        }
    }

    private static string? Resolve(string rel)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir != null; i++)
        {
            var c = Path.Combine(dir, rel);
            if (File.Exists(c)) return c;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}
