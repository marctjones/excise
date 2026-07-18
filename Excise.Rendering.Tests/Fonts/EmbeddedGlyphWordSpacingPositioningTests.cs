using System.IO;
using System.Linq;
using System.Reflection;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Fonts;

/// <summary>
/// Regression test for issue #652.
///
/// #652 was filed against a real embedded Type 1C/CFF font resource where an
/// em-dash appeared to render at the wrong scale/position. The issue's own
/// hypothesis — a non-default CFF <c>FontMatrix</c> (Top DICT operator 12 7)
/// — was investigated and REFUTED at the byte level: the CFF Top DICT
/// involved has no FontMatrix operator at all (confirmed via a from-scratch
/// Top DICT parse cross-checked against fontTools), so it uses the default
/// 0.001 scale, which is exactly what <see cref="Excise.Rendering.Fonts.CffToOpenType"/>
/// already hard-codes as <c>unitsPerEm</c>. No FontMatrix handling was added.
///
/// The real bug: <c>RenderText</c>'s embedded/byte-to-glyph glyph-ID draw
/// path (<c>SkiaRenderer.Text.cs</c>) dispatched glyph runs via
/// <c>SKTextBlobBuilder.AddRun</c> with Skia's default (hmtx-only)
/// positioning. Default positioning has no notion of PDF <c>Tc</c>/<c>Tw</c>
/// (character/word spacing) — those were only folded into the *tracked
/// cursor* used to position the next Tj/Tf, never into the actual on-canvas
/// glyph run. Whenever Tc or Tw was non-zero, the drawn glyphs silently
/// drifted from the PDF-intended (and cursor-tracked) positions — in the
/// #652 fixture, Tw=-0.588 over 10 spaces shifted a word's trailing glyph
/// ~6pt right of where the correctly-tracked cursor said the next glyph
/// should start, visually merging the two. Fixed by explicitly positioning
/// glyphs (<c>AddPositionedRun</c>) using the same /Widths+Tc+Tw cumulative
/// formula already used by the non-embedded fallback path, whenever Tc/Tw
/// actually apply.
///
/// This test reproduces the mechanism directly and deterministically: a
/// minimal from-scratch PDF embeds a real CFF font (Inconsolata, reusing the
/// fixture already checked into Excise.Core.Tests) as /FontFile3, and draws
/// "A A" once with Tw=0 and once with a large positive Tw. Before the fix,
/// the rendered gap between the two "A" glyphs was IDENTICAL in both lines
/// (Tw only moved the tracked cursor, not the pixels). After the fix, the
/// Tw=20 line's gap must be visibly wider.
/// </summary>
public class EmbeddedGlyphWordSpacingPositioningTests
{
    [Fact]
    public void WordSpacing_AppliesToEmbeddedCffGlyphRun_NotJustTheTrackedCursor()
    {
        var cff = LoadInconsolataCff();
        var pdfNoSpacing = BuildTwoWordPdf(cff, tw: 0);
        var pdfWithSpacing = BuildTwoWordPdf(cff, tw: 20);

        var gapNoSpacing = MeasureInterWordGapPx(pdfNoSpacing);
        var gapWithSpacing = MeasureInterWordGapPx(pdfWithSpacing);

        gapNoSpacing.Should().BeGreaterThan(0, "the two 'A' glyphs must actually be separated by a space");

        // At 150 DPI, Tw=20 (points) is a 20 * 150/72 ≈ 41.7px shift. Assert a
        // generous fraction of that to stay robust to hinting/rounding while
        // still failing hard on the pre-fix behavior (which produced ~0px
        // difference between the two lines, since Tw never reached the pixels).
        var expectedMinimumShiftPx = 20 * 150.0 / 72.0 * 0.5;
        (gapWithSpacing - gapNoSpacing).Should().BeGreaterThan(
            (int)expectedMinimumShiftPx,
            $"Tw=20 must visibly widen the inter-word gap versus Tw=0 " +
            $"(no-spacing gap={gapNoSpacing}px, with-spacing gap={gapWithSpacing}px). " +
            "A near-zero difference means Tw is being applied to the tracked " +
            "cursor but not to the actual drawn glyph run — issue #652's real bug.");
    }

