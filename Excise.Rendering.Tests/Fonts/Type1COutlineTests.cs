using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Fonts;
using Excise.Core.Primitives;
using Excise.Rendering.Differential;
using Excise.Rendering.Fonts;
using Excise.TestSupport;
using SkiaSharp;

namespace Excise.Rendering.Tests.Fonts;

/// <summary>
/// #1937: the charset is valid, but malformed Private DICT hints make the
/// macOS native backend reject this embedded CFF. This verifies the observable
/// fallback diagnostic, not a rendering fix. The disagreement remains deferred.
/// </summary>
public class Type1COutlineTests(ITestOutputHelper output)
{
    [Fact]
    public void Bug1308536_NativeCffRejection_ReportsGlyphSubstitution()
    {
        var path = TestRepoLayout.FindFile("test-pdfs", "pdfjs", "bug1308536.pdf");
        Assert.SkipWhen(path == null, TestRepoLayout.AbsenceReason("pdf.js corpus fixture", "test-pdfs/pdfjs/bug1308536.pdf")); // [requires: corpus:pdfjs]
        using var doc = PdfDocument.Open(path!);
        var page = doc.GetPage(1);
        var dict = page.GetFont("F1")!;
        var descriptor = (PdfDictionary)doc.Resolve(dict["FontDescriptor"]);
        var cff = ((PdfStream)doc.Resolve(descriptor["FontFile3"])).DecodedData;
        var info = CffParser.Parse(cff);
        info.Should().NotBeNull();
        info!.NumGlyphs.Should().Be(70);
        info.GlyphNameToIndex["C"].Should().Be(14);
        info.GlyphNameToIndex["e"].Should().Be(37);
        info.GlyphNameToIndex["l"].Should().Be(43);
        info.GlyphNameToIndex["a"].Should().Be(33);

        // Probe the platform backend without changing any font bytes. FreeType
        // accepts this CFF; the macOS backend rejects it before the outline probe.
        var mapping = new Dictionary<char, int>();
        foreach (var (name, gid) in info.GlyphNameToIndex)
            if (AdobeGlyphList.TryGet(name, out var c)) mapping[c] = gid;
        var wrapped = CffToOpenType.Wrap(cff, info.NumGlyphs, new CffToOpenType.PdfFontInfo
        {
            PsName = descriptor.GetNameOrNull("FontName")!,
            XMin = info.XMin, YMin = info.YMin, XMax = info.XMax, YMax = info.YMax,
            Ascent = 753, Descent = 105,
            UnicodeToGlyph = mapping,
        });
        wrapped.Should().NotBeNull();
        using var data = SKData.CreateCopy(wrapped!);
        using var face = SKTypeface.FromData(data);
        Assert.SkipWhen(face != null, "This platform's native font backend accepts the malformed CFF; this test covers the rejection diagnostic.");

        var diagnostics = new List<string>();
        using var actual = new SkiaRenderer().RenderPage(page, new RenderOptions { Dpi = 144, Diagnostics = diagnostics });
        diagnostics.Should().Contain(note => note.Contains("Font /F1 (BAFOKE+UltraCondensedSansTwo)")
            && note.Contains("rejected by the native font backend")
            && note.Contains("falling back to a system typeface"));
        output.WriteLine(string.Join(Environment.NewLine, diagnostics));
    }

    [Fact]
    public void Bug1308536_MutoolAndPdftocairo_AgreeOnEmbeddedGlyphs()
    {
        var path = TestRepoLayout.FindFile("test-pdfs", "pdfjs", "bug1308536.pdf");
        Assert.SkipWhen(path == null, TestRepoLayout.AbsenceReason("pdf.js corpus fixture", "test-pdfs/pdfjs/bug1308536.pdf")); // [requires: corpus:pdfjs]
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        Assert.SkipUnless(PdftocairoReferenceRenderer.IsAvailable, "pdftocairo not installed");
        using var mutool = MutoolReferenceRenderer.RenderPage(path!, 1, 144);
        using var cairo = PdftocairoReferenceRenderer.RenderPage(path!, 1, 144);
        mutool.Should().NotBeNull();
        cairo.Should().NotBeNull();
        var agreement = DifferentialMetrics.Compare(mutool!, cairo!);
        agreement.MeanAbsoluteError.Should().BeLessThan(3,
            "these independent renderers agree on recovered glyph shapes, without establishing specification correctness for malformed input (#1937)");
        using var doc = PdfDocument.Open(path!);
        using var actual = new SkiaRenderer().RenderPage(doc.GetPage(1), new RenderOptions { Dpi = 144 });
        output.WriteLine($"mutool/pdftocairo: {agreement}");
        output.WriteLine($"excise/mutool: {DifferentialMetrics.Compare(actual, mutool!)}");
        output.WriteLine($"excise/pdftocairo: {DifferentialMetrics.Compare(actual, cairo!)}");
    }
}
