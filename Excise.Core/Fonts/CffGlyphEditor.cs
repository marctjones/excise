using System;
using System.Collections.Generic;
using System.Linq;

namespace Excise.Core.Fonts;

/// <summary>
/// #1920: empty some glyphs of a CID-keyed CFF program (a <c>/FontFile3</c>
/// <c>/CIDFontType0C</c>, or the <c>CFF </c> table of an OpenType one) and keep
/// every other byte that describes a kept glyph. Glyph ids do not change: the
/// CharStrings INDEX is rebuilt with each emptied glyph's charstring replaced by
/// the font's own empty charstring, and only the offsets of structures stored
/// after it move. Everything else (charset, FDSelect, FDArray, Private DICTs) is
/// copied byte for byte.
/// </summary>
/// <remarks>
/// Refused rather than guessed: a name-keyed CFF (its charset spells each
/// glyph's name), a font with subroutines (an outline fragment of an emptied
/// glyph can live in one), more than one font in the set, a charset that lists
/// only the CIDs the document draws (it would keep naming the removed ones), and
/// an offset that must move but is not encoded in five bytes.
/// </remarks>
internal static class CffGlyphEditor
{
    private const int OpCharset = 15, OpEncoding = 16, OpCharStrings = 17, OpPrivate = 18, OpSubrs = 19;
    private const int OpRos = 1230, OpFdArray = 1236, OpFdSelect = 1237;

    private sealed record DictOperand(int Position, int Length, int Value);

    private sealed class Dict
    {
        public Dictionary<int, List<DictOperand>> Ops { get; } = new();
    }

    private sealed class Parsed
    {
        public required byte[] Data;
        public required int TopDictStart;
        public required Dict Top;
        public required int GlobalSubrCount;
        public required List<(int Offset, int Length)> CharStrings;
        public required int CharStringsStart;
        public required int CharStringsEnd;
        public required List<(int Start, Dict Dict)> FdDicts;
        public int[]? GidToCid;
    }