    private static byte[] LoadInconsolataCff()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith("Inconsolata.cff", System.StringComparison.Ordinal));
        using var s = asm.GetManifestResourceStream(name)!;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Renders the fixture and measures the horizontal gap (in px) between
    /// the first "A" glyph's ink and the second "A" glyph's ink on the one
    /// text line the fixture draws.
    /// </summary>
    private static int MeasureInterWordGapPx(byte[] pdfBytes)
    {
        using var doc = PdfDocument.Open(pdfBytes);
        var page = doc.GetPage(1);
        using var bmp = new SkiaRenderer().RenderPage(page, new RenderOptions { Dpi = 150 });

        // Baseline at PDF y=700 on an 800pt-tall page, 40pt font → glyph ink
        // band roughly [660, 700]pt above the origin; scan a generous band.
        int yTopPx = PtToPx(800 - 700 - 40);
        int yBottomPx = PtToPx(800 - 700 + 5);
        yTopPx = System.Math.Max(0, yTopPx);
        yBottomPx = System.Math.Min(bmp.Height - 1, yBottomPx);

        var inkColumn = new bool[bmp.Width];
        for (int x = 0; x < bmp.Width; x++)
        {
            for (int y = yTopPx; y <= yBottomPx; y++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.Red < 128 && c.Green < 128 && c.Blue < 128)
                {
                    inkColumn[x] = true;
                    break;
                }
            }
        }

        // Find ink runs (clusters of consecutive true columns).
        var clusters = new System.Collections.Generic.List<(int Start, int End)>();
        int? runStart = null;
        for (int x = 0; x < bmp.Width; x++)
        {
            if (inkColumn[x])
            {
                runStart ??= x;
            }
            else if (runStart != null)
            {
                clusters.Add((runStart.Value, x - 1));
                runStart = null;
            }
        }
        if (runStart != null) clusters.Add((runStart.Value, bmp.Width - 1));

        clusters.Count.Should().BeGreaterThanOrEqualTo(2,
            $"expected two separate 'A' glyph ink clusters, found {clusters.Count}");

        // Gap between the end of the first cluster and the start of the last.
        return clusters[^1].Start - clusters[0].End;
    }

    private static int PtToPx(double pt) => (int)System.Math.Round(pt * 150.0 / 72.0);

    /// <summary>
    /// Builds a minimal from-scratch PDF: one page, one embedded Type1C font
    /// (raw CFF bytes, uncompressed), drawing "A A" once at 40pt with the
    /// given Tw. Font-object boilerplate follows the same raw-byte-offset
    /// pattern used throughout SkiaRendererTests.cs's CreatePdfWithContent*
    /// helpers, extended with a real embedded /FontFile3 stream.
    /// </summary>
    private static byte[] BuildTwoWordPdf(byte[] cffBytes, double tw)
    {
        using var ms = new MemoryStream();

        void WriteAscii(string s)
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(s);
            ms.Write(bytes, 0, bytes.Length);
        }
        void WriteLine(string s) => WriteAscii(s + "\n");

        var offsets = new long[9];

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
        WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 800] " +
                   "/Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>");
        WriteLine("endobj");

        var content = $"BT\n/F1 40 Tf\n0 Tc\n{tw.ToString(System.Globalization.CultureInfo.InvariantCulture)} Tw\n100 700 Td\n(A A) Tj\nET";
        offsets[4] = ms.Position;
        WriteLine("4 0 obj");
        WriteLine($"<< /Length {content.Length} >>");
        WriteLine("stream");
        WriteAscii(content);
        WriteLine("");
        WriteLine("endstream");
        WriteLine("endobj");

        // /Widths covers FirstChar(32=space)..LastChar(65='A'); only the two
        // codes actually used matter, the rest are filler zeros.
        //
        // /Encoding is a DICTIONARY with an explicit /Differences (not a bare
        // base-encoding name) — this matters: it's what makes SetFont build
        // _currentCodeToGlyphName (see BuildEncodingMaps in SkiaRenderer.Text.cs),
        // which is what makes the CFF wrap populate a non-null byte→glyph map
        // and route drawing through the RenderText branch this test targets.
        // A bare "/Encoding /WinAnsiEncoding" name lets Skia's own cmap
        // resolve the glyphs directly, landing in the *other*, untested
        // plain-text fallback branch instead — exactly the same trap #652's
        // real fixture doesn't fall into, since real embedded CFF subsets
        // almost always ship a /Differences-based /Encoding.
        offsets[5] = ms.Position;
        WriteLine("5 0 obj");
        WriteAscii("<< /Type /Font /Subtype /Type1 /BaseFont /Inconsolata " +
                   "/FirstChar 32 /LastChar 65 /Widths [500");
        for (int c = 33; c <= 64; c++) WriteAscii(" 0");
        WriteLine(" 500 ] /Encoding 8 0 R /FontDescriptor 6 0 R >>");
        WriteLine("endobj");

        offsets[6] = ms.Position;
        WriteLine("6 0 obj");
        WriteLine("<< /Type /FontDescriptor /FontName /Inconsolata " +
                   "/Flags 32 /FontBBox [-100 -300 900 900] /ItalicAngle 0 " +
                   "/Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 " +
                   "/FontFile3 7 0 R >>");
        WriteLine("endobj");

        offsets[7] = ms.Position;
        WriteLine("7 0 obj");
        WriteLine($"<< /Subtype /Type1C /Length {cffBytes.Length} >>");
        WriteLine("stream");
        ms.Write(cffBytes, 0, cffBytes.Length);
        WriteLine("");
        WriteLine("endstream");
        WriteLine("endobj");

        offsets[8] = ms.Position;
        WriteLine("8 0 obj");
        WriteLine("<< /Type /Encoding /Differences [32 /space 65 /A] >>");
        WriteLine("endobj");

        var xrefPos = ms.Position;
        WriteLine("xref");
        WriteLine("0 9");
        WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 8; i++)
            WriteLine($"{offsets[i]:D10} 00000 n ");
        WriteLine("trailer");
        WriteLine("<< /Size 9 /Root 1 0 R >>");
        WriteLine("startxref");
        WriteLine(xrefPos.ToString());
        WriteLine("%%EOF");

        return ms.ToArray();
    }
}
