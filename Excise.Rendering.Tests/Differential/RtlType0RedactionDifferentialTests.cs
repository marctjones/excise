using System.Collections.Generic;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Fonts;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// RTL redaction in <b>Type0 / Identity-H</b> (CID) Arabic/Hebrew fonts,
/// verified with the two INDEPENDENT oracles CLAUDE.md mandates — never with a
/// saved-bytes search (#632, #606).
///
/// This is the case the task singled out: in Identity-H the content-stream
/// operands are 2-byte CIDs (glyph indices), so the Arabic/Hebrew string is
/// NEVER present in the file as Unicode — a saved-bytes grep, even UTF-16BE, is
/// structurally BLIND to it and would report a false green. So correctness is
/// asserted only by:
///
///   1. mutool as an INDEPENDENT EXTRACTOR — it reads the word (via /ToUnicode)
///      BEFORE redaction (anti-vacuity) and cannot recover it AFTER, in either
///      order.
///   2. Ghostscript as an INDEPENDENT RENDERER — an ink differential over the
///      word's region. Redaction is driven with drawBlackRect:false, so the
///      region must come back BLANK (glyphs REMOVED), not black (merely
///      covered) — the strictly stronger result.
///
/// The redaction is driven through RedactText(logicalWord) — the logical→visual
/// bidi MATCHING is the deliverable, and RedactArea(box) would sidestep it.
///
/// Fixtures embed the checked-in DejaVu Sans TTF (covers Hebrew + Arabic base
/// letters) and paint the word's GIDs in visual (reversed) order.
/// </summary>
public class RtlType0RedactionDifferentialTests : IDisposable
{
    private const string ArabicWord = "سلام"; // logical
    private const string HebrewWord = "שלום"; // logical
    private const string Keep = "KEEP";

    private static readonly int[] ArabicScalars = { 0x0633, 0x0644, 0x0627, 0x0645 };
    private static readonly int[] HebrewScalars = { 0x05E9, 0x05DC, 0x05D5, 0x05DD };

    private readonly List<string> _temp = new();

    [Theory]
    [InlineData("arabic")]
    [InlineData("hebrew")]
    public void Type0Rtl_Redaction_LeavesNoTextForAnIndependentExtractor(string which)
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        var (scalars, word) = which == "arabic"
            ? (ArabicScalars, ArabicWord) : (HebrewScalars, HebrewWord);

        var beforePath = SaveTemp(Type0RtlFixture.VisualOrderWithKeep(scalars));

        // Oracle sanity / anti-vacuity: mutool reads the word from the CID
        // fixture BEFORE we redact — so its absence afterwards means something.
        var before = MutoolTextExtractor.ExtractPage(beforePath, 1);
        before.Should().NotBeNull("mutool must read the fixture");
        // Order-independent: mupdf reads RTL in logical order on macOS and visual
        // (reversed) order on Linux — both recover the word. See RtlOracleText.
        RtlOracleText.Recovered(before, word).Should().BeTrue(
            "anti-vacuity: an independent extractor recovers the word from the unredacted CID " +
            "fixture (in logical or visual order — mupdf's RTL bidi direction is build-dependent)");
        // Linux mupdf spaces out CID glyphs ("K E E P"); match on letters, not the raw run.
        RtlOracleText.Recovered(before, Keep).Should().BeTrue("the keep word is present too");

        using var doc = PdfDocument.Open(Type0RtlFixture.VisualOrderWithKeep(scalars));
        var removed = doc.RedactText(word, drawBlackRect: false).VerifiedRemovals;
        removed.Should().BeGreaterThan(0,
            "a logical-order needle must match the visual-order CID run");

