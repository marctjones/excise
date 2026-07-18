using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Excise.Rendering.Fonts;

/// <summary>
/// Wraps raw CFF/Type1C font bytes in a minimal OpenType/SFNT container so that
/// SkiaSharp (which requires an SFNT wrapper — it can't load standalone CFF) can
/// load the font via <c>SKTypeface.FromData</c>.
///
/// Produces an "OTTO" (CFF-based OpenType) font with stub metric tables plus a
/// real cmap built from the caller-supplied Unicode→glyph-index map. The stub
/// tables are deliberately minimal; CFF carries its own metrics that Skia uses
/// for glyph shapes and our PDF /Widths path handles layout, so a rigorous
/// head/hhea/hmtx is not required for correct visual output.
///
/// Reference: OpenType spec (Microsoft Typography). This is a pragmatic
/// implementation — it covers the happy path for subset fonts in modern PDFs,
/// not every corner of the spec. Returns null on any failure so the caller can
/// fall back cleanly.
/// </summary>
internal static class CffToOpenType
{
    public sealed class PdfFontInfo
    {
        /// <summary>FontName for /name and /post tables. "Unknown" when absent.</summary>
        public string PsName = "Unknown";
        /// <summary>FontBBox from /FontDescriptor (font design units).</summary>
        public short XMin = 0, YMin = -300, XMax = 1000, YMax = 1000;
        public short Ascent = 800, Descent = -200;
        /// <summary>Italic angle in fixed-point 16.16. /FontDescriptor /ItalicAngle value.</summary>
        public int ItalicAngleFixed = 0;
        /// <summary>/FontDescriptor /FontWeight, defaulting to 400 (regular).</summary>
        public ushort WeightClass = 400;
        /// <summary>Unicode codepoint → CFF glyph index. Used to build the cmap.</summary>
        public IReadOnlyDictionary<char, int> UnicodeToGlyph = new Dictionary<char, int>();
        /// <summary>Glyph index → advance width in font design units (0..numGlyphs-1).
        /// When null, hmtx is built with a stub width for every glyph.</summary>
        public IReadOnlyDictionary<int, ushort>? GlyphWidths;
    }

    public static byte[]? Wrap(byte[] cffBytes, int numGlyphs, PdfFontInfo info)
    {
        if (cffBytes == null || cffBytes.Length == 0 || numGlyphs <= 0)
            return null;

        try
        {
            // Build every table body first so we know their lengths before writing
            // the sfnt header and directory.
            byte[] head = BuildHead(info);
            byte[] hhea = BuildHhea(info, numGlyphs);
            byte[] hmtx = BuildHmtx(numGlyphs, info.GlyphWidths);
            byte[] maxp = BuildMaxp(numGlyphs);
            byte[] name = BuildName(info.PsName);
            byte[] os2 = BuildOs2(info);
            byte[] post = BuildPost(info);
            byte[] cmap = BuildCmap(info.UnicodeToGlyph);

            // Tables required by the OTTO spec. Sort alphabetically by tag for
            // the directory (Skia is strict about this).
            var tables = new List<(string Tag, byte[] Data)>
            {
                ("CFF ", cffBytes),
                ("OS/2", os2),
                ("cmap", cmap),
                ("head", head),
                ("hhea", hhea),
                ("hmtx", hmtx),
                ("maxp", maxp),
                ("name", name),
                ("post", post),
            };
            tables.Sort((a, b) => string.CompareOrdinal(a.Tag, b.Tag));

            int numTables = tables.Count;
            int headerSize = 12;
            int directorySize = numTables * 16;

            // Compute table offsets (4-byte aligned).
            int offset = headerSize + directorySize;
            int[] offsets = new int[numTables];
            for (int i = 0; i < numTables; i++)
            {
                offsets[i] = offset;
                offset += AlignUp(tables[i].Data.Length, 4);
            }
            int totalSize = offset;

            var buf = new byte[totalSize];
            int p = 0;

            // Offset Table (sfnt header).
            int entrySelector = Log2Floor(numTables);
            int searchRange = (1 << entrySelector) * 16;
            int rangeShift = numTables * 16 - searchRange;

            WriteU32(buf, ref p, 0x4F54544F); // 'OTTO'
            WriteU16(buf, ref p, (ushort)numTables);
            WriteU16(buf, ref p, (ushort)searchRange);
            WriteU16(buf, ref p, (ushort)entrySelector);
            WriteU16(buf, ref p, (ushort)rangeShift);

            // Remember where head's checkSumAdjustment lives so we can fix it up
            // after writing everything.
            int headTableOffset = -1;

            for (int i = 0; i < numTables; i++)
            {
                var (tag, data) = tables[i];
                uint checksum = ComputeChecksum(data);
                WriteU32(buf, ref p, TagToU32(tag));
                WriteU32(buf, ref p, checksum);
                WriteU32(buf, ref p, (uint)offsets[i]);
                WriteU32(buf, ref p, (uint)data.Length);
                if (tag == "head") headTableOffset = offsets[i];
            }

            for (int i = 0; i < numTables; i++)
            {
                Array.Copy(tables[i].Data, 0, buf, offsets[i], tables[i].Data.Length);
                // Zero-padding is already in place (fresh array).
            }

            // Fix up checkSumAdjustment in head: it equals 0xB1B0AFBA minus the
            // checksum of the entire file (with the field treated as 0).
            if (headTableOffset >= 0)
            {
                uint fileChecksum = ComputeChecksum(buf);
                uint adj = 0xB1B0AFBAu - fileChecksum;
                int pa = headTableOffset + 8; // offset within head
                WriteU32(buf, ref pa, adj);
            }

            return buf;
        }
        catch
        {
            return null;
        }
    }

