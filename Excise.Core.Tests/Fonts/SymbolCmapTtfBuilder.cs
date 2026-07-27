using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Excise.Core.Tests.Fonts;

/// <summary>
/// Test-only surgery on a real TrueType font (DejaVu Sans) that REPLACES its
/// cmap table with a single Microsoft-Symbol <c>(3,0)</c> subtable, keeping
/// every other table — including the real <c>glyf</c> outlines and the
/// <c>post</c> (v2.0) glyph names — byte-for-byte.
///
/// A <c>(3,0)</c> symbol cmap addresses glyphs through an F000-based Private Use
/// offset: content-stream code <c>0xNN</c> is looked up in the cmap at
/// <c>0xF0NN</c>. This builder maps <c>0xF000 | code =&gt; gid</c> for each
/// caller-supplied (contentCode, intendedLetter) pair, resolving the letter's
/// glyph id through DejaVu's own Unicode cmap.
///
/// Why keep <c>post</c>: it names each glyph (e.g. gid → "R"), so an independent
/// extractor (mutool) can recover Unicode via glyph-name → AdobeGlyphList even
/// though there is no /ToUnicode and no Unicode cmap. That makes the fixture a
/// RECOVERABLE symbol font — the oracle can read it, so a excise mis-decode is a
/// real, assertable divergence rather than genuinely-unrecoverable text (#791).
/// </summary>
internal static class SymbolCmapTtfBuilder
{
    /// <summary>
    /// Returns a DejaVu-derived TrueType with a single (3,0) symbol cmap mapping
    /// (0xF000 | code) → glyph(letter) for each pair. Codes must be 0x00..0xFF.
    /// </summary>
    public static byte[] BuildSymbolCmapFont(byte[] dejaVu, IReadOnlyList<(int Code, char Letter)> mapping)
    {
        var dir = ReadTableDirectory(dejaVu);
        var unicodeToGid = ParseUnicodeCmap(dejaVu, dir["cmap"]);

        var symbolEntries = new List<(int SymbolCode, int Gid)>();
        foreach (var (code, letter) in mapping)
        {
            if (code < 0 || code > 0xFF)
                throw new ArgumentOutOfRangeException(nameof(mapping), $"code 0x{code:X} out of byte range");
            if (!unicodeToGid.TryGetValue(letter, out var gid) || gid == 0)
                throw new InvalidOperationException($"DejaVu has no glyph for '{letter}' (U+{(int)letter:X4})");
            symbolEntries.Add((0xF000 | code, gid));
        }

        var newCmap = BuildSymbolCmapTable(symbolEntries);

        // Reassemble: every original table verbatim except 'cmap'.
        var tables = new List<(string Tag, byte[] Data)>();
        foreach (var kv in dir)
        {
            var (off, len) = kv.Value;
            var data = kv.Key == "cmap" ? newCmap : dejaVu.AsSpan(off, len).ToArray();
            tables.Add((kv.Key, data));
        }
        return AssembleSfnt(dejaVu, tables);
    }

    // ---- table directory -----------------------------------------------------

    private static Dictionary<string, (int Off, int Len)> ReadTableDirectory(byte[] d)
    {
        int num = U16(d, 4);
        var dir = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        for (int i = 0; i < num; i++)
        {
            int rec = 12 + i * 16;
            string tag = System.Text.Encoding.ASCII.GetString(d, rec, 4);
            int off = (int)U32(d, rec + 8);
            int len = (int)U32(d, rec + 12);
            dir[tag] = (off, len);
        }
        return dir;
    }

    // ---- parse DejaVu's Unicode cmap ((3,1) format 4) → char→gid --------------

    private static Dictionary<int, int> ParseUnicodeCmap(byte[] d, (int Off, int Len) cmap)
    {
        int co = cmap.Off;
        int n = U16(d, co + 2);
        int best = -1;
        foreach (var i in Enumerable.Range(0, n))
        {
            int rec = co + 4 + i * 8;
            int plat = U16(d, rec), enc = U16(d, rec + 2);
            int sub = co + (int)U32(d, rec + 4);
            int fmt = U16(d, sub);
            if (plat == 3 && enc == 1 && fmt == 4) { best = sub; break; }
        }
        if (best < 0) throw new InvalidOperationException("DejaVu has no (3,1) format-4 cmap");

        var map = new Dictionary<int, int>();
        int segX2 = U16(d, best + 6);
        int segCount = segX2 / 2;
        int endP = best + 14;
        int startP = endP + segX2 + 2;
        int deltaP = startP + segX2;
        int rangeP = deltaP + segX2;
        for (int s = 0; s < segCount; s++)
        {
            int end = U16(d, endP + s * 2);
            int start = U16(d, startP + s * 2);
            short delta = (short)U16(d, deltaP + s * 2);
            int rangeOff = U16(d, rangeP + s * 2);
            for (int c = start; c <= end && c != 0xFFFF; c++)
            {
                int gid;
                if (rangeOff == 0) gid = (c + delta) & 0xFFFF;
                else
                {
                    int giAddr = rangeP + s * 2 + rangeOff + (c - start) * 2;
                    int g = U16(d, giAddr);
                    gid = g == 0 ? 0 : (g + delta) & 0xFFFF;
                }
                if (gid != 0) map[c] = gid;
            }
        }
        return map;
    }

    // ---- build a (3,0) symbol cmap table -------------------------------------

