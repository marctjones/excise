using Excise.Core.Content;
using Excise.Core.Document;

namespace Excise.Core.Text.Segmentation;

/// <summary>
/// Splits text operations into keep/remove segments based on letter positions and redaction area.
/// This is the foundation for partial text redaction (e.g., redacting "World" from "Hello World").
/// </summary>
/// <remarks>
/// The segmenter analyzes each character in a text operation and determines whether it should
/// be kept or removed based on its position relative to the redaction area. It groups consecutive
/// characters with the same keep/remove status into segments.
///
/// This enables:
/// - Partial text redaction at character-level granularity
/// - Maintaining correct text positioning after segmentation
/// - Identifying partially overlapped glyphs for rasterization
/// </remarks>
public class TextSegmenter
{

    /// <summary>
    /// Build segments from a text operation and letter matches.
    /// Determines which parts to keep and which to remove based on redaction area.
    /// </summary>
    /// <param name="text">The text from the operation.</param>
    /// <param name="operationBounds">The bounding box of the entire operation.</param>
    /// <param name="letterMatches">Letters matched to this operation with positions.</param>
    /// <param name="redactionArea">Area to redact.</param>
    /// <param name="strategy">Strategy for determining when to remove glyphs.</param>
    /// <returns>List of segments to KEEP (segments to remove are excluded).</returns>
    public List<TextSegment> BuildSegments(
        string text,
        PdfRectangle operationBounds,
        List<LetterMatch> letterMatches,
        PdfRectangle redactionArea,
        GlyphRemovalStrategy strategy = GlyphRemovalStrategy.AnyOverlap)
        => BuildSegments(text, operationBounds, letterMatches, new[] { redactionArea }, strategy);

    internal List<TextSegment> BuildSegments(
        string text,
        PdfRectangle operationBounds,
        List<LetterMatch> letterMatches,
        IReadOnlyList<PdfRectangle> redactionAreas,
        GlyphRemovalStrategy strategy = GlyphRemovalStrategy.AnyOverlap)
    {
        var allSegments = new List<TextSegment>();
        TextSegment? currentSegment = null;

        // If no letter matches, fall back to checking whole operation bounding box
        if (letterMatches.Count == 0)
        {
            bool wholeOperationInRedactionArea = redactionAreas.Any(operationBounds.IntersectsWith);

            if (!wholeOperationInRedactionArea)
            {
                // Keep entire operation
                return new List<TextSegment>
                {
                    new TextSegment
                    {
                        StartIndex = 0,
                        EndIndex = text.Length,
                        Keep = true,
                        StartX = operationBounds.Left,
                        StartY = operationBounds.Bottom,
                        Width = operationBounds.Width,
                        Height = operationBounds.Height,
                        OriginalText = text
                    }
                };
            }
            else
            {
                // Remove entire operation
                return new List<TextSegment>();
            }
        }

        // Process each character, building segments
        for (int i = 0; i < text.Length; i++)
        {
            // Find matching letter for this character index
            var match = letterMatches.FirstOrDefault(m => m.CharacterIndex == i);

            bool keep;
            double glyphX, glyphY, glyphWidth, glyphHeight;
            GlyphOverlapType overlapType = GlyphOverlapType.None;

            if (match != null)
            {
                // We have letter position info - get detailed overlap info
                var (shouldRemove, overlap) = GetLetterOverlapInfo(
                    match.Letter.GlyphRectangle, redactionAreas, strategy);
                keep = !shouldRemove;
                overlapType = overlap;

                // Use letter position directly
                var rect = match.Letter.GlyphRectangle;
                glyphX = rect.Left;
                glyphY = rect.Bottom;
                glyphWidth = rect.Width;
                glyphHeight = rect.Height;
            }
            else
            {
                // No letter match for this index - use FIXED width estimate
                // This happens for unmapped characters (trailing spaces, etc.)
                // CRITICAL: Don't use operation bbox width - for reconstructed operations,
                // this is a placeholder that causes massive width accumulation!
                keep = true;  // Conservative: keep if unknown
                glyphX = operationBounds.Left;
                glyphY = operationBounds.Bottom;
                glyphWidth = 5.0;   // Fixed approximate space width
                glyphHeight = 12.0; // Fixed approximate text height
            }

            // Start new segment if keep status or overlap type changed
            // (we want separate segments for partial vs full overlap)
            if (currentSegment == null || currentSegment.Keep != keep ||
                (!keep && currentSegment.OverlapType != overlapType))
            {
                if (currentSegment != null)
                {
                    allSegments.Add(currentSegment);
                }

                currentSegment = new TextSegment
                {
                    StartIndex = i,
                    EndIndex = i + 1,
                    Keep = keep,
                    OverlapType = keep ? GlyphOverlapType.None : overlapType,
                    StartX = glyphX,
                    StartY = glyphY,
                    Width = glyphWidth,
                    Height = glyphHeight,
                    OriginalText = text
                };

                // Add letter match to new segment
                if (match != null)
                {
                    currentSegment.LetterMatches.Add(match);
                    // A Type0/CID font's original code must be preserved on
                    // reconstruction, not re-encoded via Unicode (#353) — a
                    // CID font can't render arbitrary Unicode bytes. This is
                    // independent of byte length (#659: a Type0 font can
                    // legally use a 1-byte codespace, not just Identity-H/V's
                    // 2-byte one), so check the font kind directly rather
                    // than inferring it from CodeByteLength.
                    if (match.Letter.IsCidFont)
                        currentSegment.IsCidFont = true;
                }
            }
            else
            {
                // Extend current segment
                currentSegment.EndIndex = i + 1;
                currentSegment.Width += glyphWidth;

                // Add letter match to current segment
                if (match != null)
                {
                    currentSegment.LetterMatches.Add(match);
                    if (match.Letter.IsCidFont)
                        currentSegment.IsCidFont = true;
                }
            }
        }

        // Add final segment
        if (currentSegment != null)
        {
            allSegments.Add(currentSegment);
        }

        // Return only segments to keep
        var segmentsToKeep = allSegments.Where(s => s.Keep).ToList();
        return segmentsToKeep;
    }

    /// <summary>
    /// Get detailed overlap information for a letter.
    /// Returns whether to remove the letter and what type of overlap exists.
    /// </summary>
    /// <param name="glyphRect">The glyph's bounding box.</param>
    /// <param name="redactionArea">The redaction area.</param>
    /// <param name="strategy">The removal strategy to apply.</param>
    /// <returns>Tuple of (shouldRemove, overlapType).</returns>
    private (bool ShouldRemove, GlyphOverlapType OverlapType) GetLetterOverlapInfo(
        PdfRectangle glyphRectangle,
        IReadOnlyList<PdfRectangle> redactionAreas,
        GlyphRemovalStrategy strategy)
    {
        var result = (ShouldRemove: false, OverlapType: GlyphOverlapType.None);
        foreach (var area in redactionAreas)
        {
            result = GetLetterOverlapInfo(glyphRectangle, area, strategy);
            if (result.ShouldRemove) return result;
        }
        return result;
    }

    private (bool ShouldRemove, GlyphOverlapType OverlapType) GetLetterOverlapInfo(
        PdfRectangle glyphRect,
        PdfRectangle redactionArea,
        GlyphRemovalStrategy strategy)
    {
        var overlapType =
            !glyphRect.IntersectsWith(redactionArea) ? GlyphOverlapType.None
            : redactionArea.Contains(glyphRect) ? GlyphOverlapType.Full
            : GlyphOverlapType.Partial;
        return (strategy.Selects(glyphRect, redactionArea), overlapType);
    }
}
