using System;
using System.Text;

namespace Excise.Core.Text;

/// <summary>
/// Folds the Latin ligatures of the Alphabetic Presentation Forms block —
/// U+FB00–U+FB06: ﬀ(ff) ﬁ(fi) ﬂ(fl) ﬃ(ffi) ﬄ(ffl) ﬅ(long s-t) ﬆ(st) — to
/// their plain-letter compatibility decompositions, so a search needle typed
/// in plain letters ("office") can match text a PDF stores with single
/// ligature code points ("oﬃce"). The Latin sibling of
/// <see cref="ArabicPresentationForms"/>. See issue #722.
/// </summary>
/// <remarks>
/// <para>
/// Folding is for MATCHING only. Extraction (<c>page.Text</c>, letters,
/// operator text) keeps the raw ligature code points: glyph-level removal
/// pairs operator characters with letters one-to-one on the raw values, and
/// extraction parity is measured against independent extractors that also
/// report the raw <c>/ToUnicode</c> mapping. Callers fold both the needle
/// and the haystack view they search — never the stored text.
/// </para>
/// <para>
/// The fold is the Unicode compatibility decomposition (NFKC) of each
/// character in U+FB00–U+FB06, and nothing else. It is deliberately NOT a
/// whole-string NFKC: that would also rewrite unrelated compatibility
/// characters (fullwidth forms, superscripts, Arabic/Hebrew presentation
/// forms) and change matching behavior for other text. The Armenian
/// ligatures (U+FB13–U+FB17) and Hebrew presentation forms (U+FB1D–U+FB4F)
/// that share the Alphabetic Presentation Forms block pass through
/// untouched.
/// </para>
/// <para>
/// The fold always expands length: one ligature becomes two or three plain
/// letters (U+FB03 ﬃ → "ffi"), so callers doing index arithmetic must do it
/// consistently in either raw or folded space, not across the two. Note NFKC
/// also folds ſ (long s) → s, so both U+FB05 (ﬅ) and U+FB06 (ﬆ) fold to
/// "st" — the letters a user types.
/// </para>
/// </remarks>
public static class LatinLigatures
{
    private const int BlockStart = 0xFB00;
    private const int BlockEnd = 0xFB06;

    /// <summary>
    /// Per-character fold cache for the ligature range. Index 0 corresponds
    /// to U+FB00.
    /// </summary>
    private static readonly string?[] FoldCache = new string?[BlockEnd - BlockStart + 1];

    /// <summary>
    /// True when <paramref name="c"/> is a Latin ligature code point
    /// (U+FB00–U+FB06).
    /// </summary>
    public static bool IsLigature(char c) =>
        c >= (char)BlockStart && c <= (char)BlockEnd;

    /// <summary>
    /// True when any character of <paramref name="text"/> is a Latin
    /// ligature code point.
    /// </summary>
    public static bool ContainsLigatures(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var c in text)
            if (IsLigature(c)) return true;
        return false;
    }

    /// <summary>
    /// Replace every Latin ligature code point with its plain-letter
    /// compatibility decomposition (e.g. U+FB03 → "ffi"). All other
    /// characters are returned unchanged; when the input contains no
    /// ligatures the original string instance is returned.
    /// </summary>
    public static string Fold(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        int first = -1;
        for (int i = 0; i < text.Length; i++)
        {
            if (IsLigature(text[i])) { first = i; break; }
        }

        if (first < 0) return text;

        var sb = new StringBuilder(text.Length + 8);
        sb.Append(text, 0, first);
        for (int i = first; i < text.Length; i++)
        {
            var c = text[i];
            if (IsLigature(c)) sb.Append(FoldChar(c));
            else sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>
    /// The NFKC decomposition of a single ligature in U+FB00–U+FB06. All
    /// seven code points are assigned and decompose to ASCII letters, so
    /// unlike the Arabic blocks no unassigned-code-point guard is needed —
    /// the ground truth stays the deterministic Unicode decomposition.
    /// </summary>
    internal static string FoldChar(char c)
    {
        int index = c - BlockStart;
        return FoldCache[index] ??=
            c.ToString().Normalize(NormalizationForm.FormKC);
    }
}