    // --- Table builders ---

    private static byte[] BuildHead(PdfFontInfo info)
    {
        var ms = new MemoryStream();
        WriteU32(ms, 0x00010000); // version
        WriteU32(ms, 0x00010000); // fontRevision
        WriteU32(ms, 0); // checkSumAdjustment (fixed up later)
        WriteU32(ms, 0x5F0F3CF5); // magicNumber
        WriteU16(ms, 0x0003); // flags
        WriteU16(ms, 1000); // unitsPerEm (CFF convention)
        WriteI64(ms, 0); // created
        WriteI64(ms, 0); // modified
        WriteI16(ms, info.XMin);
        WriteI16(ms, info.YMin);
        WriteI16(ms, info.XMax);
        WriteI16(ms, info.YMax);
        WriteU16(ms, 0); // macStyle
        WriteU16(ms, 6); // lowestRecPPEM
        WriteI16(ms, 2); // fontDirectionHint
        WriteI16(ms, 0); // indexToLocFormat
        WriteI16(ms, 0); // glyphDataFormat
        return ms.ToArray();
    }

    private static byte[] BuildHhea(PdfFontInfo info, int numGlyphs)
    {
        var ms = new MemoryStream();
        WriteU32(ms, 0x00010000);
        WriteI16(ms, info.Ascent);
        WriteI16(ms, info.Descent);
        WriteI16(ms, 0); // lineGap
        WriteU16(ms, 1000); // advanceWidthMax (upper bound)
        WriteI16(ms, 0); // minLeftSideBearing
        WriteI16(ms, 0); // minRightSideBearing
        WriteI16(ms, info.XMax); // xMaxExtent
        WriteI16(ms, 1); // caretSlopeRise
        WriteI16(ms, 0); // caretSlopeRun
        WriteI16(ms, 0); // caretOffset
        WriteI16(ms, 0); WriteI16(ms, 0); WriteI16(ms, 0); WriteI16(ms, 0); // reserved
        WriteI16(ms, 0); // metricDataFormat
        WriteU16(ms, (ushort)Math.Min(numGlyphs, 65535)); // numberOfHMetrics
        return ms.ToArray();
    }

    private static byte[] BuildHmtx(int numGlyphs, IReadOnlyDictionary<int, ushort>? glyphWidths)
    {
        // One entry per glyph. Populate with PDF /Widths keyed by glyph index
        // when available — Skia uses hmtx for measuring multi-glyph runs even
        // when CFF has its own widths internally.
        var ms = new MemoryStream();
        for (int g = 0; g < numGlyphs; g++)
        {
            ushort w = 500;
            if (glyphWidths != null && glyphWidths.TryGetValue(g, out var found))
                w = found;
            WriteU16(ms, w);
            WriteI16(ms, 0);
        }
        return ms.ToArray();
    }

    private static byte[] BuildMaxp(int numGlyphs)
    {
        var ms = new MemoryStream();
        WriteU32(ms, 0x00005000); // CFF variant
        WriteU16(ms, (ushort)Math.Min(numGlyphs, 65535));
        return ms.ToArray();
    }

