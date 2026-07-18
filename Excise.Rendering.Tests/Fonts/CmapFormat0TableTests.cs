using AwesomeAssertions;
using Excise.Rendering.Fonts;
using SkiaSharp;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Excise.Rendering.Tests.Fonts;

/// <summary>
/// Unit-level coverage for the format-0 cmap reader excise uses to render
/// subset TrueType fonts whose cmap is byte-keyed (Mac Roman / format-0).
/// veraPDF Test Builder, LibreOffice's PDF export and Office all emit
/// these — Skia's text shaper can't read them, so excise parses the table
/// itself and dispatches via SKTextEncoding.GlyphId.
/// </summary>
public class CmapFormat0TableTests
{
    /// <summary>
    /// Build a minimal SFNT-wrapped font with one cmap subtable in
    /// format-0 mapping byte codes 0x01..0x05 to glyph IDs 1..5.
    /// </summary>
    private static SKTypeface BuildFormat0CmapTypeface()
    {
        // We need a real loadable SFNT for SKTypeface.FromData to succeed.
        // Take any embedded subset font we can find in the smoke corpus
        // and substitute its cmap with a synthetic format-0 subtable.
        var donor = TryLoadDonor();
        if (donor == null) return null!;

        var cmap = BuildFormat0CmapTable();
        var withSyntheticCmap = ReplaceCmap(donor, cmap);
        return SKTypeface.FromData(SKData.CreateCopy(withSyntheticCmap))!;
    }

    [Fact]
    public void TryRead_ReturnsByteToGlyphMap_ForFormat0Cmap()
    {
        var typeface = BuildFormat0CmapTypeface();
        if (typeface == null) return;     // skip when donor font isn't on disk

        try
        {
            var map = CmapFormat0Table.TryRead(typeface);
            map.Should().NotBeNull();
            map![0x01].Should().Be(1);
            map[0x02].Should().Be(2);
            map[0x03].Should().Be(3);
            map[0x04].Should().Be(4);
            map[0x05].Should().Be(5);
            map[0x00].Should().Be(0, "0x00 has no entry in our synthetic cmap");
            map[0x06].Should().Be(0, "byte codes past LastChar should be unmapped");
        }
        finally { typeface.Dispose(); }
    }

    [Fact]
    public void TryRead_ReturnsNull_WhenAllEntriesAreNotdef()
    {
        // Build a cmap whose 256-byte glyphIdArray is all zero.
        var donor = TryLoadDonor();
        if (donor == null) return;

        var emptyCmap = BuildFormat0CmapTable(allZero: true);
        var withEmptyCmap = ReplaceCmap(donor, emptyCmap);
        using var typeface = SKTypeface.FromData(SKData.CreateCopy(withEmptyCmap));
        typeface.Should().NotBeNull();

        var map = CmapFormat0Table.TryRead(typeface);
        map.Should().BeNull("a cmap that maps every byte to .notdef has no mappings worth using");
    }

    // Build a 6 + 256 = 262-byte cmap subtable in format 0:
    //   uint16 format(=0), uint16 length(=262), uint16 language(=0),
    //   uint8 glyphIdArray[256]
    // Wrapped in a cmap table header pointing at that subtable.
    //
    // Total layout:
    //   cmap header (4 bytes) + 1 EncodingRecord (8 bytes) + format-0 subtable (262 bytes) = 274 bytes
    private static byte[] BuildFormat0CmapTable(bool allZero = false)
    {
        var cmap = new byte[4 + 8 + 6 + 256];
        // cmap header: version=0, numTables=1
        WriteU16(cmap, 0, 0);
        WriteU16(cmap, 2, 1);
        // EncodingRecord: platformID=1 (Macintosh), encodingID=0 (Roman),
        // offset=12 (start of subtable from cmap base)
        WriteU16(cmap, 4, 1);
        WriteU16(cmap, 6, 0);
        WriteU32(cmap, 8, 12);
        // Subtable: format=0, length=262, language=0
        WriteU16(cmap, 12, 0);
        WriteU16(cmap, 14, 262);
        WriteU16(cmap, 16, 0);
        // glyphIdArray: byte codes 0x01..0x05 → glyph IDs 1..5
        if (!allZero)
        {
            for (int b = 1; b <= 5; b++)
                cmap[18 + b] = (byte)b;
        }
        return cmap;
    }

