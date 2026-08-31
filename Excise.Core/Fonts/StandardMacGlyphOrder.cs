namespace Excise.Core.Text;

/// <summary>
/// The standard Macintosh glyph ordering (the 258-entry order a TrueType
/// <c>post</c> table format 1.0 implies, Apple TrueType Reference / ISO 32000-2
/// §9.6.6.4). Maps a glyph index (GID) to its PostScript glyph name.
///
/// This is the last-resort GID→Unicode bridge for a <b>non-embedded</b>
/// <c>CIDFontType2</c> with <c>Identity-H/V</c> encoding and no <c>/ToUnicode</c>
/// CMap: the content-stream 2-byte codes are CIDs = GIDs (Identity), but with no
/// embedded font program there is no cmap to give GID→Unicode. The producing
/// application almost always laid the text out against a TrueType font in this
/// standard order, so mapping GID→name→Unicode (via the Adobe Glyph List)
/// recovers the real text — the same heuristic mutool and pdf.js apply. Without
/// it, extraction mis-reads the raw GID as a Latin-1 code point (#532:
/// issue4722.pdf extracted "DESCRIPTION" as "'(6&amp;5,37,21", a fixed −29 shift).
///
/// Names outside the 258-entry table return false — callers must not extrapolate
/// (the ASCII range is coincidentally linear, the rest is not).
/// </summary>
internal static class StandardMacGlyphOrder
{
    // Indices 0..257. Names map to Unicode through AdobeGlyphList; the first
    // three (.notdef/.null/nonmarkingreturn) and any unmapped name yield no
    // Unicode, which the caller treats as "no glyph".
    private static readonly string[] Names =
    {
        ".notdef", ".null", "nonmarkingreturn", "space", "exclam", "quotedbl",
        "numbersign", "dollar", "percent", "ampersand", "quotesingle",
        "parenleft", "parenright", "asterisk", "plus", "comma", "hyphen",
        "period", "slash", "zero", "one", "two", "three", "four", "five", "six",
        "seven", "eight", "nine", "colon", "semicolon", "less", "equal",
        "greater", "question", "at", "A", "B", "C", "D", "E", "F", "G", "H", "I",
        "J", "K", "L", "M", "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X",
        "Y", "Z", "bracketleft", "backslash", "bracketright", "asciicircum",
        "underscore", "grave", "a", "b", "c", "d", "e", "f", "g", "h", "i", "j",
        "k", "l", "m", "n", "o", "p", "q", "r", "s", "t", "u", "v", "w", "x", "y",
        "z", "braceleft", "bar", "braceright", "asciitilde", "Adieresis",
        "Aring", "Ccedilla", "Eacute", "Ntilde", "Odieresis", "Udieresis",
        "aacute", "agrave", "acircumflex", "adieresis", "atilde", "aring",
        "ccedilla", "eacute", "egrave", "ecircumflex", "edieresis", "iacute",
        "igrave", "icircumflex", "idieresis", "ntilde", "oacute", "ograve",
        "ocircumflex", "odieresis", "otilde", "uacute", "ugrave", "ucircumflex",
        "udieresis", "dagger", "degree", "cent", "sterling", "section", "bullet",
        "paragraph", "germandbls", "registered", "copyright", "trademark",
        "acute", "dieresis", "notequal", "AE", "Oslash", "infinity", "plusminus",
        "lessequal", "greaterequal", "yen", "mu", "partialdiff", "summation",
        "product", "pi", "integral", "ordfeminine", "ordmasculine", "Omega",
        "ae", "oslash", "questiondown", "exclamdown", "logicalnot", "radical",
        "florin", "approxequal", "Delta", "guillemotleft", "guillemotright",
        "ellipsis", "nonbreakingspace", "Agrave", "Atilde", "Otilde", "OE", "oe",
        "endash", "emdash", "quotedblleft", "quotedblright", "quoteleft",
        "quoteright", "divide", "lozenge", "ydieresis", "Ydieresis", "fraction",
        "currency", "guilsinglleft", "guilsinglright", "fi", "fl", "daggerdbl",
        "periodcentered", "quotesinglbase", "quotedblbase", "perthousand",
        "Acircumflex", "Ecircumflex", "Aacute", "Edieresis", "Egrave", "Iacute",
        "Icircumflex", "Idieresis", "Igrave", "Oacute", "Ocircumflex", "apple",
        "Ograve", "Uacute", "Ucircumflex", "Ugrave", "dotlessi", "circumflex",
        "tilde", "macron", "breve", "dotaccent", "ring", "cedilla",
        "hungarumlaut", "ogonek", "caron", "Lslash", "lslash", "Scaron",
        "scaron", "Zcaron", "zcaron", "brokenbar", "Eth", "eth", "Yacute",
        "yacute", "Thorn", "thorn", "minus", "multiply", "onesuperior",
        "twosuperior", "threesuperior", "onehalf", "onequarter", "threequarters",
        "franc", "Gbreve", "gbreve", "Idotaccent", "Scedilla", "scedilla",
        "Cacute", "cacute", "Ccaron", "ccaron", "dcroat"
    };

    public static bool TryGetName(int gid, out string name)
    {
        if (gid >= 0 && gid < Names.Length)
        {
            name = Names[gid];
            return true;
        }

        name = string.Empty;
        return false;
    }
}
