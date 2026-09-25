using System.Globalization;

namespace Excise.Rendering;

/// <summary>
/// <c>char</c> adapters over <see cref="Excise.Core.Text.AdobeGlyphList"/>, the
/// glyph-name table text extraction decodes <c>/Differences</c> names through,
/// so the renderer cannot draw a glyph extraction reads differently (#1831).
/// A name whose Unicode value is not one UTF-16 unit (a ligature decoded to
/// "fi", a supplementary-plane code point) resolves to nothing here.
/// </summary>
internal static class AdobeGlyphList
{
    /// <summary>Reverse lookup: Unicode → glyph name.</summary>
    /// <remarks>
    /// Falls back to the Adobe "uniXXXX" convention (AGL §D.1) for any BMP
    /// codepoint not in the named-glyph table. Subsetted CFF programs emitted
    /// by tools like XEP follow that convention internally, so the synthetic
    /// name is what we need to look up the glyph in the CFF charset.
    /// </remarks>
    public static bool TryGetName(char unicode, out string glyphName)
    {
        glyphName = Excise.Core.Text.AdobeGlyphList.ToGlyphName(unicode)
            ?? (unicode >= 0x20 ? "uni" + ((int)unicode).ToString("X4", CultureInfo.InvariantCulture) : string.Empty);
        return glyphName.Length > 0;
    }

    public static bool TryGet(string glyphName, out char unicode)
    {
        if (Excise.Core.Text.AdobeGlyphList.ToUnicode(glyphName) is { Length: 1 } core)
        {
            unicode = core[0];
            return true;
        }

        // Uniform naming convention (AGL §D.1): /uniXXXX for BMP codepoints.
        if (glyphName.Length == 7 && glyphName.StartsWith("uni"))
        {
            if (int.TryParse(glyphName.Substring(3), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out var code))
            {
                unicode = (char)code;
                return true;
            }
        }
        // /uXXXX or /uXXXXXX (non-BMP truncates to BMP — adequate for Skia rendering).
        if (glyphName.Length >= 5 && glyphName[0] == 'u' &&
            int.TryParse(glyphName.Substring(1), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out var code2) &&
            code2 < 0x10000)
        {
            unicode = (char)code2;
            return true;
        }

        unicode = '\0';
        return false;
    }
}
