using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Authoring;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Excise.Core.Primitives;
using Excise.Core.Tests.Fixtures;
using Xunit;

namespace Excise.Core.Tests.Document;

/// <summary>
/// #1435 — a widget's <c>/DA</c> (default appearance) names the font a viewer
/// uses when it generates the field's appearance from the field value. That
/// name used to be <c>/Helv</c> unconditionally, so a document built with an
/// embedded <see cref="PdfDocumentBuilder.DefaultFont"/> — including one
/// declaring itself PDF/A via <see cref="PdfDocumentBuilder.PdfA"/> — still
/// shipped a non-embedded base-14 <c>/BaseFont /Helvetica</c> dictionary and
/// pointed every form field at it. PDF/A forbids non-embedded base-14 fonts,
/// so the file's own conformance claim was false.
///
/// <para>⚠️ veraPDF does NOT catch this and cannot be used as the gate:
/// measured on the issue's repro, the PDF/A-2b profile reports the widget's
/// missing <c>/AP</c>, missing <c>/F</c> and <c>/NeedAppearances true</c>
/// (all tracked separately) but says nothing about the /DR font — its
/// font-embedding rules score fonts REACHED THROUGH a content stream, and a
/// /DA font with no appearance stream is reached through none. The leak is
/// therefore asserted structurally, on the bytes we wrote.</para>
/// </summary>
public class WidgetDefaultAppearanceFontTests
{
    /// <summary>The issue's repro, verbatim in shape.</summary>
    private static byte[] BuildPdfAWithField() =>
        PdfDocumentBuilder.Create()
            .DefaultFont(PdfFont.FromTrueType(TestFontFixtures.LoadDejaVuSansBytes(), 11))
            .PdfA()
            .Paragraph("Hello — café · naïve")
            .TextField("Name", "name")
            .SaveToBytes();

    [Fact]
    public void PdfAWithFormField_EmbedsNoBase14FontForTheWidgetAppearance()
    {
        var raw = Encoding.Latin1.GetString(BuildPdfAWithField());

        raw.Should().NotContain("/BaseFont /Helvetica",
            "PDF/A forbids non-embedded base-14 fonts; a form field must not smuggle "
            + "one in through the widget's /DA font resource (#1435)");
        raw.Should().NotContain("/Helv 10 Tf",
            "the widget /DA must name the document's embedded font, not the base-14 fallback");
    }

    [Fact]
    public void PdfAWithFormField_WidgetDefaultAppearanceNamesTheEmbeddedFont()
    {
        using var doc = PdfDocument.Open(BuildPdfAWithField());

        var field = doc.GetAcroForm()!.FindField("name")!;
        var da = field.RawDictionary.GetStringOrNull("DA");
        da.Should().NotBeNull();

        // "/F1 11 Tf 0 g" -> "F1"
        var resourceName = da!.TrimStart().Split(' ')[0].TrimStart('/');
        resourceName.Should().NotBe("Helv");

        var drFonts = DefaultResourceFonts(doc);
        drFonts.ContainsKey(resourceName).Should().BeTrue(
            "a /DA font name that does not resolve in /DR leaves the viewer to guess");

        var fontDict = (PdfDictionary)doc.Resolve(drFonts[resourceName]);
        fontDict.GetName("Subtype").Should().Be("Type0");
        DescriptorOf(doc, fontDict).ContainsKey("FontFile2").Should().BeTrue(
            "the /DA font must carry its font program — that is the whole point of #1435");
    }

    /// <summary>
    /// The embedded program must be written ONCE. The /DR entry shares the page
    /// resource's object rather than building a second copy of the same font.
    /// </summary>
    [Fact]
    public void PdfAWithFormField_EmbedsTheFontProgramOnlyOnce()
    {
        var raw = Encoding.Latin1.GetString(BuildPdfAWithField());

        System.Text.RegularExpressions.Regex.Matches(raw, @"/FontFile2\b").Count.Should().Be(1,
            "the widget /DA font and the page's body font are the same font — embedding it "
            + "twice would double the file size for nothing");
    }

