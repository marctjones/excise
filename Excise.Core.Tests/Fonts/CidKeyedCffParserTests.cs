using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Fonts;
using Excise.Core.Primitives;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Excise.Core.Tests.Fonts;

/// <summary>
/// Direct CFF-parser coverage for CID-keyed fonts (Adobe-Japan1 /
/// Adobe-CNS1 / Adobe-Korea1). Pulls a real CIDFontType0C blob out of
/// the verapdf corpus, parses it, and asserts the parser:
///
/// 1. Identifies it as CID-keyed via the /ROS Top DICT operator (12 30).
/// 2. Builds a non-empty CID → glyph-index map covering the CIDs the
///    PDF's /W array references.
/// 3. Leaves <see cref="CffParser.CffFontInfo.GlyphNames"/> empty (CID
///    fonts have no PostScript glyph names).
///
/// Skips silently when the corpus isn't checked out (CI without
/// test-pdfs/) so the test suite still runs in minimal environments.
/// </summary>
public class CidKeyedCffParserTests
{
    [Fact]
    public void Parse_CidKeyedKozMinPro_DetectsRosAndBuildsCidMap()
    {
        var cff = TryLoadKozMinProCff();
        if (cff == null) return; // corpus missing; degrade to skip

        var info = CffParser.Parse(cff);
        info.Should().NotBeNull();
        info!.IsCidKeyed.Should().BeTrue("KozMinPro is /ROS-marked Adobe-Japan1");
        info.CidToGlyph.Should().NotBeNull();
        info.CidToGlyph!.Count.Should().BeGreaterThan(1, "subset has more than just .notdef");
        info.CidToGlyph[0].Should().Be(0, "CID 0 must always map to glyph 0 (.notdef)");

        // The fixture's /W array references CIDs 1, 41, 56, 69, 70, 77, 80, 83.
        // At least a few of them should be present in the subset's CFF charset.
        int present = new[] { 1, 41, 56, 69, 70, 77, 80, 83 }
            .Count(cid => info.CidToGlyph.ContainsKey(cid));
        present.Should().BeGreaterThan(0,
            "the /W-referenced CIDs should be in the CFF charset; if none are, the wrapping won't render any glyphs");

        // CID-keyed CFFs don't carry glyph names; GlyphNames stays empty.
        info.GlyphNames.Should().BeEmpty();
        info.GlyphNameToIndex.Should().BeEmpty();
    }

    [Fact]
    public void Parse_SimpleCff_StaysNonCidKeyed()
    {
        var cff = TryLoadSimpleCff();
        if (cff == null) return;

        var info = CffParser.Parse(cff);
        info.Should().NotBeNull();
        info!.IsCidKeyed.Should().BeFalse("the smoke-corpus Type1C subset is not CID-keyed");
        info.CidToGlyph.Should().BeNull();
        info.GlyphNames.Should().NotBeEmpty("simple CFFs carry PostScript glyph names");
    }

    [Fact]
    public void Parse_CidKeyedWithPredefinedCharsetOffset_MapsIdentityForAllGlyphs()
    {
        // A CID-keyed CFF whose Top DICT omits the charset operator (offset
        // 0). Predefined SID charsets don't apply to CIDFonts; the mapping
        // is Identity (glyph i → CID i), which is also how FreeType resolves
        // it. 240 glyphs on purpose: the pre-#515 code fell into the
        // IsoAdobe branch, which is ACCIDENTALLY identity for glyphs ≤ 228
        // (IsoAdobe SIDs are sequential) but leaves every glyph above 228
        // unmapped — so only a font bigger than the IsoAdobe table
        // distinguishes the bug from the fix.
        const int numGlyphs = 240;
        var cff = BuildCidKeyedCffWithoutCharset(numGlyphs);

        var info = CffParser.Parse(cff);
        info.Should().NotBeNull();
        info!.IsCidKeyed.Should().BeTrue();
        info.NumGlyphs.Should().Be(numGlyphs);
        info.CidToGlyph.Should().NotBeNull();
        info.CidToGlyph![0].Should().Be(0);
        info.CidToGlyph[100].Should().Be(100, "identity below the IsoAdobe boundary");
        info.CidToGlyph[228].Should().Be(228, "identity at the IsoAdobe boundary");
        info.CidToGlyph.Should().ContainKey(235,
            "glyphs above the 228-entry IsoAdobe table must still be mapped — before #515 " +
            "they were silently dropped and every high CID rendered .notdef");
        info.CidToGlyph[235].Should().Be(235);
        info.CidToGlyph[numGlyphs - 1].Should().Be(numGlyphs - 1);
    }

