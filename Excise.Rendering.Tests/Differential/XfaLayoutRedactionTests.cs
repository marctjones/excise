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
    public void RedactingAnXfaFormExciseDidNotLayOut_AlsoRemovesItsXfa()
    {
        // #1574 widened decision 5: any XFA packet restates the form's values,
        // so any redaction removes it — not only one of a form excise laid out.
        using var document = PdfDocument.Open(XfaTestForms.BuildPdf(
            XfaTestForms.PositionedTemplate(), XfaTestForms.Data("<FullName>Plain</FullName>")));

        var report = document.RedactText("Placeholder-term-not-present");

        document.DetectXfaForm().Should().Be(PdfXfaFormKind.None);
        report.Carriers.Should().Contain(c => c.Carrier.StartsWith("/XFA (dynamic XFA form", StringComparison.Ordinal));
    }

    /// <summary>
    /// #1574's reproduction: area redaction of a STATIC XFA form removed the
    /// field value from the page and the widget but left it in the XFA
    /// datasets, where Acrobat merges it back. Checked with the saved bytes,
    /// mutool (text and catalog), and Poppler (text and raster): the value is
    /// gone everywhere, the XFA form is gone, and the field outside the box
    /// still shows.
    /// </summary>
    [Fact]
    public void StaticXfaForm_AreaRedaction_RemovesTheDatasets_AndKeepsTheOtherField()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        Assert.SkipUnless(PdftotextTextExtractor.IsAvailable, "pdftotext (poppler) not installed");
        Assert.SkipUnless(PdftocairoReferenceRenderer.IsAvailable, "pdftocairo (poppler) not installed");

        const string city = "Springfield";
        var input = XfaTestForms.BuildStaticPdf($"Zanzibar {Secret}", city, $"{Secret}-notes");
        var inputPath = Path.Combine(Path.GetTempPath(), $"excise-static-xfa-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(inputPath, input);
        _temp.Add(inputPath);

        // Guards: the oracles see the leak and the second field BEFORE.
        SavedPdfLeakScanner.FindTerm(input, Secret).Should().NotBeEmpty();
        MutoolShow(inputPath, "trailer/Root/AcroForm/XFA").Should().NotBe("null");
        MutoolTextExtractor.ExtractPage(inputPath, 1).Should().Contain(Secret).And.Contain(city);

        using var document = PdfDocument.Open(input);
        document.DetectXfaForm().Should().Be(PdfXfaFormKind.Static, "fixture sanity");
        document.Pages[0].RedactArea(new PdfRectangle(90, 590, 410, 640));

        var path = Save(document);
        AssertNoXfaSurvives(path, File.ReadAllBytes(path));
        PdftotextTextExtractor.ExtractPage(path, 1).Should().NotContain(Secret)
            .And.Contain(city, "the AcroForm field outside the box still shows in Poppler");
        MutoolTextExtractor.ExtractPage(path, 1).Should().Contain(city).And.Contain("Name:");

        using var raster = PdftocairoReferenceRenderer.RenderPage(path, 1, 72);
        raster.Should().NotBeNull("pdftocairo must render the redacted form");
        DarkPixels(raster!, 100, 792 - 530, 400, 792 - 500).Should().BeGreaterThan(20,
            "pdftocairo still draws the City field's appearance");
    }

    private static int DarkPixels(SkiaSharp.SKBitmap bitmap, int left, int top, int right, int bottom)
    {
        var count = 0;
        for (var y = Math.Max(0, top); y < Math.Min(bitmap.Height, bottom); y++)
        for (var x = Math.Max(0, left); x < Math.Min(bitmap.Width, right); x++)
        {
            var c = bitmap.GetPixel(x, y);
            if (c.Red + c.Green + c.Blue < 3 * 128)
                count++;
        }
        return count;
    }
}
