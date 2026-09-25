namespace Excise.Core.Fonts;

/// <summary>
/// MacRomanEncoding, ISO 32000-2 Annex D, Table D.2: the one table the text
/// extractor and the renderer both decode it through (#1831).
/// </summary>
/// <remarks>
/// Codes below 0x80 are ASCII. Code 0xDB is <c>currency</c> (U+00A4): Apple
/// moved it to the euro, and Annex D note 1 says MacRomanEncoding did not. The
/// fifteen high codes Table D.2 leaves unassigned keep their Mac OS Roman
/// characters (≠ ∞ ≤ ≥ ∂ ∑ ∏ π ∫ Ω √ ≈ ∆ ◊ and the Apple logo, U+F8FF), which is
/// how poppler decodes them.
/// </remarks>
internal static class MacRomanEncoding
{
    private const string High =
        "ÄÅÇÉÑÖÜáàâäãåçéè" + // 0x80
        "êëíìîïñóòôöõúùûü" + // 0x90
        "†°¢£§•¶ß®©™´¨≠ÆØ" + // 0xA0
        "∞±≤≥¥µ∂∑∏π∫ªºΩæø" + // 0xB0
        "¿¡¬√ƒ≈∆«»… ÀÃÕŒœ" + // 0xC0
        "–—“”‘’÷◊ÿŸ⁄¤‹›ﬁﬂ" + // 0xD0
        "‡·‚„‰ÂÊÁËÈÍÎÏÌÓÔ" + // 0xE0
        "ÒÚÛÙıˆ˜¯˘˙˚¸˝˛ˇ"; // 0xF0

    /// <summary>The character for <paramref name="code"/>; a code outside 0x80–0xFF decodes as itself.</summary>
    public static char Decode(int code) => code is >= 0x80 and <= 0xFF ? High[code - 0x80] : (char)code;
}
