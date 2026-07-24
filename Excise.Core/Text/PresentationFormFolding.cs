using System.Text;

namespace Excise.Core.Text;

/// <summary>
/// Combined presentation-form fold used by every matching path: the Arabic
/// presentation blocks (U+FB50–U+FDFF, U+FE70–U+FEFF — #632, via
/// <see cref="ArabicPresentationForms"/>) and the Latin ligatures
/// (U+FB00–U+FB06 — #722, via <see cref="LatinLigatures"/>), folded to their
/// compatibility decompositions in a single pass. Everything else — Hebrew
/// and Armenian presentation forms, fullwidth forms, all other compatibility
/// characters — keeps its identity; this is deliberately NOT whole-string
/// NFKC.
/// </summary>
/// <remarks>
/// Folding is for MATCHING only: callers fold both the needle and the
/// haystack view they search, never the stored text — extraction, letters
/// and operator text keep raw code points so glyph-level removal still pairs
/// char↔glyph on raw values. The fold can change length (lam-alef 1→2,
/// ﬃ 1→3), so index arithmetic must stay consistently in raw or folded
/// space, not across the two.
/// </remarks>
public static class PresentationFormFolding
{
    /// <summary>
    /// True when <paramref name="c"/> is folded by this helper — an Arabic
    /// presentation form or a Latin ligature.
    /// </summary>
    public static bool IsFoldable(char c) =>
        LatinLigatures.IsLigature(c) || ArabicPresentationForms.IsPresentationForm(c);

    /// <summary>
    /// True when any character of <paramref name="text"/> is foldable.
    /// </summary>
    public static bool ContainsFoldable(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var c in text)
            if (IsFoldable(c)) return true;
        return false;
    }

    /// <summary>
    /// Replace every Arabic presentation form and Latin ligature with its
    /// compatibility decomposition. All other characters are returned
    /// unchanged; when nothing is foldable the original string instance is
    /// returned.
    /// </summary>
    public static string Fold(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        int first = -1;
        for (int i = 0; i < text.Length; i++)
        {
            if (IsFoldable(text[i])) { first = i; break; }
        }

        if (first < 0) return text;

        var sb = new StringBuilder(text.Length + 8);
        sb.Append(text, 0, first);
        for (int i = first; i < text.Length; i++)
        {
            var c = text[i];
            if (LatinLigatures.IsLigature(c)) sb.Append(LatinLigatures.FoldChar(c));
            else if (ArabicPresentationForms.IsPresentationForm(c)) sb.Append(ArabicPresentationForms.FoldChar(c));
            else sb.Append(c);
        }

        return sb.ToString();
    }
}
