using System;
using System.Collections.Generic;
using System.Diagnostics;
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
public static class MutoolGlyphPositions
{
    /// <summary>One glyph, at the position mutool places it.</summary>
    public readonly record struct Glyph(string Char, double X, double Y);

    // mutool emits: <char quad="..." x="36" y="3.93" ... c="M"/>
    // Attribute order is stable across the versions we use, but matching by
    // name rather than position keeps this from breaking silently if it moves.
    private static readonly Regex CharRe = new(
        "<char[^>]*?\\bx=\"(?<x>[-0-9.]+)\"[^>]*?\\by=\"(?<y>[-0-9.]+)\"[^>]*?\\bc=\"(?<c>[^\"]*)\"",
        RegexOptions.Compiled);

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
        try
        {
            var psi = new ProcessStartInfo("mutool")
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            psi.ArgumentList.Add("draw");
            if (!string.IsNullOrEmpty(password)) { psi.ArgumentList.Add("-p"); psi.ArgumentList.Add(password); }
            psi.ArgumentList.Add("-o"); psi.ArgumentList.Add(outPath);
            psi.ArgumentList.Add("-F"); psi.ArgumentList.Add("stext");
            psi.ArgumentList.Add(pdfPath);
            psi.ArgumentList.Add(pageNumber.ToString(CultureInfo.InvariantCulture));

            using var p = Process.Start(psi);
            if (p == null) return null;
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(entireProcessTree: true); } catch { } return null; }
            if (p.ExitCode != 0 || !File.Exists(outPath)) return null;

            var glyphs = new List<Glyph>();
            foreach (Match m in CharRe.Matches(File.ReadAllText(outPath)))
            {
                if (double.TryParse(m.Groups["x"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                    double.TryParse(m.Groups["y"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                    glyphs.Add(new Glyph(m.Groups["c"].Value, x, y));
            }
            return glyphs;
        }
        catch { return null; }
        finally { try { if (File.Exists(outPath)) File.Delete(outPath); } catch { } }
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
