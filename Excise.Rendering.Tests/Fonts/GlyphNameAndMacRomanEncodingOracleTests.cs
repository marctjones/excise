using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Fonts;

/// <summary>
/// #1831: the renderer and the text extractor answered "which character is this
/// glyph name / this MacRoman code" from different tables. Where they disagreed
/// the renderer drew a glyph that extraction, and therefore redaction, read
/// differently or not at all. Each fixture here is a non-embedded Helvetica, so
/// the answer comes from the encoding alone, and excise's extraction is checked
/// against an independent tool, not against itself.
/// </summary>
public sealed class GlyphNameAndMacRomanEncodingOracleTests
{
    private readonly ITestOutputHelper _out;

    public GlyphNameAndMacRomanEncodingOracleTests(ITestOutputHelper output) => _out = output;

    // Names only the renderer's table used to know, plus Delta, where the two
    // tables disagreed (U+2206 vs U+0394). a109-a112 are separate: mutool reads
    // an "aNNN" name as the byte NNN.
    private static readonly (string Name, char Expected)[] Names =
    {
        ("Delta", '∆'), ("caron", 'ˇ'), ("breve", '˘'), ("ring", '˚'),
        ("ogonek", '˛'), ("hungarumlaut", '˝'), ("dotaccent", '˙'),
        ("fraction", '⁄'), ("Lslash", 'Ł'), ("lslash", 'ł'),
        ("Lcaron", 'Ľ'), ("lcaron", 'ľ'), ("lozenge", '◊'), ("integral", '∫'),
    };

    private static readonly (string Name, char Expected)[] DingbatNames =
    {
        ("a109", '♠'), ("a110", '♥'), ("a111", '♦'), ("a112", '♣'),
    };

    // Codes Annex D, Table D.2 leaves unassigned in the MAC column (mutool falls
    // back to StandardEncoding there, poppler and excise to Mac OS Roman), 0xCA
    // (note 6's non-breaking space, which mutool writes as U+0020), and fi/fl
    // (mutool splits the ligature).
    private static readonly HashSet<int> MacRomanCodesMutoolReadsDifferently = new()
    {
        0xAD, 0xB0, 0xB2, 0xB3, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xBD, 0xC3, 0xC5, 0xC6, 0xD7, 0xF0,
        0xCA, 0xDE, 0xDF,
    };

    [Fact]
    public void Differences_GlyphNames_ExtractorMatchesMutoolAndTheRenderer()
    {
        var pdf = BuildPdf(DifferencesFont("Helvetica", Names.Select(n => n.Name)), CodesFrom(65, Names.Length));
        var excise = ExciseText(pdf);
        excise.Should().Be(new string(Names.Select(n => n.Expected).ToArray()));

        foreach (var (name, expected) in Names)
        {
            AdobeGlyphList.TryGet(name, out var drawn).Should().BeTrue($"the renderer must resolve /{name}");
            drawn.Should().Be(expected, $"the renderer must draw the character extraction reads for /{name}");
        }

        WithTempPdf(pdf, path =>
        {
            Assert.SkipWhen(!MutoolReferenceRenderer.IsAvailable, "mutool not installed.");
            var mutool = MutoolTextExtractor.ExtractPage(path, 1);
            Assert.SkipWhen(mutool == null, "mutool declined to extract.");
            _out.WriteLine($"excise '{excise}' mutool '{mutool!.Trim()}'");
            WithoutWhitespace(mutool).Should().Be(excise);
        });
    }

    [Fact]
    public void Differences_ZapfDingbatsNames_ExtractorMatchesPdftotextAndTheRenderer()
    {
        var pdf = BuildPdf(
            DifferencesFont("ZapfDingbats", DingbatNames.Select(n => n.Name)), CodesFrom(65, DingbatNames.Length));
        var excise = ExciseText(pdf);
        excise.Should().Be(new string(DingbatNames.Select(n => n.Expected).ToArray()));

        foreach (var (name, expected) in DingbatNames)
        {
            AdobeGlyphList.TryGet(name, out var drawn).Should().BeTrue($"the renderer must resolve /{name}");
            drawn.Should().Be(expected);
        }

        WithTempPdf(pdf, path =>
        {
            Assert.SkipWhen(!PdftotextTextExtractor.IsAvailable, "pdftotext not installed.");
            var poppler = PdftotextTextExtractor.ExtractPage(path, 1);
            Assert.SkipWhen(poppler == null, "pdftotext declined to extract.");
            _out.WriteLine($"excise '{excise}' pdftotext '{poppler!.Trim()}'");
            WithoutWhitespace(poppler).Should().Be(excise);
        });
    }

