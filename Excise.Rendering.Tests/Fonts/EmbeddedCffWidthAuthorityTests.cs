using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Fonts;

/// <summary>
/// #584: for an embedded CFF/Type1C simple font, the inter-Tj cursor advance
/// used to trust Skia's own MeasureText against the wrapped embedded
/// typeface, on the assumption that "Skia loaded the real font, so its
/// metrics are correct." Per ISO 32000 9.2.4, /Widths is authoritative
/// regardless of whether the font is embedded — but a naive "PDF /Widths
/// disagrees with the font's own design width" fixture doesn't actually
/// distinguish the two mechanisms: <c>CffToOpenType.BuildHmtx</c> populates
/// the wrapped typeface's own hmtx *from that same /Widths array*, so for a
/// glyph whose code is cleanly covered by /Widths, hmtx-driven MeasureText
/// and a direct /Widths read end up numerically identical either way.
///
/// The real disagreement only appears for a PDF character code that falls
/// *outside* the /Widths[FirstChar..LastChar] range: /Widths-driven code
/// falls back to the PDF's own /MissingWidth (spec-correct), while
/// BuildHmtx's fallback for a glyph with no /Widths-derived entry is a
/// hardcoded stub of 500 units — unrelated to /MissingWidth and, for a
/// real-world subsetted font, exactly why an em-dash reachable only via an
/// out-of-range code collided with the following word (see #584's
/// diagnosis notes). This fixture reproduces that precise disagreement:
/// 'A' (code 65) is drawn, but /Widths only covers an unrelated code 70,
/// so /MissingWidth (a deliberately huge, easy-to-distinguish value) must
/// govern the advance.
/// </summary>
public sealed class EmbeddedCffWidthAuthorityTests
{
    [Fact]
    public void RenderPage_EmbeddedCffSimpleFont_OutOfRangeCodeAdvancesByMissingWidthNotHmtxStub()
    {
        var cffPath = FindRepoFile("Excise.Core.Tests", "Fixtures", "Fonts", "Inconsolata.cff");
        Assert.SkipWhen(cffPath == null, "Inconsolata.cff fixture not found.");
        var cffBytes = File.ReadAllBytes(cffPath!);

        // /Widths covers only code 70 ('F'); 'A' (code 65, the glyph we
        // actually draw) falls outside [FirstChar, LastChar] and must fall
        // back to /MissingWidth. /MissingWidth is deliberately huge (2000,
        // twice a full em) so it's unmistakably distinct from BuildHmtx's
        // hardcoded 500-unit stub for a glyph with no /Widths-derived hmtx
        // entry — whichever one governs the advance is obvious from where
        // the second glyph lands.
        const int missingWidthPerMille = 2000;
        const float fontSize = 12f;
        const float startX = 10f;
        const int dpi = 150;

        var pdfData = BuildTwoGlyphPdf(cffBytes, missingWidthPerMille, fontSize, startX);
        using var doc = PdfDocument.Open(pdfData);
        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = dpi, BackgroundColor = SKColors.White });

        var scale = dpi / 72f;
        var expectedSecondGlyphLeftPx = (startX + missingWidthPerMille / 1000f * fontSize) * scale;

        var inkColumns = FindInkColumnRuns(bitmap);
        inkColumns.Should().HaveCount(2,
            "two separate (A) Tj calls should paint two distinct glyph ink runs, not overlap into one");

        var secondGlyphLeftPx = (float)inkColumns[1].start;
        secondGlyphLeftPx.Should().BeInRange(
            expectedSecondGlyphLeftPx - 6, expectedSecondGlyphLeftPx + 6,
            "#584: 'A' (code 65) falls outside the font's /Widths[FirstChar..LastChar] range, so " +
            "the advance must come from the PDF's own /MissingWidth (2000/1000 em) — not from " +
            "CffToOpenType.BuildHmtx's unrelated hardcoded 500-unit stub for glyphs the /Widths " +
            "population loop never reached, which is what the pre-#584 code trusted for embedded fonts.");
    }

    /// <summary>Runs of contiguous non-white columns, as (start, end) pixel ranges, scanning the whole bitmap.</summary>
    private static List<(int start, int end)> FindInkColumnRuns(SKBitmap bitmap)
    {
        var isInk = new bool[bitmap.Width];
        for (var x = 0; x < bitmap.Width; x++)
        {
            for (var y = 0; y < bitmap.Height; y++)
            {
                var p = bitmap.GetPixel(x, y);
                if (p.Red < 128 || p.Green < 128 || p.Blue < 128)
                {
                    isInk[x] = true;
                    break;
                }
            }
        }

        var runs = new List<(int, int)>();
        var inRun = false;
        var runStart = 0;
        for (var x = 0; x < bitmap.Width; x++)
        {
            if (isInk[x] && !inRun) { inRun = true; runStart = x; }
            else if (!isInk[x] && inRun) { inRun = false; runs.Add((runStart, x - 1)); }
        }
        if (inRun) runs.Add((runStart, bitmap.Width - 1));
        return runs;
    }

    private static string? FindRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private static byte[] BuildTwoGlyphPdf(byte[] cffBytes, int widthPerMille, float fontSize, float startX)
    {
        using var ms = new MemoryStream();
        var offsets = new long[8];

        void WriteAscii(string value)
        {
            var bytes = Encoding.ASCII.GetBytes(value);
            ms.Write(bytes, 0, bytes.Length);
        }
        void WriteLine(string value) => WriteAscii(value + "\n");

        WriteLine("%PDF-1.4");

        offsets[1] = ms.Position;
        WriteLine("1 0 obj");
        WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        WriteLine("endobj");

        offsets[2] = ms.Position;
        WriteLine("2 0 obj");
        WriteLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        WriteLine("endobj");

        offsets[3] = ms.Position;
        WriteLine("3 0 obj");
        WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 50] /Contents 4 0 R "
            + "/Resources << /Font << /F1 5 0 R >> >> >>");
        WriteLine("endobj");

        var content = $"BT /F1 {fontSize} Tf {startX} 20 Td (A) Tj (A) Tj ET";
        offsets[4] = ms.Position;
        WriteLine("4 0 obj");
        WriteLine($"<< /Length {content.Length} >>");
        WriteLine("stream");
        WriteLine(content);
        WriteLine("endstream");
        WriteLine("endobj");

        // FirstChar/LastChar cover only code 70 ('F') — code 65 ('A'), the
        // glyph actually drawn below, is deliberately outside this range so
        // its advance must come from /MissingWidth, not from /Widths[idx].
        offsets[5] = ms.Position;
        WriteLine("5 0 obj");
        WriteLine("<< /Type /Font /Subtype /Type1 /BaseFont /Inconsolata "
            + "/FirstChar 70 /LastChar 70 /Widths [600] "
            + "/FontDescriptor 6 0 R /Encoding /StandardEncoding >>");
        WriteLine("endobj");

        offsets[6] = ms.Position;
        WriteLine("6 0 obj");
        WriteLine("<< /Type /FontDescriptor /FontName /Inconsolata /Flags 32 "
            + "/FontBBox [-100 -300 700 900] /ItalicAngle 0 /Ascent 800 /Descent -200 "
            + "/CapHeight 700 /StemV 80 /MissingWidth " + widthPerMille + " /FontFile3 7 0 R >>");
        WriteLine("endobj");

        offsets[7] = ms.Position;
        WriteLine("7 0 obj");
        WriteLine($"<< /Subtype /Type1C /Length {cffBytes.Length} >>");
        WriteLine("stream");
        ms.Write(cffBytes, 0, cffBytes.Length);
        WriteLine("");
        WriteLine("endstream");
        WriteLine("endobj");

        var xrefPos = ms.Position;
        WriteLine("xref");
        WriteLine("0 8");
        WriteLine("0000000000 65535 f ");
        for (var i = 1; i <= 7; i++)
            WriteLine($"{offsets[i]:D10} 00000 n ");

        WriteLine("trailer");
        WriteLine("<< /Root 1 0 R /Size 8 >>");
        WriteLine("startxref");
        WriteLine(xrefPos.ToString());
        WriteLine("%%EOF");

        return ms.ToArray();
    }
}
