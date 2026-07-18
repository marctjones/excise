using System;
using System.Collections.Generic;

namespace Excise.Rendering;

/// <summary>
/// Maps PDF/PostScript glyph names to Unicode codepoints. Used when a font's
/// <c>/Encoding</c> dictionary has a <c>/Differences</c> array — each entry
/// there names a glyph (e.g. <c>/endash</c>, <c>/A</c>) at a specific code
/// position, and we need a Unicode char to hand to Skia's fallback font.
///
/// This is a pragmatic subset of the Adobe Glyph List (AGL). It covers ASCII,
/// Latin-1 Supplement, Latin Extended-A, common typographic marks, and the
/// ligatures that actually show up in embedded subset fonts. When a glyph name
/// isn't found we return '\0' so the caller can fall through to base encoding.
/// </summary>
internal static class AdobeGlyphList
{
    // Roslyn can't prove that _map is initialized before the Lazy<>'s factory
    // runs (the field appears later in source order), even though the factory
    // is only invoked on first .Value access — bang-suppress the spurious
    // CS8602 warning rather than reorder the file.
    private static readonly Lazy<Dictionary<char, string>> _reverse =
        new(() =>
        {
            var inv = new Dictionary<char, string>(_map!.Count);
            foreach (var kvp in _map)
                if (!inv.ContainsKey(kvp.Value))
                    inv[kvp.Value] = kvp.Key;
            return inv;
        });

