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
/// <item>Arabic harakat / Hebrew niqqud stripping (#725) — the optional
/// vocalization marks of the two scripts (Arabic U+064B–U+065F, U+0670,
/// Koranic U+06D6–U+06ED; Hebrew combining points/cantillation in
/// U+0591–U+05C7) are removed, so a bare needle ("كتب", "שלום") matches
/// vocalized storage ("كَتَبَ", "שָׁלוֹם"). Scoped to those scripts' combining
/// marks ONLY: Latin combining accents are canonically meaningful and kept,
/// and the Hebrew PUNCTUATION sharing the block (maqaf U+05BE, paseq
/// U+05C0, sof pasuq U+05C3, nun hafukha U+05C6) is untouched.</item>
/// <item>Invisible/optional separator folding (#726) — soft hyphen U+00AD
/// and the zero-width characters U+200B–U+200D and U+FEFF are removed, and
/// non-breaking space U+00A0 becomes a plain space, so "secret" matches
/// "se&#173;cret" in justified text and "top secret" matches
/// "top&#160;secret". Exactly those five-plus-one code points — real
/// hyphens, spaces, and other format characters keep their identity.</item>
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
    /// presentation forms/ligatures decomposed to plain letters, the whole
    /// string composed to Unicode NFC, then optional Arabic/Hebrew
    /// vocalization marks stripped. Returns the original string instance
    /// when nothing changes.
    /// </summary>
    public static string Fold(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var folded = PresentationFormFolding.Fold(text);
        folded = CanonicalCompose(folded);
        folded = StripSemiticVocalization(folded);
        return FoldSeparators(folded);
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
    /// True when <paramref name="c"/> is an optional Arabic or Hebrew
    /// vocalization mark stripped for matching (#725): Arabic harakat and
    /// annotation signs — U+064B–U+065F (incl. shadda/sukun), U+0670
    /// (superscript alef), U+06D6–U+06DC, U+06DF–U+06E4, U+06E7–U+06E8,
    /// U+06EA–U+06ED (Koranic marks) — and the Hebrew combining marks of
    /// U+0591–U+05C7: U+0591–U+05BD (cantillation + points), U+05BF (rafe),
    /// U+05C1–U+05C2 (shin/sin dots), U+05C4–U+05C5, U+05C7. The
    /// non-combining PUNCTUATION interleaved in that Hebrew range — maqaf
    /// U+05BE, paseq U+05C0, sof pasuq U+05C3, nun hafukha U+05C6 — is
    /// deliberately NOT included. Runs AFTER canonical composition, so
    /// marks that canonically compose into a distinct letter (alef + madda
    /// U+0653 → U+0622, alef + hamza U+0654 → U+0623) are preserved inside
    /// the composed letter and only truly optional marks are stripped.
    /// </summary>
    public static bool IsSemiticVocalizationMark(char c)
    {
        if (c < '\u0591' || c > '\u06ED') return false;

        return c switch
        {
            // Hebrew: combining marks only, skipping the block's punctuation.
            >= '\u0591' and <= '\u05BD' => true,
            '\u05BF' => true,
            '\u05C1' or '\u05C2' => true,
            '\u05C4' or '\u05C5' => true,
            '\u05C7' => true,

            // Arabic: harakat, tanween, shadda, sukun; superscript alef.
            >= '\u064B' and <= '\u065F' => true,
            '\u0670' => true,

            // Arabic: Koranic annotation signs (the combining subranges).
            >= '\u06D6' and <= '\u06DC' => true,
            >= '\u06DF' and <= '\u06E4' => true,
            '\u06E7' or '\u06E8' => true,
            >= '\u06EA' and <= '\u06ED' => true,

            _ => false,
        };
    }

    /// <summary>
    /// Remove every character matched by
    /// <see cref="IsSemiticVocalizationMark"/>; returns the original string
    /// instance when none is present.
    /// </summary>
    private static string StripSemiticVocalization(string text)
    {
        int first = -1;
        for (int i = 0; i < text.Length; i++)
        {
            if (IsSemiticVocalizationMark(text[i])) { first = i; break; }
        }

        if (first < 0) return text;

        var sb = new StringBuilder(text.Length);
        sb.Append(text, 0, first);
        for (int i = first; i < text.Length; i++)
        {
            if (!IsSemiticVocalizationMark(text[i])) sb.Append(text[i]);
        }

        return sb.ToString();
    }

    /// <summary>
    /// True when <paramref name="c"/> is an invisible/optional separator
    /// removed for matching (#726): soft hyphen U+00AD (a line-break
    /// OPPORTUNITY the layout engine may or may not render), zero-width
    /// space/non-joiner/joiner U+200B–U+200D, and zero-width no-break
    /// space / BOM U+FEFF. Exactly these code points — a real hyphen
    /// (U+002D) is visible content and keeps its identity, as do all other
    /// format characters (bidi marks are handled by
    /// <see cref="BidiReorderer"/>, not stripped here).
    /// </summary>
    public static bool IsIgnorableSeparator(char c) =>
        c is '\u00AD' or '\u200B' or '\u200C' or '\u200D' or '\uFEFF';

    /// <summary>
    /// Remove every <see cref="IsIgnorableSeparator"/> character and map
    /// non-breaking space U+00A0 to a plain space; returns the original
    /// string instance when neither occurs.
    /// </summary>
    private static string FoldSeparators(string text)
    {
        int first = -1;
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (IsIgnorableSeparator(c) || c == '\u00A0') { first = i; break; }
        }

        if (first < 0) return text;

        var sb = new StringBuilder(text.Length);
        sb.Append(text, 0, first);
        for (int i = first; i < text.Length; i++)
        {
            var c = text[i];
            if (IsIgnorableSeparator(c)) continue;
            sb.Append(c == '\u00A0' ? ' ' : c);
        }

        return sb.ToString();
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