        var afterPath = SaveTemp(doc.SaveToBytes());
        var after = MutoolTextExtractor.ExtractPage(afterPath, 1);
        after.Should().NotBeNull("mutool must still read the redacted file");
        // Space-immune leak scan: strip the glyph-spacing mupdf injects so a
        // surviving "س ل ا م" cannot slip past NotContain — this is stricter than
        // scanning the raw run, and still distinguishes logical from visual order.
        var afterScan = RtlOracleText.StripSpacing(after);
        afterScan.Should().NotContain(word, "the word must be gone from every text carrier mutool reads");
        afterScan.Should().NotContain(Reverse(word), "nor in visual (reversed) order");
        RtlOracleText.Recovered(after, Keep).Should().BeTrue(
            "only the targeted word may be removed, not the whole page");
    }

    [Theory]
    [InlineData("arabic")]
    [InlineData("hebrew")]
    public void Type0Rtl_Redaction_RemovesInk_NotMerelyCoversIt(string which)
    {
        Assert.SkipUnless(GhostscriptReferenceRenderer.IsAvailable, "ghostscript not installed");
        var (scalars, word) = which == "arabic"
            ? (ArabicScalars, ArabicWord) : (HebrewScalars, HebrewWord);

        using var doc = PdfDocument.Open(Type0RtlFixture.VisualOrderWithKeep(scalars));
        var page = doc.GetPage(1);
        var wordBox = BoundsOf(page, word);
        var keepBox = BoundsOf(page, Keep);

        var beforePath = SaveTemp(doc);
        using var before = GhostscriptReferenceRenderer.RenderPage(beforePath, 1, dpi: 150);
        before.Should().NotBeNull();
        InkFractionIn(before!, wordBox, page.Height).Should().BeGreaterThan(0.02,
            "fixture sanity — the RTL word must actually be inked before redaction");

        // drawBlackRect:false ⇒ structural removal only. The region must come
        // back BLANK, proving the glyphs are GONE, not covered by a box.
        var removed = doc.RedactText(word, drawBlackRect: false).VerifiedRemovals;
        removed.Should().BeGreaterThan(0);

        var afterPath = SaveTemp(doc);
        using var after = GhostscriptReferenceRenderer.RenderPage(afterPath, 1, dpi: 150);
        after.Should().NotBeNull();

        InkFractionIn(after!, wordBox, page.Height).Should().BeLessThan(0.001,
            "an independent renderer must draw NO ink where the CID word was — blank, not a black " +
            "box; surviving ink would mean the glyphs were covered, not removed");
        InkFractionIn(after!, keepBox, page.Height).Should().BeGreaterThan(0.02,
            "the untargeted word must still be inked — a blanked page would satisfy the assertion above");
    }

    // ---- ink helper (PDF content coords, bottom-left origin) ----
    private static double InkFractionIn(SKBitmap bmp, PdfRectangle box, double pageHeight)
    {
        const double scale = 150.0 / 72.0;
        int x0 = Math.Max(0, (int)(box.Left * scale));
        int x1 = Math.Min(bmp.Width - 1, (int)(box.Right * scale));
        int y0 = Math.Max(0, (int)((pageHeight - box.Top) * scale));
        int y1 = Math.Min(bmp.Height - 1, (int)((pageHeight - box.Bottom) * scale));
        if (x1 <= x0 || y1 <= y0) return 0;

        int ink = 0, total = 0;
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            var p = bmp.GetPixel(x, y);
            total++;
            if (p.Red < 200 || p.Green < 200 || p.Blue < 200) ink++;
        }
        return total == 0 ? 0 : (double)ink / total;
    }

    private static PdfRectangle BoundsOf(PdfPage page, string word)
    {
        // The word's letters, on its own line; union their glyph rectangles.
        var run = page.Letters
            .Where(l => word.Contains(l.Value, StringComparison.Ordinal))
            .ToList();
        run.Should().NotBeEmpty($"fixture must render '{word}'");
        var y = run[0].GlyphRectangle.Bottom;
        var line = page.Letters
            .Where(l => Math.Abs(l.GlyphRectangle.Bottom - y) < 2.0 &&
                        word.Contains(l.Value, StringComparison.Ordinal))
            .ToList();
        return new PdfRectangle(
            line.Min(l => l.GlyphRectangle.Left) - 1,
            line.Min(l => l.GlyphRectangle.Bottom) - 1,
            line.Max(l => l.GlyphRectangle.Right) + 1,
            line.Max(l => l.GlyphRectangle.Top) + 1).Normalize();
    }

    private static string Reverse(string s)
    {
        var c = s.ToCharArray();
        Array.Reverse(c);
        return new string(c);
    }

    private string SaveTemp(PdfDocument doc) => SaveTemp(doc.SaveToBytes());
    private string SaveTemp(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"rtl-type0-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, bytes);
        _temp.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var p in _temp)
            try { File.Delete(p); } catch (IOException) { }
    }
}

/// <summary>
/// Type0 / Identity-H CIDFontType2 fixture embedding the checked-in DejaVu Sans
/// TTF. Line 1 carries the RTL word's GIDs in visual (reversed) order; line 2
/// carries an LTR "KEEP" word. A /ToUnicode CMap maps each GID back to its
/// scalar. The Arabic/Hebrew string appears in the file ONLY as CIDs — never as
/// Unicode — which is exactly why saved-bytes search cannot verify redaction.
/// </summary>
internal static class Type0RtlFixture
{
    private static readonly byte[] FontBytes = LoadDejaVu();
    private static readonly TrueTypeFontFile Ttf = TrueTypeFontFile.Parse(FontBytes);

