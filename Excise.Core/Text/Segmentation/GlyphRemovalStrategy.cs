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
