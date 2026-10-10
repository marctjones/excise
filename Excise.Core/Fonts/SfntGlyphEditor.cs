using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Excise.Core.Fonts;

/// <summary>
/// #1920: empty some glyphs of an embedded sfnt program (TrueType <c>glyf</c>,
/// or OpenType <c>CFF </c> through <see cref="CffGlyphEditor"/>) and remove what
/// the font itself says about them, while every byte describing a kept glyph
/// stays as it was. Glyph ids never change.
/// </summary>
/// <remarks>
/// <para>What is edited: the outline (its <c>glyf</c> bytes leave and its
/// <c>loca</c> span becomes empty, or its charstring becomes the font's empty
/// one); the <c>cmap</c> entries that select it; its <c>post</c> 2.0 name; its
/// <c>hmtx</c>/<c>vmtx</c> metrics and <c>VORG</c> record; and the Apple
/// <c>Zapf</c> glyph-info table (it records each glyph's Unicode value) is
/// dropped whole. A side table is edited only when it tracks what the document
/// drew (most of its entries are drawn glyphs): in a table that describes every
/// glyph, the removed glyph is one among many, and emptying its entry would be
/// the one hole that names it.</para>
/// <para>Refused: a table this editor does not know (it may describe glyphs),
/// colour or bitmap glyph tables, variable fonts, a font collection, a
/// <c>loca</c> that is not monotonic, a cmap subtable format other than 0, 4,
/// 6, 12 and 14 (or a 14 that names an emptied glyph).</para>
/// </remarks>
internal static class SfntGlyphEditor
{
    /// <summary>Tables kept byte for byte: they carry no per-glyph identity.</summary>
    private static readonly HashSet<string> Verbatim = new(StringComparer.Ordinal)
    {
        "head", "hhea", "maxp", "OS/2", "name", "cvt ", "fpgm", "prep", "gasp", "hdmx", "LTSH",
        "VDMX", "kern", "GSUB", "GPOS", "GDEF", "BASE", "JSTF", "MATH", "vhea", "DSIG", "PCLT",
    };

    /// <summary>The edited program and the gids actually emptied.</summary>
    internal sealed record Result(byte[] Data, IReadOnlySet<int> Blanked);

    /// <summary>
    /// Empty <paramref name="blank"/> in <paramref name="font"/>.
    /// <paramref name="drawnGids"/> are the glyphs the document drew before the
    /// redaction; <paramref name="drawnCids"/> the CIDs (a CID-keyed CFF's
    /// charset is checked against them). A glyph a kept composite glyph uses as
    /// a component is not emptied: it is visibly drawn.
    /// </summary>
    public static Result? BlankGlyphs(byte[] font, IReadOnlySet<int> blank, IReadOnlySet<int> drawnGids,
        IReadOnlySet<int> drawnCids, out string? refusal)
    {
        try
        {
            return BlankCore(font, blank, drawnGids, drawnCids, out refusal);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidOperationException or OverflowException)
        {
            refusal = "its font program could not be read (" + ex.Message + ")";
            return null;
        }
    }

