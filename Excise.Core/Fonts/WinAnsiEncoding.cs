using System.Collections.Generic;

namespace Excise.Core.Fonts;

/// <summary>
/// WinAnsi (CP1252) — the encoding a simple PDF font almost always uses, and
/// the one a string drawn with such a font must be written in.
///
/// <para><b>Why this is its own type (#1644).</b> The mapping existed, privately,
/// inside <c>PdfFont</c>'s string escaper, where #426 put it after decimal
/// escapes rendered "é", "—" and "·" as garbage. Nothing else could reach it,
/// so <c>RestoredCopyBuilder</c> grew a cruder rule —
/// <c>c is >= ' ' and &lt;= '~' ? c : '?'</c> — and a reconstruction of a
/// redaction leak printed <c>?conceded?</c> where the recovered text was
/// <c>"conceded"</c>. The JSON was right and the PDF was wrong, in an artifact
/// whose stated purpose is what a reviewer hands a court.</para>
///
/// <para><b>What it can and cannot represent.</b> ASCII and Latin-1 high map to
/// their own byte; CP1252's 0x80–0x9F block holds the curly quotes, the dashes
/// and the ellipsis that Word and InDesign emit constantly. Nothing else fits —
/// CJK, Cyrillic, Greek, Hebrew and U+0100+ Latin have no WinAnsi byte at all,
/// and no amount of mapping invents one. Those need an embedded font, which is
/// the half of #1644 that stays open.</para>
///
/// <para>⚠️ So a caller must handle <see cref="TryMap"/> returning false as
/// DATA LOSS and say so, rather than substituting a character and moving on.
/// Silently printing '?' is what made the reconstruction misstate its own
/// findings.</para>
/// </summary>
internal static class WinAnsiEncoding
{
    /// <summary>
    /// The CP1252 0x80–0x9F block, which is where WinAnsi and Latin-1 differ.
    /// Outside it, ASCII (0x20–0x7E) and Latin-1 (0xA0–0xFF) are their own byte.
    /// </summary>
    private static readonly Dictionary<char, byte> HighMap = new()
    {
        ['€'] = 0x80, ['‚'] = 0x82, ['ƒ'] = 0x83, ['„'] = 0x84,
        ['…'] = 0x85, ['†'] = 0x86, ['‡'] = 0x87, ['ˆ'] = 0x88,
        ['‰'] = 0x89, ['Š'] = 0x8A, ['‹'] = 0x8B, ['Œ'] = 0x8C,
        ['Ž'] = 0x8E, ['‘'] = 0x91, ['’'] = 0x92, ['“'] = 0x93,
        ['”'] = 0x94, ['•'] = 0x95, ['–'] = 0x96, ['—'] = 0x97,
        ['˜'] = 0x98, ['™'] = 0x99, ['š'] = 0x9A, ['›'] = 0x9B,
        ['œ'] = 0x9C, ['ž'] = 0x9E, ['Ÿ'] = 0x9F,
    };

    // HighMap inverted. The five codes CP1252 leaves undefined decode as
    // themselves, as .NET's CP1252 decoder does.
    private static readonly char[] HighDecode = BuildHighDecode();

    private static char[] BuildHighDecode()
    {
        var table = new char[0x20];
        for (var i = 0; i < table.Length; i++) table[i] = (char)(0x80 + i);
        foreach (var (c, b) in HighMap) table[b - 0x80] = c;
        return table;
    }

    /// <summary>The character for <paramref name="code"/>; a code outside 0x80–0x9F decodes as itself.</summary>
    public static char Decode(int code) => code is >= 0x80 and <= 0x9F ? HighDecode[code - 0x80] : (char)code;

    /// <summary>The WinAnsi byte for <paramref name="c"/>, if it has one.</summary>
    public static bool TryMap(char c, out byte b)
    {
        if (c >= 0x20 && c <= 0x7E) { b = (byte)c; return true; }   // ASCII
        if (c >= 0xA0 && c <= 0xFF) { b = (byte)c; return true; }   // Latin-1 high == WinAnsi
        return HighMap.TryGetValue(c, out b);                       // CP1252 specials
    }

    /// <summary>
    /// Encode <paramref name="text"/> for drawing with a WinAnsi simple font.
    /// </summary>
    /// <param name="substitute">The byte written for a character with no WinAnsi form.</param>
    /// <param name="lost">
    /// How many characters had no WinAnsi form. ⚠️ A caller that ignores this is
    /// printing a string it knows to be wrong — see the type summary.
    /// </param>
    public static byte[] Encode(string text, out int lost, byte substitute = (byte)'?')
    {
        lost = 0;
        var bytes = new byte[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            if (TryMap(text[i], out var b)) bytes[i] = b;
            else { bytes[i] = substitute; lost++; }
        }
        return bytes;
    }
}
