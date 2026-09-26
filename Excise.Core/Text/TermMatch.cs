using System;
using System.Collections.Generic;
using System.Text;

namespace Excise.Core.Text;

/// <summary>
/// The one whole-word rule (#1052, #1834). Redaction's string carriers and the GUI
/// search both find a term as a substring and, under whole-word, keep the hit only
/// when a non-word character (or the text edge) bounds it on both sides. A user who
/// sees N whole-word hits must lose exactly those N occurrences.
/// </summary>
/// <remarks>
/// A word character is a letter, digit or underscore: whole-word <c>SECRET</c> does
/// not match inside <c>SECRET_KEY</c>. The page-letter matcher applies the same
/// character test but also treats a neighbour on another line as a boundary.
/// </remarks>
internal static class TermMatch
{
    internal static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    /// Next occurrence of <paramref name="term"/> at or after <paramref name="startIndex"/>,
    /// or -1. A candidate the whole-word rule rejects is skipped by one character.
    /// </summary>
    internal static int IndexOf(
        string value, string term, bool caseSensitive, bool wholeWord, int startIndex)
    {
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var at = startIndex;
        while (at <= value.Length - term.Length)
        {
            var found = value.IndexOf(term, at, comparison);
            if (found < 0) return -1;
            var end = found + term.Length;
            if (!wholeWord
                || ((found == 0 || !IsWordChar(value[found - 1])) && (end >= value.Length || !IsWordChar(value[end]))))
                return found;

            at = found + 1;
        }
        return -1;
    }

    /// <summary>
    /// Does <paramref name="value"/> hold one of <paramref name="terms"/>? Matched raw and,
    /// failing that, in the <see cref="MatchingNormalization"/> fold the page matcher uses,
    /// where a soft hyphen or a ligature inside the term does not hide it.
    /// </summary>
    internal static bool Holds(string? value, IReadOnlyList<string> terms, bool caseSensitive, bool wholeWord)
    {
        if (string.IsNullOrEmpty(value)) return false;
        string? folded = null;
        foreach (var term in terms)
        {
            if (string.IsNullOrEmpty(term)) continue;
            if (IndexOf(value, term, caseSensitive, wholeWord, 0) >= 0) return true;
            folded ??= MatchingNormalization.Fold(value);
            var foldedTerm = MatchingNormalization.Fold(term);
            if ((!ReferenceEquals(folded, value) || !ReferenceEquals(foldedTerm, term))
                && foldedTerm.Length > 0 && IndexOf(folded, foldedTerm, caseSensitive, wholeWord, 0) >= 0)
                return true;
        }
        return false;
    }

    /// <summary>
    /// <paramref name="value"/> with every term cut out, or null when it holds none. The one
    /// cut every text carrier uses (#1860): a cut can re-form a term (<c>KESKESTRELTREL</c>
    /// less <c>KESTREL</c> is <c>KESTREL</c>, and cutting one term can join another), so the
    /// cut repeats until nothing is left to cut. Empty when a term is still there only in the
    /// fold, which no cut of the raw text reaches: the caller then drops the whole value.
    /// </summary>
    internal static string? Mask(string value, IReadOnlyList<string> terms, bool caseSensitive, bool wholeWord)
    {
        if (!Holds(value, terms, caseSensitive, wholeWord)) return null;
        var masked = value;
        for (var cut = true; cut;)
        {
            // Each productive pass shortens the value, so this ends.
            cut = false;
            foreach (var term in terms)
            {
                if (Cut(masked, term, caseSensitive, wholeWord) is not { } shorter) continue;
                masked = shorter;
                cut = true;
            }
        }
        return Holds(masked, terms, caseSensitive, wholeWord) ? string.Empty : masked;
    }

    private static string? Cut(string value, string term, bool caseSensitive, bool wholeWord)
    {
        if (string.IsNullOrEmpty(term)) return null;
        var at = IndexOf(value, term, caseSensitive, wholeWord, 0);
        if (at < 0) return null;

        var sb = new StringBuilder(value.Length);
        var from = 0;
        while (at >= 0)
        {
            sb.Append(value, from, at - from);
            from = at + term.Length;
            at = IndexOf(value, term, caseSensitive, wholeWord, from);
        }
        return sb.Append(value, from, value.Length - from).ToString();
    }
}
