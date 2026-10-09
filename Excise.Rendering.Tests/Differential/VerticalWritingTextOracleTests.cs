using System;
using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1902 — whole-page text of vertical (Identity-V) writing against Poppler.
/// Two columns read right-to-left must come out one line per column, not one
/// line per glyph. pdftotext is the oracle: mutool (1.2x) places every
/// vertical glyph on its own stext line, which is the defect this pins.
/// </summary>
public class VerticalWritingTextOracleTests
{
    [Fact]
    public void IdentityV_TwoColumns_PageTextLinesMatchPdftotext()
    {
        Assert.SkipUnless(PdftotextTextExtractor.IsAvailable, "pdftotext not present");

        var path = Path.Combine(Path.GetTempPath(), $"excise-vertical-oracle-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, TwoColumnIdentityVPdf());

            string pageText;
            using (var doc = PdfDocument.Open(path))
                pageText = doc.GetPage(1).Text;
            var poppler = PdftotextTextExtractor.ExtractPage(path, 1);
            poppler.Should().NotBeNull("pdftotext must read the fixture");

            Lines(pageText).Should().Equal(new[] { "日本語", "漢字、" });
            Lines(pageText).Should().Equal(Lines(poppler!),
                "each vertical column is one line of text, as Poppler reads it");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string[] Lines(string text) =>
        text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();

    /// <summary>
    /// Identity-V Type0 font, /ToUnicode /Identity-H so codes decode as
    /// UTF-16BE, and a FontDescriptor so independent tools accept the font.
    /// The second column stands <paramref name="secondColumnX"/> across the page.
    /// </summary>
    internal static byte[] TwoColumnIdentityVPdf(int secondColumnX = 200)
    {
        var content =
            $"BT /F0 24 Tf 1 0 0 1 300 700 Tm <65E5672C8A9E> Tj 1 0 0 1 {secondColumnX} 700 Tm <6F225B573001> Tj ET";
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[6];
        void Obj(int n, string body) { offsets[n] = sb.Length; sb.Append($"{n} 0 obj {body} endobj\n"); }

        Obj(1, "<</Type/Catalog/Pages 2 0 R>>");
        Obj(2, "<</Type/Pages/Count 1/Kids[3 0 R]>>");
        Obj(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</Font<</F0 4 0 R>>>>/Contents 5 0 R>>");
        Obj(4, "<</Type/Font/Subtype/Type0/BaseFont/Test/Encoding/Identity-V/ToUnicode/Identity-H" +
               "/DescendantFonts[<</Type/Font/Subtype/CIDFontType2/BaseFont/Test" +
               "/CIDSystemInfo<</Registry(Adobe)/Ordering(Identity)/Supplement 0>>/DW 1000" +
               "/FontDescriptor<</Type/FontDescriptor/FontName/Test/Flags 4/FontBBox[0 -120 1000 880]" +
               "/ItalicAngle 0/Ascent 880/Descent -120/CapHeight 700/StemV 80>>>>]>>");
        Obj(5, $"<</Length {content.Length}>>\nstream\n{content}\nendstream");

        var xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        for (var i = 1; i <= 5; i++) sb.Append($"{offsets[i]:D10} 00000 n \n");
        sb.Append($"trailer <</Size 6/Root 1 0 R>>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