    private static byte[] BuildName(string psName)
    {
        // One name record: PostScript name (nameID 6) in Windows/Unicode.
        var nameBytes = Encoding.BigEndianUnicode.GetBytes(psName);
        var ms = new MemoryStream();
        WriteU16(ms, 0); // format
        WriteU16(ms, 1); // count
        WriteU16(ms, (ushort)(6 + 12)); // stringOffset = header(6) + 1 record(12)
        WriteU16(ms, 3); // platformID (Windows)
        WriteU16(ms, 1); // encodingID (Unicode BMP)
        WriteU16(ms, 0x0409); // languageID (en-US)
        WriteU16(ms, 6); // nameID (PostScript name)
        WriteU16(ms, (ushort)nameBytes.Length);
        WriteU16(ms, 0); // string offset (within string storage)
        ms.Write(nameBytes, 0, nameBytes.Length);
        return ms.ToArray();
    }

    private static byte[] BuildOs2(PdfFontInfo info)
    {
        var ms = new MemoryStream();
        WriteU16(ms, 4); // version
        WriteI16(ms, 500); // xAvgCharWidth
        WriteU16(ms, info.WeightClass);
        WriteU16(ms, 5); // usWidthClass (medium)
        WriteU16(ms, 0); // fsType
        WriteI16(ms, 650); WriteI16(ms, 700); WriteI16(ms, 0); WriteI16(ms, 140); // ySubscript*
        WriteI16(ms, 650); WriteI16(ms, 700); WriteI16(ms, 0); WriteI16(ms, 470); // ySuperscript*
        WriteI16(ms, 50); WriteI16(ms, 250); // yStrikeout*
        WriteI16(ms, 0); // sFamilyClass
        for (int i = 0; i < 10; i++) ms.WriteByte(0); // panose
        WriteU32(ms, 0xFFFFFFFFu); WriteU32(ms, 0xFFFFFFFFu);
        WriteU32(ms, 0xFFFFFFFFu); WriteU32(ms, 0xFFFFFFFFu); // ulUnicodeRange1-4
        ms.Write(Encoding.ASCII.GetBytes("anon"), 0, 4); // achVendID
        WriteU16(ms, 0x0040); // fsSelection (regular)
        WriteU16(ms, 0x20); // usFirstCharIndex
        WriteU16(ms, 0xFFFF); // usLastCharIndex
        WriteI16(ms, info.Ascent); WriteI16(ms, info.Descent); WriteI16(ms, 0); // sTypo*
        WriteU16(ms, (ushort)Math.Max(0, (int)info.Ascent)); // usWinAscent
        WriteU16(ms, (ushort)Math.Max(0, -(int)info.Descent)); // usWinDescent
        WriteU32(ms, 1); WriteU32(ms, 0); // codePageRange
        WriteI16(ms, 500); // sxHeight
        WriteI16(ms, 700); // sCapHeight
        WriteU16(ms, 0); WriteU16(ms, 0x20); WriteU16(ms, 0); // usDefault/Break/MaxContext
        return ms.ToArray();
    }

    private static byte[] BuildPost(PdfFontInfo info)
    {
        var ms = new MemoryStream();
        WriteU32(ms, 0x00030000); // version 3 (no glyph names)
        WriteU32(ms, (uint)info.ItalicAngleFixed); // italicAngle (16.16 fixed)
        WriteI16(ms, -100); // underlinePosition
        WriteI16(ms, 50); // underlineThickness
        WriteU32(ms, 0); // isFixedPitch
        WriteU32(ms, 0); WriteU32(ms, 0); WriteU32(ms, 0); WriteU32(ms, 0); // mem fields
        return ms.ToArray();
    }

