using Excise.Core.Document;

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

internal static class GlyphRemovalStrategyExtensions
{
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
