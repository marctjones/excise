using System;
using System.Collections.Generic;

namespace Excise.Core.Text;

/// <summary>
/// OVERPRINTED glyphs (#1047): runs of adjacent letters with the same value stamped on
/// top of one another, the "faux bold by stacking" trick. The ONE rule for them: the
/// redaction matcher, page text, the word list (search, the search index, copy) and the
/// selection's copied text all read a stack as one letter through this class.
/// </summary>
/// <remarks>
/// <para>Faux-bold and drop-shadow effects are produced by drawing the same text several
/// times at sub-point offsets. The letter model records every stamp, correctly, and
/// keeps doing so: a page's <c>Letters</c> hold every copy. Read in
/// order, a 3x-stamped <c>Test</c> is <c>TTTeeesssttt</c>: no search for <c>test</c>
/// matched it, copy gave the tripled letters, and before #1047 the term survived a
/// redaction that reported success (CLAUDE.md Limitations #1).</para>
///
/// <para>The discriminator is geometric, not textual, because a genuine double letter
/// must NOT collapse. In <c>letter</c> the two <c>t</c>s sit a glyph's advance apart;
/// overprinted copies sit on top of each other. So two same-valued neighbours merge
/// only when their origins are within a quarter of the glyph's width and height, which
/// scales with the type. Only NEIGHBOURS in the letter order merge: a word drawn whole
/// several times (<c>Test</c> then <c>Test</c> again) is not a stack of letters and
/// reads as its repeats, as MuPDF reads it.</para>
///
/// <para>A collapse is only ever a VIEW. The redaction matcher restores every
/// original stamp of a matched view letter before removal, so all copies are
/// deleted; keeping one would leave the text drawn and extractable.</para>
/// </remarks>
internal static class OverprintedGlyphs
{
    /// <summary>
    /// The letters with every overprinted stack collapsed to its first stamp, and for
    /// each view letter the first and last original indices it stands for.
    /// </summary>
    internal static (List<Letter> View, List<int> SpanStart, List<int> SpanEnd) Collapse(IReadOnlyList<Letter> letters)
    {
        var view = new List<Letter>(letters.Count);
        var spanStart = new List<int>(letters.Count);
        var spanEnd = new List<int>(letters.Count);

        for (var i = 0; i < letters.Count; i++)
        {
            view.Add(letters[i]);
            spanStart.Add(i);

            var last = i;
            while (last + 1 < letters.Count && IsOverprintOf(letters[last], letters[last + 1]))
                last++;

            spanEnd.Add(last);
            i = last;
        }

        return (view, spanStart, spanEnd);
    }

    /// <summary>
    /// The letters with every overprinted stack read once (its first stamp): the view
    /// text and words are built from. The same list when there is no stack.
    /// </summary>
    internal static IReadOnlyList<Letter> Representatives(IReadOnlyList<Letter> letters)
    {
        var first = -1;
        for (var i = 1; i < letters.Count; i++)
        {
            if (IsOverprintOf(letters[i - 1], letters[i])) { first = i; break; }
        }
        if (first < 0) return letters;

        var view = new List<Letter>(letters.Count);
        for (var i = 0; i < first; i++) view.Add(letters[i]);
        for (var i = first; i < letters.Count; i++)
        {
            if (!IsOverprintOf(letters[i - 1], letters[i])) view.Add(letters[i]);
        }
        return view;
    }

    /// <summary>
    /// Whether <paramref name="b"/> is the same glyph as <paramref name="a"/> stamped
    /// essentially on top of it.
    /// </summary>
    internal static bool IsOverprintOf(Letter a, Letter b)
    {
        if (!string.Equals(a.Value, b.Value, StringComparison.Ordinal)) return false;
        if (a.Value.Length == 0 || char.IsWhiteSpace(a.Value[0])) return false;

        // Tolerance from glyph size, so it scales with the type. A quarter of a
        // glyph is far below the ~1 advance width separating real neighbours
        // and far above the sub-point offsets faux-bold uses (observed: 0.2pt
        // horizontal and 0.4pt vertical on a 10pt glyph).
        var w = Math.Max(Math.Abs(a.Width), 0.01);
        var h = Math.Max(Math.Abs(a.GlyphRectangle.Height), 0.01);
        var tolX = w * 0.25;
        var tolY = h * 0.25;

        return Math.Abs(a.StartX - b.StartX) <= tolX
            && Math.Abs(a.StartY - b.StartY) <= tolY;
    }
}
