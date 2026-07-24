using Excise.Core.Content;
using Excise.Core.Document;
using Excise.Core.Text;

namespace Excise.Core.Operations;

/// <summary>
/// Fluent builder for PDF redaction operations.
/// Provides a clean API for specifying what to redact.
/// </summary>
public class PdfRedaction
{
    private readonly PdfPage _page;
    private readonly List<RedactionAction> _actions = new();
    private bool _drawMarkers = true;
    private (double R, double G, double B) _markerColor = (0, 0, 0);

    private PdfRedaction(PdfPage page)
    {
        _page = page;
    }

    /// <summary>
    /// Start a redaction operation on a page.
    /// </summary>
    public static PdfRedaction OnPage(PdfPage page)
    {
        return new PdfRedaction(page);
    }

    /// <summary>
    /// Redact a specific rectangular area.
    /// </summary>
    public PdfRedaction Area(PdfRectangle rect)
    {
        _actions.Add(new RedactionAction(RedactionType.Area, Area: rect));
        return this;
    }

    /// <summary>
    /// Redact a specific rectangular area.
    /// </summary>
    public PdfRedaction Area(double left, double bottom, double right, double top)
    {
        return Area(new PdfRectangle(left, bottom, right, top));
    }

    /// <summary>
    /// Redact all occurrences of specific text.
    /// </summary>
    public PdfRedaction Text(string searchText)
    {
        if (!string.IsNullOrEmpty(searchText))
            _actions.Add(new RedactionAction(RedactionType.Text, SearchText: searchText));
        return this;
    }

    /// <summary>
    /// Redact text matching a predicate.
    /// </summary>
    public PdfRedaction Letters(Func<Letter, bool> predicate)
    {
        _actions.Add(new RedactionAction(RedactionType.LetterPredicate, LetterPredicate: predicate));
        return this;
    }

    /// <summary>
    /// Redact all text operators (removes all text from the page).
    /// </summary>
    public PdfRedaction AllText()
    {
        _actions.Add(new RedactionAction(RedactionType.AllText));
        return this;
    }

    /// <summary>
    /// Redact all operators of a specific category.
    /// </summary>
    public PdfRedaction Category(OperatorCategory category)
    {
        _actions.Add(new RedactionAction(RedactionType.Category, Category: category));
        return this;
    }

    /// <summary>
    /// Configure whether to draw visual markers over redacted areas.
    /// Default is true.
    /// </summary>
    public PdfRedaction WithMarkers(bool draw = true)
    {
        _drawMarkers = draw;
        return this;
    }

    /// <summary>
    /// Set the color for redaction markers.
    /// Default is black (0, 0, 0).
    /// </summary>
    public PdfRedaction MarkerColor(double r, double g, double b)
    {
        _markerColor = (r, g, b);
        return this;
    }

    /// <summary>
    /// Use black markers (default).
    /// </summary>
    public PdfRedaction BlackMarkers()
    {
        _markerColor = (0, 0, 0);
        _drawMarkers = true;
        return this;
    }

    /// <summary>
    /// Use white markers.
    /// </summary>
    public PdfRedaction WhiteMarkers()
    {
        _markerColor = (1, 1, 1);
        _drawMarkers = true;
        return this;
    }

    /// <summary>
    /// Apply all redaction actions to the page.
    /// Returns a summary of what was redacted.
    /// </summary>
    public RedactionResult Apply()
    {
        var content = _page.GetContentStream();
        var result = new RedactionResult();
        var markersToAdd = new List<PdfRectangle>();

        foreach (var action in _actions)
        {
            switch (action.Type)
            {
                case RedactionType.Area:
                    {
                        var area = action.Area!.Value;
                        var beforeCount = content.Count;
                        content = content.RemoveIntersecting(area);
                        result.OperatorsRemoved += beforeCount - content.Count;
                        result.AreasRedacted++;
                        markersToAdd.Add(area);
                    }
                    break;

                case RedactionType.Text:
                    {
                        var letters = _page.Letters;
                        var matches = FindTextOccurrences(letters, action.SearchText!);
                        foreach (var match in matches)
                        {
                            var bbox = CalculateBoundingBox(match);
                            var beforeCount = content.Count;
                            content = content.RemoveIntersecting(bbox);
                            result.OperatorsRemoved += beforeCount - content.Count;
                            result.TextOccurrencesRedacted++;
                            markersToAdd.Add(bbox);
                        }
                    }
                    break;

                case RedactionType.LetterPredicate:
                    {
                        var letters = _page.Letters.Where(action.LetterPredicate!).ToList();
                        foreach (var letter in letters)
                        {
                            var beforeCount = content.Count;
                            content = content.RemoveIntersecting(letter.GlyphRectangle);
                            result.OperatorsRemoved += beforeCount - content.Count;
                            result.LettersRedacted++;
                            markersToAdd.Add(letter.GlyphRectangle);
                        }
                    }
                    break;

                case RedactionType.AllText:
                    {
                        var beforeCount = content.Count;
                        content = content.RemoveCategory(OperatorCategory.TextShowing);
                        result.OperatorsRemoved += beforeCount - content.Count;
                        result.AllTextRemoved = true;
                        // For all text, we don't add individual markers - too many
                    }
                    break;

                case RedactionType.Category:
                    {
                        var beforeCount = content.Count;
                        content = content.RemoveCategory(action.Category!.Value);
                        result.OperatorsRemoved += beforeCount - content.Count;
                        result.CategoriesRemoved++;
                    }
                    break;
            }
        }

        // Add visual markers if requested
        if (_drawMarkers && markersToAdd.Count > 0)
        {
            foreach (var area in markersToAdd)
            {
                content = content
                    .Append(ContentOperator.SaveState())
                    .Append(ContentOperator.SetFillRgb(_markerColor.R, _markerColor.G, _markerColor.B))
                    .Append(ContentOperator.Rectangle(area))
                    .Append(ContentOperator.Fill())
                    .Append(ContentOperator.RestoreState());
            }
        }

        _page.SetContentStream(content);
        return result;
    }

