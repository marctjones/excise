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
    /// The last index of the shortest span of <paramref name="text"/> from
    /// <paramref name="start"/> whose <see cref="MatchingNormalization.Fold"/>
    /// reaches <paramref name="needle"/> (folded and trimmed), with any combining
    /// marks that continue its last cluster; -1 when no span from there folds to
    /// it. The fold changes length both ways (ﬃ 1→3; e + U+0301 → é and a
    /// whitespace run → one space shrink), so the span grows one raw character
    /// at a time until its fold is as long as the needle. The page matcher and
    /// the carrier cut both map a folded match back to raw text this way (#1871).
    /// </summary>
    internal static int MatchEnd(string text, int start, string needle, StringComparison comparison)
    {
        // A window of 4× the needle is the safe upper bound on the raw text that
        // can fold down to it; a raw window SHORTER than the needle can still
        // match, since a lam-alef ligature is one raw char but two needle chars.
        var window = Math.Min(needle.Length * 4, text.Length - start);
        if (!MatchingNormalization.Fold(text.Substring(start, window)).StartsWith(needle, comparison))
            return -1;

        var end = start;
        while (end < text.Length && MatchingNormalization.Fold(text.Substring(start, end - start + 1)).Length < needle.Length)
            end++;
        if (end >= text.Length) return -1;

        // The last letter's canonical cluster may continue past the minimal span
        // (needle "café" against raw "cafe" + U+0301): the accent goes with it.
        while (end + 1 < text.Length && MatchingNormalization.IsCombiningMark(text[end + 1]))
            end++;
        return end;
    }

    /// <summary>
    /// Does <paramref name="value"/> hold one of <paramref name="terms"/>? Matched raw and,
    /// failing that, as the page matcher matches: in the <see cref="MatchingNormalization"/>
    /// fold, where a soft hyphen, a ligature, a curly quote, a typographic dash or a doubled
    /// space does not hide it (#1871), with whole-word judged on the raw neighbours.
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
            if (FoldedSpans(value, folded, term, caseSensitive, wholeWord) is { } next && (!wholeWord || next(0).Start >= 0))
                return true;
        }
        return false;
    }

    /// <summary>
    /// <paramref name="value"/> with every term cut out, or null when it holds none. The one
    /// cut every text carrier uses (#1860): a cut can re-form a term (<c>KESKESTRELTREL</c>
    /// less <c>KESTREL</c> is <c>KESTREL</c>, and cutting one term can join another), so the
    /// cut repeats until nothing is left to cut. A term only the fold sees is cut where
    /// <see cref="MatchEnd"/> maps it back to the raw text. Empty when a term is still there
    /// in the fold after that, which no cut of the raw text reaches: the caller then drops
    /// the whole value.
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

    /// <summary>
    /// <paramref name="value"/> less every occurrence of <paramref name="term"/>: the raw
    /// ones, or when there are none, the spans whose fold is the term's; null when neither.
    /// </summary>
    private static string? Cut(string value, string term, bool caseSensitive, bool wholeWord)
    {
        if (string.IsNullOrEmpty(term)) return null;
        Func<int, (int Start, int End)>? next = from =>
            IndexOf(value, term, caseSensitive, wholeWord, from) is var at and >= 0 ? (at, at + term.Length - 1) : (-1, -1);
        var span = next(0);
        if (span.Start < 0 && (next = FoldedSpans(value, MatchingNormalization.Fold(value), term, caseSensitive, wholeWord)) != null)
            span = next(0);
        if (span.Start < 0) return null;

        var sb = new StringBuilder(value.Length);
        var kept = 0;
        for (; span.Start >= 0; span = next!(span.End + 1))
        {
            sb.Append(value, kept, span.Start - kept);
            kept = span.End + 1;
        }
        return sb.Append(value, kept, value.Length - kept).ToString();
    }

    /// <summary>
    /// The finder of the raw spans of <paramref name="value"/> whose fold is
    /// <paramref name="term"/>'s, from a raw index on; null when <paramref name="folded"/>
    /// does not hold the folded term at all, or when neither folds to anything new (the raw
    /// match already said no). Under whole-word a span must be bounded in the raw text, the
    /// rule <c>FindTextMatches</c> applies on the page.
    /// </summary>
    private static Func<int, (int Start, int End)>? FoldedSpans(
        string value, string folded, string term, bool caseSensitive, bool wholeWord)
    {
        var needle = MatchingNormalization.Fold(term).Trim();
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (needle.Length == 0 || (ReferenceEquals(folded, value) && ReferenceEquals(needle, term))
            || folded.IndexOf(needle, comparison) < 0)
            return null;

        return from =>
        {
            for (var at = from; at < value.Length; at++)
            {
                var end = MatchEnd(value, at, needle, comparison);
                if (end >= 0 && (!wholeWord
                    || ((at == 0 || !IsWordChar(value[at - 1])) && (end + 1 >= value.Length || !IsWordChar(value[end + 1])))))
                    return (at, end);
            }
            return (-1, -1);
        };
    }
}
