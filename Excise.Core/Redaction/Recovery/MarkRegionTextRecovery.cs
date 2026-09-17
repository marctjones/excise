using System;
using System.Collections.Generic;
using System.Linq;
using Excise.Core.Document;

namespace Excise.Core.Redaction.Recovery;

/// <summary>
/// #1606/#1592 — text still sitting inside a redaction MARK, found by reading
/// the page rather than by watching what was drawn over what.
///
/// <para><b>The gap this closes.</b> Every other text channel reasons about
/// draw order or contrast: something was painted over the glyphs, or in a
/// colour too close to them. That misses two common shapes entirely —
/// <list type="bullet">
///   <item>a <c>/Redact</c> annotation that was never applied, where the text
///   is simply still there and fully visible, and</item>
///   <item>a covering box drawn as an ANNOTATION rather than as page content,
///   which the content-stream walk never sees.</item>
/// </list>
/// In both cases a mark IS detected, so the report showed a redaction and
/// graded it <c>not-recovered</c> — which reads as <i>this redaction held</i>
/// while the text underneath is trivially extractable. Silence would have been
/// better than that.</para>
///
/// <para><b>Why it is restricted to ANNOTATION marks.</b> A content-stream
/// filled box over text is already the hidden-text channel's job, and reporting
/// it here too would double every ordinary finding. An annotation mark is the
/// case no other channel can reach, and it carries its own statement of intent:
/// §12.5.6.23 says a <c>/Redact</c> annotation marks a region intended for
/// redaction, so text inside one is unambiguously material somebody meant to
/// remove.</para>
///
/// <para><b>Confidence is Certain.</b> The glyphs are in the content stream and
/// any extractor reads them. Nothing here is estimated.</para>
/// </summary>
public static class MarkRegionTextRecovery
{
    /// <summary>A glyph's centre must fall inside the mark for its letter to count.</summary>
    private const double EdgeTolerancePt = 0.5;

    /// <param name="MarkId">The mark this text was found inside.</param>
    public readonly record struct MarkRegionText(
        int PageNumber, string MarkId, string Kind, string Text, PdfRectangle Rect);

    /// <summary>
    /// Text inside each annotation-derived mark in <paramref name="marks"/>.
    /// Marks are passed in rather than re-detected so the ids match the report's.
    /// </summary>
    public static IReadOnlyList<MarkRegionText> Scan(
        PdfDocument document, IReadOnlyList<RedactionMark> marks)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(marks);

        var found = new List<MarkRegionText>();
        foreach (var byPage in marks
                     .Where(m => m.Kind is RedactionMarkKind.RedactAnnotation
                                        or RedactionMarkKind.ShapeAnnotation)
                     .GroupBy(m => m.PageNumber))
        {
            IReadOnlyList<Text.Letter> letters;
            try { letters = document.GetPage(byPage.Key).Letters; }
            catch { continue; }
            if (letters.Count == 0) continue;

            foreach (var mark in byPage)
            {
                var inside = letters.Where(l => CentreInside(l.GlyphRectangle, mark.Rect)).ToList();
                if (inside.Count == 0) continue;

                // One finding per baseline, so a multi-line mark reports lines
                // rather than one run with the lines concatenated out of order.
                foreach (var line in inside
                             .GroupBy(l => Math.Round(l.GlyphRectangle.Bottom, 0))
                             .OrderByDescending(g => g.Key))
                {
                    var ordered = line.OrderBy(l => l.GlyphRectangle.Left).ToList();
                    var text = string.Concat(ordered.Select(l => l.Value));
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    found.Add(new MarkRegionText(
                        byPage.Key, mark.Id, DescribeKind(mark.Kind), text,
                        new PdfRectangle(
                            ordered.Min(l => l.GlyphRectangle.Left),
                            ordered.Min(l => l.GlyphRectangle.Bottom),
                            ordered.Max(l => l.GlyphRectangle.Right),
                            ordered.Max(l => l.GlyphRectangle.Top))));
                }
            }
        }
        return found;
    }

    private static string DescribeKind(RedactionMarkKind kind) => kind switch
    {
        RedactionMarkKind.RedactAnnotation =>
            "text inside an unapplied /Redact annotation — marked for redaction, never removed",
        RedactionMarkKind.ShapeAnnotation =>
            "text under an annotation-drawn box — the page content was never touched",
        _ => "text inside a redaction mark",
    };

    private static bool CentreInside(PdfRectangle glyph, PdfRectangle mark)
    {
        var g = glyph.Normalize();
        var m = mark.Normalize();
        var cx = (g.Left + g.Right) / 2.0;
        var cy = (g.Bottom + g.Top) / 2.0;
        return cx >= m.Left - EdgeTolerancePt && cx <= m.Right + EdgeTolerancePt
            && cy >= m.Bottom - EdgeTolerancePt && cy <= m.Top + EdgeTolerancePt;
    }
}
