namespace Pdfe.Core.Graphics;

/// <summary>
/// Greedy word-wrapping shared by the low-level <see cref="PdfGraphics.DrawText"/>
/// and the high-level authoring facade. Measures with the font's own metrics, so
/// it works for both base-14 and embedded fonts.
/// </summary>
internal static class TextWrapper
{
    /// <summary>
    /// Wrap <paramref name="text"/> to <paramref name="maxWidth"/> points. Hard
    /// line breaks (<c>\n</c>, <c>\r\n</c>) are preserved; a single word wider
    /// than the column is emitted on its own (over-long) line. A non-positive
    /// <paramref name="maxWidth"/> disables wrapping (hard breaks only).
    /// </summary>
    public static IEnumerable<string> Wrap(string text, PdfFont font, double maxWidth)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield return string.Empty;
            yield break;
        }

        var hardLines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        foreach (var hardLine in hardLines)
        {
            if (hardLine.Length == 0)
            {
                yield return string.Empty;
                continue;
            }

            var words = hardLine.Split(' ');
            var current = new System.Text.StringBuilder();
            foreach (var word in words)
            {
                if (current.Length == 0)
                {
                    current.Append(word);
                    continue;
                }

                var candidate = current.ToString() + " " + word;
                if (maxWidth > 0 && font.MeasureWidth(candidate) > maxWidth)
                {
                    yield return current.ToString();
                    current.Clear();
                    current.Append(word);
                }
                else
                {
                    current.Append(' ').Append(word);
                }
            }

            if (current.Length > 0)
                yield return current.ToString();
        }
    }
}
