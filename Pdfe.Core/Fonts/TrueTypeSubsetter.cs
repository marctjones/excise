namespace Pdfe.Core.Fonts;

/// <summary>
/// Produces a subset of a TrueType-outline font that keeps only the glyph
/// outlines actually used, while preserving glyph ids ("retain-gid" subsetting).
/// Because GIDs are unchanged, the font still works with Identity-H encoding and
/// an Identity CIDToGIDMap — no cmap/encoding rewrite needed. The big <c>glyf</c>
/// table (usually the bulk of a font) shrinks to the used outlines; other tables
/// are copied verbatim.
///
/// <para>Composite glyphs pull in their component glyphs automatically.</para>
/// </summary>
internal static class TrueTypeSubsetter
{
    /// <summary>
    /// Return a new sfnt containing only <paramref name="usedGids"/> (plus glyph 0
    /// and any composite components). Falls back to the original bytes if the font
    /// has no <c>glyf</c>/<c>loca</c> (e.g. CFF) or anything looks off.
    /// </summary>
    public static byte[] Subset(byte[] data, IReadOnlySet<int> usedGids)
    {
        try
        {
            return SubsetCore(data, usedGids);
        }
        catch
        {
            return data; // never break embedding because subsetting failed
        }
    }

    private static byte[] SubsetCore(byte[] data, IReadOnlySet<int> usedGids)
    {
        var tables = ReadTableDirectory(data, out _);
        if (!tables.ContainsKey("glyf") || !tables.ContainsKey("loca") ||
            !tables.ContainsKey("head") || !tables.ContainsKey("maxp"))
            return data;

        var head = tables["head"];
        short indexToLocFormat = S16(data, head.off + 50);
        int numGlyphs = U16(data, tables["maxp"].off + 4);

        // Read the original loca table → glyph data offsets within glyf.
        var loca = tables["loca"];
        int[] locaOffsets = new int[numGlyphs + 1];
        for (int i = 0; i <= numGlyphs; i++)
            locaOffsets[i] = indexToLocFormat == 0 ? U16(data, loca.off + i * 2) * 2
                                                   : (int)U32(data, loca.off + i * 4);

        int glyfBase = tables["glyf"].off;

        // Closure: include used gids + glyph 0 + every component of a composite.
        var keep = new HashSet<int> { 0 };
        var queue = new Queue<int>();
        foreach (var g in usedGids) if (g >= 0 && g < numGlyphs && keep.Add(g)) queue.Enqueue(g);
        if (keep.Add(0)) queue.Enqueue(0);
        while (queue.Count > 0)
        {
            int g = queue.Dequeue();
            int start = locaOffsets[g], end = locaOffsets[g + 1];
            if (end <= start) continue;                       // empty glyph
            foreach (int comp in CompositeComponents(data, glyfBase + start, end - start))
                if (comp >= 0 && comp < numGlyphs && keep.Add(comp)) queue.Enqueue(comp);
        }

        // Build the new glyf (kept outlines, 2-byte aligned) + long-format loca.
        using var glyf = new MemoryStream();
        var newLoca = new int[numGlyphs + 1];
        for (int g = 0; g < numGlyphs; g++)
        {
            newLoca[g] = (int)glyf.Length;
            if (keep.Contains(g))
            {
                int start = locaOffsets[g], end = locaOffsets[g + 1];
                int len = end - start;
                if (len > 0) glyf.Write(data, glyfBase + start, len);
                while (glyf.Length % 2 != 0) glyf.WriteByte(0);   // pad to even
            }
        }
        newLoca[numGlyphs] = (int)glyf.Length;
        byte[] newGlyf = glyf.ToArray();

        byte[] newLocaBytes = new byte[(numGlyphs + 1) * 4];     // long format
        for (int i = 0; i <= numGlyphs; i++) WriteU32(newLocaBytes, i * 4, (uint)newLoca[i]);

        // Assemble a fresh sfnt with only the tables a FontFile2 CID font needs.
        // cmap/GSUB/GPOS/GDEF/kern/name/post/OS2 are unnecessary under Identity-H
        // + Identity CIDToGIDMap (the PDF addresses glyphs by GID directly), so
        // dropping them shrinks the subset dramatically.
        var keepTables = new[] { "head", "hhea", "hmtx", "maxp", "cvt ", "fpgm", "prep", "glyf", "loca" };
        var outTables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var tag in keepTables)
        {
            if (tag is "glyf" or "loca") continue;            // substituted below
            if (!tables.TryGetValue(tag, out var t)) continue; // optional (cvt/fpgm/prep)
            byte[] body = new byte[t.len];
            Array.Copy(data, t.off, body, 0, t.len);
            outTables[tag] = body;
        }
        outTables["glyf"] = newGlyf;
        outTables["loca"] = newLocaBytes;
        // head.indexToLocFormat = 1 (long). head is at outTables["head"], offset 50.
        WriteS16(outTables["head"], 50, 1);
        // Zero head.checkSumAdjustment (offset 8) before computing the file checksum.
        WriteU32(outTables["head"], 8, 0);

