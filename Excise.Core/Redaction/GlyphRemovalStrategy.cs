using System.Collections.Generic;
using System.Linq;
using Excise.Core.Document;
using Excise.Core.Text;

namespace Excise.Core.Text.Segmentation;

/// <summary>
/// Strategy for determining when a glyph should be removed during redaction.
/// ISO 32000-2:2020 doesn't specify this - it's an implementation choice for security vs precision.
/// </summary>
public enum GlyphRemovalStrategy
{
    /// <summary>
    /// Remove glyph if ANY part of it intersects the redaction area (most secure).
    /// This is the recommended default as it prevents partial glyph exposure.
    /// </summary>
    AnyOverlap,

    /// <summary>
    /// Remove glyph only if it's FULLY contained within the redaction area.
    /// More precise but may leave partial glyphs visible at boundaries.
    /// </summary>
    FullyContained,

    /// <summary>
    /// Remove glyph if its CENTER POINT is inside the redaction area (legacy behavior).
    /// Provides middle-ground security but can miss edge cases.
    /// </summary>
    CenterPoint
}

/// <summary>
/// #2055: an area a glyph pass removes glyphs from. <see cref="Box"/> is the
/// page-space rectangle every pass reads (carrier scrubbers, form flattening,
/// and the glyph decision when there is no frame). <see cref="Frame"/>, when
/// set, is the same area in the frame of a line of text advancing along
/// <see cref="Angle"/> (<see cref="TextSelectionEngine.LineFrame"/>), and the
/// glyph decision reads it instead: the page-space box around a run turned by
/// an oblique angle is a square that covers the lines above and below it.
/// </summary>
internal readonly record struct GlyphArea(PdfRectangle Box, double Angle = 0, PdfRectangle? Frame = null)
{
    public static implicit operator GlyphArea(PdfRectangle box) => new(box);

    /// <summary>The glyph's box and this area, in the space the decision is made in.</summary>
    internal (PdfRectangle Glyph, PdfRectangle Area) Compare(Letter glyph) =>
        Frame is { } frame
            ? (TextSelectionEngine.BoxInLineFrame(glyph, Angle), frame)
            : (glyph.GlyphRectangle, Box);

    internal static IReadOnlyList<GlyphArea> Of(IReadOnlyList<PdfRectangle> boxes) =>
        boxes.Select(b => (GlyphArea)b).ToList();
}

internal static class GlyphRemovalStrategyExtensions
{
    /// <summary>
    /// Whether <paramref name="strategy"/> removes <paramref name="glyph"/> from
    /// <paramref name="area"/>, decided in the area's frame (#2055). The one
    /// glyph decision: the operand split, the reconstruction fallback and the
    /// width ledger all ask it.
    /// </summary>
    internal static bool Selects(this GlyphRemovalStrategy strategy, Letter glyph, GlyphArea area)
    {
        var (g, a) = area.Compare(glyph);
        return strategy.Selects(g, a);
    }

    /// <summary>
    /// Whether <paramref name="strategy"/> removes something occupying
    /// <paramref name="glyph"/> (a letter, or an image's page-space box) from
    /// <paramref name="area"/>. The one decision glyph selection, segmentation and
    /// image redaction all make. Edges count as inside, so a zero-width glyph
    /// centred on the area's edge is removed by <c>CenterPoint</c>: judging by
    /// strict intersection first left that glyph in the file.
    /// </summary>
    internal static bool Selects(this GlyphRemovalStrategy strategy, PdfRectangle glyph, PdfRectangle area)
    {
        var g = glyph.Normalize();
        var a = area.Normalize();
        return strategy switch
        {
            GlyphRemovalStrategy.FullyContained => g.IntersectsWith(a) && a.Contains(g),
            GlyphRemovalStrategy.CenterPoint => a.Contains((g.Left + g.Right) * 0.5, (g.Bottom + g.Top) * 0.5),
            _ => g.IntersectsWith(a),
        };
    }
}