    private static byte[] BuildCmap(IReadOnlyDictionary<char, int> unicodeToGlyph)
    {
        // Build one segment per codepoint (least-efficient but simplest correct
        // format-4 layout). Add the mandatory terminal segment (0xFFFF → 0).
        var entries = new List<(char Unicode, int GlyphIndex)>();
        foreach (var kvp in unicodeToGlyph)
        {
            if (kvp.Key == 0 || kvp.Key == 0xFFFF) continue;
            if (kvp.Value <= 0 || kvp.Value > 65535) continue;
            entries.Add((kvp.Key, kvp.Value));
        }
        entries.Sort((a, b) => a.Unicode.CompareTo(b.Unicode));

        int segCount = entries.Count + 1; // + terminal
        int segCountX2 = segCount * 2;
        int entrySelector = Log2Floor(segCount);
        int searchRange = 2 * (1 << entrySelector);
        int rangeShift = segCountX2 - searchRange;

        // Format 4 subtable size:
        //   14 bytes header + segCount*2 endCode + 2 reservedPad +
        //   segCount*2 startCode + segCount*2 idDelta + segCount*2 idRangeOffset
        int subtableLength = 14 + 2 + 2 * 4 * segCount;

        // cmap table = 4 (version+numTables) + 8 (encoding record) + subtable.
        int totalLength = 4 + 8 + subtableLength;
        var ms = new MemoryStream(totalLength);

        WriteU16(ms, 0); // version
        WriteU16(ms, 1); // numTables
        // Encoding record: Windows Unicode BMP.
        WriteU16(ms, 3); WriteU16(ms, 1);
        WriteU32(ms, 12); // offset (4 + 8)

        // Format 4 subtable.
        WriteU16(ms, 4); // format
        WriteU16(ms, (ushort)subtableLength); // length
        WriteU16(ms, 0); // language
        WriteU16(ms, (ushort)segCountX2);
        WriteU16(ms, (ushort)searchRange);
        WriteU16(ms, (ushort)entrySelector);
        WriteU16(ms, (ushort)rangeShift);

        // endCode[segCount]: one per entry, then 0xFFFF terminal.
        foreach (var e in entries) WriteU16(ms, e.Unicode);
        WriteU16(ms, 0xFFFF);
        WriteU16(ms, 0); // reservedPad
        // startCode[segCount].
        foreach (var e in entries) WriteU16(ms, e.Unicode);
        WriteU16(ms, 0xFFFF);
        // idDelta[segCount]: glyphIndex = (code + idDelta) mod 65536.
        foreach (var e in entries)
        {
            int delta = (e.GlyphIndex - e.Unicode) & 0xFFFF;
            WriteU16(ms, (ushort)delta);
        }
        WriteU16(ms, 1); // terminal delta (0xFFFF + 1 = 0)
        // idRangeOffset[segCount]: all zero (direct delta lookup).
        for (int i = 0; i < segCount; i++) WriteU16(ms, 0);

        return ms.ToArray();
    }

    // --- Checksum / utility ---

    private static uint ComputeChecksum(byte[] data)
    {
        uint sum = 0;
        int i = 0;
        int end = data.Length & ~3;
        while (i < end)
        {
            sum += ((uint)data[i] << 24) | ((uint)data[i + 1] << 16) |
                   ((uint)data[i + 2] << 8) | data[i + 3];
            i += 4;
        }
        // Tail: pad with zeros
        if (i < data.Length)
        {
            uint tail = 0;
            int shift = 24;
            while (i < data.Length)
            {
                tail |= ((uint)data[i] << shift);
                shift -= 8;
                i++;
            }
            sum += tail;
        }
        return sum;
    }

    private static int Log2Floor(int n)
    {
        int r = 0;
        while ((1 << (r + 1)) <= n) r++;
        return r;
    }

    private static int AlignUp(int v, int align) => (v + align - 1) & ~(align - 1);

    private static uint TagToU32(string tag)
    {
        return ((uint)tag[0] << 24) | ((uint)tag[1] << 16) |
               ((uint)tag[2] << 8) | tag[3];
    }

    // --- Write helpers (big-endian) ---

    private static void WriteU16(byte[] buf, ref int p, ushort v)
    {
        buf[p++] = (byte)(v >> 8); buf[p++] = (byte)v;
    }
    private static void WriteU32(byte[] buf, ref int p, uint v)
    {
        buf[p++] = (byte)(v >> 24); buf[p++] = (byte)(v >> 16);
        buf[p++] = (byte)(v >> 8); buf[p++] = (byte)v;
    }
    private static void WriteU16(MemoryStream ms, ushort v)
    {
        ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v);
    }
    private static void WriteU16(MemoryStream ms, int v) => WriteU16(ms, (ushort)v);
    private static void WriteI16(MemoryStream ms, short v)
    {
        ms.WriteByte((byte)((ushort)v >> 8)); ms.WriteByte((byte)(ushort)v);
    }
    private static void WriteI16(MemoryStream ms, int v) => WriteI16(ms, (short)v);
    private static void WriteU32(MemoryStream ms, uint v)
    {
        ms.WriteByte((byte)(v >> 24)); ms.WriteByte((byte)(v >> 16));
        ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v);
    }
    private static void WriteU32(MemoryStream ms, int v) => WriteU32(ms, (uint)v);
    private static void WriteI64(MemoryStream ms, long v)
    {
        for (int i = 7; i >= 0; i--) ms.WriteByte((byte)(v >> (i * 8)));
    }
}