        return BuildSfnt(outTables);
    }

    private static IEnumerable<int> CompositeComponents(byte[] data, int glyphOff, int glyphLen)
    {
        short numberOfContours = S16(data, glyphOff);
        if (numberOfContours >= 0) yield break;                 // simple glyph

        int p = glyphOff + 10;                                  // skip numContours + bbox(8)
        const int ARG_1_AND_2_ARE_WORDS = 0x0001;
        const int WE_HAVE_A_SCALE = 0x0008;
        const int MORE_COMPONENTS = 0x0020;
        const int WE_HAVE_AN_X_AND_Y_SCALE = 0x0040;
        const int WE_HAVE_A_TWO_BY_TWO = 0x0080;
        while (true)
        {
            if (p + 4 > glyphOff + glyphLen) yield break;
            int flags = U16(data, p);
            int glyphIndex = U16(data, p + 2);
            p += 4;
            yield return glyphIndex;
            p += (flags & ARG_1_AND_2_ARE_WORDS) != 0 ? 4 : 2;
            if ((flags & WE_HAVE_A_SCALE) != 0) p += 2;
            else if ((flags & WE_HAVE_AN_X_AND_Y_SCALE) != 0) p += 4;
            else if ((flags & WE_HAVE_A_TWO_BY_TWO) != 0) p += 8;
            if ((flags & MORE_COMPONENTS) == 0) yield break;
        }
    }

    private static byte[] BuildSfnt(SortedDictionary<string, byte[]> tables)
    {
        int n = tables.Count;
        int entrySelector = (int)Math.Floor(Math.Log2(n));
        int searchRange = (1 << entrySelector) * 16;
        int rangeShift = n * 16 - searchRange;

        int dirSize = 12 + n * 16;
        int offset = dirSize;
        var layout = new List<(string tag, int off, int len, byte[] body)>();
        foreach (var (tag, body) in tables)
        {
            layout.Add((tag, offset, body.Length, body));
            offset += (body.Length + 3) & ~3;                   // 4-byte aligned
        }
        int total = offset;
        byte[] outv = new byte[total];

        WriteU32(outv, 0, 0x00010000);                          // sfnt version
        WriteU16(outv, 4, (ushort)n);
        WriteU16(outv, 6, (ushort)searchRange);
        WriteU16(outv, 8, (ushort)entrySelector);
        WriteU16(outv, 10, (ushort)rangeShift);

        int dir = 12;
        foreach (var (tag, off, len, body) in layout)
        {
            for (int i = 0; i < 4; i++) outv[dir + i] = (byte)tag[i];
            WriteU32(outv, dir + 4, TableChecksum(body));
            WriteU32(outv, dir + 8, (uint)off);
            WriteU32(outv, dir + 12, (uint)len);
            Array.Copy(body, 0, outv, off, body.Length);
            dir += 16;
        }

        // head.checkSumAdjustment = 0xB1B0AFBA - checksum(whole file).
        if (TryFindTable(layout, "head", out int headOff))
        {
            uint fileChecksum = TableChecksum(outv);
            WriteU32(outv, headOff + 8, unchecked(0xB1B0AFBA - fileChecksum));
        }
        return outv;
    }

    private static bool TryFindTable(List<(string tag, int off, int len, byte[] body)> layout, string tag, out int off)
    {
        foreach (var t in layout) if (t.tag == tag) { off = t.off; return true; }
        off = 0; return false;
    }

    private static uint TableChecksum(byte[] data)
    {
        uint sum = 0;
        int n = data.Length;
        for (int i = 0; i < n; i += 4)
        {
            uint w = (uint)(data[i] << 24);
            if (i + 1 < n) w |= (uint)(data[i + 1] << 16);
            if (i + 2 < n) w |= (uint)(data[i + 2] << 8);
            if (i + 3 < n) w |= data[i + 3];
            sum = unchecked(sum + w);
        }
        return sum;
    }

    private static Dictionary<string, (int off, int len)> ReadTableDirectory(byte[] data, out uint sfnt)
    {
        sfnt = U32(data, 0);
        ushort numTables = U16(data, 4);
        var tables = new Dictionary<string, (int, int)>();
        int p = 12;
        for (int i = 0; i < numTables; i++, p += 16)
        {
            string tag = System.Text.Encoding.ASCII.GetString(data, p, 4);
            tables[tag] = ((int)U32(data, p + 8), (int)U32(data, p + 12));
        }
        return tables;
    }

    private static ushort U16(byte[] d, int o) => (ushort)((d[o] << 8) | d[o + 1]);
    private static short S16(byte[] d, int o) => (short)U16(d, o);
    private static uint U32(byte[] d, int o) => (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);
    private static void WriteU16(byte[] d, int o, ushort v) { d[o] = (byte)(v >> 8); d[o + 1] = (byte)v; }
    private static void WriteS16(byte[] d, int o, short v) => WriteU16(d, o, (ushort)v);
    private static void WriteU32(byte[] d, int o, uint v) { d[o] = (byte)(v >> 24); d[o + 1] = (byte)(v >> 16); d[o + 2] = (byte)(v >> 8); d[o + 3] = (byte)v; }
}
