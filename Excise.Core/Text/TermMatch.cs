using System;
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
    /// <paramref name="value"/> with every occurrence of <paramref name="term"/> cut out,
    /// or null when it contains none.
    /// </summary>
    internal static string? Cut(string value, string term, bool caseSensitive, bool wholeWord)
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
