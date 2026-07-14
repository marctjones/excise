using System.Text;

namespace Pdfe.Core.Fonts;

/// <summary>
/// Minimal read-only parser for a TrueType-outline (<c>glyf</c>-based) or CFF-outline
/// OpenType sfnt font, exposing exactly what's needed to <em>embed</em> the font and
/// lay out Unicode text: units-per-em, glyph count, per-glyph advance widths, a
/// Unicode→glyph (cmap) map, the font bounding box, and vertical metrics.
///
/// <para>Supports TrueType files (sfnt version 0x00010000 or 'true') and CFF-based
/// OpenType ('OTTO'). Both use the same sfnt wrapper with cmap/hmtx/head/hhea/maxp
/// tables; the only difference is the outline format (glyf vs CFF).</para>
/// </summary>
internal sealed class TrueTypeFontFile
{
    public byte[] Data { get; }
    public ushort UnitsPerEm { get; }
    public int GlyphCount { get; }
    public string PostScriptName { get; }
    public short XMin { get; }
    public short YMin { get; }
    public short XMax { get; }
    public short YMax { get; }
    public short Ascent { get; }
    public short Descent { get; }
    public bool IsBold { get; }
    public bool IsItalic { get; }
    public bool IsCff { get; }

    private readonly ushort[] _advanceWidths;          // per glyph id, font units
    private readonly Dictionary<int, int> _cmap;       // unicode codepoint -> gid

    private TrueTypeFontFile(byte[] data, ushort unitsPerEm, int glyphCount, string psName,
        short xMin, short yMin, short xMax, short yMax, short ascent, short descent,
        bool bold, bool italic, bool isCff, ushort[] advanceWidths, Dictionary<int, int> cmap)
    {
        Data = data; UnitsPerEm = unitsPerEm; GlyphCount = glyphCount; PostScriptName = psName;
        XMin = xMin; YMin = yMin; XMax = xMax; YMax = yMax; Ascent = ascent; Descent = descent;
        IsBold = bold; IsItalic = italic; IsCff = isCff; _advanceWidths = advanceWidths; _cmap = cmap;
    }

    /// <summary>Glyph id for a Unicode codepoint, or 0 (.notdef) if unmapped.</summary>
    public int GidForCodepoint(int codepoint) => _cmap.TryGetValue(codepoint, out var g) ? g : 0;

    /// <summary>Advance width of a glyph in font units (clamped to the table).</summary>
    public int AdvanceWidth(int gid)
    {
        if (_advanceWidths.Length == 0) return 0;
        return gid < _advanceWidths.Length ? _advanceWidths[gid] : _advanceWidths[^1];
    }

    /// <summary>codepoint → gid map (read-only), used to build /W and ToUnicode.</summary>
    public IReadOnlyDictionary<int, int> Cmap => _cmap;

