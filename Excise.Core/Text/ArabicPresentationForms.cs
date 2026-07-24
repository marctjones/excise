using System;
using System.Text;

namespace Excise.Core.Text;

/// <summary>
/// Folds Arabic presentation forms — the shaped positional glyphs of
/// U+FB50–U+FDFF (Arabic Presentation Forms-A, incl. lam-alef ligatures) and
/// U+FE70–U+FEFF (Forms-B) — to their base-letter compatibility
/// decompositions, so a search needle typed in base letters (U+0621–U+064A…)
/// can match text a PDF stores in shaped form. See issue #632.
/// </summary>
/// <remarks>
/// <para>
/// Folding is for MATCHING only. Extraction (<c>page.Text</c>, letters,
/// operator text) keeps the raw presentation-form code points: glyph-level
/// removal pairs operator characters with letters one-to-one on the raw
/// values, and extraction parity is measured against independent extractors
/// that also report the raw <c>/ToUnicode</c> mapping. Callers fold both the
/// needle and the haystack view they search — never the stored text.
/// </para>
/// <para>
/// The fold is the Unicode compatibility decomposition (NFKC) of each
/// character in the two Arabic presentation blocks, and nothing else. It is
/// deliberately NOT a whole-string NFKC: that would also rewrite unrelated
/// compatibility characters (fi/fl ligatures, fullwidth forms, superscripts)
/// and change matching behavior for non-Arabic text. Characters outside the
/// two blocks — including the Hebrew presentation block U+FB1D–U+FB4F —
/// pass through untouched.
/// </para>
/// <para>
/// Note the fold can change length: lam-alef ligatures expand to two base
/// letters (U+FEFC → U+0644 U+0627), so callers doing index arithmetic must
/// do it consistently in either raw or folded space, not across the two.
/// </para>
/// </remarks>
public static class ArabicPresentationForms
{
    private const int BlockAStart = 0xFB50;
    private const int BlockALength = 0xFDFF - 0xFB50 + 1;
    private const int BlockBStart = 0xFE70;
    private const int BlockBLength = 0xFEFF - 0xFE70 + 1;

    /// <summary>
    /// Per-character fold cache for the two presentation blocks. Index 0
    /// corresponds to U+FB50; the Forms-B block follows the Forms-A block.
    /// </summary>
    private static readonly string?[] FoldCache = new string?[BlockALength + BlockBLength];

    /// <summary>
    /// True when <paramref name="c"/> is in an Arabic presentation-form block
    /// (U+FB50–U+FDFF or U+FE70–U+FEFF).
    /// </summary>
    public static bool IsPresentationForm(char c) =>
        (c >= (char)0xFB50 && c <= (char)0xFDFF) ||
        (c >= (char)0xFE70 && c <= (char)0xFEFF);

    /// <summary>
    /// True when any character of <paramref name="text"/> is an Arabic
    /// presentation form.
    /// </summary>
    public static bool ContainsPresentationForms(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var c in text)
            if (IsPresentationForm(c)) return true;
        return false;
    }

    /// <summary>
    /// Replace every Arabic presentation-form character with its base-letter
    /// compatibility decomposition (e.g. U+FEB3 → U+0633; lam-alef U+FEFC →
    /// U+0644 U+0627). All other characters are returned unchanged; when the
    /// input contains no presentation forms the original string instance is
    /// returned.
    /// </summary>
    public static string Fold(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        int first = -1;
        for (int i = 0; i < text.Length; i++)
        {
            if (IsPresentationForm(text[i])) { first = i; break; }
        }

        if (first < 0) return text;

        var sb = new StringBuilder(text.Length + 8);
        sb.Append(text, 0, first);
        for (int i = first; i < text.Length; i++)
        {
            var c = text[i];
            if (IsPresentationForm(c)) sb.Append(FoldChar(c));
            else sb.Append(c);
        }

        return sb.ToString();
    }

    internal static string FoldChar(char c)
    {
        int index = c <= (char)0xFDFF
            ? c - BlockAStart
            : BlockALength + (c - BlockBStart);

        var cached = FoldCache[index];
        if (cached != null) return cached;

        string folded;
        try
        {
            // NFKC rather than NFKD so decompositions recompose canonically —
            // U+FE81 (alef-with-madda isolated) folds to U+0622, the character
            // a user types, not to U+0627 U+0653.
            folded = c.ToString().Normalize(NormalizationForm.FormKC);
        }
        catch (ArgumentException)
        {
            // Noncharacters (U+FDD0–U+FDEF) and unassigned code points inside
            // the blocks have no decomposition and can make Normalize throw;
            // they fold to themselves.
            folded = c.ToString();
        }

        FoldCache[index] = folded;
        return folded;
    }
}
