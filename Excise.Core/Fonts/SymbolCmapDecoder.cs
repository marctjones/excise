using System.Collections.Generic;
using Excise.Core.Text;

namespace Excise.Core.Fonts;

/// <summary>
/// Decodes one-byte content codes to Unicode for a simple SYMBOLIC TrueType
/// through its Microsoft-Symbol <c>(3,0)</c> cmap and <c>post</c> glyph names —
/// the recovery path #791 added for symbolic fonts with no <c>/Encoding</c>.
/// </summary>
/// <remarks>
/// <para>This is assembly on top of the (3,0)/post decoding primitives that
/// <see cref="TrueTypeFontFile"/> already exposes
/// (<see cref="TrueTypeFontFile.GidForSymbolByte"/> /
/// <see cref="TrueTypeFontFile.GlyphName"/> /
/// <see cref="TrueTypeFontFile.Cmap"/>): symbol-cmap code→gid, then gid→Unicode
/// via the program's own non-PUA Unicode cmap or its post glyph names.</para>
///
/// <para>It exists so the <see cref="Text.Segmentation.HiddenTextDetector"/> audit
/// can compute what a symbolic (3,0) font's glyphs WOULD spell (#796) WITHOUT
/// touching <see cref="TextExtractor"/>'s decode path. When <c>/Encoding</c> is
/// present, extraction (like mutool/poppler) honours WinAnsi and never consults
/// the (3,0) cmap (#794/#795); comparing this decode against the extracted text
/// surfaces the resulting redaction gap instead of leaving it silent.</para>
/// </remarks>
internal static class SymbolCmapDecoder
{
    /// <summary>
    /// code (0..255) → Unicode for a symbolic TrueType via its (3,0) symbol cmap
    /// and post glyph names. Codes whose only recovery is a Private-Use scalar are
    /// skipped (genuinely unrecoverable — must not masquerade as real text).
    /// Returns an empty map when nothing resolves.
    /// </summary>
    public static Dictionary<int, string> BuildCodeToText(TrueTypeFontFile ttf)
    {
        var gidToCodepoint = ReverseCmap(ttf.Cmap);
        var result = new Dictionary<int, string>();
        for (int code = 0; code <= 0xFF; code++)
        {
            int gid = ttf.GidForSymbolByte(code);
            if (gid == 0) continue;

            string? unicode = null;
            if (gidToCodepoint.TryGetValue(gid, out var cp) && !IsPrivateUse(cp))
                unicode = char.ConvertFromUtf32(cp);
            if (unicode == null)
            {
                var name = ttf.GlyphName(gid);
                if (name != null) unicode = GlyphNameToUnicode(name);
            }
            if (unicode is { Length: > 0 } && !IsPrivateUse(char.ConvertToUtf32(unicode, 0)))
                result[code] = unicode;
        }
        return result;
    }

    private static Dictionary<int, int> ReverseCmap(IReadOnlyDictionary<int, int> unicodeToGid)
    {
        var gidToCodepoint = new Dictionary<int, int>();
        foreach (var (cp, gid) in unicodeToGid)
        {
            if (gid == 0 || cp <= 0 || cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF))
                continue;
            if (!gidToCodepoint.TryGetValue(gid, out var existing) || PreferCodepoint(cp, existing))
                gidToCodepoint[gid] = cp;
        }
        return gidToCodepoint;
    }

    private static bool PreferCodepoint(int candidate, int existing)
    {
        bool candidatePua = IsPrivateUse(candidate);
        bool existingPua = IsPrivateUse(existing);
        if (candidatePua != existingPua)
            return existingPua; // prefer the non-PUA codepoint
        return candidate < existing;
    }

    private static bool IsPrivateUse(int cp) =>
        (cp >= 0xE000 && cp <= 0xF8FF) || cp >= 0xF0000;

    private static string? GlyphNameToUnicode(string glyphName)
    {
        var fromUni = TryDecodeUniName(glyphName);
        if (fromUni != null) return fromUni;
        return AdobeGlyphList.ToUnicode(glyphName);
    }

    // "uniXXXX" (one or more 4-hex groups) or "uXXXX".."uXXXXXX" per the AGL
    // naming convention. Mirrors TextExtractor.TryDecodeUniName so this audit
    // recovers the same names extraction would.
    private static string? TryDecodeUniName(string glyphName)
    {
        if (glyphName.StartsWith("uni", System.StringComparison.Ordinal))
        {
            var hex = glyphName.Substring(3);
            if (hex.Length == 0 || hex.Length % 4 != 0 || !IsAllHex(hex))
                return null;

            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < hex.Length; i += 4)
            {
                if (!int.TryParse(hex.AsSpan(i, 4), System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out var codePoint))
                    return null;
                sb.Append((char)codePoint);
            }
            return sb.ToString();
        }

        if (glyphName.Length >= 1 && glyphName[0] == 'u' && glyphName.Length != 1)
        {
            var hex = glyphName.Substring(1);
            if (hex.Length is < 4 or > 6 || !IsAllHex(hex))
                return null;
            if (!int.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var codePoint))
                return null;
            if (codePoint is < 0 or > 0x10FFFF or (>= 0xD800 and <= 0xDFFF))
                return null;
            return char.ConvertFromUtf32(codePoint);
        }

        return null;
    }

    private static bool IsAllHex(string s)
    {
        foreach (var c in s)
            if (!System.Uri.IsHexDigit(c)) return false;
        return true;
    }
}
