using Excise.Core.Document;

namespace Excise.Core.Text;

/// <summary>
/// Represents a word extracted from a PDF page.
/// A word is a sequence of letters separated by whitespace.
/// </summary>
public class Word
{
    /// <summary>
    /// The letters that make up this word.
    /// </summary>
    public IReadOnlyList<Letter> Letters { get; }

    /// <summary>
    /// The text content of the word.
    /// </summary>
    public string Text { get; }

    /// <summary>
    /// The bounding box of the word in PDF coordinates.
    /// </summary>
    public PdfRectangle BoundingBox { get; }

    public Word(IReadOnlyList<Letter> letters)
    {
        Letters = letters;
        Text = string.Concat(letters.Select(l => l.Value));
        BoundingBox = CalculateBoundingBox(letters);
    }

    private static PdfRectangle CalculateBoundingBox(IReadOnlyList<Letter> letters)
    {
        if (letters.Count == 0)
            return default;

        double left = double.MaxValue;
        double bottom = double.MaxValue;
        double right = double.MinValue;
        double top = double.MinValue;

        foreach (var letter in letters)
        {
            var rect = letter.GlyphRectangle;
            if (rect.Left < left) left = rect.Left;
            if (rect.Bottom < bottom) bottom = rect.Bottom;
            if (rect.Right > right) right = rect.Right;
            if (rect.Top > top) top = rect.Top;
        }

        return new PdfRectangle(left, bottom, right, top);
    }

    public override string ToString() => Text;
}