    [Fact]
    public void MacRomanEncoding_AnnexDHighCodes_ExtractorMatchesMutool()
    {
        var codes = Enumerable.Range(0x80, 0x80)
            .Where(c => !MacRomanCodesMutoolReadsDifferently.Contains(c))
            .Select(c => (byte)c)
            .ToArray();
        var pdf = BuildPdf("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /MacRomanEncoding >>", codes);
        var excise = ExciseText(pdf);
        excise.Should().HaveLength(codes.Length);
        excise.Should().Contain("¤", "Annex D note 1: MacRomanEncoding code 333 stays currency")
            .And.NotContain("€");

        WithTempPdf(pdf, path =>
        {
            Assert.SkipWhen(!MutoolReferenceRenderer.IsAvailable, "mutool not installed.");
            var mutool = MutoolTextExtractor.ExtractPage(path, 1);
            Assert.SkipWhen(mutool == null, "mutool declined to extract.");
            _out.WriteLine($"excise '{excise}'");
            _out.WriteLine($"mutool '{mutool!.Trim()}'");
            WithoutWhitespace(mutool).Should().Be(excise);
        });
    }

    [Fact]
    public void Renderer_MacRomanCode333_DrawsTheCurrencySignExtractionReads()
    {
        // WinAnsi 0xA4 and 0x80 are currency and Euro in every table; MacRoman
        // 0xDB must draw the first.
        using var macRoman = Render("/MacRomanEncoding", 0xDB);
        using var currency = Render("/WinAnsiEncoding", 0xA4);
        using var euro = Render("/WinAnsiEncoding", 0x80);

        Pixels(euro).Should().NotEqual(Pixels(currency), "the fixture must tell the two glyphs apart");
        Pixels(macRoman).Should().Equal(Pixels(currency));
    }

    private static SKBitmap Render(string encoding, byte code)
    {
        var pdf = BuildPdf($"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding {encoding} >>", new[] { code });
        using var doc = PdfDocument.Open(pdf);
        return new SkiaRenderer().RenderPage(doc.GetPage(1), new RenderOptions { Dpi = 72, BackgroundColor = SKColors.White });
    }

    private static byte[] Pixels(SKBitmap bitmap) => bitmap.Bytes;

    private static string ExciseText(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        return WithoutWhitespace(new TextExtractor(doc.GetPage(1)).ExtractText());
    }

    private static string WithoutWhitespace(string text) =>
        new(text.Where(c => !char.IsWhiteSpace(c)).ToArray());

    private static byte[] CodesFrom(int first, int count) =>
        Enumerable.Range(first, count).Select(c => (byte)c).ToArray();

    private static string DifferencesFont(string baseFont, IEnumerable<string> names) =>
        $"<< /Type /Font /Subtype /Type1 /BaseFont /{baseFont} "
        + $"/Encoding << /Type /Encoding /Differences [65 {string.Join(' ', names.Select(n => "/" + n))}] >> >>";

    private static byte[] BuildPdf(string fontDict, byte[] codes)
    {
        var content = Encoding.ASCII.GetBytes($"BT /F1 10 Tf 20 60 Td <{Convert.ToHexString(codes)}> Tj ET");
        string[] objects =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 1400 100] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{Encoding.ASCII.GetString(content)}\nendstream",
            fontDict,
        };
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        int xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    private static void WithTempPdf(byte[] pdf, Action<string> body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-1831-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdf);
        try { body(path); }
        finally { try { File.Delete(path); } catch (IOException) { } }
    }
}
