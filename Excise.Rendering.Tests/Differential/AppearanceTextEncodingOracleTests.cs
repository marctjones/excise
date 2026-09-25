using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Rendering.Differential;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// Text excise writes into appearance streams (FreeText, visible signature,
/// flattened form field) must reach the file as WinAnsi bytes under a font that
/// declares WinAnsiEncoding, not as '?' (#1835). The saved stream is checked
/// for the octal escapes, and mutool, which shares no code with excise, must
/// read the accented text back: a tool must not be its own oracle.
/// </summary>
public class AppearanceTextEncodingOracleTests : IDisposable
{
    // "José Ñandú €" in WinAnsi: é=0xE9, Ñ=0xD1, ú=0xFA, €=0x80.
    private const string Text = "José Ñandú €";
    private const string TextAsWinAnsiLiteral = @"(Jos\351 \321and\372 \200)";

    private readonly List<string> _temp = new();

    [Fact]
    public void FreeTextAppearance_WritesWinAnsiBytes_AndMutoolReadsTheText()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        byte[] saved;
        using (var doc = PdfDocument.CreateNew())
        {
            doc.Pages.AddBlank();
            doc.AddFreeTextAnnotation(1, new PdfRectangle(100, 480, 400, 560), Text, fontSize: 14);
            saved = doc.SaveToBytes();
        }

        MutoolTextExtractor.ExtractPage(SaveTemp(saved), 1).Should().Contain(Text);

        using var reopened = PdfDocument.Open(saved);
        var annotation = reopened.GetPage(1).GetAnnotations().Single();
        AssertWinAnsiAppearance(reopened, annotation.RawDictionary);

    }

    [Fact]
    public void SignatureAppearance_WritesWinAnsiBytes_AndMutoolReadsTheText()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        byte[] saved;
        using (var doc = PdfDocument.CreateNew())
        {
            doc.Pages.AddBlank();
            var widget = new PdfDictionary();
            widget.SetName("Type", "Annot");
            widget.SetName("Subtype", "Widget");
            widget.SetName("FT", "Sig");
            widget.SetString("T", "Sig1");
            widget["Rect"] = PdfArray.FromRectangle(100, 600, 400, 660);
            // mutool draws its own placeholder for an unsigned signature field and
            // shows the /AP only once the field carries a /V.
            var signature = new PdfDictionary();
            signature.SetName("Type", "Sig");
            signature.SetName("Filter", "Adobe.PPKLite");
            widget["V"] = signature;
            SignatureAppearanceAuthoring.ApplyVisibleAppearance(doc, widget, new[] { Text });
            var widgetRef = doc.AddIndirectObject(widget);
            doc.GetPage(1).Dictionary["Annots"] = new PdfArray(widgetRef);
            var acroForm = new PdfDictionary();
            acroForm["Fields"] = new PdfArray(widgetRef);
            doc.Catalog["AcroForm"] = acroForm;
            saved = doc.SaveToBytes();
        }

        MutoolTextExtractor.ExtractPage(SaveTemp(saved), 1).Should().Contain(Text);

        using var reopened = PdfDocument.Open(saved);
        var annotation = reopened.GetPage(1).GetAnnotations().Single();
        AssertWinAnsiAppearance(reopened, annotation.RawDictionary);

    }

    [Fact]
    public void FlattenedFormField_WritesWinAnsiBytes_AndMutoolReadsTheText()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        byte[] saved;
        using (var doc = PdfDocument.CreateNew())
        {
            doc.Pages.AddBlank();
            AcroFormAuthoring.AddTextField(doc, 1, new PdfRectangle(100, 600, 400, 630), "applicant.name");
            doc.GetAcroForm()!.FindField("applicant.name")!.SetValue(Text);
            doc.FlattenAcroForm();
            saved = doc.SaveToBytes();
        }

        MutoolTextExtractor.ExtractPage(SaveTemp(saved), 1).Should().Contain(Text);

        using var reopened = PdfDocument.Open(saved);
        var content = Encoding.Latin1.GetString(reopened.GetPage(1).GetContentStreamBytes());
        content.Should().Contain(TextAsWinAnsiLiteral);

        var fontName = System.Text.RegularExpressions.Regex.Match(content, @"/(\S+) 10 Tf").Groups[1].Value;
        var fonts = (PdfDictionary)reopened.Resolve(reopened.GetPage(1).Resources!["Font"]);
        ((PdfDictionary)reopened.Resolve(fonts[fontName])).GetName("Encoding").Should().Be("WinAnsiEncoding");

    }

    [Fact]
    public void FlattenedFormField_OnPageWhoseHelveticaDeclaresNoEncoding_StillReadsAsWinAnsi()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        byte[] saved;
        using (var doc = PdfDocument.CreateNew())
        {
            var page = doc.Pages.AddBlank();
            var helvetica = new PdfDictionary();
            helvetica.SetName("Type", "Font");
            helvetica.SetName("Subtype", "Type1");
            helvetica.SetName("BaseFont", "Helvetica");
            var fonts = new PdfDictionary();
            fonts["F1"] = helvetica;
            var resources = new PdfDictionary();
            resources["Font"] = fonts;
            page.Dictionary["Resources"] = resources;

            AcroFormAuthoring.AddTextField(doc, 1, new PdfRectangle(100, 600, 400, 630), "applicant.name");
            doc.GetAcroForm()!.FindField("applicant.name")!.SetValue(Text);
            doc.FlattenAcroForm();
            saved = doc.SaveToBytes();
        }

        // A same-named page font without /Encoding would read 0xE9 as Oslash (StandardEncoding).
        MutoolTextExtractor.ExtractPage(SaveTemp(saved), 1).Should().Contain(Text);
    }

    private static void AssertWinAnsiAppearance(PdfDocument doc, PdfDictionary annotation)
    {
        var ap = (PdfDictionary)doc.Resolve(annotation["AP"]);
        var stream = (PdfStream)doc.Resolve(ap["N"]);
        Encoding.ASCII.GetString(stream.DecodedData).Should().Contain(TextAsWinAnsiLiteral);

        var fonts = (PdfDictionary)((PdfDictionary)stream["Resources"])["Font"];
        var helv = (PdfDictionary)doc.Resolve(fonts["Helv"]);
        helv.GetName("Encoding").Should().Be("WinAnsiEncoding",
            "bytes above 0x7F are only WinAnsi under a font that declares it");
    }

    private string SaveTemp(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-appearance-text-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, bytes);
        _temp.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var path in _temp)
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }
}
