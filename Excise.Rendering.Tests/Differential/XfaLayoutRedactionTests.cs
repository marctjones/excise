using System.Diagnostics;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.Core.Xfa;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1547 phase 2, decision 5: redacting a laid-out XFA form removes the XFA
/// form whole. The packet restates every value on the pages, and XFA viewers
/// (Acrobat, Firefox) regenerate the pages from it, so a redaction that left
/// the packet would be undone by the next viewer.
///
/// <para>Checked with tools that are not excise: the saved bytes, streams
/// inflated (<see cref="SavedPdfLeakScanner"/>), mutool's text, and mutool's
/// own reading of the catalog (<c>mutool show</c>).</para>
/// </summary>
public class XfaLayoutRedactionTests : IDisposable
{
    private const string Secret = "Quillfeather";
    private readonly List<string> _temp = new();

    public void Dispose()
    {
        foreach (var path in _temp)
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    private static PdfDocument LaidOutForm(string value)
    {
        var document = PdfDocument.Open(XfaTestForms.BuildPdf(
            XfaTestForms.PositionedTemplate(),
            XfaTestForms.Data($"<FullName>{value}</FullName><Unshown>{Secret}-hidden</Unshown>")));
        document.ApplyXfaLayout(cancellationToken: TestContext.Current.CancellationToken)
            .Status.Should().Be(XfaLayoutStatus.LaidOut);
        return document;
    }

    private string Save(PdfDocument document)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-xfa-redact-{Guid.NewGuid():N}.pdf");
        document.Save(path);
        _temp.Add(path);
        return path;
    }

    /// <summary><c>mutool show FILE PATH</c>, trimmed; null when mutool refuses.</summary>
    private static string? MutoolShow(string pdf, string objectPath)
    {
        var psi = new ProcessStartInfo("mutool") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "show", pdf, objectPath })
            psi.ArgumentList.Add(a);
        using var process = Process.Start(psi);
        if (process == null)
            return null;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            return null;
        }
        _ = stderr.GetAwaiter().GetResult();
        return stdout.GetAwaiter().GetResult().Trim();
    }

    private static void AssertNoXfaSurvives(string path, byte[] saved)
    {
        SavedPdfLeakScanner.FindTerm(saved, Secret).Should().BeEmpty(
            "neither the page nor the XFA packet (including values the page never showed) may keep the term");
        MutoolTextExtractor.ExtractPage(path, 1).Should().NotContain(Secret);
        MutoolShow(path, "trailer/Root/AcroForm/XFA").Should().Be("null", "mutool must find no XFA form to regenerate");
        MutoolShow(path, "trailer/Root/NeedsRendering").Should().Be("null");
    }

    [Fact]
    public void AreaRedaction_RemovesTheXfaForm()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        using var document = LaidOutForm(Secret);

        // The widget region: x 162..378, y 90..118.8 from the top of a 792pt
        // page, as a PDF rectangle (bottom-left origin).
        document.Pages[0].RedactArea(new PdfRectangle(160, 792 - 120, 380, 792 - 88));

        var path = Save(document);
        AssertNoXfaSurvives(path, File.ReadAllBytes(path));
        MutoolTextExtractor.ExtractPage(path, 1).Should().Contain("Name", "only the redacted area goes");
    }

    [Fact]
    public void TextRedaction_RemovesTheXfaForm_AndSaysSo()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        using var document = LaidOutForm($"Jane {Secret}");

        var report = document.RedactText(Secret);

        report.Carriers.Should().Contain(c => c.Carrier.StartsWith("/XFA", StringComparison.Ordinal) && c.Scrubbed);
        var path = Save(document);
        AssertNoXfaSurvives(path, File.ReadAllBytes(path));
        MutoolTextExtractor.ExtractPage(path, 1).Should().Contain("Jane");
    }

    [Fact]
    public void TextRedaction_WithCarriersOff_StillRemovesTheXfaForm()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        using var document = LaidOutForm(Secret);

        document.RedactText(Secret, scrubDocumentCarriers: false);

        var path = Save(document);
        MutoolShow(path, "trailer/Root/AcroForm/XFA").Should().Be("null",
            "the XFA form is the source of the redacted page, not an optional carrier");
    }

    [Fact]
    public void ReopeningARedactedForm_DoesNotLayItOutAgain()
    {
        using var document = LaidOutForm(Secret);
        document.RedactText(Secret);
        var saved = document.SaveToBytes();

        using var reopened = PdfDocument.Open(saved);
        reopened.DetectXfaForm().Should().Be(PdfXfaFormKind.None);
        reopened.ApplyXfaLayout(cancellationToken: TestContext.Current.CancellationToken)
            .Status.Should().Be(XfaLayoutStatus.NotDynamicXfa);
    }

    [Fact]
    public void RedactingAnOrdinaryXfaDocument_KeepsItsXfa_AsBefore()
    {
        // Not laid out by excise: the pre-#1547 behaviour (term strip) holds.
        using var document = PdfDocument.Open(XfaTestForms.BuildPdf(
            XfaTestForms.PositionedTemplate(), XfaTestForms.Data("<FullName>Plain</FullName>")));

        document.RedactText("Placeholder-term-not-present");

        document.DetectXfaForm().Should().Be(PdfXfaFormKind.Dynamic);
    }
}
