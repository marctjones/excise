using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Excise.Core.Text;

/// <summary>
/// The single normalization pipeline used by every MATCHING path — redaction
/// text search (<c>PdfDocument.RedactText</c>, <c>TextRedactor</c>,
/// <c>PdfRedaction</c>, the operator-text backstop) and the GUI search
/// service. It composes, in order:
/// <list type="number">
/// <item>Presentation-form folding (<see cref="PresentationFormFolding"/>) —
/// Arabic presentation blocks (#632) and Latin ligatures (#722) to their
/// plain-letter compatibility decompositions.</item>
/// <item>Canonical composition (NFC, #724) — so precomposed needles
/// ("café", U+00E9) match canonically equivalent decomposed storage
/// ("cafe" + combining acute U+0301) and vice versa. Canonical ONLY — this
/// is deliberately not NFKC, which would rewrite unrelated compatibility
/// characters and loosen matching.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// Folding is for MATCHING only: callers fold both the needle and the
/// haystack view they search, never the stored text — extraction
/// (<c>page.Text</c>, letters, operator text) keeps raw code points so
/// glyph-level removal still pairs char↔glyph on raw values. The fold can
/// change length in both directions (ﬃ 1→3 expands; e + U+0301 → é 2→1
/// shrinks), so index arithmetic must stay consistently in raw or folded
/// space, never across the two.
/// </para>
/// <para>
/// Canonical composition is NOT per-character: a base letter and its
/// combining mark may arrive as two separate letters/glyphs. Per-letter
/// callers must use <see cref="FoldAll"/>, which folds letter values
/// cluster-wise (a letter whose folded value begins with a combining mark is
/// merged into the preceding letter's folded value, which then re-composes),
/// so that concatenating the per-letter folds equals folding the
/// concatenation.
/// </para>
/// </remarks>
public static class MatchingNormalization
{
    /// <summary>
    /// Fold <paramref name="text"/> into the canonical matching space:
    /// presentation forms/ligatures decomposed to plain letters, then the
    /// whole string composed to Unicode NFC. Returns the original string
    /// instance when nothing changes.
    /// </summary>
    public static string Fold(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var folded = PresentationFormFolding.Fold(text);
        return CanonicalCompose(folded);
    }

    /// <summary>
    /// Fold a sequence of per-letter text values so that
    /// <c>string.Concat(result)</c> equals <see cref="Fold"/> of the
    /// concatenated values (modulo unmatched cluster boundaries). A letter
    /// whose folded value begins with a combining mark contributes the empty
    /// string, and its mark is composed into the preceding letter's folded
    /// value — callers doing folded-space index arithmetic must treat
    /// zero-length values as part of the preceding letter's cluster.
    /// </summary>
    public static string[] FoldAll(IReadOnlyList<string> values)
    {
        if (values == null) throw new ArgumentNullException(nameof(values));

        var folded = new string[values.Count];
        int lastNonEmpty = -1;
        for (int i = 0; i < values.Count; i++)
        {
            folded[i] = Fold(values[i] ?? string.Empty);

            // A folded value that STARTS with a combining mark continues the
            // preceding letter's canonical cluster (decomposed accents stored
            // as their own glyphs, or width-folded voiced marks). Merge it
            // left and re-fold so the cluster composes; the merged letter
            // keeps a zero-length folded value.
            if (folded[i].Length > 0 && lastNonEmpty >= 0 &&
                IsCombiningMark(folded[i][0]))
            {
                folded[lastNonEmpty] = Fold(folded[lastNonEmpty] + folded[i]);
                folded[i] = string.Empty;
                continue;
            }

            if (folded[i].Length > 0) lastNonEmpty = i;
        }

        return folded;
    }

    /// <summary>
    /// True when <paramref name="c"/> is a combining mark that attaches to
    /// the preceding base character (Unicode nonspacing/spacing-combining/
    /// enclosing mark).
    /// </summary>
    public static bool IsCombiningMark(char c)
    {
        var category = CharUnicodeInfo.GetUnicodeCategory(c);
        return category is UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark;
    }

    /// <summary>
    /// Unicode canonical composition (NFC), tolerant of the malformed
    /// sequences PDF /ToUnicode maps can produce: strings that are already
    /// NFC are returned as the same instance, and strings .NET cannot
    /// normalize (unpaired surrogates, noncharacters) are returned raw
    /// rather than throwing — matching then degrades to exact comparison
    /// for that string instead of failing the whole operation.
    /// </summary>
    private static string CanonicalCompose(string text)
    {
        try
        {
            return text.IsNormalized(NormalizationForm.FormC)
                ? text
                : text.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return text;
        }
    }
}