    #region Private helpers

    private List<List<Letter>> FindTextOccurrences(IReadOnlyList<Letter> letters, string searchText)
    {
        var results = new List<List<Letter>>();

        // Fold Arabic presentation forms (#632) and Latin ligatures (#722)
        // on both sides so a plain-letter needle matches shaped/ligated
        // text; index arithmetic stays in folded space via the per-letter
        // folded values (lam-alef folds 1 char → 2, ﬃ folds 1 → 3).
        var needle = PresentationFormFolding.Fold(searchText);
        var foldedValues = new string[letters.Count];
        for (int i = 0; i < letters.Count; i++)
            foldedValues[i] = PresentationFormFolding.Fold(letters[i].Value);

        var fullText = string.Concat(foldedValues);

        var pos = 0;
        while ((pos = fullText.IndexOf(needle, pos, StringComparison.Ordinal)) >= 0)
        {
            var match = new List<Letter>();
            var charIndex = 0;
            var letterIndex = 0;

            while (charIndex < pos && letterIndex < letters.Count)
            {
                charIndex += foldedValues[letterIndex].Length;
                letterIndex++;
            }

            var matchChars = 0;
            while (matchChars < needle.Length && letterIndex < letters.Count)
            {
                match.Add(letters[letterIndex]);
                matchChars += foldedValues[letterIndex].Length;
                letterIndex++;
            }

            if (match.Count > 0)
                results.Add(match);

            pos += 1;
        }

        return results;
    }

    private static PdfRectangle CalculateBoundingBox(List<Letter> letters)
    {
        if (letters.Count == 0)
            return new PdfRectangle(0, 0, 0, 0);

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

    #endregion

    #region Types

    private enum RedactionType
    {
        Area,
        Text,
        LetterPredicate,
        AllText,
        Category
    }

    private record RedactionAction(
        RedactionType Type,
        PdfRectangle? Area = null,
        string? SearchText = null,
        Func<Letter, bool>? LetterPredicate = null,
        OperatorCategory? Category = null);

    #endregion
}

/// <summary>
/// Summary of redaction results.
/// </summary>
public class RedactionResult
{
    /// <summary>
    /// Number of PDF operators removed from the content stream.
    /// </summary>
    public int OperatorsRemoved { get; set; }

    /// <summary>
    /// Number of rectangular areas that were redacted.
    /// </summary>
    public int AreasRedacted { get; set; }

    /// <summary>
    /// Number of text string occurrences that were redacted.
    /// </summary>
    public int TextOccurrencesRedacted { get; set; }

    /// <summary>
    /// Number of individual letters that were redacted.
    /// </summary>
    public int LettersRedacted { get; set; }

    /// <summary>
    /// Number of operator categories that were removed.
    /// </summary>
    public int CategoriesRemoved { get; set; }

    /// <summary>
    /// Whether all text was removed from the page.
    /// </summary>
    public bool AllTextRemoved { get; set; }

    /// <summary>
    /// Whether any redaction was performed.
    /// </summary>
    public bool WasRedacted =>
        OperatorsRemoved > 0 || AreasRedacted > 0 || TextOccurrencesRedacted > 0 ||
        LettersRedacted > 0 || CategoriesRemoved > 0 || AllTextRemoved;

    public override string ToString()
    {
        var parts = new List<string>();
        if (AreasRedacted > 0) parts.Add($"{AreasRedacted} areas");
        if (TextOccurrencesRedacted > 0) parts.Add($"{TextOccurrencesRedacted} text occurrences");
        if (LettersRedacted > 0) parts.Add($"{LettersRedacted} letters");
        if (AllTextRemoved) parts.Add("all text");
        if (CategoriesRemoved > 0) parts.Add($"{CategoriesRemoved} categories");

        var what = parts.Count > 0 ? string.Join(", ", parts) : "nothing";
        return $"Redacted {what} ({OperatorsRemoved} operators removed)";
    }
}