    private static Result? BlankCore(byte[] font, IReadOnlySet<int> blank, IReadOnlySet<int> drawnGids,
        IReadOnlySet<int> drawnCids, out string? refusal)
    {
        refusal = null;
        var version = U32(font, 0);
        var isCff = version == 0x4F54544F;               // 'OTTO'
        if (!isCff && version != 0x00010000 && version != 0x74727565)  // 'true'
        {
            refusal = "its font program is not a single TrueType or OpenType font";
            return null;
        }

        var tables = ReadTables(font);
        foreach (var tag in tables.Keys)
        {
            if (Verbatim.Contains(tag) || tag is "cmap" or "post" or "hmtx" or "vmtx" or "VORG" or "Zapf") continue;
            if (!isCff && tag is "glyf" or "loca") continue;
            if (isCff && tag == "CFF ") continue;
            refusal = $"its font program has a '{tag.TrimEnd()}' table the editor does not know";
            return null;
        }
        if (!tables.ContainsKey("maxp") || (!isCff && (!tables.ContainsKey("glyf") || !tables.ContainsKey("loca") || !tables.ContainsKey("head"))) ||
            (isCff && !tables.ContainsKey("CFF ")))
        {
            refusal = "its font program lacks the tables that hold its outlines";
            return null;
        }

        var numGlyphs = U16(font, tables["maxp"].Offset + 4);
        var output = tables.ToDictionary(kv => kv.Key, kv => Slice(font, kv.Value), StringComparer.Ordinal);
        IReadOnlySet<int> blanked;

        if (isCff)
        {
            var cff = output["CFF "];
            var targets = blank.Where(g => g > 0 && g < numGlyphs).ToHashSet();
            var edited = CffGlyphEditor.BlankGlyphs(cff, targets, drawnCids, out refusal);
            if (edited == null) return null;
            output["CFF "] = edited;
            blanked = targets;
        }
        else
        {
            var glyf = BlankGlyf(font, tables, numGlyphs, blank, out blanked, out var newGlyf, out var newLoca, out refusal);
            if (!glyf) return null;
            output["glyf"] = newGlyf!;
            output["loca"] = newLoca!;
        }

        if (blanked.Count == 0)
            return new Result(font, blanked);

        if (output.ContainsKey("cmap"))
        {
            var cmap = EditCmap(output["cmap"], blanked, drawnGids, out refusal);
            if (cmap == null) return null;
            output["cmap"] = cmap;
        }

        if (output.TryGetValue("post", out var post))
        {
            var edited = EditPost(post, blanked, drawnGids, out refusal);
            if (edited == null) return null;
            output["post"] = edited;
        }

        if (output.ContainsKey("hmtx") && output.ContainsKey("hhea"))
            output["hmtx"] = EditMetrics(output["hmtx"], U16(output["hhea"], 34), numGlyphs, blanked, drawnGids);
        if (output.ContainsKey("vmtx") && output.ContainsKey("vhea"))
            output["vmtx"] = EditMetrics(output["vmtx"], U16(output["vhea"], 34), numGlyphs, blanked, drawnGids);
        if (output.TryGetValue("VORG", out var vorg))
            output["VORG"] = EditVorg(vorg, blanked);
        output.Remove("Zapf");

        var rebuilt = BuildSfnt(version, output);

        // The result must read back: the same tables, and every kept outline unchanged.
        var check = ReadTables(rebuilt);
        if (!check.Keys.ToHashSet().SetEquals(output.Keys) ||
            output.Any(kv => !rebuilt.AsSpan(check[kv.Key].Offset, check[kv.Key].Length).SequenceEqual(kv.Value) && kv.Key != "head"))
        {
            refusal = "its edited font program did not read back";
            return null;
        }
        if (!isCff && !KeptOutlinesUnchanged(font, rebuilt, numGlyphs, blanked))
        {
            refusal = "its edited font program did not read back unchanged for the kept glyphs";
            return null;
        }
        return new Result(rebuilt, blanked);
    }