    /// <summary>Reverse lookup: Unicode → glyph name.</summary>
    /// <remarks>
    /// Falls back to the Adobe "uniXXXX" convention (AGL §D.1) for any BMP
    /// codepoint not in the named-glyph table. Subsetted CFF programs emitted
    /// by tools like XEP follow that convention internally, so the synthetic
    /// name is what we need to look up the glyph in the CFF charset.
    /// </remarks>
    public static bool TryGetName(char unicode, out string glyphName)
    {
        if (_reverse.Value.TryGetValue(unicode, out var name))
        {
            glyphName = name;
            return true;
        }
        if (unicode >= 0x20)
        {
            glyphName = "uni" + ((int)unicode).ToString("X4",
                System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }
        glyphName = string.Empty;
        return false;
    }

    public static bool TryGet(string glyphName, out char unicode)
    {
        if (_map.TryGetValue(glyphName, out unicode))
            return true;

        // Uniform naming convention (AGL §D.1): /uniXXXX for BMP codepoints.
        if (glyphName.Length == 7 && glyphName.StartsWith("uni"))
        {
            if (int.TryParse(glyphName.Substring(3), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var code))
            {
                unicode = (char)code;
                return true;
            }
        }
        // /uXXXX or /uXXXXXX (non-BMP truncates to BMP — adequate for Skia rendering).
        if (glyphName.Length >= 5 && glyphName[0] == 'u' &&
            int.TryParse(glyphName.Substring(1), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var code2) &&
            code2 < 0x10000)
        {
            unicode = (char)code2;
            return true;
        }

        unicode = '\0';
        return false;
    }

    // Keep alphabetized within each section for easy diffing when expanding.
    private static readonly Dictionary<string, char> _map = new()
    {
        // ASCII printable (U+0020 – U+007E)
        ["space"] = ' ',
        ["exclam"] = '!',
        ["quotedbl"] = '"',
        ["numbersign"] = '#',
        ["dollar"] = '$',
        ["percent"] = '%',
        ["ampersand"] = '&',
        ["quotesingle"] = '\'',
        ["parenleft"] = '(',
        ["parenright"] = ')',
        ["asterisk"] = '*',
        ["plus"] = '+',
        ["comma"] = ',',
        ["hyphen"] = '-',
        ["period"] = '.',
        ["slash"] = '/',
        ["zero"] = '0',
        ["one"] = '1',
        ["two"] = '2',
        ["three"] = '3',
        ["four"] = '4',
        ["five"] = '5',
        ["six"] = '6',
        ["seven"] = '7',
        ["eight"] = '8',
        ["nine"] = '9',
        ["colon"] = ':',
        ["semicolon"] = ';',
        ["less"] = '<',
        ["equal"] = '=',
        ["greater"] = '>',
        ["question"] = '?',
        ["at"] = '@',
        ["A"] = 'A', ["B"] = 'B', ["C"] = 'C', ["D"] = 'D', ["E"] = 'E',
        ["F"] = 'F', ["G"] = 'G', ["H"] = 'H', ["I"] = 'I', ["J"] = 'J',
        ["K"] = 'K', ["L"] = 'L', ["M"] = 'M', ["N"] = 'N', ["O"] = 'O',
        ["P"] = 'P', ["Q"] = 'Q', ["R"] = 'R', ["S"] = 'S', ["T"] = 'T',
        ["U"] = 'U', ["V"] = 'V', ["W"] = 'W', ["X"] = 'X', ["Y"] = 'Y',
        ["Z"] = 'Z',
        ["bracketleft"] = '[',
        ["backslash"] = '\\',
        ["bracketright"] = ']',
        ["asciicircum"] = '^',
        ["underscore"] = '_',
        ["grave"] = '`',
        ["a"] = 'a', ["b"] = 'b', ["c"] = 'c', ["d"] = 'd', ["e"] = 'e',
        ["f"] = 'f', ["g"] = 'g', ["h"] = 'h', ["i"] = 'i', ["j"] = 'j',
        ["k"] = 'k', ["l"] = 'l', ["m"] = 'm', ["n"] = 'n', ["o"] = 'o',
        ["p"] = 'p', ["q"] = 'q', ["r"] = 'r', ["s"] = 's', ["t"] = 't',
        ["u"] = 'u', ["v"] = 'v', ["w"] = 'w', ["x"] = 'x', ["y"] = 'y',
        ["z"] = 'z',
        ["braceleft"] = '{',
        ["bar"] = '|',
        ["braceright"] = '}',
        ["asciitilde"] = '~',

        // PDF/PS legacy aliases (pre-AGL) — quoteleft/quoteright point at
        // typographic curly quotes in modern fonts but at ASCII positions in
        // StandardEncoding. Most real fonts map them to U+2018/U+2019.
        ["quoteleft"] = '‘',
        ["quoteright"] = '’',

        // Common typographic marks (U+2000-ish range)
        ["endash"] = '–',
        ["emdash"] = '—',
        ["quotedblleft"] = '“',
        ["quotedblright"] = '”',
        ["quotedblbase"] = '„',
        ["quotesinglbase"] = '‚',
        ["guilsinglleft"] = '‹',
        ["guilsinglright"] = '›',
        ["bullet"] = '•',
        ["ellipsis"] = '…',
        ["dagger"] = '†',
        ["daggerdbl"] = '‡',
        ["perthousand"] = '‰',
        ["fraction"] = '⁄',
        ["trademark"] = '™',
        ["Euro"] = '€',

        // Common Latin-1 Supplement (U+00A0 – U+00FF)
        ["exclamdown"] = '¡',
        ["cent"] = '¢',
        ["sterling"] = '£',
        ["currency"] = '¤',
        ["yen"] = '¥',
        ["brokenbar"] = '¦',
        ["section"] = '§',
        ["dieresis"] = '¨',
        ["copyright"] = '©',
        ["ordfeminine"] = 'ª',
        ["guillemotleft"] = '«',
        ["logicalnot"] = '¬',
        ["registered"] = '®',
        ["macron"] = '¯',
        ["degree"] = '°',
        ["plusminus"] = '±',
        ["twosuperior"] = '²',
        ["threesuperior"] = '³',
        ["acute"] = '´',
        ["mu"] = 'µ',
        ["paragraph"] = '¶',
        ["periodcentered"] = '·',
        ["cedilla"] = '¸',
        ["onesuperior"] = '¹',
        ["ordmasculine"] = 'º',
        ["guillemotright"] = '»',
        ["onequarter"] = '¼',
        ["onehalf"] = '½',
        ["threequarters"] = '¾',
        ["questiondown"] = '¿',

        // Accented Latin (most common)
        ["Agrave"] = 'À', ["Aacute"] = 'Á', ["Acircumflex"] = 'Â',
        ["Atilde"] = 'Ã', ["Adieresis"] = 'Ä', ["Aring"] = 'Å',
        ["AE"] = 'Æ', ["Ccedilla"] = 'Ç',
        ["Egrave"] = 'È', ["Eacute"] = 'É', ["Ecircumflex"] = 'Ê',
        ["Edieresis"] = 'Ë',
        ["Igrave"] = 'Ì', ["Iacute"] = 'Í', ["Icircumflex"] = 'Î',
        ["Idieresis"] = 'Ï',
        ["Eth"] = 'Ð', ["Ntilde"] = 'Ñ',
        ["Ograve"] = 'Ò', ["Oacute"] = 'Ó', ["Ocircumflex"] = 'Ô',
        ["Otilde"] = 'Õ', ["Odieresis"] = 'Ö',
        ["multiply"] = '×', ["Oslash"] = 'Ø',
        ["Ugrave"] = 'Ù', ["Uacute"] = 'Ú', ["Ucircumflex"] = 'Û',
        ["Udieresis"] = 'Ü', ["Yacute"] = 'Ý', ["Thorn"] = 'Þ',
        ["germandbls"] = 'ß',
        ["agrave"] = 'à', ["aacute"] = 'á', ["acircumflex"] = 'â',
        ["atilde"] = 'ã', ["adieresis"] = 'ä', ["aring"] = 'å',
        ["ae"] = 'æ', ["ccedilla"] = 'ç',
        ["egrave"] = 'è', ["eacute"] = 'é', ["ecircumflex"] = 'ê',
        ["edieresis"] = 'ë',
        ["igrave"] = 'ì', ["iacute"] = 'í', ["icircumflex"] = 'î',
        ["idieresis"] = 'ï',
        ["eth"] = 'ð', ["ntilde"] = 'ñ',
        ["ograve"] = 'ò', ["oacute"] = 'ó', ["ocircumflex"] = 'ô',
        ["otilde"] = 'õ', ["odieresis"] = 'ö',
        ["divide"] = '÷', ["oslash"] = 'ø',
        ["ugrave"] = 'ù', ["uacute"] = 'ú', ["ucircumflex"] = 'û',
        ["udieresis"] = 'ü', ["yacute"] = 'ý', ["thorn"] = 'þ',
        ["ydieresis"] = 'ÿ',

        // Latin Extended-A (common subset)
        ["OE"] = 'Œ', ["oe"] = 'œ',
        ["Scaron"] = 'Š', ["scaron"] = 'š',
        ["Ydieresis"] = 'Ÿ',
        ["Zcaron"] = 'Ž', ["zcaron"] = 'ž',
        ["Lslash"] = 'Ł', ["lslash"] = 'ł',
        ["Lcaron"] = 'Ľ', ["lcaron"] = 'ľ',

        // Ligatures (extremely common in embedded PDF fonts)
        ["fi"] = 'ﬁ',
        ["fl"] = 'ﬂ',
        ["ffi"] = 'ﬃ',
        ["ffl"] = 'ﬄ',
        ["ff"] = 'ﬀ',

        // Misc marks used in body text
        ["florin"] = 'ƒ',
        ["circumflex"] = 'ˆ',
        ["tilde"] = '˜',
        ["breve"] = '˘',
        ["dotaccent"] = '˙',
        ["ring"] = '˚',
        ["ogonek"] = '˛',
        ["caron"] = 'ˇ',
        ["hungarumlaut"] = '˝',
        ["minus"] = '−',
        ["lozenge"] = '◊',
        ["infinity"] = '∞',
        ["integral"] = '∫',
        ["summation"] = '∑',
        ["product"] = '∏',
        ["radical"] = '√',
        ["partialdiff"] = '∂',
        ["Delta"] = '∆',
        ["approxequal"] = '≈',
        ["notequal"] = '≠',
        ["lessequal"] = '≤',
        ["greaterequal"] = '≥',

        // ZapfDingbats historical glyph names. These are not AGL names, but
        // PDFs commonly use them in /Differences for the Standard 14 dingbat font.
        ["a109"] = '♠',
        ["a110"] = '♥',
        ["a111"] = '♦',
        ["a112"] = '♣',
    };
}
