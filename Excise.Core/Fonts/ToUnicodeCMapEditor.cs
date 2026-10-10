using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Excise.Core.Text;
using static Excise.Core.Text.CMapTokenizer;

namespace Excise.Core.Fonts;

/// <summary>
/// #1920: rewrite a <c>/ToUnicode</c> CMap without some codes, leaving every
/// other code's mapping exactly as written. The entries are re-emitted from the
/// source tokens in their original order (a later entry overrides an earlier
/// one), so any CMap reader reads each kept code as it did before; a removed
/// code inside a <c>bfrange</c> splits the range around it.
/// </summary>
internal static class ToUnicodeCMapEditor
{
    private abstract record Entry;

    private sealed record CharEntry(string Src, Token Dst) : Entry;

    private sealed record RangeEntry(string Lo, string Hi, string? Dst, List<Token>? Array) : Entry;

    /// <summary>
    /// The CMap with <paramref name="removed"/> codes unmapped, or null with a
    /// <paramref name="refusal"/> when it cannot be rewritten without changing
    /// another code's reading. Returns the input unchanged (same instance) when
    /// no removed code is mapped.
    /// </summary>
    public static byte[]? RemoveCodes(byte[] cmap, IReadOnlySet<int> removed, out string? refusal)
    {
        refusal = null;
        var text = Encoding.Latin1.GetString(cmap);
        var tokens = Tokenize(text);
        var codespaces = new List<(string Lo, string Hi)>();
        var blocks = new List<List<Entry>>();
        var touched = false;

        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Type != TokenType.Keyword) continue;
            switch (t.Text)
            {
                case "usecmap":
                case "begincidchar":
                case "begincidrange":
                case "beginnotdefchar":
                case "beginnotdefrange":
                    refusal = $"its /ToUnicode CMap uses {t.Text}";
                    return null;
                case "begincodespacerange":
                    i++;
                    while (i + 1 < tokens.Count && tokens[i].Type == TokenType.HexString && tokens[i + 1].Type == TokenType.HexString)
                    {
                        codespaces.Add((tokens[i].Text, tokens[i + 1].Text));
                        i += 2;
                    }
                    if (i >= tokens.Count || tokens[i].Text != "endcodespacerange")
                    {
                        refusal = "its /ToUnicode codespace range is malformed";
                        return null;
                    }
                    break;
                case "beginbfchar":
                {
                    var block = new List<Entry>();
                    i++;
                    while (i + 1 < tokens.Count && tokens[i].Type == TokenType.HexString &&
                           tokens[i + 1].Type is TokenType.HexString or TokenType.Name)
                    {
                        var entry = new CharEntry(tokens[i].Text, tokens[i + 1]);
                        if (removed.Contains(HexToInt(entry.Src))) touched = true;
                        else block.Add(entry);
                        i += 2;
                    }
                    if (i >= tokens.Count || tokens[i].Text != "endbfchar")
                    {
                        refusal = "its /ToUnicode bfchar block is malformed";
                        return null;
                    }
                    blocks.Add(block);
                    break;
                }
                case "beginbfrange":
                {
                    var block = new List<Entry>();
                    i++;
                    while (i + 2 < tokens.Count && tokens[i].Type == TokenType.HexString && tokens[i + 1].Type == TokenType.HexString)
                    {
                        string lo = tokens[i].Text, hi = tokens[i + 1].Text;
                        RangeEntry entry;
                        if (tokens[i + 2].Type == TokenType.HexString)
                        {
                            entry = new RangeEntry(lo, hi, tokens[i + 2].Text, null);
                            i += 3;
                        }
                        else if (tokens[i + 2].Type == TokenType.ArrayStart)
                        {
                            var j = i + 3;
                            var items = new List<Token>();
                            while (j < tokens.Count && tokens[j].Type != TokenType.ArrayEnd)
                                items.Add(tokens[j++]);
                            if (j >= tokens.Count)
                            {
                                refusal = "its /ToUnicode bfrange array is unterminated";
                                return null;
                            }
                            entry = new RangeEntry(lo, hi, null, items);
                            i = j + 1;
                        }
                        else
                        {
                            refusal = "its /ToUnicode bfrange block is malformed";
                            return null;
                        }

                        if (!SplitRange(entry, removed, block, ref touched, out refusal))
                            return null;
                    }
                    if (i >= tokens.Count || tokens[i].Text != "endbfrange")
                    {
                        refusal = "its /ToUnicode bfrange block is malformed";
                        return null;
                    }
                    blocks.Add(block);
                    break;
                }
            }
        }

        if (!touched) return cmap;

        var output = Emit(codespaces, blocks);

        // The edit must read back as exactly the old mapping minus the removed codes.
        var before = ToUnicodeCMapParser.Parse(cmap);
        var after = ToUnicodeCMapParser.Parse(output);
        var expected = before.Where(kv => !removed.Contains(kv.Key)).ToList();
        if (after.Count != expected.Count || expected.Any(kv => !after.TryGetValue(kv.Key, out var v) || v != kv.Value))
        {
            refusal = "its /ToUnicode CMap did not read back unchanged for the kept codes after the edit";
            return null;
        }
        return output;
    }

    private static bool SplitRange(RangeEntry entry, IReadOnlySet<int> removed, List<Entry> block, ref bool touched, out string? refusal)
    {
        refusal = null;
        int lo = HexToInt(entry.Lo), hi = HexToInt(entry.Hi);
        if (hi < lo || hi - lo > 0xFFFF)
        {
            // Out of the editor's model; keep verbatim only when no removed code could be in it.
            if (removed.Any(c => c >= Math.Min(lo, hi) && c <= Math.Max(lo, hi)))
            {
                refusal = "its /ToUnicode CMap has a malformed bfrange over a removed code";
                return false;
            }
            block.Add(entry);
            return true;
        }

        var hits = removed.Where(c => c >= lo && c <= hi).OrderBy(c => c).ToList();
        if (hits.Count == 0)
        {
            block.Add(entry);
            return true;
        }
        touched = true;

        if (entry.Dst != null)
        {
            // An incrementing destination is split only where every reader agrees
            // on it: an even-length UTF-16 value whose last byte does not carry
            // across the range, and a source range varying only in its last byte.
            var dst = entry.Dst.Length % 2 == 0 ? entry.Dst : "0" + entry.Dst;
            if (dst.Length < 4 || (lo >> 8) != (hi >> 8) ||
                (HexToInt(dst[^2..]) + (hi - lo)) > 0xFF)
            {
                refusal = "a removed code sits in a /ToUnicode bfrange readers do not agree on";
                return false;
            }
        }

        var width = entry.Lo.Length;
        var start = lo;
        foreach (var gap in hits.Append(hi + 1))
        {
            if (gap > start)
            {
                var end = gap - 1;
                if (entry.Dst != null)
                {
                    var dst = entry.Dst.Length % 2 == 0 ? entry.Dst : "0" + entry.Dst;
                    var last = HexToInt(dst[^2..]) + (start - lo);
                    var newDst = dst[..^2] + last.ToString("X2", CultureInfo.InvariantCulture);
                    block.Add(new RangeEntry(Hex(start, width), Hex(end, width), newDst, null));
                }
                else
                {
                    var items = entry.Array!;
                    var from = start - lo;
                    var count = Math.Min(end - start + 1, Math.Max(0, items.Count - from));
                    if (count > 0)
                        block.Add(new RangeEntry(Hex(start, width), Hex(start + count - 1, width), null,
                            items.GetRange(from, count)));
                }
            }
            start = gap + 1;
        }
        return true;
    }

    private static string Hex(int value, int digits) => value.ToString("X" + digits, CultureInfo.InvariantCulture);

    private static byte[] Emit(List<(string Lo, string Hi)> codespaces, List<List<Entry>> blocks)
    {
        var sb = new StringBuilder();
        sb.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n");
        sb.Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n");
        sb.Append("/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n");
        foreach (var chunk in codespaces.Chunk(100))
        {
            sb.Append(chunk.Length.ToString(CultureInfo.InvariantCulture)).Append(" begincodespacerange\n");
            foreach (var (lo, hi) in chunk) sb.Append('<').Append(lo).Append("> <").Append(hi).Append(">\n");
            sb.Append("endcodespacerange\n");
        }
        foreach (var block in blocks)
        {
            foreach (var chunk in block.Chunk(100))
            {
                var isChar = chunk[0] is CharEntry;
                // A block holds one kind; bfchar and bfrange are never mixed.
                sb.Append(chunk.Length.ToString(CultureInfo.InvariantCulture)).Append(isChar ? " beginbfchar\n" : " beginbfrange\n");
                foreach (var e in chunk)
                {
                    switch (e)
                    {
                        case CharEntry c:
                            sb.Append('<').Append(c.Src).Append("> ").Append(TokenText(c.Dst)).Append('\n');
                            break;
                        case RangeEntry r:
                            sb.Append('<').Append(r.Lo).Append("> <").Append(r.Hi).Append("> ");
                            if (r.Dst != null) sb.Append('<').Append(r.Dst).Append('>');
                            else sb.Append('[').Append(string.Join(" ", r.Array!.Select(TokenText))).Append(']');
                            sb.Append('\n');
                            break;
                    }
                }
                sb.Append(isChar ? "endbfchar\n" : "endbfrange\n");
            }
        }
        sb.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static string TokenText(Token t) => t.Type switch
    {
        TokenType.HexString => "<" + t.Text + ">",
        TokenType.Name => "/" + t.Text,
        _ => t.Text,
    };
}
