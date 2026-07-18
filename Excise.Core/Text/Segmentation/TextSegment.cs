using Excise.Core.Document;

namespace Excise.Core.Text.Segmentation;

/// <summary>
/// Represents a segment of text that should be kept or removed during redaction.
/// Enhanced for CJK support with raw byte preservation.
/// </summary>
public class TextSegment
{
    /// <summary>
    /// Start index in the original text (inclusive).
    /// </summary>
    public required int StartIndex { get; init; }

    /// <summary>
    /// End index in the original text (exclusive).
    /// </summary>
    public int EndIndex { get; set; }

    /// <summary>
    /// Whether to keep this segment (false means remove).
    /// </summary>
    public required bool Keep { get; init; }

    /// <summary>
    /// Type of overlap with redaction area.
    /// For Keep=true segments, this is GlyphOverlapType.None.
    /// For Keep=false segments, this is Full or Partial depending on coverage.
    /// </summary>
    public GlyphOverlapType OverlapType { get; set; } = GlyphOverlapType.None;

    /// <summary>
    /// Whether this segment is a candidate for rasterization (partial overlap).
    /// When true, the text should be removed but the visible portion preserved as image.
    /// </summary>
    public bool IsPartialOverlap => OverlapType == GlyphOverlapType.Partial;

    /// <summary>
    /// X position of first character in this segment (PDF coordinates).
    /// </summary>
    public required double StartX { get; init; }

    /// <summary>
    /// Y position (baseline) of this segment (PDF coordinates).
    /// </summary>
    public required double StartY { get; init; }

    /// <summary>
    /// Total width of this segment in points.
    /// </summary>
    public double Width { get; set; }

    /// <summary>
    /// Height of this segment in points.
    /// </summary>
    public required double Height { get; set; }

    /// <summary>
    /// The full original text (for extracting substring).
    /// </summary>
    public required string OriginalText { get; init; }

    /// <summary>
    /// The text of this segment.
    /// </summary>
    public string Text => OriginalText.Substring(StartIndex, EndIndex - StartIndex);

    /// <summary>
    /// Get the bounding box for this segment in PDF coordinates.
    /// Useful for partial overlap rasterization.
    /// </summary>
    public PdfRectangle BoundingBox => new PdfRectangle(
        StartX,
        StartY,
        StartX + Width,
        StartY + Height
    );

    #region CJK Support (Issue #281)

    /// <summary>
    /// The letter matches for this segment's characters.
    /// Used to access raw bytes and glyph positions for reconstruction.
    /// </summary>
    public List<LetterMatch> LetterMatches { get; set; } = new();

    /// <summary>
    /// Whether this segment is from a CID-keyed font.
    /// </summary>
    public bool IsCidFont { get; set; }

    /// <summary>
    /// Whether the font has a ToUnicode CMap.
    /// When true, raw bytes must be used for reconstruction to preserve encoding.
    /// </summary>
    public bool HasToUnicode { get; set; }

    /// <summary>
    /// Whether the original operand was a hex string.
    /// </summary>
    public bool WasHexString { get; set; }

    /// <summary>
    /// Get the raw bytes for this segment (for CJK reconstruction).
    /// </summary>
    public byte[] GetRawBytes()
    {
        if (LetterMatches.Count == 0)
            return Array.Empty<byte>();

        return LetterMatches
            .Where(m => m.RawBytes != null)
            .SelectMany(m => m.RawBytes!)
            .ToArray();
    }

    #endregion
}
