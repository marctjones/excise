namespace Excise.Core.Fonts;

/// <summary>
/// Glyph advance widths for the 14 standard Type1 fonts, in 1000ths of an em,
/// for character codes 32-126.
///
/// <para><b>Why this exists (#1100).</b> A standard-14 font has no /Widths array
/// — §9.6.2.2 lets a producer omit it entirely — so excise has to supply the
/// metrics. It supplied a flat 600 for every face that was not Courier or
/// Helvetica, which meant all four Times faces advanced 0.6em per glyph
/// regardless of the glyph.</para>
///
/// <para><b>That is redaction geometry, not typography.</b> On a 22-character
/// Times-Roman line at 20pt the letter model drifted 74pt from the truth. The
/// consequences, all measured on
/// <c>test-pdfs/pdfjs/issue15893_reduced.pdf</c>:</para>
///
/// <list type="bullet">
///   <item>the rebuilt run after glyph removal was placed at x=142 where the
///     real metrics put it at 111, so the tail of the line crossed the 200pt
///     page edge and mutool read "Issue  - passw" for "Issue  - password" —
///     text that looks destroyed and is merely off-page;</item>
///   <item>the black rectangle for the redacted word was drawn at x=82-142
///     while the glyphs sat at 56-106, so the visible marker missed the text it
///     claimed to cover by 26pt;</item>
///   <item><c>page.RedactArea(rect)</c> selects glyphs by this same geometry,
///     so area redaction on a Times document removed the wrong ones.</item>
/// </list>
///
/// <para><b>Provenance.</b> Generated from the URW base-35 AFM files shipped
/// with Ghostscript, which are metrically compatible with the Adobe standard 14
/// by design. Spot-checked against published Adobe values before use
/// (Times-Roman I=333, w=722, space=250; Helvetica space=278, A=667, w=722,
/// period=278; Symbol alpha=631; ZapfDingbats a1=974) — all agreed, as did
/// every letter of the Helvetica table this replaces. These are metric facts
/// about the standard 14, not font programs; no font data is embedded.</para>
///
/// <para><b>Scope, deliberately.</b> Codes 32-126 only, which is the range
/// where StandardEncoding, WinAnsiEncoding and MacRomanEncoding all agree, so
/// no encoding lookup is needed to be correct. Codes outside it need the font's
/// /Encoding to resolve a glyph name and are left to the caller's existing
/// fallback rather than guessed at — being confidently wrong in a new way is
/// not an improvement on being obviously wrong in an old one.</para>
/// </summary>
internal static class StandardFontMetrics
{
    private const int First = 32;
    private const int Last = 126;

    /// <summary>Times-Roman</summary>
    private static readonly short[] TimesRoman =
    {
         250,  333,  408,  500,  500,  833,  778,  333,  333,  333,  // 32-41
         500,  564,  250,  333,  250,  278,  500,  500,  500,  500,  // 42-51
         500,  500,  500,  500,  500,  500,  278,  278,  564,  564,  // 52-61
         564,  444,  921,  722,  667,  667,  722,  611,  556,  722,  // 62-71
         722,  333,  389,  722,  611,  889,  722,  722,  556,  722,  // 72-81
         667,  556,  611,  722,  722,  944,  722,  722,  611,  333,  // 82-91
         278,  333,  469,  500,  333,  444,  500,  444,  500,  444,  // 92-101
         333,  500,  500,  278,  278,  500,  278,  778,  500,  500,  // 102-111
         500,  500,  333,  389,  278,  500,  500,  722,  500,  500,  // 112-121
         444,  480,  200,  480,  541,  // 122-126
    };

