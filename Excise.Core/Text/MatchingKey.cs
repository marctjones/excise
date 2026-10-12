using System;
using System.Collections.Generic;
using System.Text;

namespace Excise.Core.Text;

/// <summary>
/// The last step of the matching space: what case-insensitive matching compares.
/// Search, annotation search, term redaction on the page and every carrier read a
/// term and its text through it, by way of <see cref="TermMatch"/>, so what search
/// shows is what redaction removes.
/// </summary>
/// <remarks>
/// <para>Applied AFTER <see cref="MatchingNormalization.Fold(string)"/> (or to raw
/// text, which only finds a subset; the folded pass finds the rest). Each code
/// point maps to its own piece of the key, independent of its neighbours, so a key
/// position always maps back to one source code point.</para>
/// <para>Case: with <c>foldCase</c> each code point is replaced by its Unicode
/// full case fold (<see cref="UnicodeCaseFolding"/>). The fold can be longer than
/// the source (<c>ß</c> → <c>ss</c>): a match that starts or ends inside such an
/// expansion takes the whole source character.</para>
/// </remarks>
internal static class MatchingKey
{
    /// <summary>
    /// The key of <paramref name="text"/>; the same instance when nothing changes.
    /// </summary>
    internal static string Of(string text, bool foldCase)
    {
        if (string.IsNullOrEmpty(text) || !foldCase) return text;
        return UnicodeCaseFolding.Fold(text);
    }

    /// <summary>
    /// The key of <paramref name="text"/> with, per key character, the index in
    /// <paramref name="text"/> of the code point it came from.
    /// </summary>
    internal static (string Key, int[] Source) Map(string text, bool foldCase)
    {
        var sb = new StringBuilder(text.Length + 8);
        var source = new List<int>(text.Length + 8);
        for (var i = 0; i < text.Length; i++)
        {
            var width = UnicodeCaseFolding.CodePointWidth(text, i);
            var piece = foldCase
                ? UnicodeCaseFolding.FoldCodePoint(width == 2 ? char.ConvertToUtf32(text[i], text[i + 1]) : text[i])
                : null;
            if (piece == null) sb.Append(text, i, width);
            else sb.Append(piece);
            for (var k = source.Count; k < sb.Length; k++) source.Add(i);
            i += width - 1;
        }
        return (sb.ToString(), source.ToArray());
    }
}
