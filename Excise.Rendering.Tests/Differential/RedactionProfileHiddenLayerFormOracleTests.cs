using System;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Tests.Redaction.Recovery;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using Excise.TestSupport;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1868, #1872: an XObject drawn only inside an OFF layer (a form or an image)
/// leaves the file with the layer, read back by tools that are not excise. <c>RedactionProfileTests</c> holds the
/// report assertions; this is where qpdf and mutool are.
/// </summary>
/// <remarks>
/// mutool's page text cannot see the leak: nothing draws the XObject, with or
/// without the fix. qpdf's dump decodes every stream in the file, drawn or not,
/// so it is the independent reader of these shapes; mutool reads the shape where
/// the form is also drawn visibly, and its render shows the page did not change.
/// </remarks>
public sealed class RedactionProfileHiddenLayerFormOracleTests : IDisposable
{
    private const string Token = "HIDDENONLYFORMTRAP";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"hidden-form-oracle-{Guid.NewGuid():N}");

    public RedactionProfileHiddenLayerFormOracleTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Redact(bool drawnVisiblyToo, string entry, bool maximum) =>
        Redact(RecoveryFixtureBuilder.FormInHiddenLayer(Token, drawnVisiblyToo), $"{drawnVisiblyToo}", entry, maximum);

    private string Redact(byte[] input, string shape, string entry, bool maximum)
    {
        var path = Path.Combine(_dir, $"{shape}-{entry}-{maximum}.pdf");
        var options = maximum ? RedactionOptions.Maximum : RedactionOptions.Default;
        using (var doc = PdfDocument.Open(input))
        {
            if (entry == "area")
                doc.GetPage(1).RedactAreaWithReport(new PdfRectangle(60, 690, 200, 720), options);
            else
                doc.RedactText(entry, options);
            doc.Save(path);
        }
        var check = QpdfReferenceTool.Check(path);
        check.Should().NotBeNull();
        check!.Value.Success.Should().BeTrue($"qpdf --check: {check.Value.Output}");
        return path;
    }

    [Theory]
    [InlineData("VISIBLE", false)]
    [InlineData(Token, false)]
    [InlineData("area", false)]
    [InlineData("VISIBLE", true)]
    [InlineData(Token, true)]
    [InlineData("area", true)]
    public void AFormDrawnOnlyInAHiddenLayer_IsInNoReadersView(string entry, bool maximum)
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable && MutoolReferenceRenderer.IsAvailable,
            "qpdf and mutool are the independent readers (brew install qpdf mupdf-tools)");

        var path = Redact(drawnVisiblyToo: false, entry, maximum);

        MutoolTextExtractor.ExtractPage(path, 1).Should().NotBeNull().And.NotContain(Token);
        CarrierTrapIndependentCorroborationTests.QpdfDump(path).Should().NotContain(Token,
            "the form's only Do was in the removed span, so the form leaves the file with it");
        SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(path), Token).Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFormAlsoDrawnVisibly_StaysUntilItsOwnTextIsRedacted(bool maximum)
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable && MutoolReferenceRenderer.IsAvailable,
            "qpdf and mutool are the independent readers (brew install qpdf mupdf-tools)");

        var kept = Redact(drawnVisiblyToo: true, "VISIBLE", maximum);
        MutoolTextExtractor.ExtractPage(kept, 1).Should().Contain(Token,
            "the visible draw is page content: redacting another word must not take it");

        var redacted = Redact(drawnVisiblyToo: true, Token, maximum);
        MutoolTextExtractor.ExtractPage(redacted, 1).Should().NotContain(Token);
        CarrierTrapIndependentCorroborationTests.QpdfDump(redacted).Should().NotContain(Token);
        SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(redacted), Token).Should().BeEmpty();
    }

    private const string ImageToken = "IMAGEPIXELTRAPXX";

    /// <summary>
    /// #1872: an image drawn only in an OFF layer leaves the file, and mutool
    /// renders the page as it rendered the input. The render is compared where
    /// nothing else changes the page: a term with no match draws no box.
    /// </summary>
    [Theory]
    [InlineData("image", "NOMATCHXYZ", false)]
    [InlineData("image", "VISIBLE", false)]
    [InlineData("image", "area", false)]
    [InlineData("image", "NOMATCHXYZ", true)]
    [InlineData("image", "area", true)]
    public void AnXObjectNoContentStreamDraws_IsInNoReadersView(string shape, string entry, bool maximum)
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable && MutoolReferenceRenderer.IsAvailable,
            "qpdf and mutool are the independent readers (brew install qpdf mupdf-tools)");
        var (input, token) = (RecoveryFixtureBuilder.ImageInHiddenLayer(ImageToken), ImageToken);
        var original = Path.Combine(_dir, $"{shape}-input.pdf");
        File.WriteAllBytes(original, input);
        CarrierTrapIndependentCorroborationTests.QpdfDump(original).Should().Contain(token,
            "the fixture carries the token where qpdf reads it");

        var path = Redact(input, shape, entry, maximum);

        CarrierTrapIndependentCorroborationTests.QpdfDump(path).Should().NotContain(token,
            "nothing draws the object, so it leaves the file");
        SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(path), token).Should().BeEmpty();
        if (entry is "NOMATCHXYZ")
        {
            using var before = MutoolReferenceRenderer.RenderPage(original, 1, 72);
            using var after = MutoolReferenceRenderer.RenderPage(path, 1, 72);
            before.Should().NotBeNull();
            after.Should().NotBeNull();
            after!.Bytes.SequenceEqual(before!.Bytes).Should().BeTrue("no reader drew the object, so the page is unchanged");
        }
    }
}