    /// <summary>Times-Bold</summary>
    private static readonly short[] TimesBold =
    {
         250,  333,  555,  500,  500, 1000,  833,  333,  333,  333,  // 32-41
         500,  570,  250,  333,  250,  278,  500,  500,  500,  500,  // 42-51
         500,  500,  500,  500,  500,  500,  333,  333,  570,  570,  // 52-61
         570,  500,  930,  722,  667,  722,  722,  667,  611,  778,  // 62-71
         778,  389,  500,  778,  667,  944,  722,  778,  611,  778,  // 72-81
         722,  556,  667,  722,  722, 1000,  722,  722,  667,  333,  // 82-91
         278,  333,  581,  500,  333,  500,  556,  444,  556,  444,  // 92-101
         333,  500,  556,  278,  333,  556,  278,  833,  556,  500,  // 102-111
         556,  556,  444,  389,  333,  556,  500,  722,  500,  500,  // 112-121
         444,  394,  220,  394,  520,  // 122-126
    };

    /// <summary>Times-Italic</summary>
    private static readonly short[] TimesItalic =
    {
         250,  333,  420,  500,  500,  833,  778,  333,  333,  333,  // 32-41
         500,  675,  250,  333,  250,  278,  500,  500,  500,  500,  // 42-51
         500,  500,  500,  500,  500,  500,  333,  333,  675,  675,  // 52-61
         675,  500,  920,  611,  611,  667,  722,  611,  611,  722,  // 62-71
         722,  333,  444,  667,  556,  833,  667,  722,  611,  722,  // 72-81
         611,  500,  556,  722,  611,  833,  611,  556,  556,  389,  // 82-91
         278,  389,  422,  500,  333,  500,  500,  444,  500,  444,  // 92-101
         278,  500,  500,  278,  278,  444,  278,  722,  500,  500,  // 102-111
         500,  500,  389,  389,  278,  500,  444,  667,  444,  444,  // 112-121
         389,  400,  275,  400,  541,  // 122-126
    };

    /// <summary>Times-BoldItalic</summary>
    private static readonly short[] TimesBoldItalic =
    {
         250,  389,  555,  500,  500,  833,  778,  333,  333,  333,  // 32-41
         500,  570,  250,  333,  250,  278,  500,  500,  500,  500,  // 42-51
         500,  500,  500,  500,  500,  500,  333,  333,  570,  570,  // 52-61
         570,  500,  832,  667,  667,  667,  722,  667,  667,  722,  // 62-71
         778,  389,  500,  667,  611,  889,  722,  722,  611,  722,  // 72-81
         667,  556,  611,  722,  667,  889,  667,  611,  611,  333,  // 82-91
         278,  333,  570,  500,  333,  500,  500,  444,  500,  444,  // 92-101
         333,  500,  556,  278,  278,  500,  278,  778,  556,  500,  // 102-111
         500,  500,  389,  389,  278,  556,  444,  667,  500,  444,  // 112-121
         389,  348,  220,  348,  570,  // 122-126
    };

    /// <summary>Helvetica</summary>
    private static readonly short[] Helvetica =
    {
         278,  278,  355,  556,  556,  889,  667,  221,  333,  333,  // 32-41
         389,  584,  278,  333,  278,  278,  556,  556,  556,  556,  // 42-51
         556,  556,  556,  556,  556,  556,  278,  278,  584,  584,  // 52-61
         584,  556, 1015,  667,  667,  722,  722,  667,  611,  778,  // 62-71
         722,  278,  500,  667,  556,  833,  722,  778,  667,  778,  // 72-81
         722,  667,  611,  722,  667,  944,  667,  667,  611,  278,  // 82-91
         278,  278,  469,  556,  222,  556,  556,  500,  556,  556,  // 92-101
         278,  556,  556,  222,  222,  500,  222,  833,  556,  556,  // 102-111
         556,  556,  333,  500,  278,  556,  500,  722,  500,  500,  // 112-121
         500,  334,  260,  334,  584,  // 122-126
    };

