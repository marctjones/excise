using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Excise.Avalonia.Controls;
using Excise.Avalonia.Services;
using Excise.Core.Document;
using Excise.Core.Text;
using Xunit;

namespace Excise.Avalonia.Tests;

/// <summary>
/// RTL text selection and bounded multi-column selection for the viewer
/// (#373). The selection engine builds the on-screen highlight in VISUAL
/// order (so each glyph rectangle is drawn where the user sees it, including
/// inside an RTL run) while re-ordering the COPIED text into logical reading
/// order — reusing the logical order Excise.Core's extractor already produced
/// via its bidi reorderer (#632), not a second bidi implementation.
///
/// The logical-order expectations here are hand-authored Unicode strings
/// (independent of excise): the fixtures paint Arabic in the visual order that
/// virtually every producer emits, and the tests assert the letters come back
/// out in the order a human reads them. The #632 extraction layer these build
/// on is itself pinned against mutool / python-bidi in Excise.Core.Tests.
///
/// The assembly disables parallelization (SkiaSharp's font manager is
/// process-wide); the engine tests are pure logic and the one control test
/// runs through <see cref="HeadlessSessionGuard"/>.
/// </summary>
public class TextSelectionRtlTests
{
    // Logical order (first character = first letter a reader pronounces).
    // سلام — U+0633 U+0644 U+0627 U+0645.
    private const string ArabicWord = "سلام";
    private static readonly int[] ArabicScalars = { 0x0633, 0x0644, 0x0627, 0x0645 };

    // ── (a) mixed LTR+RTL line copies in logical order ───────────────────────

    [Fact]
    public void MixedLatinRtlLine_CopiesInLogicalOrder()
    {
        using var doc = PdfDocument.Open(
            RtlFixtures.SingleTjWithLatinPrefix("HELLO", ArabicScalars));
        var letters = doc.GetPage(1).Letters;

        var reading = TextSelectionEngine.SortReadingOrder(letters);
        reading.Should().HaveCount(9);

        var range = TextSelectionEngine.ColumnAwareRange(
            reading, reading[0], reading[^1],
            TextSelectionEngine.EstimateColumnGap(reading));

        // Painted order is visual: the Arabic run is reversed on screen.
        Concat(range).Should().Be("HELLO" + Reverse(ArabicWord),
            "the highlighted run follows visual (painted) order");

        // Copied text is logical: HELLO then the Arabic word as read.
        var logical = TextSelectionEngine.ToLogicalOrder(range, letters);
        Concat(logical).Should().Be("HELLO" + ArabicWord,
            "copied RTL text must read in logical order, not visual order");
    }

    // ── (b) selecting within an RTL run highlights the correct rects ─────────

    [Fact]
    public void SelectionWithinRtlRun_HighlightsCorrectContiguousRects()
    {
        using var doc = PdfDocument.Open(RtlFixtures.SingleTj(ArabicScalars, visualOrder: true));
        var letters = doc.GetPage(1).Letters;
        Concat(letters).Should().Be(ArabicWord, "extraction is logical order (#632)");

        // Visual (painted) order, left-to-right: مالس — the mirror of سلام.
        var reading = TextSelectionEngine.SortReadingOrder(letters);
        Concat(reading).Should().Be(Reverse(ArabicWord));

        // Drag over the two middle glyphs of the run (visually adjacent).
        var range = TextSelectionEngine.ColumnAwareRange(
            reading, reading[1], reading[2],
            TextSelectionEngine.EstimateColumnGap(reading));

        range.Should().HaveCount(2, "only the two selected glyphs are highlighted");

        // The highlight rectangles are exactly the two selected glyph rects...
        var rects = range.Select(l => l.GlyphRectangle).OrderBy(r => r.Left).ToList();
        rects.Should().OnlyContain(r => letters.Any(l =>
            Math.Abs(l.GlyphRectangle.Left - r.Left) < 0.001 &&
            Math.Abs(l.GlyphRectangle.Right - r.Right) < 0.001));

        // ...and they are a VISUALLY CONTIGUOUS block: no other glyph on the
        // page sits horizontally between them, so the highlight covers the run
        // the user dragged over with nothing leaking in and nothing missed.
        letters.Should().NotContain(l =>
            l.GlyphRectangle.Left > rects[0].Left + 0.001 &&
            l.GlyphRectangle.Left < rects[1].Left - 0.001,
            "the two highlighted RTL glyphs are visually adjacent");

        // The copied sub-selection is the correct LOGICAL substring (chars 2–3
        // of سلام = لا), not the visual pair.
        var logical = TextSelectionEngine.ToLogicalOrder(range, letters);
        Concat(logical).Should().Be("لا");
    }

