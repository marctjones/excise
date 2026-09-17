namespace Excise.Core.Xfa;

/// <summary>A placed run inside a laid-out line; <see cref="X"/> is the offset from the line start.</summary>
internal readonly record struct XfaPlacedRun(string Text, XfaFontSpec Font, double X, double Width);

/// <summary>One laid-out line of text.</summary>
internal sealed record XfaTextLine(IReadOnlyList<XfaPlacedRun> Runs, double Width, double Height, double MaxSize, string HAlign, bool FirstOfParagraph);

/// <summary>A laid-out block of text with its total height.</summary>
internal sealed record XfaTextBlock(IReadOnlyList<XfaTextLine> Lines, double Height, double Width)
{
    public static readonly XfaTextBlock Empty = new(Array.Empty<XfaTextLine>(), 0, 0);
}

/// <summary>
/// Word-wrapped text layout with base-14 metrics, shared by measurement and
/// emission so the two can never disagree about where a line breaks.
/// </summary>
internal static class XfaText
{
    /// <summary>Plain text as paragraphs of one run each (hard breaks kept).</summary>
    public static List<XfaParagraph> PlainParagraphs(string? text, XfaFontSpec font)
    {
        var result = new List<XfaParagraph>();
        if (string.IsNullOrEmpty(text))
            return result;
        // Designer writes paragraph breaks in plain text as U+2029.
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n')
            .Replace('\u2029', '\n').Replace('\u2028', '\n').Replace('\u000B', '\n').Replace('\u0085', '\n');
        foreach (var line in normalized.Split('\n'))
        {
            var runs = line.Length == 0
                ? (IReadOnlyList<XfaTextRun>)Array.Empty<XfaTextRun>()
                : new[] { new XfaTextRun(line.Replace('\t', ' '), font) };
            result.Add(new XfaParagraph(runs, null, 0, 0));
        }
        return result;
    }

    public static double Measure(string text, XfaFontSpec font)
        => font.Width(text);

    /// <summary>
    /// Lay out <paramref name="paragraphs"/> in <paramref name="maxWidth"/>
    /// points. <paramref name="wrap"/> false keeps each paragraph on one line.
    /// </summary>
    public static XfaTextBlock Layout(
        IReadOnlyList<XfaParagraph> paragraphs,
        double maxWidth,
        bool wrap,
        XfaParaSpec para,
        XfaFontSpec baseFont,
        XfaBudget budget)
    {
        if (paragraphs.Count == 0)
            return XfaTextBlock.Empty;

        var lines = new List<XfaTextLine>();
        double height = para.SpaceAbove;
        double widest = 0;
        double available = maxWidth - para.MarginLeft - para.MarginRight;

        foreach (var paragraph in paragraphs)
        {
            budget.Tick();
            height += paragraph.SpaceBefore;
            var align = paragraph.HAlign ?? para.HAlign;
            var runs = new List<XfaPlacedRun>();
            double x = para.TextIndent;
            double maxSize = 0;
            bool first = true;

            void EndLine()
            {
                var size = maxSize > 0 ? maxSize : baseFont.Size;
                var lineHeight = para.LineHeight ?? size * 1.2;
                // Trailing spaces do not count toward alignment.
                var trimmed = TrimTrailingSpace(runs);
                double width = trimmed.Count == 0 ? 0 : trimmed[^1].X + trimmed[^1].Width;
                lines.Add(new XfaTextLine(trimmed, width, lineHeight, size, align, first));
                // Run offsets already include the first line's indent.
                widest = Math.Max(widest, width);
                height += lineHeight;
                runs = new List<XfaPlacedRun>();
                x = 0;
                maxSize = 0;
                first = false;
            }

            foreach (var run in paragraph.Runs)
            {
                var font = run.Font;
                foreach (var token in Tokens(run.Text))
                {
                    budget.Tick();
                    bool isSpace = token == " ";
                    if (isSpace && runs.Count == 0 && !first)
                        continue;

                    var width = font.Width(token);
                    if (wrap && available > 0 && !isSpace && runs.Count > 0 && x + width > available + 0.01)
                    {
                        EndLine();
                    }

                    if (wrap && available > 0 && !isSpace && runs.Count == 0 && width > available + 0.01 && token.Length > 1)
                    {
                        // A word wider than the line: break it by characters.
                        foreach (var piece in BreakWord(token, font, Math.Max(available - x, 1)))
                        {
                            if (runs.Count > 0)
                                EndLine();
                            var pieceWidth = font.Width(piece);
                            runs.Add(new XfaPlacedRun(piece, run.Font, x, pieceWidth));
                            x += pieceWidth;
                            maxSize = Math.Max(maxSize, run.Font.Size);
                        }
                        continue;
                    }

                    runs.Add(new XfaPlacedRun(token, run.Font, x, width));
                    x += width;
                    maxSize = Math.Max(maxSize, run.Font.Size);
                }
            }

            EndLine();
            height += paragraph.SpaceAfter;
        }

        height += para.SpaceBelow;
        return new XfaTextBlock(Merge(lines), height, widest + para.MarginLeft + para.MarginRight);
    }

    private static IEnumerable<string> Tokens(string text)
    {
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == ' ')
            {
                if (i > start)
                    yield return text[start..i];
                yield return " ";
                start = i + 1;
            }
        }
        if (start < text.Length)
            yield return text[start..];
    }

    private static IEnumerable<string> BreakWord(string word, XfaFontSpec font, double width)
    {
        int start = 0;
        for (int i = 1; i <= word.Length; i++)
        {
            if (i == word.Length || font.Width(word[start..(i + 1)]) > width)
            {
                if (i == word.Length)
                {
                    yield return word[start..];
                    yield break;
                }
                yield return word[start..i];
                start = i;
            }
        }
    }

    private static List<XfaPlacedRun> TrimTrailingSpace(List<XfaPlacedRun> runs)
    {
        while (runs.Count > 0 && runs[^1].Text == " ")
            runs.RemoveAt(runs.Count - 1);
        return runs;
    }

    /// <summary>Join adjacent runs with the same font into one, so each line emits few text objects.</summary>
    private static List<XfaTextLine> Merge(List<XfaTextLine> lines)
    {
        var result = new List<XfaTextLine>(lines.Count);
        foreach (var line in lines)
        {
            var merged = new List<XfaPlacedRun>();
            foreach (var run in line.Runs)
            {
                if (merged.Count > 0 && merged[^1].Font == run.Font)
                {
                    var last = merged[^1];
                    merged[^1] = last with { Text = last.Text + run.Text, Width = run.X + run.Width - last.X };
                }
                else
                {
                    merged.Add(run);
                }
            }
            result.Add(line with { Runs = merged });
        }
        return result;
    }
}