    /// <summary>Helvetica-Bold</summary>
    private static readonly short[] HelveticaBold =
    {
         278,  333,  474,  556,  556,  889,  722,  278,  333,  333,  // 32-41
         389,  584,  278,  333,  278,  278,  556,  556,  556,  556,  // 42-51
         556,  556,  556,  556,  556,  556,  333,  333,  584,  584,  // 52-61
         584,  611,  975,  722,  722,  722,  722,  667,  611,  778,  // 62-71
         722,  278,  556,  722,  611,  833,  722,  778,  667,  778,  // 72-81
         722,  667,  611,  722,  667,  944,  667,  667,  611,  333,  // 82-91
         278,  333,  584,  556,  278,  556,  611,  556,  611,  556,  // 92-101
         333,  611,  611,  278,  278,  556,  278,  889,  611,  611,  // 102-111
         611,  611,  389,  556,  333,  611,  556,  778,  556,  556,  // 112-121
         500,  389,  280,  389,  584,  // 122-126
    };

    /// <summary>Helvetica-Oblique</summary>
    private static readonly short[] HelveticaOblique =
    {
         278,  278,  355,  556,  556,  889,  667,  222,  333,  333,  // 32-41
         389,  584,  278,  333,  278,  278,  556,  556,  556,  556,  // 42-51
         556,  556,  556,  556,  556,  556,  278,  278,  584,  584,  // 52-61
         584,  556, 1015,  667,  667,  722,  722,  667,  611,  778,  // 62-71
         722,  278,  500,  667,  556,  833,  722,  778,  667,  778,  // 72-81
         722,  667,  611,  722,  667,  944,  667,  667,  611,  278,  // 82-91
         278,  278,  469,  556,  222,  556,  556,  500,  556,  556,  // 92-101
         278,  556,  556,  222,  222,  500,  222,  833,  556,  556,  // 102-111
         556,  556,  333,  500,  278,  556,  500,  722,  500,  500,  // 112-121
         500,  334,  260,  334,  584,  // 122-126
    };

    /// <summary>Helvetica-BoldOblique</summary>
    private static readonly short[] HelveticaBoldOblique =
    {
         278,  333,  474,  556,  556,  889,  722,  278,  333,  333,  // 32-41
         389,  584,  278,  333,  278,  278,  556,  556,  556,  556,  // 42-51
         556,  556,  556,  556,  556,  556,  333,  333,  584,  584,  // 52-61
         584,  611,  975,  722,  722,  722,  722,  667,  611,  778,  // 62-71
         722,  278,  556,  722,  611,  833,  722,  778,  667,  778,  // 72-81
         722,  667,  611,  722,  667,  944,  667,  667,  611,  333,  // 82-91
         278,  333,  584,  556,  278,  556,  611,  556,  611,  556,  // 92-101
         333,  611,  611,  278,  278,  556,  278,  889,  611,  611,  // 102-111
         611,  611,  389,  556,  333,  611,  556,  778,  556,  556,  // 112-121
         500,  389,  280,  389,  584,  // 122-126
    };

    /// <summary>Courier (all four faces are monospaced at 600)</summary>
    private static readonly short[] Courier =
    {
         600,  600,  600,  600,  600,  600,  600,  600,  600,  600,  // 32-41
         600,  600,  600,  600,  600,  600,  600,  600,  600,  600,  // 42-51
         600,  600,  600,  600,  600,  600,  600,  600,  600,  600,  // 52-61
         600,  600,  600,  600,  600,  600,  600,  600,  600,  600,  // 62-71
         600,  600,  600,  600,  600,  600,  600,  600,  600,  600,  // 72-81
         600,  600,  600,  600,  600,  600,  600,  600,  600,  600,  // 82-91
         600,  600,  600,  600,  600,  600,  600,  600,  600,  600,  // 92-101
         600,  600,  600,  600,  600,  600,  600,  600,  600,  600,  // 102-111
         600,  600,  600,  600,  600,  600,  600,  600,  600,  600,  // 112-121
         600,  600,  600,  600,  600,  // 122-126
    };