    // ── (c) a column-local drag does not include the neighbour column ────────

    [Fact]
    public void ColumnLocalDrag_ExcludesAdjacentColumnOnSameYBand()
    {
        // Two columns on two shared Y-bands; a wide gutter separates them.
        var left1 = L("a", 10, 100); var left2 = L("b", 20, 100);
        var right1 = L("Y", 200, 100); var right2 = L("Z", 210, 100);
        var left3 = L("c", 10, 80); var left4 = L("d", 20, 80);
        var right3 = L("Y", 200, 80); var right4 = L("Z", 210, 80);

        var all = new[] { left1, left2, right1, right2, left3, left4, right3, right4 };
        var reading = TextSelectionEngine.SortReadingOrder(all);
        var gap = TextSelectionEngine.EstimateColumnGap(reading);

        // Drag down the LEFT column: anchor a (top line), focus d (next line).
        var plain = TextSelectionEngine.RangeBetween(reading, left1, left4);
        Concat(plain).Should().Contain("Y",
            "without column awareness the plain range vacuums up the right column");

        var range = TextSelectionEngine.ColumnAwareRange(reading, left1, left4, gap);
        Concat(range).Should().Be("abcd",
            "a column-local drag must not include the adjacent column's words");
        range.Should().NotContain(l => l.Value == "Y" || l.Value == "Z");
    }

    // ── direction-agnostic word spacing (locks the JoinText change) ──────────

    [Fact]
    public void JoinText_InsertsWordSpace_InRtlLogicalOrder()
    {
        // Two RTL "words" in logical (right-to-left) order with a wide gap.
        // Logical order means each next glyph sits to the LEFT of the previous.
        var w1a = L("س", 100, 100, 8, 12); var w1b = L("ل", 92, 100, 8, 12);
        var w2a = L("ا", 60, 100, 8, 12); var w2b = L("م", 52, 100, 8, 12);

        var text = TextSelectionEngine.JoinText(new[] { w1a, w1b, w2a, w2b });

        text.Should().Be("سل ام",
            "a wide RTL gap (prev to the right of cur) still marks a word break");
    }

    // ── column-aware default does not regress single-line RTL (#774) ─────────

    [Fact]
    public void ColumnAwareStrategy_SingleRtlLine_IsIdenticalToSimple()
    {
        using var doc = PdfDocument.Open(RtlFixtures.SingleTj(ArabicScalars, visualOrder: true));
        var letters = doc.GetPage(1).Letters;

        // A single line has too few rows to be multi-column, so the new
        // ColumnAware default must fall through to the old geometric order.
        var simple = TextSelectionEngine.SortReadingOrder(letters, ReadingOrderStrategy.Simple);
        var columnAware = TextSelectionEngine.SortReadingOrder(letters, ReadingOrderStrategy.ColumnAware);
        Concat(columnAware).Should().Be(Concat(simple),
            "column-aware must not reorder a single RTL line");
        Concat(columnAware).Should().Be(Reverse(ArabicWord),
            "visual (painted) order is preserved for the highlight run");
    }

    // ── control smoke test (headless) ────────────────────────────────────────

    [Fact]
    public async Task Viewer_LoadsRtlDocument_WithoutThrowing()
    {
        await HeadlessSessionGuard.Session().Dispatch(() =>
        {
            using var doc = PdfDocument.Open(RtlFixtures.SingleTj(ArabicScalars, visualOrder: true));
            var viewer = new PdfViewerControl { Document = doc };

            // The text pipeline the selection code consumes is reachable and
            // logical for an RTL page loaded into the real control.
            viewer.GetAccessiblePageText().Should().NotBeNullOrEmpty();
            Concat(doc.GetPage(1).Letters).Should().Be(ArabicWord);
            return true;
        }, CancellationToken.None);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string Concat(IEnumerable<Letter> letters) =>
        string.Concat(letters.Select(l => l.Value));

    private static string Reverse(string s)
    {
        var a = s.ToCharArray();
        Array.Reverse(a);
        return new string(a);
    }

    private static Letter L(string value, double left, double bottom,
        double width = 8, double height = 12)
    {
        var rect = new PdfRectangle(left, bottom, left + width, bottom + height);
        return new Letter(value, rect, fontSize: height, fontName: "Helvetica",
            startX: left, startY: bottom, width: width, characterCode: value[0]);
    }
}

/// <summary>
/// Minimal raw-PDF fixtures with a Type1 font and a ToUnicode CMap, mirroring
/// the #632 <c>RtlPdfFixtures</c> (which live in Excise.Core.Tests and are not
/// visible here). Character codes 0x41.. are assigned positionally so the
/// content stream's byte order is the only thing controlling extraction order.
/// </summary>
internal static class RtlFixtures
{
    /// <summary>
    /// One Tj painting the word left-to-right with positive advances.
    /// <paramref name="visualOrder"/> true reverses the codes (leftmost glyph
    /// is the LAST logical character — the common producer encoding).
    /// </summary>
    public static byte[] SingleTj(int[] logicalScalars, bool visualOrder)
    {
        var codes = Codes(logicalScalars.Length);
        if (visualOrder) Array.Reverse(codes);
        var content = $"BT /F1 24 Tf 100 700 Td ({new string(codes)}) Tj ET";
        return Build(content, Bfchar(logicalScalars), logicalScalars.Length);
    }

