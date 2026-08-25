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
/// <para><b>The 32-126 fast path.</b> The code-indexed tables above cover codes
/// 32-126, very nearly the range where StandardEncoding, WinAnsiEncoding and
/// MacRomanEncoding agree, so a width is correct there without resolving the
/// font's /Encoding. (Two codes, 39 and 96, actually diverge — WinAnsi's
/// quotesingle/grave vs the Standard quoteright/quoteleft these tables encode —
/// but that is a glyph-identity choice baked into the checked-in values, not a
/// width error, and <see cref="TryGetWidth"/> keeps it byte-for-byte.)</para>
///
/// <para><b>Above 126 (#1106).</b> The encodings genuinely diverge — code 0xE9
/// is eacute in WinAnsi and something else in StandardEncoding — so a width
/// there requires a glyph name. <see cref="TryGetWidth"/> now resolves codes
/// 128-255 through WinAnsiEncoding (the standard-14 default this cascade already
/// assumes elsewhere) to a glyph name, then to a width from the
/// <see cref="UpperGlyphWidths"/> AFM table, killing the last silently-guessed
/// range in the non-embedded standard-14 path. <see cref="TryGetWidthByGlyphName"/>
/// exposes the name→width step directly, which is the seam an /Encoding
/// /Differences remap resolves through once a caller passes the resolved name.
/// Symbol and ZapfDingbats use their own encodings, so their upper range still
/// fails closed rather than being read through WinAnsi names.</para>
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

    // glyph name -> advance (1000ths of em) for the eight Latin standard-14
    // faces, in this column order:
    //   0 Times-Roman 1 Times-Bold 2 Times-Italic 3 Times-BoldItalic
    //   4 Helvetica 5 Helvetica-Bold 6 Helvetica-Oblique 7 Helvetica-BoldOblique
    // Values verbatim from the Adobe Core-14 AFMs (StartFontMetrics 4.1,
    // (c) Adobe 1985-1997), the same provenance as the code tables above.
    // Covers exactly the 123 glyph names reachable through WinAnsiEncoding
    // codes 128-255 (#1106). Courier is monospaced (600) and handled without
    // a table; Symbol/ZapfDingbats use their own encodings and fail closed.
    private static readonly Dictionary<string, short[]> UpperGlyphWidths = new()
    {
        { "AE", new short[] {  889, 1000,  889,  944, 1000, 1000, 1000, 1000 } },
        { "Aacute", new short[] {  722,  722,  611,  667,  667,  722,  667,  722 } },
        { "Acircumflex", new short[] {  722,  722,  611,  667,  667,  722,  667,  722 } },
        { "Adieresis", new short[] {  722,  722,  611,  667,  667,  722,  667,  722 } },
        { "Agrave", new short[] {  722,  722,  611,  667,  667,  722,  667,  722 } },
        { "Aring", new short[] {  722,  722,  611,  667,  667,  722,  667,  722 } },
        { "Atilde", new short[] {  722,  722,  611,  667,  667,  722,  667,  722 } },
        { "Ccedilla", new short[] {  667,  722,  667,  667,  722,  722,  722,  722 } },
        { "Eacute", new short[] {  611,  667,  611,  667,  667,  667,  667,  667 } },
        { "Ecircumflex", new short[] {  611,  667,  611,  667,  667,  667,  667,  667 } },
        { "Edieresis", new short[] {  611,  667,  611,  667,  667,  667,  667,  667 } },
        { "Egrave", new short[] {  611,  667,  611,  667,  667,  667,  667,  667 } },
        { "Eth", new short[] {  722,  722,  722,  722,  722,  722,  722,  722 } },
        { "Euro", new short[] {  500,  500,  500,  500,  556,  556,  556,  556 } },
        { "Iacute", new short[] {  333,  389,  333,  389,  278,  278,  278,  278 } },
        { "Icircumflex", new short[] {  333,  389,  333,  389,  278,  278,  278,  278 } },
        { "Idieresis", new short[] {  333,  389,  333,  389,  278,  278,  278,  278 } },
        { "Igrave", new short[] {  333,  389,  333,  389,  278,  278,  278,  278 } },
        { "Ntilde", new short[] {  722,  722,  667,  722,  722,  722,  722,  722 } },
        { "OE", new short[] {  889, 1000,  944,  944, 1000, 1000, 1000, 1000 } },
        { "Oacute", new short[] {  722,  778,  722,  722,  778,  778,  778,  778 } },
        { "Ocircumflex", new short[] {  722,  778,  722,  722,  778,  778,  778,  778 } },
        { "Odieresis", new short[] {  722,  778,  722,  722,  778,  778,  778,  778 } },
        { "Ograve", new short[] {  722,  778,  722,  722,  778,  778,  778,  778 } },
        { "Oslash", new short[] {  722,  778,  722,  722,  778,  778,  778,  778 } },
        { "Otilde", new short[] {  722,  778,  722,  722,  778,  778,  778,  778 } },
        { "Scaron", new short[] {  556,  556,  500,  556,  667,  667,  667,  667 } },
        { "Thorn", new short[] {  556,  611,  611,  611,  667,  667,  667,  667 } },
        { "Uacute", new short[] {  722,  722,  722,  722,  722,  722,  722,  722 } },
        { "Ucircumflex", new short[] {  722,  722,  722,  722,  722,  722,  722,  722 } },
        { "Udieresis", new short[] {  722,  722,  722,  722,  722,  722,  722,  722 } },
        { "Ugrave", new short[] {  722,  722,  722,  722,  722,  722,  722,  722 } },
        { "Yacute", new short[] {  722,  722,  556,  611,  667,  667,  667,  667 } },
        { "Ydieresis", new short[] {  722,  722,  556,  611,  667,  667,  667,  667 } },
        { "Zcaron", new short[] {  611,  667,  556,  611,  611,  611,  611,  611 } },
        { "aacute", new short[] {  444,  500,  500,  500,  556,  556,  556,  556 } },
        { "acircumflex", new short[] {  444,  500,  500,  500,  556,  556,  556,  556 } },
        { "acute", new short[] {  333,  333,  333,  333,  333,  333,  333,  333 } },
        { "adieresis", new short[] {  444,  500,  500,  500,  556,  556,  556,  556 } },
        { "ae", new short[] {  667,  722,  667,  722,  889,  889,  889,  889 } },
        { "agrave", new short[] {  444,  500,  500,  500,  556,  556,  556,  556 } },
        { "aring", new short[] {  444,  500,  500,  500,  556,  556,  556,  556 } },
        { "atilde", new short[] {  444,  500,  500,  500,  556,  556,  556,  556 } },
        { "brokenbar", new short[] {  200,  220,  275,  220,  260,  280,  260,  280 } },
        { "bullet", new short[] {  350,  350,  350,  350,  350,  350,  350,  350 } },
        { "ccedilla", new short[] {  444,  444,  444,  444,  500,  556,  500,  556 } },
        { "cedilla", new short[] {  333,  333,  333,  333,  333,  333,  333,  333 } },
        { "cent", new short[] {  500,  500,  500,  500,  556,  556,  556,  556 } },
        { "circumflex", new short[] {  333,  333,  333,  333,  333,  333,  333,  333 } },
        { "copyright", new short[] {  760,  747,  760,  747,  737,  737,  737,  737 } },
        { "currency", new short[] {  500,  500,  500,  500,  556,  556,  556,  556 } },
        { "dagger", new short[] {  500,  500,  500,  500,  556,  556,  556,  556 } },
        { "daggerdbl", new short[] {  500,  500,  500,  500,  556,  556,  556,  556 } },
        { "degree", new short[] {  400,  400,  400,  400,  400,  400,  400,  400 } },
        { "dieresis", new short[] {  333,  333,  333,  333,  333,  333,  333,  333 } },
        { "divide", new short[] {  564,  570,  675,  570,  584,  584,  584,  584 } },
        { "eacute", new short[] {  444,  444,  444,  444,  556,  556,  556,  556 } },
        { "ecircumflex", new short[] {  444,  444,  444,  444,  556,  556,  556,  556 } },
        { "edieresis", new short[] {  444,  444,  444,  444,  556,  556,  556,  556 } },
        { "egrave", new short[] {  444,  444,  444,  444,  556,  556,  556,  556 } },
        { "ellipsis", new short[] { 1000, 1000,  889, 1000, 1000, 1000, 1000, 1000 } },
        { "emdash", new short[] { 1000, 1000,  889, 1000, 1000, 1000, 1000, 1000 } },
        { "endash", new short[] {  500,  500,  500,  500,  556,  556,  556,  556 } },
        { "eth", new short[] {  500,  500,  500,  500,  556,  611,  556,  611 } },
        { "exclamdown", new short[] {  333,  333,  389,  389,  333,  333,  333,  333 } },
        { "florin", new short[] {  500,  500,  500,  500,  556,  556,  556,  556 } },
        { "germandbls", new short[] {  500,  556,  500,  500,  611,  611,  611,  611 } },
        { "guillemotleft", new short[] {  500,  500,  500,  500,  556,  556,  556,  556 } },
        { "guillemotright", new short[] {  500,  500,  500,  500,  556,  556,  556,  556 } },
        { "guilsinglleft", new short[] {  333,  333,  333,  333,  333,  333,  333,  333 } },
        { "guilsinglright", new short[] {  333,  333,  333,  333,  333,  333,  333,  333 } },
        { "hyphen", new short[] {  333,  333,  333,  333,  333,  333,  333,  333 } },
        { "iacute", new short[] {  278,  278,  278,  278,  278,  278,  278,  278 } },
        { "icircumflex", new short[] {  278,  278,  278,  278,  278,  278,  278,  278 } },
        { "idieresis", new short[] {  278,  278,  278,  278,  278,  278,  278,  278 } },
        { "igrave", new short[] {  278,  278,  278,  278,  278,  278,  278,  278 } },
        { "logicalnot", new short[] {  564,  570,  675,  606,  584,  584,  584,  584 } },
        { "macron", new short[] {  333,  333,  333,  333,  333,  333,  333,  333 } },
        { "mu", new short[] {  500,  556,  500,  576,  556,  611,  556,  611 } },
        { "multiply", new short[] {  564,  570,  675,  570,  584,  584,  584,  584 } },
        { "ntilde", new short[] {  500,  556,  500,  556,  556,  611,  556,  611 } },
        { "oacute", new short[] {  500,  500,  500,  500,  556,  611,  556,  611 } },
        { "ocircumflex", new short[] {  500,  500,  500,  500,  556,  611,  556,  611 } },
        { "odieresis", new short[] {  500,  500,  500,  500,  556,  611,  556,  611 } },
        { "oe", new short[] {  722,  722,  667,  722,  944,  944,  944,  944 } },
        { "ograve", new short[] {  500,  500,  500,  500,  556,  611,  556,  611 } },
        { "onehalf", new short[] {  750,  750,  750,  750,  834,  834,  834,  834 } },
        { "onequarter", new short[] {  750,  750,  750,  750,  834,  834,  834,  834 } },
        { "onesuperior", new short[] {  300,  300,  300,  300,  333,  333,  333,  333 } },
        { "ordfeminine", new short[] {  276,  300,  276,  266,  370,  370,  370,  370 } },
        { "ordmasculine", new short[] {  310,  330,  310,  300,  365,  365,  365,  365 } },
        { "oslash", new short[] {  500,  500,  500,  500,  611,  611,  611,  611 } },
        { "otilde", new short[] {  500,  500,  500,  500,  556,  611,  556,  611 } },
        { "paragraph", new short[] {  453,  540,  523,  500,  537,  556,  537,  556 } },
        { "periodcentered", new short[] {  250,  250,  250,  250,  278,  278,  278,  278 } },
        { "perthousand", new short[] { 1000, 1000, 1000, 1000, 1000, 1000, 1000, 1000 } },
        { "plusminus", new short[] {  564,  570,  675,  570,  584,  584,  584,  584 } },
        { "questiondown", new short[] {  444,  500,  500,  500,  611,  611,  611,  611 } },
        { "quotedblbase", new short[] {  444,  500,  556,  500,  333,  500,  333,  500 } },
        { "quotedblleft", new short[] {  444,  500,  556,  500,  333,  500,  333,  500 } },
        { "quotedblright", new short[] {  444,  500,  556,  500,  333,  500,  333,  500 } },
        { "quoteleft", new short[] {  333,  333,  333,  333,  222,  278,  222,  278 } },
        { "quoteright", new short[] {  333,  333,  333,  333,  222,  278,  222,  278 } },
        { "quotesinglbase", new short[] {  333,  333,  333,  333,  222,  278,  222,  278 } },
        { "registered", new short[] {  760,  747,  760,  747,  737,  737,  737,  737 } },
        { "scaron", new short[] {  389,  389,  389,  389,  500,  556,  500,  556 } },
        { "section", new short[] {  500,  500,  500,  500,  556,  556,  556,  556 } },
        { "space", new short[] {  250,  250,  250,  250,  278,  278,  278,  278 } },
        { "sterling", new short[] {  500,  500,  500,  500,  556,  556,  556,  556 } },
        { "thorn", new short[] {  500,  556,  500,  500,  556,  611,  556,  611 } },
        { "threequarters", new short[] {  750,  750,  750,  750,  834,  834,  834,  834 } },
        { "threesuperior", new short[] {  300,  300,  300,  300,  333,  333,  333,  333 } },
        { "tilde", new short[] {  333,  333,  333,  333,  333,  333,  333,  333 } },
        { "trademark", new short[] {  980, 1000,  980, 1000, 1000, 1000, 1000, 1000 } },
        { "twosuperior", new short[] {  300,  300,  300,  300,  333,  333,  333,  333 } },
        { "uacute", new short[] {  500,  556,  500,  556,  556,  611,  556,  611 } },
        { "ucircumflex", new short[] {  500,  556,  500,  556,  556,  611,  556,  611 } },
        { "udieresis", new short[] {  500,  556,  500,  556,  556,  611,  556,  611 } },
        { "ugrave", new short[] {  500,  556,  500,  556,  556,  611,  556,  611 } },
        { "yacute", new short[] {  500,  500,  444,  444,  500,  556,  500,  556 } },
        { "ydieresis", new short[] {  500,  500,  444,  444,  500,  556,  500,  556 } },
        { "yen", new short[] {  500,  500,  500,  500,  556,  556,  556,  556 } },
        { "zcaron", new short[] {  444,  444,  389,  389,  500,  500,  500,  500 } },
    };

    // WinAnsiEncoding (CP1252) character code -> glyph name for codes 128-255
    // (PDF 32000-1 Annex D.2). 0xA0->space and 0xAD->hyphen per Adobe's
    // definition; codes 127/129/141/143/144/157 are unassigned and absent,
    // so a width for them fails closed rather than guessing.
    private static readonly Dictionary<int, string> WinAnsiUpper = new()
    {
        { 128, "Euro" }, { 130, "quotesinglbase" }, { 131, "florin" }, { 132, "quotedblbase" },
        { 133, "ellipsis" }, { 134, "dagger" }, { 135, "daggerdbl" }, { 136, "circumflex" },
        { 137, "perthousand" }, { 138, "Scaron" }, { 139, "guilsinglleft" }, { 140, "OE" },
        { 142, "Zcaron" }, { 145, "quoteleft" }, { 146, "quoteright" }, { 147, "quotedblleft" },
        { 148, "quotedblright" }, { 149, "bullet" }, { 150, "endash" }, { 151, "emdash" },
        { 152, "tilde" }, { 153, "trademark" }, { 154, "scaron" }, { 155, "guilsinglright" },
        { 156, "oe" }, { 158, "zcaron" }, { 159, "Ydieresis" }, { 160, "space" },
        { 161, "exclamdown" }, { 162, "cent" }, { 163, "sterling" }, { 164, "currency" },
        { 165, "yen" }, { 166, "brokenbar" }, { 167, "section" }, { 168, "dieresis" },
        { 169, "copyright" }, { 170, "ordfeminine" }, { 171, "guillemotleft" }, { 172, "logicalnot" },
        { 173, "hyphen" }, { 174, "registered" }, { 175, "macron" }, { 176, "degree" },
        { 177, "plusminus" }, { 178, "twosuperior" }, { 179, "threesuperior" }, { 180, "acute" },
        { 181, "mu" }, { 182, "paragraph" }, { 183, "periodcentered" }, { 184, "cedilla" },
        { 185, "onesuperior" }, { 186, "ordmasculine" }, { 187, "guillemotright" }, { 188, "onequarter" },
        { 189, "onehalf" }, { 190, "threequarters" }, { 191, "questiondown" }, { 192, "Agrave" },
        { 193, "Aacute" }, { 194, "Acircumflex" }, { 195, "Atilde" }, { 196, "Adieresis" },
        { 197, "Aring" }, { 198, "AE" }, { 199, "Ccedilla" }, { 200, "Egrave" },
        { 201, "Eacute" }, { 202, "Ecircumflex" }, { 203, "Edieresis" }, { 204, "Igrave" },
        { 205, "Iacute" }, { 206, "Icircumflex" }, { 207, "Idieresis" }, { 208, "Eth" },
        { 209, "Ntilde" }, { 210, "Ograve" }, { 211, "Oacute" }, { 212, "Ocircumflex" },
        { 213, "Otilde" }, { 214, "Odieresis" }, { 215, "multiply" }, { 216, "Oslash" },
        { 217, "Ugrave" }, { 218, "Uacute" }, { 219, "Ucircumflex" }, { 220, "Udieresis" },
        { 221, "Yacute" }, { 222, "Thorn" }, { 223, "germandbls" }, { 224, "agrave" },
        { 225, "aacute" }, { 226, "acircumflex" }, { 227, "atilde" }, { 228, "adieresis" },
        { 229, "aring" }, { 230, "ae" }, { 231, "ccedilla" }, { 232, "egrave" },
        { 233, "eacute" }, { 234, "ecircumflex" }, { 235, "edieresis" }, { 236, "igrave" },
        { 237, "iacute" }, { 238, "icircumflex" }, { 239, "idieresis" }, { 240, "eth" },
        { 241, "ntilde" }, { 242, "ograve" }, { 243, "oacute" }, { 244, "ocircumflex" },
        { 245, "otilde" }, { 246, "odieresis" }, { 247, "divide" }, { 248, "oslash" },
        { 249, "ugrave" }, { 250, "uacute" }, { 251, "ucircumflex" }, { 252, "udieresis" },
        { 253, "yacute" }, { 254, "thorn" }, { 255, "ydieresis" },
    };

    /// <summary>
    /// The advance for <paramref name="charCode"/> in <paramref name="baseFont"/>,
    /// or false when this class has nothing authoritative to say — an unknown
    /// family, or a code this class cannot resolve to a glyph name.
    /// </summary>
    public static bool TryGetWidth(string? baseFont, int charCode, out double width)
    {
        width = 0;
        if (baseFont == null) return false;

        // 32-126 fast path — the code-indexed tables, byte-for-byte as before.
        // Deliberately NOT routed through the WinAnsi name lookup below: codes
        // 39 and 96 carry the Standard quoteright/quoteleft glyphs in these
        // tables, and re-resolving them as WinAnsi quotesingle/grave would
        // silently change two long-pinned values (#1106).
        if (charCode >= First && charCode <= Last)
        {
            var table = TableFor(baseFont);
            if (table == null) return false;
            width = table[charCode - First];
            return true;
        }

        // 128-255 (#1106): the encodings diverge, so resolve the code to a glyph
        // name through WinAnsiEncoding (the standard-14 default), then the name
        // to a width. Codes 127 and the unassigned WinAnsi slots have no name
        // and fail closed.
        if (charCode > Last && WinAnsiUpper.TryGetValue(charCode, out var glyphName))
            return TryGetWidthByGlyphName(baseFont, glyphName, out width);

        return false;
    }

    /// <summary>
    /// The advance for a glyph named <paramref name="glyphName"/> in
    /// <paramref name="baseFont"/>, from the Adobe AFM metrics — the name→width
    /// step an /Encoding /Differences remap resolves through. True only for the
    /// Latin standard-14 faces (and Courier, monospaced 600) on a glyph name the
    /// AFM covers; Symbol, ZapfDingbats and unknown families fail closed rather
    /// than guess.
    /// </summary>
    public static bool TryGetWidthByGlyphName(string? baseFont, string? glyphName, out double width)
    {
        width = 0;
        if (baseFont == null || glyphName == null) return false;

        var name = StripSubsetPrefix(baseFont);

        // Courier: monospaced, every glyph advances 600 — but only vouch for a
        // glyph name the standard-14 metrics actually recognise, so garbage
        // still fails closed.
        if (Has(name, "Courier") || Has(name, "Mono"))
        {
            if (UpperGlyphWidths.ContainsKey(glyphName))
            {
                width = 600;
                return true;
            }
            return false;
        }

        var faceIndex = LatinFaceIndex(name);
        if (faceIndex < 0) return false; // Symbol, ZapfDingbats, unknown

        if (UpperGlyphWidths.TryGetValue(glyphName, out var widths))
        {
            width = widths[faceIndex];
            return true;
        }
        return false;
    }

    private static string StripSubsetPrefix(string baseFont) =>
        baseFont.Length > 7 && baseFont[6] == '+' ? baseFont[7..] : baseFont;

    /// <summary>
    /// Column index into <see cref="UpperGlyphWidths"/> for a Latin standard-14
    /// face, or -1 for Courier / Symbol / ZapfDingbats / unknown. Mirrors the
    /// family and style resolution in <see cref="TableFor"/>.
    /// </summary>
    private static int LatinFaceIndex(string name)
    {
        var bold = name.Contains("Bold", StringComparison.OrdinalIgnoreCase);
        var italic = name.Contains("Italic", StringComparison.OrdinalIgnoreCase)
                  || name.Contains("Oblique", StringComparison.OrdinalIgnoreCase);

        if (Has(name, "Times") || Has(name, "Serif") && !Has(name, "SansSerif"))
            return (bold, italic) switch
            {
                (true, true) => 3,   // Times-BoldItalic
                (true, false) => 1,  // Times-Bold
                (false, true) => 2,  // Times-Italic
                _ => 0,              // Times-Roman
            };

        if (Has(name, "Helvetica") || Has(name, "Arial"))
            return (bold, italic) switch
            {
                (true, true) => 7,   // Helvetica-BoldOblique
                (true, false) => 5,  // Helvetica-Bold
                (false, true) => 6,  // Helvetica-Oblique
                _ => 4,              // Helvetica
            };

        return -1;
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
