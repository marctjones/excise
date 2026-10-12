using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;

namespace Excise.Core.Text;

/// <summary>
/// The last step of the matching space: what case-insensitive and accent-insensitive
/// matching compare. Search, annotation search, term redaction on the page and every
/// carrier read a term and its text through it, by way of <see cref="TermMatch"/>, so
/// what search shows is what redaction removes.
/// </summary>
/// <remarks>
/// <para>Applied AFTER <see cref="MatchingNormalization.Fold(string)"/> (or to raw
/// text, which only finds a subset; the folded pass finds the rest). Each code
/// point maps to its own piece of the key, independent of its neighbours, so a key
/// position always maps back to one source code point. Case is folded first, then
/// accents are stripped.</para>
/// <para>Case: with <c>foldCase</c> each code point is replaced by its Unicode
/// full case fold (<see cref="UnicodeCaseFolding"/>). The fold can be longer than
/// the source (<c>ß</c> → <c>ss</c>): a match that starts or ends inside such an
/// expansion takes the whole source character.</para>
/// <para>Accents: with <c>ignoreAccents</c> the combining diacritical marks of
/// <see cref="IsStrippedAccent"/> are removed, both those stored as their own code
/// points and those inside a precomposed letter's canonical decomposition, so
/// <c>é</c>, <c>e</c> + U+0301 and <c>e</c> all key as <c>e</c>, <c>ǘ</c> as
/// <c>u</c>, <c>ά</c> as <c>α</c>, <c>ё</c> and <c>й</c> as <c>е</c> and
/// <c>и</c>. Only those blocks: the marks of Indic scripts, Thai, Japanese kana
/// voicing and Hangul jamo are part of the letter and are kept, so <c>क़</c>,
/// <c>ก่</c>, <c>が</c> and <c>각</c> stay distinct from <c>क</c>, <c>ก</c>,
/// <c>か</c> and <c>가</c>. A letter with no canonical decomposition keeps its
/// identity (<c>ø</c>, <c>ł</c>, <c>đ</c>, <c>æ</c>). Hebrew points and Arabic
/// harakat are not handled here: the fold always removes them (#725).</para>
/// </remarks>
internal static class MatchingKey
{
    /// <summary>
    /// True for the combining diacritical marks stripped under ignore-accents: the
    /// Combining Diacritical Marks block U+0300–U+036F and its Extended
    /// (U+1AB0–U+1AFF), Supplement (U+1DC0–U+1DFF) and Half Marks (U+FE20–U+FE2F)
    /// blocks, the marks Latin, Greek and Cyrillic letters carry.
    /// </summary>
    internal static bool IsStrippedAccent(char c) =>
        c is >= '̀' and <= 'ͯ'
            or >= '᪰' and <= '᫿'
            or >= '᷀' and <= '᷿'
            or >= '︠' and <= '︯';

    /// <summary>
    /// The key of <paramref name="text"/>; the same instance when nothing changes.
    /// </summary>
    internal static string Of(string text, bool foldCase, bool ignoreAccents = false)
    {
        if (string.IsNullOrEmpty(text) || (!foldCase && !ignoreAccents)) return text;
        StringBuilder? sb = null;
        for (var i = 0; i < text.Length; i++)
        {
            var width = UnicodeCaseFolding.CodePointWidth(text, i);
            var piece = Piece(text, i, width, foldCase, ignoreAccents);
            if (piece == null)
            {
                sb?.Append(text, i, width);
            }
            else
            {
                sb ??= new StringBuilder(text.Length + 4).Append(text, 0, i);
                sb.Append(piece);
            }
            i += width - 1;
        }
        return sb?.ToString() ?? text;
    }

    /// <summary>
    /// The key of <paramref name="text"/> with, per key character, the index in
    /// <paramref name="text"/> of the code point it came from. A stripped accent
    /// has no key character.
    /// </summary>
    internal static (string Key, int[] Source) Map(string text, bool foldCase, bool ignoreAccents = false)
    {
        var sb = new StringBuilder(text.Length + 8);
        var source = new List<int>(text.Length + 8);
        for (var i = 0; i < text.Length; i++)
        {
            var width = UnicodeCaseFolding.CodePointWidth(text, i);
            var piece = Piece(text, i, width, foldCase, ignoreAccents);
            if (piece == null) sb.Append(text, i, width);
            else sb.Append(piece);
            for (var k = source.Count; k < sb.Length; k++) source.Add(i);
            i += width - 1;
        }
        return (sb.ToString(), source.ToArray());
    }

    /// <summary>The key of the code point at <paramref name="i"/>, or null when it keys as itself.</summary>
    private static string? Piece(string text, int i, int width, bool foldCase, bool ignoreAccents)
    {
        var c = text[i];
        if (width == 1 && c < 0x80)
            return foldCase && c is >= 'A' and <= 'Z' ? ((char)(c + 32)).ToString() : null;

        var piece = foldCase
            ? UnicodeCaseFolding.FoldCodePoint(width == 2 ? char.ConvertToUtf32(c, text[i + 1]) : c)
            : null;
        if (!ignoreAccents) return piece;
        if (piece == null)
            return width == 1 ? Unaccented(c) : null;

        StringBuilder? sb = null;
        for (var k = 0; k < piece.Length; k++)
        {
            var unaccented = char.IsSurrogate(piece[k]) ? null : Unaccented(piece[k]);
            if (unaccented == null) sb?.Append(piece[k]);
            else (sb ??= new StringBuilder(piece.Length).Append(piece, 0, k)).Append(unaccented);
        }
        return sb?.ToString() ?? piece;
    }

    // Per BMP character, its accent-free key, or null when it keys as itself.
    // Bounded by the BMP; filled as characters are met.
    private static readonly ConcurrentDictionary<char, string?> UnaccentedCache = new();

    /// <summary>
    /// <paramref name="c"/> without the <see cref="IsStrippedAccent"/> marks of its
    /// canonical decomposition (recomposed), "" for such a mark itself, or null when
    /// it keeps its identity.
    /// </summary>
    private static string? Unaccented(char c)
    {
        if (IsStrippedAccent(c)) return string.Empty;
        if (c < 'À' || char.IsSurrogate(c)) return null;
        return UnaccentedCache.GetOrAdd(c, static ch =>
        {
            string decomposed;
            try { decomposed = ch.ToString().Normalize(NormalizationForm.FormD); }
            catch (ArgumentException) { return null; }

            var kept = new StringBuilder(decomposed.Length);
            var stripped = false;
            foreach (var d in decomposed)
            {
                if (IsStrippedAccent(d)) stripped = true;
                else kept.Append(d);
            }
            if (!stripped) return null;
            try { return kept.ToString().Normalize(NormalizationForm.FormC); }
            catch (ArgumentException) { return kept.ToString(); }
        });
    }
}