    /// <summary>
    /// The subset is built from the glyphs WE encoded. A /DA font's glyphs are
    /// chosen by the viewer from what the user types, so the subset has to keep
    /// more than the document happens to draw — otherwise typing into the field
    /// produces .notdef boxes.
    /// </summary>
    [Fact]
    public void WidgetAppearanceFont_SubsetKeepsGlyphsTheDocumentNeverDrew()
    {
        using var doc = PdfDocument.Open(BuildPdfAWithField());
        var field = doc.GetAcroForm()!.FindField("name")!;
        var resourceName = field.RawDictionary.GetStringOrNull("DA")!.TrimStart().Split(' ')[0].TrimStart('/');
        var fontDict = (PdfDictionary)doc.Resolve(DefaultResourceFonts(doc)[resourceName]);

        var toUnicode = (PdfStream)doc.Resolve(fontDict.GetOptional("ToUnicode")!);
        var cmap = toUnicode.GetDecodedString(Encoding.ASCII);

        // 'Z' (U+005A) and 'ü' (U+00FC) appear nowhere in the drawn text, so
        // finding them in the ToUnicode CMap means the subset reserved them.
        cmap.Should().Contain("<005A>", "the reserved ASCII range must survive subsetting (#1435)");
        cmap.Should().Contain("<00FC>", "the reserved Latin-1 range must survive subsetting (#1435)");
    }

    /// <summary>
    /// The control: with no embedded default font there is nothing better to
    /// name, so the historical <c>/Helv</c> behaviour is unchanged — including
    /// the /DR entry that makes the name resolve.
    /// </summary>
    [Fact]
    public void WithoutADefaultFont_TheWidgetStillUsesHelv()
    {
        using var doc = PdfDocument.Open(
            PdfDocumentBuilder.Create().Paragraph("plain").TextField("Name", "name").SaveToBytes());

        var field = doc.GetAcroForm()!.FindField("name")!;
        field.RawDictionary.GetStringOrNull("DA").Should().Be("/Helv 10 Tf 0 g");

        var fonts = DefaultResourceFonts(doc);
        fonts.ContainsKey("Helv").Should().BeTrue();
        ((PdfDictionary)doc.Resolve(fonts["Helv"])).GetName("BaseFont").Should().Be("Helvetica");
    }

    /// <summary>
    /// Two fields sharing one font share one /DR entry — the resource dictionary
    /// must not grow an entry per field.
    /// </summary>
    [Fact]
    public void RepeatedFields_ShareOneDefaultResourceFontEntry()
    {
        using var doc = PdfDocument.Open(
            PdfDocumentBuilder.Create()
                .DefaultFont(PdfFont.FromTrueType(TestFontFixtures.LoadDejaVuSansBytes(), 11))
                .TextField("One", "one")
                .TextField("Two", "two")
                .Dropdown("Three", ["a", "b"], "three")
                .SaveToBytes());

        DefaultResourceFonts(doc).Count().Should().Be(1);
    }

    private static PdfDictionary DefaultResourceFonts(PdfDocument doc)
    {
        var form = (PdfDictionary)doc.Resolve(doc.Catalog.GetOptional("AcroForm")!);
        var dr = (PdfDictionary)doc.Resolve(form.GetOptional("DR")!);
        return (PdfDictionary)doc.Resolve(dr.GetOptional("Font")!);
    }

    private static PdfDictionary DescriptorOf(PdfDocument doc, PdfDictionary type0)
    {
        var descendants = (PdfArray)doc.Resolve(type0.GetOptional("DescendantFonts")!);
        var cid = (PdfDictionary)doc.Resolve(descendants.First());
        return (PdfDictionary)doc.Resolve(cid.GetOptional("FontDescriptor")!);
    }
}
