using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Excise.Rendering.Differential;

/// <summary>
/// Per-glyph positions from <c>mutool draw -F stext</c> — an independent,
/// FONT-AGNOSTIC oracle for where each character sits on the page.
///
/// <para>Every other text oracle here answers "what characters are on this
/// page". This answers "and where", which is the quantity a font-metrics
/// defect corrupts and a text comparison cannot see: #1100 produced output
/// whose content stream held every character and whose line ran off the page
/// edge, and text-presence checks called it clean.</para>
///
/// <para>Two consumers: #1104's advance-parity gate, and the redaction
/// benchmark's residue tier — deciding whether a redaction left a gap the
/// width of what it removed, or closed the layout up.</para>
///
/// <para>Never throws: returns null when mutool is unavailable or refuses,
/// matching <see cref="MutoolTextExtractor"/>. Null is "no answer", not
/// "no glyphs".</para>
/// </summary>
internal static class MutoolGlyphPositions
{
    /// <summary>One glyph, at the position mutool places it.</summary>
    public readonly record struct Glyph(string Char, double X, double Y);

    // mutool emits a <char> element with x, y, and c attributes. Attribute
    // order is not part of the stext contract: MuPDF 1.26 emits c before x
    // and y, while older versions put x/y first. Parse the tag then its named
    // attributes so a tool upgrade cannot silently turn corroboration off.
    private static readonly Regex CharTagRe = new(
        "<char\\b(?<attributes>[^>]*)>", RegexOptions.Compiled);
    private static readonly Regex AttributeRe = new(
        "\\b(?<name>x|y|c)=\"(?<value>[^\"]*)\"", RegexOptions.Compiled);

    /// <summary>
    /// Glyph positions for one page (1-based), in mutool's emission order.
    /// Null when mutool is unavailable or refuses.
    /// </summary>
    public static IReadOnlyList<Glyph>? ExtractPage(string pdfPath, int pageNumber,
                                                    string? password = null,
                                                    int timeoutMs = 60_000)
    {
        if (!MutoolReferenceRenderer.IsAvailable) return null;

        var outPath = Path.Combine(Path.GetTempPath(), $"excise-stext-{Guid.NewGuid():N}.xml");
        var args = new List<string> { "draw" };
        if (!string.IsNullOrEmpty(password))
        {
            args.Add("-p");
            args.Add(password);
        }
        args.AddRange(new[] { "-o", outPath, "-F", "stext", pdfPath, pageNumber.ToString(CultureInfo.InvariantCulture) });
        var xml = ReferenceProcess.RunToTextFile("mutool", args, outPath, timeoutMs);
        if (xml == null) return null;

        var glyphs = new List<Glyph>();
        foreach (Match tag in CharTagRe.Matches(xml))
        {
            var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match attribute in AttributeRe.Matches(tag.Groups["attributes"].Value))
                attributes[attribute.Groups["name"].Value] = attribute.Groups["value"].Value;

            if (attributes.TryGetValue("x", out var xText) &&
                attributes.TryGetValue("y", out var yText) &&
                attributes.TryGetValue("c", out var character) &&
                double.TryParse(xText, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                double.TryParse(yText, NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                glyphs.Add(new Glyph(character, x, y));
        }
        return glyphs;
    }

    /// <summary>
    /// Did the surviving text stay where it was?
    ///
    /// <para>Redaction can remove glyphs and either LEAVE THE GAP or close the
    /// layout up. Leaving it preserves the page's appearance and preserves the
    /// width of what was removed — a channel that constrains the missing
    /// string without containing it. Closing up destroys that channel and the
    /// layout with it.</para>
    ///
    /// <para>Detected by comparing the positions of glyphs that survived: if
    /// the text following the removal did not shift, the gap is still there.
    /// Compares the rightmost inked x on the page, which moves when a line
    /// reflows and does not when a hole is punched in it.</para>
    ///
    /// <para>Returns null when either side could not be read.</para>
    /// </summary>
    public static bool? LayoutGapPreserved(IReadOnlyList<Glyph>? before,
                                           IReadOnlyList<Glyph>? after,
                                           double tolerancePt = 1.0)
    {
        if (before == null || after == null) return null;
        if (before.Count == 0 || after.Count == 0) return null;
        if (before.Count == after.Count) return null;   // nothing removed here

        var maxBefore = double.MinValue;
        foreach (var g in before) if (g.X > maxBefore) maxBefore = g.X;
        var maxAfter = double.MinValue;
        foreach (var g in after) if (g.X > maxAfter) maxAfter = g.X;

        return Math.Abs(maxBefore - maxAfter) <= tolerancePt;
    }
}