    /// <summary>Symbol</summary>
    private static readonly short[] Symbol =
    {
         250,  333,  713,  500,  549,  833,  778,  439,  333,  333,  // 32-41
         500,  549,  250,  549,  250,  278,  500,  500,  500,  500,  // 42-51
         500,  500,  500,  500,  500,  500,  278,  278,  549,  549,  // 52-61
         549,  444,  549,  722,  667,  722,  612,  611,  763,  603,  // 62-71
         722,  333,  631,  722,  686,  889,  722,  722,  768,  741,  // 72-81
         556,  592,  611,  690,  439,  768,  645,  795,  611,  333,  // 82-91
         863,  333,  658,  500,  500,  631,  549,  549,  494,  439,  // 92-101
         521,  411,  603,  329,  603,  549,  549,  576,  521,  549,  // 102-111
         549,  521,  549,  603,  439,  576,  713,  686,  493,  686,  // 112-121
         494,  480,  200,  480,  549,  // 122-126
    };

    /// <summary>ZapfDingbats</summary>
    private static readonly short[] ZapfDingbats =
    {
         278,  974,  961,  974,  980,  719,  789,  790,  791,  690,  // 32-41
         960,  939,  549,  855,  911,  933,  911,  945,  974,  755,  // 42-51
         846,  762,  761,  571,  677,  763,  760,  759,  754,  494,  // 52-61
         552,  537,  577,  692,  786,  788,  788,  790,  793,  794,  // 62-71
         816,  823,  789,  841,  823,  833,  816,  831,  923,  744,  // 72-81
         723,  749,  790,  792,  695,  776,  768,  792,  759,  707,  // 82-91
         708,  682,  701,  826,  815,  789,  789,  707,  687,  696,  // 92-101
         689,  786,  787,  713,  791,  785,  791,  873,  761,  762,  // 102-111
         762,  759,  759,  892,  892,  788,  784,  438,  138,  277,  // 112-121
         415,  392,  392,  668,  668,  // 122-126
    };

    /// <summary>
    /// The advance for <paramref name="charCode"/> in <paramref name="baseFont"/>,
    /// or false when this class has nothing authoritative to say — an unknown
    /// family, or a code outside 32-126.
    /// </summary>
    public static bool TryGetWidth(string? baseFont, int charCode, out double width)
    {
        width = 0;
        if (baseFont == null || charCode < First || charCode > Last) return false;

        var table = TableFor(baseFont);
        if (table == null) return false;

        width = table[charCode - First];
        return true;
    }

    /// <summary>
    /// Match a /BaseFont to one of the 14. Handles the subset prefix (§9.6.4:
    /// six uppercase letters and a '+'), the "Arial,Bold" style suffix that
    /// Windows producers emit, and the Arial/TimesNewRoman aliases that are
    /// metrically substituted for Helvetica/Times in practice.
    /// </summary>
    private static short[]? TableFor(string baseFont)
    {
        var name = baseFont;
        if (name.Length > 7 && name[6] == '+') name = name[7..];

        var bold = name.Contains("Bold", StringComparison.OrdinalIgnoreCase);
        var italic = name.Contains("Italic", StringComparison.OrdinalIgnoreCase)
                  || name.Contains("Oblique", StringComparison.OrdinalIgnoreCase);

        // Order matters: check the exact-name families before the substring
        // ones, or "CourierNewPS-BoldMT" never reaches Courier.
        if (Has(name, "Courier") || Has(name, "Mono")) return Courier;

        if (Has(name, "Times") || Has(name, "Serif") && !Has(name, "SansSerif"))
            return (bold, italic) switch
            {
                (true, true) => TimesBoldItalic,
                (true, false) => TimesBold,
                (false, true) => TimesItalic,
                _ => TimesRoman,
            };

        if (Has(name, "Helvetica") || Has(name, "Arial"))
            return (bold, italic) switch
            {
                (true, true) => HelveticaBoldOblique,
                (true, false) => HelveticaBold,
                (false, true) => HelveticaOblique,
                _ => Helvetica,
            };

        if (Has(name, "ZapfDingbats") || Has(name, "Dingbats")) return ZapfDingbats;
        if (Has(name, "Symbol")) return Symbol;

        return null;
    }

    private static bool Has(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
