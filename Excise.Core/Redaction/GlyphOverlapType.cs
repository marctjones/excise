namespace Excise.Core.Text.Segmentation;

/// <summary>
/// Type of overlap between a glyph and a redaction area.
/// Used to determine whether partial glyph rasterization is needed.
/// </summary>
public enum GlyphOverlapType
{
    /// <summary>
    /// Glyph does not intersect the redaction area at all (keep glyph).
    /// </summary>
    None,

    /// <summary>
    /// Glyph is fully contained within the redaction area (remove completely).
    /// </summary>
    Full,

    /// <summary>
    /// Glyph partially overlaps the redaction area (needs rasterization).
    /// The visible portion should be preserved as an image while removing the text operator.
    /// </summary>
    Partial
}
