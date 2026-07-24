using Excise.Core.Content;
using Excise.Core.Document;
using Excise.Core.Text;

namespace Excise.Core.Operations;

/// <summary>
/// Provides glyph-level text redaction for PDF pages.
/// Removes text content from the PDF structure, not just visual covering.
/// </summary>
public class TextRedactor
{
    /// <summary>
    /// Redact text in a specific area of a page.
    /// Removes content from PDF structure and optionally draws a visual marker.
    /// </summary>
    /// <param name="page">The page to redact.</param>
    /// <param name="area">The rectangular area to redact in PDF coordinates.</param>
    /// <param name="drawMarker">Whether to draw a visual marker over the redacted area.</param>
    /// <param name="markerColor">RGB color for the marker (0-1 range). Default is black.</param>
    public void RedactArea(
        PdfPage page,
        PdfRectangle area,
        bool drawMarker = true,
        (double R, double G, double B)? markerColor = null)
    {
        var content = page.GetContentStream();

        // Remove operators that intersect with the redaction area
        var redacted = content.RemoveIntersecting(area);

        // Add visual marker if requested
        if (drawMarker)
        {
            var color = markerColor ?? (0, 0, 0); // Default black
            redacted = redacted
                .Append(ContentOperator.SaveState())
                .Append(ContentOperator.SetFillRgb(color.R, color.G, color.B))
                .Append(ContentOperator.Rectangle(area))
                .Append(ContentOperator.Fill())
                .Append(ContentOperator.RestoreState());
        }

        page.SetContentStream(redacted);
    }

    /// <summary>
    /// Redact all occurrences of specific text on a page.
    /// Uses letter bounding boxes to find and remove matching text.
    /// </summary>
    /// <param name="page">The page to redact.</param>
    /// <param name="searchText">The text to find and redact.</param>
    /// <param name="drawMarker">Whether to draw visual markers.</param>
    /// <param name="markerColor">RGB color for markers. Default is black.</param>
    /// <returns>The number of text occurrences found and redacted.</returns>
    public int RedactText(
        PdfPage page,
        string searchText,
        bool drawMarker = true,
        (double R, double G, double B)? markerColor = null)
    {
        if (string.IsNullOrEmpty(searchText))
            return 0;

        // Get letters with positions
        var letters = page.Letters;
        if (letters.Count == 0)
            return 0;

        // Find all occurrences of the search text
        var matches = FindTextOccurrences(letters, searchText);
        if (matches.Count == 0)
            return 0;

        // Get the content stream
        var content = page.GetContentStream();

        // Remove operators for each match
        foreach (var match in matches)
        {
            // Calculate bounding box for the matched letters
            var bbox = CalculateBoundingBox(match);
            content = content.RemoveIntersecting(bbox);
        }

        // Add visual markers for all matches
        if (drawMarker)
        {
            var color = markerColor ?? (0, 0, 0);
            foreach (var match in matches)
            {
                var bbox = CalculateBoundingBox(match);
                content = content
                    .Append(ContentOperator.SaveState())
                    .Append(ContentOperator.SetFillRgb(color.R, color.G, color.B))
                    .Append(ContentOperator.Rectangle(bbox))
                    .Append(ContentOperator.Fill())
                    .Append(ContentOperator.RestoreState());
            }
        }

        page.SetContentStream(content);
        return matches.Count;
    }

    /// <summary>
    /// Redact text using a predicate to select which letters to remove.
    /// </summary>
    /// <param name="page">The page to redact.</param>
    /// <param name="letterPredicate">Predicate to determine if a letter should be redacted.</param>
    /// <param name="drawMarker">Whether to draw visual markers.</param>
    /// <param name="markerColor">RGB color for markers. Default is black.</param>
    /// <returns>The number of letters redacted.</returns>
    public int RedactLetters(
        PdfPage page,
        Func<Letter, bool> letterPredicate,
        bool drawMarker = true,
        (double R, double G, double B)? markerColor = null)
    {
        var letters = page.Letters.Where(letterPredicate).ToList();
        if (letters.Count == 0)
            return 0;

        var content = page.GetContentStream();

        // Remove operators for each matching letter
        foreach (var letter in letters)
        {
            content = content.RemoveIntersecting(letter.GlyphRectangle);
        }

        // Add visual markers
        if (drawMarker)
        {
            var color = markerColor ?? (0, 0, 0);
            foreach (var letter in letters)
            {
                content = content
                    .Append(ContentOperator.SaveState())
                    .Append(ContentOperator.SetFillRgb(color.R, color.G, color.B))
                    .Append(ContentOperator.Rectangle(letter.GlyphRectangle))
                    .Append(ContentOperator.Fill())
                    .Append(ContentOperator.RestoreState());
            }
        }

        page.SetContentStream(content);
        return letters.Count;
    }

    /// <summary>
    /// Find all occurrences of text in the letter sequence.
    /// </summary>
    private List<List<Letter>> FindTextOccurrences(IReadOnlyList<Letter> letters, string searchText)
    {
        var results = new List<List<Letter>>();

        // Fold Arabic presentation forms to base letters on BOTH sides so a
        // base-letter needle matches shaped text (#632). All index arithmetic
        // below is done consistently in folded space: the per-letter folded
        // values (a lam-alef ligature folds 1 char → 2) drive the mapping
        // from folded string positions back to letters.
        var needle = ArabicPresentationForms.Fold(searchText);
        var foldedValues = new string[letters.Count];
        for (int i = 0; i < letters.Count; i++)
            foldedValues[i] = ArabicPresentationForms.Fold(letters[i].Value);

        // Build a string from all letters for searching
        var fullText = string.Concat(foldedValues);

        // Find all start positions of the search text
        var pos = 0;
        while ((pos = fullText.IndexOf(needle, pos, StringComparison.Ordinal)) >= 0)
        {
            // Map back to letters
            var match = new List<Letter>();
            var charIndex = 0;
            var letterIndex = 0;

            // Skip to position
            while (charIndex < pos && letterIndex < letters.Count)
            {
                charIndex += foldedValues[letterIndex].Length;
                letterIndex++;
            }

            // Collect letters that make up the match
            var matchChars = 0;
            while (matchChars < needle.Length && letterIndex < letters.Count)
            {
                match.Add(letters[letterIndex]);
                matchChars += foldedValues[letterIndex].Length;
                letterIndex++;
            }

            if (match.Count > 0)
                results.Add(match);

            pos += 1; // Move past this match to find overlapping occurrences
        }

        return results;
    }

    /// <summary>
    /// Calculate bounding box that encompasses all letters in a match.
    /// </summary>
    private PdfRectangle CalculateBoundingBox(List<Letter> letters)
    {
        if (letters.Count == 0)
            throw new ArgumentException("Cannot calculate bounding box for empty letter list");

        var first = letters[0].GlyphRectangle;
        double left = first.Left;
        double bottom = first.Bottom;
        double right = first.Right;
        double top = first.Top;

        for (int i = 1; i < letters.Count; i++)
        {
            var rect = letters[i].GlyphRectangle;
            left = Math.Min(left, rect.Left);
            bottom = Math.Min(bottom, rect.Bottom);
            right = Math.Max(right, rect.Right);
            top = Math.Max(top, rect.Top);
        }

        return new PdfRectangle(left, bottom, right, top);
    }
}