    /// <summary>
    /// The glyphs that have an outline (a non-empty <c>glyf</c> span, or a CFF
    /// charstring longer than an empty one), or null when the program cannot be read.
    /// </summary>
    public static HashSet<int>? OutlineGids(byte[] font)
    {
        try
        {
            var version = U32(font, 0);
            var tables = ReadTables(font);
            if (version == 0x4F54544F)
                return tables.TryGetValue("CFF ", out var cff) ? CffGlyphEditor.OutlineGids(Slice(font, cff)) : null;
            if (!tables.ContainsKey("glyf") || !tables.ContainsKey("loca") || !tables.ContainsKey("maxp") || !tables.ContainsKey("head"))
                return null;
            var numGlyphs = U16(font, tables["maxp"].Offset + 4);
            var loca = ReadLoca(font, tables, numGlyphs, out _);
            var set = new HashSet<int>();
            for (var g = 0; g < numGlyphs; g++)
                if (loca[g + 1] > loca[g]) set.Add(g);
            return set;
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidOperationException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>The CFF table of an OpenType program, or the bytes themselves for a bare CFF.</summary>
    public static byte[]? CffOf(byte[] font)
    {
        try
        {
            if (U32(font, 0) != 0x4F54544F) return font.Length > 0 && font[0] == 1 ? font : null;
            return ReadTables(font).TryGetValue("CFF ", out var cff) ? Slice(font, cff) : null;
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidOperationException or OverflowException)
        {
            return null;
        }
    }

    // ---- glyf / loca ---------------------------------------------------------

    private static int[] ReadLoca(byte[] font, Dictionary<string, (int Offset, int Length)> tables, int numGlyphs, out bool longFormat)
    {
        longFormat = S16(font, tables["head"].Offset + 50) == 1;
        var loca = tables["loca"].Offset;
        var offsets = new int[numGlyphs + 1];
        for (var i = 0; i <= numGlyphs; i++)
            offsets[i] = longFormat ? checked((int)U32(font, loca + 4 * i)) : U16(font, loca + 2 * i) * 2;
        return offsets;
    }

    private static bool BlankGlyf(byte[] font, Dictionary<string, (int Offset, int Length)> tables, int numGlyphs,
        IReadOnlySet<int> blank, out IReadOnlySet<int> blanked, out byte[]? newGlyf, out byte[]? newLoca, out string? refusal)
    {
        blanked = new HashSet<int>();
        newGlyf = newLoca = null;
        refusal = null;
        var loca = ReadLoca(font, tables, numGlyphs, out var longFormat);
        var glyfBase = tables["glyf"].Offset;
        var glyfLength = tables["glyf"].Length;
        for (var i = 0; i < numGlyphs; i++)
        {
            if (loca[i + 1] < loca[i] || loca[i + 1] > glyfLength)
            {
                refusal = "its 'loca' table is not monotonic";
                return false;
            }
        }

        // A glyph a kept composite uses as a component is drawn: keep it.
        var targets = blank.Where(g => g > 0 && g < numGlyphs).ToHashSet();
        var keep = new HashSet<int>();
        var queue = new Queue<int>();
        for (var g = 0; g < numGlyphs; g++)
            if (!targets.Contains(g)) { keep.Add(g); queue.Enqueue(g); }
        while (queue.Count > 0)
        {
            var g = queue.Dequeue();
            if (loca[g + 1] <= loca[g]) continue;
            foreach (var component in CompositeComponents(font, glyfBase + loca[g], loca[g + 1] - loca[g]))
                if (component >= 0 && component < numGlyphs && keep.Add(component))
                {
                    targets.Remove(component);
                    queue.Enqueue(component);
                }
        }
        blanked = targets;

        var glyf = new List<byte>(glyfLength);
        var newOffsets = new int[numGlyphs + 1];
        for (var g = 0; g < numGlyphs; g++)
        {
            newOffsets[g] = glyf.Count;
            if (targets.Contains(g)) continue;
            for (var k = loca[g]; k < loca[g + 1]; k++) glyf.Add(font[glyfBase + k]);
        }
        newOffsets[numGlyphs] = glyf.Count;
        newGlyf = glyf.ToArray();

        newLoca = new byte[(numGlyphs + 1) * (longFormat ? 4 : 2)];
        for (var i = 0; i <= numGlyphs; i++)
        {
            if (longFormat) W32(newLoca, 4 * i, (uint)newOffsets[i]);
            else
            {
                if ((newOffsets[i] & 1) != 0)
                {
                    refusal = "its short 'loca' table holds an odd glyph offset";
                    return false;
                }
                W16(newLoca, 2 * i, (ushort)(newOffsets[i] / 2));
            }
        }
        return true;
    }

    private static bool KeptOutlinesUnchanged(byte[] before, byte[] after, int numGlyphs, IReadOnlySet<int> blanked)
    {
        var t1 = ReadTables(before);
        var t2 = ReadTables(after);
        var l1 = ReadLoca(before, t1, numGlyphs, out _);
        var l2 = ReadLoca(after, t2, numGlyphs, out _);
        for (var g = 0; g < numGlyphs; g++)
        {
            var a = before.AsSpan(t1["glyf"].Offset + l1[g], l1[g + 1] - l1[g]);
            var b = after.AsSpan(t2["glyf"].Offset + l2[g], l2[g + 1] - l2[g]);
            if (blanked.Contains(g) ? b.Length != 0 : !a.SequenceEqual(b)) return false;
        }
        return true;
    }

    internal static IEnumerable<int> CompositeComponents(byte[] data, int glyphOff, int glyphLen)
    {
        if (glyphLen < 10 || S16(data, glyphOff) >= 0) yield break;
        var p = glyphOff + 10;
        while (p + 4 <= glyphOff + glyphLen)
        {
            int flags = U16(data, p);
            yield return U16(data, p + 2);
            p += 4;
            p += (flags & 0x0001) != 0 ? 4 : 2;
            if ((flags & 0x0008) != 0) p += 2;
            else if ((flags & 0x0040) != 0) p += 4;
            else if ((flags & 0x0080) != 0) p += 8;
            if ((flags & 0x0020) == 0) yield break;
        }
    }

    // ---- cmap ----------------------------------------------------------------

    /// <summary>True when most of <paramref name="entries"/> are glyphs the document drew.</summary>
    internal static bool TracksUsage(IEnumerable<int> entries, IReadOnlySet<int> drawn)
    {
        int inDrawn = 0, other = 0;
        foreach (var e in entries)
        {
            if (drawn.Contains(e)) inDrawn++;
            else other++;
        }
        return inDrawn + other > 0 && other <= inDrawn;
    }

    private static byte[]? EditCmap(byte[] cmap, IReadOnlySet<int> blanked, IReadOnlySet<int> drawnGids, out string? refusal)
    {
        refusal = null;
        int numTables = U16(cmap, 2);
        var records = new List<(int Platform, int Encoding, int Offset)>();
        for (var i = 0; i < numTables; i++)
            records.Add((U16(cmap, 4 + 8 * i), U16(cmap, 6 + 8 * i), checked((int)U32(cmap, 8 + 8 * i))));

        var subtables = new Dictionary<int, byte[]>();
        var maps = new Dictionary<int, Dictionary<int, int>>();
        foreach (var offset in records.Select(r => r.Offset).Distinct())
        {
            int format = U16(cmap, offset);
            int length = format switch
            {
                0 or 2 or 4 or 6 => U16(cmap, offset + 2),
                8 or 10 or 12 or 13 => checked((int)U32(cmap, offset + 4)),
                14 => checked((int)U32(cmap, offset + 2)),
                _ => -1,
            };
            if (length <= 0 || offset + length > cmap.Length)
            {
                refusal = $"its 'cmap' subtable format {format} could not be read";
                return null;
            }
            subtables[offset] = cmap.AsSpan(offset, length).ToArray();
            if (format is 0 or 4 or 6 or 12)
                maps[offset] = TrueTypeFontFile.ReadCmapSubtable(cmap, offset);
            else if (format == 14)
            {
                if (Format14NamesAny(cmap, offset, blanked))
                {
                    refusal = "its 'cmap' variation-sequence subtable selects a removed glyph";
                    return null;
                }
            }
            else
            {
                refusal = $"its 'cmap' has a format {format} subtable the editor does not rewrite";
                return null;
            }
        }

        // Leave a cmap that maps glyphs the document never drew: a removed glyph hides among them.
        if (!TracksUsage(maps.Values.SelectMany(m => m.Values).Distinct(), drawnGids))
            return cmap;

        var changed = false;
        foreach (var (offset, map) in maps)
        {
            var kept = map.Where(kv => !blanked.Contains(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value);
            if (kept.Count == map.Count) continue;
            var original = subtables[offset];
            byte[]? rebuilt = U16(original, 0) switch
            {
                0 => ZeroFormat0(original, map.Keys.Where(k => !kept.ContainsKey(k))),
                6 => ZeroFormat6(original, map.Keys.Where(k => !kept.ContainsKey(k))),
                4 => BuildFormat4(U16(original, 4), kept),
                12 => BuildFormat12(U32(original, 8), kept),
                _ => null,
            };
            if (rebuilt == null)
            {
                refusal = "its 'cmap' subtable could not be rewritten";
                return null;
            }
            var check = TrueTypeFontFile.ReadCmapSubtable(rebuilt, 0);
            if (check.Count != kept.Count || kept.Any(kv => !check.TryGetValue(kv.Key, out var g) || g != kv.Value))
            {
                refusal = "its 'cmap' subtable did not read back unchanged for the kept glyphs";
                return null;
            }
            subtables[offset] = rebuilt;
            changed = true;
        }
        if (!changed) return cmap;

        // Reassemble: header, records in their order, subtables (shared offsets stay shared).
        var output = new List<byte>();
        var header = new byte[4 + 8 * records.Count];
        W16(header, 0, U16(cmap, 0));
        W16(header, 2, (ushort)records.Count);
        output.AddRange(header);
        var newOffsets = new Dictionary<int, int>();
        foreach (var offset in records.Select(r => r.Offset).Distinct().OrderBy(o => o))
        {
            newOffsets[offset] = output.Count;
            output.AddRange(subtables[offset]);
            while (output.Count % 4 != 0) output.Add(0);
        }
        var bytes = output.ToArray();
        for (var i = 0; i < records.Count; i++)
        {
            W16(bytes, 4 + 8 * i, (ushort)records[i].Platform);
            W16(bytes, 6 + 8 * i, (ushort)records[i].Encoding);
            W32(bytes, 8 + 8 * i, (uint)newOffsets[records[i].Offset]);
        }
        return bytes;
    }

    private static bool Format14NamesAny(byte[] cmap, int offset, IReadOnlySet<int> blanked)
    {
        var records = checked((int)U32(cmap, offset + 6));
        for (var i = 0; i < records; i++)
        {
            var rec = offset + 10 + 11 * i;
            var nonDefault = checked((int)U32(cmap, rec + 7));
            if (nonDefault == 0) continue;
            var table = offset + nonDefault;
            var mappings = checked((int)U32(cmap, table));
            for (var k = 0; k < mappings; k++)
                if (blanked.Contains(U16(cmap, table + 4 + 5 * k + 3))) return true;
        }
        return false;
    }

    private static byte[] ZeroFormat0(byte[] subtable, IEnumerable<int> codes)
    {
        var copy = (byte[])subtable.Clone();
        foreach (var c in codes) if (c is >= 0 and < 256) copy[6 + c] = 0;
        return copy;
    }

    private static byte[] ZeroFormat6(byte[] subtable, IEnumerable<int> codes)
    {
        var copy = (byte[])subtable.Clone();
        int first = U16(copy, 6), count = U16(copy, 8);
        foreach (var c in codes)
            if (c >= first && c < first + count) W16(copy, 10 + 2 * (c - first), 0);
        return copy;
    }

    private static byte[]? BuildFormat4(int language, Dictionary<int, int> map)
    {
        var codes = map.Keys.Where(c => c is >= 0 and < 0xFFFF).OrderBy(c => c).ToList();
        if (codes.Count != map.Count) return null;
        var segments = new List<(int Start, int End, bool Delta)>();
        var i = 0;
        while (i < codes.Count)
        {
            var j = i;
            while (j + 1 < codes.Count && codes[j + 1] == codes[j] + 1) j++;
            // One run of consecutive codes: a delta segment when its gids run too.
            var delta = Enumerable.Range(i, j - i + 1).All(k => map[codes[k]] - codes[k] == map[codes[i]] - codes[i]);
            segments.Add((codes[i], codes[j], delta));
            i = j + 1;
        }
        segments.Add((0xFFFF, 0xFFFF, true));

        var segCount = segments.Count;
        var glyphIds = new List<int>();
        var rangeOffsets = new int[segCount];
        for (var s = 0; s < segCount; s++)
        {
            var (start, end, delta) = segments[s];
            if (delta) continue;
            rangeOffsets[s] = 2 * (segCount - s) + 2 * glyphIds.Count;
            for (var c = start; c <= end; c++) glyphIds.Add(map[c]);
        }
        var length = 16 + 8 * segCount + 2 * glyphIds.Count;
        if (length > 0xFFFF) return null;

        var o = new byte[length];
        W16(o, 0, 4);
        W16(o, 2, (ushort)length);
        W16(o, 4, (ushort)language);
        W16(o, 6, (ushort)(segCount * 2));
        var entrySelector = (int)Math.Floor(Math.Log2(segCount));
        var searchRange = 2 * (1 << entrySelector);
        W16(o, 8, (ushort)searchRange);
        W16(o, 10, (ushort)entrySelector);
        W16(o, 12, (ushort)(2 * segCount - searchRange));
        int ends = 14, starts = ends + 2 * segCount + 2, deltas = starts + 2 * segCount, ranges = deltas + 2 * segCount;
        for (var s = 0; s < segCount; s++)
        {
            var (start, end, delta) = segments[s];
            W16(o, ends + 2 * s, (ushort)end);
            W16(o, starts + 2 * s, (ushort)start);
            var idDelta = start == 0xFFFF ? 1 : delta ? (map[start] - start) & 0xFFFF : 0;
            W16(o, deltas + 2 * s, (ushort)idDelta);
            W16(o, ranges + 2 * s, (ushort)rangeOffsets[s]);
        }
        var g = ranges + 2 * segCount;
        foreach (var id in glyphIds) { W16(o, g, (ushort)id); g += 2; }
        return o;
    }

    private static byte[] BuildFormat12(uint language, Dictionary<int, int> map)
    {
        var codes = map.Keys.OrderBy(c => c).ToList();
        var groups = new List<(int Start, int End, int Gid)>();
        var i = 0;
        while (i < codes.Count)
        {
            var j = i;
            while (j + 1 < codes.Count && codes[j + 1] == codes[j] + 1 && map[codes[j + 1]] == map[codes[j]] + 1) j++;
            groups.Add((codes[i], codes[j], map[codes[i]]));
            i = j + 1;
        }
        var o = new byte[16 + 12 * groups.Count];
        W16(o, 0, 12);
        W32(o, 4, (uint)o.Length);
        W32(o, 8, language);
        W32(o, 12, (uint)groups.Count);
        for (var k = 0; k < groups.Count; k++)
        {
            W32(o, 16 + 12 * k, (uint)groups[k].Start);
            W32(o, 20 + 12 * k, (uint)groups[k].End);
            W32(o, 24 + 12 * k, (uint)groups[k].Gid);
        }
        return o;
    }

    // ---- post, metrics, VORG -------------------------------------------------

    private static byte[]? EditPost(byte[] post, IReadOnlySet<int> blanked, IReadOnlySet<int> drawnGids, out string? refusal)
    {
        refusal = null;
        var format = U32(post, 0);
        if (format is 0x00010000 or 0x00030000) return post;
        if (format != 0x00020000)
        {
            refusal = $"its 'post' table is format {format >> 16}.{(format >> 12) & 0xF}, which the editor does not rewrite";
            return null;
        }

        int numGlyphs = U16(post, 32);
        var index = new int[numGlyphs];
        for (var g = 0; g < numGlyphs; g++) index[g] = U16(post, 34 + 2 * g);
        var names = new List<byte[]>();
        for (var p = 34 + 2 * numGlyphs; p < post.Length;)
        {
            var len = post[p];
            if (p + 1 + len > post.Length) break;
            names.Add(post.AsSpan(p, 1 + len).ToArray());
            p += 1 + len;
        }

        var named = Enumerable.Range(0, numGlyphs).Where(g => index[g] != 0);
        if (!TracksUsage(named, drawnGids) || !blanked.Any(g => g < numGlyphs && index[g] != 0))
            return post;

        foreach (var g in blanked) if (g < numGlyphs) index[g] = 0;
        var used = index.Where(i => i >= 258).Distinct().OrderBy(i => i).ToList();
        var remap = used.Select((old, k) => (old, k)).ToDictionary(x => x.old, x => 258 + x.k);
        var output = new List<byte>(post.AsSpan(0, 34 + 2 * numGlyphs).ToArray());
        for (var g = 0; g < numGlyphs; g++)
        {
            var v = index[g] >= 258 ? remap[index[g]] : index[g];
            output[34 + 2 * g] = (byte)(v >> 8);
            output[35 + 2 * g] = (byte)v;
        }
        foreach (var old in used)
        {
            if (old - 258 >= names.Count)
            {
                refusal = "its 'post' table names a glyph past its string list";
                return null;
            }
            output.AddRange(names[old - 258]);
        }
        return output.ToArray();
    }

    /// <summary>
    /// Zero the advance and side bearing of each emptied glyph when the table
    /// already zeroes glyphs the document never drew (or has none), so the
    /// emptied glyph looks like them. The last long metric is never changed
    /// when glyphs after it share its advance.
    /// </summary>
    private static byte[] EditMetrics(byte[] table, int longMetrics, int numGlyphs, IReadOnlySet<int> blanked, IReadOnlySet<int> drawnGids)
    {
        if (longMetrics <= 0 || table.Length < 4 * longMetrics) return table;
        int Advance(int g) => U16(table, 4 * Math.Min(g, longMetrics - 1));
        var unused = Enumerable.Range(1, Math.Max(0, numGlyphs - 1)).Where(g => !drawnGids.Contains(g) && !blanked.Contains(g)).ToList();
        if (unused.Count > 0 && unused.Count(g => Advance(g) == 0) * 2 <= unused.Count)
            return table;

        var copy = (byte[])table.Clone();
        foreach (var g in blanked)
        {
            if (g <= 0 || g >= numGlyphs) continue;
            if (g < longMetrics)
            {
                if (g < longMetrics - 1 || longMetrics == numGlyphs) W16(copy, 4 * g, 0);
                W16(copy, 4 * g + 2, 0);
            }
            else
            {
                var at = 4 * longMetrics + 2 * (g - longMetrics);
                if (at + 2 <= copy.Length) W16(copy, at, 0);
            }
        }
        return copy;
    }

    private static byte[] EditVorg(byte[] vorg, IReadOnlySet<int> blanked)
    {
        int count = U16(vorg, 6);
        var kept = new List<(int Gid, int Y)>();
        for (var i = 0; i < count && 8 + 4 * i + 4 <= vorg.Length; i++)
        {
            var gid = U16(vorg, 8 + 4 * i);
            if (!blanked.Contains(gid)) kept.Add((gid, U16(vorg, 10 + 4 * i)));
        }
        if (kept.Count == count) return vorg;
        var o = new byte[8 + 4 * kept.Count];
        Array.Copy(vorg, o, 6);
        W16(o, 6, (ushort)kept.Count);
        for (var i = 0; i < kept.Count; i++)
        {
            W16(o, 8 + 4 * i, (ushort)kept[i].Gid);
            W16(o, 10 + 4 * i, (ushort)kept[i].Y);
        }
        return o;
    }

    // ---- sfnt container ------------------------------------------------------

    internal static Dictionary<string, (int Offset, int Length)> ReadTables(byte[] font)
    {
        int n = U16(font, 4);
        var tables = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        for (var i = 0; i < n; i++)
        {
            var p = 12 + 16 * i;
            var tag = Encoding.ASCII.GetString(font, p, 4);
            var offset = checked((int)U32(font, p + 8));
            var length = checked((int)U32(font, p + 12));
            if (offset < 0 || length < 0 || offset + length > font.Length)
                throw new InvalidOperationException($"sfnt table '{tag}' out of range");
            tables[tag] = (offset, length);
        }
        return tables;
    }

    private static byte[] Slice(byte[] font, (int Offset, int Length) t) => font.AsSpan(t.Offset, t.Length).ToArray();

    private static byte[] BuildSfnt(uint version, Dictionary<string, byte[]> tables)
    {
        var tags = tables.Keys.OrderBy(t => t, StringComparer.Ordinal).ToList();
        var n = tags.Count;
        var entrySelector = (int)Math.Floor(Math.Log2(n));
        var searchRange = (1 << entrySelector) * 16;
        var offset = 12 + 16 * n;
        var layout = new List<(string Tag, int Offset)>();
        foreach (var tag in tags)
        {
            layout.Add((tag, offset));
            offset += (tables[tag].Length + 3) & ~3;
        }
        var o = new byte[offset];
        W32(o, 0, version);
        W16(o, 4, (ushort)n);
        W16(o, 6, (ushort)searchRange);
        W16(o, 8, (ushort)entrySelector);
        W16(o, 10, (ushort)(n * 16 - searchRange));
        var headAt = -1;
        for (var i = 0; i < n; i++)
        {
            var (tag, at) = layout[i];
            var body = tables[tag];
            if (tag == "head" && body.Length >= 12)
            {
                body = (byte[])body.Clone();
                W32(body, 8, 0);
                headAt = at;
            }
            var rec = 12 + 16 * i;
            for (var k = 0; k < 4; k++) o[rec + k] = (byte)tag[k];
            W32(o, rec + 4, Checksum(body));
            W32(o, rec + 8, (uint)at);
            W32(o, rec + 12, (uint)body.Length);
            Array.Copy(body, 0, o, at, body.Length);
        }
        if (headAt >= 0) W32(o, headAt + 8, unchecked(0xB1B0AFBA - Checksum(o)));
        return o;
    }

    private static uint Checksum(byte[] data)
    {
        uint sum = 0;
        for (var i = 0; i < data.Length; i += 4)
        {
            uint w = (uint)data[i] << 24;
            if (i + 1 < data.Length) w |= (uint)data[i + 1] << 16;
            if (i + 2 < data.Length) w |= (uint)data[i + 2] << 8;
            if (i + 3 < data.Length) w |= data[i + 3];
            sum = unchecked(sum + w);
        }
        return sum;
    }

    internal static int U16(byte[] d, int o) => (d[o] << 8) | d[o + 1];
    private static short S16(byte[] d, int o) => (short)U16(d, o);
    internal static uint U32(byte[] d, int o) => (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);
    private static void W16(byte[] d, int o, int v) { d[o] = (byte)(v >> 8); d[o + 1] = (byte)v; }
    private static void W32(byte[] d, int o, uint v) { d[o] = (byte)(v >> 24); d[o + 1] = (byte)(v >> 16); d[o + 2] = (byte)(v >> 8); d[o + 3] = (byte)v; }
}
