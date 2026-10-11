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
        string value, string term, bool caseSensitive, bool wholeWord, int startIndex,
        SeparatorMatching separators = default) =>
        IndexOf(value, term, caseSensitive, wholeWord, startIndex, separators, out _);

    /// <summary>
    /// <see cref="IndexOf(string, string, bool, bool, int, SeparatorMatching)"/> with the
    /// length of the text matched, which differs from the term's when
    /// <paramref name="separators"/> lets a separator run match a longer or an empty one.
    /// </summary>
    internal static int IndexOf(
        string value, string term, bool caseSensitive, bool wholeWord, int startIndex,
        SeparatorMatching separators, out int length)
    {
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        length = term.Length;
        if (IsFlexible(term, separators))
            return FlexibleIndexOf(value, term, comparison, wholeWord, startIndex, separators, out length);

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
    internal static int MatchEnd(
        string text, int start, string needle, StringComparison comparison, SeparatorMatching separators = default)
    {
        if (IsFlexible(needle, separators))
            return FlexibleMatchEnd(text, start, needle, comparison, separators);

        // A window of 4× the needle is the safe upper bound on the raw text that
        // can fold down to it; a raw window SHORTER than the needle can still
        // match, since a lam-alef ligature is one raw char but two needle chars.
        var window = Math.Min(needle.Length * 4, text.Length - start);
        if (!MatchingNormalization.Fold(text.Substring(start, window)).StartsWith(needle, comparison))
            return -1;

        return GrowToFoldedLength(text, start, needle.Length);
    }

    /// <summary>
    /// The last raw index of the shortest span of <paramref name="text"/> from
    /// <paramref name="start"/> whose fold is <paramref name="foldedLength"/> long,
    /// with any combining marks that continue its last cluster; -1 past the end.
    /// </summary>
    private static int GrowToFoldedLength(string text, int start, int foldedLength)
    {
        var end = start;
        while (end < text.Length && MatchingNormalization.Fold(text.Substring(start, end - start + 1)).Length < foldedLength)
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
    internal static bool Holds(
        string? value, IReadOnlyList<string> terms, bool caseSensitive, bool wholeWord,
        SeparatorMatching separators = default)
    {
        if (string.IsNullOrEmpty(value)) return false;
        string? folded = null;
        foreach (var term in terms)
        {
            if (string.IsNullOrEmpty(term)) continue;
            if (IndexOf(value, term, caseSensitive, wholeWord, 0, separators) >= 0) return true;
            folded ??= MatchingNormalization.Fold(value);
            if (FoldedSpans(value, folded, term, caseSensitive, wholeWord, separators) is { } next && (!wholeWord || next(0).Start >= 0))
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
    internal static string? Mask(
        string value, IReadOnlyList<string> terms, bool caseSensitive, bool wholeWord,
        SeparatorMatching separators = default)
    {
        if (!Holds(value, terms, caseSensitive, wholeWord, separators)) return null;
        var masked = value;
        for (var cut = true; cut;)
        {
            // Each productive pass shortens the value, so this ends.
            cut = false;
            foreach (var term in terms)
            {
                if (Cut(masked, term, caseSensitive, wholeWord, separators) is not { } shorter) continue;
                masked = shorter;
                cut = true;
            }
        }
        return Holds(masked, terms, caseSensitive, wholeWord, separators) ? string.Empty : masked;
    }

    /// <summary>
    /// <paramref name="value"/> less every occurrence of <paramref name="term"/>: the raw
    /// ones, or when there are none, the spans whose fold is the term's; null when neither.
    /// </summary>
    private static string? Cut(
        string value, string term, bool caseSensitive, bool wholeWord, SeparatorMatching separators)
    {
        if (string.IsNullOrEmpty(term)) return null;
        Func<int, (int Start, int End)>? next = from =>
            IndexOf(value, term, caseSensitive, wholeWord, from, separators, out var length) is var at and >= 0
                ? (at, at + length - 1) : (-1, -1);
        var span = next(0);
        if (span.Start < 0 && (next = FoldedSpans(value, MatchingNormalization.Fold(value), term, caseSensitive, wholeWord, separators)) != null)
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
        string value, string folded, string term, bool caseSensitive, bool wholeWord, SeparatorMatching separators)
    {
        var needle = MatchingNormalization.Fold(term).Trim();
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (needle.Length == 0 || (ReferenceEquals(folded, value) && ReferenceEquals(needle, term))
            || IndexOf(folded, needle, caseSensitive, wholeWord: false, 0, separators) < 0)
            return null;

        return from =>
        {
            for (var at = from; at < value.Length; at++)
            {
                var end = MatchEnd(value, at, needle, comparison, separators);
                if (end >= 0 && (!wholeWord
                    || ((at == 0 || !IsWordChar(value[at - 1])) && (end + 1 >= value.Length || !IsWordChar(value[end + 1])))))
                    return (at, end);
            }
            return (-1, -1);
        };
    }

    /// <summary>
    /// A separator for <see cref="SeparatorMatching"/>: whitespace, a hyphen, the
    /// hyphen and non-breaking hyphen U+2010/U+2011, and the dashes the fold reads as
    /// a hyphen (en, em, minus), so a raw value and its fold agree. A soft hyphen is
    /// not one: it is invisible, the fold removes it, and <c>open&#173;source</c>
    /// reads as the closed compound.
    /// </summary>
    internal static bool IsSeparator(char c) =>
        c is '-' or '‐' or '‑' or '–' or '—' or '−' || char.IsWhiteSpace(c);

    /// <summary>
    /// True when <paramref name="separators"/> changes how <paramref name="term"/>
    /// matches: it is not exact and the term has a separator between two other
    /// characters. Otherwise every path keeps its literal comparison.
    /// </summary>
    internal static bool IsFlexible(string term, SeparatorMatching separators)
    {
        if (separators.IsExact) return false;
        var first = 0;
        while (first < term.Length && IsSeparator(term[first])) first++;
        var last = term.Length - 1;
        while (last > first && IsSeparator(term[last])) last--;
        for (var i = first + 1; i < last; i++)
            if (IsSeparator(term[i])) return true;
        return false;
    }

    /// <summary>
    /// The length of the text <paramref name="term"/> matches at <paramref name="at"/>
    /// of <paramref name="value"/>, or -1. Literal except at a separator run between two
    /// other characters of the term, which matches a run of separators (any mix, at
    /// least one) under <see cref="SeparatorMatching.HyphenAsSpace"/> or the same run
    /// otherwise, and also no separator at all under
    /// <see cref="SeparatorMatching.AllowNoSeparator"/>. A separator run in the text is
    /// taken whole, so a whole-word test after the match sees its real neighbour.
    /// </summary>
    internal static int FlexibleMatchLength(
        string value, int at, string term, StringComparison comparison, SeparatorMatching separators)
    {
        var lastLiteral = term.Length - 1;
        while (lastLiteral >= 0 && IsSeparator(term[lastLiteral])) lastLiteral--;

        var v = at;
        var n = 0;
        while (n < term.Length)
        {
            var interior = n > 0 && n < lastLiteral && IsSeparator(term[n]) && !IsSeparator(term[n - 1]);
            if (!interior)
            {
                // The literal stretch up to the next interior separator run (or the end).
                var stop = n + 1;
                while (stop < term.Length && !(stop < lastLiteral && IsSeparator(term[stop]) && !IsSeparator(term[stop - 1])))
                    stop++;
                var length = stop - n;
                if (v + length > value.Length || !value.AsSpan(v, length).Equals(term.AsSpan(n, length), comparison))
                    return -1;
                v += length;
                n = stop;
                continue;
            }

            var runEnd = n;
            while (runEnd < term.Length && IsSeparator(term[runEnd])) runEnd++;
            var textRunEnd = v;
            while (textRunEnd < value.Length && IsSeparator(value[textRunEnd])) textRunEnd++;

            if (separators.HyphenAsSpace && textRunEnd > v)
                v = textRunEnd;
            else if (!separators.HyphenAsSpace && textRunEnd - v == runEnd - n
                     && value.AsSpan(v, runEnd - n).Equals(term.AsSpan(n, runEnd - n), StringComparison.Ordinal))
                v = textRunEnd;
            else if (!(separators.AllowNoSeparator && textRunEnd == v))
                return -1;
            n = runEnd;
        }
        return v - at;
    }

    private static int FlexibleIndexOf(
        string value, string term, StringComparison comparison, bool wholeWord, int startIndex,
        SeparatorMatching separators, out int length)
    {
        // Every match starts with the term's first character; a cheap prefilter.
        var head = term.AsSpan(0, 1);
        for (var at = Math.Max(startIndex, 0); at < value.Length; at++)
        {
            var found = value.AsSpan(at).IndexOf(head, comparison);
            if (found < 0) break;
            at += found;
            var matched = FlexibleMatchLength(value, at, term, comparison, separators);
            if (matched < 0) continue;
            var end = at + matched;
            if (!wholeWord
                || ((at == 0 || !IsWordChar(value[at - 1])) && (end >= value.Length || !IsWordChar(value[end]))))
            {
                length = matched;
                return at;
            }
        }
        length = term.Length;
        return -1;
    }

    /// <summary>
    /// <see cref="MatchEnd"/> for a needle whose separators match flexibly: the fold
    /// of the raw window from <paramref name="start"/> must open with a match, and the
    /// raw span grows until its fold is as long as the folded text matched.
    /// </summary>
    private static int FlexibleMatchEnd(
        string text, int start, string needle, StringComparison comparison, SeparatorMatching separators)
    {
        // Room for a separator run in the text longer than the needle's one.
        var window = Math.Min(needle.Length * 4 + 64, text.Length - start);
        var matched = FlexibleMatchLength(
            MatchingNormalization.Fold(text.Substring(start, window)), 0, needle, comparison, separators);
        return matched <= 0 ? -1 : GrowToFoldedLength(text, start, matched);
    }
}