    private static byte[] BuildSymbolCmapTable(List<(int SymbolCode, int Gid)> entries)
    {
        entries = entries.OrderBy(e => e.SymbolCode).ToList();
        // One format-4 segment per code, plus the mandatory 0xFFFF terminator.
        var segStart = new List<int>();
        var segEnd = new List<int>();
        var segDelta = new List<int>();
        foreach (var (code, gid) in entries)
        {
            segStart.Add(code);
            segEnd.Add(code);
            segDelta.Add((gid - code) & 0xFFFF);
        }
        segStart.Add(0xFFFF); segEnd.Add(0xFFFF); segDelta.Add(1);

        int segCount = segStart.Count;
        int segX2 = segCount * 2;
        int entrySelector = (int)Math.Floor(Math.Log2(segCount));
        int searchRange = 2 * (1 << entrySelector);
        int rangeShift = segX2 - searchRange;

        using var sub = new MemoryStream();
        void S16(int v) { sub.WriteByte((byte)(v >> 8)); sub.WriteByte((byte)v); }
        S16(4);                       // format
        int lenPos = (int)sub.Position; S16(0); // length placeholder
        S16(0);                       // language
        S16(segX2);
        S16(searchRange);
        S16(entrySelector);
        S16(rangeShift);
        foreach (var e in segEnd) S16(e);
        S16(0);                       // reservedPad
        foreach (var s in segStart) S16(s);
        foreach (var dlt in segDelta) S16(dlt);
        foreach (var _ in segStart) S16(0); // idRangeOffset all zero
        var subBytes = sub.ToArray();
        subBytes[lenPos] = (byte)(subBytes.Length >> 8);
        subBytes[lenPos + 1] = (byte)subBytes.Length;

        using var ms = new MemoryStream();
        void W16(int v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
        void W32(long v) { ms.WriteByte((byte)(v >> 24)); ms.WriteByte((byte)(v >> 16)); ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
        W16(0);                       // cmap version
        W16(1);                       // numTables
        W16(3);                       // platformID = Microsoft
        W16(0);                       // encodingID = Symbol
        W32(12);                      // offset to subtable
        ms.Write(subBytes, 0, subBytes.Length);
        return ms.ToArray();
    }

    // ---- reassemble sfnt with recomputed directory + checksums ---------------

    private static byte[] AssembleSfnt(byte[] original, List<(string Tag, byte[] Data)> tables)
    {
        // Preserve the original sfnt version ('true'/0x00010000/'OTTO').
        uint sfntVersion = U32(original, 0);
        tables.Sort((a, b) => string.CompareOrdinal(a.Tag, b.Tag));
        int numTables = tables.Count;
        int headerSize = 12;
        int directorySize = numTables * 16;

        int offset = headerSize + directorySize;
        var offsets = new int[numTables];
        for (int i = 0; i < numTables; i++)
        {
            offsets[i] = offset;
            offset += AlignUp(tables[i].Data.Length, 4);
        }
        int total = offset;
        var buf = new byte[total];
        int p = 0;

        int entrySelector = (int)Math.Floor(Math.Log2(numTables));
        int searchRange = (1 << entrySelector) * 16;
        int rangeShift = numTables * 16 - searchRange;
        WriteU32(buf, ref p, sfntVersion);
        WriteU16(buf, ref p, (ushort)numTables);
        WriteU16(buf, ref p, (ushort)searchRange);
        WriteU16(buf, ref p, (ushort)entrySelector);
        WriteU16(buf, ref p, (ushort)rangeShift);

        int headOffset = -1;
        for (int i = 0; i < numTables; i++)
        {
            var (tag, data) = tables[i];
            WriteU32(buf, ref p, TagToU32(tag));
            WriteU32(buf, ref p, Checksum(data));
            WriteU32(buf, ref p, (uint)offsets[i]);
            WriteU32(buf, ref p, (uint)data.Length);
            if (tag == "head") headOffset = offsets[i];
        }
        for (int i = 0; i < numTables; i++)
            Array.Copy(tables[i].Data, 0, buf, offsets[i], tables[i].Data.Length);

        if (headOffset >= 0)
        {
            // Zero checkSumAdjustment, then set it to 0xB1B0AFBA - fileChecksum.
            int adjPos = headOffset + 8;
            buf[adjPos] = buf[adjPos + 1] = buf[adjPos + 2] = buf[adjPos + 3] = 0;
            uint fileSum = Checksum(buf);
            int pa = adjPos;
            WriteU32(buf, ref pa, 0xB1B0AFBAu - fileSum);
        }
        return buf;
    }

    // ---- primitives ----------------------------------------------------------

    private static int AlignUp(int v, int a) => (v + a - 1) / a * a;

    private static uint Checksum(byte[] data)
    {
        uint sum = 0;
        int i = 0;
        for (; i + 4 <= data.Length; i += 4)
            sum += (uint)((data[i] << 24) | (data[i + 1] << 16) | (data[i + 2] << 8) | data[i + 3]);
        if (i < data.Length)
        {
            uint last = 0;
            for (int j = 0; j < 4; j++)
                last = (last << 8) | (i + j < data.Length ? data[i + j] : (uint)0);
            sum += last;
        }
        return sum;
    }

    private static uint TagToU32(string tag) =>
        ((uint)tag[0] << 24) | ((uint)tag[1] << 16) | ((uint)tag[2] << 8) | tag[3];

    private static int U16(byte[] d, int o) => (d[o] << 8) | d[o + 1];
    private static uint U32(byte[] d, int o) =>
        ((uint)d[o] << 24) | ((uint)d[o + 1] << 16) | ((uint)d[o + 2] << 8) | d[o + 3];

    private static void WriteU16(byte[] b, ref int p, ushort v) { b[p++] = (byte)(v >> 8); b[p++] = (byte)v; }
    private static void WriteU32(byte[] b, ref int p, uint v)
    { b[p++] = (byte)(v >> 24); b[p++] = (byte)(v >> 16); b[p++] = (byte)(v >> 8); b[p++] = (byte)v; }
}