    private static byte[] LoadDejaVu()
    {
        // Resolve the checked-in font by walking up from the test bin directory
        // to the repo root and reading the shared Core.Tests fixture copy.
        var dir = AppContext.BaseDirectory;
        for (var d = new DirectoryInfo(dir); d != null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "Excise.Core.Tests", "Fixtures", "Fonts", "DejaVuSans.ttf");
            if (File.Exists(candidate)) return File.ReadAllBytes(candidate);
        }
        throw new FileNotFoundException("DejaVuSans.ttf fixture not found by walking up from " + dir);
    }

    public static byte[] VisualOrderWithKeep(int[] logicalRtlScalars)
    {
        var rtlVisual = logicalRtlScalars.Reverse().ToArray();
        int[] keep = "KEEP".Select(c => (int)c).ToArray();

        var rtlGids = rtlVisual.Select(GidFor).ToArray();
        var keepGids = keep.Select(GidFor).ToArray();

        var line1 = $"BT /F0 24 Tf 72 700 Td <{Hex(rtlGids)}> Tj ET";
        var line2 = $"BT /F0 24 Tf 72 600 Td <{Hex(keepGids)}> Tj ET";
        var content = line1 + "\n" + line2;

        var pairs = new List<(int gid, int scalar)>();
        for (int i = 0; i < rtlGids.Length; i++) pairs.Add((rtlGids[i], rtlVisual[i]));
        for (int i = 0; i < keepGids.Length; i++) pairs.Add((keepGids[i], keep[i]));

        var seen = new HashSet<int>();
        var bf = new StringBuilder();
        int count = 0;
        foreach (var (gid, scalar) in pairs)
        {
            if (!seen.Add(gid)) continue;
            bf.Append($"<{gid:X4}> <{scalar:X4}>\n");
            count++;
        }
        var toUni = "/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n" +
            "1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n" +
            $"{count} beginbfchar\n{bf}endbfchar\nendcmap\nend end";

        var ms = new MemoryStream();
        var offsets = new long[11];
        void Ascii(string s) { var b = Encoding.ASCII.GetBytes(s); ms.Write(b, 0, b.Length); }
        void Obj(int n) => offsets[n] = ms.Length;

        Ascii("%PDF-1.7\n");
        Obj(1); Ascii("1 0 obj <</Type/Catalog/Pages 2 0 R>> endobj\n");
        Obj(2); Ascii("2 0 obj <</Type/Pages/Count 1/Kids[3 0 R]>> endobj\n");
        Obj(3); Ascii("3 0 obj <</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]" +
                      "/Resources<</Font<</F0 4 0 R>>>>/Contents 9 0 R>> endobj\n");
        Obj(4); Ascii("4 0 obj <</Type/Font/Subtype/Type0/BaseFont/Test" +
                      "/Encoding/Identity-H/DescendantFonts[5 0 R]/ToUnicode 10 0 R>> endobj\n");
        Obj(5); Ascii("5 0 obj <</Type/Font/Subtype/CIDFontType2/BaseFont/Test" +
                      "/CIDSystemInfo<</Registry(Adobe)/Ordering(Identity)/Supplement 0>>" +
                      "/FontDescriptor 6 0 R/DW 1000>> endobj\n");
        Obj(6); Ascii("6 0 obj <</Type/FontDescriptor/FontName/Test/Flags 4" +
                      "/FontBBox[0 0 1000 1000]/ItalicAngle 0/Ascent 800/Descent -200" +
                      "/CapHeight 700/StemV 80/FontFile2 7 0 R>> endobj\n");
        Obj(7); Ascii($"7 0 obj <</Length {FontBytes.Length}/Length1 {FontBytes.Length}>>\nstream\n");
        ms.Write(FontBytes, 0, FontBytes.Length); Ascii("\nendstream endobj\n");
        Obj(9); Ascii($"9 0 obj <</Length {content.Length}>>\nstream\n{content}\nendstream endobj\n");
        Obj(10); Ascii($"10 0 obj <</Length {toUni.Length}>>\nstream\n{toUni}\nendstream endobj\n");

        var xref = ms.Length;
        Ascii("xref\n0 11\n0000000000 65535 f \n");
        for (int i = 1; i <= 10; i++)
            Ascii(offsets[i] == 0 ? "0000000000 65535 f \n" : offsets[i].ToString("D10") + " 00000 n \n");
        Ascii($"trailer <</Size 11/Root 1 0 R>>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    private static int GidFor(int scalar)
    {
        var gid = Ttf.GidForCodepoint(scalar);
        if (gid <= 0) throw new InvalidOperationException($"DejaVu Sans lacks U+{scalar:X4}");
        return gid;
    }

    private static string Hex(int[] gids) => string.Concat(gids.Select(g => g.ToString("X4")));
}