    /// <summary>
    /// A Latin prefix then the visual-order RTL word, one Tj. The prefix maps
    /// to itself; it must not collide with the 0x41.. code range (so keep
    /// clear of 'A'..).
    /// </summary>
    public static byte[] SingleTjWithLatinPrefix(string latinPrefix, int[] logicalScalars)
    {
        var codes = Codes(logicalScalars.Length);
        Array.Reverse(codes);
        var content = $"BT /F1 24 Tf 100 700 Td ({latinPrefix}{new string(codes)}) Tj ET";

        var mapping = new StringBuilder();
        foreach (var ch in latinPrefix)
            mapping.Append($"<{(int)ch:X2}> <{(int)ch:X4}>\n");
        for (int i = 0; i < logicalScalars.Length; i++)
            mapping.Append($"<{0x41 + i:X2}> <{logicalScalars[i]:X4}>\n");
        return Build(content, mapping.ToString(), latinPrefix.Length + logicalScalars.Length);
    }

    private static char[] Codes(int count)
    {
        var codes = new char[count];
        for (int i = 0; i < count; i++) codes[i] = (char)(0x41 + i);
        return codes;
    }

    private static string Bfchar(int[] scalars)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < scalars.Length; i++)
            sb.Append($"<{0x41 + i:X2}> <{scalars[i]:X4}>\n");
        return sb.ToString();
    }

    private static byte[] Build(string content, string bfcharEntries, int bfcharCount)
    {
        var cmap =
            "/CIDInit /ProcSet findresource begin\n" +
            "12 dict begin\n" +
            "begincmap\n" +
            "/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n" +
            "/CMapName /Adobe-Identity-UCS def\n" +
            "/CMapType 2 def\n" +
            "1 begincodespacerange\n<00> <FF>\nendcodespacerange\n" +
            $"{bfcharCount} beginbfchar\n{bfcharEntries}endbfchar\n" +
            "endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend";

        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, Encoding.Latin1, leaveOpen: true) { NewLine = "\n" };

        writer.WriteLine("%PDF-1.7");
        var offsets = new long[7];

        offsets[1] = Flush(writer, ms);
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        writer.WriteLine("endobj");

        offsets[2] = Flush(writer, ms);
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        writer.WriteLine("endobj");

        offsets[3] = Flush(writer, ms);
        writer.WriteLine("3 0 obj");
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                         "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>");
        writer.WriteLine("endobj");

        offsets[4] = Flush(writer, ms);
        writer.WriteLine("4 0 obj");
        writer.WriteLine($"<< /Length {content.Length} >>");
        writer.WriteLine("stream");
        writer.WriteLine(content);
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");

        offsets[5] = Flush(writer, ms);
        writer.WriteLine("5 0 obj");
        writer.WriteLine("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica " +
                         "/FirstChar 32 /LastChar 127 /ToUnicode 6 0 R >>");
        writer.WriteLine("endobj");

        offsets[6] = Flush(writer, ms);
        writer.WriteLine("6 0 obj");
        writer.WriteLine($"<< /Length {cmap.Length} >>");
        writer.WriteLine("stream");
        writer.WriteLine(cmap);
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");

        long xrefPos = Flush(writer, ms);
        writer.WriteLine("xref");
        writer.WriteLine("0 7");
        writer.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 6; i++)
            writer.WriteLine($"{offsets[i]:D10} 00000 n ");
        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Size 7 >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    private static long Flush(StreamWriter writer, MemoryStream ms)
    {
        writer.Flush();
        return ms.Position;
    }
}
