using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Excise.Core.Tests.Content;

namespace Excise.TestSupport;

/// <summary>
/// #1854: one-page PDFs whose marked-content property list holds a text
/// carrier over glyphs that do NOT spell it, in every place a property list can
/// sit: inline on the page (<c>/ActualText</c>, <c>/Alt</c>, <c>/E</c>, a
/// <c>DP</c> point), by NAME through <c>/Properties</c> (alone, shared by two
/// spans, holding an indirect string), around a form <c>Do</c>, and inside every
/// other kind of content stream: a form XObject, an annotation appearance, a
/// tiling pattern and a Type 3 glyph procedure.
/// </summary>
/// <remarks>
/// The page paints <see cref="Painted"/> and nothing else, so a redaction that
/// finds the term in the glyphs cannot reach the carrier by enclosure or by the
/// removed-text match: only a scrub by TERM can.
/// </remarks>
internal static class MarkedContentCarrierFixtures
{
    /// <summary>What every fixture paints. It never contains a carrier's term.</summary>
    public const string Painted = "Public line";

    private const string Text = $"BT /F1 12 Tf 72 700 Td ({Painted}) Tj ET";

    /// <summary>
    /// Each placement, given the carrier value as a PDF string token
    /// (<see cref="Literal"/>, <see cref="Utf16Hex"/>, <see cref="Utf16Octal"/>).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, Func<string, byte[]>> Placements =
        new Dictionary<string, Func<string, byte[]>>
        {
            ["inline /ActualText"] = v => ContentStreamFixture.Build(Span(v)),
            ["inline /Alt"] = v => ContentStreamFixture.Build(Span(v, "Alt")),
            ["inline /E"] = v => ContentStreamFixture.Build(Span(v, "E")),
            ["DP point"] = v => ContentStreamFixture.Build($"/Span << /ActualText {v} >> DP {Text}"),
            ["named"] = v => ContentStreamFixture.Build($"/Span /P1 BDC {Text} EMC",
                extraObjects: $"6 0 obj\n<< /ActualText {v} >>\nendobj\n",
                extraResources: "/Properties << /P1 6 0 R >>"),
            ["named, shared by two spans"] = v => ContentStreamFixture.Build(
                $"/Span /P1 BDC {Text} EMC /Span /P1 BDC BT /F1 12 Tf 72 600 Td (Second line) Tj ET EMC",
                extraObjects: $"6 0 obj\n<< /ActualText {v} >>\nendobj\n",
                extraResources: "/Properties << /P1 6 0 R >>"),
            ["named, indirect string"] = v => ContentStreamFixture.Build($"/Span /P1 BDC {Text} EMC",
                extraObjects: "6 0 obj\n<< /ActualText 7 0 R >>\nendobj\n" + $"7 0 obj\n{v}\nendobj\n",
                extraResources: "/Properties << /P1 6 0 R >>"),
            ["span around a form Do"] = v => ContentStreamFixture.Build($"/Span << /ActualText {v} >> BDC /Fm0 Do EMC",
                extraObjects: Form(6, Text),
                extraResources: "/XObject << /Fm0 6 0 R >>"),
            ["form XObject"] = v => ContentStreamFixture.Build("/Fm0 Do",
                extraObjects: Form(6, Span(v)),
                extraResources: "/XObject << /Fm0 6 0 R >>"),
            ["annotation appearance"] = v => ContentStreamFixture.Build(Text,
                extraObjects: "6 0 obj\n<< /Type /Annot /Subtype /Square /Rect [72 500 272 520] /AP << /N 7 0 R >> >>\nendobj\n"
                    + Form(7, Span(v)),
                extraPageEntries: "/Annots [6 0 R]"),
            ["tiling pattern"] = v => ContentStreamFixture.Build($"{Text} /Pattern cs /P0 scn 72 500 100 50 re f",
                extraObjects: Stream(6,
                    "/PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 10 10] /XStep 10 /YStep 10 /Resources << >>",
                    $"/Span << /ActualText {v} >> BDC 0 0 5 5 re f EMC"),
                extraResources: "/Pattern << /P0 6 0 R >>"),
            ["Type 3 glyph procedure"] = v => ContentStreamFixture.Build($"{Text} BT /F2 12 Tf 72 650 Td (a) Tj ET",
                extraObjects: "6 0 obj\n<< /Type /Font /Subtype /Type3 /FontBBox [0 0 1000 1000] "
                    + "/FontMatrix [0.001 0 0 0.001 0 0] /CharProcs << /a 7 0 R >> "
                    + "/Encoding << /Type /Encoding /Differences [97 /a] >> /FirstChar 97 /LastChar 97 "
                    + "/Widths [1000] /Resources << >> >>\nendobj\n"
                    + Stream(7, "", $"1000 0 d0 /Span << /ActualText {v} >> BDC 0 0 1000 1000 re f EMC"),
                extraFontResources: "/F2 6 0 R"),
        };

    /// <summary>The one-page inline <c>/ActualText</c> fixture of the issue.</summary>
    public static byte[] Inline(string value) => Placements["inline /ActualText"](value);

    public static string Literal(string text) => $"({text})";

    public static string Utf16Hex(string text) =>
        "<FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(text)) + ">";

    public static string Utf16Octal(string text) =>
        @"(\376\377" + string.Concat(Encoding.BigEndianUnicode.GetBytes(text)
            .Select(b => "\\" + Convert.ToString(b, 8).PadLeft(3, '0'))) + ")";

    private static string Span(string value, string key = "ActualText") =>
        $"/Span << /{key} {value} >> BDC {Text} EMC";

    private static string Form(int number, string content) =>
        Stream(number, "/Type /XObject /Subtype /Form /BBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >>", content);

    private static string Stream(int number, string entries, string content) =>
        $"{number} 0 obj\n<< {entries} /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}\nendstream\nendobj\n";
}