    /// <summary>CID → GID through the charset of a CID-keyed CFF; null when it is not one or cannot be read.</summary>
    public static Dictionary<int, int>? CidToGid(byte[] cff)
    {
        try
        {
            var p = Parse(cff);
            if (p?.GidToCid == null) return null;
            var map = new Dictionary<int, int>();
            for (var gid = 0; gid < p.GidToCid.Length; gid++)
                map.TryAdd(p.GidToCid[gid], gid);
            return map;
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Glyphs whose charstring is longer than an empty one; null when unreadable.</summary>
    public static HashSet<int>? OutlineGids(byte[] cff)
    {
        try
        {
            var p = Parse(cff);
            if (p == null) return null;
            var set = new HashSet<int>();
            for (var g = 0; g < p.CharStrings.Count; g++)
                if (p.CharStrings[g].Length > 4) set.Add(g);
            return set;
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// The program with <paramref name="blank"/> glyphs emptied, or null and a
    /// <paramref name="refusal"/>. <paramref name="drawnCids"/> are the CIDs the
    /// document drew before the redaction: the charset is refused when it lists
    /// mostly those (it would still name the removed CIDs).
    /// </summary>
    public static byte[]? BlankGlyphs(byte[] cff, IReadOnlySet<int> blank, IReadOnlySet<int> drawnCids, out string? refusal)
    {
        try
        {
            return BlankCore(cff, blank, drawnCids, out refusal);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidOperationException)
        {
            refusal = "its CFF program could not be read (" + ex.Message + ")";
            return null;
        }
    }

    private static byte[]? BlankCore(byte[] cff, IReadOnlySet<int> blank, IReadOnlySet<int> drawnCids, out string? refusal)
    {
        refusal = null;
        var p = Parse(cff);
        if (p == null) { refusal = "its CFF program could not be read"; return null; }
        if (p.GidToCid == null) { refusal = "its CFF program is name-keyed (the charset names each glyph)"; return null; }
        if (p.CharStrings.Count < 2) return cff;
        if (p.GlobalSubrCount > 0 || HasLocalSubrs(p))
        {
            refusal = "its CFF program has subroutines, which can hold outline fragments of the removed glyphs";
            return null;
        }

        // The charset must not itself be a list of the drawn CIDs.
        var listed = new HashSet<int>(p.GidToCid[1..]);
        var undrawnListed = listed.Count(c => !drawnCids.Contains(c));
        if (listed.Count > 0 && undrawnListed * 2 < listed.Count)
        {
            refusal = "its CFF charset lists the drawn CIDs only, so it would still list the removed ones";
            return null;
        }

        var count = p.CharStrings.Count;
        var targets = blank.Where(g => g > 0 && g < count).ToHashSet();
        if (targets.Count == 0) return cff;

        var empty = EmptyCharString(p, targets, drawnCids);

        // Rebuild the CharStrings INDEX.
        var items = new List<byte[]>(count);
        for (var g = 0; g < count; g++)
        {
            var (off, len) = p.CharStrings[g];
            items.Add(targets.Contains(g) ? empty : p.Data.AsSpan(off, len).ToArray());
        }
        var newIndex = BuildIndex(items);
        var oldStart = p.CharStringsStart;
        var oldEnd = p.CharStringsEnd;
        var delta = newIndex.Length - (oldEnd - oldStart);

        var output = new byte[p.Data.Length + delta];
        Array.Copy(p.Data, 0, output, 0, oldStart);
        Array.Copy(newIndex, 0, output, oldStart, newIndex.Length);
        Array.Copy(p.Data, oldEnd, output, oldStart + newIndex.Length, p.Data.Length - oldEnd);

        if (delta != 0)
        {
            int Map(int pos) => pos >= oldEnd ? pos + delta : pos;
            var slots = new List<DictOperand>();
            foreach (var op in new[] { OpCharset, OpEncoding, OpFdArray, OpFdSelect })
                if (p.Top.Ops.TryGetValue(op, out var operands) && operands.Count >= 1)
                    slots.Add(operands[0]);
            if (p.Top.Ops.TryGetValue(OpPrivate, out var priv) && priv.Count >= 2) slots.Add(priv[1]);
            foreach (var (_, fd) in p.FdDicts)
                if (fd.Ops.TryGetValue(OpPrivate, out var fdPriv) && fdPriv.Count >= 2) slots.Add(fdPriv[1]);

            foreach (var slot in slots)
            {
                // Encoding (16) values 0 and 1 are predefined encodings, not offsets.
                if (slot.Value <= 1 || slot.Value < oldEnd) continue;
                if (slot.Length != 5)
                {
                    refusal = "a CFF offset after the CharStrings would move but is not encoded in five bytes";
                    return null;
                }
                var at = Map(slot.Position);
                var v = slot.Value + delta;
                output[at] = 29;
                output[at + 1] = (byte)(v >> 24);
                output[at + 2] = (byte)(v >> 16);
                output[at + 3] = (byte)(v >> 8);
                output[at + 4] = (byte)v;
            }
        }

        // Read the result back: every kept charstring and the charset unchanged.
        var check = Parse(output);
        if (check?.GidToCid == null || check.CharStrings.Count != count ||
            !check.GidToCid.AsSpan().SequenceEqual(p.GidToCid))
        {
            refusal = "its CFF program did not read back unchanged after the edit";
            return null;
        }
        for (var g = 0; g < count; g++)
        {
            var (o1, l1) = p.CharStrings[g];
            var (o2, l2) = check.CharStrings[g];
            var expected = targets.Contains(g) ? empty : p.Data.AsSpan(o1, l1);
            if (!check.Data.AsSpan(o2, l2).SequenceEqual(expected))
            {
                refusal = "its CFF program did not read back unchanged after the edit";
                return null;
            }
        }
        return output;
    }

    /// <summary>
    /// What an unused glyph looks like in this font, so an emptied one does not
    /// stand out: the most common charstring among glyphs the document never
    /// drew, when it is short; otherwise a bare <c>endchar</c>.
    /// </summary>
    private static byte[] EmptyCharString(Parsed p, HashSet<int> targets, IReadOnlySet<int> drawnCids)
    {
        var counts = new Dictionary<string, (int Count, byte[] Bytes)>();
        for (var g = 1; g < p.CharStrings.Count; g++)
        {
            if (targets.Contains(g) || drawnCids.Contains(p.GidToCid![g])) continue;
            var (off, len) = p.CharStrings[g];
            if (len > 4) continue;
            var bytes = p.Data.AsSpan(off, len).ToArray();
            if (bytes.Length == 0 || bytes[^1] != 14) continue;
            var key = Convert.ToHexString(bytes);
            counts[key] = counts.TryGetValue(key, out var c) ? (c.Count + 1, bytes) : (1, bytes);
        }
        return counts.Count == 0 ? new byte[] { 14 } : counts.Values.MaxBy(c => c.Count).Bytes;
    }

    private static bool HasLocalSubrs(Parsed p)
    {
        IEnumerable<(int Offset, Dict Dict)> privates()
        {
            if (p.Top.Ops.TryGetValue(OpPrivate, out var top) && top.Count >= 2)
                yield return (top[1].Value, ReadPrivate(p.Data, top[0].Value, top[1].Value));
            foreach (var (_, fd) in p.FdDicts)
                if (fd.Ops.TryGetValue(OpPrivate, out var priv) && priv.Count >= 2)
                    yield return (priv[1].Value, ReadPrivate(p.Data, priv[0].Value, priv[1].Value));
        }

        foreach (var (offset, priv) in privates())
        {
            // Subrs is an offset from the start of its Private DICT; an empty INDEX holds nothing.
            if (priv.Ops.TryGetValue(OpSubrs, out var subrs) && subrs.Count >= 1 &&
                ReadIndex(p.Data, offset + subrs[0].Value, out _).Count > 0)
                return true;
        }
        return false;
    }

    private static Dict ReadPrivate(byte[] data, int size, int offset)
    {
        if (offset < 0 || size < 0 || offset + size > data.Length)
            throw new InvalidOperationException("Private DICT out of range");
        return ReadDict(data, offset, offset + size);
    }

    private static Parsed? Parse(byte[] data)
    {
        if (data.Length < 4 || data[0] != 1) return null;
        var pos = (int)data[2];
        var names = ReadIndex(data, pos, out pos);
        var tops = ReadIndex(data, pos, out pos);
        if (names.Count != 1 || tops.Count != 1) return null;
        ReadIndex(data, pos, out pos); // String INDEX
        var gsubrs = ReadIndex(data, pos, out _);
        var (topStart, topLen) = tops[0];
        var top = ReadDict(data, topStart, topStart + topLen);
        if (!top.Ops.TryGetValue(OpCharStrings, out var cs) || cs.Count < 1) return null;
        var csStart = cs[0].Value;
        var charStrings = ReadIndex(data, csStart, out var csEnd);

        var fdDicts = new List<(int, Dict)>();
        if (top.Ops.TryGetValue(OpFdArray, out var fda) && fda.Count >= 1)
            foreach (var (o, l) in ReadIndex(data, fda[0].Value, out _))
                fdDicts.Add((o, ReadDict(data, o, o + l)));

        int[]? gidToCid = null;
        if (top.Ops.ContainsKey(OpRos))
        {
            var charsetOffset = top.Ops.TryGetValue(OpCharset, out var charset) && charset.Count >= 1 ? charset[0].Value : 0;
            gidToCid = ReadCharset(data, charsetOffset, charStrings.Count);
        }

        return new Parsed
        {
            Data = data,
            TopDictStart = topStart,
            Top = top,
            GlobalSubrCount = gsubrs.Count,
            CharStrings = charStrings,
            CharStringsStart = csStart,
            CharStringsEnd = csEnd,
            FdDicts = fdDicts,
            GidToCid = gidToCid,
        };
    }

    private static int[] ReadCharset(byte[] d, int offset, int glyphs)
    {
        var map = new int[glyphs];
        if (offset <= 2)
        {
            // Predefined charsets are for name-keyed fonts; a CID font needs its own.
            throw new InvalidOperationException("CID-keyed CFF without a charset");
        }
        var format = d[offset];
        var p = offset + 1;
        var gid = 1;
        switch (format)
        {
            case 0:
                for (; gid < glyphs; gid++, p += 2) map[gid] = (d[p] << 8) | d[p + 1];
                break;
            case 1:
            case 2:
                while (gid < glyphs)
                {
                    var first = (d[p] << 8) | d[p + 1];
                    var left = format == 1 ? d[p + 2] : (d[p + 2] << 8) | d[p + 3];
                    p += format == 1 ? 3 : 4;
                    for (var k = 0; k <= left && gid < glyphs; k++) map[gid++] = first + k;
                }
                break;
            default:
                throw new InvalidOperationException($"CFF charset format {format}");
        }
        return map;
    }

    private static List<(int Offset, int Length)> ReadIndex(byte[] d, int pos, out int end)
    {
        var count = (d[pos] << 8) | d[pos + 1];
        var items = new List<(int, int)>(count);
        if (count == 0) { end = pos + 2; return items; }
        int offSize = d[pos + 2];
        if (offSize is < 1 or > 4) throw new InvalidOperationException("CFF INDEX offSize");
        int Off(int i)
        {
            var v = 0;
            for (var k = 0; k < offSize; k++) v = (v << 8) | d[pos + 3 + i * offSize + k];
            return v;
        }
        var dataStart = pos + 3 + (count + 1) * offSize - 1;
        for (var i = 0; i < count; i++)
        {
            int a = Off(i), b = Off(i + 1);
            if (b < a || dataStart + b > d.Length) throw new InvalidOperationException("CFF INDEX offsets");
            items.Add((dataStart + a, b - a));
        }
        end = dataStart + Off(count);
        return items;
    }

    private static byte[] BuildIndex(List<byte[]> items)
    {
        long total = 1;
        foreach (var item in items) total += item.Length;
        var offSize = total <= 0xFF ? 1 : total <= 0xFFFF ? 2 : total <= 0xFFFFFF ? 3 : 4;
        var output = new byte[3 + (items.Count + 1) * offSize + (total - 1)];
        output[0] = (byte)(items.Count >> 8);
        output[1] = (byte)items.Count;
        output[2] = (byte)offSize;
        var off = 1;
        var dataPos = 3 + (items.Count + 1) * offSize;
        for (var i = 0; i <= items.Count; i++)
        {
            for (var k = 0; k < offSize; k++)
                output[3 + i * offSize + k] = (byte)(off >> (8 * (offSize - 1 - k)));
            if (i == items.Count) break;
            Array.Copy(items[i], 0, output, dataPos, items[i].Length);
            dataPos += items[i].Length;
            off += items[i].Length;
        }
        return output;
    }

    private static Dict ReadDict(byte[] d, int start, int end)
    {
        var dict = new Dict();
        var operands = new List<DictOperand>();
        var i = start;
        while (i < end)
        {
            int b = d[i];
            if (b <= 21)
            {
                var op = b == 12 ? 1200 + d[i + 1] : b;
                i += b == 12 ? 2 : 1;
                dict.Ops[op] = operands;
                operands = new List<DictOperand>();
            }
            else if (b == 28) { operands.Add(new DictOperand(i, 3, (short)((d[i + 1] << 8) | d[i + 2]))); i += 3; }
            else if (b == 29) { operands.Add(new DictOperand(i, 5, (d[i + 1] << 24) | (d[i + 2] << 16) | (d[i + 3] << 8) | d[i + 4])); i += 5; }
            else if (b == 30)
            {
                var s = i++;
                while (i < end && (d[i] & 0xF) != 0xF && (d[i] >> 4) != 0xF) i++;
                i++;
                operands.Add(new DictOperand(s, i - s, 0));
            }
            else if (b is >= 32 and <= 246) { operands.Add(new DictOperand(i, 1, b - 139)); i++; }
            else if (b is >= 247 and <= 250) { operands.Add(new DictOperand(i, 2, (b - 247) * 256 + d[i + 1] + 108)); i += 2; }
            else if (b is >= 251 and <= 254) { operands.Add(new DictOperand(i, 2, -(b - 251) * 256 - d[i + 1] - 108)); i += 2; }
            else throw new InvalidOperationException($"CFF DICT byte {b}");
        }
        return dict;
    }
}