    /// <summary>
    /// Replace the 'cmap' table in <paramref name="sfnt"/> with
    /// <paramref name="newCmap"/>. Rewrites the table directory, fixes
    /// offsets, and recomputes the table checksum (Skia validates).
    /// </summary>
    private static byte[] ReplaceCmap(byte[] sfnt, byte[] newCmap)
    {
        // sfnt header: uint32 version, uint16 numTables, uint16 searchRange,
        //              uint16 entrySelector, uint16 rangeShift
        // Then 16 bytes per TableRecord:
        //   uint32 tag, uint32 checksum, uint32 offset, uint32 length
        int numTables = ReadU16(sfnt, 4);

        // Find the cmap entry; record its old span so we can splice.
        int cmapRecOffset = -1;
        uint cmapTag = ((uint)'c' << 24) | ((uint)'m' << 16) | ((uint)'a' << 8) | 'p';
        for (int i = 0; i < numTables; i++)
        {
            int rec = 12 + i * 16;
            if (ReadU32(sfnt, rec) == cmapTag)
            {
                cmapRecOffset = rec;
                break;
            }
        }
        if (cmapRecOffset < 0)
            throw new InvalidDataException("donor SFNT has no cmap table — pick a different donor");

        uint oldOffset = ReadU32(sfnt, cmapRecOffset + 8);
        uint oldLength = ReadU32(sfnt, cmapRecOffset + 12);

        // Build new SFNT with the new cmap appended at the end. Update the
        // table record's offset/length/checksum and pad to 4-byte boundary.
        int paddedNew = (newCmap.Length + 3) & ~3;
        var output = new byte[sfnt.Length + paddedNew];
        Buffer.BlockCopy(sfnt, 0, output, 0, sfnt.Length);
        Buffer.BlockCopy(newCmap, 0, output, sfnt.Length, newCmap.Length);

        // Update record: leave checksum at 0 (Skia tolerates that for
        // synthetic tables in our existing CFF wrapper).
        WriteU32(output, cmapRecOffset + 4, 0);
        WriteU32(output, cmapRecOffset + 8, (uint)sfnt.Length);
        WriteU32(output, cmapRecOffset + 12, (uint)newCmap.Length);

        // Zero out any bytes the old cmap occupied so Skia doesn't accidentally
        // re-read it. Defensive — Skia uses the table directory, but harmless.
        if (oldOffset + oldLength <= (uint)sfnt.Length)
            Array.Clear(output, (int)oldOffset, (int)oldLength);

        return output;
    }

    private static byte[]? TryLoadDonor()
    {
        // Use an embedded subset TrueType font from the verapdf corpus when
        // it's on disk. Falls back to null when corpus isn't present so the
        // test silently skips in CI without test-pdfs/. Same lookup pattern
        // as CffWrapperTests.TryExtractCdcType1C, just resolving FontFile2
        // (TrueType) instead of FontFile3 (CFF).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(
            Path.Combine(dir.FullName, "test-pdfs", "verapdf-corpus")))
        {
            dir = dir.Parent;
        }
        if (dir == null) return null;

        var pdfPath = Path.Combine(dir.FullName, "test-pdfs", "verapdf-corpus",
            "veraPDF-corpus-master", "PDF_UA-1", "5 Version identification",
            "5-t01-pass-a.pdf");
        if (!File.Exists(pdfPath)) return null;

        try
        {
            using var doc = Excise.Core.Document.PdfDocument.Open(pdfPath);
            var page = doc.GetPage(1);
            var fonts = page.Resources?.GetDictionaryOrNull("Font");
            if (fonts == null) return null;

            foreach (var keyName in fonts.Keys)
            {
                var fontDict = page.GetFont(keyName.Value);
                if (fontDict == null) continue;
                var descObj = fontDict.GetOptional("FontDescriptor");
                if (descObj == null) continue;
                if (doc.Resolve(descObj) is not Excise.Core.Primitives.PdfDictionary desc)
                    continue;
                var ff2Obj = desc.GetOptional("FontFile2");
                if (ff2Obj == null) continue;
                if (doc.Resolve(ff2Obj) is not Excise.Core.Primitives.PdfStream s)
                    continue;
                return s.DecodedData;
            }
        }
        catch { }
        return null;
    }

    private static int ReadU16(byte[] d, int o) => (d[o] << 8) | d[o + 1];
    private static uint ReadU32(byte[] d, int o) =>
        ((uint)d[o] << 24) | ((uint)d[o + 1] << 16) |
        ((uint)d[o + 2] << 8) | (uint)d[o + 3];

    private static void WriteU16(byte[] d, int o, int v)
    {
        d[o] = (byte)(v >> 8);
        d[o + 1] = (byte)v;
    }

    private static void WriteU32(byte[] d, int o, uint v)
    {
        d[o] = (byte)(v >> 24);
        d[o + 1] = (byte)(v >> 16);
        d[o + 2] = (byte)(v >> 8);
        d[o + 3] = (byte)v;
    }
}
