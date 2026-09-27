using System;
using System.Collections.Generic;
using System.Linq;
using Excise.Core.Content;
using Excise.Core.Document;

namespace Excise.Core.Text.Segmentation;

/// <summary>
/// #1753 — the underline, box, strike-through or highlight a producer drew to
/// one redacted word's extent goes with the word. Once a width-closing policy
/// moves the text after the word, a decoration left at the old width states
/// the removed word's width: the issue recovered 8 of 8 words that way.
/// </summary>
/// <remarks>
/// <para>A decoration is sized to a word when both of its x-edges lie within
/// <see cref="EdgeEms"/> of the word's and it sits in the word's line band. A
/// rule or highlight spanning more than the word (a table border, a sentence)
/// does not measure it and is kept.</para>
/// <para>The whole painted path is matched, so an underline filled in one
/// path with other words' underlines spans more than the word and is kept. A
/// path that also sets a clip is kept: removing it would unclip what follows.</para>
/// </remarks>
internal static class WordDecorationRemover
{
    /// <summary>How far each x-edge may sit from the word's, in word heights: padding, or a trailing comma.</summary>
    private const double EdgeEms = 0.35;

    /// <summary>How far below the word's box a decoration may reach: an underline sits under the descenders.</summary>
    private const double BelowEms = 0.5;

    /// <summary>How far above it: a box's padding.</summary>
    private const double AboveEms = 0.25;

    internal static bool IsSizedTo(PdfRectangle decoration, PdfRectangle word)
    {
        decoration = decoration.Normalize();
        word = word.Normalize();
        var height = word.Height;
        return Math.Abs(decoration.Left - word.Left) <= EdgeEms * height
               && Math.Abs(decoration.Right - word.Right) <= EdgeEms * height
               && decoration.Bottom >= word.Bottom - BelowEms * height
               && decoration.Top <= word.Top + AboveEms * height;
    }

    /// <summary>
    /// <paramref name="operations"/> without every path painted to the extent of
    /// one of <paramref name="words"/>; <paramref name="removed"/> counts them.
    /// Reads the page-space bounds the parser stamps on each painting operator.
    /// </summary>
    internal static IReadOnlyList<ContentOperator> Remove(
        IReadOnlyList<ContentOperator> operations, IReadOnlyList<PdfRectangle> words, out int removed)
    {
        removed = 0;
        var drop = new HashSet<int>();
        var path = new List<int>();
        var clips = false;
        for (var i = 0; i < operations.Count; i++)
        {
            var op = operations[i];
            switch (op.Name)
            {
                case "m" or "l" or "c" or "v" or "y" or "h" or "re":
                    path.Add(i);
                    continue;
                case "W" or "W*":
                    clips = true;
                    continue;
                case "S" or "s" or "f" or "F" or "f*" or "B" or "B*" or "b" or "b*"
                    when !clips && path.Count > 0 && op.BoundingBox is { } box
                         && words.Any(word => IsSizedTo(box, word)):
                    drop.UnionWith(path);
                    drop.Add(i);
                    removed++;
                    break;
            }
            path.Clear();
            clips = false;
        }
        return removed == 0 ? operations : operations.Where((_, i) => !drop.Contains(i)).ToList();
    }
}
