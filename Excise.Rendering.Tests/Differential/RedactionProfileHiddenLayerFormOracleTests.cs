using System;
using System.IO;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Tests.Redaction.Recovery;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using Excise.TestSupport;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1868: a form drawn only inside an OFF layer leaves the file with the layer,
/// read back by tools that are not excise. <c>RedactionProfileTests</c> holds
/// the report assertions; this is where qpdf and mutool are.
/// </summary>
/// <remarks>
/// mutool's page text cannot see the leak: once the span is gone nothing draws
/// the form, with or without the fix. qpdf's dump decodes every stream in the
/// file, drawn or not, so it is the independent reader of the hidden-only
/// shape; mutool reads the shape where the form is also drawn visibly.
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

    private string Redact(bool drawnVisiblyToo, string entry, bool maximum)
    {
        var path = Path.Combine(_dir, $"{drawnVisiblyToo}-{entry}-{maximum}.pdf");
        var options = maximum ? RedactionOptions.Maximum : RedactionOptions.Default;
        using (var doc = PdfDocument.Open(RecoveryFixtureBuilder.FormInHiddenLayer(Token, drawnVisiblyToo)))
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
}