    public static TrueTypeFontFile Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        try
        {
            return ParseCore(data);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or NotSupportedException))
        {
            // #648: BE.U16/S16/U32 do raw, unchecked array indexing (by
            // design — this is a hot path). A truncated or adversarially
            // mutated font (offsets/counts pointing past the buffer) turns
            // that into a raw IndexOutOfRangeException/ArgumentOutOfRangeException
            // instead of this class's documented failure mode. Convert it
            // to the same NotSupportedException every other "can't use this
            // font" path already throws, rather than letting a malformed
            // embedded font crash the caller.
            throw new NotSupportedException(
                $"Malformed or truncated font data ({ex.GetType().Name}: {ex.Message}).", ex);
        }
    }

    private static TrueTypeFontFile ParseCore(byte[] data)
    {
        var r = new BE(data);
        uint sfnt = r.U32(0);
        bool isCff = sfnt == 0x4F54544F; // 'OTTO'
        bool isTrueType = sfnt == 0x00010000 || sfnt == 0x74727565; // 0x00010000 or 'true'

        if (!isCff && !isTrueType)
            throw new NotSupportedException(
                "Only TrueType-outline (sfnt 0x00010000 or 'true') and CFF-based OpenType ('OTTO') fonts are supported for embedding.");

        ushort numTables = r.U16(4);
        var tables = new Dictionary<string, (int off, int len)>();
        int p = 12;
        for (int i = 0; i < numTables; i++, p += 16)
        {
            string tag = Encoding.ASCII.GetString(data, p, 4);
            tables[tag] = ((int)r.U32(p + 8), (int)r.U32(p + 12));
        }

        (int off, int len) Req(string t) =>
            tables.TryGetValue(t, out var v) ? v : throw new NotSupportedException($"Font missing required '{t}' table.");

        var head = Req("head");
        ushort unitsPerEm = r.U16(head.off + 18);
        short xMin = r.S16(head.off + 36), yMin = r.S16(head.off + 38);
        short xMax = r.S16(head.off + 40), yMax = r.S16(head.off + 42);
        ushort macStyle = r.U16(head.off + 44);
        short indexToLocFormat = r.S16(head.off + 50);
        _ = indexToLocFormat;

        var maxp = Req("maxp");
        int numGlyphs = r.U16(maxp.off + 4);

        var hhea = Req("hhea");
        short ascent = r.S16(hhea.off + 4), descent = r.S16(hhea.off + 6);
        int numberOfHMetrics = r.U16(hhea.off + 34);

        var hmtx = Req("hmtx");
        var adv = new ushort[numGlyphs];
        ushort last = 0;
        for (int i = 0; i < numGlyphs; i++)
        {
            if (i < numberOfHMetrics) last = r.U16(hmtx.off + i * 4);
            adv[i] = last;
        }

        var cmap = ParseCmap(r, Req("cmap").off);
        string psName = tables.TryGetValue("name", out var nameTbl)
            ? (ReadPostScriptName(r, nameTbl.off) ?? "EmbeddedFont")
            : "EmbeddedFont";

        return new TrueTypeFontFile(data, unitsPerEm, numGlyphs, psName,
            xMin, yMin, xMax, yMax, ascent, descent,
            (macStyle & 0x1) != 0, (macStyle & 0x2) != 0, isCff, adv, cmap);
    }

    private static Dictionary<int, int> ParseCmap(BE r, int cmapOff)
    {
        ushort numSub = r.U16(cmapOff + 2);
        // Prefer a full-Unicode (format 12) subtable, else BMP (format 4).
        int best = -1, bestScore = -1;
        for (int i = 0; i < numSub; i++)
        {
            int rec = cmapOff + 4 + i * 8;
            ushort plat = r.U16(rec), enc = r.U16(rec + 2);
            int sub = cmapOff + (int)r.U32(rec + 4);
            ushort fmt = r.U16(sub);
            int score = fmt switch
            {
                12 when plat == 3 && enc == 10 => 5,
                4 when plat == 3 && enc == 1 => 4,
                12 => 3,
                4 => 2,
                6 or 0 => 1,
                _ => 0
            };
            if (score > bestScore) { bestScore = score; best = sub; }
        }
        var map = new Dictionary<int, int>();
        if (best < 0) return map;

        ushort format = r.U16(best);
        if (format == 4)
        {
            ushort segX2 = r.U16(best + 6);
            int segCount = segX2 / 2;
            int endP = best + 14;
            int startP = endP + segX2 + 2;            // +2 reservedPad
            int deltaP = startP + segX2;
            int rangeP = deltaP + segX2;
            for (int s = 0; s < segCount; s++)
            {
                ushort end = r.U16(endP + s * 2);
                ushort start = r.U16(startP + s * 2);
                short delta = r.S16(deltaP + s * 2);
                ushort rangeOff = r.U16(rangeP + s * 2);
                for (int c = start; c <= end && c != 0xFFFF; c++)
                {
                    int gid;
                    if (rangeOff == 0) gid = (c + delta) & 0xFFFF;
                    else
                    {
                        int giAddr = rangeP + s * 2 + rangeOff + (c - start) * 2;
                        ushort g = r.U16(giAddr);
                        gid = g == 0 ? 0 : (g + delta) & 0xFFFF;
                    }
                    if (gid != 0) map[c] = gid;
                }
            }
        }
        else if (format == 12)
        {
            uint nGroups = r.U32(best + 12);
            int gp = best + 16;
            for (uint g = 0; g < nGroups; g++, gp += 12)
            {
                uint startC = r.U32(gp), endC = r.U32(gp + 4), startG = r.U32(gp + 8);
                // #648: a malformed/adversarial cmap can set endC far below
                // startC (loop never enters — harmless) or so far above it
                // that this becomes a multi-billion-iteration loop; endC =
                // 0xFFFFFFFF is worse still, since incrementing a uint past
                // its max wraps to 0 and the loop never terminates at all.
                // Valid Unicode tops out at 0x10FFFF — reject any group
                // that claims a bigger span than the entire codepoint space.
                if (endC < startC || endC - startC > 0x10FFFF) continue;
                for (uint c = startC; c <= endC; c++)
                    map[(int)c] = (int)(startG + (c - startC));
            }
        }
        else if (format == 6)
        {
            ushort first = r.U16(best + 6), count = r.U16(best + 8);
            for (int i = 0; i < count; i++)
            {
                int gid = r.U16(best + 10 + i * 2);
                if (gid != 0) map[first + i] = gid;
            }
        }
        else if (format == 0)
        {
            for (int c = 0; c < 256; c++)
            {
                int gid = r.Data[best + 6 + c];
                if (gid != 0) map[c] = gid;
            }
        }
        return map;
    }

    private static string? ReadPostScriptName(BE r, int nameOff)
    {
        ushort count = r.U16(nameOff + 2);
        int storage = nameOff + r.U16(nameOff + 4);
        string? fallback = null;
        for (int i = 0; i < count; i++)
        {
            int rec = nameOff + 6 + i * 12;
            ushort plat = r.U16(rec), enc = r.U16(rec + 2), nameId = r.U16(rec + 6);
            ushort len = r.U16(rec + 8), off = r.U16(rec + 10);
            if (nameId != 6) continue;                 // PostScript name
            // Platform 3 (Windows) / 0 (Unicode) store UTF-16BE; 1 (Mac) Latin-1.
            string val = (plat == 3 || plat == 0)
                ? Encoding.BigEndianUnicode.GetString(r.Data, storage + off, len)
                : Encoding.Latin1.GetString(r.Data, storage + off, len);
            val = val.Trim();
            if (plat == 3) return val;                  // prefer Windows record
            fallback ??= val;
        }
        return fallback;
    }

    /// <summary>Big-endian byte reader over a font blob.</summary>
    private readonly struct BE
    {
        public readonly byte[] Data;
        public BE(byte[] data) => Data = data;
        public ushort U16(int o) => (ushort)((Data[o] << 8) | Data[o + 1]);
        public short S16(int o) => (short)U16(o);
        public uint U32(int o) => (uint)((Data[o] << 24) | (Data[o + 1] << 16) | (Data[o + 2] << 8) | Data[o + 3]);
    }
}