    /// <summary>
    /// Minimal CID-keyed CFF exercising only what <see cref="CffParser"/>
    /// reads: header, Name INDEX, Top DICT (ROS + CharStrings offset, NO
    /// charset operator), String INDEX, GSubr INDEX, CharStrings INDEX of
    /// <paramref name="numGlyphs"/> one-byte (endchar) charstrings.
    /// </summary>
    private static byte[] BuildCidKeyedCffWithoutCharset(int numGlyphs)
    {
        static byte[] Int5(int v) => new byte[]
        {
            0x1D, (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v,
        };

        static byte[] Index1(params byte[][] items)
        {
            using var ms = new MemoryStream();
            ms.WriteByte((byte)(items.Length >> 8));
            ms.WriteByte((byte)items.Length);
            ms.WriteByte(0x01); // offSize 1 — payloads here stay < 255 bytes
            int offset = 1;
            ms.WriteByte((byte)offset);
            foreach (var item in items)
            {
                offset += item.Length;
                ms.WriteByte((byte)offset);
            }
            foreach (var item in items)
                ms.Write(item, 0, item.Length);
            return ms.ToArray();
        }

        byte[] header = { 0x01, 0x00, 0x04, 0x04 };
        byte[] nameIndex = Index1(System.Text.Encoding.ASCII.GetBytes("T"));
        byte[] emptyIndex = { 0x00, 0x00 };

        // Top DICT: ROS (0 0 0) + CharStrings offset — fixed 5-byte operands
        // so the layout is computable before the offset is known.
        static byte[] TopDict(int charStringsOffset) =>
            Int5(0).Concat(Int5(0)).Concat(Int5(0)).Concat(new byte[] { 0x0C, 0x1E })
                .Concat(Int5(charStringsOffset)).Concat(new byte[] { 0x11 })
                .ToArray();
        int topDictIndexLen = Index1(TopDict(0)).Length;

        int charStringsOffset = header.Length + nameIndex.Length + topDictIndexLen
            + emptyIndex.Length /* String INDEX */ + emptyIndex.Length /* GSubr INDEX */;
        var topDictIndex = Index1(TopDict(charStringsOffset));

        // CharStrings INDEX: numGlyphs one-byte endchar charstrings. offSize 1
        // holds while 1 + numGlyphs <= 255.
        using var cs = new MemoryStream();
        cs.WriteByte((byte)(numGlyphs >> 8));
        cs.WriteByte((byte)numGlyphs);
        cs.WriteByte(0x01);
        for (int i = 0; i <= numGlyphs; i++)
            cs.WriteByte((byte)(1 + i));
        for (int i = 0; i < numGlyphs; i++)
            cs.WriteByte(0x0E); // endchar

        return header
            .Concat(nameIndex)
            .Concat(topDictIndex)
            .Concat(emptyIndex)
            .Concat(emptyIndex)
            .Concat(cs.ToArray())
            .ToArray();
    }

    private static byte[]? TryLoadKozMinProCff()
    {
        // Find a fixture from the verapdf corpus that embeds a
        // CIDFontType0C font; pull /FontFile3 out of its first
        // descendant font.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(
            Path.Combine(dir.FullName, "test-pdfs", "verapdf-corpus")))
        {
            dir = dir.Parent;
        }
        if (dir == null) return null;

        var pdfPath = Path.Combine(dir.FullName,
            "test-pdfs", "verapdf-corpus", "veraPDF-corpus-master",
            "PDF_A-2b", "6.2 Graphics", "6.2.11 Use of standard structure types",
            "6.2.11.4 List Standard Structure Types",
            "6.2.11.4.2 List_The Continued attribute",
            "veraPDF test suite 6-2-11-4-2-t02-pass-a.pdf");
        if (!File.Exists(pdfPath)) return null;

        try
        {
            using var doc = PdfDocument.Open(pdfPath);
            var page = doc.GetPage(1);
            var fonts = page.Resources?.GetDirectDictionaryOrNull("Font");
            if (fonts == null) return null;

            foreach (var kvp in fonts)
            {
                var fontDict = page.GetFont(kvp.Key.Value);
                if (fontDict == null) continue;
                if (fontDict.GetNameOrNull("Subtype") != "Type0") continue;

                // Walk to descendant font / FontDescriptor / FontFile3.
                var descObj = fontDict.GetOptional("DescendantFonts");
                if (descObj == null) continue;
                if (doc.Resolve(descObj) is not PdfArray descArr || descArr.Count == 0)
                    continue;
                if (doc.Resolve(descArr[0]) is not PdfDictionary cidFont) continue;
                var fdObj = cidFont.GetOptional("FontDescriptor");
                if (fdObj == null) continue;
                if (doc.Resolve(fdObj) is not PdfDictionary descriptor) continue;
                var ff3 = descriptor.GetOptional("FontFile3");
                if (ff3 == null) continue;
                if (doc.Resolve(ff3) is not PdfStream stream) continue;
                if (stream.GetNameOrNull("Subtype") != "CIDFontType0C") continue;
                return stream.DecodedData;
            }
        }
        catch { }
        return null;
    }

    private static byte[]? TryLoadSimpleCff()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "test-pdfs", "smoke")))
            dir = dir.Parent;
        if (dir == null) return null;

        var pdfPath = Path.Combine(dir.FullName, "test-pdfs", "smoke", "cdc-vis-covid-19.pdf");
        if (!File.Exists(pdfPath)) return null;

        try
        {
            using var doc = PdfDocument.Open(pdfPath);
            var page = doc.GetPage(1);
            var fonts = page.Resources?.GetDirectDictionaryOrNull("Font");
            if (fonts == null) return null;

            foreach (var kvp in fonts)
            {
                var fontDict = page.GetFont(kvp.Key.Value);
                if (fontDict == null) continue;
                var descObj = fontDict.GetOptional("FontDescriptor");
                if (descObj == null) continue;
                if (doc.Resolve(descObj) is not PdfDictionary descriptor) continue;
                var ff3 = descriptor.GetOptional("FontFile3");
                if (ff3 == null) continue;
                if (doc.Resolve(ff3) is not PdfStream stream) continue;
                if (stream.GetNameOrNull("Subtype") != "Type1C") continue;
                return stream.DecodedData;
            }
        }
        catch { }
        return null;
    }
}
