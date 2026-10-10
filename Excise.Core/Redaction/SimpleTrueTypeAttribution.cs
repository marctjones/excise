using System;
using System.Collections.Generic;
using System.Linq;
using Excise.Core.Document;
using Excise.Core.Fonts;
using Excise.Core.Primitives;
using Excise.Core.Text;

namespace Excise.Core.Text.Segmentation;

/// <summary>
/// #1920: every glyph of a simple TrueType font's program that ANY reader could
/// select for a one-byte code. ISO 32000-2 §9.6.6.4 lets a reader choose among
/// the (3,0) subtable with or without the F000 offset, the (1,0) subtable, the
/// (3,1) subtable through the encoding's glyph names, and the <c>post</c> names,
/// and readers differ. The union over all of them is what a kept code must keep
/// (so no reader loses a glyph it draws) and what a removed code may lose only
/// when no kept code reaches it.
/// </summary>
internal sealed class SimpleTrueTypeAttribution
{
    private static readonly Dictionary<int, int> MacRomanByUnicode = BuildMacRoman();

    private readonly Dictionary<int, HashSet<int>> _byKey;
    private readonly Dictionary<string, HashSet<int>> _byName;
    private readonly Dictionary<int, string> _differences;
    private readonly IReadOnlyDictionary<int, string> _unicodeSeen;

    private SimpleTrueTypeAttribution(Dictionary<int, HashSet<int>> byKey, Dictionary<string, HashSet<int>> byName,
        Dictionary<int, string> differences, IReadOnlyDictionary<int, string> unicodeSeen)
    {
        _byKey = byKey;
        _byName = byName;
        _differences = differences;
        _unicodeSeen = unicodeSeen;
    }

    /// <summary>
    /// The attribution for <paramref name="font"/>, or null when its program's
    /// tables cannot be read. <paramref name="unicodeSeen"/> is the Unicode the
    /// content walk decoded per code; it is read when a code is asked about.
    /// </summary>
    public static SimpleTrueTypeAttribution? Build(PdfDocument document, PdfDictionary font, byte[] program,
        IReadOnlyDictionary<int, string> unicodeSeen)
    {
        var byKey = new Dictionary<int, HashSet<int>>();
        var byName = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        try
        {
            var tables = SfntGlyphEditor.ReadTables(program);
            if (tables.TryGetValue("cmap", out var cmapTable))
            {
                var cmap = program.AsSpan(cmapTable.Offset, cmapTable.Length).ToArray();
                int count = SfntGlyphEditor.U16(cmap, 2);
                for (var i = 0; i < count; i++)
                {
                    var offset = checked((int)SfntGlyphEditor.U32(cmap, 8 + 8 * i));
                    foreach (var (code, gid) in TrueTypeFontFile.ReadCmapSubtable(cmap, offset))
                    {
                        if (!byKey.TryGetValue(code, out var set)) byKey[code] = set = new HashSet<int>();
                        set.Add(gid);
                    }
                }
            }
            var parsed = TrueTypeFontFile.Parse(program);
            for (var gid = 0; gid < parsed.GlyphCount; gid++)
            {
                if (parsed.GlyphName(gid) is not { } name) continue;
                if (!byName.TryGetValue(name, out var set)) byName[name] = set = new HashSet<int>();
                set.Add(gid);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }

        var differences = new Dictionary<int, string>();
        if (document.Resolve(font.GetOptional("Encoding") ?? PdfNull.Instance) is PdfDictionary encoding &&
            document.Resolve(encoding.GetOptional("Differences") ?? PdfNull.Instance) is PdfArray diffs)
        {
            var code = 0;
            foreach (var item in diffs)
            {
                var value = document.Resolve(item);
                if (value.TryGetNumber(out var n)) code = (int)n;
                else if (value is PdfName glyph) differences[code++] = glyph.Value;
            }
        }
        return new SimpleTrueTypeAttribution(byKey, byName, differences, unicodeSeen);
    }

    /// <summary>Every gid a reader could draw for <paramref name="code"/>.</summary>
    public IEnumerable<int> GidsOf(int code)
    {
        var gids = new HashSet<int>();
        var keys = new HashSet<int> { code, 0xF000 | code, 0xF100 | code, 0xF200 | code };
        var unicode = new HashSet<int> { code, WinAnsiEncoding.Decode(code), MacRomanEncoding.Decode(code) };
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (_unicodeSeen.TryGetValue(code, out var seen) && !string.IsNullOrEmpty(seen))
            AddCodePoints(seen, unicode);
        if (_differences.TryGetValue(code, out var diffName))
        {
            names.Add(diffName);
            if (AdobeGlyphList.ToUnicode(diffName) is { } agl) AddCodePoints(agl, unicode);
        }
        foreach (var u in unicode)
        {
            keys.Add(u);
            if (MacRomanByUnicode.TryGetValue(u, out var mac)) keys.Add(mac);
            if (AdobeGlyphList.ToGlyphName(u) is { } name) names.Add(name);
            names.Add($"uni{u:X4}");
        }
        foreach (var key in keys)
            if (_byKey.TryGetValue(key, out var set)) gids.UnionWith(set);
        foreach (var name in names)
            if (_byName.TryGetValue(name, out var set)) gids.UnionWith(set);
        return gids;
    }

    private static void AddCodePoints(string s, HashSet<int> into)
    {
        for (var i = 0; i < s.Length; i++)
            into.Add(char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])
                ? char.ConvertToUtf32(s[i], s[++i]) : s[i]);
    }

    private static Dictionary<int, int> BuildMacRoman()
    {
        var map = new Dictionary<int, int>();
        for (var c = 0; c < 256; c++) map.TryAdd(MacRomanEncoding.Decode(c), c);
        return map;
    }
}
