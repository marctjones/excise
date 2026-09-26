using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using Obj = Excise.Core.Tests.Redaction.Recovery.RecoveryFixtureBuilder.Obj;
using RecoveryFixtureBuilder = Excise.Core.Tests.Redaction.Recovery.RecoveryFixtureBuilder;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1863: a page that draws a form excise cannot decode. Redaction must not
/// abort: it removes what it can read, keeps the form, and reports it. The
/// same unchecked read in the XMP scrub and in a font's /Encoding CMap is
/// pinned here too. qpdf is the structure oracle for the saved file.
/// </summary>
public sealed class UndecodableFormRedactionTests : IDisposable
{
    private const string Term = "READABLESECRET";
    private const string FormTerm = "FORMSECRET";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"undecodable-form-{Guid.NewGuid():N}");

    public UndecodableFormRedactionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Readable page text at (72, 700), then object 7, a form over the same
    /// spot whose stream cannot be decoded.
    /// </summary>
    private static byte[] Fixture(string filter)
    {
        var formData = filter == "/Nonexistent"
            ? Encoding.ASCII.GetBytes($"BT /F1 12 Tf 10 10 Td ({FormTerm}) Tj ET")
            : Encoding.ASCII.GetBytes("this is not zlib data at all");
        return RecoveryFixtureBuilder.Build(
            $"BT /F1 12 Tf 72 700 Td ({Term}) Tj ET\nq /Fx0 Do Q\n",
            extraObjects: new List<Obj>
            {
                new("<< /Type /XObject /Subtype /Form /BBox [0 0 300 100] /Matrix [1 0 0 1 60 680] " +
                    $"/Resources << /Font << /F1 5 0 R >> >> /Filter {filter} /Length {formData.Length} >>",
                    formData),
            },
            resourcesExtra: "/XObject << /Fx0 7 0 R >>");
    }

    public static TheoryData<string, string> Cases() => new()
    {
        { "/Nonexistent", "RedactText" },
        { "/Nonexistent", "RedactArea" },
        { "/FlateDecode", "RedactText" },
        { "/FlateDecode", "RedactArea" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void RemovesTheReadableText_KeepsAndReportsTheForm(string filter, string entry)
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf is the structure oracle (brew install qpdf)");
        var input = Fixture(filter);
        using var doc = PdfDocument.Open(input);

        // Before #1863 both entry points threw InvalidOperationException from
        // TextExtractor.RunFormXObject reading the form's DecodedData.
        var report = entry == "RedactText"
            ? doc.RedactText(Term, RedactionOptions.Default)
            : doc.GetPage(1).RedactAreaWithReport(new PdfRectangle(60, 690, 250, 720), RedactionOptions.Default);
        var output = Path.Combine(_dir, "output.pdf");
        doc.Save(output);

        report.Carriers.Should().ContainSingle(c => c.Carrier == "form XObject 7 0 R on page(s) 1")
            .Which.Should().Match<CarrierResult>(c => !c.Scrubbed
                && c.RefusedReason!.Contains($"/Filter {filter} could not be decoded")
                && c.RefusedReason.Contains("was not examined"));
        report.IsCleanSuccess.Should().BeFalse("a form excise could not read was left in place");

        SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(output), Term).Should().BeEmpty(
            "the readable page text is removed even though the form beside it could not be decoded");

        AssertQpdfNoWorseThan(input, output);
    }

    [Fact]
    public void RedactText_TermOnlyInTheUndecodableForm_IsReportedAndKept()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf is the structure oracle (brew install qpdf)");
        var input = Fixture("/Nonexistent");
        var output = Path.Combine(_dir, "form-only.pdf");
        using var doc = PdfDocument.Open(input);

        var report = doc.RedactText(FormTerm, RedactionOptions.Default);
        doc.Save(output);

        report.MatchesLocated.Should().Be(0, "the term is only inside a form excise cannot decode");
        report.Carriers.Should().ContainSingle(c => c.Carrier == "form XObject 7 0 R on page(s) 1" && !c.Scrubbed);
        report.IsCleanSuccess.Should().BeFalse("reporting clean here is rule 5's silent success");
        SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(output), FormTerm).Should().NotBeEmpty(
            "the form is kept and reported, never stripped in silence");
        AssertQpdfNoWorseThan(input, output);
    }

    [Fact]
    public void RedactText_UndecodableXmpPacket_IsReportedAndTheRedactionContinues()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf is the structure oracle (brew install qpdf)");
        var xmp = Encoding.ASCII.GetBytes("<x:xmpmeta xmlns:x='adobe:ns:meta/'/>");
        var output = Path.Combine(_dir, "xmp.pdf");
        var input = RecoveryFixtureBuilder.Build(
            $"BT /F1 12 Tf 72 700 Td ({Term}) Tj ET\n",
            extraObjects: new List<Obj>
            {
                new($"<< /Type /Metadata /Subtype /XML /Filter /Nonexistent /Length {xmp.Length} >>", xmp),
            },
            pageExtra: "/Metadata 7 0 R");
        using var doc = PdfDocument.Open(input);

        var report = doc.RedactText(Term, RedactionOptions.Default);
        doc.Save(output);

        report.Carriers.Should().ContainSingle(c => c.Carrier == "XMP /Metadata")
            .Which.RefusedReason.Should().Contain("could not be decoded");
        SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(output), Term).Should().BeEmpty();
        AssertQpdfNoWorseThan(input, output);
    }

    [Fact]
    public void RedactText_FontWithUndecodableEncodingCMap_DoesNotAbortTheRedaction()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf is the structure oracle (brew install qpdf)");
        const string formContent = "BT /F2 12 Tf 72 600 Td <0041> Tj ET";
        var output = Path.Combine(_dir, "font.pdf");
        var input = RecoveryFixtureBuilder.Build(
            $"BT /F1 12 Tf 72 700 Td ({Term}) Tj ET\nq /Fx1 Do Q\n",
            extraObjects: new List<Obj>
            {
                new("<< /Type /XObject /Subtype /Form /BBox [0 0 612 792] /Resources << /Font << /F2 8 0 R >> >> " +
                    $"/Length {formContent.Length} >>", Encoding.ASCII.GetBytes(formContent)),
                new("<< /Type /Font /Subtype /Type0 /BaseFont /Foo /Encoding 9 0 R /DescendantFonts [10 0 R] >>"),
                new("<< /Type /CMap /Filter /Nonexistent /Length 7 >>", Encoding.ASCII.GetBytes("garbage")),
                new("<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Foo " +
                    "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /DW 1000 >>"),
            },
            resourcesExtra: "/XObject << /Fx1 7 0 R >>");
        using var doc = PdfDocument.Open(input);

        // Before #1863 the walker's second read of the /Encoding stream threw.
        var report = doc.RedactText(Term, RedactionOptions.Default);
        doc.Save(output);

        report.VerifiedRemovals.Should().Be(1);
        SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(output), Term).Should().BeEmpty();
        AssertQpdfNoWorseThan(input, output);
    }

    /// <summary>
    /// qpdf reads the one page and warns about nothing it did not already warn
    /// about in the input: for corrupt Flate, the planted stream it cannot
    /// inflate either, which the redaction keeps.
    /// </summary>
    private void AssertQpdfNoWorseThan(byte[] input, string output)
    {
        var inputPath = Path.Combine(_dir, "qpdf-input.pdf");
        File.WriteAllBytes(inputPath, input);
        static int Warnings(string text) => text.Split('\n').Count(line => line.Contains("WARNING"));

        QpdfReferenceTool.PageCount(output).Should().Be(1);
        var before = QpdfReferenceTool.Check(inputPath);
        var after = QpdfReferenceTool.Check(output);
        before.Should().NotBeNull();
        after.Should().NotBeNull();
        after!.Value.Success.Should().BeTrue(after.Value.Output);
        Warnings(after.Value.Output).Should().BeLessThanOrEqualTo(Warnings(before!.Value.Output), after.Value.Output);
    }
}
